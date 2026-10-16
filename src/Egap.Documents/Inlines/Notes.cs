namespace Egap.Documents;

/// <summary>Whether a note is a footnote or an endnote.</summary>
public enum NoteKind
{
    /// <summary>A footnote, at the foot of the page of its reference.</summary>
    Footnote,

    /// <summary>An endnote, at the end of the section or document.</summary>
    Endnote,
}

/// <summary>The text of a footnote or endnote.</summary>
public sealed class NoteStory : Story
{
    internal NoteStory(Document document)
        : base(document)
    {
    }

    /// <summary>The note this text belongs to, or null for a copy not yet in a note.</summary>
    public Footnote? Note => Parent as Footnote;

    /// <inheritdoc/>
    public override NoteStory Clone(bool deep = true)
    {
        return (NoteStory)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new NoteStory(context.Target);
    }
}

/// <summary>
/// A footnote or endnote: the reference mark in the text, which holds the note's own text as a story (Phase 6 design Q8).
/// The mark is numbered automatically unless it has a <see cref="CustomMark"/>.
/// </summary>
public sealed class Footnote : Inline
{
    private NoteKind kind;
    private string? customMark;

    /// <summary>An empty note of <paramref name="kind"/>, not yet in a paragraph.</summary>
    public Footnote(Document document, NoteKind kind = NoteKind.Footnote)
        : base(document)
    {
        this.kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        Story = new NoteStory(document);
        Story.Attach(this, null, 0);
    }

    /// <summary>Footnote or endnote.</summary>
    public NoteKind Kind
    {
        get => kind;
        set => SetProperty(ref kind, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>A mark of its own instead of the next number, such as "*", or null.</summary>
    public string? CustomMark
    {
        get => customMark;
        set => SetProperty(ref customMark, value);
    }

    /// <summary>The note's text.</summary>
    public NoteStory Story { get; }

    /// <summary>The mark shown at the reference and before the note: its custom mark, or its number in document order.</summary>
    public string Mark => NoteNumbering.Mark(this);

    internal override int ChildCount => 1;

    /// <summary>Adds a paragraph to the note's text.</summary>
    public Paragraph AddParagraph(string? text = null)
    {
        return Story.AddParagraph(text);
    }

    /// <inheritdoc/>
    public override Footnote Clone(bool deep = true)
    {
        return (Footnote)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return index == 0 ? Story : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFootnoteStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitFootnoteEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Footnote(context.Target, kind) { customMark = customMark });
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        pending.Push((Story, ((Footnote)copy).Story));
    }
}

/// <summary>The text of a comment.</summary>
public sealed class CommentStory : Story
{
    internal CommentStory(Document document)
        : base(document)
    {
    }

    /// <summary>The comment this text belongs to, or null.</summary>
    public Comment? Comment => Parent as Comment;

    /// <inheritdoc/>
    public override CommentStory Clone(bool deep = true)
    {
        return (CommentStory)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new CommentStory(context.Target);
    }
}

/// <summary>
/// A comment: its reference mark in the text, holding the comment's own text, author and date (Phase 6 design Q8). The text
/// it is about lies between a <see cref="CommentRangeStart"/> and <see cref="CommentRangeEnd"/> with its <see cref="Id"/>. A
/// reply names the comment it answers in <see cref="ParentId"/>, as Word does.
/// </summary>
public sealed class Comment : Inline
{
    private string author;
    private string? initials;
    private DateTimeOffset? date;
    private bool done;
    private int? parentId;

    /// <summary>An empty comment by <paramref name="author"/>, not yet in a paragraph, with a new id in the document.</summary>
    public Comment(Document document, string author, DateTimeOffset? date = null)
        : this(document, author, date, document.NextCommentId())
    {
    }

    private Comment(Document document, string author, DateTimeOffset? date, int id)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(author);
        this.author = author;
        this.date = date;
        Id = id;
        Story = new CommentStory(document);
        Story.Attach(this, null, 0);
    }

    /// <summary>The comment's id in the document, which its range marks and replies name.</summary>
    public int Id { get; private set; }

    /// <summary>Who wrote it.</summary>
    public string Author
    {
        get => author;
        set => SetProperty(ref author, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The author's initials, or null.</summary>
    public string? Initials
    {
        get => initials;
        set => SetProperty(ref initials, value);
    }

    /// <summary>When it was written, or null.</summary>
    public DateTimeOffset? Date
    {
        get => date;
        set => SetProperty(ref date, value);
    }

    /// <summary>Marked as resolved.</summary>
    public bool Done
    {
        get => done;
        set => SetProperty(ref done, value);
    }

    /// <summary>The id of the comment this one answers, or null for a comment that starts a thread.</summary>
    public int? ParentId
    {
        get => parentId;
        set => SetProperty(ref parentId, value);
    }

    /// <summary>The comment this one answers, or null.</summary>
    public Comment? ParentComment => parentId is int id ? Document.FindComment(id) : null;

    /// <summary>The comment's text.</summary>
    public CommentStory Story { get; }

    internal override int ChildCount => 1;

    /// <summary>Adds a paragraph to the comment's text.</summary>
    public Paragraph AddParagraph(string? text = null)
    {
        return Story.AddParagraph(text);
    }

    /// <summary>
    /// Adds a reply by <paramref name="author"/> holding <paramref name="text"/>, placed right after this comment (and its
    /// earlier replies) in the same paragraph.
    /// </summary>
    /// <exception cref="InvalidOperationException">This comment is not in a paragraph or hyperlink yet.</exception>
    public Comment AddReply(string author, string text, DateTimeOffset? date = null)
    {
        if (Collection is null)
        {
            throw new InvalidOperationException("Put the comment in a paragraph before replying to it.");
        }

        Comment reply = new(Document, author, date) { parentId = Id };
        reply.AddParagraph(text);
        Node anchor = this;
        while (anchor.NextSibling is Comment next && next.parentId == Id)
        {
            anchor = next;
        }

        switch (Parent)
        {
            case Paragraph paragraph:
                paragraph.Inlines.Insert(anchor.IndexInParent + 1, reply);
                break;
            case CompositeInline composite:
                composite.Inlines.Insert(anchor.IndexInParent + 1, reply);
                break;
        }

        return reply;
    }

    /// <summary>The replies to this comment, in document order.</summary>
    public IEnumerable<Comment> Replies()
    {
        return Document.Descendants<Comment>().Where(c => c.parentId == Id);
    }

    /// <inheritdoc/>
    public override Comment Clone(bool deep = true)
    {
        return (Comment)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return index == 0 ? Story : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitCommentStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitCommentEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        int id = context.WholeDocument ? Id : context.Target.NextCommentId();
        context.Target.ReserveCommentId(id);
        Comment copy = CopyInline(new Comment(context.Target, author, date, id)
        {
            initials = initials,
            done = done,
            parentId = parentId,
        });
        context.TrackComment(Id, copy);
        return copy;
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        pending.Push((Story, ((Comment)copy).Story));
    }

    /// <summary>Sets the id and parent id without notifying, for clone fix-ups and readers.</summary>
    internal void SetIdsSilently(int id, int? parent)
    {
        Id = id;
        parentId = parent;
    }
}
