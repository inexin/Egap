using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Egami.Fonts;

using Egap.Documents.Layout;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Lays out equation islands for the paragraph layout and keeps each island's box for drawing: an inline equation
/// (<c>m:oMath</c>) in text style, a display paragraph (<c>m:oMathPara</c>) one display-style line per equation, justified per
/// <c>m:jc</c> or the document's <c>m:defJc</c>.
/// </summary>
internal static class MathRendering
{
    private static readonly ConditionalWeakTable<Island, Laid> Boxes = new();

    /// <summary>
    /// The box of a math island at <paramref name="size"/> points (the surrounding text's size, unless the equation's own runs
    /// set one), a display equation broken into lines to fit <paramref name="maxWidth"/> points; null when the island isn't
    /// math or its markup doesn't read.
    /// </summary>
    public static MathBox? Layout(Island island, FontProvider fonts, Document document, float size, float maxWidth = float.PositiveInfinity)
    {
        if (island.Kind != IslandKind.Math || MathIslands.Tree(island) is not { } tree)
        {
            return null;
        }

        size = RunSize(tree) ?? size;
        if (Boxes.TryGetValue(island, out Laid? laid) && laid.Size == size && laid.Fonts == fonts && laid.MaxWidth.Equals(maxWidth))
        {
            return laid.Box;
        }

        MathSettings settings = document.MathSettings;
        (Typeface face, TypefaceMath math) = MathFonts.Resolve(fonts, settings);
        MathLayout layout = new(face, math, settings, size);
        // The paragraph's margins for math (m:lMargin, m:rMargin) narrow the room display lines break to fit.
        float room = maxWidth - ((settings.LeftMargin + settings.RightMargin) / 20f);
        MathBox box = tree is MathParagraph paragraph ? Display(layout, paragraph, settings, room) : layout.Node(tree, display: IsDisplayTree(tree));
        Boxes.AddOrUpdate(island, new Laid(box, size, fonts, maxWidth));
        return box;
    }

    /// <summary>The box <see cref="Layout"/> made for <paramref name="island"/> last; null when it made none.</summary>
    public static MathBox? Box(Island island) => Boxes.TryGetValue(island, out Laid? laid) ? laid.Box : null;

    /// <summary>Whether <paramref name="island"/> is a display equation paragraph.</summary>
    public static bool IsDisplay(Island island) => island.Kind == IslandKind.Math && MathIslands.Tree(island) is { } tree && IsDisplayTree(tree);

    /// <summary>
    /// Whether a tree sets as display math: a display paragraph, or (as Word does) an inline equation that is only an equation
    /// array, which Word moves onto a line of its own.
    /// </summary>
    private static bool IsDisplayTree(MathNode tree) => tree switch
    {
        MathParagraph => true,
        MathZone zone => zone.Content.Items.Count > 0
            && zone.Content.Items.All(n => n is MathObject { Kind: MathKind.EquationArray } || n is MathRun { Text.Length: 0 })
            && zone.Content.Items.Any(n => n is MathObject),
        _ => false,
    };

    /// <summary>How a display paragraph sits in its line: <c>left</c>, <c>right</c> or <c>center</c>.</summary>
    public static string Justification(Island island, Document document)
    {
        string jc = (MathIslands.Tree(island) as MathParagraph)?.Justification ?? document.MathSettings.DisplayJustification;
        return jc is "left" or "right" ? jc : "center";
    }

    private static MathBox Display(MathLayout layout, MathParagraph paragraph, MathSettings settings, float maxWidth)
    {
        List<MathBox> lines = layout.Lines(paragraph, maxWidth);
        string jc = paragraph.Justification ?? settings.DisplayJustification;
        float width = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        MathBox box = new() { Width = width };
        float y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                y += lines[i - 1].Descent + lines[i].Ascent + (settings.SpaceBetween / 20f);
            }

            // centerGroup keeps the lines' left edges together and centres the group; center centres each line.
            float x = jc switch
            {
                "center" => (width - lines[i].Width) / 2,
                "right" => width - lines[i].Width,
                _ => 0,
            };
            box.Add(lines[i], x, y);
        }

        box.Ascent = lines.Count == 0 ? 0 : lines[0].Ascent;
        box.Descent = lines.Count == 0 ? 0 : y + lines[^1].Descent;
        return box;
    }

    /// <summary>The font size the equation's first run sets (<c>w:sz</c>, half-points), if any.</summary>
    private static float? RunSize(MathNode tree)
    {
        foreach (MathRun run in Runs(tree))
        {
            foreach (XElement properties in run.WordProperties)
            {
                XElement? sz = properties.Name.LocalName == "sz" ? properties : properties.Elements().FirstOrDefault(e => e.Name.LocalName == "sz");
                string? value = sz?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value;
                if (value is not null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float half) && half > 0)
                {
                    return half / 2;
                }
            }

            return null;
        }

        return null;
    }

    private static IEnumerable<MathRun> Runs(MathNode node)
    {
        Stack<MathNode> pending = new();
        pending.Push(node);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case MathRun run:
                    yield return run;
                    break;
                case MathRow row:
                    for (int i = row.Items.Count - 1; i >= 0; i--)
                    {
                        pending.Push(row.Items[i]);
                    }

                    break;
                case MathZone zone:
                    pending.Push(zone.Content);
                    break;
                case MathParagraph paragraph:
                    for (int i = paragraph.Items.Count - 1; i >= 0; i--)
                    {
                        pending.Push(paragraph.Items[i]);
                    }

                    break;
                case MathObject obj:
                    for (int i = obj.Arguments.Count - 1; i >= 0; i--)
                    {
                        pending.Push(obj.Arguments[i].Row);
                    }

                    foreach (List<MathRow> row in obj.Rows)
                    {
                        foreach (MathRow cell in row)
                        {
                            pending.Push(cell);
                        }
                    }

                    break;
            }
        }
    }

    private sealed record Laid(MathBox Box, float Size, FontProvider Fonts, float MaxWidth);
}
