using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Pdf.Text;

/// <summary>
/// Searching a page's text: the lines <see cref="TextCollector"/> builds are grouped into blocks (lines that follow each
/// other closely, one under the next and overlapping across), each block is written as one string with single spaces
/// between words and lines, and every character of it points back to the glyph it came from, so a match turns into
/// glyphs and one quadrilateral per line.
/// </summary>
internal static class TextSearch
{
    /// <summary>The longest a literal pattern's regular expression may run on one block.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A regular expression for a literal phrase: white space matches any run of it.</summary>
    public static Regex Pattern(string text, PdfFindOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("The text to find is only white space.", nameof(text));
        }

        string pattern = string.Join(@"\s+", parts.Select(Regex.Escape));
        if (options.WholeWord)
        {
            pattern = @"(?<![\p{L}\p{N}_])" + pattern + @"(?![\p{L}\p{N}_])";
        }

        RegexOptions flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(pattern, flags, Timeout);
    }

    /// <summary>Every match on one page.</summary>
    public static List<PdfTextMatch> Find(PdfPage page, int pageIndex, Regex pattern, PdfFindOptions options)
    {
        PdfTextPage text = page.GetText(options.Text);
        List<PdfTextMatch> matches = [];
        foreach (Block block in Blocks(text.Lines))
        {
            for (Match m = pattern.Match(block.Text); m.Success; m = m.NextMatch())
            {
                if (m.Length > 0 && ToMatch(pageIndex, block, m.Index, m.Length) is { } match)
                {
                    matches.Add(match);
                }
            }
        }

        return matches;
    }

    /// <summary>The page's lines grouped into blocks, each written out with its map back to glyphs and lines.</summary>
    internal static List<Block> Blocks(IReadOnlyList<PdfTextLine> lines)
    {
        List<Block> blocks = [];
        StringBuilder text = new();
        List<PdfTextGlyph?> glyphs = [];
        List<int> lineOf = [];
        PdfTextLine? previous = null;
        for (int l = 0; l < lines.Count; l++)
        {
            PdfTextLine line = lines[l];
            if (previous is not null)
            {
                if (Follows(previous, line))
                {
                    Add(text, glyphs, lineOf, ' ', null, l);
                }
                else
                {
                    blocks.Add(new Block(text.ToString(), [.. glyphs], [.. lineOf]));
                    text.Clear();
                    glyphs.Clear();
                    lineOf.Clear();
                }
            }

            for (int w = 0; w < line.Words.Count; w++)
            {
                if (w > 0)
                {
                    Add(text, glyphs, lineOf, ' ', null, l);
                }

                foreach (PdfTextGlyph glyph in line.Words[w].Glyphs)
                {
                    foreach (char c in glyph.Text)
                    {
                        Add(text, glyphs, lineOf, c, glyph, l);
                    }
                }
            }

            previous = line;
        }

        if (text.Length > 0)
        {
            blocks.Add(new Block(text.ToString(), [.. glyphs], [.. lineOf]));
        }

        return blocks;
    }

    /// <summary>The glyphs and per-line quadrilaterals a match covers; null when it covers only inserted spaces.</summary>
    internal static PdfTextMatch? ToMatch(int pageIndex, Block block, int start, int length)
    {
        List<PdfTextGlyph> glyphs = [];
        List<PdfQuad> quads = [];
        List<PdfRectangle> lineBoxes = [];
        int line = -1;
        for (int i = start; i < start + length; i++)
        {
            if (block.Glyphs[i] is not { } glyph)
            {
                continue;
            }

            if (glyphs.Count > 0 && ReferenceEquals(glyphs[^1], glyph))
            {
                continue;    // the glyph stands for more than one character, such as a ligature
            }

            if (block.Lines[i] != line && lineBoxes.Count > 0)
            {
                quads.Add(PdfQuad.FromRectangle(PdfTextWord.Union(lineBoxes)));
                lineBoxes.Clear();
            }

            line = block.Lines[i];
            glyphs.Add(glyph);
            lineBoxes.Add(glyph.Box);
        }

        if (glyphs.Count == 0)
        {
            return null;
        }

        quads.Add(PdfQuad.FromRectangle(PdfTextWord.Union(lineBoxes)));
        string matched = Regex.Replace(block.Text.Substring(start, length), @"\s+", " ");
        return new PdfTextMatch(pageIndex, matched, glyphs, quads);
    }

    /// <summary>Whether a line continues the block the previous one is in: just under it, overlapping it across.</summary>
    private static bool Follows(PdfTextLine previous, PdfTextLine line)
    {
        PdfRectangle a = previous.Box, b = line.Box;
        double height = Math.Max(a.Top - a.Bottom, b.Top - b.Bottom);
        if (!(height > 0))
        {
            return false;
        }

        double gap = a.Bottom - b.Top;
        bool below = gap > -0.5 * height && gap < height;
        bool across = Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left);
        return below && across;
    }

    private static void Add(StringBuilder text, List<PdfTextGlyph?> glyphs, List<int> lineOf, char c, PdfTextGlyph? glyph, int line)
    {
        text.Append(c);
        glyphs.Add(glyph);
        lineOf.Add(line);
    }

    /// <summary>A block's text and, for each of its characters, the glyph it came from (null for an inserted space) and its line.</summary>
    internal sealed record Block(string Text, PdfTextGlyph?[] Glyphs, int[] Lines);
}
