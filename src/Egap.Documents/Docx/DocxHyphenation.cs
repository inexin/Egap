using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// The settings part's hyphenation elements (ECMA-376 Part 1, 17.15.1): <c>w:autoHyphenation</c>,
/// <c>w:consecutiveHyphenLimit</c>, <c>w:hyphenationZone</c> and <c>w:doNotHyphenateCaps</c>.
/// </summary>
internal static class DocxHyphenation
{
    /// <summary>Word's zone when <c>w:hyphenationZone</c> is absent: 360 twips (0.25 inch).</summary>
    public const double DefaultZone = 18;

    public static void Read(XElement root, DocumentSettings settings)
    {
        settings.AutoHyphenation = root.Element(Ns.W + "autoHyphenation") is { } auto && DocxUnits.OnOff(auto);
        if (DocxUnits.Int(root.Element(Ns.W + "consecutiveHyphenLimit").Val()) is int limit)
        {
            settings.ConsecutiveHyphenLimit = Math.Clamp(limit, 0, short.MaxValue);
        }

        if (DocxUnits.Points(root.Element(Ns.W + "hyphenationZone").Val()) is double zone && zone >= 0)
        {
            settings.HyphenationZone = zone;
        }

        settings.HyphenateCapitals = root.Element(Ns.W + "doNotHyphenateCaps") is not { } caps || !DocxUnits.OnOff(caps);
    }

    /// <summary>The <c>w:consecutiveHyphenLimit</c> element for a limit, or null for none.</summary>
    public static XElement? Limit(int limit)
    {
        return limit > 0 ? DocxPropertyWriter.Val("consecutiveHyphenLimit", limit.ToString(CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>The <c>w:hyphenationZone</c> element for a zone, or null for Word's default.</summary>
    public static XElement? Zone(double zone)
    {
        return zone == DefaultZone ? null : DocxPropertyWriter.Val("hyphenationZone", DocxUnits.Twips(zone));
    }
}
