using System.Text;

using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// The comparison's promise, checked without an oracle: accepting every change of the result gives the revised document and
/// rejecting every change gives the original, block by block (paragraph text and the shape of tables and containers).
/// </summary>
internal static class RoundTrip
{
    /// <summary>Compares the two documents and checks the result both ways; returns the comparison.</summary>
    public static DocumentComparison Check(Document original, Document revised, DocumentCompareOptions? options = null)
    {
        DocumentComparison comparison = DocumentCompare.Run(original, revised, options);
        Check(original, revised, comparison);
        return comparison;
    }

    /// <summary>Checks that <paramref name="comparison"/> accepts to <paramref name="revised"/> and rejects to <paramref name="original"/>.</summary>
    public static void Check(Document original, Document revised, DocumentComparison comparison)
    {
        Check(original, revised, comparison.Document);
    }

    /// <summary>Checks that the tracked changes of <paramref name="result"/> accept to <paramref name="revised"/> and reject to <paramref name="original"/>.</summary>
    public static void Check(Document original, Document revised, Document result)
    {
        Document accepted = result.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = result.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(Blocks(revised), Blocks(accepted));
        Assert.Equal(Blocks(original), Blocks(rejected));
    }

    /// <summary>A document's blocks in order, every section's body as one run, each as one line of text.</summary>
    public static List<string> Blocks(Document document)
    {
        if (document.Revisions.Count > 0)
        {
            document = document.Clone();
            document.Revisions.AcceptAll();
        }

        List<string> lines = [];
        foreach (Section section in document.Sections)
        {
            foreach (Block block in section.Body.Blocks)
            {
                lines.Add(Describe(block));
            }
        }

        return lines;
    }

    /// <summary>
    /// Compares the two documents and checks the result both ways by text and by look: each paragraph's style and resolved
    /// formatting, and each stretch of text with its resolved run formatting.
    /// </summary>
    public static DocumentComparison CheckFormatting(Document original, Document revised, DocumentCompareOptions? options = null)
    {
        DocumentComparison comparison = Check(original, revised, options);
        Document accepted = comparison.Document.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = comparison.Document.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(Looks(revised), Looks(accepted));
        Assert.Equal(Looks(original), Looks(rejected));
        return comparison;
    }

    /// <summary>Every paragraph of a document as its style, look and runs with their looks, in document order.</summary>
    public static List<string> Looks(Document document)
    {
        if (document.Revisions.Count > 0)
        {
            document = document.Clone();
            document.Revisions.AcceptAll();
        }

        List<string> lines = [];
        // Paragraphs of the body and its tables and containers; not those inside inlines (comments, notes, text boxes).
        foreach (Paragraph paragraph in document.Sections.SelectMany(s => s.Body.Descendants<Paragraph>()).Where(p => p.GetAncestor<Inline>() is null))
        {
            ResolvedParagraphFormat f = paragraph.GetEffectiveFormat();
            StringBuilder line = new($"{paragraph.Style?.Id}|{f.Alignment}|{f.LeftIndent}|{f.SpaceAfter}|{f.OutlineLevel}:");
            string? last = null;
            foreach (Run run in paragraph.Descendants<Run>())
            {
                if (run.Text.Length == 0 || run.GetAncestor<Paragraph>() != paragraph)
                {
                    continue;
                }

                ResolvedRunFormat r = run.GetEffectiveFormat();
                string look = $"[{run.Style?.Id}|{r.Bold}|{r.Italic}|{r.Underline}|{r.Size}|{r.Color}|{r.FontAscii}]";
                if (look != last)
                {
                    line.Append(look);
                    last = look;
                }

                line.Append(run.Text);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>
    /// Compares the two documents and checks the result both ways in the other stories too: headers and footers, notes and
    /// text boxes (accepting gives the revised text, rejecting the original); and that the result has the revised
    /// document's comments.
    /// </summary>
    public static DocumentComparison CheckStories(Document original, Document revised, DocumentCompareOptions? options = null)
    {
        DocumentComparison comparison = Check(original, revised, options);
        Document accepted = comparison.Document.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = comparison.Document.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(Stories(revised), Stories(accepted));
        Assert.Equal(Stories(original), Stories(rejected));
        Assert.Equal(Comments(revised), Comments(accepted));
        return comparison;
    }

    /// <summary>The text of every header and footer (by section and kind), note and text box, in order; empty ones left out.</summary>
    public static List<string> Stories(Document document)
    {
        if (document.Revisions.Count > 0)
        {
            document = document.Clone();
            document.Revisions.AcceptAll();
        }

        List<string> lines = [];
        for (int k = 0; k < document.Sections.Count; k++)
        {
            foreach (HeaderFooter story in document.Sections[k].HeadersFooters.OrderBy(h => h.Kind))
            {
                if (story.GetText().Trim(ControlChar.ParagraphBreak, ' ').Length > 0)
                {
                    lines.Add($"{k}:{story.Kind}|{story.GetText()}");
                }
            }
        }

        foreach (Section section in document.Sections)
        {
            foreach (Inline inline in section.Body.Descendants<Inline>())
            {
                switch (inline)
                {
                    case Footnote note:
                        lines.Add($"{note.Kind}|{note.Story.GetText()}");
                        break;
                    case Shape { TextBox: { } box }:
                        lines.Add($"TextBox|{box.GetText()}");
                        break;
                }
            }
        }

        return lines;
    }

    /// <summary>Every comment as its anchor text and its own text, sorted.</summary>
    public static List<string> Comments(Document document)
    {
        Dictionary<int, System.Text.StringBuilder> open = [];
        Dictionary<int, string> anchors = [];
        List<string> comments = [];
        foreach (Inline inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case CommentRangeStart start:
                    open[start.CommentId] = new();
                    break;
                case CommentRangeEnd end when open.Remove(end.CommentId, out var text):
                    anchors[end.CommentId] = text.ToString();
                    break;
                case Run run when run.GetAncestor<Comment>() is null && run.Revision is not { Removes: true }:
                    foreach (System.Text.StringBuilder text in open.Values)
                    {
                        text.Append(run.Text);
                    }

                    break;
            }
        }

        foreach (Comment comment in document.Descendants<Comment>())
        {
            comments.Add((anchors.TryGetValue(comment.Id, out string? anchor) ? anchor : "") + "|" + comment.Story.GetText());
        }

        comments.Sort(StringComparer.Ordinal);
        return comments;
    }

    private static string Describe(Block block)
    {
        return block switch
        {
            // Comments are not compared yet, so their reference marks are left out of the text.
            Paragraph paragraph => "P|" + paragraph.GetText().Replace(ControlChar.CommentReference.ToString(), "", StringComparison.Ordinal),
            Table table => "T[" + string.Join(" ", table.Rows.Select(row => "R(" + string.Join(" ", row.Cells.Select(cell => "C{" + List(cell.Blocks) + "}")) + ")")) + "]",
            ContentControl control => "CC{" + List(control.Blocks) + "}",
            FlexContainer flex => "FLEX{" + string.Join(" ", flex.Items.Select(item => "{" + List(item.Blocks) + "}")) + "}",
            GridContainer grid => "GRID{" + string.Join(" ", grid.Items.Select(item => "{" + List(item.Blocks) + "}")) + "}",
            _ => block.GetType().Name + "|" + block.GetText(),
        };
    }

    private static string List(NodeCollection<Block> blocks)
    {
        StringBuilder text = new();
        foreach (Block block in blocks)
        {
            text.Append(Describe(block)).Append(" / ");
        }

        return text.ToString();
    }
}
