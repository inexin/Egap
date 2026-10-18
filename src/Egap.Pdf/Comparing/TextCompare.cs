namespace Egap.Pdf.Comparing;

/// <summary>
/// The text pass of compare (Phase 7 design Q12, Build Plan task 14): the words of both documents in reading order, one
/// word-level Myers diff over the whole of each, so text that moved to the next page is no change; runs of edits between
/// equal words become insertions, deletions and replacements, and a deleted run that comes back elsewhere unchanged is a
/// move. Pages are matched by the equal words they share, and pages between two matches that share none are paired in order.
/// </summary>
internal static class TextCompare
{
    /// <summary>A deleted run must have at least this many words to be found again as a move.</summary>
    internal const int MinMovedWords = 3;

    /// <summary>Every word of every page, in content order; a page that cannot be read gives none.</summary>
    public static List<PdfComparedWord> Words(PdfDocument document, CancellationToken cancellationToken = default)
    {
        List<PdfComparedWord> words = [];
        for (int i = 0; i < document.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (PdfTextLine line in document.Pages[i].GetText().Lines)
                {
                    foreach (PdfTextWord word in line.Words)
                    {
                        Split(word, i + 1, words);
                    }
                }
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                // A page whose text cannot be read is compared by its pixels only.
            }
        }

