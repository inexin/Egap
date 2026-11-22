using System.Text;

namespace Egap.Documents.Comparing;

/// <summary>
/// Paragraphs split and joined: one paragraph of one document against two or more adjacent paragraphs of the other whose
/// words together match it. Word marks such a change as the paragraph marks between the pieces, inserted for a split and
/// deleted for a join, with the word changes inside; the result here is the same.
/// </summary>
internal sealed partial class BlockComparer
{
    /// <summary>The most paragraphs one paragraph is taken to be split into, or joined from.</summary>
    private const int MostParts = 4;

    /// <summary>Blocks of a changed stretch compared as one: a block against a block, or a paragraph against adjacent paragraphs.</summary>
    private readonly record struct Group(int A, int CountA, int B, int CountB);

    /// <summary>
    /// Pairs the blocks of a changed stretch as <see cref="Pair{T}"/> does, and also a paragraph with adjacent paragraphs of the
    /// other document when their words together match it better than any one of them does.
    /// </summary>
    private List<Group> Groups(List<Block> a, List<Block> b)
    {
        // Every pair's score first (in the band when the stretch is long), so a group's pieces reuse their own.
        bool band = (long)a.Count * b.Count > (long)PairBand * PairBand * 4;
        int[] from = new int[a.Count];
        double[][] scores = new double[a.Count][];
        for (int i = 0; i < a.Count; i++)
        {
            int to = b.Count;
            if (band)
            {
                int center = (int)((long)i * b.Count / Math.Max(1, a.Count));
                from[i] = Math.Max(0, center - PairBand);
                to = Math.Min(b.Count, center + PairBand + 1);
            }

            scores[i] = new double[to - from[i]];
            for (int j = from[i]; j < to; j++)
            {
                scores[i][j - from[i]] = Score(a[i], b[j]);
            }
        }

        List<(double Score, Group Group)> candidates = [];
        Scores pairs = new(this, a, b, from, scores);
        for (int i = 0; i < a.Count; i++)
        {
            for (int k = 0; k < scores[i].Length; k++)
            {
                double value = scores[i][k];
                int j = from[i] + k;
                if (value >= PairThreshold)
                {
                    candidates.Add((value, new Group(i, 1, j, 1)));
                }

                if (value > 0 && a[i] is Paragraph pa && b[j] is Paragraph pb)
                {
                    AddParts(WordSet(pa), b, pairs, i, j, value, split: true, candidates);
                    AddParts(WordSet(pb), a, pairs, i, j, value, split: false, candidates);
                }
            }
        }

        return Pick(candidates, a.Count, b.Count);
    }

    /// <summary>The scores of a stretch's pairs: kept where they were worked out, else worked out.</summary>
    private sealed class Scores(BlockComparer owner, List<Block> a, List<Block> b, int[] from, double[][] scores)
    {
        public double this[int i, int j] => j >= from[i] && j - from[i] < scores[i].Length ? scores[i][j - from[i]] : owner.Score(a[i], b[j]);
    }

    /// <summary>
    /// Adds the runs of two to <see cref="MostParts"/> paragraphs of <paramref name="many"/> from (<paramref name="i"/>,
    /// <paramref name="j"/>) on whose words together match <paramref name="one"/>'s better than each alone, as split (one in
    /// A, pieces in B from j) or joined (one in B, pieces in A from i) candidates. Every piece shares words with the one
    /// paragraph, so an unrelated paragraph next to it is not taken in; a run whose words cannot match well enough, by the
    /// words each piece shares, is not merged to find out.
    /// </summary>
    private void AddParts(int[] one, List<Block> many, Scores pairs, int i, int j, double first, bool split, List<(double Score, Group Group)> candidates)
    {
        int start = split ? j : i;
        int size = WordSet(many[start]).Length, merged = 0;
        double common = first * (one.Length + size) / 2, best = first;
        int[] union = [];
        for (int count = 2; count <= MostParts && start + count <= many.Count && many[start + count - 1] is Paragraph part; count++)
        {
            double alone = split ? pairs[i, start + count - 1] : pairs[start + count - 1, j];
            if (alone == 0)
            {
                return;
            }

            int[] words = WordSet(part);
            common += alone * (one.Length + words.Length) / 2;
            size += words.Length;
            best = Math.Max(best, alone);
            double bound = (2 * common / (one.Length + size)) + 1e-9;
            if (bound < PairThreshold || bound <= best)
            {
                continue;
            }

            for (; merged < count; merged++)
            {
                union = MergeSorted(union, WordSet(many[start + merged]));
            }

            double score = Dice(one, union);
            if (score >= PairThreshold && score > best)
            {
                candidates.Add((score, split ? new Group(i, 1, start, count) : new Group(start, count, j, 1)));
            }
        }
    }

    private static int[] MergeSorted(int[] a, int[] b)
    {
        int[] merged = new int[a.Length + b.Length];
        int i = 0, j = 0, k = 0;
        while (i < a.Length && j < b.Length)
        {
            merged[k++] = a[i] <= b[j] ? a[i++] : b[j++];
        }

        while (i < a.Length)
        {
            merged[k++] = a[i++];
        }

        while (j < b.Length)
        {
            merged[k++] = b[j++];
        }

        return merged;
    }

