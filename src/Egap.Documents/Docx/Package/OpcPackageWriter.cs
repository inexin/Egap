using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// Writes an OPC package: [Content_Types].xml first (defaults for rels, xml and media extensions, an override for each other
/// part), then the package relationships and every part with its relationships, in the order added.
/// </summary>
internal sealed class OpcPackageWriter
{
    private static readonly Dictionary<string, string> MediaDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpeg"] = "image/jpeg",
        ["jpg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["tif"] = "image/tiff",
        ["tiff"] = "image/tiff",
        ["emf"] = "image/x-emf",
        ["wmf"] = "image/x-wmf",
        ["svg"] = "image/svg+xml",
        ["webp"] = "image/webp",
        ["odttf"] = "application/vnd.openxmlformats-officedocument.obfuscatedFont",
        ["bin"] = "application/vnd.ms-office.vbaProject",
    };

    private readonly List<(string Name, string ContentType, byte[] Data)> parts = [];
    private readonly Dictionary<string, List<OpcRelationship>> relationships = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a part; its name must be unique.</summary>
    public void AddPart(string name, string contentType, byte[] data)
    {
        parts.Add((OpcPackage.NormalizeName(name), contentType, data));
    }

    /// <summary>Adds an XML part, written as UTF-8 with a standalone declaration as Word does.</summary>
    public void AddXmlPart(string name, string contentType, XDocument xml)
    {
        AddPart(name, contentType, Serialize(xml));
    }

    /// <summary>Whether a part of this name was added.</summary>
    public bool HasPart(string name)
    {
        string normalized = OpcPackage.NormalizeName(name);
        return parts.Exists(p => string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a relationship from <paramref name="sourceName"/> ("/" for the package).</summary>
    public void AddRelationship(string sourceName, string id, string type, string target, bool external = false)
    {
        if (!relationships.TryGetValue(sourceName, out List<OpcRelationship>? list))
        {
            list = [];
            relationships[sourceName] = list;
        }

        list.Add(new OpcRelationship(id, type, target, external, target));
    }

    /// <summary>A relationship id not yet used from <paramref name="sourceName"/>.</summary>
    public string NextRelationshipId(string sourceName)
    {
        HashSet<string> used = relationships.TryGetValue(sourceName, out List<OpcRelationship>? list) ? [.. list.Select(r => r.Id)] : [];
        for (int i = used.Count + 1; ; i++)
        {
            string id = "rId" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }

    public void Save(Stream stream)
    {
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);
        Write(zip, "[Content_Types].xml", Serialize(ContentTypes()));
        if (relationships.TryGetValue("/", out List<OpcRelationship>? packageRels))
        {
            Write(zip, "_rels/.rels", Serialize(Relationships(packageRels)));
        }

        foreach ((string name, _, byte[] data) in parts)
        {
            Write(zip, name[1..], data);
            if (relationships.TryGetValue(name, out List<OpcRelationship>? partRels) && partRels.Count > 0)
            {
                Write(zip, OpcPackage.RelationshipsPartName(name)[1..], Serialize(Relationships(partRels)));
            }
        }
    }

    internal static byte[] Serialize(XDocument xml)
    {
        using MemoryStream output = new();
        XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None };
        using (XmlWriter writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartDocument(standalone: true);
            xml.Root?.WriteTo(writer);
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream output = entry.Open();
        output.Write(data);
    }

    private XDocument ContentTypes()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        XElement types = new(ns + "Types");
        types.Add(new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")));
        types.Add(new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase) { "rels", "xml" };
        foreach ((string name, string contentType, _) in parts)
        {
            string extension = Path.GetExtension(name).TrimStart('.');
            if (MediaDefaults.TryGetValue(extension, out string? media) && media == contentType && extensions.Add(extension))
            {
                types.Add(new XElement(ns + "Default", new XAttribute("Extension", extension.ToLowerInvariant()), new XAttribute("ContentType", contentType)));
            }
        }

        foreach ((string name, string contentType, _) in parts)
        {
            string extension = Path.GetExtension(name).TrimStart('.');
            bool byDefault = (extension.Equals("xml", StringComparison.OrdinalIgnoreCase) && contentType == "application/xml")
                || (MediaDefaults.TryGetValue(extension, out string? media) && media == contentType);
            if (!byDefault)
            {
                types.Add(new XElement(ns + "Override", new XAttribute("PartName", name), new XAttribute("ContentType", contentType)));
            }
        }

        return new XDocument(types);
    }

    private static XDocument Relationships(List<OpcRelationship> list)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        XElement root = new(ns + "Relationships");
        foreach (OpcRelationship relationship in list)
        {
            XElement element = new(ns + "Relationship", new XAttribute("Id", relationship.Id), new XAttribute("Type", relationship.Type), new XAttribute("Target", relationship.Target));
            if (relationship.External)
            {
                element.Add(new XAttribute("TargetMode", "External"));
            }

            root.Add(element);
        }

        return new XDocument(root);
    }
}
