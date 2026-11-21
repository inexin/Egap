namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// Makes a revised copy of a document with seeded random edits of the kinds people make: words inserted, deleted and
/// replaced, paragraphs split, joined, added, removed and moved, table rows added and dropped, and with
/// <paramref name="formatting"/> also words reformatted (bold, size, a paragraph's alignment) and words put in capitals.
/// </summary>
internal sealed class EditMutator(int seed, bool formatting = false)
{
    private static readonly string[] Vocabulary =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima",
        "the", "of", "and", "contract", "party", "shall", "within", "days", "notice", "agreement", "section", "term",
    ];

    private readonly Random random = new(seed);

    /// <summary>A copy of <paramref name="document"/> with <paramref name="edits"/> random edits.</summary>
    public Document Mutate(Document document, int edits)
    {
        Document copy = document.Clone();
        for (int e = 0; e < edits; e++)
        {
            List<Paragraph> paragraphs = [.. copy.Sections.SelectMany(s => s.Body.Descendants<Paragraph>()).Where(p => p.Parent is Body or Cell)];
            if (paragraphs.Count == 0)
            {
                if (copy.Sections.Count > 0)
                {
                    copy.Sections[0].Body.AddParagraph(Words(3));
                }

                continue;
            }

            Paragraph paragraph = paragraphs[random.Next(paragraphs.Count)];
            switch (random.Next(formatting ? 13 : 10))
            {
                case 0:
                case 1:
                    InsertWords(paragraph);
                    break;
                case 2:
                case 3:
                    DeleteWords(paragraph);
                    break;
                case 4:
                    DeleteWords(paragraph);
                    InsertWords(paragraph);
                    break;
                case 5:
                    Split(paragraph);
                    break;
                case 6:
                    Join(paragraph);
                    break;
                case 7:
                    AddParagraph(paragraph);
                    break;
                case 8:
                    RemoveOrMove(paragraph, paragraphs);
                    break;
                case 9:
                    ChangeRows(paragraph);
                    break;
                case 10:
                    Reformat(paragraph);
                    break;
                case 11:
                    ChangeCase(paragraph);
                    break;
                default:
                    paragraph.Format.Alignment = paragraph.GetEffectiveFormat().Alignment == ParagraphAlignment.Center ? ParagraphAlignment.Right : ParagraphAlignment.Center;
                    break;
            }
        }

        return copy;
    }

    private string Words(int count)
    {
        return string.Join(" ", Enumerable.Range(0, count).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]));
    }

    private Run? PickRun(Paragraph paragraph)
    {
        List<Run> runs = [.. paragraph.Runs.Where(r => r.Text.Length > 0)];
        return runs.Count == 0 ? null : runs[random.Next(runs.Count)];
    }

    private void InsertWords(Paragraph paragraph)
    {
        Run? run = PickRun(paragraph);
        if (run is null)
        {
            paragraph.AddRun(Words(1 + random.Next(3)));
            return;
        }

        int at = WordBoundary(run.Text);
        run.Text = run.Text[..at] + " " + Words(1 + random.Next(3)) + " " + run.Text[at..];
    }

    private void DeleteWords(Paragraph paragraph)
    {
        if (PickRun(paragraph) is not Run run)
        {
            return;
        }

        int start = WordBoundary(run.Text);
        int end = Math.Min(run.Text.Length, start + 1 + random.Next(Math.Max(1, Math.Min(40, run.Text.Length - start))));
        run.Text = run.Text[..start] + run.Text[end..];
    }

    private int WordBoundary(string text)
    {
        int at = random.Next(text.Length + 1);
        while (at < text.Length && at > 0 && !char.IsWhiteSpace(text[at - 1]))
        {
            at++;
        }

        return at;
    }

    private void Split(Paragraph paragraph)
    {
        if (PickRun(paragraph) is not Run run)
        {
            return;
        }

        Paragraph next = Like(paragraph);
        paragraph.ParentCollection().Insert(paragraph.ParentCollection().IndexOf(paragraph) + 1, next);
        int at = random.Next(run.Text.Length + 1);
        Run tail = run.Clone();
        tail.Text = run.Text[at..];
        run.Text = run.Text[..at];
        next.Inlines.Add(tail);
        int index = paragraph.Inlines.IndexOf(run);
        while (paragraph.Inlines.Count > index + 1)
        {
            Inline moved = paragraph.Inlines[index + 1];
            moved.Remove();
            next.Inlines.Add(moved);
        }
    }

    /// <summary>A new, empty paragraph with the style and direct formatting of <paramref name="paragraph"/>.</summary>
    private static Paragraph Like(Paragraph paragraph)
    {
        Paragraph like = new(paragraph.Document) { Style = paragraph.Style };
        like.Format.CopyRaw(paragraph.Format);
        return like;
    }

    private static void Join(Paragraph paragraph)
    {
        if (paragraph.NextSibling is not Paragraph next)
        {
            return;
        }

        foreach (Inline inline in next.Inlines.ToList())
        {
            inline.Remove();
            paragraph.Inlines.Add(inline);
        }

        next.Remove();
    }

    private void AddParagraph(Paragraph paragraph)
    {
        Paragraph added = Like(paragraph);
        NodeCollection<Block> blocks = paragraph.ParentCollection();
        blocks.Insert(blocks.IndexOf(paragraph) + random.Next(2), added);
        added.AddRun(Words(2 + random.Next(8)));
    }

    private void RemoveOrMove(Paragraph paragraph, List<Paragraph> paragraphs)
    {
        NodeCollection<Block> blocks = paragraph.ParentCollection();
        if (blocks.Count < 2)
        {
            return;
        }

        paragraph.Remove();
        if (random.Next(2) == 0 && paragraph.Parent is null)
        {
            Paragraph target = paragraphs[random.Next(paragraphs.Count)];
            if (target != paragraph && target.Parent is not null)
            {
                NodeCollection<Block> into = target.ParentCollection();
                into.Insert(into.IndexOf(target), paragraph);
            }
        }
    }

    /// <summary>Splits off a stretch of a run, at word boundaries, and makes it bold (or not) or another size.</summary>
    private void Reformat(Paragraph paragraph)
    {
        if (PickRun(paragraph) is not Run run)
        {
            return;
        }

        int start = WordBoundary(run.Text);
        int end = start;
        while (end < run.Text.Length && !char.IsWhiteSpace(run.Text[end]))
        {
            end++;
        }

        if (end == start)
        {
            return;
        }

        int index = paragraph.Inlines.IndexOf(run);
        Run middle = run.Clone(), tail = run.Clone();
        middle.Text = run.Text[start..end];
        tail.Text = run.Text[end..];
        run.Text = run.Text[..start];
        paragraph.Inlines.Insert(index + 1, middle);
        if (tail.Text.Length > 0)
        {
            paragraph.Inlines.Insert(index + 2, tail);
        }

        if (random.Next(2) == 0)
        {
            middle.Format.Bold = !middle.GetEffectiveFormat().Bold;
        }
        else
        {
            middle.Format.Size = middle.GetEffectiveFormat().Size + 2;
        }
    }

    /// <summary>Puts a word of a run in capitals.</summary>
    private void ChangeCase(Paragraph paragraph)
    {
        if (PickRun(paragraph) is not Run run)
        {
            return;
        }

        int start = WordBoundary(run.Text);
        int end = start;
        while (end < run.Text.Length && !char.IsWhiteSpace(run.Text[end]))
        {
            end++;
        }

        run.Text = run.Text[..start] + run.Text[start..end].ToUpperInvariant() + run.Text[end..];
    }

    private void ChangeRows(Paragraph paragraph)
    {
        if (paragraph.GetAncestor<Table>() is not Table table || table.Rows.Count == 0)
        {
            InsertWords(paragraph);
            return;
        }

        Row row = table.Rows[random.Next(table.Rows.Count)];
        if (random.Next(2) == 0 && table.Rows.Count > 1)
        {
            row.Remove();
        }
        else
        {
            // Edited once in the tree, so the copy counts as changed (a detached copy keeps its source's file markup).
            Row copy = row.Clone();
            table.Rows.Insert(table.Rows.IndexOf(row) + 1, copy);
            foreach (Run run in copy.Descendants<Run>())
            {
                run.Text = Words(1 + random.Next(3));
            }

            foreach (Paragraph cellParagraph in copy.Descendants<Paragraph>())
            {
                cellParagraph.AddRun(Words(1));
            }
        }
    }
}

/// <summary>Helpers for the mutator.</summary>
internal static class BlockExtensions
{
    /// <summary>The block list a paragraph is in.</summary>
    public static NodeCollection<Block> ParentCollection(this Paragraph paragraph)
    {
        return paragraph.Parent switch
        {
            BlockContainer container => container.Blocks,
            ContentControl control => control.Blocks,
            _ => throw new InvalidOperationException("The paragraph is not in a block list."),
        };
    }
}
