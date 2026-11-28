using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.DrawingML;
using Egap.Presentations.Pptx;

using FontStyle = Egami.Fonts.FontStyle;

namespace Egap.Presentations.Drawing;

/// <summary>What a run looks like once its properties are looked up down the chain (ECMA-376 Part 1, 21.1.2.3.9, <c>a:rPr</c>).</summary>
internal sealed record RunLook(
    string Latin,
    string? EastAsian,
    string? Complex,
    string? Symbol,
    float Size,
    bool Bold,
    bool Italic,
    string Underline,
    string Strike,
    float Baseline,
    string Caps,
    float Spacing,
    float? KernFrom,
    XElement? Fill,
    Rgba8 Color,
    XElement? Outline,
    Rgba8? Highlight,
    Rgba8? UnderlineColor,
    string? Language,
    XElement? Effects)
{
    public FontStyle FontStyle => (Bold, Italic) switch
    {
        (true, true) => FontStyle.BoldItalic,
        (true, false) => FontStyle.Bold,
        (false, true) => FontStyle.Italic,
        _ => FontStyle.Normal,
    };
}

/// <summary>A piece of a paragraph's text in one style: a run, or the part of one in one script.</summary>
internal sealed record TextAtom(string Text, TextStyle Style, RunLook Look);

/// <summary>Turns runs into Egami text: their looks from the property chain, their text split by script so each script takes its own font.</summary>
internal static class TextRuns
{
    private static readonly XNamespace A = PptxNames.A;
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    /// <summary>DrawingML's text size when nothing in the chain sets one: 18 points.</summary>
    public const float DefaultSize = 18;

    /// <summary>
    /// The look of a run with run properties <paramref name="rPr"/> in a paragraph looked up in <paramref name="chain"/>,
    /// sized by <paramref name="fontScale"/>, the autofit scale.
    /// </summary>
    public static RunLook Look(XElement? rPr, XElement?[] chain, TextFrame frame, float fontScale)
    {
        // The paragraph's own default run properties do not count: PowerPoint keeps them for new text only.
        XElement?[] sources = new XElement?[chain.Length];
        sources[0] = rPr;
        for (int i = 1; i < chain.Length; i++)
        {
            sources[i] = chain[i]?.Element(A + "defRPr");
        }

        Theme theme = frame.Theme;
        string latin = ThemeFont(Typeface(sources, "latin"), theme) ?? "";
        if (latin.Length == 0)
        {
            latin = theme.MinorLatinFont.Length > 0 ? theme.MinorLatinFont : "Calibri";
        }

        float size = (float)((Number(sources, "sz") ?? (DefaultSize * 100)) / 100) * fontScale;
        Rgba8 color = Black;
        XElement? fill = null;
        foreach (XElement? source in sources)
        {
            if (source?.Elements().FirstOrDefault(DrawingFills.IsFill) is { } found)
            {
                fill = found;
                color = found.Name.LocalName switch
                {
                    "solidFill" => DrawingColors.Resolve(found, theme, null) ?? Black,
                    "noFill" => new Rgba8(0, 0, 0, 0),
                    "gradFill" => DrawingFills.Stops(found).Select(s => DrawingColors.Resolve(s.Stop, theme, null)).FirstOrDefault(c => c is not null) ?? Black,
                    _ => Black,
                };
                break;
            }
        }

        string underline = Text(sources, "u") ?? "none";
        if (rPr?.Element(A + "hlinkClick") is { } link && ((string?)link.Attribute(PptxNames.R + "id") is { Length: > 0 } || (string?)link.Attribute("action") is { Length: > 0 }))
        {
            // A hyperlink takes the theme's hyperlink colour and an underline, as PowerPoint shows one.
            color = DrawingColors.Slot(theme, "hlink");
            fill = null;
            if ((string?)rPr.Attribute("u") is null)
            {
                underline = "sng";
            }
        }

        Rgba8? highlight = Find(sources, "highlight") is { } mark ? DrawingColors.Resolve(mark, theme, null) : null;
        Rgba8? underlineColor = Find(sources, "uFill") is { } uFill ? DrawingColors.Resolve(uFill.Element(A + "solidFill"), theme, null) : null;
        XElement? outline = Find(sources, "ln");
        if (outline is not null && DrawingFills.Kind(outline) is null or "noFill")
        {
            outline = null;
        }

        double? kern = Number(sources, "kern");
        return new RunLook(
            latin,
            ThemeFont(Typeface(sources, "ea"), theme) is { Length: > 0 } ea ? ea : null,
            ThemeFont(Typeface(sources, "cs"), theme) is { Length: > 0 } cs ? cs : null,
            ThemeFont(Typeface(sources, "sym"), theme) is { Length: > 0 } sym ? sym : null,
            Math.Max(0.5f, size),
            Flag(sources, "b"),
            Flag(sources, "i"),
            underline,
            Text(sources, "strike") ?? "noStrike",
            (float)((Number(sources, "baseline") ?? 0) / 100000),
            Text(sources, "cap") ?? "none",
            (float)((Number(sources, "spc") ?? 0) / 100),
            kern is double k ? (float)(k / 100) : null,
            fill,
            color,
            outline,
            highlight,
            underlineColor,
            Text(sources, "lang"),
            Find(sources, "effectLst"));
    }

