using System.Text;

using Egap.Documents.Comparing;

namespace Egap.Documents.Fuzz;

/// <summary>
/// The <c>compare</c> target (compare design Q9): any bytes as two Markdown documents, split at the first form feed; with
/// none, the second is the first with edits chosen from the bytes. The two are compared with three sets of options, and
/// the result must accept to the revised document and reject to the original, block by block; written as DOCX and read
/// again, it must still do so, unless the comparison says some content could not be tracked. Nothing may throw.
/// </summary>
internal static class CompareTarget
{
    private static readonly DocumentCompareOptions[] Options =
    [
        new(),
        new() { Base = CompareBase.Original },
        new() { Granularity = CompareGranularity.Character },
    ];

    public static void Run(Stream input)
    {
        using StreamReader reader = new(input);
        string text = reader.ReadToEnd();
        int split = text.IndexOf('\f', StringComparison.Ordinal);
        Document original = MarkdownFormat.Parse(split >= 0 ? text[..split] : text);
        Document revised = split >= 0 ? MarkdownFormat.Parse(text[(split + 1)..]) : Edit(original, text);
        foreach (DocumentCompareOptions options in Options)
        {
            DocumentComparison comparison = DocumentCompare.Run(original, revised, options);
            if (Tracked(comparison))
            {
                Check(original, revised, comparison.Document, "result");
            }

            _ = comparison.ToString();
            foreach (DocumentChange change in comparison.Changes)
            {
                _ = change.ToString();
            }
        }

        // Through DOCX, against the inputs through DOCX too: DOCX cannot hold every character Markdown can.
        DocumentComparison forward = DocumentCompare.Run(original, revised);
        if (Tracked(forward))
        {
            Check(ThroughDocx(original), ThroughDocx(revised), ThroughDocx(forward.Document), "DOCX");
        }

        if (!forward.HasChanges && !Blocks(original).SequenceEqual(Blocks(revised)))
        {
            throw new InvalidOperationException("HasChanges is false for documents whose text differs.");
        }
    }

    /// <summary>
    /// Whether every difference became a tracked change: content that cannot be one (a raw HTML block, say) is kept as in
    /// the base document with a <see cref="DocumentCompareWarningKind.ContentNotTracked"/> warning, so the round trip
    /// cannot hold.
    /// </summary>
    private static bool Tracked(DocumentComparison comparison)
    {
        return !comparison.Warnings.Any(w => w.Kind == DocumentCompareWarningKind.ContentNotTracked);
    }

    private static Document ThroughDocx(Document document)
    {
        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        docx.Position = 0;
        return DocxFormat.Load(docx);
    }

    /// <summary>A copy of <paramref name="document"/> with edits drawn from a hash of <paramref name="text"/>.</summary>
    private static Document Edit(Document document, string text)
    {
        Random random = new(string.GetHashCode(text, StringComparison.Ordinal) & 0x7FFFFFFF);
        Document copy = document.Clone();
        for (int e = 1 + random.Next(6); e > 0; e--)
        {
            List<Paragraph> paragraphs = [.. copy.Descendants<Paragraph>().Where(p => p.Parent is Body or Cell)];
            if (paragraphs.Count == 0)
            {
                if (copy.Sections.Count > 0)
                {
                    copy.Sections[0].Body.AddParagraph("new words here");
                }

                continue;
            }

            Paragraph paragraph = paragraphs[random.Next(paragraphs.Count)];
            switch (random.Next(5))
            {
                case 0:
                    paragraph.AddRun(" inserted words");
                    break;
                case 1 when paragraph.Inlines.Count > 0:
                    paragraph.Inlines[random.Next(paragraph.Inlines.Count)].Remove();
                    break;
                case 2 when paragraph.Parent is Body:
                    paragraph.Remove();
                    break;
                case 3 when paragraph.Parent is Body body:
                    body.Blocks.Insert(body.Blocks.IndexOf(paragraph), (Paragraph)paragraph.Clone());
                    break;
                default:
                    if (paragraph.Descendants<Run>().FirstOrDefault(r => r.Text.Length > 0) is { } run)
                    {
                        run.Text = run.Text[..random.Next(run.Text.Length)] + "changed" + run.Text[random.Next(run.Text.Length)..];
                    }

                    break;
            }
        }

        return copy;
    }

    /// <summary>Throws when <paramref name="result"/> does not accept to the revised document and reject to the original.</summary>
    private static void Check(Document original, Document revised, Document result, string what)
    {
        Document accepted = result.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = result.Clone();
        rejected.Revisions.RejectAll();
        Same(Blocks(revised), Blocks(accepted), what + " accepted");
        Same(Blocks(original), Blocks(rejected), what + " rejected");
    }

    private static void Same(List<string> expected, List<string> actual, string what)
    {
        if (!expected.SequenceEqual(actual))
        {
            int at = 0;
            while (at < Math.Min(expected.Count, actual.Count) && expected[at] == actual[at])
            {
                at++;
            }

            throw new InvalidOperationException(
                $"The {what} differs at block {at}: expected \"{(at < expected.Count ? expected[at] : "(end)")}\", got \"{(at < actual.Count ? actual[at] : "(end)")}\".");
        }
    }

    /// <summary>The body's blocks as text, tables by rows and cells, with existing changes accepted; empty paragraphs at the end left out.</summary>
    private static List<string> Blocks(Document document)
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

        // A story keeps a paragraph even when every change in it is undone, so empty paragraphs at the end do not count.
        while (lines.Count > 0 && lines[^1] == "P|")
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string Describe(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                return "P|" + paragraph.GetText().Replace(ControlChar.CommentReference.ToString(), "", StringComparison.Ordinal);
            case Table table:
                StringBuilder text = new("T[");
                foreach (Row row in table.Rows)
                {
                    text.Append("R(");
                    foreach (Cell cell in row.Cells)
                    {
                        text.Append("C{").AppendJoin(" / ", cell.Blocks.Select(Describe)).Append('}');
                    }

                    text.Append(')');
                }

                return text.Append(']').ToString();
            default:
                return block.GetType().Name + "|" + block.GetText();
        }
    }
}
