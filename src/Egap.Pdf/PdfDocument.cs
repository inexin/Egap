using System.Globalization;
using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf;

/// <summary>
/// A PDF document: its trailer, catalog and indirect objects. Opening reads only the cross-reference; objects load the
/// first time they are used, and the file stays open until the document is disposed.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("in.pdf");
/// foreach (PdfWarning warning in pdf.Warnings) Console.WriteLine(warning);
/// PdfDictionary? info = pdf.Trailer.Get&lt;PdfDictionary&gt;("Info");
/// </code>
/// </example>
public sealed class PdfDocument : IDisposable
{
    private readonly PdfSource source;
    private readonly WarningLog log;
    private readonly TrailerOwner trailerOwner = new();
    private global::Egap.Pdf.Security.StandardSecurityHandler? security;
    private PdfPageCollection? pages;

    /// <summary>The length of the file an incremental update goes after, and where its newest cross-reference section is.</summary>
    private long baseLength;
    private (long Offset, bool IsStream) previousSection;

    private PdfDocument(PdfSource source, PdfReadOptions options)
    {
        this.source = source;
        Options = options;
        log = new WarningLog(options.Strict);
        HeaderVersion = ReadHeaderVersion(out long header);
        ForwardingResolver references = new();
        XrefTable xref;
        bool rebuilt = false;
        try
        {
            xref = new XrefReader(source, options, log, references).Read();
        }
        catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
        {
            if (header > 0 && TryShifted(source, header, references) is XrefTable shiftedXref)
            {
                xref = shiftedXref;
                this.source = new OffsetSource(source, header);
                log.Add(PdfWarningCode.ShiftedOffsets, $"The header is at byte {header}, and the file's offsets count from there.", header);
            }
            else
            {
                xref = XrefRebuilder.Rebuild(source, options, log, references);
                rebuilt = true;
            }
        }

        if (!rebuilt && log.ToArray().Any(w => w.Code is PdfWarningCode.MalformedXref or PdfWarningCode.BrokenXrefChain))
        {
            XrefRebuilder.FillGaps(this.source, xref, options, log);
        }

        Objects = OpenObjects(xref, references);
        if (xref.Trailer.Get("Root") is not PdfDictionary && !rebuilt)
        {
            // The cross-reference read, but names no usable catalog: rebuild it from what the file holds.
            xref = XrefRebuilder.Rebuild(this.source, options, log, references);
            rebuilt = true;
            Objects = OpenObjects(xref, references);
        }

        Xref = xref;
        Rebuilt = rebuilt;
        baseLength = this.source.Length;
        previousSection = xref.Sections.Count > 0 ? (xref.Sections[0].Offset, xref.Sections[0].IsStream) : (0, false);
        Trailer = xref.Trailer;
        Containment.Bind(Trailer, trailerOwner);
        if (Trailer.Get("Root") is not PdfDictionary)
        {
            throw new PdfException("The file has no document catalog, even after scanning it for objects.");
        }
    }

    /// <summary>
    /// The object table for a cross-reference. For an encrypted file the encryption dictionary is loaded first, and the
    /// password checked, so every object after it is decrypted as it loads.
    /// </summary>
    private PdfObjectTable OpenObjects(XrefTable xref, ForwardingResolver references)
    {
        ObjectLoader loader = new(source, xref, Options, log);
        PdfObjectTable objects = new(loader, log);
        ((IObjectResolver)objects).Saw(references.HighestSeen);
        references.Target = objects;

        // A rebuilt cross-reference opens the objects again, and its trailer alone decides whether the file is encrypted.
        security = null;
        EncryptNumber = null;
        if (xref.Trailer["Encrypt"] is PdfObject encryption && encryption.Resolve() is PdfDictionary encrypt)
        {
            byte[] firstId = (xref.Trailer.Get<PdfArray>("ID") is { Count: > 0 } ids ? ids.Get<PdfString>(0)?.Bytes.ToArray() : null) ?? [];
            security = global::Egap.Pdf.Security.StandardSecurityHandler.Open(encrypt, firstId, Options.Password, log);
            loader.EncryptNumber = (encryption as PdfReference)?.ObjectNumber;
            EncryptNumber = loader.EncryptNumber;
            loader.Decryptor = security;
        }

        return objects;
    }

    /// <summary>
    /// The pages in order, numbered from 0. The page tree is walked on first use; only its nodes are loaded.
    /// </summary>
    public PdfPageCollection Pages => LazyInitializer.EnsureInitialized(ref pages, () => new PdfPageCollection(this));

