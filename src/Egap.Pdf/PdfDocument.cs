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

    /// <summary>Whether the document was made by <see cref="Create"/>: it saves whole, compact by default (design Q7).</summary>
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
        PdfDocument document = Open(PdfSource.FromMemory(EmptyFile()), null);
        document.IsNew = true;
        return document;
    }

    /// <summary>The bytes of an empty PDF 1.7 file: object 1 the catalog, object 2 an empty page tree.</summary>
    private static byte[] EmptyFile()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>"];
        StringBuilder file = new("%PDF-1.7\n");
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
        options ??= PdfSaveOptions.Default;
        string full = Path.GetFullPath(path);
        bool sameFile = string.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase);
        if (WritesWhole(options))
        {
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
        options ??= PdfSaveOptions.Default;
        if (WritesWhole(options))
        {
            Writing.RewriteWriter.Write(this, output, options);
            return;
        }

        CopyWithUpdate(output, Writing.IncrementalWriter.Update(this, baseLength, previousSection, EndsWithNewline(), options)?.Bytes);
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