    /// <summary>A theme font reference (<c>+mj-lt</c>, <c>+mn-ea</c> and the rest) as the theme's font; any other name as it is.</summary>
    public static string? ThemeFont(string? name, Theme theme) => name switch
    {
        "+mj-lt" => theme.MajorLatinFont,
        "+mn-lt" => theme.MinorLatinFont,
        "+mj-ea" => theme.MajorEastAsiaFont,
        "+mn-ea" => theme.MinorEastAsiaFont,
        "+mj-cs" => theme.MajorComplexScriptFont,
        "+mn-cs" => theme.MinorComplexScriptFont,
        _ => name,
    };

    /// <summary>
    /// Adds <paramref name="text"/> in <paramref name="look"/> to <paramref name="atoms"/>, split where the script changes
    /// between Latin, East Asian, complex and symbol text, each in its font, and set in capitals or small capitals.
    /// </summary>
    public static void Add(List<TextAtom> atoms, string text, RunLook look, TextFrame frame, Func<RunLook, string, LineHeight?> lineHeight, RectF bounds)
    {
        if (text.Length == 0)
        {
            return;
        }

        CultureInfo culture = Culture(look.Language);
        if (look.Caps == "all")
        {
            text = text.ToUpper(culture);
        }

        int start = 0;
        Script current = Script.Neutral;
        for (int i = 0; i < text.Length; i++)
        {
            Script script = ScriptOf(text, i, look);
            if (script == Script.Neutral || script == current)
            {
                continue;
            }

            if (current != Script.Neutral && i > start)
            {
                Emit(text[start..i], current);
                start = i;
            }

            current = script;
        }

        Emit(text[start..], current == Script.Neutral ? Script.Latin : current);

        void Emit(string piece, Script script)
        {
            string font = script switch
            {
                Script.EastAsian => look.EastAsian ?? look.Latin,
                Script.Complex => look.Complex ?? look.Latin,
                Script.Symbol => look.Symbol ?? look.Latin,
                _ => look.Latin,
            };

            if (look.Caps == "small")
            {
                // Small capitals: lower-case letters set as capitals at four fifths of the size.
                int from = 0;
                for (int i = 1; i <= piece.Length; i++)
                {
                    if (i == piece.Length || char.IsLower(piece[i]) != char.IsLower(piece[from]))
                    {
                        string part = piece[from..i];
                        bool lower = char.IsLower(part[0]);
                        RunLook sized = lower ? look with { Size = look.Size * 0.8f } : look;
                        atoms.Add(new TextAtom(lower ? part.ToUpper(culture) : part, Style(sized, font, frame, lineHeight(sized, font), bounds), look));
                        from = i;
                    }
                }

                return;
            }

            atoms.Add(new TextAtom(piece, Style(look, font, frame, lineHeight(look, font), bounds), look));
        }
    }

