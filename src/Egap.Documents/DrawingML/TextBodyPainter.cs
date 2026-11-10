using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

using Egap.Documents.Layout;

using FontStyle = Egami.Fonts.FontStyle;

namespace Egap.Documents.DrawingML;

/// <summary>
/// Draws a DrawingML text body (<c>a:txBody</c>, <c>dsp:txBody</c>) into a shape's text rectangle: the body's insets, anchor,
/// vertical text and autofit scale; each paragraph's alignment, level, indents, spacing and bullet character; each run's font, size,
/// bold, italic, underline, strike-through, caps, colour and baseline shift, with defaults from the body's list styles and the
/// shape's <c>fontRef</c>.
/// </summary>
internal static class TextBodyPainter
{
    private static readonly XNamespace A = DrawingNs.A;
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    /// <summary>DrawingML's default text size, 18 points.</summary>
    private const float DefaultSize = 18;

    /// <summary>
    /// Draws <paramref name="txBody"/> into <paramref name="rect"/> (before the body's insets), turned by
    /// <paramref name="rotation"/> degrees about the rectangle's centre. <paramref name="style"/> is the shape's <c>style</c>, whose
    /// <c>fontRef</c> gives the default font and colour.
    /// </summary>
    public static void Draw(Canvas canvas, RectF rect, double rotation, XElement txBody, XElement? style, ShapePaintContext context)
    {
        XElement? bodyPr = txBody.Element(A + "bodyPr");
        double rot = rotation + ((DrawingNs.Double((string?)bodyPr?.Attribute("rot")) ?? 0) / 60000);
        string vert = (string?)bodyPr?.Attribute("vert") ?? "horz";
        double turn = vert switch
        {
            "vert" or "eaVert" or "mongolianVert" => 90,
            "vert270" => 270,
            _ => 0,
        };

        float left = Emu(bodyPr, "lIns", 91440), top = Emu(bodyPr, "tIns", 45720), right = Emu(bodyPr, "rIns", 91440), bottom = Emu(bodyPr, "bIns", 45720);
        RectF inner = new(rect.X + left, rect.Y + top, Math.Max(0, rect.Width - left - right), Math.Max(0, rect.Height - top - bottom));
        Vector2 center = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        if (turn != 0)
        {
            // The text runs down the box: lay it out across the box's height.
            inner = new RectF(center.X - (inner.Height / 2), center.Y - (inner.Width / 2), inner.Height, inner.Width);
        }

        XElement? autofit = bodyPr?.Element(A + "normAutofit");
        float fontScale = (float)((DrawingNs.Double((string?)autofit?.Attribute("fontScale")) ?? 100000) / 100000);
        float spacingCut = (float)((DrawingNs.Double((string?)autofit?.Attribute("lnSpcReduction")) ?? 0) / 100000);
        bool wrap = (string?)bodyPr?.Attribute("wrap") != "none";

        FontProvider fonts = context.Fonts ?? new FontProvider(context.Theme.Document);
        Defaults defaults = DefaultsOf(style, context.Theme);
        List<Block> blocks = Lay(txBody, inner.Width, wrap, fontScale, spacingCut, defaults, fonts, context.Theme);
        if (blocks.Count == 0)
        {
            return;
        }

        float height = blocks.Sum(b => b.Before + b.Height + b.After) - blocks[0].Before - blocks[^1].After;
        float y = ((string?)bodyPr?.Attribute("anchor")) switch
        {
            "ctr" => inner.Y + ((inner.Height - height) / 2),
            "b" => inner.Bottom - height,
            _ => inner.Y,
        };

        float x = inner.X;
        if (ShapePainter.Flag(bodyPr, "anchorCtr"))
        {
            float widest = blocks.Max(b => b.Indent + b.Layout.LongestLine);
            x = inner.X + ((inner.Width - widest) / 2);
        }

        int saved = canvas.Save();
        try
        {
            if (rot + turn != 0)
            {
                canvas.Translate(center.X, center.Y);
                canvas.Rotate((float)(rot + turn));
                canvas.Translate(-center.X, -center.Y);
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                y += i == 0 ? 0 : block.Before;
                if (block.Bullet is { } bullet && block.Layout.Lines.Count > 0)
                {
                    float baseline = y + block.Layout.Lines[0].Baseline;
                    canvas.DrawTextLayout(bullet, new Vector2(x + block.BulletAt, baseline - (bullet.Lines.Count > 0 ? bullet.Lines[0].Baseline : 0)));
                }

                canvas.DrawTextLayout(block.Layout, new Vector2(x + block.Indent, y));
                y += block.Height + block.After;
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>The text body's plain text, one line per paragraph: for alternative text and searches.</summary>
    public static string Text(XElement txBody) =>
        string.Join('\n', txBody.Elements(A + "p").Select(p => string.Concat(p.Elements().Select(e => e.Name.LocalName switch
        {
            "r" or "fld" => (string?)e.Element(A + "t") ?? "",
            "br" => "\n",
            _ => "",
        }))));

    /// <summary>A laid-out paragraph, with the room it keeps before and after it, its indent and its bullet.</summary>
    private sealed record Block(TextLayout Layout, float Height, float Before, float After, float Indent, TextLayout? Bullet, float BulletAt);

    /// <summary>The shape's defaults: its <c>fontRef</c> font and colour.</summary>
    private readonly record struct Defaults(string Font, Rgba8 Color);

    /// <summary>What a run looks like, before it becomes a text style.</summary>
    private sealed record Look(string Font, float Size, bool Bold, bool Italic, bool Underline, bool Strike, bool Caps, Rgba8 Color, float Baseline);

    private static Defaults DefaultsOf(XElement? style, Theme theme)
    {
        XElement? fontRef = style?.Element(A + "fontRef");
        string font = (string?)fontRef?.Attribute("idx") == "major" ? "+mj-lt" : "+mn-lt";
        Rgba8 color = DrawingColors.Resolve(fontRef, theme, null) ?? Black;
        return new Defaults(font, color);
    }

    private static List<Block> Lay(XElement txBody, float width, bool wrap, float fontScale, float spacingCut, Defaults defaults, FontProvider fonts, Theme theme)
    {
        XElement? lists = txBody.Element(A + "lstStyle");
        List<Block> blocks = [];
        foreach (XElement p in txBody.Elements(A + "p"))
        {
            XElement? pPr = p.Element(A + "pPr");
            int level = Math.Clamp((int)(DrawingNs.Double((string?)pPr?.Attribute("lvl")) ?? 0), 0, 8);
            XElement? listLevel = lists?.Element(A + $"lvl{level + 1}pPr");
            XElement?[] paragraphChain = [pPr, listLevel];
            XElement?[] runDefaults = [pPr?.Element(A + "defRPr"), listLevel?.Element(A + "defRPr")];

            RichTextBuilder builder = new();
            Look? first = null;
            foreach (XElement run in p.Elements())
            {
                string? text = run.Name.LocalName switch
                {
                    "r" or "fld" => (string?)run.Element(A + "t") ?? "",
                    "br" => "\n",
                    _ => null,
                };
                if (text is null)
                {
                    continue;
                }

                Look look = LookOf(run.Element(A + "rPr"), runDefaults, defaults, fontScale, theme);
                first ??= look;
                builder.Push(Style(look, fonts));
                builder.Append(look.Caps ? text.ToUpperInvariant() : text);
                builder.Pop();
            }

            // An empty paragraph keeps the height of its end mark.
            Look mark = first ?? LookOf(p.Element(A + "endParaRPr"), runDefaults, defaults, fontScale, theme);
            if (first is null)
            {
                builder.Push(Style(mark, fonts));
                builder.Append("​");
                builder.Pop();
            }

            float lineHeight = LineHeightOf(mark, fonts);
            float spacing = Spacing(paragraphChain, "lnSpc", mark.Size, lineHeight, 1) ?? lineHeight;
            spacing = Math.Max(0, spacing * (1 - spacingCut));
            float before = Spacing(paragraphChain, "spcBef", mark.Size, lineHeight, 0) ?? 0;
            float after = Spacing(paragraphChain, "spcAft", mark.Size, lineHeight, 0) ?? 0;

            float marL = (float)((Attribute(paragraphChain, "marL") ?? 0) / DrawingNs.EmuPerPoint);
            float indent = (float)((Attribute(paragraphChain, "indent") ?? 0) / DrawingNs.EmuPerPoint);
            TextAlign align = AttributeText(paragraphChain, "algn") switch
            {
                "ctr" => TextAlign.Center,
                "r" => TextAlign.Right,
                "just" or "dist" or "justLow" or "thaiDist" => TextAlign.Justify,
                _ => TextAlign.Left,
            };

            TextLayout? bullet = Bullet(paragraphChain, mark, fonts, theme);
            float textAt = bullet is null ? Math.Max(0, marL + Math.Min(0, indent)) : marL;
            if (bullet is null && indent > 0)
            {
                textAt = marL;
            }

            Egami.Fonts.Layout.ParagraphStyle paragraph = new()
            {
                DefaultStyle = Style(mark, fonts) with { LineHeight = Egami.Fonts.Layout.LineHeight.Absolute(spacing) },
                Align = align,
                Fallback = fonts.Fallback,
            };
            RichText spaced = builder.ToRichText();
            float room = wrap ? Math.Max(1, width - textAt) : 1e6f;
            TextLayout layout = Egami.Fonts.Layout.Paragraph.Create(spaced, paragraph).Layout(room);
            if (!wrap && align != TextAlign.Left)
            {
                // Unwrapped text aligns within the box, not within the endless line it was laid on.
                layout = Egami.Fonts.Layout.Paragraph.Create(spaced, paragraph).Layout(Math.Max(Math.Max(1, width - textAt), layout.LongestLine));
            }

            blocks.Add(new Block(layout, layout.Height, before, after, textAt, bullet, marL + indent));
        }

        return blocks;
    }

    private static TextLayout? Bullet(XElement?[] chain, Look mark, FontProvider fonts, Theme theme)
    {
        foreach (XElement? pPr in chain)
        {
            if (pPr is null)
            {
                continue;
            }

            if (pPr.Element(A + "buNone") is not null)
            {
                return null;
            }

            if (pPr.Element(A + "buChar") is { } character && (string?)character.Attribute("char") is { Length: > 0 } text)
            {
                string font = (string?)Find(chain, "buFont")?.Attribute("typeface") ?? mark.Font;
                double scale = (DrawingNs.Double((string?)Find(chain, "buSzPct")?.Attribute("val")) ?? 100000) / 100000;
                Rgba8 color = Find(chain, "buClr") is { } holder ? DrawingColors.Resolve(holder, theme, null) ?? mark.Color : mark.Color;
                Look look = mark with { Font = font, Size = (float)(mark.Size * scale), Color = color, Underline = false, Strike = false, Caps = false };
                return Egami.Fonts.Layout.Paragraph.Create(text, new Egami.Fonts.Layout.ParagraphStyle { DefaultStyle = Style(look, fonts), Fallback = fonts.Fallback }).Layout(1e5f);
            }
        }

        return null;
    }

    private static XElement? Find(XElement?[] chain, string name)
    {
        foreach (XElement? element in chain)
        {
            if (element?.Element(A + name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static double? Attribute(XElement?[] chain, string name)
    {
        foreach (XElement? element in chain)
        {
            if (DrawingNs.Double((string?)element?.Attribute(name)) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? AttributeText(XElement?[] chain, string name)
    {
        foreach (XElement? element in chain)
        {
            if ((string?)element?.Attribute(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>A line or paragraph spacing: <c>spcPct</c> as a share of the line (or, for line spacing, the line itself), <c>spcPts</c> in hundredths of a point.</summary>
    private static float? Spacing(XElement?[] chain, string name, float size, float lineHeight, float lineShare)
    {
        if (Find(chain, name) is not { } spacing)
        {
            return null;
        }

        if (DrawingNs.Double((string?)spacing.Element(A + "spcPts")?.Attribute("val")) is { } points)
        {
            return (float)(points / 100);
        }

        if (DrawingNs.Double((string?)spacing.Element(A + "spcPct")?.Attribute("val")) is { } percent)
        {
            // Line spacing is a share of the font's line; spacing before and after a share of the text size's line.
            float unit = lineShare > 0 ? lineHeight : size * 1.2f;
            return (float)(percent / 100000 * unit);
        }

        return null;
    }

    private static Look LookOf(XElement? rPr, XElement?[] defaults, Defaults shape, float fontScale, Theme theme)
    {
        XElement?[] chain = [rPr, .. defaults];
        string? Text(string name) => AttributeText(chain, name);
        bool Flag(string name) => Text(name) is "1" or "true";

        string font = Find(chain, "latin")?.Attribute("typeface")?.Value ?? shape.Font;
        font = font switch
        {
            "+mn-lt" or "+mn-ea" or "+mn-cs" => theme.MinorLatinFont,
            "+mj-lt" or "+mj-ea" or "+mj-cs" => theme.MajorLatinFont,
            _ => font,
        };
        if (string.IsNullOrWhiteSpace(font))
        {
            font = string.IsNullOrWhiteSpace(theme.MinorLatinFont) ? "Calibri" : theme.MinorLatinFont;
        }

        float size = (float)((Attribute(chain, "sz") ?? (DefaultSize * 100)) / 100) * fontScale;
        Rgba8 color = shape.Color;
        foreach (XElement? element in chain)
        {
            if (element?.Element(A + "solidFill") is { } fill)
            {
                color = DrawingColors.Resolve(fill, theme, null) ?? color;
                break;
            }

            if (element?.Element(A + "noFill") is not null)
            {
                color = new Rgba8(0, 0, 0, 0);
                break;
            }
        }

        float baseline = (float)((Attribute(chain, "baseline") ?? 0) / 100000);
        return new Look(
            font,
            Math.Max(1, size),
            Flag("b"),
            Flag("i"),
            Text("u") is { } u && u != "none",
            Text("strike") is { } s && s != "noStrike",
            Text("cap") == "all",
            color,
            baseline);
    }

    private static TextStyle Style(Look look, FontProvider fonts)
    {
        FontStyle style = (look.Bold, look.Italic) switch { (true, true) => FontStyle.BoldItalic, (true, false) => FontStyle.Bold, (false, true) => FontStyle.Italic, _ => FontStyle.Normal };
        TextDecorationLines lines = (look.Underline ? TextDecorationLines.Underline : TextDecorationLines.None) | (look.Strike ? TextDecorationLines.LineThrough : TextDecorationLines.None);

        // A raised or lowered run is set smaller, as Office sets it.
        float size = look.Baseline != 0 ? look.Size * 2 / 3 : look.Size;
        return new TextStyle
        {
            FontFamilies = fonts.ResolveFamily(look.Font, style) is { } family ? [family] : null,
            Size = size,
            FontStyle = style,
            Color = look.Color,
            Decoration = lines == TextDecorationLines.None ? null : new TextDecoration { Lines = lines },
            BaselineShift = look.Baseline != 0 ? -look.Baseline * look.Size : null,
        };
    }

    private static float LineHeightOf(Look look, FontProvider fonts)
    {
        FontStyle style = (look.Bold, look.Italic) switch { (true, true) => FontStyle.BoldItalic, (true, false) => FontStyle.Bold, (false, true) => FontStyle.Italic, _ => FontStyle.Normal };
        return new Font(fonts.Resolve(look.Font, style).Typeface, look.Size).LineHeight;
    }

    private static float Emu(XElement? element, string name, double fallback) =>
        (float)((DrawingNs.Double((string?)element?.Attribute(name)) ?? fallback) / DrawingNs.EmuPerPoint);
}
