using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Pdf.Metadata;

/// <summary>
/// An XMP packet (XMP part 1 and 2) read with System.Xml: the properties the PDF document information maps to are read
/// and written, and everything else, unknown namespaces included, is kept as it is. A packet is written back only when a
/// property changed; then the padding before the trailer is resized so the packet keeps its length when it can.
/// </summary>
internal sealed class XmpPacket
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    public static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    public static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    public static readonly XNamespace PdfUAId = "http://www.aiim.org/pdfua/ns/id/";
    public static readonly XNamespace Extension = "http://www.aiim.org/pdfa/ns/extension/";
    public static readonly XNamespace Schema = "http://www.aiim.org/pdfa/ns/schema#";
    public static readonly XNamespace Property = "http://www.aiim.org/pdfa/ns/property#";
    private static readonly XNamespace XmpMeta = "adobe:ns:meta/";
    private static readonly XNamespace Xml = XNamespace.Xml;

    /// <summary>The most bytes an XMP packet is read from, against packets built to exhaust memory.</summary>
    private const int MaxBytes = 16 << 20;

    private XmpPacket(XDocument document, int length)
    {
        Document = document;
        Length = length;
    }

    /// <summary>The parsed packet; changed in place by the setters.</summary>
    public XDocument Document { get; }

    /// <summary>The length of the packet as read, which a rewrite keeps when it can.</summary>
    public int Length { get; }

    /// <summary>Parses a packet; null when it is not well-formed XML or holds no <c>rdf:RDF</c>.</summary>
    public static XmpPacket? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            return null;
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
            IgnoreWhitespace = false,
        };
        try
        {
            using MemoryStream stream = new(bytes.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Descendants(Rdf + "RDF").Any() ? new XmpPacket(document, bytes.Length) : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A new packet with an empty description, as Acrobat writes one.</summary>
    public static XmpPacket Create()
    {
        XDocument document = new(
            new XProcessingInstruction("xpacket", "begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\""),
            new XElement(
                XmpMeta + "xmpmeta",
                new XAttribute(XNamespace.Xmlns + "x", XmpMeta.NamespaceName),
                new XElement(
                    Rdf + "RDF",
                    new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                    new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))),
            new XProcessingInstruction("xpacket", "end=\"w\""));
        return new XmpPacket(document, 0);
    }

    /// <summary>The packet as UTF-8, padded with spaces before the trailer to its old length, or 2 KB when it grew.</summary>
    public byte[] ToBytes()
    {
        // The old padding is whitespace between the root element and the trailer; it is replaced, not kept.
        XDocument copy = new(Document);
        copy.Nodes().OfType<XText>().Remove();
        string xml = copy.ToString(SaveOptions.DisableFormatting);
        int end = xml.LastIndexOf("<?xpacket end", StringComparison.Ordinal);
        byte[] body = Encoding.UTF8.GetBytes(end < 0 ? xml : xml[..end]);
        byte[] trailer = end < 0 ? [] : Encoding.UTF8.GetBytes(xml[end..]);
        int padding = end < 0 ? 0 : Length - body.Length - trailer.Length;
        if (padding < 1)
        {
            padding = end < 0 ? 0 : 2048;
        }

        byte[] bytes = new byte[body.Length + padding + trailer.Length];
        body.CopyTo(bytes, 0);
        for (int i = 0; i < padding; i++)
        {
            bytes[body.Length + i] = (byte)((i + 1) % 100 == 0 ? '\n' : ' ');
        }

        trailer.CopyTo(bytes, body.Length + padding);
        return bytes;
    }

    /// <summary>A simple property: an attribute of a description, or an element's text.</summary>
    public string? GetText(XName name)
    {
        foreach (XElement description in Descriptions())
        {
            if (description.Attribute(name) is XAttribute attribute)
            {
                return attribute.Value;
            }

            if (description.Element(name) is XElement element)
            {
                return Items(element) is { Count: > 0 } items ? items[0] : element.Value.Trim();
            }
        }

        return null;
    }

    /// <summary>A language alternative's default (<c>x-default</c>, else the first item), or simple text.</summary>
    public string? GetAlternative(XName name)
    {
        foreach (XElement description in Descriptions())
        {
            if (description.Attribute(name) is XAttribute attribute)
            {
                return attribute.Value;
            }

            if (description.Element(name) is XElement element)
            {
                XElement? alternative = element.Element(Rdf + "Alt");
                if (alternative is null)
                {
                    return element.Value.Trim();
                }

                XElement? chosen = alternative.Elements(Rdf + "li").FirstOrDefault(li => (string?)li.Attribute(Xml + "lang") == "x-default")
                    ?? alternative.Elements(Rdf + "li").FirstOrDefault();
                return chosen?.Value;
            }
        }

        return null;
    }

    /// <summary>An ordered or unordered array's items joined with <c>"; "</c>, or simple text.</summary>
    public string? GetList(XName name)
    {
        foreach (XElement description in Descriptions())
        {
            if (description.Attribute(name) is XAttribute attribute)
            {
                return attribute.Value;
            }

            if (description.Element(name) is XElement element)
            {
                List<string> items = Items(element);
                return items.Count > 0 ? string.Join("; ", items) : element.Value.Trim();
            }
        }

        return null;
    }

    /// <summary>Sets a simple property, or removes it when <paramref name="value"/> is null.</summary>
    public void SetText(XName name, string? value)
    {
        value = Clean(value);
        Set(name, value is null ? null : new XElement(name, value));
    }

    /// <summary>The text without the characters XML cannot hold (controls such as NUL), which Info strings may carry.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    public static string? Clean(string? value)
    {
        if (value is null || value.All(XmlConvert.IsXmlChar))
        {
            return value;
        }

        StringBuilder clean = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i]))
            {
                clean.Append(value[i]);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], value[i]))
            {
                clean.Append(value, i, 2);
                i++;
            }
        }

        return clean.ToString();
    }

    /// <summary>
    /// Declares a property in a PDF/A extension schema (ISO 19005-1 6.7.8), so PDF/A accepts it beside the predefined
    /// schemas; nothing changes when the schema already declares it.
    /// </summary>
    public void DeclareExtension(XNamespace ns, string prefix, string schemaName, string property, string valueType, string description)
    {
        XElement target = Descriptions().FirstOrDefault() ?? NewDescription();
        foreach ((string p, XNamespace n) in (ReadOnlySpan<(string, XNamespace)>)[("pdfaExtension", Extension), ("pdfaSchema", Schema), ("pdfaProperty", Property)])
        {
            if (target.GetPrefixOfNamespace(n) is null)
            {
                target.SetAttributeValue(XNamespace.Xmlns + p, n.NamespaceName);
            }
        }

        XElement schemas = Document.Descendants(Extension + "schemas").FirstOrDefault() ?? Add(target, new XElement(Extension + "schemas", new XElement(Rdf + "Bag")));
        XElement bag = schemas.Element(Rdf + "Bag") ?? Add(schemas, new XElement(Rdf + "Bag"));
        if (bag.Descendants(Schema + "namespaceURI").Any(u => u.Value.Trim() == ns.NamespaceName))
        {
            return;
        }

        bag.Add(new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"),
            new XElement(Schema + "schema", schemaName),
            new XElement(Schema + "namespaceURI", ns.NamespaceName),
            new XElement(Schema + "prefix", prefix),
            new XElement(Schema + "property", new XElement(Rdf + "Seq", new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"),
                new XElement(Property + "name", property),
                new XElement(Property + "valueType", valueType),
                new XElement(Property + "category", "internal"),
                new XElement(Property + "description", description))))));

        static XElement Add(XElement parent, XElement child)
        {
            parent.Add(child);
            return child;
        }
    }

    /// <summary>Sets a language alternative with one <c>x-default</c> item, or removes it.</summary>
    public void SetAlternative(XName name, string? value)
    {
        value = Clean(value);
        Set(name, value is null ? null : new XElement(name, new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(Xml + "lang", "x-default"), value))));
    }

    /// <summary>Sets an ordered array with one item, or removes it.</summary>
    public void SetSequence(XName name, string? value)
    {
        value = Clean(value);
        Set(name, value is null ? null : new XElement(name, new XElement(Rdf + "Seq", new XElement(Rdf + "li", value))));
    }

    /// <summary>Replaces a property in every description with <paramref name="element"/>, put in the first description.</summary>
    private void Set(XName name, XElement? element)
    {
        foreach (XElement description in Descriptions().ToList())
        {
            description.Attribute(name)?.Remove();
            description.Elements(name).Remove();
        }

        if (element is null)
        {
            return;
        }

        XElement target = Descriptions().FirstOrDefault() ?? NewDescription();
        if (target.GetPrefixOfNamespace(name.Namespace) is null)
        {
            string prefix = name.Namespace == Dc ? "dc" : name.Namespace == Xmp ? "xmp" : name.Namespace == Pdf ? "pdf" : name.Namespace == PdfAId ? "pdfaid" : name.Namespace == PdfUAId ? "pdfuaid" : name.Namespace == Extension ? "pdfaExtension" : "ns" + target.Attributes().Count();
            target.SetAttributeValue(XNamespace.Xmlns + prefix, name.Namespace.NamespaceName);
        }

        target.Add(element);
    }

    private XElement NewDescription()
    {
        XElement rdf = Document.Descendants(Rdf + "RDF").First();
        XElement description = new(Rdf + "Description", new XAttribute(Rdf + "about", ""));
        rdf.Add(description);
        return description;
    }

    private IEnumerable<XElement> Descriptions()
    {
        return Document.Descendants(Rdf + "RDF").Take(1).Elements(Rdf + "Description");
    }

    private static List<string> Items(XElement element)
    {
        return [.. element.Elements().Where(e => e.Name == Rdf + "Seq" || e.Name == Rdf + "Bag" || e.Name == Rdf + "Alt").Elements(Rdf + "li").Select(li => li.Value)];
    }
}
