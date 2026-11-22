using System.Text;

namespace Egap.Documents.Comparing;

/// <summary>The stories beside the main text: headers and footers, and the comments the result keeps.</summary>
internal sealed partial class BlockComparer
{
    // The comments of each input, read before the result is built on one of them: matched by anchor text and comment text.
    private List<CommentRead>? commentsA;
    private List<CommentRead>? commentsB;

    /// <summary>
    /// Compares the headers and footers of the sections at the same place (or whose breaks pair, when the documents have
    /// different sections), each with the one of the same kind; one only in one document is marked inserted or deleted whole.
    /// </summary>
    public void CompareHeadersFooters(Document a, Document b)
    {
        List<(Section A, Section B, Section Target)> pairs = sectionPairs
            ?? [.. Enumerable.Range(0, Math.Min(a.Sections.Count, b.Sections.Count)).Select(k => (a.Sections[k], b.Sections[k], result.Sections[k]))];
        foreach ((Section sectionA, Section sectionB, Section sectionTarget) in pairs)
        {
            foreach (HeaderFooterKind kind in Enum.GetValues<HeaderFooterKind>())
            {
                HeaderFooter? storyA = sectionA.HeadersFooters[kind], storyB = sectionB.HeadersFooters[kind];
                if (storyA is null && storyB is null)
                {
                    continue;
                }

                HeaderFooter target = sectionTarget.HeadersFooters.GetOrAdd(kind);
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

    private static List<CommentRead> ReadComments(Document document)
    {
        Dictionary<int, OpenRange> open = [];
        Dictionary<int, CommentRead> ranges = [];
        List<Comment> comments = [];
        foreach (Inline inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case CommentRangeStart start:
                    open[start.CommentId] = new OpenRange(start.GetAncestor<Paragraph>());
                    break;
                case CommentRangeEnd end when open.Remove(end.CommentId, out OpenRange? range):
                    Paragraph? last = end.GetAncestor<Paragraph>();
                    string tail = last == range.Start ? range.Head.ToString() : range.Paragraph == last ? range.Current.ToString() : "";
                    ranges[end.CommentId] = new CommentRead(null!, range.All.ToString(), range.Start, last, range.Head.ToString(), tail);
                    break;
                case Run run when open.Count > 0 && run.GetAncestor<Comment>() is null:
                    Paragraph? paragraph = run.GetAncestor<Paragraph>();
                    foreach (OpenRange range in open.Values)
                    {
                        range.Add(run.Text, paragraph);
                    }

                    break;
                case Comment comment:
                    comments.Add(comment);
                    break;
            }
        }

        List<CommentRead> list = [];
        foreach (Comment comment in comments)
        {
            string text = "\u0001" + comment.Story.GetText();
            list.Add(ranges.TryGetValue(comment.Id, out CommentRead found)
                ? found with { Comment = comment, Key = found.Key + text }
                : new CommentRead(comment, text, comment.GetAncestor<Paragraph>(), comment.GetAncestor<Paragraph>(), "", ""));
        }

        return list;
    }

    /// <summary>
    /// Gives the result (built on the original) the revised document's comments: those only in the original are removed with
    /// their range, those only in the revised document are added at the same anchor text, or at the end of the paragraph that
    /// holds it when the text is not found. Runs once the result's changes are marked.
    /// </summary>
    private void PlaceComments()
    {
        if (commentsA is null || commentsB is null || placed is null)
        {
            return;
        }

        Dictionary<string, Queue<Comment>> kept = new(StringComparer.Ordinal);
        foreach (CommentRead read in commentsA)
        {
            if (!kept.TryGetValue(read.Key, out Queue<Comment>? queue))
            {
                kept[read.Key] = queue = new Queue<Comment>();
            }

            queue.Enqueue(read.Comment);
        }

        Dictionary<int, int> ids = [];
        List<CommentRead> added = [];
        foreach (CommentRead read in commentsB)
        {
            if (kept.TryGetValue(read.Key, out Queue<Comment>? queue) && queue.TryDequeue(out Comment? match))
            {
                ids[read.Comment.Id] = match.Id;
            }
            else
            {
                added.Add(read);
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

        foreach (CommentRead read in added)
        {
            Paragraph? first = Placed(read.Start), last = Placed(read.End);
            if (first is null && last is null)
            {
                notTracked++;
                continue;
            }

            Comment comment = read.Comment;
            Comment copy = result.Import(comment);
            copy.ParentId = comment.ParentId is int parent && ids.TryGetValue(parent, out int mapped) ? mapped : null;
            ids[comment.Id] = copy.Id;
            CommentRangeEnd rangeEnd = new(result, copy.Id);
            CommentRangeStart rangeStart = new(result, copy.Id);
            if (first is null || last is null || first == last)
            {
                Paragraph target = (first ?? last)!;
                (int start, int end) = Anchor(target, first is null ? read.Tail : read.Head);
                Revise(target, copy, rangeEnd, rangeStart);
                target.Inlines.Insert(end, copy);
                target.Inlines.Insert(end, rangeEnd);
                target.Inlines.Insert(start, rangeStart);
            }
            else
            {
                // A range over paragraphs: it starts where the start paragraph ends with the range's text there, and ends
                // where the end paragraph starts with it.
                int end = Position(last, read.Tail, atEnd: false);
                Revise(last, copy, rangeEnd, null);
                last.Inlines.Insert(end, copy);
                last.Inlines.Insert(end, rangeEnd);
                int start = Position(first, read.Head, atEnd: true);
                Revise(first, rangeStart, null, null);
                first.Inlines.Insert(start, rangeStart);
            }
        }

        // A reply whose comment went is a comment of its own.
        HashSet<int> present = [.. result.Descendants<Comment>().Select(c => c.Id)];
        foreach (Comment orphan in result.Descendants<Comment>().Where(c => c.ParentId is int p && !present.Contains(p)).ToList())
        {
            orphan.ParentId = null;
        }
    }

    /// <summary>The result paragraph that holds the text of <paramref name="paragraph"/>, if it is still in the result.</summary>
    private Paragraph? Placed(Paragraph? paragraph)
    {
        return paragraph is not null && placed!.TryGetValue(paragraph, out Paragraph? target) && target.Parent is not null ? target : null;
    }

    /// <summary>In an inserted paragraph, gives comment marks its insertion, so they go with it when it is rejected.</summary>
    private static void Revise(Paragraph paragraph, Inline first, Inline? second, Inline? third)
    {
        if (paragraph.MarkRevision is { Removes: false } inserted)
        {
            first.Revision = inserted;
            second?.Revision = inserted;
            third?.Revision = inserted;
        }
    }

    /// <summary>
    /// Where a comment's range starts and ends among a result paragraph's inlines: around the first place
    /// <paramref name="anchor"/> stands in the paragraph's revised text, splitting runs as needed, else empty at the
    /// paragraph's end.
    /// </summary>
    private static (int Start, int End) Anchor(Paragraph paragraph, string anchor)
    {
        NodeCollection<Inline> inlines = paragraph.Inlines;
        if (anchor.Length > 0)
        {
            (string text, List<(int Index, int Start, int Length, bool Splittable)> spans) = Spans(inlines);
            int at = text.IndexOf(anchor, StringComparison.Ordinal);
            if (at >= 0 && Cut(inlines, spans, at + anchor.Length, out int end, out _) && Cut(inlines, spans, at, out int start, out bool split))
            {
                // Cutting the start's run in two puts one more inline before the end.
                return (start, split && start <= end ? end + 1 : end);
            }
        }

        return (inlines.Count, inlines.Count);
    }

    /// <summary>
    /// The index among a result paragraph's inlines where its revised text ends with <paramref name="text"/> (when
    /// <paramref name="atEnd"/>) or starts with it, splitting a run as needed; else the paragraph's end or start.
    /// </summary>
    private static int Position(Paragraph paragraph, string text, bool atEnd)
    {
        NodeCollection<Inline> inlines = paragraph.Inlines;
        (string own, List<(int Index, int Start, int Length, bool Splittable)> spans) = Spans(inlines);
        int at;
        if (atEnd)
        {
            at = own.EndsWith(text, StringComparison.Ordinal) ? own.Length - text.Length : own.Length;
        }
        else
        {
            at = own.StartsWith(text, StringComparison.Ordinal) ? text.Length : 0;
        }

        if (atEnd && at == own.Length)
        {
            return inlines.Count;
        }

        return Cut(inlines, spans, at, out int index, out _) ? index : atEnd ? inlines.Count : 0;
    }

    /// <summary>A paragraph's revised text, and where each inline that is not deleted stands in it.</summary>
    private static (string Text, List<(int Index, int Start, int Length, bool Splittable)> Spans) Spans(NodeCollection<Inline> inlines)
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

        return (text.ToString(), spans);
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

    /// <summary>
    /// A comment as read from an input: its key (anchor text and comment text), the paragraphs its range starts and ends in,
    /// and the range's text in each: <see cref="Head"/> from the range start to the start paragraph's end (the whole anchor
    /// when the range is in one paragraph), <see cref="Tail"/> from the end paragraph's start to the range end.
    /// </summary>
    private readonly record struct CommentRead(Comment Comment, string Key, Paragraph? Start, Paragraph? End, string Head, string Tail);

    /// <summary>The text of a comment range being read, as a whole and in its first and current paragraph.</summary>
    private sealed class OpenRange(Paragraph? start)
    {
        public Paragraph? Start { get; } = start;

        public Paragraph? Paragraph { get; private set; } = start;

        public StringBuilder All { get; } = new();

        public StringBuilder Head { get; } = new();

        public StringBuilder Current { get; } = new();

        public void Add(string text, Paragraph? paragraph)
        {
            All.Append(text);
            if (paragraph == Start)
            {
                Head.Append(text);
            }

            if (paragraph != Paragraph)
            {
                Paragraph = paragraph;
                Current.Clear();
            }

            Current.Append(text);
        }
    }
}