    /// <summary>
    /// Flattens the annotations of every page, as <see cref="PdfAnnotationCollection.Flatten"/> does; form fields are flattened
    /// with <see cref="PdfForm.Flatten"/>.
    /// </summary>
    /// <returns>How many annotations were drawn.</returns>
    public int FlattenAnnotations(PdfFlattenFilter filter = PdfFlattenFilter.All, bool includeLinks = false)
    {
        // By index: flattening a page of a tree repaired only in memory rebuilds the tree, which replaces the page list.
        int drawn = 0;
        for (int i = 0; i < Pages.Count; i++)
        {
            drawn += Pages[i].Annotations.Flatten(filter, includeLinks);
        }

        return drawn;
    }

    /// <summary>
    /// A new document with the pages of each source in turn (Phase 2 task 18), imported as
    /// <see cref="PdfPageCollection.Import"/> imports them; the sources must stay open until it is written.
    /// </summary>
    public static PdfDocument Merge(params IEnumerable<PdfDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        PdfDocument merged = Create();
        try
        {
            foreach (PdfDocument source in sources)
            {
                merged.Pages.Import(source);
            }

            return merged;
        }
        catch
        {
            merged.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new document with some of this document's pages, in the order given (Phase 2 task 18), imported as
    /// <see cref="PdfPageCollection.Import"/> imports them; this document must stay open until it is written.
    /// </summary>
    public PdfDocument ExtractPages(IEnumerable<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        PdfDocument extracted = Create();
        try
        {
            extracted.Pages.Import(this, pageIndices);
            return extracted;
        }
        catch
        {
            extracted.Dispose();
            throw;
        }
    }

    /// <summary>The copier for a source document, made on first use, so each source has one map (Phase 2 Q4).</summary>
    internal Writing.ObjectCopier CopierFor(PdfDocument source)
    {
        lock (copiers)
        {
            if (!copiers.TryGetValue(source, out Writing.ObjectCopier? copier))
            {
                copier = new Writing.ObjectCopier(source, this);
                copiers[source] = copier;
            }

            return copier;
        }
    }

    private readonly Dictionary<PdfDocument, Writing.ObjectCopier> copiers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The file's revisions, oldest first (Phase 5 design, 5.1): the original file and each incremental update after it. A
    /// file whose cross-reference had to be rebuilt has one. Found on first use by reading each <c>startxref</c>.
    /// </summary>
    public IReadOnlyList<PdfRevision> Revisions => LazyInitializer.EnsureInitialized(ref revisions, FindRevisions);

    private IReadOnlyList<PdfRevision>? revisions;

    private PdfRevision[] FindRevisions()
    {
        List<global::Egap.Pdf.Signatures.RevisionScanner.Found> found = global::Egap.Pdf.Signatures.RevisionScanner.Scan(source, Xref, Rebuilt);
        return [.. found.Select((f, i) => new PdfRevision(this, i, f.XrefOffset, f.EofEnd, f.Length, i == found.Count - 1))];
    }

    /// <summary>
    /// The signed signature fields and document timestamps of the form, in the order they were signed (Phase 5 design, 5.1).
    /// Reading them checks nothing; <see cref="PdfSignature.ByteRangeIssues"/> says what is wrong with each byte range.
    /// </summary>
    public IReadOnlyList<PdfSignature> Signatures => PdfSignature.Find(this);

    /// <summary>
    /// Signs the document into <paramref name="output"/> (Phase 5 design, 5.1): an incremental update that puts a signature
    /// into the signature field <paramref name="fieldName"/>, made as an invisible field on the first page when the form has
    /// none of that name. The original file is copied to the output first, unchanged, so a failed signature never touches
    /// the input. Afterwards this document is read-only: the signed file is the output, which can be opened to go on.
    /// </summary>
    /// <param name="fieldName">The signature field's fully qualified name.</param>
    /// <param name="options">The signer, level, format and dictionary entries.</param>
    /// <param name="output">Where the signed file goes, from its current position.</param>
    /// <param name="cancellationToken">Cancels the signing.</param>
    /// <exception cref="ArgumentException">The options have no signer, or the field is not an unsigned signature field.</exception>
    /// <exception cref="PdfException">The file needed repair, or the signature does not fit the reserved space.</exception>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("contract.pdf");
    /// using FileStream output = File.Create("contract-signed.pdf");
    /// await pdf.SignAsync("Signature1", new PdfSignOptions { Signer = PdfSigner.FromCertificate(certificate), Reason = "Approved" }, output);
    /// </code>
    /// </example>
    public Task SignAsync(string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        return global::Egap.Pdf.Signatures.SignatureWriter.SignAsync(this, fieldName, options, output, cancellationToken);
    }

    /// <summary>
    /// Prepares a signature made elsewhere (Phase 5 design Q3): writes the signed file to <paramref name="output"/> with the
    /// space for the CMS reserved and returns the digest the CMS must sign, which
    /// <see cref="PdfPreparedSignature.CompleteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> writes in. The output must be
    /// seekable. Afterwards this document is read-only, as after <see cref="SignAsync"/>.
    /// </summary>
    /// <param name="fieldName">The signature field's fully qualified name.</param>
    /// <param name="options">The level, format and dictionary entries; a signer, if given, sizes the reserved space.</param>
    /// <param name="output">Where the prepared file goes, from its current position; seekable.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    public Task<PdfPreparedSignature> PrepareSignatureAsync(string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        return global::Egap.Pdf.Signatures.SignatureWriter.PrepareDeferredAsync(this, fieldName, options, output, cancellationToken);
    }

    /// <summary>
    /// Adds a document timestamp into <paramref name="output"/> (ISO 32000-2, 12.8.5): an incremental update with an RFC 3161
    /// token over the whole file, which proves when it existed and, over long-term validation data, is how PAdES B-LTA ends.
    /// Afterwards this document is read-only, as after <see cref="SignAsync"/>.
    /// </summary>
    /// <param name="timestamp">The timestamp authority.</param>
    /// <param name="output">Where the timestamped file goes, from its current position.</param>
    /// <param name="fieldName">The timestamp's field name; <c>DocTimeStamp1</c>, or the next number free, when left out.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task TimestampAsync(PdfTimestampClient timestamp, Stream output, string? fieldName = null, CancellationToken cancellationToken = default)
    {
        return global::Egap.Pdf.Signatures.SignatureWriter.TimestampAsync(this, timestamp, fieldName, null, output, cancellationToken);
    }

    /// <summary>
    /// Adds long-term validation data into <paramref name="output"/> (Phase 5 design Q4): for every signature and timestamp,
    /// its chain's certificates and an OCSP response or CRL for each, from <paramref name="revocation"/>, in the Document
    /// Security Store, as an incremental update; with <paramref name="archiveTimestamp"/>, a document timestamp over it all
    /// (PAdES B-LTA). Signatures made at B-T become B-LT, or B-LTA. Afterwards this document is read-only, as after
    /// <see cref="SignAsync"/>.
    /// </summary>
    /// <param name="revocation">Where the OCSP responses and CRLs come from.</param>
    /// <param name="output">Where the file goes, from its current position.</param>
    /// <param name="archiveTimestamp">A timestamp authority for the document timestamp, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task AddValidationDataAsync(PdfRevocationSource revocation, Stream output, PdfTimestampClient? archiveTimestamp = null, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        await global::Egap.Pdf.Signatures.LongTermWriter.AddAsync(this, revocation, archiveTimestamp, output, copyBase: true, cancellationToken).ConfigureAwait(false);
        MarkSignedElsewhere();
    }

