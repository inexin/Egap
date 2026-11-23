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

    /// <summary>
    /// The smallest share of common words (Dice coefficient) for a paragraph and its pieces taken together: higher than for
    /// two paragraphs, since more pieces give more chances of common words by accident.
    /// </summary>
    private const double GroupThreshold = 0.75;

    /// <summary>How much the pieces' words together may differ in number from the one paragraph's, as a share of the larger.</summary>
    private const double GroupSizeSlack = 0.25;

    /// <summary>
    /// How many words the pieces together may differ by from the one paragraph's in any case, so short paragraphs joined
    /// with a word or two deleted (as "Four." and "Five six." into "Four.Five") still count; the share of common words
    /// still has to reach <see cref="GroupThreshold"/>.
    /// </summary>
    private const int GroupSizeWords = 2;

    // The pieces' word sets while DiceOfPieces counts them.
    private readonly int[][] pieceWords = new int[MostParts][];

    /// <summary>Blocks of a changed stretch compared as one: a block against a block, or a paragraph against adjacent paragraphs.</summary>
    private readonly record struct Group(int A, int CountA, int B, int CountB);

    /// <summary>
    /// Pairs the blocks of a changed stretch as <see cref="Pair{T}"/> does, and also a paragraph with adjacent paragraphs of the
    /// other document when their words together match it better than any one of them does.
    /// </summary>
    private List<Group> Groups(List<Block> a, List<Block> b)
    {
        List<(double Score, int A, int B)> singles = [];
        List<(double Score, Group Group)> groups = [];
        int[] sizesA = Sizes(a), sizesB = Sizes(b);
        RowScores scores = new(this, a, b);
        for (int i = 0; i < a.Count; i++)
        {
            (int from, int to) = scores.Fill(i);
            for (int j = from; j < to; j++)
            {
                double value = scores[i, j];
                if (value >= PairThreshold)
                {
                    singles.Add((value, i, j));
                }

                if (value > 0 && sizesA[i] > 0 && sizesB[j] > 0)
                {
                    AddParts(scores, a, b, sizesA, sizesB, i, j, value, split: true, groups);
                    AddParts(scores, a, b, sizesA, sizesB, i, j, value, split: false, groups);
                }
            }
        }

        return Pick(singles, groups, a.Count, b.Count);
    }

    /// <summary>
    /// The scores of a stretch's pairs, row by row (each paragraph of A against B, in the band when the stretch is long), kept
    /// for the row at hand and the rows after it that a join's pieces reach, so a piece's score is worked out once.
    /// </summary>
    private sealed class RowScores
    {
        private readonly BlockComparer owner;
        private readonly List<Block> a;
        private readonly List<Block> b;
        private readonly bool band;
        private readonly double[][] rows = new double[MostParts][];
        private readonly int[] rowOf = [-1, -1, -1, -1];
        private readonly int[] from = new int[MostParts];
        private readonly int[] to = new int[MostParts];

        public RowScores(BlockComparer owner, List<Block> a, List<Block> b)
        {
            this.owner = owner;
            this.a = a;
            this.b = b;
            band = (long)a.Count * b.Count > (long)PairBand * PairBand * 4;
        }

        /// <summary>Works out row <paramref name="i"/> and the rows after it up to a join's reach; returns row i's range in B.</summary>
        public (int From, int To) Fill(int i)
        {
            for (int r = i; r < Math.Min(a.Count, i + MostParts); r++)
            {
                int slot = r % MostParts;
                if (rowOf[slot] == r)
                {
                    continue;
                }

                int start = 0, end = b.Count;
                if (band)
                {
                    int center = (int)((long)r * b.Count / Math.Max(1, a.Count));
                    start = Math.Max(0, center - PairBand);
                    end = Math.Min(b.Count, center + PairBand + 1);
                }

                double[] row = rows[slot] ??= new double[band ? (2 * PairBand) + 1 : b.Count];
                for (int j = start; j < end; j++)
                {
                    row[j - start] = owner.Score(a[r], b[j]);
                }

                (rowOf[slot], from[slot], to[slot]) = (r, start, end);
            }

            int at = i % MostParts;
            return (from[at], to[at]);
        }

        /// <summary>The score of a[i] against b[j]: kept when its row has it, else worked out.</summary>
        public double this[int i, int j]
        {
            get
            {
                int slot = i % MostParts;
                return rowOf[slot] == i && j >= from[slot] && j < to[slot] ? rows[slot][j - from[slot]] : owner.Score(a[i], b[j]);
            }
        }
    }

    /// <summary>Whether <paramref name="larger"/> words are more than the slack above <paramref name="smaller"/>.</summary>
    private static bool FarApart(int larger, int smaller) => larger - smaller > Math.Max(GroupSizeSlack * larger, GroupSizeWords);

    /// <summary>The number of words of each paragraph of a stretch; -1 for a block that is not a paragraph.</summary>
    private int[] Sizes(List<Block> blocks)
    {
        int[] sizes = new int[blocks.Count];
        for (int k = 0; k < blocks.Count; k++)
        {
            sizes[k] = blocks[k] is Paragraph paragraph ? WordSet(paragraph).Length : -1;
        }

        return sizes;
    }

    /// <summary>
    /// Adds the runs of two to <see cref="MostParts"/> adjacent paragraphs, from b[j] (a split of a[i]) or from a[i] (a join
    /// into b[j]), whose words together match the one paragraph's better than each piece alone, as candidates. The pieces
    /// must have about as many words as the one paragraph and each share words with it, so an unrelated paragraph next to
    /// it is not taken in. Bounds from the word counts and from each piece's own score rule most runs out before their
    /// words are counted.
    /// </summary>
    private void AddParts(RowScores scores, List<Block> a, List<Block> b, int[] sizesA, int[] sizesB, int i, int j, double first, bool split, List<(double Score, Group Group)> groups)
    {
        List<Block> many = split ? b : a;
        int[] sizes = split ? sizesB : sizesA;
        int start = split ? j : i, one = split ? sizesA[i] : sizesB[j];
        double firstCommon = first * (one + sizes[start]) / 2;
        int size = sizes[start], rest = 0, scored = 1;
        double common = firstCommon, best = first;
        for (int count = 2; count <= MostParts && start + count <= many.Count && sizes[start + count - 1] > 0; count++)
        {
            size += sizes[start + count - 1];
            rest += sizes[start + count - 1];
            if (FarApart(size, one))
            {
                // The pieces have far more words than the paragraph, and more pieces only add to them.
                return;
            }

            // At best every word of the later pieces is common.
            if (FarApart(one, size) || 2 * (firstCommon + rest) / (one + size) < GroupThreshold)
            {
                continue;
            }

            for (; scored < count; scored++)
            {
                int k = start + scored;
                double alone = split ? scores[i, k] : scores[k, j];
                if (alone == 0)
                {
                    return;
                }

                common += alone * (one + sizes[k]) / 2;
                best = Math.Max(best, alone);
            }

            double bound = (2 * common / (one + size)) + 1e-9;
            if (bound < GroupThreshold || bound <= best)
            {
                continue;
            }

            double score = DiceOfPieces(WordSet(split ? a[i] : b[j]), many, start, count, size);
            if (score >= GroupThreshold && score > best)
            {
                groups.Add((score, split ? new Group(i, 1, start, count) : new Group(start, count, j, 1)));
            }
        }
    }

    /// <summary>
    /// The Dice coefficient of a paragraph's words and the words of <paramref name="count"/> pieces from
    /// <paramref name="start"/> taken together (<paramref name="size"/> words), counting repeats, without merging the
    /// pieces' words.
    /// </summary>
    private double DiceOfPieces(int[] one, List<Block> many, int start, int count, int size)
    {
        Span<int> at = stackalloc int[MostParts];
        at.Clear();
        for (int k = 0; k < count; k++)
        {
            pieceWords[k] = WordSet(many[start + k]);
        }

        int common = 0;
        for (int i = 0; i < one.Length;)
        {
            int word = one[i], times = 0;
            for (; i < one.Length && one[i] == word; i++)
            {
                times++;
            }

            int found = 0;
            for (int k = 0; k < count && found < times; k++)
            {
                int[] words = pieceWords[k];
                int p = at[k];
                while (p < words.Length && words[p] < word)
                {
                    p++;
                }

                while (p < words.Length && words[p] == word && found < times)
                {
                    p++;
                    found++;
                }

                at[k] = p;
            }

            common += found;
        }

        return one.Length + size == 0 ? 1 : 2.0 * common / (one.Length + size);
    }

    /// <summary>
    /// Takes candidates best first (a pair before a group of the same score), each only when its blocks are free and it
    /// keeps both documents' order.
    /// </summary>
    private static List<Group> Pick(List<(double Score, int A, int B)> singles, List<(double Score, Group Group)> candidates, int countA, int countB)
    {
        singles.Sort((x, y) => x.Score != y.Score ? y.Score.CompareTo(x.Score) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        candidates.Sort((x, y) =>
            x.Score != y.Score ? y.Score.CompareTo(x.Score)
            : x.Group.A != y.Group.A ? x.Group.A.CompareTo(y.Group.A)
            : x.Group.B != y.Group.B ? x.Group.B.CompareTo(y.Group.B)
            : (x.Group.CountA + x.Group.CountB).CompareTo(y.Group.CountA + y.Group.CountB));
        List<Group> groups = [];
        bool[] usedA = new bool[countA], usedB = new bool[countB];
        int s = 0, c = 0;
        while (s < singles.Count || c < candidates.Count)
        {
            Group group;
            if (c == candidates.Count || (s < singles.Count && singles[s].Score >= candidates[c].Score))
            {
                group = new Group(singles[s].A, 1, singles[s].B, 1);
                s++;
            }
            else
            {
                group = candidates[c].Group;
                c++;
            }

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
            return count == 1 ? used[start] : Array.IndexOf(used, true, start, count) >= 0;
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
