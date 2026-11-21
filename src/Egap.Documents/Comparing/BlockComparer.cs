namespace Egap.Documents.Comparing;

/// <summary>
/// The comparison itself: lines up the blocks of the two documents, pairs changed paragraphs, diffs the words of each pair
/// and builds the result on the base document's copy. "A" is always the original and "B" the revised document; the base is
/// one of them, its nodes become the result's, and content only in the other is imported.
/// </summary>
internal sealed class BlockComparer
{
    /// <summary>How far from the diagonal of a changed stretch paragraphs are tried as pairs, when the stretch is long.</summary>
    private const int PairBand = 64;

    /// <summary>The smallest share of common words (Dice coefficient) for two changed paragraphs to be compared word by word.</summary>
    private const double PairThreshold = 0.5;

    private readonly Document result;
    private readonly bool baseIsRevised;
    private readonly List<DocumentCompareWarning> warnings;
    private readonly TokenTable table;
    private readonly Revision insertion;
    private readonly Revision deletion;
    private readonly Dictionary<Paragraph, ParagraphTokens> tokens = [];
    private readonly Dictionary<Paragraph, int[]> wordSets = [];
    private readonly List<PendingChange> changes = [];
    private readonly List<Action> marks = [];
    private int insertedWords;
    private int deletedWords;
    private int paragraphsTouched;
    private bool limitReached;
    private int notTracked;

    public BlockComparer(Document result, bool baseIsRevised, DocumentCompareOptions options, List<DocumentCompareWarning> warnings)
    {
        this.result = result;
        this.baseIsRevised = baseIsRevised;
        this.warnings = warnings;
        table = new TokenTable(options.Granularity == CompareGranularity.Character, !options.CaseChanges, !options.Whitespace);
        insertion = new Revision(RevisionKind.Insertion, options.Author, options.Date);
        deletion = new Revision(RevisionKind.Deletion, options.Author, options.Date);
    }

    /// <summary>A change found, kept until the result's tree is final so its story can be named.</summary>
    private sealed record PendingChange(CompareChangeKind Kind, string OldText, string NewText, Node Anchor, List<TrackedChange> Tracked);

    /// <summary>
    /// Compares the main text: section by section when both documents have as many sections, else every section's body as
    /// one run of blocks. Puts the result's blocks in place.
    /// </summary>
    public void CompareBodies(Document a, Document b)
    {
        if (a.Sections.Count == b.Sections.Count)
        {
            for (int k = 0; k < a.Sections.Count; k++)
            {
                CompareContainer(a.Sections[k].Body.Blocks, b.Sections[k].Body.Blocks, result.Sections[k].Body.Blocks);
            }

            return;
        }

        warnings.Add(new DocumentCompareWarning(
            DocumentCompareWarningKind.SectionsDiffer, $"The original has {a.Sections.Count} sections and the revised document {b.Sections.Count}; the result keeps the base document's."));
        List<(Block Block, Body Body)> blocksA = Flatten(a), blocksB = Flatten(b);

        List<(Block Block, Body Body)> baseBlocks = baseIsRevised ? blocksB : blocksA;
        Dictionary<Block, Body> bodyOf = [];
        foreach ((Block block, Body body) in baseBlocks)
        {
            bodyOf[block] = body;
        }

        Dictionary<Body, List<Block>> lists = [];
        foreach (Section section in result.Sections)
        {
            lists[section.Body] = [];
        }

        // Blocks only in the other document go with the next base block, so a stretch replaced at the start of a section stays
        // in that section; blocks after the last base block go with it.
        Body? current = null;
        List<Block> waiting = [];
        CompareBlocks([.. blocksA.Select(x => x.Block)], [.. blocksB.Select(x => x.Block)], (block, isBase) =>
        {
            if (!isBase)
            {
                waiting.Add(block);
                return;
            }

            current = bodyOf[block];
            lists[current].AddRange(waiting);
            waiting.Clear();
            lists[current].Add(block);
        });

        if (waiting.Count > 0)
        {
            if (current is null && result.Sections.Count == 0)
            {
                current = result.AddSection().Body;
                lists[current] = [];
            }

            current ??= result.Sections[^1].Body;
            lists[current].AddRange(waiting);
        }

        foreach ((Body body, List<Block> list) in lists)
        {
            Replace(body.Blocks, list);
        }
    }

