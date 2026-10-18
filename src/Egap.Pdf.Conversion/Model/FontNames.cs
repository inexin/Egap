using System.Text;

namespace Egap.Pdf.Conversion;

/// <summary>
/// PDF font names to family names an editor can find (design Q6): the standard 14 to the fonts Word and browsers have with
/// the same metrics, and run-together PostScript names spelled out ("TimesNewRoman" gives "Times New Roman").
/// </summary>
internal static class FontNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial",
        ["Arial"] = "Arial",
        ["ArialNarrow"] = "Arial Narrow",
        ["Times"] = "Times New Roman",
        ["TimesRoman"] = "Times New Roman",
        ["Times-Roman"] = "Times New Roman",
        ["TimesNewRoman"] = "Times New Roman",
        ["Courier"] = "Courier New",
        ["CourierNew"] = "Courier New",
        ["Symbol"] = "Symbol",
        ["ZapfDingbats"] = "Segoe UI Symbol",
        ["Wingdings"] = "Wingdings",
    };

    /// <summary>The family to name in the document for a cleaned PDF family (<see cref="FontTraits.Family"/>).</summary>
    public static string Map(string family)
    {
        if (family.Any(c => char.IsControl(c) || char.IsSurrogate(c)))
        {
            family = string.Concat(family.Where(c => !char.IsControl(c) && !char.IsSurrogate(c)));
        }

        if (family.Length == 0)
        {
            return "";
        }

        if (Known.TryGetValue(family, out string? known))
        {
            return known;
        }

        if (family.Contains(' ', StringComparison.Ordinal))
        {
            return family;
        }

        // Spaces where a capital starts a word: "SegoeUI" gives "Segoe UI", "MSGothic" gives "MS Gothic".
        StringBuilder spelled = new(family.Length + 4);
        for (int i = 0; i < family.Length; i++)
        {
            char c = family[i];
            bool wordStart = i > 0 && char.IsUpper(c)
                && (char.IsLower(family[i - 1]) || (char.IsUpper(family[i - 1]) && i + 1 < family.Length && char.IsLower(family[i + 1])));
            if (wordStart)
            {
                spelled.Append(' ');
            }

            spelled.Append(c);
        }

        return spelled.ToString();
    }
}
