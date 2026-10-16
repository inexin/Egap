using System.Xml.Linq;

namespace Egap.Pdf.Metadata;

/// <summary>
/// The XMP rules of PDF/A (ISO 19005-2 6.6.2): every property from a schema the XMP specification predefines, from the
/// PDF/A identification schema or from an extension schema the packet declares; predefined properties in their defined
/// form (a language alternative, an ordered or unordered array, or simple text); and no <c>bytes</c> or <c>encoding</c> in
/// the packet header. Fixing removes undeclared properties, wraps or joins values into their form and rewrites the header.
/// </summary>
internal static class XmpPdfA
{
    private static readonly XNamespace Rdf = XmpPacket.Rdf;
    private static readonly XNamespace Extension = "http://www.aiim.org/pdfa/ns/extension/";
    private static readonly XNamespace Schema = "http://www.aiim.org/pdfa/ns/schema#";
    private static readonly XNamespace Property = "http://www.aiim.org/pdfa/ns/property#";

    /// <summary>
    /// The predefined properties by namespace, from the XMP specification's schemas, each with its form: <c>a</c> a language
    /// alternative, <c>s</c> an ordered array, <c>b</c> an unordered array, <c>t</c> simple text, <c>*</c> anything (a
    /// structure). A namespace mapped to null predefines every name (camera and image schemas, rare in PDFs).
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, char>?> Predefined = new(StringComparer.Ordinal)
    {
        ["http://purl.org/dc/elements/1.1/"] = new()
        {
            ["contributor"] = 'b',
            ["coverage"] = 't',
            ["creator"] = 's',
            ["date"] = 's',
            ["description"] = 'a',
            ["format"] = 't',
            ["identifier"] = 't',
            ["language"] = 'b',
            ["publisher"] = 'b',
            ["relation"] = 'b',
            ["rights"] = 'a',
            ["source"] = 't',
            ["subject"] = 'b',
            ["title"] = 'a',
            ["type"] = 'b',
        },
        ["http://ns.adobe.com/xap/1.0/"] = new()
        {
            ["Advisory"] = 'b',
            ["BaseURL"] = 't',
            ["CreateDate"] = 't',
            ["CreatorTool"] = 't',
            ["Identifier"] = 'b',
            ["Label"] = 't',
            ["MetadataDate"] = 't',
            ["ModifyDate"] = 't',
            ["Nickname"] = 't',
            ["Rating"] = 't',
            ["Thumbnails"] = '*',
        },
        ["http://ns.adobe.com/xap/1.0/rights/"] = new() { ["Certificate"] = 't', ["Marked"] = 't', ["Owner"] = 'b', ["UsageTerms"] = 'a', ["WebStatement"] = 't' },
        ["http://ns.adobe.com/xap/1.0/mm/"] = new()
        {
            ["DerivedFrom"] = '*',
            ["DocumentID"] = 't',
            ["History"] = '*',
            ["InstanceID"] = 't',
            ["ManagedFrom"] = '*',
            ["Manager"] = 't',
            ["ManageTo"] = 't',
            ["ManageUI"] = 't',
            ["ManagerVariant"] = 't',
            ["RenditionClass"] = 't',
            ["RenditionParams"] = 't',
            ["VersionID"] = 't',
            ["Versions"] = '*',
            ["LastURL"] = 't',
            ["RenditionOf"] = '*',
            ["SaveID"] = 't',
        },
        ["http://ns.adobe.com/xap/1.0/bj/"] = new() { ["JobRef"] = '*' },
        ["http://ns.adobe.com/xap/1.0/t/pg/"] = new() { ["MaxPageSize"] = '*', ["NPages"] = 't', ["Fonts"] = '*', ["Colorants"] = '*', ["PlateNames"] = '*' },
        ["http://ns.adobe.com/pdf/1.3/"] = new() { ["Keywords"] = 't', ["PDFVersion"] = 't', ["Producer"] = 't' },
        ["http://ns.adobe.com/photoshop/1.0/"] = new()
        {
            ["AuthorsPosition"] = 't',
            ["CaptionWriter"] = 't',
            ["Category"] = 't',
            ["City"] = 't',
            ["Country"] = 't',
            ["Credit"] = 't',
            ["DateCreated"] = 't',
            ["Headline"] = 't',
            ["Instructions"] = 't',
            ["Source"] = 't',
            ["State"] = 't',
            ["SupplementalCategories"] = 'b',
            ["TransmissionReference"] = 't',
            ["Urgency"] = 't',
        },
        ["http://www.aiim.org/pdfa/ns/id/"] = new() { ["part"] = 't', ["conformance"] = 't', ["amd"] = 't', ["rev"] = 't', ["corr"] = 't' },
        ["http://www.aiim.org/pdfa/ns/extension/"] = new() { ["schemas"] = '*' },
        ["http://ns.adobe.com/xmp/1.0/DynamicMedia/"] = null,
        ["http://ns.adobe.com/tiff/1.0/"] = null,
        ["http://ns.adobe.com/exif/1.0/"] = null,
    };

