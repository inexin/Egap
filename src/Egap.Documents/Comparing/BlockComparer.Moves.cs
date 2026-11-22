using System.Globalization;
using System.Text.RegularExpressions;

namespace Egap.Documents.Comparing;

/// <summary>Moves inside the text: a run of deleted words that comes back, the same, among the inserted words.</summary>
internal sealed partial class BlockComparer
{
    /// <summary>The most changed words on either side for which moved text is looked for; past it the cost is not worth it.</summary>
    private const int MostMoveWords = 200_000;

    /// <summary>
    /// Finds moved sentences and other runs of words: deleted text of at least <see cref="MoveWords"/> words that is inserted,
    /// the same, elsewhere (in the same paragraph or another) becomes a move, its two sides tracked as the source and the
    /// destination with one name, as Word marks them. The rest of each change stays as it was.
    /// </summary>
    private void FindTextMoves()
    {
        List<Stretch> deleted = [], inserted = [];
        int deletedCount = 0, insertedCount = 0, order = 0;
        foreach (Paragraph paragraph in result.Sections.SelectMany(s => s.Body.Descendants<Paragraph>()))
        {
            if (paragraph.GetAncestor<Inline>() is not null)
            {
                continue;
            }

            Stretch? open = null;
            foreach (Inline inline in paragraph.Inlines)
            {
                RevisionKind? kind = inline is Run { Text.Length: > 0, Revision.Kind: RevisionKind.Deletion or RevisionKind.Insertion } run ? run.Revision!.Kind : null;
                if (kind is null || open is null || open.Kind != kind)
                {
                    if (open is not null)
                    {
                        Close(open);
                    }

                    open = kind is { } k ? new Stretch(k, order++) : null;
                }

                open?.Runs.Add((Run)inline);
            }

            if (open is not null)
            {
                Close(open);
            }
        }

        if (deletedCount < MoveWords || insertedCount < MoveWords || deletedCount > MostMoveWords || insertedCount > MostMoveWords)
        {
            return;
        }

        // Each run of MoveWords inserted words, by the hash of its words, where it starts.
        Dictionary<ulong, List<(Stretch Stretch, int Word)>> starts = [];
        foreach (Stretch stretch in inserted)
        {
            for (int w = 0; w + MoveWords <= stretch.Words.Count; w++)
            {
                ulong key = Shingle(stretch, w);
                if (!starts.TryGetValue(key, out List<(Stretch, int)>? list))
                {
                    starts[key] = list = [];
                }

                list.Add((stretch, w));
            }
        }

        List<(Stretch From, int FromWord, Stretch To, int ToWord, int Count)> found = [];
        foreach (Stretch from in deleted)
        {
            for (int w = 0; w + MoveWords <= from.Words.Count;)
            {
                (Stretch? To, int ToWord, int Count) best = (null, 0, 0);
                if (!from.Used[w] && starts.TryGetValue(Shingle(from, w), out List<(Stretch Stretch, int Word)>? candidates))
                {
                    foreach ((Stretch to, int start) in candidates)
                    {
                        int n = 0;
                        while (w + n < from.Words.Count && start + n < to.Words.Count && !from.Used[w + n] && !to.Used[start + n]
                            && from.Words[w + n].Text == to.Words[start + n].Text)
                        {
                            n++;
                        }

                        if (n >= MoveWords && n > best.Count && SameSpan(from, w, to, start, n))
                        {
                            best = (to, start, n);
                        }
                    }
                }

                if (best.To is not { } target)
                {
                    w++;
                    continue;
                }

                from.Used.AsSpan(w, best.Count).Fill(true);
                target.Used.AsSpan(best.ToWord, best.Count).Fill(true);
                found.Add((from, w, target, best.ToWord, best.Count));
                w += best.Count;
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        Dictionary<Run, int> changeOf = [];
        for (int c = 0; c < changes.Count; c++)
        {
            foreach (TrackedChange tracked in changes[c].Tracked)
            {
                if (tracked.Target == TrackedChangeTarget.Node && tracked.Node is Run run)
                {
                    changeOf.TryAdd(run, c);
                }
            }
        }

        HashSet<int> touched = [];
        // Spans are cut from the end of each stretch first, so the offsets of the spans before stay right.
        List<(Stretch Stretch, int Start, int End, int Match, bool Source)> spans = [];
        for (int m = 0; m < found.Count; m++)
        {
            (Stretch from, int fromWord, Stretch to, int toWord, int count) = found[m];
            spans.Add((from, from.Words[fromWord].Start, from.Words[fromWord + count - 1].End, m, true));
            spans.Add((to, to.Words[toWord].Start, to.Words[toWord + count - 1].End, m, false));
        }

        Move[] made = new Move[found.Count];
        for (int m = 0; m < found.Count; m++)
        {
            string name = "move" + (moves.Count + 1).ToString(CultureInfo.InvariantCulture);
            Move move = new(new Revision(RevisionKind.MoveFrom, author, date) { MoveName = name }, new Revision(RevisionKind.MoveTo, author, date) { MoveName = name });
            moves.Add(move);
            made[m] = move;
        }

        List<TrackedChange>[] sources = new List<TrackedChange>[found.Count], destinations = new List<TrackedChange>[found.Count];
        foreach ((Stretch stretch, int start, int end, int match, bool source) in spans.OrderByDescending(s => s.Start))
        {
            Move move = made[match];
            List<TrackedChange> side = [];
            foreach (Run run in Isolate(stretch, start, end, changeOf))
            {
                if (changeOf.Remove(run, out int change))
                {
                    changes[change].Tracked.RemoveAll(t => t.Node == run);
                    touched.Add(change);
                }

                run.Revision = source ? move.From : move.To;
                side.Add(new TrackedChange(run, TrackedChangeTarget.Node));
            }

            if (source)
            {
                sources[match] = side;
                move.Text.Append(stretch.Text.AsSpan(start, end - start));
            }
            else
            {
                destinations[match] = side;
            }
        }

        for (int m = 0; m < found.Count; m++)
        {
            Move move = made[m];
            bool sourceFirst = found[m].From.Order < found[m].To.Order;
            move.Tracked.AddRange(sourceFirst ? sources[m] : destinations[m]);
            move.Tracked.AddRange(sourceFirst ? destinations[m] : sources[m]);
            move.Change = changes.Count;
            changes.Add(new PendingChange(CompareChangeKind.Moved, "", "", found[m].From.Runs[0].Paragraph ?? (Node)result, move.Tracked));
            int words = found[m].Count;
            movedWords += words;
            deletedWords = Math.Max(0, deletedWords - words);
            insertedWords = Math.Max(0, insertedWords - words);
        }

        foreach (int c in touched)
        {
            PendingChange change = changes[c];
            if (change.Tracked.Count == 0)
            {
                removedChanges.Add(c);
                continue;
            }

            string old = string.Concat(change.Tracked.Where(t => t.Node is Inline { Revision.Removes: true }).Select(t => t.Node.GetText()));
            string @new = string.Concat(change.Tracked.Where(t => t.Node is Inline { Revision.Removes: false }).Select(t => t.Node.GetText()));
            CompareChangeKind kind = old.Length == 0 ? CompareChangeKind.Inserted : @new.Length == 0 ? CompareChangeKind.Deleted : CompareChangeKind.Replaced;
            changes[c] = change with { Kind = kind, OldText = old, NewText = @new };
        }

        sortChanges = true;

        void Close(Stretch stretch)
        {
            stretch.Text = string.Concat(stretch.Runs.Select(r => r.Text));
            foreach (Match word in WordPattern().Matches(stretch.Text))
            {
                stretch.Words.Add((word.Value, word.Index, word.Index + word.Length));
            }

            stretch.Used = new bool[stretch.Words.Count];
            if (stretch.Words.Count >= MoveWords)
            {
                (stretch.Kind == RevisionKind.Deletion ? deleted : inserted).Add(stretch);
            }

            if (stretch.Kind == RevisionKind.Deletion)
            {
                deletedCount += stretch.Words.Count;
            }
            else
            {
                insertedCount += stretch.Words.Count;
            }
        }
    }

    // Changes that moved text emptied, left out of the result's list, and whether the list needs sorting into document order.
    private readonly HashSet<int> removedChanges = [];
    private bool sortChanges;

    /// <summary>The hash of the <see cref="MoveWords"/> words of <paramref name="stretch"/> from word <paramref name="start"/>.</summary>
    private static ulong Shingle(Stretch stretch, int start)
    {
        ulong hash = 14695981039346656037UL;
        for (int k = start; k < start + MoveWords; k++)
        {
            foreach (char c in stretch.Words[k].Text)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }

            hash = (hash ^ ' ') * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Whether the text of the two runs of words, spaces and punctuation between them included, is the same.</summary>
    private static bool SameSpan(Stretch a, int wordA, Stretch b, int wordB, int count)
    {
        ReadOnlySpan<char> x = a.Text.AsSpan(a.Words[wordA].Start, a.Words[wordA + count - 1].End - a.Words[wordA].Start);
        ReadOnlySpan<char> y = b.Text.AsSpan(b.Words[wordB].Start, b.Words[wordB + count - 1].End - b.Words[wordB].Start);
        return x.SequenceEqual(y);
    }

    /// <summary>
    /// Cuts the stretch's runs so that characters <paramref name="start"/> to <paramref name="end"/> of its text are whole runs,
    /// and returns those. A run cut in two keeps its change for both parts.
    /// </summary>
    private static List<Run> Isolate(Stretch stretch, int start, int end, Dictionary<Run, int> changeOf)
    {
        List<Run> inside = [];
        int at = 0;
        for (int r = 0; r < stretch.Runs.Count; r++)
        {
            Run run = stretch.Runs[r];
            int runStart = at, runEnd = at + run.Text.Length;
            at = runEnd;
            if (runEnd <= start || runStart >= end)
            {
                continue;
            }

            if (runStart < start)
            {
                Run tail = Split(run, start - runStart);
                stretch.Runs.Insert(r + 1, tail);
                at = start;
                continue;
            }

            if (runEnd > end)
            {
                Run tail = Split(run, end - runStart);
                stretch.Runs.Insert(r + 1, tail);
                at = end;
            }

            inside.Add(run);
        }

        return inside;

        Run Split(Run run, int offset)
        {
            Run tail = run.Clone();
            tail.Text = run.Text[offset..];
            run.Text = run.Text[..offset];
            NodeCollection<Inline> inlines = run.Paragraph!.Inlines;
            inlines.Insert(inlines.IndexOf(run) + 1, tail);
            if (changeOf.TryGetValue(run, out int change))
            {
                changeOf[tail] = change;
            }

            return tail;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    /// <summary>A stretch of runs of one paragraph that are all deleted or all inserted, with its text and words.</summary>
    private sealed class Stretch(RevisionKind kind, int order)
    {
        public RevisionKind Kind => kind;

        /// <summary>Where the stretch comes among all stretches, in document order.</summary>
        public int Order => order;

        public List<Run> Runs { get; } = [];

        public string Text { get; set; } = "";

        public List<(string Text, int Start, int End)> Words { get; } = [];

        public bool[] Used { get; set; } = [];
    }
}
