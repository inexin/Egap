using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Presentations.Pptx;

/// <summary>
/// A presentation's package, kept whole: every part as read, in order, and the XML of the parts the model wraps. Parts and
/// relationships can be added and removed. A save writes each part's bytes back as they were, except the XML parts the
/// model changed or made, which are written from their XML (in Strict namespaces again when the package had them).
/// </summary>
internal sealed class PptxPackage
{
    public const string ContentTypesName = "/[Content_Types].xml";
    private const string RelationshipsContentType = "application/vnd.openxmlformats-package.relationships+xml";
    private const string VbaProjectRelationship = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";
    private const string VbaProjectContentType = "application/vnd.ms-office.vbaProject";
    private static readonly XNamespace RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly List<PackagePart> order = [];
    private readonly Dictionary<string, PackagePart> parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<OpcRelationship>> relationships = new(StringComparer.OrdinalIgnoreCase);

    private PptxPackage(DocumentLimits limits)
    {
        Limits = limits;
    }

    public DocumentLimits Limits { get; }

    /// <summary>The Strict namespaces of the package, by their transitional names, for the parts it makes; empty when transitional.</summary>
    public Dictionary<string, string> StrictNames { get; } = new(StringComparer.Ordinal);

    public bool IsStrict => StrictNames.Count > 0;

    public IEnumerable<PackagePart> Parts => order;