    /// <summary>
    /// Whether the document cannot be saved: an earlier revision opened with <see cref="PdfRevision.Open"/>, or a document
    /// already signed into another stream, whose saved state is that stream (open it to go on).
    /// </summary>
    public bool IsReadOnly { get; private set; }

    private string readOnlyReason = "An earlier revision opened with PdfRevision.Open is read-only and cannot be saved.";

    /// <summary>A read-only document over the first <paramref name="length"/> bytes of this one's file.</summary>
    internal PdfDocument OpenRevision(long length, PdfReadOptions options)
    {
        PdfDocument revision = Open(new PrefixSource(source, length), options);
        revision.IsReadOnly = true;
        return revision;
    }

    /// <summary>The length of the file the document was opened from.</summary>
    internal long FileLength => source.Length;

    /// <summary>
    /// The document with <paramref name="update"/> appended to its file: read-only for checking what a save would do, or
    /// writable for adding another update after it (long-term validation data, then an archive timestamp).
    /// </summary>
    internal PdfDocument OpenWithUpdate(byte[] update, bool writable = false)
    {
        PdfDocument after = Open(new AppendedSource(new PrefixSource(source, baseLength), update), Options with { Strict = false });
        after.IsReadOnly = !writable;
        return after;
    }

    /// <summary>Reads bytes of the file the document was opened from, as it was opened.</summary>
    internal byte[] ReadFileBytes(long position, int count)
    {
        return source.ReadBytes(position, count);
    }

