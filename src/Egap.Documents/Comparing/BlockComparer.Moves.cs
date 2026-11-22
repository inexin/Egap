using System.Globalization;
using System.Runtime.InteropServices;

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
        // A first pass counts the changed words and notes the stretches long enough to hold a move, so a comparison with too
        // many changed words, or none to move, allocates next to nothing here.
        List<(Paragraph Paragraph, int First, int End, RevisionKind Kind, int Order)> candidates = [];
        int deletedCount = 0, insertedCount = 0, order = 0;
        foreach (Paragraph paragraph in result.Sections.SelectMany(s => s.Body.Descendants<Paragraph>()))
        {
            if (paragraph.GetAncestor<Inline>() is not null)
            {
                continue;
            }

            NodeCollection<Inline> inlines = paragraph.Inlines;
            RevisionKind? open = null;
            int openFirst = 0, count = 0;
            bool inWord = false;
            for (int i = 0; i <= inlines.Count; i++)
            {
                Run? run = i < inlines.Count && inlines[i] is Run { Text.Length: > 0, Revision.Kind: RevisionKind.Deletion or RevisionKind.Insertion } r ? r : null;
                RevisionKind? kind = run?.Revision!.Kind;
                if (open is { } done && kind != done)
                {
                    if (count >= MoveWords)
                    {
                        candidates.Add((paragraph, openFirst, i, done, order++));
                    }

                    if (done == RevisionKind.Deletion)
                    {
                        deletedCount += count;
                    }
                    else
                    {
                        insertedCount += count;
                    }

                    if (deletedCount > MostMoveWords || insertedCount > MostMoveWords)
                    {
                        return;
                    }

                    open = null;
                }

                if (run is null)
                {
                    continue;
                }

                if (open is null)
                {
                    (open, openFirst, count, inWord) = (kind, i, 0, false);
                }

                foreach (char c in run.Text)
                {
                    bool word = IsWordChar(c);
                    count += word && !inWord ? 1 : 0;
                    inWord = word;
                }
            }
        }

        if (deletedCount < MoveWords || insertedCount < MoveWords)
        {
            return;
        }

        List<Stretch> deleted = [], inserted = [];
        foreach ((Paragraph paragraph, int firstRun, int endRun, RevisionKind kind, int place) in candidates)
        {
            Stretch stretch = new(kind, place);
            for (int i = firstRun; i < endRun; i++)
            {
                stretch.Runs.Add((Run)paragraph.Inlines[i]);
            }

            stretch.TakeWords();
            (kind == RevisionKind.Deletion ? deleted : inserted).Add(stretch);
        }

        if (deleted.Count == 0 || inserted.Count == 0)
        {
            return;
        }

        // Each run of MoveWords inserted words, by the hash of its words, where it starts: the first entry of each hash in a
        // dictionary, and the others chained through arrays, so the index takes a few allocations whatever its size.
        int entries = 0;
        foreach (Stretch stretch in inserted)
        {
            entries += stretch.Words.Count - MoveWords + 1;
        }

        Dictionary<ulong, int> first = new(entries);
        int[] next = new int[entries];
        (Stretch Stretch, int Word)[] at = new (Stretch, int)[entries];
        int e = 0;
        foreach (Stretch stretch in inserted)
        {
            for (int w = 0; w + MoveWords <= stretch.Words.Count; w++, e++)
            {
                at[e] = (stretch, w);
                ref int head = ref CollectionsMarshal.GetValueRefOrAddDefault(first, Shingle(stretch, w), out bool exists);
                next[e] = exists ? head : -1;
                head = e;
            }
        }

        List<(Stretch From, int FromWord, Stretch To, int ToWord, int Count)> found = [];
        foreach (Stretch from in deleted)
        {
            for (int w = 0; w + MoveWords <= from.Words.Count;)
            {
                (Stretch? To, int ToWord, int Count) best = (null, 0, 0);
                if (!from.Used[w] && first.TryGetValue(Shingle(from, w), out int candidate))
                {
                    for (; candidate >= 0; candidate = next[candidate])
                    {
                        (Stretch to, int start) = at[candidate];
                        int n = 0;
                        while (w + n < from.Words.Count && start + n < to.Words.Count && !from.Used[w + n] && !to.Used[start + n]
                            && SameWord(from, w + n, to, start + n))
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
            hash = (hash ^ stretch.Words[k].Hash) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Whether the two words are the same text.</summary>
    private static bool SameWord(Stretch a, int wordA, Stretch b, int wordB)
    {
        (ulong hashA, int startA, int endA) = a.Words[wordA];
        (ulong hashB, int startB, int endB) = b.Words[wordB];
        return hashA == hashB && a.Text.AsSpan(startA, endA - startA).SequenceEqual(b.Text.AsSpan(startB, endB - startB));
    }

    /// <summary>A letter or a number, the characters of a word for moved text.</summary>
    private static bool IsWordChar(char c) => char.GetUnicodeCategory(c) is <= UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

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

    /// <summary>A stretch of runs of one paragraph that are all deleted or all inserted, with its text and words.</summary>
    private sealed class Stretch(RevisionKind kind, int order)
    {
        public RevisionKind Kind => kind;

        /// <summary>Where the stretch comes among all stretches, in document order.</summary>
        public int Order => order;

        public List<Run> Runs { get; } = [];

        public string Text { get; set; } = "";

        /// <summary>Sets <see cref="Text"/> from the runs and finds its words.</summary>
        public void TakeWords()
        {
            Text = Runs.Count == 1 ? Runs[0].Text : string.Concat(Runs.Select(r => r.Text));
            for (int i = 0; i < Text.Length;)
            {
                if (!IsWordChar(Text[i]))
                {
                    i++;
                    continue;
                }

                int start = i;
                ulong hash = 14695981039346656037UL;
                for (; i < Text.Length && IsWordChar(Text[i]); i++)
                {
                    hash = (hash ^ Text[i]) * 1099511628211UL;
                }

                Words.Add((hash, start, i));
            }

            Used = new bool[Words.Count];
        }

        /// <summary>The words: the hash of each one's text, and where it starts and ends in <see cref="Text"/>.</summary>
        public List<(ulong Hash, int Start, int End)> Words { get; private set; } = [];

        public bool[] Used { get; set; } = [];
    }
}
