using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Docx;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// An OpenDocument text file read whole into memory within <see cref="DocumentLimits"/> (ODF 1.3 Part 2): a ZIP package with
/// its <c>mimetype</c>, manifest, <c>content.xml</c>, <c>styles.xml</c>, <c>meta.xml</c> and <c>settings.xml</c> and the files
/// they name (pictures, embedded objects), or a flat XML document (FODT) holding all of it in one <c>office:document</c>.
/// </summary>
internal sealed class OdfPackage
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

    private OdfPackage()
    {
    }

    /// <summary>The media type: the <c>mimetype</c> file's, the manifest's root entry's or the flat document's.</summary>
    public string? MediaType { get; private set; }

    /// <summary>Whether this is a template (OTT, or a flat template).</summary>
    public bool IsTemplate => MediaType == TemplateMediaType;

    /// <summary>Whether this is a flat XML document, whose styles and content share one tree.</summary>
    public bool IsFlat { get; private set; }

    /// <summary>The ODF version the document's root names, such as "1.3", or null.</summary>
    public string? Version { get; private set; }

    /// <summary>The root of <c>content.xml</c>, or the flat document's.</summary>
    public XElement Content { get; private set; } = null!;

    /// <summary>The root of <c>styles.xml</c>, or the flat document's; null when the package has none.</summary>
    public XElement? Styles { get; private set; }

    /// <summary>The root of <c>meta.xml</c>, or the flat document's; null when the package has none.</summary>
    public XElement? Meta { get; private set; }

    /// <summary>The root of <c>settings.xml</c>, or the flat document's; null when the package has none.</summary>
    public XElement? Settings { get; private set; }

    /// <summary>The manifest's entries by path: their media types.</summary>
    public IReadOnlyDictionary<string, string> Manifest { get; private set; } = new Dictionary<string, string>();

    /// <summary>The paths of the package's files, in no particular order.</summary>
    public IEnumerable<string> FileNames => files.Keys;

    /// <summary>Reads an ODT, OTT or FODT file.</summary>
    /// <exception cref="DocumentException">Not an OpenDocument text file, or encrypted.</exception>
    /// <exception cref="DocumentLimitException">Past a limit.</exception>
    public static OdfPackage Read(byte[] bytes, DocumentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        OdfPackage package = new();
        if (bytes is [0x50, 0x4B, 0x03, 0x04, ..])
        {
            package.ReadZip(bytes, limits);
        }
        else
        {
            package.ReadFlat(bytes, limits);
        }

        return package;
    }

    /// <summary>
    /// The media type an ODF package names in its first bytes (ODF 1.3 Part 2, 3.3: a stored, uncompressed first entry named
    /// <c>mimetype</c>), or null.
    /// </summary>
    public static string? MediaTypeOfZip(ReadOnlySpan<byte> head)
    {
        if (head is not [0x50, 0x4B, 0x03, 0x04, ..] || head.Length < 38 || !head[30..38].SequenceEqual("mimetype"u8))
        {
            return null;
        }

        int extra = head[28] | (head[29] << 8);
        int size = head[18] | (head[19] << 8) | (head[20] << 16) | (head[21] << 24);
        int start = 38 + extra;
        if (size <= 0 || size > 200 || start + size > head.Length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(head.Slice(start, size));
    }

    /// <summary>Whether the bytes start a flat ODF text document: XML whose root is <c>office:document</c> of a text type.</summary>
    public static bool IsFlatText(ReadOnlySpan<byte> head)
    {
        if (head is [0xEF, 0xBB, 0xBF, ..])
        {
            head = head[3..];
        }

        head = head.TrimStart(" \t\r\n"u8);
        return head.StartsWith("<"u8) && head.IndexOf("office:document"u8) >= 0
            && head.IndexOf("urn:oasis:names:tc:opendocument:xmlns:office:1.0"u8) >= 0
            && head.IndexOf("\"application/vnd.oasis.opendocument.text"u8) >= 0;
    }

    /// <summary>The file at <paramref name="path"/> in the package (relative to its root, "./" and a leading "/" allowed), or null.</summary>
    public byte[]? GetFile(string path)
    {
        string name = NormalizePath(path);
        return name.Length > 0 && files.TryGetValue(name, out byte[]? data) ? data : null;
    }

    /// <summary>Loads an XML file of the package, or null when it is missing.</summary>
    public XElement? GetXml(string path, DocumentLimits limits)
    {
        return GetFile(path) is { } data ? LoadXml(data, path, limits) : null;
    }

    /// <summary>A package path without "./", a leading "/" or "..", as the ZIP names it.</summary>
    public static string NormalizePath(string path)
    {
        List<string> parts = [];
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(Uri.UnescapeDataString(part));
        }

        return string.Join('/', parts);
    }

    private void ReadZip(byte[] bytes, DocumentLimits limits)
    {
        try
        {
            using ZipArchive zip = new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new DocumentLimitException($"The package has {zip.Entries.Count} entries, more than the limit of {limits.MaxEntries}.");
            }

            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (entry.Length > limits.MaxPartSize)
                {
                    throw new DocumentLimitException($"File {entry.FullName} is {entry.Length} bytes, more than the limit of {limits.MaxPartSize}.");
                }

                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new DocumentLimitException($"File {entry.FullName} expands {entry.Length / entry.CompressedLength} times, past the limit of {limits.MaxCompressionRatio}.");
                }

                total += entry.Length;
                if (total > limits.MaxTotalSize)
                {
                    throw new DocumentLimitException($"The package's files add up to more than the limit of {limits.MaxTotalSize} bytes.");
                }

                files.TryAdd(NormalizePath(entry.FullName), OpcPackage.ReadEntry(entry, limits));
            }
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The file is not a valid ZIP package: " + e.Message, e);
        }

        ReadManifest(limits);
        if (files.TryGetValue("mimetype", out byte[]? mimetype))
        {
            MediaType = Encoding.ASCII.GetString(mimetype).Trim();
        }
        else if (Manifest.TryGetValue("", out string? root))
        {
            MediaType = root;
        }

        if (!IsText(MediaType))
        {
            throw new DocumentException(MediaType is null
                ? "The file is not an OpenDocument package: it has no mimetype."
                : $"The file is an OpenDocument package of type {MediaType}, not a text document.");
        }

        Content = GetXml("content.xml", limits) ?? throw new DocumentException("The OpenDocument package has no content.xml.");
        Styles = GetXml("styles.xml", limits);
        Meta = GetXml("meta.xml", limits);
        Settings = GetXml("settings.xml", limits);
        Version = Content.Attr(OfficeNs, "version");
    }

    private void ReadManifest(DocumentLimits limits)
    {
        Dictionary<string, string> manifest = new(StringComparer.Ordinal);
        Manifest = manifest;
        if (GetXml("META-INF/manifest.xml", limits) is not { } root)
        {
            return;
        }

        foreach (XElement entry in root.Elements(OdfNames.ManifestNs + "file-entry"))
        {
            string? path = entry.Attr(OdfNames.ManifestNs, "full-path");
            if (path is null)
            {
                continue;
            }

            // Encrypted files carry encryption data in the manifest (ODF 1.3 Part 2, 4.8); the content cannot be read
            // without the password.
            if (entry.Element(OdfNames.ManifestNs + "encryption-data") is not null)
            {
                throw new DocumentException("The OpenDocument file is password-protected, which Egap does not read yet.");
            }

            manifest[path == "/" ? "" : NormalizePath(path)] = entry.Attr(OdfNames.ManifestNs, "media-type") ?? "";
        }
    }

    private void ReadFlat(byte[] bytes, DocumentLimits limits)
    {
        XElement root = LoadXml(bytes, "the flat document", limits);
        if (root.Name != OfficeNs + "document")
        {
            throw new DocumentException("The file is not an OpenDocument file: its root is not office:document.");
        }

        MediaType = root.Attr(OfficeNs, "mimetype");
        if (!IsText(MediaType))
        {
            throw new DocumentException($"The flat OpenDocument file is of type {MediaType ?? "(none)"}, not a text document.");
        }

        IsFlat = true;
        Content = root;
        Styles = root;
        Meta = root;
        Settings = root;
        Version = root.Attr(OfficeNs, "version");
    }

    private static XElement LoadXml(byte[] data, string name, DocumentLimits limits)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = limits.MaxPartSize,
            CloseInput = true,
        };

        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(data, writable: false), settings);
            XDocument xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return xml.Root ?? throw new DocumentException($"{name} has no root element.");
        }
        catch (XmlException e)
        {
            throw new DocumentException($"{name} is not well-formed XML: {e.Message}", e);
        }
    }
}
