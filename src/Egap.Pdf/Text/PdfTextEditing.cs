using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Egap.Pdf.Redaction;
using Egap.Pdf.Text;

namespace Egap.Pdf;

/// <summary>How <see cref="PdfDocument.ReplaceText(string, string, PdfFindOptions?, PdfReplaceOptions?)"/> writes the new text.</summary>
public sealed record PdfReplaceOptions
{
    /// <summary>Gets the defaults: no fallback font.</summary>
    public static PdfReplaceOptions Default { get; } = new();

    /// <summary>
    /// Gets a typeface the new text is written in, embedded as a subset, where the matched text's own font lacks a glyph it
    /// needs; null (the default) to leave such matches as they are, with a warning.
    /// </summary>
    public Typeface? FallbackFont { get; init; }
}

/// <summary>What <see cref="PdfDocument.ReplaceText(string, string, PdfFindOptions?, PdfReplaceOptions?)"/> did.</summary>
/// <param name="Replaced">The matches whose text was replaced.</param>
/// <param name="Skipped">The matches left as they were; <paramref name="Warnings"/> says why.</param>
/// <param name="Warnings">One warning for each match left as it was.</param>
public sealed record PdfReplaceResult(IReadOnlyList<PdfTextMatch> Replaced, IReadOnlyList<PdfTextMatch> Skipped, IReadOnlyList<PdfWarning> Warnings);

/// <summary>Redacting and replacing text by phrase.</summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Finds a phrase as <see cref="FindText(string, PdfFindOptions?, CancellationToken)"/> does, marks each match for
    /// redaction and applies it for real: the matched glyphs are removed from the content, with what else the options say
    /// (images under the area, line art, annotations). Redaction marks already in the document stay as they are. The
    /// document must then be saved whole, so nothing redacted survives.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("contract.pdf");
    /// PdfRedactionResult result = pdf.RedactText("Confidential", new PdfFindOptions { MatchCase = false });
    /// pdf.Save("contract-redacted.pdf");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public PdfRedactionResult RedactText(string text, PdfFindOptions? find = null, PdfRedactionOptions? options = null)
    {
        return RedactText(TextSearch.Pattern(text, find ?? PdfFindOptions.Default), find, options);
    }

    /// <summary>Redacts every match of a regular expression, as <see cref="RedactText(string, PdfFindOptions?, PdfRedactionOptions?)"/> does.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public PdfRedactionResult RedactText(Regex pattern, PdfFindOptions? find = null, PdfRedactionOptions? options = null)
    {
        HashSet<PdfRedactAnnotation> marks = [.. MarkRedactions(pattern, find)];
        return Redactor.Apply(this, options ?? PdfRedactionOptions.Default, marks.Contains);
    }

    /// <summary>
    /// Finds a phrase and adds a redaction annotation over each match, without applying them, so the marks can be checked in
    /// a viewer first; <see cref="ApplyRedactions"/> applies them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public IReadOnlyList<PdfRedactAnnotation> MarkRedactions(string text, PdfFindOptions? find = null)
    {
        return MarkRedactions(TextSearch.Pattern(text, find ?? PdfFindOptions.Default), find);
    }

    /// <summary>Adds a redaction annotation over each match of a regular expression, without applying them.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public IReadOnlyList<PdfRedactAnnotation> MarkRedactions(Regex pattern, PdfFindOptions? find = null)
    {
        List<PdfRedactAnnotation> marks = [];
        foreach (PdfTextMatch match in FindText(pattern, find))
        {
            PdfRedactAnnotation mark = new(match.Box) { QuadPoints = [.. match.Quads.SelectMany(q => q.ToPoints())] };
            marks.Add(Pages[match.PageIndex].Annotations.Add(mark));
        }

        return marks;
    }

    /// <summary>
    /// Replaces a phrase with other text wherever it is shown. Each match's glyphs are removed from the content for real,
    /// and the new text is written where the match began, in the same font, size, colour and rendering mode (invisible OCR
    /// text stays invisible), when that font has a glyph for every character of it. Otherwise the match is written in
    /// <see cref="PdfReplaceOptions.FallbackFont"/>, or left as it was with a warning. Text after the match does not move.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("price-list.pdf");
    /// PdfReplaceResult result = pdf.ReplaceText("2025", "2026");
    /// Console.WriteLine($"{result.Replaced.Count} replaced, {result.Skipped.Count} left");
    /// pdf.Save("price-list-2026.pdf");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is null.</exception>
    public PdfReplaceResult ReplaceText(string text, string replacement, PdfFindOptions? find = null, PdfReplaceOptions? options = null)
    {
        return ReplaceText(TextSearch.Pattern(text, find ?? PdfFindOptions.Default), replacement, find, options);
    }

    /// <summary>
    /// Replaces every match of a regular expression, as <see cref="ReplaceText(string, string, PdfFindOptions?, PdfReplaceOptions?)"/>
    /// does; the replacement may name groups (<c>$1</c>, <c>${year}</c>) as <see cref="Regex.Replace(string, string)"/> takes them.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PdfReplaceResult ReplaceText(Regex pattern, string replacement, PdfFindOptions? find = null, PdfReplaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(replacement);
        return TextReplacer.Replace(this, pattern, replacement, find ?? PdfFindOptions.Default, options ?? PdfReplaceOptions.Default);
    }
}