        return words;
    }

    /// <summary>
    /// A word as text extraction gives it, cut at the spaces it may hold (justified text can join words whose gap is narrow,
    /// and an <c>/ActualText</c> span is one glyph for many words), each part with the box of its own glyphs.
    /// </summary>
    private static void Split(PdfTextWord word, int page, List<PdfComparedWord> words)
    {
        System.Text.StringBuilder text = new();
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Flush()
        {
            if (text.Length > 0)
            {
                words.Add(new PdfComparedWord(page, text.ToString(), new PdfRectangle(x0, y0, x1, y1)));
            }

            text.Clear();
            (x0, y0, x1, y1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        }

        foreach (PdfTextGlyph glyph in word.Glyphs)
        {
            // A span with /ActualText is one glyph holding all its text: its words share the span's box.
            PdfRectangle b = glyph.Box.Normalize();
            string[] parts = glyph.Text.Split((char[]?)null);
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    Flush();
                }

                if (parts[p].Length > 0)
                {
                    text.Append(parts[p]);
                    (x0, y0, x1, y1) = (Math.Min(x0, b.Left), Math.Min(y0, b.Bottom), Math.Max(x1, b.Right), Math.Max(y1, b.Top));
                }
            }
        }

        Flush();
    }

    /// <summary>The text changes; <paramref name="equal"/> gets every pair of equal words, old index then new.</summary>
    public static List<PdfTextChange> Changes(List<PdfComparedWord> oldWords, List<PdfComparedWord> newWords, List<(int Old, int New)> equal)
    {
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        int Id(string word) => ids.TryGetValue(word, out int id) ? id : ids[word] = ids.Count;
        int[] a = [.. oldWords.Select(w => Id(w.Text))], b = [.. newWords.Select(w => Id(w.Text))];
        List<PdfTextChange> changes = [];
        int i = 0, j = 0;
        List<PdfComparedWord> deleted = [], inserted = [];
        void Flush()
        {
            if (deleted.Count == 0 && inserted.Count == 0)
            {
                return;
            }

            PdfTextChangeKind kind = deleted.Count == 0 ? PdfTextChangeKind.Inserted : inserted.Count == 0 ? PdfTextChangeKind.Deleted : PdfTextChangeKind.Replaced;
            PdfComparedWord? anchor = kind == PdfTextChangeKind.Deleted && newWords.Count > 0 ? newWords[Math.Min(j, newWords.Count - 1)] : null;
            changes.Add(new PdfTextChange(kind, [.. deleted], [.. inserted], anchor));
            deleted.Clear();
            inserted.Clear();
        }

        foreach ((WordDiff.Step step, int count) in WordDiff.Diff(a, b))
        {
            switch (step)
            {
                case WordDiff.Step.Equal:
                    Flush();
                    for (int k = 0; k < count; k++)
                    {
                        equal.Add((i++, j++));
                    }

                    break;
                case WordDiff.Step.Delete:
                    deleted.AddRange(oldWords.Skip(i).Take(count));
                    i += count;
                    break;
                default:
                    inserted.AddRange(newWords.Skip(j).Take(count));
                    j += count;
                    break;
            }
        }

        Flush();
        return Moves(changes);
    }

    /// <summary>A deletion whose words an insertion brings back unchanged becomes one move, placed where the words went.</summary>
    private static List<PdfTextChange> Moves(List<PdfTextChange> changes)
    {
        List<PdfTextChange> result = [.. changes];
        foreach (PdfTextChange deletion in changes.Where(c => c.Kind == PdfTextChangeKind.Deleted && c.OldWords.Count >= MinMovedWords))
        {
            PdfTextChange? insertion = result.FirstOrDefault(c => c.Kind == PdfTextChangeKind.Inserted && c.NewText == deletion.OldText);
            if (insertion is null || !result.Contains(deletion))
            {
                continue;
            }

            int at = result.IndexOf(insertion);
            result[at] = new PdfTextChange(PdfTextChangeKind.Moved, deletion.OldWords, insertion.NewWords, null);
            result.Remove(deletion);
        }

        return result;
    }

    /// <summary>The page matches, in the new document's order, removed pages where the old order puts them.</summary>
    public static IReadOnlyList<PdfPageMatch> Match(int oldPages, int newPages, List<(int Old, int New)> equal, List<PdfComparedWord> oldWords, List<PdfComparedWord> newWords)
    {
        Dictionary<(int Old, int New), int> shared = [];
        foreach ((int o, int n) in equal)
        {
            (int, int) key = (oldWords[o].Page, newWords[n].Page);
            shared[key] = shared.GetValueOrDefault(key) + 1;
        }

        int?[] oldOf = new int?[newPages + 1];
        bool[] oldTaken = new bool[oldPages + 1];
        foreach (((int o, int n), _) in shared.OrderByDescending(s => s.Value).ThenBy(s => s.Key.New))
        {
            if (oldOf[n] is null && !oldTaken[o])
            {
                oldOf[n] = o;
                oldTaken[o] = true;
            }
        }

        // Between two matched pages, pages that share no words are paired in order.
        List<(int Old, int New)> anchors = [(0, 0), .. Enumerable.Range(1, newPages).Where(n => oldOf[n] is not null).Select(n => (oldOf[n]!.Value, n)), (oldPages + 1, newPages + 1)];
        for (int k = 0; k + 1 < anchors.Count; k++)
        {
            (int o0, int n0) = anchors[k];
            (int o1, int n1) = anchors[k + 1];
            if (o1 <= o0)
            {
                continue;
            }

            List<int> olds = [.. Enumerable.Range(o0 + 1, Math.Max(0, o1 - o0 - 1)).Where(o => !oldTaken[o])];
            List<int> news = [.. Enumerable.Range(n0 + 1, Math.Max(0, n1 - n0 - 1)).Where(n => oldOf[n] is null)];
            for (int p = 0; p < Math.Min(olds.Count, news.Count); p++)
            {
                oldOf[news[p]] = olds[p];
                oldTaken[olds[p]] = true;
            }
        }

        List<PdfPageMatch> matches = [];
        int nextOld = 1;
        void RemovedUpTo(int old)
        {
            for (; nextOld < old; nextOld++)
            {
                if (!oldTaken[nextOld])
                {
                    matches.Add(new PdfPageMatch(nextOld, null));
                }
            }
        }

        for (int n = 1; n <= newPages; n++)
        {
            if (oldOf[n] is int o)
            {
                RemovedUpTo(o);
                nextOld = Math.Max(nextOld, o + 1);
            }

            matches.Add(new PdfPageMatch(oldOf[n], n));
        }

        RemovedUpTo(oldPages + 1);
        return matches;
    }
}
