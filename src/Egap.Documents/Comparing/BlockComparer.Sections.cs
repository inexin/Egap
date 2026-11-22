namespace Egap.Documents.Comparing;

/// <summary>Documents with different sections: the main text compared as one run of blocks, and the section breaks tracked.</summary>
internal sealed partial class BlockComparer
{
    // While the bodies of documents with different sections are compared: each top-level block of the non-base document and
    // the result's block that stands for it (its copy, or the base block it was paired or equal with), and the paragraphs
    // marked inserted or deleted whole.
    private Dictionary<Block, Block>? counterparts;
    private HashSet<Block>? wholes;

    // The paragraphs whose tracked marks are section breaks only in one document, which accepting or rejecting removes.
    private readonly HashSet<Paragraph> breakMarks = [];

    // The sections whose headers and footers are compared: (original's, revised document's, result's). Null: by index.
    private List<(Section A, Section B, Section Target)>? sectionPairs;

    /// <summary>Notes the result's block that stands for a top-level block of the non-base document.</summary>
    private void Placed(Block other, Block node)
    {
        counterparts?.TryAdd(other, node);
    }

    /// <summary>
    /// Compares the bodies of documents with different sections as one run of blocks, keeping each section break as a tracked
    /// paragraph mark, as Word does: accepting or rejecting the mark takes the break with it (the section joins the next).
    /// <list type="bullet">
    /// <item>A break in both documents, after the same block, stays; its two sections' headers and footers are compared.</item>
    /// <item>A break only in one document, after a paragraph only there, is that paragraph's tracked mark.</item>
    /// <item>
    /// A break only in one document after a paragraph in both goes on an empty copy of the paragraph after it, whose mark is
    /// tracked as the break's side (deleted for the original's), the paragraph's own mark the other way: accepting or
    /// rejecting joins the paragraph into the copy, or the copy into the next section, so the paragraph ends its section in
    /// one document only. After a table the empty paragraph alone is tracked.
    /// </item>
    /// </list>
    /// The result's sections are the base document's, and a section like the other document's for each of its own breaks.
    /// </summary>
    private void CompareSectionsDiffer(Document a, Document b)
    {
        Document other = baseIsRevised ? a : b;
        List<(Block Block, Body Body)> blocksA = Flatten(a), blocksB = Flatten(b);
        Dictionary<Block, Body> bodyOf = [];
        foreach ((Block block, Body body) in baseIsRevised ? blocksB : blocksA)
        {
            bodyOf[block] = body;
        }

        List<(Block Block, bool IsBase)> items = [];
        counterparts = [];
        wholes = [];
        CompareBlocks([.. blocksA.Select(x => x.Block)], [.. blocksB.Select(x => x.Block)], (block, isBase) => items.Add((block, isBase)));
        Dictionary<Block, Block> placedAt = counterparts;
        HashSet<Block> whole = wholes;
        counterparts = null;
        wholes = null;

        // The other document's breaks, by the result's block they follow.
        Dictionary<Block, Section> otherBreaks = [];
        for (int k = 0; k < other.Sections.Count - 1; k++)
        {
            Section section = other.Sections[k];
            if (section.Body.Blocks.Count > 0 && placedAt.TryGetValue(section.Body.Blocks[^1], out Block? node))
            {
                otherBreaks.TryAdd(node, section);
            }
        }

        List<Section> baseSections = [.. result.Sections];
        sectionPairs = [];
        if (other.Sections.Count > 0 && baseSections.Count > 0)
        {
            AddPair(other.Sections[^1], baseSections[^1]);
        }

        // Each block goes in its base section, a block only in the other document with the next base block's.
        Body?[] owner = new Body?[items.Count];
        Body? next = baseSections.Count > 0 ? baseSections[^1].Body : null;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].IsBase)
            {
                next = bodyOf[items[i].Block];
            }

            owner[i] = next;
        }

        if (next is null && items.Count > 0)
        {
            Section added = result.AddSection();
            baseSections.Add(added);
            for (int i = 0; i < owner.Length; i++)
            {
                owner[i] ??= added.Body;
            }
        }

        Dictionary<Body, List<(Section? Added, List<Block> Blocks)>> parts = [];
        foreach (Section section in baseSections)
        {
            parts[section.Body] = [(null, [])];
        }

        // The other document's own breaks are its sections' (the original's when the revised document is the base).
        Revision otherBreak = baseIsRevised ? deletion : insertion, baseBreak = baseIsRevised ? insertion : deletion;
        for (int i = 0; i < items.Count; i++)
        {
            Block block = items[i].Block;
            List<(Section? Added, List<Block> Blocks)> list = parts[owner[i]!];
            list[^1].Blocks.Add(block);
            bool baseEnd = items[i].IsBase && bodyOf[block] is { Section: { } baseSection } body && ReferenceEquals(body.Blocks[^1], block)
                && baseSection != baseSections[^1];
            otherBreaks.TryGetValue(block, out Section? from);
            if (baseEnd && from is not null)
            {
                AddPair(from, bodyOf[block].Section!);
            }
            else if (from is not null)
            {
                if (block is Paragraph wholeParagraph && whole.Contains(block))
                {
                    breakMarks.Add(wholeParagraph);
                }
                else
                {
                    list[^1].Blocks.Add(BreakParagraph(block, otherBreak));
                }

                list[^1] = (NewSection(from), list[^1].Blocks);
                list.Add((null, []));
            }
            else if (baseEnd && block is Paragraph wholeParagraph && whole.Contains(block))
            {
                breakMarks.Add(wholeParagraph);
            }
            else if (baseEnd)
            {
                list[^1].Blocks.Add(BreakParagraph(block, baseBreak));
            }
        }

        // Base blocks can move to an added section before their own, so every body lets go of its blocks first.
        foreach (Section section in baseSections.Where(s => parts[s.Body].Count > 1))
        {
            section.Body.Blocks.Clear();
        }

        foreach (Section section in baseSections)
        {
            List<(Section? Added, List<Block> Blocks)> list = parts[section.Body];
            int at = result.Sections.IndexOf(section);
            foreach ((Section? added, List<Block> blocks) in list.Take(list.Count - 1))
            {
                result.Sections.Insert(at++, added!);
                Replace(added!.Body.Blocks, blocks);
            }

            Replace(section.Body.Blocks, list[^1].Blocks);
        }

        void AddPair(Section otherSection, Section baseSection)
        {
            sectionPairs.Add(baseIsRevised ? (otherSection, baseSection, baseSection) : (baseSection, otherSection, baseSection));
        }
    }

    /// <summary>
    /// An empty paragraph after <paramref name="block"/> that holds a section break only in one document, its mark tracked
    /// with <paramref name="revision"/>; a paragraph before it gets the other change on its own mark.
    /// </summary>
    private Paragraph BreakParagraph(Block block, Revision revision)
    {
        bool deleted = revision == deletion;
        List<TrackedChange> tracked = [];
        Paragraph paragraph;
        if (block is Paragraph { MarkRevision: null } before)
        {
            paragraph = before.Clone(deep: false);
            paragraph.FormatRevision = null;
            Revision other = deleted ? insertion : deletion;
            marks.Add(() => before.MarkRevision = other);
            tracked.Add(new TrackedChange(before, TrackedChangeTarget.ParagraphMark));
        }
        else
        {
            paragraph = new(result);
        }

        marks.Add(() => paragraph.MarkRevision = revision);
        breakMarks.Add(paragraph);
        tracked.Add(new TrackedChange(paragraph, TrackedChangeTarget.ParagraphMark));
        changes.Add(new PendingChange(deleted ? CompareChangeKind.Deleted : CompareChangeKind.Inserted, deleted ? "\r" : "", deleted ? "" : "\r", paragraph, tracked));
        paragraphsTouched++;
        return paragraph;
    }

    /// <summary>
    /// Keeps the section breaks both documents have: a paragraph inserted or deleted whole at the end of a section would take
    /// the break with its mark, so the mark change moves to the last paragraph before it that is in both documents. Accepting
    /// or rejecting then joins that paragraph's text into the section's last paragraph, whose formatting is tracked to match.
    /// </summary>
    private void KeepSectionBreaks()
    {
        foreach (Section section in result.Sections.Take(result.Sections.Count - 1))
        {
            if (section.Body.Blocks.Count == 0 || section.Body.Blocks[^1] is not Paragraph { MarkRevision: { } revision } last || breakMarks.Contains(last))
            {
                continue;
            }

            Node? node = last.PreviousSibling;
            while (node is Paragraph { MarkRevision: { } chained } && chained.Removes == revision.Removes)
            {
                node = node.PreviousSibling;
            }

            if (node is not Paragraph { MarkRevision: null } kept)
            {
                continue;
            }

            kept.MarkRevision = revision;
            last.MarkRevision = null;
            foreach (PendingChange change in changes)
            {
                int at = change.Tracked.FindIndex(t => t.Node == last && t.Target == TrackedChangeTarget.ParagraphMark);
                if (at >= 0)
                {
                    change.Tracked[at] = new TrackedChange(kept, TrackedChangeTarget.ParagraphMark);
                }
            }

            if (!formatting || last.FormatRevision is not null
                || kept.Style?.Id == last.Style?.Id && (SameDirect(kept.Format, last.Format) || SameLook(kept.GetEffectiveFormat(), last.GetEffectiveFormat())))
            {
                continue;
            }

            // The joined paragraph is the last one's node: deleted, it ends up with the kept paragraph's text and look;
            // inserted, it is the kept paragraph again once the insertion is rejected.
            Paragraph from = revision.Removes ? last : kept;
            ParagraphFormat old = new();
            old.Apply(from.Format);
            ParagraphStyle? oldStyle = from.Style;
            if (revision.Removes)
            {
                last.Format.ClearFormatting();
                last.Format.Apply(kept.Format);
                last.Style = kept.Style;
            }

            bool styleChanged = oldStyle != last.Style;
            last.FormatRevision = new FormatRevision(author, date, old) { StyleChanged = styleChanged, OldStyle = styleChanged ? oldStyle : null };
        }
    }

    /// <summary>A section for the result like one of the other document: its page setup, headers and footers, no text.</summary>
    private Section NewSection(Section from)
    {
        Section copy = result.Import(from);
        copy.Body.Blocks.Clear();
        foreach (Inline mark in copy.Descendants<Inline>().Where(IsMarker).ToList())
        {
            mark.Remove();
        }

        return copy;
    }
}