/// <summary>
/// Replacing text: the matches on a page are found first, their glyphs are then removed by redaction limited to text, and
/// each new text is written as its own text object over the page, in the match's font where every character has a glyph,
/// else in the fallback typeface through a <see cref="PdfCanvas"/>.
/// </summary>
internal static class TextReplacer
{
    public static PdfReplaceResult Replace(PdfDocument document, Regex pattern, string replacement, PdfFindOptions find, PdfReplaceOptions options)
    {
        List<PdfTextMatch> replaced = [], skipped = [];
        List<PdfWarning> warnings = [];
        Dictionary<PdfFont, Dictionary<string, byte[]>?> encoders = new(ReferenceEqualityComparer.Instance);
        document.WrittenResources.Finish();
        foreach (int index in (find.Pages ?? PdfPageRange.All).Resolve(document.Pages.Count))
        {
            PdfPage page = document.Pages[index];
            PdfTextPage text = TextCollector.ReadWithDetails(page, find.Text ?? PdfTextOptions.Default, out Dictionary<PdfTextGlyph, GlyphDetails> details);
            List<(PdfTextMatch Match, string NewText)> found = [];
            foreach (TextSearch.Block block in TextSearch.Blocks(text.Lines))
            {
                for (Match m = pattern.Match(block.Text); m.Success; m = m.NextMatch())
                {
                    if (m.Length > 0 && TextSearch.ToMatch(index, block, m.Index, m.Length) is { } match)
                    {
                        found.Add((match, m.Result(replacement)));
                    }
                }
            }

            List<(PdfTextMatch Match, Action<PdfPage> Write)> planned = [];
            foreach ((PdfTextMatch match, string newText) in found)
            {
                PdfTextGlyph first = match.Glyphs[0];
                if (!details.TryGetValue(first, out GlyphDetails detail))
                {
                    // A glyph made of a whole /ActualText span has no one matrix to write at.
                    skipped.Add(match);
                    warnings.Add(new PdfWarning(PdfWarningCode.TextNotReplaced, $"\"{match.Text}\" on page {index + 1} was left as it was: it is marked-content replacement text, not glyphs."));
                }
                else if (Encode(first, detail, newText, encoders) is { } codes)
                {
                    planned.Add((match, p => WriteInFont(p, first, detail, codes)));
                }
                else if (options.FallbackFont is { } fallback)
                {
                    planned.Add((match, p => WriteInFallback(p, first, detail, newText, fallback)));
                }
                else
                {
                    skipped.Add(match);
                    warnings.Add(new PdfWarning(PdfWarningCode.TextNotReplaced, $"\"{match.Text}\" on page {index + 1} was left as it was: its font has no glyph for some of \"{newText}\", and no fallback font was given."));
                }
            }

            if (planned.Count == 0)
            {
                continue;
            }

            RemoveGlyphs(document, page, [.. planned.SelectMany(p => p.Match.Glyphs)]);
            foreach ((PdfTextMatch match, Action<PdfPage> write) in planned)
            {
                write(page);
                replaced.Add(match);
            }
        }

        return new PdfReplaceResult(replaced, skipped, warnings);
    }

    /// <summary>
    /// Removes the glyphs from the page's content, and nothing else: each is redacted through an area a little narrower than
    /// its box, and a glyph goes only when most of its box is covered, so a kerned neighbour stays.
    /// </summary>
    private static void RemoveGlyphs(PdfDocument document, PdfPage page, IReadOnlyList<PdfTextGlyph> glyphs)
    {
        List<PdfRectangle> areas = [];
        foreach (PdfTextGlyph glyph in glyphs)
        {
            PdfRectangle box = glyph.Box.Normalize();
            double inset = box.Width * 0.1;
            areas.Add(new PdfRectangle(box.Left + inset, box.Bottom, box.Right - inset, box.Top));
        }

        PdfRedactionOptions textOnly = new() { Images = PdfRedactImages.None, Paths = PdfRedactPaths.None, GlyphOverlap = 0.5 };
        Redactor.RedactContent(document, page, areas, textOnly);
    }

