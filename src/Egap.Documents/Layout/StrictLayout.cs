using System.Globalization;

using Egami.Fonts;
using Egami.Fonts.Layout;

namespace Egap.Documents.Layout;

/// <summary>Why a strict layout failed.</summary>
public enum LayoutProblem
{
    /// <summary>Text runs past the right edge of its column, as an unbreakable word or number too long for it does.</summary>
    TextOverflow,

    /// <summary>An object, table, row or paragraph runs past the edge of its page area.</summary>
    ContentOverflow,

    /// <summary>A character has no glyph in any font the layout could use, so it would show as an empty box.</summary>
    MissingGlyph,

    /// <summary>A font the document names was found nowhere and has no stand-in, so the last resort would be used.</summary>
    MissingFont,
}

/// <summary>
/// Thrown by a strict layout (<see cref="LayoutOptions.Strict"/>) when content cannot be laid out as written; names the node
/// and the page, so the code or template that made it can be fixed.
/// </summary>
public sealed class LayoutException : DocumentException
{
    internal LayoutException(LayoutProblem problem, string message, Node? node, int? pageIndex)
        : base(message)
    {
        Problem = problem;
        Node = node;
        PageIndex = pageIndex;
    }

    /// <summary>Gets what went wrong.</summary>
    public LayoutProblem Problem { get; }

    /// <summary>Gets the node that could not be laid out, such as the paragraph, table or picture; null when no one node is to blame.</summary>
    public Node? Node { get; }

    /// <summary>Gets the 0-based index of the page it is on, when it was laid out.</summary>
    public int? PageIndex { get; }
}

/// <summary>The checks a strict layout makes once the pages are built.</summary>
internal static class StrictLayout
{
    /// <summary>How far, in points, content may stick out before it counts: rounding in widths and line heights.</summary>
    private const double Tolerance = 0.5;

    public static void Check(DocumentLayout layout, FontProvider fonts)
    {
        foreach (string family in fonts.LastResortFamilies)
        {
            Run? user = layout.Document.Descendants().OfType<Run>().FirstOrDefault(r => string.Equals(r.GetEffectiveFormat().FontAscii, family, StringComparison.OrdinalIgnoreCase));
            throw new LayoutException(LayoutProblem.MissingFont, $"The font '{family}' was not found and has no stand-in; embed it, add it to FontSettings.Collections, or name a font Egap has.", (Node?)user?.Ancestors().OfType<Paragraph>().FirstOrDefault() ?? user, null);
        }

        foreach (PageLayout page in layout.Pages)
        {
            foreach (AreaBox area in page.Boxes.OfType<AreaBox>())
            {
                foreach (LayoutBox box in area.Descendants())
                {
                    switch (box)
                    {
                        case LineBox line:
                            CheckLine(line, page);
                            break;
                        case ObjectBox { IsFloating: true }:
                            break;
                        case ObjectBox or TableBox or RowBox or ParagraphBox when area.Area == PageArea.Body:
                            if (box.Bounds.Right > area.Bounds.Right + Tolerance || box.Bounds.Bottom > area.Bounds.Bottom + Tolerance)
                            {
                                throw new LayoutException(LayoutProblem.ContentOverflow,
                                    $"{What(box.Node)} on page {page.Index + 1} runs {Over(box.Bounds, area.Bounds)} past the body; make it smaller, let it break, or give it room.",
                                    box.Node, page.Index);
                            }

                            break;
                    }
                }
            }
        }
    }

    private static void CheckLine(LineBox line, PageLayout page)
    {
        TextLine text = line.Line;
        if (text.Left + text.Width > line.Bounds.Width + Tolerance)
        {
            throw new LayoutException(LayoutProblem.TextOverflow,
                $"A line of {What(line.Node)} on page {page.Index + 1} is {(text.Left + text.Width - line.Bounds.Width).ToString("0.#", CultureInfo.InvariantCulture)} pt wider than its column; let words break (ParagraphFormat.WordBreak) or shorten it.",
                line.Node, page.Index);
        }

        foreach (LayoutRun run in text.Runs)
        {
            if (run.Glyphs is not GlyphRun glyphs || glyphs.Text is not { } source)
            {
                continue;
            }

            ReadOnlySpan<ushort> ids = glyphs.Glyphs;
            ReadOnlySpan<int> clusters = glyphs.Clusters;
            for (int g = 0; g < ids.Length && g < clusters.Length; g++)
            {
                if (ids[g] != 0 || clusters[g] < 0 || clusters[g] >= source.Length)
                {
                    continue;
                }

                int code = char.ConvertToUtf32Safe(source, clusters[g]);
                if (code < 0x20 || char.IsWhiteSpace(source, clusters[g]) || CharUnicodeInfo.GetUnicodeCategory(code) is UnicodeCategory.Format or UnicodeCategory.Control)
                {
                    continue;
                }

                throw new LayoutException(LayoutProblem.MissingGlyph,
                    $"U+{code:X4} in {What(line.Node)} on page {page.Index + 1} has no glyph in any font; use a font that has it, or add one to FontSettings.Collections.",
                    line.Node, page.Index);
            }
        }
    }

    private static string What(Node? node)
    {
        string kind = node switch
        {
            Paragraph => "the paragraph",
            Table => "the table",
            Row => "a table row",
            Picture => "the picture",
            Shape => "the shape",
            null => "content",
            _ => "the " + node.GetType().Name.ToLowerInvariant(),
        };
        string text = node is Paragraph or Row or Table ? node.GetText().Trim() : string.Empty;
        return text.Length == 0 ? kind : $"{kind} \"{(text.Length > 40 ? text[..40] + "..." : text)}\"";
    }

    private static string Over(LayoutRect box, LayoutRect area)
    {
        double right = box.Right - area.Right, bottom = box.Bottom - area.Bottom;
        return right > bottom
            ? $"{right.ToString("0.#", CultureInfo.InvariantCulture)} pt"
            : $"{bottom.ToString("0.#", CultureInfo.InvariantCulture)} pt";
    }
}

file static class CharExtensions
{
    extension(char)
    {
        /// <summary>The code point at an index, joining a surrogate pair; the lone code unit when it is not one.</summary>
        public static int ConvertToUtf32Safe(string text, int index)
        {
            return char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(text[index], text[index + 1])
                : text[index];
        }
    }
}
