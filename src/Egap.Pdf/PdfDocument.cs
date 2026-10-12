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

        Objects = new PdfObjectTable(new ObjectLoader(this.source, xref, options, log), log);
        references.Target = Objects;
        if (xref.Trailer.Get("Root") is not PdfDictionary && !rebuilt)
        {
            // The cross-reference read, but names no usable catalog: rebuild it from what the file holds.
            xref = XrefRebuilder.Rebuild(this.source, options, log, references);
            rebuilt = true;
            Objects = new PdfObjectTable(new ObjectLoader(this.source, xref, options, log), log);
            references.Target = Objects;
        }

        Xref = xref;
        Rebuilt = rebuilt;
        Trailer = xref.Trailer;
        Containment.Bind(Trailer, trailerOwner);
        if (Trailer.Get("Root") is not PdfDictionary)
        {
            throw new PdfException("The file has no document catalog, even after scanning it for objects.");
        }
    }

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
    /// Saves the document to a file. Saving over the file the document was opened from is allowed: a rewrite goes to a
    /// temporary file in the same folder that then replaces the original (design Q4).
    /// </summary>
    /// <exception cref="NotSupportedException">The mode is not supported yet: incremental saving comes in a later step, and
    /// encrypted documents cannot be saved yet.</exception>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= PdfSaveOptions.Default;
        CheckMode(options);
        string full = Path.GetFullPath(path);
        if (string.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    Writing.RewriteWriter.Write(this, output, options);
                }

                // Windows will not replace a file that is open, but it renames and deletes one opened with delete sharing,
                // as the document's file is: move the original aside, put the new file in place, then delete the original.
                // The document keeps reading the original's bytes until it is disposed.
                string aside = temporary + ".old";
                File.Move(full, aside);
                try
                {
                    File.Move(temporary, full);
                }
                catch
                {
                    File.Move(aside, full);
                    throw;
                }

                File.Delete(aside);
            }
            finally
            {
                File.Delete(temporary);
            }

            return;
        }

        using FileStream file = new(full, FileMode.Create, FileAccess.Write);
        Save(file, options);
    }

    /// <summary>Saves the document to a stream, from its current position.</summary>
    /// <exception cref="NotSupportedException">The mode is not supported yet.</exception>
    public void Save(Stream output, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= PdfSaveOptions.Default;
        CheckMode(options);
        Writing.RewriteWriter.Write(this, output, options);
    }

    private static void CheckMode(PdfSaveOptions options)
    {
        if (options.Mode != PdfSaveMode.Rewrite)
        {
            throw new NotSupportedException("Incremental saving is not supported yet; save with PdfSaveMode.Rewrite.");
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
    }
}