    /// <summary>The new text as codes of the glyph's own font, when it can show every character; null when it cannot.</summary>
    private static byte[]? Encode(PdfTextGlyph glyph, GlyphDetails detail, string text, Dictionary<PdfFont, Dictionary<string, byte[]>?> encoders)
    {
        if (glyph.Font is not { } font || detail.FontResource is null || font.Type == PdfFontType.Type3)
        {
            return null;
        }

        if (!encoders.TryGetValue(font, out Dictionary<string, byte[]>? encoder))
        {
            encoder = Encoder(font);
            encoders[font] = encoder;
        }

        if (encoder is null)
        {
            return null;
        }

        List<byte> codes = [];
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            if (!encoder.TryGetValue(elements.GetTextElement(), out byte[]? code))
            {
                return null;
            }

            codes.AddRange(code);
        }

        return [.. codes];
    }

    /// <summary>
    /// The characters a font can show, each with its code: a simple font's 256 codes, or a two-byte Identity font's codes, kept
    /// where the font maps the code to text and (when embedded) its program has a glyph with ink for it, or it is a space.
    /// </summary>
    private static Dictionary<string, byte[]>? Encoder(PdfFont font)
    {
        bool identity = font.IsComposite && font.Dictionary.GetName("Encoding")?.Value is "Identity-H" or "Identity-V";
        if (font.IsComposite && !identity)
        {
            return null;
        }

        Typeface? face = font.IsEmbedded ? font.GetTypeface() : null;
        if (font.IsEmbedded && face is null)
        {
            return null;
        }

        Dictionary<string, byte[]> map = [];
        int last = identity ? 0xFFFF : 0xFF;
        for (int code = 0; code <= last; code++)
        {
            if (font.ToUnicode(code) is not { Length: > 0 } text || map.ContainsKey(text))
            {
                continue;
            }

            if (face is not null)
            {
                if (font.GetGlyphId(code) is not ushort id || id == 0 || id >= face.GlyphCount)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(text) && face.GetGlyphExtents(id).IsEmpty)
                {
                    continue;
                }
            }

            map[text] = identity ? [(byte)(code >> 8), (byte)code] : [(byte)code];
        }

        return map;
    }

    private static void WriteInFont(PdfPage page, PdfTextGlyph glyph, GlyphDetails detail, byte[] codes)
    {
        // A font written directly in the resources is copied: a PDF object lives in one container.
        PdfName font = page.AddResource("Font", detail.FontResource is PdfDictionary direct ? direct.Clone() : detail.FontResource!);
        PdfContentWriter content = new();
        content.Save();
        FillColor(page, content, glyph.Color);
        PdfMatrix m = detail.Matrix;
        content.BeginText().Font(font, 1).TextRenderMode(detail.RenderMode).TextMatrix(m.A, m.B, m.C, m.D, m.E, m.F).ShowText(codes, hex: true).EndText();
        content.Restore();
        page.AddContent(content.ToArray());
    }

    private static void WriteInFallback(PdfPage page, PdfTextGlyph glyph, GlyphDetails detail, string text, Typeface face)
    {
        PdfMatrix m = detail.Matrix;
        using PdfCanvas canvas = new(page, new PdfCanvasOptions { Coordinates = PdfCanvasCoordinates.Pdf });

        // Glyph space is y down on the canvas and y up in PDF text space: flip, then the match's own text matrix.
        canvas.Concat(new Matrix3x2((float)m.A, (float)m.B, (float)-m.C, (float)-m.D, (float)m.E, (float)m.F));
        canvas.DrawText(text, Vector2.Zero, new Font(face, 1), new SolidPaint(ToRgba(glyph.Color)));
    }

    private static void FillColor(PdfPage page, PdfContentWriter content, PdfColor color)
    {
        IReadOnlyList<double> c = color.Components;
        switch (color.Space)
        {
            case PdfName { Value: "DeviceGray" } when c.Count == 1:
                content.Gray(c[0]);
                break;
            case PdfName { Value: "DeviceRGB" } when c.Count == 3:
                content.Rgb(c[0], c[1], c[2]);
                break;
            case PdfName { Value: "DeviceCMYK" } when c.Count == 4:
                content.Cmyk(c[0], c[1], c[2], c[3]);
                break;
            case PdfName:
                content.Gray(0);
                break;
            default:
                if (color.Pattern is null && c.Count > 0)
                {
                    content.ColorSpace(page.AddResource("ColorSpace", color.Space)).Color([.. c]);
                }
                else
                {
                    content.Gray(0);
                }

                break;
        }
    }

    private static RgbaF32 ToRgba(PdfColor color)
    {
        IReadOnlyList<double> c = color.Components;
        return c.Count switch
        {
            1 => new RgbaF32((float)c[0], (float)c[0], (float)c[0], 1),
            3 => new RgbaF32((float)c[0], (float)c[1], (float)c[2], 1),
            4 => new RgbaF32((float)((1 - c[0]) * (1 - c[3])), (float)((1 - c[1]) * (1 - c[3])), (float)((1 - c[2]) * (1 - c[3])), 1),
            _ => new RgbaF32(0, 0, 0, 1),
        };
    }
}