    /// <summary>The comparison's result.</summary>
    public DocumentComparison Finish()
    {
        foreach (Action mark in marks)
        {
            mark();
        }

        if (limitReached)
        {
            warnings.Add(new DocumentCompareWarning(
                DocumentCompareWarningKind.EditLimitReached, "The documents differ so much that some text was marked as deleted and inserted whole."));
        }

        if (notTracked > 0)
        {
            warnings.Add(new DocumentCompareWarning(
                DocumentCompareWarningKind.ContentNotTracked, "Content that cannot be marked as a tracked change was kept as in the base document.", notTracked));
        }

        List<DocumentChange> list = [];
        foreach (PendingChange change in changes)
        {
            Story story = change.Anchor as Story ?? change.Anchor.GetAncestor<Story>() ?? result.Sections[0].Body;
            list.Add(new DocumentChange(change.Kind, change.OldText, change.NewText, story, change.Tracked));
        }

        DocumentCompareStatistics statistics = new()
        {
            InsertedWords = insertedWords,
            DeletedWords = deletedWords,
            ParagraphsTouched = paragraphsTouched,
        };
        return new DocumentComparison(result, list, statistics, warnings);
    }

    private static List<(Block, Body)> Flatten(Document document)
    {
        List<(Block, Body)> blocks = [];
        foreach (Section section in document.Sections)
        {
            foreach (Block block in section.Body.Blocks)
            {
                blocks.Add((block, section.Body));
            }
        }

        return blocks;
    }

    private static void Replace<T>(NodeCollection<T> collection, List<T> items)
        where T : Node
    {
        if (collection.Count == items.Count && items.Select((item, i) => ReferenceEquals(collection[i], item)).All(same => same))
        {
            return;
        }

        collection.Clear();
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }

    // ---- Blocks ----