    /// <summary>Reads a package from a stream (left open).</summary>
    /// <exception cref="DocumentException">Not a ZIP package, or a compound file.</exception>
    /// <exception cref="DocumentLimitException">Past a limit.</exception>
    public static PptxPackage Read(Stream stream, DocumentLimits limits)
    {
        if (stream.CanSeek)
        {
            long start = stream.Position;
            Span<byte> head = stackalloc byte[8];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = start;
            if (read == 8 && head.SequenceEqual((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            {
                throw new DocumentException("The file is a compound file, not a PPTX package: a PowerPoint 97-2003 file (PPT), which Egap.Presentations does not read, or one encrypted with rights management.");
            }
        }

        OpcPackage opc = OpcPackage.Read(stream, limits);
        PptxPackage package = new(limits);
        foreach (OpcPart part in opc.Parts)
        {
            package.Add(new PackagePart(part.Name, part.ContentType, part.Data));
        }

        return package;
    }

    /// <summary>An empty package with its content types part, for a presentation made in code.</summary>
    public static PptxPackage Create()
    {
        PptxPackage package = new(new DocumentLimits());
        XElement types = new(TypesNs + "Types",
            new XElement(TypesNs + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", RelationshipsContentType)),
            new XElement(TypesNs + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        package.AddXmlPart(ContentTypesName, "application/xml", new XDocument(types), register: false);
        return package;
    }

    public PackagePart? GetPart(string name) => parts.GetValueOrDefault(OpcPackage.NormalizeName(name));

    /// <summary>The XML of a part, read once and kept, so that the model's changes are what a save writes.</summary>
    /// <exception cref="DocumentException">The part is not well-formed XML.</exception>
    public XmlPart Xml(PackagePart part) => part.Xml ??= XmlPart.Load(part, this);

    /// <summary>The relationships from <paramref name="sourceName"/> ("/" for the package), targets resolved.</summary>
    public IReadOnlyList<OpcRelationship> Relationships(string sourceName)
    {
        if (relationships.TryGetValue(sourceName, out List<OpcRelationship>? cached))
        {
            return cached;
        }

        List<OpcRelationship> list = [];
        if (GetPart(OpcPackage.RelationshipsPartName(sourceName)) is { } rels)
        {
            foreach (XElement element in Xml(rels).Root.Elements())
            {
                if (element.Name.LocalName != "Relationship" || (string?)element.Attribute("Id") is not { } id
                    || (string?)element.Attribute("Type") is not { } type || (string?)element.Attribute("Target") is not { } target)
                {
                    continue;
                }

                bool external = string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
                list.Add(new OpcRelationship(id, type, target, external, external ? target : OpcPackage.ResolveTarget(sourceName, target)));
            }
        }

        relationships[sourceName] = list;
        return list;
    }

    /// <summary>The relationship with <paramref name="id"/> from <paramref name="sourceName"/>, or null.</summary>
    public OpcRelationship? Relationship(string sourceName, string id) => Relationships(sourceName).FirstOrDefault(r => r.Id == id);

    /// <summary>The part the relationship <paramref name="id"/> from <paramref name="sourceName"/> points at, or null.</summary>
    public PackagePart? Target(string sourceName, string id)
    {
        return Relationship(sourceName, id) is { External: false } relationship ? GetPart(relationship.TargetName) : null;
    }

    /// <summary>The first part <paramref name="sourceName"/> relates to with a relationship of <paramref name="type"/>, or null.</summary>
    public PackagePart? Related(string sourceName, string type)
    {
        foreach (OpcRelationship relationship in Relationships(sourceName))
        {
            if (!relationship.External && OpcRelationshipTypes.Same(relationship.Type, type) && GetPart(relationship.TargetName) is { } part)
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>Adds a relationship from <paramref name="sourceName"/> to the part <paramref name="targetName"/>; returns its id.</summary>
    public string AddRelationship(string sourceName, string type, string targetName)
    {
        string relsName = OpcPackage.RelationshipsPartName(sourceName);
        PackagePart rels = GetPart(relsName) ?? AddXmlPart(relsName, RelationshipsContentType, new XDocument(new XElement(RelationshipsNs + "Relationships")), register: false);
        XmlPart xml = Xml(rels);
        HashSet<string> used = [.. Relationships(sourceName).Select(r => r.Id)];
        string id;
        for (int i = used.Count + 1; ; i++)
        {
            id = "rId" + i.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(id))
            {
                break;
            }
        }

        string target = sourceName == "/" ? targetName[1..] : RelativeTarget(sourceName, targetName);
        xml.Root.Add(new XElement(xml.Root.Name.Namespace + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelationshipType(type)), new XAttribute("Target", target)));
        xml.MarkChanged();
        relationships.Remove(sourceName);
        return id;
    }

    /// <summary>Removes the relationship <paramref name="id"/> from <paramref name="sourceName"/>.</summary>
    public void RemoveRelationship(string sourceName, string id)
    {
        if (GetPart(OpcPackage.RelationshipsPartName(sourceName)) is not { } rels)
        {
            return;
        }

        XmlPart xml = Xml(rels);
        foreach (XElement element in xml.Root.Elements().Where(e => (string?)e.Attribute("Id") == id).ToList())
        {
            element.Remove();
            xml.MarkChanged();
        }

        relationships.Remove(sourceName);
    }

    /// <summary>Adds a part with bytes, and its content type unless the extension's default gives it.</summary>
    public PackagePart AddPart(string name, string contentType, byte[] data)
    {
        PackagePart part = new(OpcPackage.NormalizeName(name), contentType, data);
        Add(part);
        RegisterContentType(part.Name, contentType);
        return part;
    }

    /// <summary>Adds an XML part, written from its XML, in the package's Strict namespaces where it has them.</summary>
    public PackagePart AddXmlPart(string name, string contentType, XDocument document, bool register = true)
    {
        PackagePart part = new(OpcPackage.NormalizeName(name), contentType, []);
        part.Xml = XmlPart.Create(part, document, StrictNames);
        Add(part);
        if (register)
        {
            RegisterContentType(part.Name, contentType);
        }

        return part;
    }

    /// <summary>Removes a part, its relationships part and its content type override.</summary>
    public void RemovePart(string name)
    {
        string normalized = OpcPackage.NormalizeName(name);
        if (parts.Remove(normalized, out PackagePart? part))
        {
            order.Remove(part);
        }

        if (parts.Remove(OpcPackage.RelationshipsPartName(normalized), out PackagePart? relsPart))
        {
            order.Remove(relsPart);
        }

        relationships.Remove(normalized);
        if (GetPart(ContentTypesName) is { } types)
        {
            XmlPart xml = Xml(types);
            foreach (XElement element in xml.Root.Elements().Where(e => e.Name.LocalName == "Override" && string.Equals((string?)e.Attribute("PartName"), normalized, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                element.Remove();
                xml.MarkChanged();
            }
        }
    }

    /// <summary>
    /// Removes each of <paramref name="candidates"/> that no relationship in the package points at any more, and then the
    /// parts only those pointed at: a removed slide's notes page, comments and pictures no other slide shows.
    /// </summary>
    public void RemoveUnreferenced(IEnumerable<string> candidates)
    {
        Queue<string> queue = new(candidates);
        while (queue.TryDequeue(out string? name))
        {
            if (GetPart(name) is not { } part || part.Name == ContentTypesName || IsReferenced(part.Name))
            {
                continue;
            }

            foreach (OpcRelationship relationship in Relationships(part.Name).Where(r => !r.External))
            {
                queue.Enqueue(relationship.TargetName);
            }

            RemovePart(part.Name);
        }
    }

    /// <summary>Whether a relationship of any part left, or of the package, points at <paramref name="name"/>.</summary>
    public bool IsReferenced(string name)
    {
        foreach (PackagePart rels in order.ToList())
        {
            if (SourceOf(rels.Name) is { } source && (source == "/" || GetPart(source) is not null)
                && Relationships(source).Any(r => !r.External && string.Equals(r.TargetName, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The part a relationships part belongs to ("/" for the package's own), or null when the name is not one.</summary>
    private static string? SourceOf(string relsName)
    {
        int slash = relsName.LastIndexOf('/');
        if (!relsName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) || slash < 6
            || !relsName.AsSpan(0, slash).EndsWith("/_rels", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string folder = relsName[..(slash - 5)];
        string file = relsName[(slash + 1)..^5];
        return file.Length == 0 ? "/" : folder + file;
    }

    /// <summary>
    /// The first name <paramref name="prefix"/><c>n</c><paramref name="extension"/>, from 1, that no part has and no
    /// relationship points at: a new part must not take over a relationship left pointing at a missing one.
    /// </summary>
    public string UniqueName(string prefix, string extension)
    {
        for (int i = 1; ; i++)
        {
            string name = prefix + i.ToString(CultureInfo.InvariantCulture) + extension;
            if (!parts.ContainsKey(name) && !IsReferenced(name))
            {
                return name;
            }
        }
    }

    /// <summary>The relationship type in the package's spelling: Strict when the package is.</summary>
    public string RelationshipType(string type)
    {
        const string transitional = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        if (!IsStrict || !type.StartsWith(transitional, StringComparison.Ordinal))
        {
            return type;
        }

        string name = type[transitional.Length..] switch
        {
            "extended-properties" => "extendedProperties",
            "custom-properties" => "customProperties",
            var n => n,
        };
        return "http://purl.oclc.org/ooxml/officeDocument/relationships/" + name;
    }

    /// <summary>Writes every part back: unchanged ones byte for byte, changed or new XML parts from their XML.</summary>
    public void Save(Stream stream) => Save(stream, default);

    /// <summary>
    /// Writes every part back, with the main part's content type changed and the VBA project left out when
    /// <paramref name="options"/> say so; the package itself does not change.
    /// </summary>
    public void Save(Stream stream, PackageSave options)
    {
        HashSet<string> dropped = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> droppedIds = new(StringComparer.Ordinal);
        if (options.DropMacros && options.MainName is { } mainName)
        {
            Queue<string> queue = new();
            foreach (OpcRelationship relationship in Relationships(mainName))
            {
                if (!relationship.External && OpcRelationshipTypes.Same(relationship.Type, VbaProjectRelationship))
                {
                    droppedIds.Add(relationship.Id);
                    queue.Enqueue(relationship.TargetName);
                }
            }

            // The project and what only it points at (its signature, vbaData.xml).
            while (queue.TryDequeue(out string? name))
            {
                if (GetPart(name) is not { } part || !dropped.Add(part.Name))
                {
                    continue;
                }

                dropped.Add(OpcPackage.RelationshipsPartName(part.Name));
                foreach (OpcRelationship relationship in Relationships(part.Name).Where(r => !r.External))
                {
                    queue.Enqueue(relationship.TargetName);
                }
            }
        }

        string? mainRels = options.MainName is null ? null : OpcPackage.RelationshipsPartName(options.MainName);
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (PackagePart part in order)
        {
            if (dropped.Contains(part.Name))
            {
                continue;
            }

            byte[] data = part.CurrentData;
            if (part.Name == ContentTypesName && (options.MainContentType is not null || dropped.Count > 0))
            {
                XDocument types = new(Xml(part).Document);
                foreach (XElement element in types.Root!.Elements().ToList())
                {
                    string? partName = (string?)element.Attribute("PartName");
                    if (element.Name.LocalName == "Override" && partName is not null && dropped.Contains(partName))
                    {
                        element.Remove();
                    }
                    else if (element.Name.LocalName == "Override" && options.MainContentType is not null && string.Equals(partName, options.MainName, StringComparison.OrdinalIgnoreCase))
                    {
                        element.SetAttributeValue("ContentType", options.MainContentType);
                    }
                    else if (dropped.Count > 0 && element.Name.LocalName == "Default" && (string?)element.Attribute("ContentType") == VbaProjectContentType)
                    {
                        element.Remove();
                    }
                }

                data = OpcPackageWriter.Serialize(types);
            }
            else if (droppedIds.Count > 0 && string.Equals(part.Name, mainRels, StringComparison.OrdinalIgnoreCase))
            {
                XDocument rels = new(Xml(part).Document);
                rels.Root!.Elements().Where(e => (string?)e.Attribute("Id") is { } id && droppedIds.Contains(id)).Remove();
                data = OpcPackageWriter.Serialize(rels);
            }

            ZipArchiveEntry entry = zip.CreateEntry(part.Name[1..], CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream output = entry.Open();
            output.Write(data);
        }
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

    /// <summary>A part name relative to the folder of <paramref name="sourceName"/>, as relationship targets are written.</summary>
    internal static string RelativeTarget(string sourceName, string targetName)
    {
        string[] from = sourceName[1..(sourceName.LastIndexOf('/') + 1)].Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] to = targetName[1..].Split('/');
        int common = 0;
        while (common < from.Length && common < to.Length - 1 && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(to[common..]));
    }

    private void Add(PackagePart part)
    {
        if (parts.Remove(part.Name, out PackagePart? old))
        {
            order.Remove(old);
        }

        parts[part.Name] = part;
        order.Add(part);
    }

    private void RegisterContentType(string name, string contentType)
    {
        if (GetPart(ContentTypesName) is not { } types)
        {
            return;
        }

        XmlPart xml = Xml(types);
        int dot = name.LastIndexOf('.');
        string extension = dot > name.LastIndexOf('/') ? name[(dot + 1)..] : "";
        XElement? byExtension = xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Default"
            && string.Equals((string?)e.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase));
        if ((string?)byExtension?.Attribute("ContentType") == contentType)
        {
            return;
        }

        if (byExtension is null && extension.Length > 0 && contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            // Media go by their extension, as PowerPoint writes them.
            XElement made = new(xml.Root.Name.Namespace + "Default", new XAttribute("Extension", extension.ToLowerInvariant()), new XAttribute("ContentType", contentType));
            if (xml.Root.Elements().LastOrDefault(e => e.Name.LocalName == "Default") is { } last)
            {
                last.AddAfterSelf(made);
            }
            else
            {
                xml.Root.AddFirst(made);
            }
        }
        else
        {
            xml.Root.Add(new XElement(xml.Root.Name.Namespace + "Override", new XAttribute("PartName", name), new XAttribute("ContentType", contentType)));
        }

        xml.MarkChanged();
    }
}

/// <summary>How a save writes the package out: the main part's name and new content type (null keeps it), and whether the VBA project goes.</summary>
internal readonly record struct PackageSave(string? MainName, string? MainContentType, bool DropMacros);

/// <summary>One part of the package: its name, content type and bytes as read, and its XML once the model reads it.</summary>
internal sealed class PackagePart(string name, string contentType, byte[] data)
{
    public string Name { get; } = name;

    public string ContentType { get; } = contentType;

    /// <summary>The bytes as read; empty for a part made in code, which is written from its XML.</summary>
    public byte[] Data { get; } = data;

    public XmlPart? Xml { get; set; }

    /// <summary>The bytes a save writes now.</summary>
    public byte[] CurrentData => Xml is { Changed: true } xml ? xml.Serialize() : Data;
}

/// <summary>
/// A part's XML, with Strict namespaces mapped to transitional ones for the model, the Strict ones it had remembered so that
/// a changed part is written in the namespaces it was read in.
/// </summary>
internal sealed class XmlPart
{
    private readonly Dictionary<string, string> strict;

    private XmlPart(PackagePart part, XDocument document, Dictionary<string, string> strict, bool changed)
    {
        Part = part;
        Document = document;
        this.strict = strict;
        Changed = changed;
    }

    public PackagePart Part { get; }

    public XDocument Document { get; }

    public XElement Root => Document.Root!;

    /// <summary>Whether the model changed the XML since it was read, or made it.</summary>
    public bool Changed { get; private set; }

    /// <summary>Whether the part was written in ISO/IEC 29500 Strict namespaces.</summary>
    public bool IsStrict => strict.Count > 0;

    /// <summary>Marks the part as changed, so a save writes it from its XML.</summary>
    public void MarkChanged() => Changed = true;

    /// <summary>A part made in code, written in the Strict namespaces given (none for transitional).</summary>
    public static XmlPart Create(PackagePart part, XDocument document, IReadOnlyDictionary<string, string> strict) => new(part, document, new Dictionary<string, string>(strict), true);

    /// <exception cref="DocumentException">The part is not well-formed XML or has a DTD.</exception>
    public static XmlPart Load(PackagePart part, PptxPackage package)
    {
        XDocument document;
        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(part.Data, writable: false), package.XmlSettings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is XmlException or ArgumentOutOfRangeException)
        {
            throw new DocumentException($"Part {part.Name} is not well-formed XML: {e.Message}", e);
        }

        if (document.Root is null)
        {
            throw new DocumentException($"Part {part.Name} has no root element.");
        }

        // The transitional name of each Strict namespace the part uses, to map them back on a save.
        Dictionary<string, string> strict = new(StringComparer.Ordinal);
        foreach (XElement element in document.Root.DescendantsAndSelf())
        {
            Note(element.Name.NamespaceName);
            foreach (XAttribute attribute in element.Attributes())
            {
                Note(attribute.IsNamespaceDeclaration ? attribute.Value : attribute.Name.NamespaceName);
            }
        }

        StrictNamespaces.ToTransitional(document, package.Limits.MaxDepth);
        foreach ((string transitional, string ns) in strict)
        {
            package.StrictNames.TryAdd(transitional, ns);
        }

        return new XmlPart(part, document, strict, false);

        void Note(string ns)
        {
            string transitional = StrictNamespaces.ToTransitional(ns);
            if (transitional != ns)
            {
                strict.TryAdd(transitional, ns);
            }
        }
    }

    /// <summary>The XML as a part's bytes, in Strict namespaces where the part had them.</summary>
    public byte[] Serialize()
    {
        if (!IsStrict)
        {
            return OpcPackageWriter.Serialize(new XDocument(Document));
        }

        XDocument copy = new(Document);
        foreach (XElement element in copy.Root!.DescendantsAndSelf())
        {
            if (strict.TryGetValue(element.Name.NamespaceName, out string? ns))
            {
                element.Name = XName.Get(element.Name.LocalName, ns);
            }

            if (!element.HasAttributes)
            {
                continue;
            }

            List<XAttribute> attributes = [.. element.Attributes()];
            bool renamed = false;
            for (int i = 0; i < attributes.Count; i++)
            {
                XAttribute attribute = attributes[i];
                if (attribute.IsNamespaceDeclaration && strict.TryGetValue(attribute.Value, out string? declared))
                {
                    attributes[i] = new XAttribute(attribute.Name, declared);
                    renamed = true;
                }
                else if (!attribute.IsNamespaceDeclaration && strict.TryGetValue(attribute.Name.NamespaceName, out string? named))
                {
                    attributes[i] = new XAttribute(XName.Get(attribute.Name.LocalName, named), attribute.Value);
                    renamed = true;
                }
            }

            if (renamed)
            {
                element.ReplaceAttributes(attributes);
            }
        }

        return OpcPackageWriter.Serialize(copy);
    }
}
