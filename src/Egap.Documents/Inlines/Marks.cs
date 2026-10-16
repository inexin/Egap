namespace Egap.Documents;

/// <summary>The start of a bookmark: a named place, or a named range up to the <see cref="BookmarkEnd"/> of the same name.</summary>
public sealed class BookmarkStart : Inline
{
    private string name;

    /// <summary>A bookmark start named <paramref name="name"/>, not yet in a paragraph.</summary>
    public BookmarkStart(Document document, string name)
        : base(document)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        this.name = name;
    }

    /// <summary>The bookmark's name; a start and an end pair by it.</summary>
    public string Name
    {
        get => name;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            SetProperty(ref name, value);
        }
    }

    /// <inheritdoc/>
    public override BookmarkStart Clone(bool deep = true)
    {
        return (BookmarkStart)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitBookmarkStart(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new BookmarkStart(context.Target, name));
    }
}

/// <summary>The end of a bookmark range.</summary>
public sealed class BookmarkEnd : Inline
{
    private string name;

    /// <summary>A bookmark end named <paramref name="name"/>, not yet in a paragraph.</summary>
    public BookmarkEnd(Document document, string name)
        : base(document)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        this.name = name;
    }

    /// <summary>The bookmark's name.</summary>
    public string Name
    {
        get => name;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            SetProperty(ref name, value);
        }
    }

    /// <inheritdoc/>
    public override BookmarkEnd Clone(bool deep = true)
    {
        return (BookmarkEnd)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitBookmarkEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new BookmarkEnd(context.Target, name));
    }
}

/// <summary>A bookmark found in a document: its start, its end if any, and the text between.</summary>
/// <param name="Start">The bookmark's start.</param>
/// <param name="End">The bookmark's end, or null for a bookmark with no end.</param>
public sealed record Bookmark(BookmarkStart Start, BookmarkEnd? End)
{
    /// <summary>The bookmark's name.</summary>
    public string Name => Start.Name;

    /// <summary>The text between the start and the end, paragraph breaks included; empty for a place without a range.</summary>
    public string Text => End is null ? "" : TextRange.Between(Start, End);
}

/// <summary>The start of the text a <see cref="Comment"/> is about; it pairs with the comment by <see cref="CommentId"/>.</summary>
public sealed class CommentRangeStart : Inline
{
    private int commentId;

    /// <summary>A comment range start for <paramref name="comment"/>, not yet in a paragraph.</summary>
    public CommentRangeStart(Document document, Comment comment)
        : this(document, (comment ?? throw new ArgumentNullException(nameof(comment))).Id)
    {
    }

    /// <summary>A comment range start for the comment with <paramref name="commentId"/>, which need not exist yet.</summary>
    public CommentRangeStart(Document document, int commentId)
        : base(document)
    {
        this.commentId = commentId;
    }

    /// <summary>The id of the comment the range belongs to.</summary>
    public int CommentId
    {
        get => commentId;
        set => SetProperty(ref commentId, value);
    }

    /// <summary>The comment the range belongs to, or null when the document has none with <see cref="CommentId"/>.</summary>
    public Comment? Comment => Document.FindComment(commentId);

    /// <inheritdoc/>
    public override CommentRangeStart Clone(bool deep = true)
    {
        return (CommentRangeStart)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitCommentRangeStart(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        CommentRangeStart copy = CopyInline(new CommentRangeStart(context.Target, commentId));
        context.TrackCommentRange(copy);
        return copy;
    }

    internal void SetCommentIdSilently(int id)
    {
        commentId = id;
    }
}

/// <summary>The end of the text a <see cref="Comment"/> is about; it pairs with the comment by <see cref="CommentId"/>.</summary>
public sealed class CommentRangeEnd : Inline
{
    private int commentId;

    /// <summary>A comment range end for <paramref name="comment"/>, not yet in a paragraph.</summary>
    public CommentRangeEnd(Document document, Comment comment)
        : this(document, (comment ?? throw new ArgumentNullException(nameof(comment))).Id)
    {
    }

    /// <summary>A comment range end for the comment with <paramref name="commentId"/>, which need not exist yet.</summary>
    public CommentRangeEnd(Document document, int commentId)
        : base(document)
    {
        this.commentId = commentId;
    }

    /// <summary>The id of the comment the range belongs to.</summary>
    public int CommentId
    {
        get => commentId;
        set => SetProperty(ref commentId, value);
    }

    /// <summary>The comment the range belongs to, or null when the document has none with <see cref="CommentId"/>.</summary>
    public Comment? Comment => Document.FindComment(commentId);

    /// <inheritdoc/>
    public override CommentRangeEnd Clone(bool deep = true)
    {
        return (CommentRangeEnd)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitCommentRangeEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        CommentRangeEnd copy = CopyInline(new CommentRangeEnd(context.Target, commentId));
        context.TrackCommentRange(copy);
        return copy;
    }

    internal void SetCommentIdSilently(int id)
    {
        commentId = id;
    }
}

/// <summary>Text between two inlines of one story, for bookmarks and comment ranges.</summary>
internal static class TextRange
{
    public static string Between(Inline start, Inline end)
    {
        if (start.OwnerStory is not { } story || end.OwnerStory != story)
        {
            return "";
        }

        System.Text.StringBuilder text = new();
        bool inside = false;
        Paragraph? lastParagraph = null;
        foreach (Node node in story.Descendants())
        {
            if (node == start)
            {
                inside = true;
                lastParagraph = start.Paragraph;
                continue;
            }

            if (node == end)
            {
                break;
            }

            if (!inside || node.OwnerStory != story)
            {
                continue;
            }

            if (node is Paragraph paragraph)
            {
                if (lastParagraph is not null)
                {
                    text.Append(ControlChar.ParagraphBreak);
                }

                lastParagraph = paragraph;
            }
            else if (node is Run run)
            {
                text.Append(run.Text);
            }
            else if (node is Tab)
            {
                text.Append(ControlChar.Tab);
            }
            else if (node is Break b)
            {
                text.Append(b.Kind == BreakKind.Page ? ControlChar.PageBreak : b.Kind == BreakKind.Column ? ControlChar.ColumnBreak : ControlChar.LineBreak);
            }
        }

        return text.ToString();
    }
}