    /// <summary>
    /// The fields a structured predefined property may hold, by property: the XMP specification's ResourceEvent,
    /// ResourceRef, Version, Job, Dimensions and Thumbnail types. A namespace with a null set allows every name in it.
    /// </summary>
    private static readonly Dictionary<string, (string Namespace, HashSet<string>? Names)[]> Fields = new(StringComparer.Ordinal)
    {
        ["History"] = [("http://ns.adobe.com/xap/1.0/sType/ResourceEvent#", ["action", "instanceID", "parameters", "softwareAgent", "when"])],
        ["DerivedFrom"] = [ResourceRef()],
        ["ManagedFrom"] = [ResourceRef()],
        ["RenditionOf"] = [ResourceRef()],
        ["Versions"] = [("http://ns.adobe.com/xap/1.0/sType/Version#", ["comments", "event", "modifyDate", "modifier", "version"]),
            ("http://ns.adobe.com/xap/1.0/sType/ResourceEvent#", ["action", "instanceID", "parameters", "softwareAgent", "when"])],
        ["JobRef"] = [("http://ns.adobe.com/xap/1.0/sType/Job#", ["name", "id", "url"])],
        ["MaxPageSize"] = [("http://ns.adobe.com/xap/1.0/sType/Dimensions#", ["w", "h", "unit"])],
        ["Thumbnails"] = [("http://ns.adobe.com/xap/1.0/g/img/", ["format", "width", "height", "image"])],
        ["Fonts"] = [("http://ns.adobe.com/xap/1.0/sType/Font#", null)],
        ["Colorants"] = [("http://ns.adobe.com/xap/1.0/g/", null)],
        ["PlateNames"] = [],
    };

    private static (string, HashSet<string>?) ResourceRef()
    {
        return ("http://ns.adobe.com/xap/1.0/sType/ResourceRef#", ["instanceID", "documentID", "versionID", "renditionClass", "renditionParams", "manager", "managerVariant", "manageTo", "manageUI"]);
    }

