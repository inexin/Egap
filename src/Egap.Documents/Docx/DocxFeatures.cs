using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// The OpenType features Word 2010 and later keep in a run's properties (<c>w14:ligatures</c>, <c>w14:numForm</c>,
/// <c>w14:numSpacing</c>, <c>w14:stylisticSets</c>, <c>w14:cntxtAlts</c>), read into and written from
/// <see cref="RunFormat.Features"/>. Features Word has no element for are not written.
/// </summary>
internal static class DocxFeatures
{
    /// <summary>Whether the element is one of the feature elements this class reads.</summary>
    public static bool IsFeature(XElement e)
    {
        return e.Name.Namespace == Ns.W14 && e.Name.LocalName is "ligatures" or "numForm" or "numSpacing" or "stylisticSets" or "cntxtAlts";
    }

    /// <summary>Adds a feature element's features to the run's.</summary>
    public static void Read(XElement e, RunFormat format)
    {
        string value = (string?)e.Attribute(Ns.W14 + "val") ?? "";
        List<(string, int)> features = [];
        switch (e.Name.LocalName)
        {
            case "ligatures":
                bool all = value == "all";
                features.Add(("liga", all || value.Contains("standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                features.Add(("clig", all || value.Contains("ontextual", StringComparison.Ordinal) ? 1 : 0));
                if (all || value.Contains("istorical", StringComparison.Ordinal))
                {
                    features.Add(("hlig", 1));
                }

                if (all || value.Contains("iscretional", StringComparison.Ordinal))
                {
                    features.Add(("dlig", 1));
                }

                break;
            case "numForm" when value is "lining":
                features.Add(("lnum", 1));
                break;
            case "numForm" when value is "oldStyle":
                features.Add(("onum", 1));
                break;
            case "numSpacing" when value is "proportional":
                features.Add(("pnum", 1));
                break;
            case "numSpacing" when value is "tabular":
                features.Add(("tnum", 1));
                break;
            case "cntxtAlts":
                features.Add(("calt", value is "0" or "false" ? 0 : 1));
                break;
            case "stylisticSets":
                foreach (XElement set in e.Elements(Ns.W14 + "styleSet"))
                {
                    if (int.TryParse((string?)set.Attribute(Ns.W14 + "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id is >= 1 and <= 20)
                    {
                        features.Add(($"ss{id:00}", (string?)set.Attribute(Ns.W14 + "val") is "0" or "false" ? 0 : 1));
                    }
                }

                break;
        }

        if (features.Count > 0)
        {
            format.Features = (format.Features ?? FontFeatures.None).With(FontFeatures.Of(features));
        }
    }

    /// <summary>The feature elements for the run's features, in schema order.</summary>
    public static void Write(FontFeatures? features, List<XElement> c)
    {
        if (features is not { Count: > 0 })
        {
            return;
        }

        bool On(string tag) => features.ValueOf(tag.PadRight(4)) is > 0;
        bool Named(string tag) => features.ValueOf(tag.PadRight(4)) is not null;
        if (Named("liga") || Named("clig") || Named("hlig") || Named("dlig"))
        {
            string[] parts = [.. new[] { ("standard", "liga"), ("contextual", "clig"), ("historical", "hlig"), ("discretional", "dlig") }.Where(p => On(p.Item2)).Select(p => p.Item1)];
            string value = parts.Length switch
            {
                0 => "none",
                4 => "all",
                _ => parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..])),
            };
            c.Add(new XElement(Ns.W14 + "ligatures", new XAttribute(Ns.W14 + "val", value)));
        }

        if (On("lnum") || On("onum"))
        {
            c.Add(new XElement(Ns.W14 + "numForm", new XAttribute(Ns.W14 + "val", On("onum") ? "oldStyle" : "lining")));
        }

        if (On("pnum") || On("tnum"))
        {
            c.Add(new XElement(Ns.W14 + "numSpacing", new XAttribute(Ns.W14 + "val", On("tnum") ? "tabular" : "proportional")));
        }

        List<XElement> sets = [];
        for (int id = 1; id <= 20; id++)
        {
            if (features.ValueOf($"ss{id:00}") is int set)
            {
                sets.Add(new XElement(Ns.W14 + "styleSet", new XAttribute(Ns.W14 + "id", id), new XAttribute(Ns.W14 + "val", set > 0 ? "1" : "0")));
            }
        }

        if (sets.Count > 0)
        {
            c.Add(new XElement(Ns.W14 + "stylisticSets", sets));
        }

        if (features.ValueOf("calt") is int calt)
        {
            c.Add(new XElement(Ns.W14 + "cntxtAlts", new XAttribute(Ns.W14 + "val", calt > 0 ? "1" : "0")));
        }
    }
}
