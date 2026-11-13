using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Documents.Odf;
using Egap.Documents.Rtf;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// The math tree of an equation island: its OMML (DOCX), RTF math or MathML (ODT, HTML) markup read once and kept beside the
/// island. Islands of other formats, and markup that does not read, give null.
/// </summary>
internal static class MathIslands
{
    private static readonly ConditionalWeakTable<Island, Entry> Trees = [];

    /// <summary>Trees a reader found outside an island's markup (an ODT formula's package document), by the markup's string.</summary>
    private static readonly ConditionalWeakTable<string, MathNode> Known = [];

    /// <summary>
    /// Records the equation of island markup whose MathML is elsewhere, such as an ODT frame that links to its formula's
    /// package document; islands with that very markup string, and their copies, read this tree.
    /// </summary>
    public static void Remember(string markup, MathNode tree) => Known.AddOrUpdate(markup, tree);

    /// <summary>The equation of <paramref name="island"/>: a <see cref="MathParagraph"/> or a <see cref="MathZone"/>; null when there is none.</summary>
    public static MathNode? Tree(Island island)
    {
        if (island.Kind != IslandKind.Math)
        {
            return null;
        }

        if (Trees.TryGetValue(island, out Entry? entry) && ReferenceEquals(entry.Markup, island.Markup))
        {
            return entry.Tree;
        }

        MathNode? tree = Known.TryGetValue(island.Markup, out MathNode? known) ? known : Read(island.Format, island.Markup);
        Trees.AddOrUpdate(island, new Entry(island.Markup, tree));
        return tree;
    }

    /// <summary>
    /// Reads equation markup of <paramref name="format"/>: OMML (docx), RTF math (rtf), or MathML inside an ODT frame (odt) or
    /// on its own (html).
    /// </summary>
    public static MathNode? Read(string format, string markup)
    {
        if (format is OdtExtension.Format or "html")
        {
            return Parse(markup) is { } element && MathMLReader.Find(element) is { } math ? MathMLReader.Read(math) : null;
        }

        XElement? omml = format switch
        {
            DocxExtension.Format => Parse(markup),
            RtfExtension.Format => RtfMath.ToOmml(markup),
            _ => null,
        };
        return omml is null ? null : Omml.Read(omml) is { } node and not MathOther ? node : null;
    }

    private static XElement? Parse(string markup)
    {
        try
        {
            return XElement.Parse(markup);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private sealed record Entry(string Markup, MathNode? Tree);
}
