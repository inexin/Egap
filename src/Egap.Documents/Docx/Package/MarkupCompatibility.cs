using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// Markup compatibility (ECMA-376 Part 3) as the DOCX reader needs it: an mc:AlternateContent picks its first mc:Choice whose
/// required namespaces the reader understands, else its mc:Fallback (Phase 6 design Q9). The element as a whole is kept as
/// extension data by the caller, so the writer puts every branch back.
/// </summary>
internal static class MarkupCompatibility
{
    public static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>The children of the branch to read, or none when no branch can be read.</summary>
    public static IEnumerable<XElement> Choose(XElement alternateContent, Func<string, bool> understands)
    {
        foreach (XElement branch in alternateContent.Elements())
        {
            if (branch.Name == Mc + "Choice")
            {
                string requires = (string?)branch.Attribute("Requires") ?? "";
                bool ok = requires.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .All(prefix => branch.GetNamespaceOfPrefix(prefix) is { } ns && understands(ns.NamespaceName));
                if (ok)
                {
                    return branch.Elements();
                }
            }
            else if (branch.Name == Mc + "Fallback")
            {
                return branch.Elements();
            }
        }

        return [];
    }

    /// <summary>Whether an element is an mc:AlternateContent.</summary>
    public static bool IsAlternateContent(XElement element)
    {
        return element.Name == Mc + "AlternateContent";
    }
}