    /// <summary>Whether a structured property holds only the fields its type defines.</summary>
    private static bool FieldsFit(XElement element)
    {
        if (!Fields.TryGetValue(element.Name.LocalName, out (string Namespace, HashSet<string>? Names)[]? allowed))
        {
            return true;
        }

        IEnumerable<XName> names = element.Descendants().Select(e => e.Name)
            .Concat(element.DescendantsAndSelf().Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name))
            .Where(n => n.Namespace != Rdf && n.Namespace != XNamespace.Xml && n.Namespace != XNamespace.None);
        return names.All(n => allowed.Any(a => a.Namespace == n.NamespaceName && (a.Names is null || a.Names.Contains(n.LocalName))));
    }

    /// <summary>What breaks the rules, one line each; empty when nothing does.</summary>
    public static List<string> Problems(XmpPacket packet)
    {
        List<string> problems = [];
        if (HeaderProblem(packet.Document))
        {
            problems.Add("the XMP packet header has bytes or encoding attributes");
        }

        HashSet<(string, string)> declared = Declared(packet.Document);
        foreach ((XElement? element, XAttribute? attribute, XName name) in Properties(packet.Document))
        {
            switch (Judge(name, element, declared))
            {
                case 'u':
                    problems.Add($"the XMP property {Label(name)} is not predefined nor declared in an extension schema");
                    break;
                case 'f':
                    problems.Add($"the XMP property {Label(name)} is not in its defined form");
                    break;
            }

            _ = attribute;
        }

        return problems;
    }

    /// <summary>Puts every problem right: undeclared properties removed, predefined ones put in their form, the header rewritten.</summary>
    public static void Fix(XmpPacket packet)
    {
        XDocument document = packet.Document;
        if (HeaderProblem(document))
        {
            foreach (XProcessingInstruction instruction in document.Nodes().OfType<XProcessingInstruction>().Where(p => p.Target == "xpacket" && p.Data.Contains("begin", StringComparison.Ordinal)).ToList())
            {
                instruction.Data = "begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"";
            }
        }

        HashSet<(string, string)> declared = Declared(document);
        foreach ((XElement? element, XAttribute? attribute, XName name) in Properties(document).ToList())
        {
            switch (Judge(name, element, declared))
            {
                case 'u':
                    element?.Remove();
                    attribute?.Remove();
                    break;
                case 'f' when element is not null:
                    Reform(element, Predefined[name.NamespaceName]![name.LocalName]);
                    break;
                case 'f' when attribute is not null:
                    XElement property = new(name, attribute.Value);
                    attribute.Parent!.Add(property);
                    attribute.Remove();
                    Reform(property, Predefined[name.NamespaceName]![name.LocalName]);
                    break;
            }
        }
    }

    private static string Label(XName name)
    {
        return name.NamespaceName + name.LocalName;
    }

    private static bool HeaderProblem(XDocument document)
    {
        return document.Nodes().OfType<XProcessingInstruction>().Any(p => p.Target == "xpacket" && (p.Data.Contains("bytes=", StringComparison.Ordinal) || p.Data.Contains("encoding=", StringComparison.Ordinal)));
    }

    /// <summary>The top-level properties: each description's attributes (bar RDF's own and namespace declarations) and elements.</summary>
    private static IEnumerable<(XElement? Element, XAttribute? Attribute, XName Name)> Properties(XDocument document)
    {
        foreach (XElement description in document.Descendants(Rdf + "RDF").Take(1).Elements(Rdf + "Description"))
        {
            foreach (XAttribute attribute in description.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace != Rdf && attribute.Name.Namespace != XNamespace.Xml && attribute.Name.Namespace != XNamespace.None)
                {
                    yield return (null, attribute, attribute.Name);
                }
            }

            foreach (XElement element in description.Elements())
            {
                yield return (element, null, element.Name);
            }
        }
    }

    /// <summary>The properties extension schemas declare, as (namespace, name).</summary>
    private static HashSet<(string, string)> Declared(XDocument document)
    {
        HashSet<(string, string)> declared = [];
        foreach (XElement uri in document.Descendants(Schema + "namespaceURI"))
        {
            XElement schema = uri.Parent!;
            string ns = (uri.Value ?? "").Trim();
            foreach (XElement name in schema.Descendants(Property + "name"))
            {
                declared.Add((ns, name.Value.Trim()));
            }

            foreach (XAttribute name in schema.Descendants().Attributes(Property + "name"))
            {
                declared.Add((ns, name.Value.Trim()));
            }
        }

        foreach (XAttribute uri in document.Descendants().Attributes(Schema + "namespaceURI"))
        {
            foreach (XAttribute name in uri.Parent!.Descendants().Attributes(Property + "name"))
            {
                declared.Add((uri.Value.Trim(), name.Value.Trim()));
            }
        }

        return declared;
    }

    /// <summary><c>u</c> for an undeclared property, <c>f</c> for a predefined one out of form, <c>k</c> when fine.</summary>
    private static char Judge(XName name, XElement? element, HashSet<(string, string)> declared)
    {
        if (declared.Contains((name.NamespaceName, name.LocalName)))
        {
            return 'k';
        }

        if (!Predefined.TryGetValue(name.NamespaceName, out Dictionary<string, char>? properties))
        {
            return 'u';
        }

        if (properties is null)
        {
            // Later additions to the camera schemas, which the XMP specification PDF/A cites does not define.
            return name.LocalName == "NativeDigest" ? 'u' : 'k';
        }

        if (!properties.TryGetValue(name.LocalName, out char form))
        {
            return 'u';
        }

        if (form == '*')
        {
            // A structure: an element with fields or a resource, never plain text.
            bool structured = element is not null && (element.HasElements || element.Attribute(Rdf + "parseType")?.Value == "Resource" || element.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.Xml));
            return structured && FieldsFit(element!) ? 'k' : 'u';
        }

        if (element is null)
        {
            return form == 't' ? 'k' : 'f';
        }

        XElement? container = element.Elements().FirstOrDefault(e => e.Name.Namespace == Rdf && e.Name.LocalName is "Alt" or "Seq" or "Bag");
        return form switch
        {
            'a' => container?.Name.LocalName == "Alt" ? 'k' : 'f',
            's' => container?.Name.LocalName == "Seq" ? 'k' : 'f',
            'b' => container?.Name.LocalName == "Bag" ? 'k' : 'f',
            _ => container is null && !element.HasElements ? 'k' : 'f',
        };
    }

    /// <summary>Puts a predefined property's value in its form, keeping the text.</summary>
    private static void Reform(XElement element, char form)
    {
        List<string> items = [.. element.Descendants(Rdf + "li").Select(li => li.Value)];
        string text = items.Count > 0 ? string.Join("; ", items) : element.Value.Trim();
        element.RemoveNodes();
        element.Attributes().Where(a => !a.IsNamespaceDeclaration).Remove();
        switch (form)
        {
            case 'a':
                element.Add(new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), text)));
                break;
            case 's' or 'b':
                element.Add(new XElement(Rdf + (form == 's' ? "Seq" : "Bag"), (items.Count > 0 ? items : [text]).Select(i => new XElement(Rdf + "li", i))));
                break;
            default:
                element.Value = text;
                break;
        }
    }
}