    internal void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(readOnlyReason);
        }
    }

    /// <summary>
    /// Applies the redaction annotations of every page (Phase 5 design Q9): the text, line art and images each one covers are
    /// removed for real, not hidden, and the annotations are deleted. Afterwards every save rewrites the file whole with unused
    /// objects dropped, so nothing redacted survives in an earlier revision; that also breaks any signature.
    /// </summary>
    /// <example>
    /// <code>
    /// page.Annotations.Add(new PdfRedactAnnotation(area) { OverlayText = "REDACTED" });
    /// pdf.ApplyRedactions(new PdfRedactionOptions { Images = PdfRedactImages.Pixels, Paths = PdfRedactPaths.Covered });
    /// pdf.Save("clean.pdf");
    /// </code>
    /// </example>
    public PdfRedactionResult ApplyRedactions(PdfRedactionOptions? options = null)
    {
        ThrowIfReadOnly();
        return global::Egap.Pdf.Redaction.Redactor.Apply(this, options ?? PdfRedactionOptions.Default);
    }

    /// <summary>
    /// Removes what the document hides beyond its pages (Phase 5 design Q10): metadata, scripts and actions, attachments,
    /// hidden layers, comments, form data, thumbnails and private application data, each a switch in the options and all on
    /// by default. Afterwards every save rewrites the file whole with unused objects dropped, so earlier revisions go too.
    /// </summary>
    public PdfSanitizeResult Sanitize(PdfSanitizeOptions? options = null)
    {
        ThrowIfReadOnly();
        return global::Egap.Pdf.Redaction.DocumentSanitizer.Apply(this, options ?? PdfSanitizeOptions.Default);
    }

    /// <summary>Whether redactions were applied, so every save rewrites the file whole and drops unused objects.</summary>
    internal bool Redacted { get; private set; }

    internal void MarkRedacted()
    {
        Redacted = true;
    }

    /// <summary>Makes the document read-only after it was signed into another stream: its changes now live there.</summary>
    internal void MarkSignedElsewhere()
    {
        IsReadOnly = true;
        readOnlyReason = "The document was signed into another stream; open that stream to read or change the signed file.";
    }

    /// <summary>
    /// The incremental update a save with <paramref name="options"/> would append, after the steps a save takes first, and
    /// the length of the file it goes after; null when nothing changed. For signing (Phase 5), which writes the update itself.
    /// </summary>
    internal (byte[] Bytes, long BaseLength)? BuildUpdate(PdfSaveOptions options)
    {
        BeforeSave(options);
        return Writing.IncrementalWriter.Update(this, baseLength, previousSection, EndsWithNewline(), options) is { } update ? (update.Bytes, baseLength) : null;
    }

    /// <summary>
    /// Copies the original file, up to where an update goes, to <paramref name="output"/> (or nowhere, when it is there already),
    /// adding it to <paramref name="hash"/> as it goes.
    /// </summary>
    internal async Task CopyBaseAsync(Stream? output, System.Security.Cryptography.IncrementalHash hash, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256 * 1024];
        for (long at = 0; at < baseLength;)
        {
            int read = source.Read(at, buffer.AsSpan(0, (int)Math.Min(buffer.Length, baseLength - at)));
            if (read == 0)
            {
                throw new IOException("The file the document was opened from ended early.");
            }

            hash.AppendData(buffer, 0, read);
            if (output is not null)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            at += read;
        }
    }

    /// <summary>The interactive form, the catalog's /AcroForm.</summary>
    public PdfForm Form => new(this);

    /// <summary>The optional content groups (layers) of the catalog's /OCProperties.</summary>
    public PdfLayerCollection Layers => new(this);

    /// <summary>The embedded files of the catalog's /Names /EmbeddedFiles tree, kept in step with /AF.</summary>
    public PdfAttachmentCollection Attachments => new(this);

    /// <summary>The document outline (bookmarks); created in the catalog by the first item added.</summary>
    public PdfOutline Outline => new(this);

    /// <summary>Whether the outline has had its repair pass (see <see cref="OutlineReader"/>).</summary>
    internal bool OutlineChecked { get; set; }

    /// <summary>Guards the outline's repair pass.</summary>
    internal Lock OutlineGate { get; } = new();

    /// <summary>
    /// The fonts, images and other objects Egap has written for drawing, by what they were made from, so that 1,000 pages
    /// drawn with one font or image write it once (Phase 3 design Q6).
    /// </summary>
    internal Content.ResourceCache WrittenResources { get; } = new();

    /// <summary>What renders of this document's pages share: colour spaces, fonts and decoded images (Phase 4 design Q3).</summary>
    internal Rendering.RenderCache RenderCache => LazyInitializer.EnsureInitialized(ref renderCache, () => new Rendering.RenderCache(this));

    /// <summary>Where <see cref="PdfPage.Flush"/> moves finished streams; made the first time a page is flushed.</summary>
    internal ScratchFile Scratch => scratch ??= new ScratchFile();

    private ScratchFile? scratch;
    private Rendering.RenderCache? renderCache;

    /// <summary>The named destinations, through the catalog's /Names /Dests tree and its older /Dests dictionary.</summary>
    public PdfNamedDestinations NamedDestinations => new(this);

    /// <summary>The document's warnings, for the views that repair what they read.</summary>
    internal WarningLog Log => log;

    /// <summary>The security handler of an encrypted document, for the writers.</summary>
    internal global::Egap.Pdf.Security.StandardSecurityHandler? Security => security;

    /// <summary>The encryption dictionary's object number, when it is an indirect object.</summary>
    internal int? EncryptNumber { get; private set; }

    /// <summary>Whether the file is encrypted. Its strings and streams are decrypted as they load.</summary>
    public bool IsEncrypted => security is not null;

    /// <summary>
    /// What the file's owner allows, from <c>/P</c>; <see cref="PdfPermissions.All"/> for a file that is not encrypted or that
    /// was opened with the owner password. Advisory only: honouring them is the caller's job (design Q5).
    /// </summary>
    public PdfPermissions Permissions => security is null || security.IsOwner ? PdfPermissions.All : (PdfPermissions)security.Permissions & PdfPermissions.All;

    /// <summary>
    /// Whether the document has every permission: it is not encrypted, or it was opened with the owner password.
    /// </summary>
    public bool IsOwner => security?.IsOwner ?? true;

    /// <summary>Reads the cross-reference with offsets counted from the header, or returns null when that fails too.</summary>
    private XrefTable? TryShifted(PdfSource original, long header, IObjectResolver references)
    {
        try
        {
            return new XrefReader(new OffsetSource(new UnownedSource(original), header), Options, log, references).Read();
        }
        catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
        {
            return null;
        }
    }

    /// <summary>The options the document was opened with.</summary>
    public PdfReadOptions Options { get; }

    /// <summary>
    /// The PDF version: the header's, or the catalog's <c>/Version</c> when that is higher (7.2.2), such as <c>1.7</c>.
    /// </summary>
    public string Version
    {
        get
        {
            string? catalogVersion = Catalog.GetName("Version")?.Value;
            return catalogVersion is not null && IsVersion(catalogVersion) && string.CompareOrdinal(catalogVersion, HeaderVersion) > 0 ? catalogVersion : HeaderVersion;
        }
    }

    /// <summary>The trailer dictionary, merged over all of the file's sections: the newest value of each key.</summary>
    public PdfDictionary Trailer { get; }

    /// <summary>The document catalog, the trailer's <c>/Root</c>.</summary>
    public PdfDictionary Catalog => Trailer.Get<PdfDictionary>("Root") ?? throw new PdfException("The trailer has no /Root dictionary.");

    /// <summary>The indirect objects.</summary>
    public PdfObjectTable Objects { get; private set; }

    /// <summary>
    /// Every repair made while reading, in the order found. Objects load lazily, so the list can grow as the document is
    /// used.
    /// </summary>
    public IReadOnlyList<PdfWarning> Warnings => log.ToArray();

    /// <summary>Whether any object or the trailer changed since the document was opened.</summary>
    public bool IsChanged => trailerOwner.Changed || Objects.IsChanged;

    /// <summary>The version in the file's header.</summary>
    internal string HeaderVersion { get; }

    internal XrefTable Xref { get; }

    /// <summary>Whether the cross-reference was rebuilt by scanning; such a file saves by full rewrite (hub Q8).</summary>
    internal bool Rebuilt { get; }

    /// <summary>Whether the document was made by <see cref="Create(PdfCreateOptions)"/>: it saves whole, compact by default (design Q7).</summary>
    internal bool IsNew { get; private set; }

    /// <summary>Opens a PDF file. The file stays open, shared for reading, until the document is disposed.</summary>
    /// <exception cref="PdfException">The file is not a PDF the reader can open.</exception>
    /// <exception cref="PdfLimitException">The file goes past a limit in <paramref name="options"/>.</exception>
    public static PdfDocument Open(string path, PdfReadOptions? options = null)
    {
        return Open(PdfSource.FromFile(path), options);
    }

    /// <summary>
    /// Opens a PDF from a stream. A seekable stream is read in place and must stay unchanged while the document is used; a
    /// non-seekable one is copied to memory first.
    /// </summary>
    /// <param name="stream">The stream; the file starts at position 0.</param>
    /// <param name="options">The read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">Whether the stream stays open after the document is disposed.</param>
    public static PdfDocument Open(Stream stream, PdfReadOptions? options = null, bool leaveOpen = false)
    {
        return Open(PdfSource.FromStream(stream, leaveOpen), options);
    }

    /// <summary>Opens a PDF held in memory. The bytes must stay unchanged while the document is used.</summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> bytes, PdfReadOptions? options = null)
    {
        return Open(PdfSource.FromMemory(bytes), options);
    }

    /// <summary>
    /// Opens a PDF from a stream that is slow to read, such as a network stream: it is copied to memory asynchronously, then
    /// opened from there. The stream is disposed afterwards unless <paramref name="leaveOpen"/> is set.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(Stream stream, PdfReadOptions? options = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MemoryStream copy = new();
        try
        {
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!leaveOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        return Open(copy.GetBuffer().AsMemory(0, (int)copy.Length), options);
    }

    /// <summary>
    /// Creates an empty PDF 1.7 document (hub Q11): a catalog and a page tree with no pages. It always saves by full rewrite,
    /// with object streams and a cross-reference stream unless <see cref="PdfSaveOptions.Compression"/> says otherwise.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Create();
    /// pdf.Trailer["Info"] = pdf.Objects.Add(new PdfDictionary { ["Title"] = PdfString.FromText("Report") });
    /// pdf.Save("new.pdf");
    /// </code>
    /// </example>
    public static PdfDocument Create()
    {
        return Create(PdfCreateOptions.Default);
    }

    /// <summary>
    /// Creates an empty document with options (Phase 5 design Q11): with a PDF/A conformance, the document is the part's PDF
    /// version (1.4, 1.7 or 2.0), carries an sRGB output intent and the XMP identification, and every save applies the part's
    /// writer rules (see <see cref="PdfCreateOptions.Conformance"/>).
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = PdfAConformance.PdfA2b });
    /// pdf.Metadata.Title = "Annual report";
    /// pdf.Save("archive.pdf");
    /// </code>
    /// </example>
    public static PdfDocument Create(PdfCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string version = options.Conformance == PdfAConformance.None ? "1.7" : PdfA.PdfAWriter.Version(options.Conformance);
        PdfDocument document = Open(PdfSource.FromMemory(EmptyFile(version)), null);
        document.IsNew = true;
        if (options.Conformance != PdfAConformance.None)
        {
            document.CreatedConformance = options.Conformance;
            PdfA.PdfAWriter.Identify(document, options.Conformance);
        }

        return document;
    }

    /// <summary>The conformance the document was created with, whose writer rules each save applies; None otherwise.</summary>
    internal PdfAConformance CreatedConformance { get; private set; }

    /// <summary>
    /// The PDF/A conformance the document claims in its XMP metadata (<c>pdfaid:part</c> and <c>pdfaid:conformance</c>), or
    /// <see cref="PdfAConformance.None"/> when it claims none Egap writes. A claim is not a check of the rules.
    /// </summary>
    public PdfAConformance Conformance => PdfA.PdfAWriter.Read(this);

    /// <summary>The bytes of an empty file: object 1 the catalog, object 2 an empty page tree.</summary>
    private static byte[] EmptyFile(string version)
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>"];
        StringBuilder file = new($"%PDF-{version}\n");
        List<int> offsets = [];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(file.Length);
            file.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = file.Length;
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            file.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(file.ToString());
    }

    private static PdfDocument Open(PdfSource source, PdfReadOptions? options)
    {
        try
        {
            return new PdfDocument(source, options ?? PdfReadOptions.Default);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Saves the document to a file. An incremental save (the default) copies the original bytes and appends the changes;
    /// with no changes the file is copied byte for byte. A document that needed repair, or a <see cref="PdfSaveMode.Rewrite"/>,
    /// is written whole (hub Q8). Saving over the file the document was opened from appends the changes in place, cutting
    /// the file back if writing fails; a rewrite over it goes through a temporary file (design Q4).
    /// </summary>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfReadOnly();
        options = Forced(options ?? PdfSaveOptions.Default);
        BeforeSave(options);
        string full = Path.GetFullPath(path);
        bool sameFile = string.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase);
        if (WritesWhole(options))
        {
            GuardSignatures(options, rewrite: true, null);
            if (sameFile)
            {
                ReplaceFile(full, output => Writing.RewriteWriter.Write(this, output, options));
            }
            else
            {
                using FileStream file = new(full, FileMode.Create, FileAccess.Write);
                Writing.RewriteWriter.Write(this, file, options);
            }

            return;
        }

        (byte[] Bytes, long Xref)? update = Writing.IncrementalWriter.Update(this, baseLength, previousSection, EndsWithNewline(), options);
        GuardSignatures(options, rewrite: false, update?.Bytes);
        if (sameFile && new FileInfo(full).Length == baseLength)
        {
            if (update is { } appended)
            {
                AppendInPlace(full, appended.Bytes);
                baseLength += appended.Bytes.Length;
                previousSection = (appended.Xref, previousSection.IsStream);
                (source as BlockCache)?.Reset(baseLength);
                Objects.MarkSaved();
                trailerOwner.Reset();
            }

            return;
        }

        if (sameFile)
        {
            ReplaceFile(full, output => CopyWithUpdate(output, update?.Bytes));
            return;
        }

        using FileStream target = new(full, FileMode.Create, FileAccess.Write);
        CopyWithUpdate(target, update?.Bytes);
    }

    /// <summary>Saves the document to a stream, from its current position. See <see cref="Save(string, PdfSaveOptions?)"/>.</summary>
    public void Save(Stream output, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        SaveTo(output, options);
    }

    /// <summary>
    /// Saves the document to a stream that is slow to write, such as a network stream: the file is written to memory, then
    /// copied to <paramref name="output"/> asynchronously. See <see cref="Save(string, PdfSaveOptions?)"/>.
    /// </summary>
    public async Task SaveAsync(Stream output, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        using MemoryStream buffer = new();
        SaveTo(buffer, options);
        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private void SaveTo(Stream output, PdfSaveOptions? options)
    {
        ThrowIfReadOnly();
        options = Forced(options ?? PdfSaveOptions.Default);
        BeforeSave(options);
        if (WritesWhole(options))
        {
            GuardSignatures(options, rewrite: true, null);
            Writing.RewriteWriter.Write(this, output, options);
            return;
        }

        byte[]? update = Writing.IncrementalWriter.Update(this, baseLength, previousSection, EndsWithNewline(), options)?.Bytes;
        GuardSignatures(options, rewrite: false, update);
        CopyWithUpdate(output, update);
    }

    /// <summary>The save options with what redaction forces: a rewrite that drops unused objects (Phase 5 design Q9).</summary>
    private PdfSaveOptions Forced(PdfSaveOptions options)
    {
        options = Redacted ? options with { Mode = PdfSaveMode.Rewrite, RemoveUnused = true } : options;
        return CreatedConformance == PdfAConformance.None ? options : PdfA.PdfAWriter.Options(CreatedConformance, options);
    }

    /// <summary>Warns, or throws in strict mode, before a save breaks a signature, unless the options allow it (Phase 5 design Q7).</summary>
    private void GuardSignatures(PdfSaveOptions options, bool rewrite, byte[]? update)
    {
        if (!options.AllowInvalidatingSignatures && !IsNew)
        {
            global::Egap.Pdf.Signatures.SignatureGuard.Check(this, rewrite, update);
        }
    }

    /// <summary>The clock a save reads the modification date from; tests set their own.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The document's metadata: the Info dictionary and the XMP packet through one API (Phase 2 Q8).</summary>
    public PdfMetadata Metadata => new(this);

    /// <summary>Sets the modification date before a save with changes, unless the options say not to (Phase 2 Q9).</summary>
    private void BeforeSave(PdfSaveOptions options)
    {
        // Font subsets drawing used are written now, when their glyphs are known.
        WrittenResources.Finish();
        if (CreatedConformance != PdfAConformance.None)
        {
            PdfA.PdfAWriter.Apply(this, CreatedConformance);
        }

        if (options.UpdateModificationDate && !options.Deterministic && IsChanged)
        {
            Metadata.Touch(Clock.GetLocalNow());
        }

        // A file written whole holds only what the catalog reaches, so pages found by repair need a tree of their own first.
        if (WritesWhole(options))
        {
            Pages.RebuildIfRepaired();
        }
    }

    /// <summary>Whether the trailer changed since the document was opened or last saved in place.</summary>
    internal bool TrailerChanged => trailerOwner.Changed;

    /// <summary>
    /// Whether a save writes the whole document: when asked, or when the file needed repair to its structure, which an
    /// update would leave in place (hub Q8).
    /// </summary>
    internal bool WritesWhole(PdfSaveOptions options)
    {
        // Dropping or replacing encryption changes every string and stream, so it writes everything too.
        bool encryptionChanges = options.Encryption.Kind == PdfEncryptionKind.Aes256 || (options.Encryption.Kind == PdfEncryptionKind.None && IsEncrypted);
        // A compression other than Preserve changes how every object is stored, so it can only apply to a whole file.
        // A file cut short has no %%EOF at its end; a stream without endstream there would run on into appended changes.
        bool truncated = IsChanged && SourceSearch.LastIndexOf(source, "%%EOF"u8, source.Length, Math.Max(0, source.Length - 1024)) < 0;
        return options.Mode == PdfSaveMode.Rewrite || options.Compression != PdfCompression.Preserve || encryptionChanges || IsNew || Rebuilt || truncated || log.ToArray().Any(w => w.Code is PdfWarningCode.MalformedXref or PdfWarningCode.BrokenXrefChain
            or PdfWarningCode.XrefRebuilt or PdfWarningCode.ObjectRelocated or PdfWarningCode.ShiftedOffsets

            // A stream without endstream runs to the end of the file, so bytes appended after it would become its data.
            or PdfWarningCode.MissingEndstream);
    }

    private bool EndsWithNewline()
    {
        return baseLength == 0 || source.ReadBytes(baseLength - 1, 1) is [(byte)'\n' or (byte)'\r'];
    }

    private void CopyWithUpdate(Stream output, byte[]? update)
    {
        byte[] buffer = new byte[64 * 1024];
        for (long at = 0; at < baseLength;)
        {
            int read = source.Read(at, buffer.AsSpan(0, (int)Math.Min(buffer.Length, baseLength - at)));
            if (read == 0)
            {
                throw new IOException("The file the document was opened from ended early.");
            }

            output.Write(buffer, 0, read);
            at += read;
        }

        if (update is not null)
        {
            output.Write(update);
        }
    }

    /// <summary>Appends an update to the document's own file; if writing fails, the file is cut back to its old length.</summary>
    private void AppendInPlace(string path, byte[] update)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Position = baseLength;
        try
        {
            file.Write(update);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            file.SetLength(baseLength);
            throw;
        }
    }

    /// <summary>Writes a new file next to <paramref name="path"/> and puts it in place of the one there, which may be open.</summary>
    private static void ReplaceFile(string path, Action<Stream> write)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                write(output);
            }

            // Windows will not replace a file that is open, but it renames and deletes one opened with delete sharing,
            // as the document's file is: move the original aside, put the new file in place, then delete the original.
            // The document keeps reading the original's bytes until it is disposed.
            string aside = temporary + ".old";
            File.Move(path, aside);
            try
            {
                File.Move(temporary, path);
            }
            catch
            {
                File.Move(aside, path);
                throw;
            }

            File.Delete(aside);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>The full path of the file the document was opened from, or null for a stream or memory.</summary>
    private string? FilePath => (source is OffsetSource shifted ? shifted.Inner : source) is FileSource file ? file.Path : null;

    /// <summary>Closes the file. Objects not yet loaded can no longer be loaded.</summary>
    public void Dispose()
    {
        source.Dispose();
        scratch?.Dispose();
        renderCache = null;
    }

    private string ReadHeaderVersion(out long at)
    {
        // The header is "%PDF-1.n" or "%PDF-2.0"; some files have junk before it, so look through the first kilobyte.
        at = SourceSearch.IndexOf(source, "%PDF-"u8, 0, 1024);
        if (at >= 0)
        {
            string version = Encoding.ASCII.GetString(source.ReadBytes(at + 5, 3));
            if (IsVersion(version))
            {
                return version;
            }
        }

        log.Add(PdfWarningCode.MissingHeader, "The file has no '%PDF-n.n' header; version 1.7 was assumed.", at >= 0 ? at : 0);
        return "1.7";
    }

    private static bool IsVersion(string version)
    {
        return version.Length == 3 && char.IsAsciiDigit(version[0]) && version[1] == '.' && char.IsAsciiDigit(version[2]);
    }

    /// <summary>Tracks changes to the trailer, which is not an indirect object.</summary>
    private sealed class TrailerOwner : IObjectOwner
    {
        public bool Changed { get; private set; }

        public void OnChanged()
        {
            Changed = true;
        }

        public void Reset()
        {
            Changed = false;
        }
    }
}
