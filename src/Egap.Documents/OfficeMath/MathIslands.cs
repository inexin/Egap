using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Documents.Rtf;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// The math tree of an equation island: its OMML (DOCX) or RTF math markup read once and kept beside the island. Islands of
/// other formats, and markup that does not read, give null.
/// </summary>
internal static class MathIslands
{
    private static readonly ConditionalWeakTable<Island, Entry> Trees = [];

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

        MathNode? tree = Read(island.Format, island.Markup);
        Trees.AddOrUpdate(island, new Entry(island.Markup, tree));
        return tree;
    }

    /// <summary>Reads equation markup of <paramref name="format"/> (docx or rtf).</summary>
    public static MathNode? Read(string format, string markup)
    {
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