    /// <summary>The Egami text style of a run in <paramref name="font"/>.</summary>
    public static TextStyle Style(RunLook look, string font, TextFrame frame, LineHeight? lineHeight, RectF bounds)
    {
        FontStyle fontStyle = look.FontStyle;
        TextDecorationLines lines = (look.Underline != "none" ? TextDecorationLines.Underline : TextDecorationLines.None)
            | (look.Strike != "noStrike" ? TextDecorationLines.LineThrough : TextDecorationLines.None);
        TextDecoration? decoration = null;
        if (lines != TextDecorationLines.None)
        {
            (TextDecorationStyle style, float thickness) = look.Underline switch
            {
                "dbl" => (TextDecorationStyle.Double, 1f),
                "heavy" => (TextDecorationStyle.Solid, 2f),
                "dotted" => (TextDecorationStyle.Dotted, 1f),
                "dottedHeavy" => (TextDecorationStyle.Dotted, 2f),
                "dash" or "dashLong" or "dotDash" or "dotDotDash" => (TextDecorationStyle.Dashed, 1f),
                "dashHeavy" or "dashLongHeavy" or "dotDashHeavy" or "dotDotDashHeavy" => (TextDecorationStyle.Dashed, 2f),
                "wavy" => (TextDecorationStyle.Wavy, 1f),
                "wavyHeavy" => (TextDecorationStyle.Wavy, 2f),
                "wavyDbl" => (TextDecorationStyle.Double, 1f),
                _ => look.Strike == "dblStrike" && look.Underline == "none" ? (TextDecorationStyle.Double, 1f) : (TextDecorationStyle.Solid, 1f),
            };
            decoration = new TextDecoration
            {
                Lines = lines,
                Style = style,
                Color = look.UnderlineColor,
                Thickness = thickness == 1 ? null : look.Size / 18 * thickness,
                SkipInk = TextDecorationSkipInk.None,
            };
        }

        // A raised or lowered run is set at two thirds of its size, as Office sets superscripts and subscripts.
        float size = look.Baseline != 0 ? look.Size * 2 / 3 : look.Size;
        bool kern = look.KernFrom is float from && look.Size >= from - 0.001f;
        Paint? paint = look.Fill is { Name.LocalName: "gradFill" or "pattFill" } fill ? ShapePainter.PaintOf(fill, bounds, frame.Theme, null) : null;
        return new TextStyle
        {
            FontFamilies = frame.Fonts.Family(font, fontStyle) is { } family ? [family] : null,
            Size = size,
            FontStyle = fontStyle,
            Color = look.Color,
            Paint = paint,
            Background = look.Highlight,
            Decoration = decoration,
            LetterSpacing = look.Spacing != 0 ? look.Spacing : null,
            BaselineShift = look.Baseline != 0 ? -look.Baseline * look.Size : null,
            Features = kern ? null : [FontFeature.NoKerning],
            Language = look.Language,
            Shadows = Shadows(look, frame.Theme),
            LineHeight = lineHeight,
        };
    }

    /// <summary>The run's outer shadow (<c>a:outerShdw</c>) as a text shadow.</summary>
    private static TextShadow[]? Shadows(RunLook look, Theme theme)
    {
        if (look.Effects?.Element(A + "outerShdw") is not { } shadow || DrawingColors.Resolve(shadow, theme, null) is not { } color)
        {
            return null;
        }

        double distance = (PptxNames.Long(shadow, "dist") ?? 0) / (double)ShapeTransform.EmusPerPoint;
        double direction = (PptxNames.Long(shadow, "dir") ?? 0) / 60000.0 * Math.PI / 180;
        float blur = (float)((PptxNames.Long(shadow, "blurRad") ?? 0) / (double)ShapeTransform.EmusPerPoint);
        return [new TextShadow(new Vector2((float)(distance * Math.Cos(direction)), (float)(distance * Math.Sin(direction))), blur / 2, color)];
    }

    /// <summary>The culture a run's language names, or the invariant culture for none or one .NET does not know.</summary>
    public static CultureInfo Culture(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private enum Script
    {
        Neutral,
        Latin,
        EastAsian,
        Complex,
        Symbol,
    }

    /// <summary>
    /// Which of a run's fonts a character takes: East Asian for Chinese, Japanese and Korean text and their punctuation, complex
    /// for right-to-left and South and South-East Asian scripts, symbol for the symbol font's private use characters; spaces and
    /// punctuation go with the text around them.
    /// </summary>
    private static Script ScriptOf(string text, int index, RunLook look)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length)
        {
            int code = char.ConvertToUtf32(c, text[index + 1]);
            return code is >= 0x20000 and <= 0x3FFFF ? Script.EastAsian : Script.Latin;
        }

        if (char.IsLowSurrogate(c) || char.IsWhiteSpace(c) || (c < 0x80 && !char.IsLetterOrDigit(c)))
        {
            return Script.Neutral;
        }

        return c switch
        {
            >= '' and <= '' when look.Symbol is not null => Script.Symbol,
            >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '㏿' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿'
                or >= 'ꥠ' and <= '꥿' or >= '가' and <= '퟿' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏'
                or >= '＀' and <= '￯' => Script.EastAsian,
            >= '֐' and <= 'ࣿ' or >= 'ऀ' and <= '෿' or >= '฀' and <= '໿' or >= 'က' and <= '႟'
                or >= 'ក' and <= '៿' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Complex,
            _ => Script.Latin,
        };
    }

    private static string? Typeface(XElement?[] sources, string name) => (string?)Find(sources, name)?.Attribute("typeface");

    private static XElement? Find(XElement?[] sources, string name)
    {
        foreach (XElement? source in sources)
        {
            if (source?.Element(A + name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? Text(XElement?[] sources, string name)
    {
        foreach (XElement? source in sources)
        {
            if ((string?)source?.Attribute(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static double? Number(XElement?[] sources, string name) =>
        double.TryParse(Text(sources, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static bool Flag(XElement?[] sources, string name) => Text(sources, name) is "1" or "true";
}
