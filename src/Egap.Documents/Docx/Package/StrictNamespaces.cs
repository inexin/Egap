using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// ISO/IEC 29500 Strict names its namespaces and relationship types under purl.oclc.org; the reader maps them to the
/// transitional names on load (Phase 6 design Q10), so one reader handles both. It also checks the nesting depth.
/// </summary>
internal static class StrictNamespaces
{
    private const string StrictBase = "http://purl.oclc.org/ooxml/";
    private const string TransitionalBase = "http://schemas.openxmlformats.org/";

    /// <summary>The transitional namespace for a strict one; other namespaces as they are.</summary>
    public static string ToTransitional(string ns)
    {
        if (!ns.StartsWith(StrictBase, StringComparison.Ordinal))
        {
            return ns;
        }

        // purl.oclc.org/ooxml/<area>/<name> is schemas.openxmlformats.org/<area>/2006/<name>, with two names spelled apart.
        string rest = ns[StrictBase.Length..];
        int slash = rest.IndexOf('/');
        if (slash < 0)
        {
            return ns;
        }

        string area = rest[..slash], name = rest[(slash + 1)..];
        name = name switch
        {
            "extendedProperties" => "extended-properties",
            "customProperties" => "custom-properties",
            _ => name,
        };
        return TransitionalBase + area + "/2006/" + name;
    }

    /// <summary>Renames strict namespaces in place and checks the depth, without recursion.</summary>
    /// <exception cref="DocumentLimitException">Elements nest deeper than four times <paramref name="maxDepth"/>.</exception>
    public static void ToTransitional(XDocument xml, int maxDepth)
    {
        if (xml.Root is not { } root)
        {
            return;
        }

        int limit = maxDepth * 4;
        Stack<(XElement Element, int Depth)> pending = new();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            (XElement element, int depth) = pending.Pop();
            if (depth > limit)
            {
                throw new DocumentLimitException($"XML elements nest more than {limit} deep.");
            }

            string ns = element.Name.NamespaceName;
            if (ns.StartsWith(StrictBase, StringComparison.Ordinal))
            {
                element.Name = XName.Get(element.Name.LocalName, ToTransitional(ns));
            }

            if (element.HasAttributes)
            {
                List<XAttribute>? renamed = null;
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.Name.NamespaceName.StartsWith(StrictBase, StringComparison.Ordinal)
                        || (attribute.IsNamespaceDeclaration && attribute.Value.StartsWith(StrictBase, StringComparison.Ordinal)))
                    {
                        (renamed ??= []).Add(attribute);
                    }
                }

                foreach (XAttribute attribute in renamed ?? [])
                {
                    attribute.Remove();
                    XName name = attribute.IsNamespaceDeclaration ? attribute.Name : XName.Get(attribute.Name.LocalName, ToTransitional(attribute.Name.NamespaceName));
                    element.Add(new XAttribute(name, attribute.IsNamespaceDeclaration ? ToTransitional(attribute.Value) : attribute.Value));
                }
            }

            foreach (XElement child in element.Elements())
            {
                pending.Push((child, depth + 1));
            }
        }
    }
}

/// <summary>Relationship types DOCX uses, in their transitional spelling; strict ones compare equal through <see cref="Same"/>.</summary>
internal static class OpcRelationshipTypes
{
    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    public const string OfficeDocument = Office + "officeDocument";
    public const string Styles = Office + "styles";
    public const string Numbering = Office + "numbering";
    public const string Settings = Office + "settings";
    public const string FontTable = Office + "fontTable";
    public const string Theme = Office + "theme";
    public const string Footnotes = Office + "footnotes";
    public const string Endnotes = Office + "endnotes";
    public const string Comments = Office + "comments";
    public const string Header = Office + "header";
    public const string Footer = Office + "footer";
    public const string Image = Office + "image";
    public const string Hyperlink = Office + "hyperlink";
    public const string Font = Office + "font";
    public const string WebSettings = Office + "webSettings";
    public const string CustomXml = Office + "customXml";
    public const string ExtendedProperties = Office + "extended-properties";
    public const string CustomProperties = Office + "custom-properties";
    public const string GlossaryDocument = Office + "glossaryDocument";
    public const string CoreProperties = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string VbaProject = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";
    public const string CommentsExtended = "http://schemas.microsoft.com/office/2011/relationships/commentsExtended";
    public const string CommentsIds = "http://schemas.microsoft.com/office/2016/09/relationships/commentsIds";
    public const string People = "http://schemas.microsoft.com/office/2011/relationships/people";

    /// <summary>Whether two relationship types are the same, strict and transitional spellings alike.</summary>
    public static bool Same(string a, string b)
    {
        return string.Equals(Transitional(a), Transitional(b), StringComparison.Ordinal);
    }

    /// <summary>The transitional spelling of a relationship type.</summary>
    public static string Transitional(string type)
    {
        const string strict = "http://purl.oclc.org/ooxml/officeDocument/relationships/";
        if (type.StartsWith(strict, StringComparison.Ordinal))
        {
            string name = type[strict.Length..] switch
            {
                "extendedProperties" => "extended-properties",
                "customProperties" => "custom-properties",
                var n => n,
            };
            return Office + name;
        }

        return type;
    }
}
