using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// An Open Packaging Conventions package (ECMA-376 Part 2) as DOCX uses it, read whole into memory within
/// <see cref="DocumentLimits"/>: parts by name with their content types, and the relationships of the package and each part
/// (Phase 6 design Q9: the BCL's ZipArchive and XmlReader, no DTDs, no external entities).
/// </summary>
internal sealed class OpcPackage
{
    private readonly Dictionary<string, OpcPart> parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);

    private OpcPackage(DocumentLimits limits)
    {
        Limits = limits;
    }

    public DocumentLimits Limits { get; }

    /// <summary>The relationships of the package itself (/_rels/.rels).</summary>
    public IReadOnlyList<OpcRelationship> Relationships { get; private set; } = [];

    public IEnumerable<OpcPart> Parts => parts.Values;

    /// <summary>Reads a package from a stream (left open).</summary>
    /// <exception cref="DocumentException">Not a ZIP package, or an old binary document.</exception>
    /// <exception cref="DocumentLimitException">Past a limit.</exception>
    public static OpcPackage Read(Stream stream, DocumentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[8];
        if (stream.CanSeek)
        {
            long start = stream.Position;
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = start;
            if (read >= 8 && head.SequenceEqual((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            {
                throw new DocumentException("The file is a compound file: an encrypted document or an old binary .doc, which Egap does not read.");
            }
        }

        OpcPackage package = new(limits);
        try
        {
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
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
                    throw new DocumentLimitException($"Part {entry.FullName} is {entry.Length} bytes, more than the limit of {limits.MaxPartSize}.");
                }

                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new DocumentLimitException($"Part {entry.FullName} expands {entry.Length / entry.CompressedLength} times, past the limit of {limits.MaxCompressionRatio}.");
                }

                total += entry.Length;
                if (total > limits.MaxTotalSize)
                {
                    throw new DocumentLimitException($"The package's parts add up to more than the limit of {limits.MaxTotalSize} bytes.");
                }

                byte[] data = ReadEntry(entry, limits);
                string name = NormalizeName(entry.FullName);
                package.parts.TryAdd(name, new OpcPart(package, name, data));
            }
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The file is not a valid ZIP package: " + e.Message, e);
        }

        package.ReadContentTypes();
        package.Relationships = package.ReadRelationships("/_rels/.rels", "/");
        foreach (OpcPart part in package.parts.Values)
        {
            part.ContentType = package.ContentTypeOf(part.Name);
        }

        return package;
    }

    /// <summary>The part named <paramref name="name"/> (case-insensitive, as OPC compares names), or null.</summary>
    public OpcPart? GetPart(string name)
    {
        return parts.GetValueOrDefault(NormalizeName(name));
    }

    /// <summary>The part a relationship of <paramref name="relationships"/> of <paramref name="type"/> points at, or null.</summary>
    public OpcPart? GetRelatedPart(IReadOnlyList<OpcRelationship> relationships, string type)
    {
        foreach (OpcRelationship relationship in relationships)
        {
            if (!relationship.External && OpcRelationshipTypes.Same(relationship.Type, type) && GetPart(relationship.TargetName) is { } part)
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>The relationships in the .rels part <paramref name="relsName"/>, targets resolved against <paramref name="sourceName"/>.</summary>
    internal IReadOnlyList<OpcRelationship> ReadRelationships(string relsName, string sourceName)
    {
        if (GetPart(relsName) is not { } rels)
        {
            return [];
        }

        XDocument xml = rels.LoadXml();
        List<OpcRelationship> list = [];
        foreach (XElement element in xml.Root?.Elements() ?? [])
        {
            if (element.Name.LocalName != "Relationship")
            {
                continue;
            }

            string? id = (string?)element.Attribute("Id");
            string? type = (string?)element.Attribute("Type");
            string? target = (string?)element.Attribute("Target");
            if (id is null || type is null || target is null)
            {
                continue;
            }

            bool external = string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            list.Add(new OpcRelationship(id, type, target, external, external ? target : ResolveTarget(sourceName, target)));
        }

        return list;
    }

    /// <summary>A part name in OPC's form: a leading slash, forward slashes, percent-escapes decoded.</summary>
    public static string NormalizeName(string name)
    {
        string normalized = name.Replace('\\', '/');
        try
        {
            normalized = Uri.UnescapeDataString(normalized);
        }
        catch (UriFormatException)
        {
        }

        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    /// <summary>A relationship target relative to its source part, resolved to a part name.</summary>
    public static string ResolveTarget(string sourceName, string target)
    {
        string path = target.Split('#', 2)[0];
        if (path.StartsWith('/'))
        {
            return NormalizeName(path);
        }

        string directory = sourceName[..(sourceName.LastIndexOf('/') + 1)];
        List<string> segments = [.. directory.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string segment in NormalizeName(path)[1..].Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>The name of the .rels part for <paramref name="partName"/>.</summary>
    public static string RelationshipsPartName(string partName)
    {
        int slash = partName.LastIndexOf('/');
        return partName[..(slash + 1)] + "_rels/" + partName[(slash + 1)..] + ".rels";
    }

    /// <summary>Safe XML reading for every part: no DTDs, no external entities, a size cap.</summary>
    internal XmlReaderSettings XmlSettings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = Limits.MaxPartSize,
        IgnoreProcessingInstructions = false,
        CloseInput = true,
    };

    private static byte[] ReadEntry(ZipArchiveEntry entry, DocumentLimits limits)
    {
        using Stream input = entry.Open();
        if (entry.Length is > 0 and <= int.MaxValue && entry.Length <= limits.MaxPartSize)
        {
            // The size the ZIP directory gives, read straight into its array; a stream that runs short or long is read again
            // the careful way.
            byte[] exact = new byte[entry.Length];
            int filled = input.ReadAtLeast(exact, exact.Length, throwOnEndOfStream: false);
            if (filled == exact.Length && input.ReadByte() < 0)
            {
                return exact;
            }

            input.Dispose();
            return ReadEntryChecked(entry, limits);
        }

        return ReadEntryChecked(entry, limits);
    }

    private static byte[] ReadEntryChecked(ZipArchiveEntry entry, DocumentLimits limits)
    {
        using Stream input = entry.Open();
        using MemoryStream output = new(capacity: (int)Math.Min(entry.Length, 64L * 1024 * 1024));
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            total += read;
            if (total > limits.MaxPartSize)
            {
                throw new DocumentLimitException($"Part {entry.FullName} grows past the limit of {limits.MaxPartSize} bytes when decompressed.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private void ReadContentTypes()
    {
        if (GetPart("/[Content_Types].xml") is not { } types)
        {
            throw new DocumentException("The package has no [Content_Types].xml part.");
        }

        XDocument xml = types.LoadXml();
        foreach (XElement element in xml.Root?.Elements() ?? [])
        {
            string? contentType = (string?)element.Attribute("ContentType");
            if (contentType is null)
            {
                continue;
            }

            if (element.Name.LocalName == "Default" && (string?)element.Attribute("Extension") is { } extension)
            {
                defaults[extension] = contentType;
            }
            else if (element.Name.LocalName == "Override" && (string?)element.Attribute("PartName") is { } partName)
            {
                overrides[NormalizeName(partName)] = contentType;
            }
        }
    }

    private string ContentTypeOf(string name)
    {
        if (overrides.TryGetValue(name, out string? type))
        {
            return type;
        }

        int dot = name.LastIndexOf('.');
        return dot > name.LastIndexOf('/') && defaults.TryGetValue(name[(dot + 1)..], out type) ? type : "application/octet-stream";
    }
}

/// <summary>One part of a package: its name, content type and bytes, its relationships read on first use.</summary>
internal sealed class OpcPart(OpcPackage package, string name, byte[] data)
{
    private IReadOnlyList<OpcRelationship>? relationships;

    public string Name { get; } = name;

    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] Data { get; } = data;

    /// <summary>This part's relationships (from its .rels part), resolved against its name.</summary>
    public IReadOnlyList<OpcRelationship> Relationships => relationships ??= package.ReadRelationships(OpcPackage.RelationshipsPartName(Name), Name);

    /// <summary>Whether this is a relationships part.</summary>
    public bool IsRelationships => Name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) && Name.Contains("/_rels/", StringComparison.Ordinal);

    /// <summary>The part's XML with strict namespaces mapped to transitional ones and white space kept, read safely.</summary>
    /// <exception cref="DocumentException">The part is not well-formed XML or has a DTD.</exception>
    public XDocument LoadXml()
    {
        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(Data, writable: false), package.XmlSettings);
            XDocument xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            StrictNamespaces.ToTransitional(xml, package.Limits.MaxDepth);
            return xml;
        }
        catch (XmlException e)
        {
            throw new DocumentException($"Part {Name} is not well-formed XML: {e.Message}", e);
        }
    }

    /// <summary>The relationship with <paramref name="id"/>, or null.</summary>
    public OpcRelationship? GetRelationship(string id)
    {
        foreach (OpcRelationship relationship in Relationships)
        {
            if (relationship.Id == id)
            {
                return relationship;
            }
        }

        return null;
    }
}

/// <summary>A relationship: its id, type, target as written, whether it points outside the package, and the resolved part name.</summary>
internal sealed record OpcRelationship(string Id, string Type, string Target, bool External, string TargetName);
