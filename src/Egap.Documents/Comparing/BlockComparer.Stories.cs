using System.Text;

namespace Egap.Documents.Comparing;

/// <summary>The stories beside the main text: headers and footers, and the comments the result keeps.</summary>
internal sealed partial class BlockComparer
{
    // The comments of each input, read before the result is built on one of them: by anchor text and comment text.
    private List<(Comment Comment, string Key, Paragraph? Paragraph, string Anchor)>? commentsA;
    private List<(Comment Comment, string Key, Paragraph? Paragraph, string Anchor)>? commentsB;

    /// <summary>
    /// Compares the headers and footers of the sections at the same place, each with the one of the same kind; one only in
    /// one document is marked inserted or deleted whole.
    /// </summary>
    public void CompareHeadersFooters(Document a, Document b)
    {
        int sections = Math.Min(a.Sections.Count, b.Sections.Count);
        for (int k = 0; k < sections; k++)
        {
            foreach (HeaderFooterKind kind in Enum.GetValues<HeaderFooterKind>())
            {
                HeaderFooter? storyA = a.Sections[k].HeadersFooters[kind], storyB = b.Sections[k].HeadersFooters[kind];
                if (storyA is null && storyB is null)
                {
                    continue;
                }

                HeaderFooter target = result.Sections[k].HeadersFooters.GetOrAdd(kind);
                CompareContainer(storyA?.Blocks ?? (IEnumerable<Block>)[], storyB?.Blocks ?? (IEnumerable<Block>)[], target.Blocks);
            }
        }
    }

    /// <summary>
    /// Reads the comments of both inputs, before the comparison changes the base: the result gets the revised document's
    /// comments, so this matters only when the original is the base.
    /// </summary>
    public void ReadComments(Document a, Document b)
    {
        if (placed is null)
        {
            return;
        }

        commentsA = ReadComments(a);
        commentsB = ReadComments(b);
    }

    private static List<(Comment, string, Paragraph?, string)> ReadComments(Document document)
    {
        Dictionary<int, (StringBuilder Text, Paragraph? Paragraph)> open = [];
        Dictionary<int, (string Anchor, Paragraph? Paragraph)> anchors = [];
        List<Comment> comments = [];
        foreach (Inline inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case CommentRangeStart start:
                    open[start.CommentId] = (new StringBuilder(), start.GetAncestor<Paragraph>());
                    break;
                case CommentRangeEnd end when open.Remove(end.CommentId, out var range):
                    anchors[end.CommentId] = (range.Text.ToString(), range.Paragraph);
                    break;
                case Run run when open.Count > 0 && run.GetAncestor<Comment>() is null:
                    foreach ((StringBuilder text, _) in open.Values)
                    {
                        text.Append(run.Text);
                    }

                    break;
                case Comment comment:
                    comments.Add(comment);
                    break;
            }
        }

        List<(Comment, string, Paragraph?, string)> list = [];
        foreach (Comment comment in comments)
        {
            (string anchor, Paragraph? paragraph) = anchors.TryGetValue(comment.Id, out var found) ? found : ("", comment.GetAncestor<Paragraph>());
            list.Add((comment, anchor + "\u0001" + comment.Story.GetText(), paragraph ?? comment.GetAncestor<Paragraph>(), anchor));
        }

