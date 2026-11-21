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
    private readonly string author;
    private readonly DateTimeOffset? date;
    private readonly Dictionary<ParagraphTokens, Run?[]> runsAt = [];
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
        author = options.Author;
        date = options.Date;
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
                    CompareFormatting(a[i + k], b[j + k]);
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
        return Pair(a, b, Score);
    }

    /// <summary>Pairs items of two changed stretches whose score reaches the threshold, best pairs first, keeping their order.</summary>
    private static List<(int A, int B)> Pair<T>(List<T> a, List<T> b, Func<T, T, double> score)
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
                double value = score(a[i], b[j]);
                if (value >= PairThreshold)
                {
                    candidates.Add((value, i, j));
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
            (Table ta, Table tb) => 0.5 + (0.5 * Dice(WordSet(ta), WordSet(tb))),
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
            case (Table ta, Table tb) when !CompareTable(ta, tb, (Table)node):
                // No row in common: the original table goes and the revised one comes, whole.
                Whole(a, fromA: true, emit);
                Whole(b, fromA: false, emit);
                return;
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

    /// <summary>
    /// Compares two tables row by row into <paramref name="target"/> (the base's): rows with the same content stay, rows that
    /// changed are paired (same number of cells) and compared cell by cell as stories, nested tables in turn, and the rest
    /// are marked inserted or deleted. Returns false, changing nothing, when no row stays or pairs.
    /// </summary>
    private bool CompareTable(Table a, Table b, Table target)
    {
        List<Row> rowsA = [.. a.Rows], rowsB = [.. b.Rows];
        ulong[] keysA = [.. rowsA.Select(Key)], keysB = [.. rowsB.Select(Key)];
        List<DiffStep> script = SequenceDiff.Diff<ulong>(keysA, keysB, out bool limit);

        // Plan first, so a table with nothing in common is left to the caller untouched.
        List<(Row? A, Row? B)> plan = [];
        int i = 0, j = 0, kept = 0;
        for (int s = 0; s < script.Count; s++)
        {
            DiffStep step = script[s];
            if (step.Operation == DiffOperation.Equal)
            {
                for (int k = 0; k < step.Count; k++)
                {
                    plan.Add((rowsA[i + k], rowsB[j + k]));
                }

                kept += step.Count;
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

            List<Row> stretchA = rowsA.GetRange(i, deleted), stretchB = rowsB.GetRange(j, inserted);
            List<(int A, int B)> pairs;
            if (deleted == inserted && stretchA.Zip(stretchB).All(p => p.First.Cells.Count == p.Second.Cells.Count))
            {
                // As many rows on both sides, cell for cell alike in shape: each row was edited where it stands.
                pairs = [.. Enumerable.Range(0, deleted).Select(k => (k, k))];
            }
            else
            {
                pairs = Pair(stretchA, stretchB, (x, y) => x.Cells.Count == y.Cells.Count ? Dice(WordSet(x), WordSet(y)) : 0);
            }

            int pi = 0, pj = 0;
            foreach ((int pa, int pb) in pairs.Append((deleted, inserted)))
            {
                for (; pi < pa; pi++)
                {
                    plan.Add((stretchA[pi], null));
                }

                for (; pj < pb; pj++)
                {
                    plan.Add((null, stretchB[pj]));
                }

                if (pa < deleted)
                {
                    plan.Add((stretchA[pa], stretchB[pb]));
                    kept++;
                    pi = pa + 1;
                    pj = pb + 1;
                }
            }

            i += deleted;
            j += inserted;
        }

        if (kept == 0)
        {
            return false;
        }

        limitReached |= limit;
        List<Row> output = [];
        foreach ((Row? rowA, Row? rowB) in plan)
        {
            if (rowA is not null && rowB is not null)
            {
                Row node = baseIsRevised ? rowB : rowA;
                for (int k = 0; k < node.Cells.Count; k++)
                {
                    CompareContainer(rowA.Cells[k].Blocks, rowB.Cells[k].Blocks, node.Cells[k].Blocks);
                }

                output.Add(node);
            }
            else
            {
                output.Add(WholeRow(rowA ?? rowB!, fromA: rowA is not null));
            }
        }

        Replace(target.Rows, output);
        return true;
    }

    /// <summary>A row only in one document: marked deleted (from A) or inserted (from B), with its text.</summary>
    private Row WholeRow(Row row, bool fromA)
    {
        bool isBase = fromA != baseIsRevised;
        Revision revision = fromA ? deletion : insertion;
        Row node = isBase ? row : Import(row);
        List<TrackedChange> tracked = [new TrackedChange(node, TrackedChangeTarget.Row)];
        int words = 0;
        foreach (Paragraph paragraph in node.Descendants<Paragraph>())
        {
            words += CountWords(paragraph);
            paragraphsTouched++;
            foreach (Inline inline in paragraph.Inlines)
            {
                tracked.Add(new TrackedChange(inline, TrackedChangeTarget.Node));
            }
        }

        marks.Add(() =>
        {
            foreach (TrackedChange change in tracked)
            {
                if (change.Node is Row r)
                {
                    r.Revision = revision;
                }
                else
                {
                    ((Inline)change.Node).Revision = revision;
                }
            }
        });

        string text = string.Join("\t", node.Cells.Select(c => c.GetText().TrimEnd(ControlChar.ParagraphBreak)));
        changes.Add(new PendingChange(fromA ? CompareChangeKind.Deleted : CompareChangeKind.Inserted, fromA ? text : "", fromA ? "" : text, node, tracked));
        if (fromA)
        {
            deletedWords += words;
        }
        else
        {
            insertedWords += words;
        }

        return node;
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
        bool touched = false;
        for (int s = 0; s < script.Count; s++)
        {
            DiffStep step = script[s];
            if (step.Operation == DiffOperation.Equal)
            {
                touched |= EmitEqual(a, b, i, j, step.Count, baseIsRevised ? fromB : fromA, output);
                i += step.Count;
                j += step.Count;
                continue;
            }

            touched = true;

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
        touched |= CompareParagraphFormat(a.Paragraph, b.Paragraph);
        if (touched)
        {
            paragraphsTouched++;
        }

        Replace(baseTokens.Paragraph.Inlines, output);
    }

    // ---- Formatting ----

    /// <summary>
    /// Compares the formatting of blocks whose text is the same: paragraph by paragraph, in tables and containers too.
    /// </summary>
    private void CompareFormatting(Block a, Block b)
    {
        if (a is Paragraph pa && b is Paragraph pb)
        {
            MergeParagraph(Tokens(pa), Tokens(pb));
            return;
        }

        if (a is Table or ContentControl or FlexContainer or GridContainer && a.GetType() == b.GetType())
        {
            List<Paragraph> paragraphsA = [.. a.Descendants<Paragraph>()], paragraphsB = [.. b.Descendants<Paragraph>()];
            if (paragraphsA.Count == paragraphsB.Count)
            {
                for (int k = 0; k < paragraphsA.Count; k++)
                {
                    MergeParagraph(Tokens(paragraphsA[k]), Tokens(paragraphsB[k]));
                }
            }
        }
    }

    /// <summary>
    /// Emits tokens that are equal in both paragraphs from the base, cut where either side's run changes, and marks the
    /// pieces whose formatting differs as format changes. Returns whether any did.
    /// </summary>
    private bool EmitEqual(ParagraphTokens a, ParagraphTokens b, int i, int j, int count, Emitter emitter, List<Inline> output)
    {
        Run?[] runsA = RunsAt(a), runsB = RunsAt(b);
        List<(int Start, int End, Run? A, Run? B)> pieces = [];
        for (int k = 0; k < count; k++)
        {
            int startA = a.Starts[i + k], endA = a.Starts[i + k + 1], startB = b.Starts[j + k], endB = b.Starts[j + k + 1];
            int start = baseIsRevised ? startB : startA, length = baseIsRevised ? endB - startB : endA - startA;
            if (endA - startA == endB - startB)
            {
                for (int o = 0; o < length; o++)
                {
                    Add(start + o, start + o + 1, runsA[startA + o], runsB[startB + o]);
                }
            }
            else
            {
                // Folded white space or case of another length: the token goes by the runs it starts in.
                Add(start, start + length, length == 0 ? null : runsA[startA], length == 0 ? null : runsB[startB]);
            }
        }

        bool changed = false;
        foreach ((int start, int end, Run? runA, Run? runB) in pieces)
        {
            int before = output.Count;
            emitter.Emit(start, end, null, output, null);
            if (runA is not null && runB is not null && !SameFormatting(runA, runB))
            {
                MarkFormat(output.GetRange(before, output.Count - before).OfType<Run>().ToList(), runA, runB, (baseIsRevised ? b : a).Paragraph);
                changed = true;
            }
        }

        return changed;

        void Add(int start, int end, Run? runA, Run? runB)
        {
            if (pieces.Count > 0 && pieces[^1] is var last && last.End == start && last.A == runA && last.B == runB)
            {
                pieces[^1] = last with { End = end };
            }
            else
            {
                pieces.Add((start, end, runA, runB));
            }
        }
    }

    /// <summary>The run each character of a paragraph's compared text is in; null for atoms and marks.</summary>
    private Run?[] RunsAt(ParagraphTokens paragraph)
    {
        if (!runsAt.TryGetValue(paragraph, out Run?[]? runs))
        {
            runs = new Run?[paragraph.Text.Length];
            foreach (ParagraphPiece piece in paragraph.Pieces)
            {
                if (piece.Kind == PieceKind.Text)
                {
                    Array.Fill(runs, (Run)piece.First, piece.Start, piece.Length);
                }
            }

            runsAt[paragraph] = runs;
        }

        return runs;
    }

    /// <summary>
    /// Whether two runs are formatted alike: they look alike, or they set the same direct formatting and character style
    /// (so a look that differs only through the paragraph's style is the paragraph's change).
    /// </summary>
    private static bool SameFormatting(Run a, Run b)
    {
        return a.Style?.Id == b.Style?.Id && SameDirect(a.Format, b.Format) || SameLook(a.GetEffectiveFormat(), b.GetEffectiveFormat());
    }

    /// <summary>Whether two runs' formatting resolves to the same look (language and proofing left out).</summary>
    private static bool SameLook(ResolvedRunFormat a, ResolvedRunFormat b)
    {
        return (a.Bold, a.Italic, a.Underline, a.UnderlineColor, a.Strikethrough, a.DoubleStrikethrough, a.AllCaps, a.SmallCaps, a.Hidden, a.Overline)
                == (b.Bold, b.Italic, b.Underline, b.UnderlineColor, b.Strikethrough, b.DoubleStrikethrough, b.AllCaps, b.SmallCaps, b.Hidden, b.Overline)
            && (a.Size, a.Color, a.Highlight, a.VerticalPosition, a.Position, a.Spacing, a.Scale, a.FontAscii, a.FontEastAsia, a.FontComplexScript)
                == (b.Size, b.Color, b.Highlight, b.VerticalPosition, b.Position, b.Spacing, b.Scale, b.FontAscii, b.FontEastAsia, b.FontComplexScript)
            && Equals(a.Shading, b.Shading) && Equals(a.Border, b.Border) && Equals(a.Emboss, b.Emboss) && Equals(a.Imprint, b.Imprint)
            && Equals(a.Outline, b.Outline) && Equals(a.Shadow, b.Shadow);
    }

    /// <summary>Whether two formats set the same properties to the same values (lists only as being set or not).</summary>
    private static bool SameDirect(FormatBase a, FormatBase b)
    {
        for (int id = 0; id < a.PropertyCount; id++)
        {
            object? x = a.GetRaw(id), y = b.GetRaw(id);
            bool same = (x, y) switch
            {
                (null, null) => true,
                (null, _) or (_, null) => false,
                (List, List) => true,
                (Array ax, Array ay) => System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(ax, ay),
                _ => x.Equals(y),
            };
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two paragraphs' formatting resolves to the same look, their lists only as being in one or not.</summary>
    private static bool SameLook(ResolvedParagraphFormat a, ResolvedParagraphFormat b)
    {
        return (a.Alignment, a.LeftIndent, a.RightIndent, a.FirstLineIndent, a.SpaceBefore, a.SpaceAfter, a.LineSpacing, a.LineSpacingRule, a.OutlineLevel)
                == (b.Alignment, b.LeftIndent, b.RightIndent, b.FirstLineIndent, b.SpaceBefore, b.SpaceAfter, b.LineSpacing, b.LineSpacingRule, b.OutlineLevel)
            && (a.KeepWithNext, a.KeepTogether, a.PageBreakBefore, a.ContextualSpacing, a.Bidi, a.List is null, a.ListLevel)
                == (b.KeepWithNext, b.KeepTogether, b.PageBreakBefore, b.ContextualSpacing, b.Bidi, b.List is null, b.ListLevel)
            && a.TabStops.SequenceEqual(b.TabStops) && Equals(a.Shading, b.Shading) && Equals(a.BorderTop, b.BorderTop)
            && Equals(a.BorderBottom, b.BorderBottom) && Equals(a.BorderLeft, b.BorderLeft) && Equals(a.BorderRight, b.BorderRight);
    }

    /// <summary>
    /// Marks base runs whose text is equal but whose formatting differs: the result's run takes the revised document's
    /// formatting and style, and its format revision holds the original's.
    /// </summary>
    private void MarkFormat(List<Run> runs, Run runA, Run runB, Paragraph paragraph)
    {
        if (runs.Count == 0)
        {
            return;
        }

        // The other document's run, copied into the result so its style is the result's.
        Run other = result.Import(baseIsRevised ? runA : runB, deep: false);
        List<TrackedChange> tracked = [.. runs.Select(r => new TrackedChange(r, TrackedChangeTarget.Format))];
        marks.Add(() =>
        {
            foreach (Run run in runs)
            {
                RunFormat old = new();
                CharacterStyle? oldStyle;
                if (baseIsRevised)
                {
                    old.Apply(other.Format);
                    oldStyle = other.Style;
                }
                else
                {
                    old.Apply(run.Format);
                    oldStyle = run.Style;
                    run.Format.ClearFormatting();
                    run.Format.Apply(other.Format);
                    run.Style = other.Style;
                }

                bool styleChanged = oldStyle != run.Style;
                run.FormatRevision = new FormatRevision(author, date, old) { StyleChanged = styleChanged, OldStyle = styleChanged ? oldStyle : null };
            }
        });

        string text = string.Concat(runs.Select(r => r.Text));
        changes.Add(new PendingChange(CompareChangeKind.Formatted, text, text, paragraph, tracked));
    }

    /// <summary>
    /// Compares a pair's paragraph formatting and style; where they differ the result's paragraph takes the revised
    /// document's and its format revision holds the original's. Returns whether they differ.
    /// </summary>
    private bool CompareParagraphFormat(Paragraph a, Paragraph b)
    {
        if (a.Style?.Id == b.Style?.Id && (SameDirect(a.Format, b.Format) || SameLook(a.GetEffectiveFormat(), b.GetEffectiveFormat())))
        {
            return false;
        }

        Paragraph node = baseIsRevised ? b : a;
        Paragraph other = result.Import(baseIsRevised ? a : b, deep: false);
        marks.Add(() =>
        {
            ParagraphFormat old = new();
            ParagraphStyle? oldStyle;
            if (baseIsRevised)
            {
                old.Apply(other.Format);
                oldStyle = other.Style;
            }
            else
            {
                old.Apply(node.Format);
                oldStyle = node.Style;
                node.Format.ClearFormatting();
                node.Format.Apply(other.Format);
                node.Style = other.Style;
            }

            bool styleChanged = oldStyle != node.Style;
            node.FormatRevision = new FormatRevision(author, date, old) { StyleChanged = styleChanged, OldStyle = styleChanged ? oldStyle : null };
        });

        string text = node.GetText().TrimEnd(ControlChar.ParagraphBreak);
        changes.Add(new PendingChange(CompareChangeKind.Formatted, text, text, node, [new TrackedChange(node, TrackedChangeTarget.Format)]));
        return true;
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
    private int[] WordSet(Node block)
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
                return t.Rows.Aggregate(Mix(2, (ulong)t.Rows.Count), (k, row) => Mix(k, Key(row)));
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

    /// <summary>A row's fingerprint: its cells' contents in order.</summary>
    private ulong Key(Row row)
    {
        ulong key = Mix(9, (ulong)row.Cells.Count);
        foreach (Cell cell in row.Cells)
        {
            key = Mix(key, Key(cell.Blocks));
        }

        return key;
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