    /// <summary>
    /// Lines up two runs of blocks and sends the result's blocks, in order, to <paramref name="emit"/> (with whether each is a
    /// base node).
    /// </summary>
    private void CompareBlocks(IReadOnlyList<Block> a, IReadOnlyList<Block> b, Action<Block, bool> emit)
    {
        ulong[] keysA = [.. a.Select(Key)], keysB = [.. b.Select(Key)];
        List<DiffStep> script = SequenceDiff.Diff<ulong>(keysA, keysB, out bool limit);
        limitReached |= limit;
        int i = 0, j = 0;
        for (int s = 0; s < script.Count; s++)
        {
            DiffStep step = script[s];
            if (step.Operation == DiffOperation.Equal)
            {
                for (int k = 0; k < step.Count; k++)
                {
                    emit(baseIsRevised ? b[j + k] : a[i + k], true);
                }

                i += step.Count;
                j += step.Count;
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

            CompareStretch([.. a.Skip(i).Take(deleted)], [.. b.Skip(j).Take(inserted)], emit);
            i += deleted;
            j += inserted;
        }
    }

    /// <summary>A changed stretch: blocks only in A and blocks only in B, paired where they are alike.</summary>
    private void CompareStretch(List<Block> a, List<Block> b, Action<Block, bool> emit)
    {
        List<(int A, int B)> pairs = Pair(a, b);
        int i = 0, j = 0;
        foreach ((int pa, int pb) in pairs.Append((a.Count, b.Count)))
        {
            for (; i < pa; i++)
            {
                Whole(a[i], fromA: true, emit);
            }

            for (; j < pb; j++)
            {
                Whole(b[j], fromA: false, emit);
            }

            if (pa < a.Count)
            {
                Paired(a[pa], b[pb], emit);
                i = pa + 1;
                j = pb + 1;
            }
        }
    }

    /// <summary>
    /// Pairs blocks of a changed stretch: paragraphs whose words overlap enough, and containers of the same kind, best pairs
    /// first, keeping their order.
    /// </summary>
    private List<(int A, int B)> Pair(List<Block> a, List<Block> b)
    {
        List<(double Score, int A, int B)> candidates = [];
        bool band = (long)a.Count * b.Count > (long)PairBand * PairBand * 4;
        for (int i = 0; i < a.Count; i++)
        {
            int from = 0, to = b.Count;
            if (band)
            {
                int center = (int)((long)i * b.Count / Math.Max(1, a.Count));
                from = Math.Max(0, center - PairBand);
                to = Math.Min(b.Count, center + PairBand + 1);
            }

            for (int j = from; j < to; j++)
            {
                double score = Score(a[i], b[j]);
                if (score >= PairThreshold)
                {
                    candidates.Add((score, i, j));
                }
            }
        }

        candidates.Sort((x, y) => x.Score != y.Score ? y.Score.CompareTo(x.Score) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        List<(int A, int B)> pairs = [];
        bool[] usedB = new bool[b.Count];
        foreach ((_, int ca, int cb) in candidates)
        {
            int at = pairs.BinarySearch((ca, -1), Comparer<(int A, int B)>.Create((x, y) => x.A.CompareTo(y.A)));
            if (at >= 0 || usedB[cb])
            {
                continue;
            }

            at = ~at;
            if ((at > 0 && pairs[at - 1].B >= cb) || (at < pairs.Count && pairs[at].B <= cb))
            {
                continue;
            }

            pairs.Insert(at, (ca, cb));
            usedB[cb] = true;
        }

        return pairs;
    }

    private double Score(Block a, Block b)
    {
        return (a, b) switch
        {
            (Paragraph pa, Paragraph pb) => Dice(WordSet(pa), WordSet(pb)),
            (ContentControl ca, ContentControl cb) => 0.5 + (0.5 * Dice(WordSet(ca), WordSet(cb))),
            (FlexContainer fa, FlexContainer fb) when fa.Items.Count == fb.Items.Count => 0.5 + (0.5 * Dice(WordSet(fa), WordSet(fb))),
            (GridContainer ga, GridContainer gb) when ga.Items.Count == gb.Items.Count => 0.5 + (0.5 * Dice(WordSet(ga), WordSet(gb))),
            _ => 0,
        };
    }

    /// <summary>A block that is only in one document: marked deleted (from A) or inserted (from B) as a whole.</summary>
    private void Whole(Block block, bool fromA, Action<Block, bool> emit)
    {
        bool isBase = fromA != baseIsRevised;
        Revision revision = fromA ? deletion : insertion;
        if (block is not (Paragraph or Table))
        {
            // A container or kept island has no tracked form of its own; the base's stays, the other's is left out.
            notTracked++;
            if (isBase)
            {
                emit(block, true);
            }

            return;
        }

        Block node = isBase ? block : Import(block);
        List<TrackedChange> tracked = [];
        int words = 0, paragraphs = 0;
        if (node is Table tableNode)
        {
            foreach (Row row in tableNode.Rows)
            {
                tracked.Add(new TrackedChange(row, TrackedChangeTarget.Row));
            }
        }

        foreach (Paragraph paragraph in node is Paragraph p ? [p] : node.Descendants<Paragraph>())
        {
            words += CountWords(paragraph);
            paragraphs++;
            foreach (Inline inline in paragraph.Inlines)
            {
                tracked.Add(new TrackedChange(inline, TrackedChangeTarget.Node));
            }

            if (node is Paragraph)
            {
                tracked.Add(new TrackedChange(paragraph, TrackedChangeTarget.ParagraphMark));
            }
        }

        // The revisions go on once the block is in the result's tree, so the change counts as an edit of the paragraphs and
        // tables it touches (writers keep the markup they read only for nodes that were not edited).
        marks.Add(() =>
        {
            foreach (TrackedChange change in tracked)
            {
                switch (change.Node)
                {
                    case Row row:
                        row.Revision = revision;
                        break;
                    case Paragraph paragraph:
                        paragraph.MarkRevision = revision;
                        break;
                    case Inline inline:
                        inline.Revision = revision;
                        break;
                }
            }
        });

        string text = node is Paragraph para ? para.GetText() : node.GetText().TrimEnd(ControlChar.ParagraphBreak);
        changes.Add(new PendingChange(fromA ? CompareChangeKind.Deleted : CompareChangeKind.Inserted, fromA ? text : "", fromA ? "" : text, node, tracked));
        if (fromA)
        {
            deletedWords += words;
        }
        else
        {
            insertedWords += words;
        }

        paragraphsTouched += paragraphs;
        emit(node, isBase);
    }

    /// <summary>Two blocks paired as one changed block: paragraphs diffed word by word, containers compared inside.</summary>
    private void Paired(Block a, Block b, Action<Block, bool> emit)
    {
        Block node = baseIsRevised ? b : a;
        switch (a, b)
        {
            case (Paragraph pa, Paragraph pb):
                MergeParagraph(Tokens(pa), Tokens(pb));
                break;
            case (ContentControl ca, ContentControl cb):
                CompareContainer(ca.Blocks, cb.Blocks, ((ContentControl)node).Blocks);
                break;
            case (FlexContainer fa, FlexContainer fb):
                for (int k = 0; k < fa.Items.Count; k++)
                {
                    CompareContainer(fa.Items[k].Blocks, fb.Items[k].Blocks, ((FlexContainer)node).Items[k].Blocks);
                }

                break;
            case (GridContainer ga, GridContainer gb):
                for (int k = 0; k < ga.Items.Count; k++)
                {
                    CompareContainer(ga.Items[k].Blocks, gb.Items[k].Blocks, ((GridContainer)node).Items[k].Blocks);
                }

                break;
        }

        emit(node, true);
    }

    private void CompareContainer(NodeCollection<Block> a, NodeCollection<Block> b, NodeCollection<Block> target)
    {
        List<Block> list = [];
        CompareBlocks([.. a], [.. b], (block, _) => list.Add(block));
        Replace(target, list);
    }

    /// <summary>A copy of a block of the other document for the result, without bookmarks, comments and other marks.</summary>
    private T Import<T>(T node)
        where T : Node
    {
        T copy = result.Import(node);
        foreach (Inline mark in copy.Descendants<Inline>().Where(IsMarker).ToList())
        {
            mark.Remove();
        }

        return copy;
    }

    private static bool IsMarker(Inline inline)
    {
        return inline is BookmarkStart or BookmarkEnd or CommentRangeStart or CommentRangeEnd or EditableRangeStart or EditableRangeEnd or Comment;
    }

    // ---- Words ----

    /// <summary>
    /// Rebuilds the base paragraph of a pair from the word diff: equal tokens from the base, tokens only in the original
    /// marked deleted and tokens only in the revised document marked inserted, each taken from its own document.
    /// </summary>
    private void MergeParagraph(ParagraphTokens a, ParagraphTokens b)
    {
        List<DiffStep> script = SequenceDiff.Diff<int>(a.Ids, b.Ids, out bool limit);
        limitReached |= limit;
        ParagraphTokens baseTokens = baseIsRevised ? b : a;
        Emitter fromA = new(this, a, isBase: !baseIsRevised), fromB = new(this, b, isBase: baseIsRevised);
        List<Inline> output = [];
        int i = 0, j = 0;
        for (int s = 0; s < script.Count; s++)
        {
            DiffStep step = script[s];
            if (step.Operation == DiffOperation.Equal)
            {
                if (baseIsRevised)
                {
                    fromB.Emit(b.Starts[j], b.Starts[j + step.Count], null, output, null);
                }
                else
                {
                    fromA.Emit(a.Starts[i], a.Starts[i + step.Count], null, output, null);
                }

                i += step.Count;
                j += step.Count;
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
            fromA.Emit(a.Starts[i], a.Starts[i + deleted], deletion, output, tracked);
            fromB.Emit(b.Starts[j], b.Starts[j + inserted], insertion, output, tracked);
            deletedWords += CountWords(a, i, i + deleted);
            insertedWords += CountWords(b, j, j + inserted);
            CompareChangeKind kind = deleted == 0 ? CompareChangeKind.Inserted : inserted == 0 ? CompareChangeKind.Deleted : CompareChangeKind.Replaced;
            changes.Add(new PendingChange(kind, a.TextOf(i, i + deleted), b.TextOf(j, j + inserted), baseTokens.Paragraph, tracked));
            i += deleted;
            j += inserted;
        }

        (baseIsRevised ? fromB : fromA).Emit(baseTokens.Text.Length, baseTokens.Text.Length, null, output, null);
        paragraphsTouched++;
        Replace(baseTokens.Paragraph.Inlines, output);
    }

    private static int CountWords(ParagraphTokens paragraph, int start, int end)
    {
        int words = 0;
        for (int t = start; t < end; t++)
        {
            if (paragraph.Words[t])
            {
                words++;
            }
        }

        return words;
    }

    private int CountWords(Paragraph paragraph)
    {
        ParagraphTokens t = Tokens(paragraph);
        return CountWords(t, 0, t.Count);
    }

    private ParagraphTokens Tokens(Paragraph paragraph)
    {
        if (!tokens.TryGetValue(paragraph, out ParagraphTokens? t))
        {
            t = ParagraphTokens.Create(paragraph, table);
            tokens[paragraph] = t;
        }

        return t;
    }

    /// <summary>The sorted ids of a block's words, for the Dice coefficient.</summary>
    private int[] WordSet(Block block)
    {
        if (block is Paragraph paragraph)
        {
            if (!wordSets.TryGetValue(paragraph, out int[]? set))
            {
                ParagraphTokens t = Tokens(paragraph);
                set = [.. t.Ids.Where((_, k) => t.Words[k])];
                Array.Sort(set);
                wordSets[paragraph] = set;
            }

            return set;
        }

        int[] all = [.. block.Descendants<Paragraph>().SelectMany(WordSet)];
        Array.Sort(all);
        return all;
    }

    /// <summary>Twice the common words over all words of both, counting repeats: 1 for the same words, 0 for none in common.</summary>
    private static double Dice(int[] a, int[] b)
    {
        if (a.Length + b.Length == 0)
        {
            return 1;
        }

        int common = 0, i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                common++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return 2.0 * common / (a.Length + b.Length);
    }

    /// <summary>A block's fingerprint: equal for blocks that compare equal.</summary>
    private ulong Key(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                return Mix(1, Tokens(paragraph).Key);
            case Table t:
                ulong key = Mix(2, (ulong)t.Rows.Count);
                foreach (Row row in t.Rows)
                {
                    key = Mix(key, (ulong)row.Cells.Count);
                    foreach (Cell cell in row.Cells)
                    {
                        key = Mix(key, Key(cell.Blocks));
                    }
                }

                return key;
            case ContentControl control:
                return Mix(3, Key(control.Blocks));
            case FlexContainer flex:
                return flex.Items.Aggregate(Mix(4, (ulong)flex.Items.Count), (k, item) => Mix(k, Key(item.Blocks)));
            case GridContainer grid:
                return grid.Items.Aggregate(Mix(5, (ulong)grid.Items.Count), (k, item) => Mix(k, Key(item.Blocks)));
            case BlockIsland island:
                return Mix(6, (ulong)StringComparer.Ordinal.GetHashCode(island.Markup) ^ ((ulong)island.Markup.Length << 32));
            default:
                return Mix(7, (ulong)StringComparer.Ordinal.GetHashCode(block.GetText()));
        }
    }

    private ulong Key(NodeCollection<Block> blocks)
    {
        ulong key = Mix(8, (ulong)blocks.Count);
        foreach (Block block in blocks)
        {
            key = Mix(key, Key(block));
        }

        return key;
    }

    private static ulong Mix(ulong a, ulong b)
    {
        ulong hash = (a ^ b) * 0x9E3779B97F4A7C15UL;
        return hash ^ (hash >> 29);
    }

    /// <summary>
    /// Walks one paragraph's inlines in order and hands out the stretches the word diff asks for: the base's own nodes where
    /// a run is taken whole, cut copies where a run is split, and imported copies from the other document.
    /// </summary>
    private sealed class Emitter(BlockComparer owner, ParagraphTokens paragraph, bool isBase)
    {
        private int piece;

        /// <summary>
        /// Adds the inlines of text <paramref name="from"/> to <paramref name="to"/> to <paramref name="output"/>, marked with
        /// <paramref name="revision"/> when one is given; the base's marks before <paramref name="to"/> come along unmarked.
        /// </summary>
        public void Emit(int from, int to, Revision? revision, List<Inline> output, List<TrackedChange>? tracked)
        {
            ParagraphPiece[] pieces = paragraph.Pieces;
            int last = paragraph.Text.Length;
            while (piece < pieces.Length)
            {
                ParagraphPiece p = pieces[piece];
                int end = p.Start + p.Length;
                if (p.Kind == PieceKind.Marker || p.Length == 0)
                {
                    // Marks (and empty runs) go with the stretch they are in; the base's marks at the very end with the last.
                    if (p.Start < to || (to == last && from == last))
                    {
                        if (isBase && p.Kind == PieceKind.Marker)
                        {
                            output.Add(p.First);
                        }

                        piece++;
                        continue;
                    }

                    break;
                }

                if (end <= from)
                {
                    piece++;
                    continue;
                }

                if (p.Start >= to)
                {
                    break;
                }

                int s = Math.Max(from, p.Start), e = Math.Min(to, end);
                if (p.Kind == PieceKind.Text)
                {
                    Run run = (Run)p.First;
                    Run part;
                    if (isBase && s == p.Start && e == end)
                    {
                        part = run;
                    }
                    else
                    {
                        part = isBase ? run.Clone() : owner.result.Import(run);
                        part.Text = run.Text.Substring(s - p.Start, e - s);
                    }

                    Add(part, revision, output, tracked);
                }
                else
                {
                    foreach (Inline inline in ParagraphTokens.InlinesOf(p).ToList())
                    {
                        Add(isBase ? inline : owner.Import(inline), revision, output, tracked);
                    }
                }

                if (e < end)
                {
                    break;
                }

                piece++;
            }
        }

        private static void Add(Inline inline, Revision? revision, List<Inline> output, List<TrackedChange>? tracked)
        {
            if (revision is not null)
            {
                inline.Revision = revision;
                tracked?.Add(new TrackedChange(inline, TrackedChangeTarget.Node));
            }

            output.Add(inline);
        }
    }
}