    /// <summary>Takes candidate groups best first, each only when its blocks are free and it keeps both documents' order.</summary>
    private static List<Group> Pick(List<(double Score, Group Group)> candidates, int countA, int countB)
    {
        candidates.Sort((x, y) =>
            x.Score != y.Score ? y.Score.CompareTo(x.Score)
            : x.Group.A != y.Group.A ? x.Group.A.CompareTo(y.Group.A)
            : x.Group.B != y.Group.B ? x.Group.B.CompareTo(y.Group.B)
            : (x.Group.CountA + x.Group.CountB).CompareTo(y.Group.CountA + y.Group.CountB));
        List<Group> groups = [];
        bool[] usedA = new bool[countA], usedB = new bool[countB];
        foreach ((_, Group group) in candidates)
        {
            if (Used(usedA, group.A, group.CountA) || Used(usedB, group.B, group.CountB))
            {
                continue;
            }

            int at = 0, high = groups.Count;
            while (at < high)
            {
                int middle = (at + high) / 2;
                if (groups[middle].A < group.A)
                {
                    at = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if ((at > 0 && groups[at - 1].B + groups[at - 1].CountB > group.B) || (at < groups.Count && groups[at].B < group.B + group.CountB))
            {
                continue;
            }

            groups.Insert(at, group);
            Array.Fill(usedA, true, group.A, group.CountA);
            Array.Fill(usedB, true, group.B, group.CountB);
        }

        return groups;

        static bool Used(bool[] used, int start, int count)
        {
            return Array.IndexOf(used, true, start, count) >= 0;
        }
    }

    /// <summary>
    /// One paragraph against adjacent paragraphs of the other document: their words diffed as one text into the result's
    /// paragraphs, which are the side with more paragraphs. The marks between them are inserted (a split, one paragraph in A)
    /// or deleted (a join, one paragraph in B); the last paragraph's mark is the one paragraph's own.
    /// </summary>
    private void SplitOrJoin(List<Paragraph> a, List<Paragraph> b, Action<Block, bool> emit)
    {
        bool split = a.Count == 1;
        List<Paragraph> many = split ? b : a;
        Paragraph one = split ? a[0] : b[0];
        int k = many.Count;
        bool manyIsBase = split == baseIsRevised;

        // The result's paragraphs: the base's own where it has the pieces; else copies of the other's (their formatting, for
        // their marks) and the base's one paragraph last, as the mark the pieces end with.
        Paragraph[] target = new Paragraph[k];
        for (int p = 0; p < k; p++)
        {
            target[p] = manyIsBase ? many[p] : p == k - 1 ? one : result.Import(many[p], deep: false);
        }

        if (placed is not null)
        {
            for (int p = 0; p < b.Count; p++)
            {
                placed.TryAdd(b[p], target[split ? p : 0]);
            }
        }

        ParagraphTokens[] ta = [.. a.Select(Tokens)], tb = [.. b.Select(Tokens)];
        int[] offA = Offsets(ta), offB = Offsets(tb), offMany = split ? offB : offA;
        List<DiffStep> script = SequenceDiff.Diff<int>([.. ta.SelectMany(t => t.Ids)], [.. tb.SelectMany(t => t.Ids)], out bool limit);
        limitReached |= limit;
        Emitter[] emA = [.. ta.Select(t => new Emitter(this, t, isBase: !baseIsRevised))];
        Emitter[] emB = [.. tb.Select(t => new Emitter(this, t, isBase: baseIsRevised))];
        List<Inline>[] outputs = [.. target.Select(_ => new List<Inline>())];
        int marked = 0, i = 0, j = 0;
        for (int s = 0; s < script.Count; s++)
        {
            DiffStep step = script[s];
            if (step.Operation == DiffOperation.Equal)
            {
                // Cut where the pieces' paragraphs end, so each stretch is within one paragraph of either side.
                for (int count = step.Count; count > 0;)
                {
                    int p = Find(offMany, split ? j : i);
                    int n = Math.Min(count, offMany[p + 1] - (split ? j : i));
                    int pa = split ? 0 : p, pb = split ? p : 0;
                    EmitEqual(ta[pa], tb[pb], i - offA[pa], j - offB[pb], n, baseIsRevised ? emB[pb] : emA[pa], Output(p));
                    i += n;
                    j += n;
                    count -= n;
                }

                continue;
            }

            int deleted = 0, inserted = 0;
            if (step.Operation == DiffOperation.Delete)
            {
                deleted = step.Count;
                if (s + 1 < script.Count && script[s + 1].Operation == DiffOperation.Insert)
                {
                    inserted = script[++s].Count;
                }
            }
            else
            {
                inserted = step.Count;
            }

            List<TrackedChange> tracked = [];
            int anchor = Side(true, i, i + deleted, deletion, split ? j : i + deleted, tracked);
            int anchorB = Side(false, j, j + inserted, insertion, split ? j : i + deleted, tracked);
            CompareChangeKind kind = deleted == 0 ? CompareChangeKind.Inserted : inserted == 0 ? CompareChangeKind.Deleted : CompareChangeKind.Replaced;
            changes.Add(new PendingChange(kind, TextOf(ta, offA, i, i + deleted), TextOf(tb, offB, j, j + inserted), target[anchor >= 0 ? anchor : anchorB], tracked));
            i += deleted;
            j += inserted;
        }

        // The base's marks after its last word, then the marks between the pieces not reached yet.
        for (int p = 0; p < k; p++)
        {
            if (manyIsBase || p == k - 1)
            {
                ParagraphTokens t = manyIsBase ? (split ? tb[p] : ta[p]) : (split ? ta[0] : tb[0]);
                Emitter emitter = manyIsBase ? (split ? emB[p] : emA[p]) : (split ? emA[0] : emB[0]);
                emitter.Emit(t.Text.Length, t.Text.Length, null, Output(p), null);
            }
        }

        CompareParagraphFormat(a[^1], b[^1]);
        paragraphsTouched += k;

        // The last first: when it is the base's one paragraph, its inlines go to the pieces before it too.
        for (int p = k - 1; p >= 0; p--)
        {
            Replace(target[p].Inlines, outputs[p]);
        }

        for (int p = 0; p < k; p++)
        {
            emit(target[p], manyIsBase || p == k - 1);
        }

        // The output of piece p, once the marks of the pieces before it are recorded, so changes stay in document order.
        List<Inline> Output(int p)
        {
            for (; marked < p; marked++)
            {
                Paragraph paragraph = target[marked];
                Revision revision = split ? insertion : deletion;
                marks.Add(() => paragraph.MarkRevision = revision);
                string mark = ControlChar.ParagraphBreak.ToString();
                changes.Add(new PendingChange(
                    split ? CompareChangeKind.Inserted : CompareChangeKind.Deleted, split ? "" : mark, split ? mark : "", paragraph, [new TrackedChange(paragraph, TrackedChangeTarget.ParagraphMark)]));
            }

            return outputs[p];
        }

        // Emits tokens [from, to) of A or B marked with revision: the pieces' own into their paragraphs, the one paragraph's
        // where the pieces are at (manyAt), after the last token before it. Returns the first paragraph written, or -1.
        int Side(bool sideA, int from, int to, Revision revision, int manyAt, List<TrackedChange> tracked)
        {
            if (from == to)
            {
                return -1;
            }

            ParagraphTokens[] t = sideA ? ta : tb;
            int[] off = sideA ? offA : offB;
            Emitter[] em = sideA ? emA : emB;
            int first = -1;
            bool pieces = sideA != split;
            for (int at = from; at < to;)
            {
                int q = pieces ? Find(off, at) : 0;
                int end = pieces ? Math.Min(to, off[q + 1]) : to;
                int p = pieces ? q : manyAt == 0 ? 0 : Find(offMany, manyAt - 1);
                em[q].Emit(t[q].Starts[at - off[q]], t[q].Starts[end - off[q]], revision, Output(p), tracked);
                if (sideA)
                {
                    deletedWords += CountWords(t[q], at - off[q], end - off[q]);
                }
                else
                {
                    insertedWords += CountWords(t[q], at - off[q], end - off[q]);
                }

                first = first < 0 ? p : first;
                at = end;
            }

            return first;
        }
    }

    /// <summary>Where each paragraph's tokens start in the paragraphs' tokens one after another, and the total last.</summary>
    private static int[] Offsets(ParagraphTokens[] paragraphs)
    {
        int[] offsets = new int[paragraphs.Length + 1];
        for (int p = 0; p < paragraphs.Length; p++)
        {
            offsets[p + 1] = offsets[p] + paragraphs[p].Count;
        }

        return offsets;
    }

    /// <summary>The paragraph that holds token <paramref name="at"/>; the last for the end.</summary>
    private static int Find(int[] offsets, int at)
    {
        int p = 0;
        while (p < offsets.Length - 2 && offsets[p + 1] <= at)
        {
            p++;
        }

        return p;
    }

    /// <summary>The text of tokens [from, to) of paragraphs one after another, with a paragraph break between paragraphs.</summary>
    private static string TextOf(ParagraphTokens[] paragraphs, int[] offsets, int from, int to)
    {
        if (paragraphs.Length == 1)
        {
            return paragraphs[0].TextOf(from, to);
        }

        StringBuilder text = new();
        for (int at = from; at < to;)
        {
            int p = Find(offsets, at);
            int end = Math.Min(to, offsets[p + 1]);
            if (text.Length > 0 || at > from)
            {
                text.Append(ControlChar.ParagraphBreak);
            }

            text.Append(paragraphs[p].TextOf(at - offsets[p], end - offsets[p]));
            at = end;
        }

        return text.ToString();
    }
}
