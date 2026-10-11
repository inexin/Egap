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
        try
        {
            xref = new XrefReader(source, options, log, references).Read();
        }
        catch (PdfException e) when (header > 0 && e is not PdfLimitException && e.Code is null)
        {
            // Junk before the header, and offsets that count from the header, as Acrobat and qpdf accept.
            OffsetSource shifted = new(source, header);
            xref = new XrefReader(shifted, options, log, references).Read();
            this.source = shifted;
            log.Add(PdfWarningCode.ShiftedOffsets, $"The header is at byte {header}, and the file's offsets count from there.", header);
        }

        Xref = xref;
        Objects = new PdfObjectTable(new ObjectLoader(this.source, xref, options, log), log);
        references.Target = Objects;
        Trailer = xref.Trailer;
        Containment.Bind(Trailer, trailerOwner);
        if (Trailer.Get("Root") is not PdfDictionary)
        {
            throw new PdfException("The trailer has no /Root dictionary (the document catalog).");
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
    public PdfObjectTable Objects { get; }

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
