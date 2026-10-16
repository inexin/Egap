namespace Egap.Documents;

/// <summary>
/// One clone or import: the document the copies go to, and the styles and lists already brought over, so an import copies
/// each style (with the styles it is based on) and each list once. A whole-document copy finds them by id instead.
/// </summary>
internal sealed class CloneContext(Document target, bool wholeDocument = false)
{
    /// <summary>Whether a whole document is being copied: styles, lists and comments keep their ids.</summary>
    public bool WholeDocument => wholeDocument;

    private Dictionary<object, object>? imported;
    private Dictionary<int, Comment>? comments;
    private List<Inline>? commentRanges;

    public Document Target { get; } = target;

    /// <summary>The style in the target document that stands for <paramref name="style"/>.</summary>
    public T? Map<T>(T? style)
        where T : Style
    {
        if (style is null || !style.IsInDocument)
        {
            return null;
        }

        if (style.Document == Target)
        {
            return style;
        }

        return wholeDocument ? (T?)Target.Styles[style.Id] : (T)Target.Styles.Import(style, this);
    }

    /// <summary>The list in the target document that stands for <paramref name="list"/>.</summary>
    public List? Map(List? list)
    {
        if (list is null || list == List.None || list.Document == Target)
        {
            return list;
        }

        if (list.Document is not { } source || !source.Lists.Contains(list))
        {
            return null;
        }

        return wholeDocument ? Target.Lists[list.Id] : Target.Lists.Import(list, this, Imported);
    }

    /// <summary>What this import has copied so far, source to copy.</summary>
    public Dictionary<object, object> Imported => imported ??= [];

    /// <summary>Notes that a comment with <paramref name="oldId"/> was copied as <paramref name="copy"/>, with a new id.</summary>
    public void TrackComment(int oldId, Comment copy)
    {
        (comments ??= [])[oldId] = copy;
    }

    /// <summary>Notes a copied comment range mark, to point at the copied comment once the clone is done.</summary>
    public void TrackCommentRange(Inline mark)
    {
        (commentRanges ??= []).Add(mark);
    }

    /// <summary>
    /// Ends the clone: range marks and replies of copied comments now name the copies' new ids; marks of comments not
    /// copied keep their ids.
    /// </summary>
    public void Finish()
    {
        if (comments is null)
        {
            return;
        }

        foreach (Inline mark in commentRanges ?? [])
        {
            switch (mark)
            {
                case CommentRangeStart start when comments.TryGetValue(start.CommentId, out Comment? c):
                    start.SetCommentIdSilently(c.Id);
                    break;
                case CommentRangeEnd end when comments.TryGetValue(end.CommentId, out Comment? c):
                    end.SetCommentIdSilently(c.Id);
                    break;
            }
        }

        foreach (Comment copy in comments.Values)
        {
            if (copy.ParentId is int parent && comments.TryGetValue(parent, out Comment? parentCopy))
            {
                copy.SetIdsSilently(copy.Id, parentCopy.Id);
            }
        }
    }

    /// <summary>Points the lists a copied paragraph format names at the target document's.</summary>
    public void FixLists(FormatBase format)
    {
        if (format is ParagraphFormat paragraph && paragraph.GetRaw(ParagraphFormat.ListId) is List list && list != List.None && list.Document != Target)
        {
            paragraph.SetRawSilently(ParagraphFormat.ListId, Map(list));
        }
    }
}
