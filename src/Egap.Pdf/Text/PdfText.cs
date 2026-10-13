using System.Text;

namespace Egap.Pdf;

/// <summary>The order <see cref="PdfPage.GetText(PdfTextOptions?)"/> puts text in (Phase 3 design Q10).</summary>
public enum PdfTextOrder
{
    /// <summary>
    /// As the content stream shows it, which most producers write in reading order and which never scrambles two columns;
    /// the default.
    /// </summary>
    Content,

    /// <summary>Lines top to bottom and each line left to right, for scanned or messy files whose content order is not reading order.</summary>
    Position,
}

/// <summary>How <see cref="PdfPage.GetText(PdfTextOptions?)"/> builds words and lines.</summary>
public sealed record PdfTextOptions
{
    /// <summary>The default options: content order, gaps over 0.3 of a space make a word break.</summary>
    public static PdfTextOptions Default { get; } = new();

    /// <summary>Gets the order of lines and words. Defaults to <see cref="PdfTextOrder.Content"/>.</summary>
    public PdfTextOrder Order { get; init; }

    /// <summary>
    /// Gets how wide a gap between two glyphs must be to break a word, as a fraction of the font's space width. Defaults to 0.3.
    /// </summary>
    public double WordGap { get; init; } = 0.3;
}

/// <summary>
/// One glyph of a page's text (Phase 3 design Q10): the text it stands for, its box in the page's default user space, its
/// font, size and fill colour. A span with <c>/ActualText</c> gives one glyph for the whole span, holding that text.
/// </summary>
/// <param name="Text">
/// The text: from the font's ToUnicode map, else its glyph name through the Adobe Glyph List, else a private-use character
/// (U+E000 plus the code) that a warning counts.
/// </param>
/// <param name="Box">The glyph's box (its advance by the font's ascent and descent), the smallest upright rectangle around it.</param>
/// <param name="Font">The font, or <see langword="null"/> when the content shows text with no font selected.</param>
/// <param name="FontSize">The font size in user space, the scale of the text matrix and the current transformation included.</param>
/// <param name="Color">The fill colour.</param>
/// <param name="Code">The character code shown, or -1 for an <c>/ActualText</c> span.</param>
public sealed record PdfTextGlyph(string Text, PdfRectangle Box, PdfFont? Font, double FontSize, PdfColor Color, int Code)
{
    /// <summary>The baseline's start in default user space.</summary>
    public PdfPoint Origin { get; init; }

    /// <summary>The baseline's end: the origin moved by the glyph's advance.</summary>
    public PdfPoint End { get; init; }

    /// <summary>Whether the glyph is a space in the text (shown by the content, not inserted for a gap).</summary>
    public bool IsSpace => Text.Length > 0 && Text.All(char.IsWhiteSpace);
}

/// <summary>A word: glyphs with no space and no wide gap between them.</summary>
public sealed class PdfTextWord
{
    internal PdfTextWord(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        Glyphs = glyphs;
        Text = string.Concat(glyphs.Select(g => g.Text));
        Box = Union(glyphs.Select(g => g.Box));
    }

    /// <summary>The word's glyphs, in order.</summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }

    /// <summary>The word's text.</summary>
    public string Text { get; }

    /// <summary>The box around the word's glyphs.</summary>
    public PdfRectangle Box { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }

    internal static PdfRectangle Union(IEnumerable<PdfRectangle> boxes)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (PdfRectangle box in boxes)
        {
            left = Math.Min(left, box.Left);
            bottom = Math.Min(bottom, box.Bottom);
            right = Math.Max(right, box.Right);
            top = Math.Max(top, box.Top);
        }

        return left <= right ? new PdfRectangle(left, bottom, right, top) : default;
    }
}

/// <summary>A line: words on one baseline, in one direction.</summary>
public sealed class PdfTextLine
{
    internal PdfTextLine(IReadOnlyList<PdfTextWord> words)
    {
        Words = words;
        Text = string.Join(' ', words.Select(w => w.Text));
        Box = PdfTextWord.Union(words.Select(w => w.Box));
    }

    /// <summary>The line's words, in order.</summary>
    public IReadOnlyList<PdfTextWord> Words { get; }

    /// <summary>The line's text: its words joined by single spaces.</summary>
    public string Text { get; }

    /// <summary>The box around the line's words.</summary>
    public PdfRectangle Box { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>A page's text (Phase 3 design Q10): every glyph shown, built into words and lines.</summary>
public sealed class PdfTextPage
{
    internal PdfTextPage(IReadOnlyList<PdfTextGlyph> glyphs, IReadOnlyList<PdfTextLine> lines, IReadOnlyList<PdfWarning> warnings)
    {
        Glyphs = glyphs;
        Lines = lines;
        Warnings = warnings;
    }

    /// <summary>Every glyph shown, in content order, spaces included.</summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }

    /// <summary>The lines, in the order the options asked for.</summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }

    /// <summary>What could not be read, such as glyphs with no Unicode text.</summary>
    public IReadOnlyList<PdfWarning> Warnings { get; }

    /// <summary>The page's text: lines joined with newlines.</summary>
    public override string ToString()
    {
        StringBuilder text = new();
        foreach (PdfTextLine line in Lines)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(line.Text);
        }

        return text.ToString();
    }
}