        return list;
    }

    /// <summary>
    /// Gives the result (built on the original) the revised document's comments: those only in the original are removed with
    /// their range, those only in the revised document are added at the same anchor text, or around the paragraph that holds
    /// it when the text is not found. Runs once the result's changes are marked.
    /// </summary>
    private void PlaceComments()
    {
        if (commentsA is null || commentsB is null || placed is null)
        {
            return;
        }

        Dictionary<string, Queue<Comment>> kept = new(StringComparer.Ordinal);
        foreach ((Comment comment, string key, _, _) in commentsA)
        {
            if (!kept.TryGetValue(key, out Queue<Comment>? queue))
            {
                kept[key] = queue = new Queue<Comment>();
            }

            queue.Enqueue(comment);
        }

        Dictionary<int, int> ids = [];
        List<(Comment Comment, Paragraph? Paragraph, string Anchor)> added = [];
        foreach ((Comment comment, string key, Paragraph? paragraph, string anchor) in commentsB)
        {
            if (kept.TryGetValue(key, out Queue<Comment>? queue) && queue.TryDequeue(out Comment? match))
            {
                ids[comment.Id] = match.Id;
            }
            else
            {
                added.Add((comment, paragraph, anchor));
            }
        }

        foreach (Comment removed in kept.Values.SelectMany(q => q))
        {
            int id = removed.Id;
            foreach (Inline mark in result.Descendants<Inline>().Where(i => i is CommentRangeStart s && s.CommentId == id || i is CommentRangeEnd e && e.CommentId == id).ToList())
            {
                mark.Remove();
            }

            removed.Remove();
        }

        foreach ((Comment comment, Paragraph? paragraph, string anchor) in added)
        {
            if (paragraph is null || !placed.TryGetValue(paragraph, out Paragraph? target) || target.Parent is null)
            {
                notTracked++;
                continue;
            }

            Comment copy = result.Import(comment);
            copy.ParentId = comment.ParentId is int parent && ids.TryGetValue(parent, out int mapped) ? mapped : null;
            ids[comment.Id] = copy.Id;
            (int start, int end) = Anchor(target, anchor);
            CommentRangeEnd rangeEnd = new(result, copy.Id);
            CommentRangeStart rangeStart = new(result, copy.Id);
            if (target.MarkRevision is { Removes: false } inserted)
            {
                // In an inserted paragraph the comment goes with it when the insertion is rejected.
                copy.Revision = rangeEnd.Revision = rangeStart.Revision = inserted;
            }

            target.Inlines.Insert(end, copy);
            target.Inlines.Insert(end, rangeEnd);
            target.Inlines.Insert(start, rangeStart);
        }

        // A reply whose comment went is a comment of its own.
        HashSet<int> present = [.. result.Descendants<Comment>().Select(c => c.Id)];
        foreach (Comment orphan in result.Descendants<Comment>().Where(c => c.ParentId is int p && !present.Contains(p)).ToList())
        {
            orphan.ParentId = null;
        }
    }

    /// <summary>
    /// Where a comment's range starts and ends among a result paragraph's inlines: around the first place
    /// <paramref name="anchor"/> stands in the paragraph's revised text, splitting runs as needed, else around the whole
    /// paragraph.
    /// </summary>
    private static (int Start, int End) Anchor(Paragraph paragraph, string anchor)
    {
        NodeCollection<Inline> inlines = paragraph.Inlines;
        if (anchor.Length == 0)
        {
            // A range around no text stays empty.
            return (inlines.Count, inlines.Count);
        }

        if (anchor.Length > 0)
        {
            StringBuilder text = new();
            List<(int Index, int Start, int Length, bool Splittable)> spans = [];
            for (int i = 0; i < inlines.Count; i++)
            {
                if (inlines[i].Revision is { Removes: true })
                {
                    continue;
                }

                string own = inlines[i] is Run run ? run.Text : inlines[i] is Comment ? "" : inlines[i].GetText();
                spans.Add((i, text.Length, own.Length, inlines[i] is Run));
                text.Append(own);
            }

            int at = text.ToString().IndexOf(anchor, StringComparison.Ordinal);
            if (at >= 0 && Cut(inlines, spans, at + anchor.Length, out int end, out _) && Cut(inlines, spans, at, out int start, out bool split))
            {
                // Cutting the start's run in two puts one more inline before the end.
                return (start, split && start <= end ? end + 1 : end);
            }
        }

        return (0, inlines.Count);
    }

    /// <summary>
    /// The index in <paramref name="inlines"/> where text position <paramref name="position"/> falls between inlines, a run
    /// cut in two there when it falls inside one; false when it falls inside an inline that cannot be cut.
    /// </summary>
    private static bool Cut(NodeCollection<Inline> inlines, List<(int Index, int Start, int Length, bool Splittable)> spans, int position, out int index, out bool split)
    {
        split = false;
        foreach ((int i, int start, int length, bool splittable) in spans)
        {
            if (position <= start)
            {
                index = i;
                return true;
            }

            if (position < start + length)
            {
                if (!splittable)
                {
                    index = -1;
                    return false;
                }

                Run run = (Run)inlines[i];
                Run tail = run.Clone();
                tail.Text = run.Text[(position - start)..];
                run.Text = run.Text[..(position - start)];
                inlines.Insert(i + 1, tail);
                index = i + 1;
                split = true;
                return true;
            }
        }

        index = spans.Count == 0 ? inlines.Count : spans[^1].Index + 1;
        return true;
    }
}
