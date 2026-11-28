using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Presentations.Pptx;

/// <summary>
/// A loaded presentation's package, kept whole: every part as read, and the XML of the parts the model wraps. A save writes
/// each part's bytes back as they were, in the order read, except the XML parts the model changed, which are written from
/// their XML (in Strict namespaces again when the part had them).
/// </summary>
internal sealed class PptxPackage
{
    private readonly Dictionary<string, XmlPart> xml = new(StringComparer.OrdinalIgnoreCase);

    private PptxPackage(OpcPackage package)
    {
        Opc = package;
    }

    public OpcPackage Opc { get; }

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
                throw new DocumentException("The file is a compound file, not a PPTX package: an encrypted presentation or a PowerPoint 97-2003 file (PPT), which Egap.Presentations does not read.");
            }
        }

        return new PptxPackage(OpcPackage.Read(stream, limits));
    }

    /// <summary>The XML of a part, read once and kept, so that the model's changes are what a save writes.</summary>
    /// <exception cref="DocumentException">The part is not well-formed XML.</exception>
    public XmlPart Xml(OpcPart part)
    {
        if (!xml.TryGetValue(part.Name, out XmlPart? loaded))
        {
            loaded = XmlPart.Load(part, Opc);
            xml[part.Name] = loaded;
        }

        return loaded;
    }

    /// <summary>Writes every part back: unchanged ones byte for byte, changed XML parts from their XML.</summary>
    public void Save(Stream stream)
    {
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (OpcPart part in Opc.Parts)
        {
            byte[] data = xml.TryGetValue(part.Name, out XmlPart? changed) && changed.Changed ? changed.Serialize() : part.Data;
            ZipArchiveEntry entry = zip.CreateEntry(part.Name[1..], CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream output = entry.Open();
            output.Write(data);
        }
    }
}

/// <summary>
/// A part's XML, with Strict namespaces mapped to transitional ones for the model, the Strict ones it had remembered so that
/// a changed part is written in the namespaces it was read in.
/// </summary>
internal sealed class XmlPart
{
    private readonly Dictionary<string, string> strict;

    private XmlPart(OpcPart part, XDocument document, Dictionary<string, string> strict)
    {
        Part = part;
        Document = document;
        this.strict = strict;
    }

    public OpcPart Part { get; }

    public XDocument Document { get; }

    public XElement Root => Document.Root!;

    /// <summary>Whether the model changed the XML since it was read.</summary>
    public bool Changed { get; private set; }

    /// <summary>Whether the part was written in ISO/IEC 29500 Strict namespaces.</summary>
    public bool IsStrict => strict.Count > 0;

    /// <summary>Marks the part as changed, so a save writes it from its XML.</summary>
    public void MarkChanged() => Changed = true;

    /// <exception cref="DocumentException">The part is not well-formed XML or has a DTD.</exception>
    public static XmlPart Load(OpcPart part, OpcPackage package)
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
        return new XmlPart(part, document, strict);

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
