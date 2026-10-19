namespace Egap.Documents;

/// <summary>An inline that holds other inlines: a hyperlink or an inline content control.</summary>
public abstract class CompositeInline : Inline
{
    private protected CompositeInline(Document document)
        : base(document)
    {
        Inlines = new ChildCollection<Inline>(this, InlineRules.Check);
    }

    /// <summary>The inlines inside, in order.</summary>
    public NodeCollection<Inline> Inlines { get; }

    /// <summary>The runs directly inside.</summary>
    public IEnumerable<Run> Runs => Inlines.OfType<Run>();

    internal override int ChildCount => Inlines.Count;

    /// <summary>Adds a run of <paramref name="text"/> at the end.</summary>
    public Run AddRun(string text)
    {
        return Inlines.AddTrusted(new Run(Document, text));
    }

    internal override Node GetChild(int index)
    {
        return Inlines[index];
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Inline> inlines = ((CompositeInline)copy).Inlines;
        foreach (Inline inline in Inlines)
        {
            inlines.AddTrusted(CloneChild(inline, context, pending));
        }
    }
}

/// <summary>A hyperlink: the inlines it holds link to an address, a bookmark in the document, or both.</summary>
public sealed class Hyperlink : CompositeInline
{
    private string? address;
    private string? anchor;
    private string? tooltip;
    private string? target;

    /// <summary>An empty hyperlink of <paramref name="document"/>, not yet in a paragraph.</summary>
    public Hyperlink(Document document)
        : base(document)
    {
    }

    /// <summary>The address, such as https://example.com, or null for a link inside the document.</summary>
    public string? Address
    {
        get => address;
        set => SetProperty(ref address, value);
    }

    /// <summary>The bookmark the link goes to, or null.</summary>
    public string? Anchor
    {
        get => anchor;
        set => SetProperty(ref anchor, value);
    }

    /// <summary>The tooltip, or null.</summary>
    public string? Tooltip
    {
        get => tooltip;
        set => SetProperty(ref tooltip, value);
    }

    /// <summary>The frame to open the link in, such as _blank, or null.</summary>
    public string? Target
    {
        get => target;
        set => SetProperty(ref target, value);
    }

    /// <inheritdoc/>
    public override Hyperlink Clone(bool deep = true)
    {
        return (Hyperlink)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitHyperlinkStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitHyperlinkEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Hyperlink(context.Target) { address = address, anchor = anchor, tooltip = tooltip, target = target });
    }
}

/// <summary>What kind of content control (Word's structured document tag) it is.</summary>
public enum ContentControlKind
{
    /// <summary>Rich text: any content.</summary>
    RichText,

    /// <summary>Plain text.</summary>
    PlainText,

    /// <summary>A date picker.</summary>
    Date,

    /// <summary>A drop-down list.</summary>
    DropDownList,

    /// <summary>A combo box.</summary>
    ComboBox,

    /// <summary>A check box.</summary>
    CheckBox,

    /// <summary>A picture.</summary>
    Picture,

    /// <summary>A group that protects its content.</summary>
    Group,

    /// <summary>A building block gallery.</summary>
    BuildingBlockGallery,

    /// <summary>A repeating section.</summary>
    RepeatingSection,

    /// <summary>A citation.</summary>
    Citation,

    /// <summary>A bibliography.</summary>
    Bibliography,

    /// <summary>An equation.</summary>
    Equation,
}

/// <summary>The settings content controls have, block or inline: kind, tag, title, locks and placeholder state.</summary>
public sealed class ContentControlProperties
{
    private readonly Node owner;
    private ContentControlKind kind;
    private string? tag;
    private string? title;
    private bool lockContents;
    private bool lockControl;
    private bool showingPlaceholder;
    private bool? isChecked;
    private IReadOnlyList<string>? items;
    private string? radioGroup;

    internal ContentControlProperties(Node owner)
    {
        this.owner = owner;
    }

    /// <summary>The kind of control.</summary>
    public ContentControlKind Kind
    {
        get => kind;
        set => Set(ref kind, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The tag code uses to find the control, or null.</summary>
    public string? Tag
    {
        get => tag;
        set => Set(ref tag, value);
    }

    /// <summary>The title people see, or null.</summary>
    public string? Title
    {
        get => title;
        set => Set(ref title, value);
    }

    /// <summary>The content cannot be edited.</summary>
    public bool LockContents
    {
        get => lockContents;
        set => Set(ref lockContents, value);
    }

    /// <summary>The control cannot be deleted.</summary>
    public bool LockControl
    {
        get => lockControl;
        set => Set(ref lockControl, value);
    }

    /// <summary>The content shown is the placeholder text.</summary>
    public bool ShowingPlaceholder
    {
        get => showingPlaceholder;
        set => Set(ref showingPlaceholder, value);
    }

    /// <summary>For a check box, whether it is checked; otherwise null.</summary>
    public bool? IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    /// <summary>For a drop-down list or combo box, the items to choose from, in order; otherwise null.</summary>
    public IReadOnlyList<string>? Items
    {
        get => items;
        set => Set(ref items, value is null ? null : [.. value]);
    }

    /// <summary>
    /// For a check box that is one of a set of options (an HTML radio button), the set's name: check boxes with the same
    /// group are exclusive, and each one's <see cref="Tag"/> is the value it stands for. Null for an ordinary check box.
    /// Word has no such control, so DOCX keeps them as check boxes.
    /// </summary>
    public string? RadioGroup
    {
        get => radioGroup;
        set => Set(ref radioGroup, value);
    }

    internal void CopyFrom(ContentControlProperties source)
    {
        items = source.items;
        radioGroup = source.radioGroup;
        kind = source.kind;
        tag = source.tag;
        title = source.title;
        lockContents = source.lockContents;
        lockControl = source.lockControl;
        showingPlaceholder = source.showingPlaceholder;
        isChecked = source.isChecked;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            owner.Document.NotifyChanged(owner, DocumentChangeKind.PropertyChanged, owner, property: "Properties." + property);
        }
    }
}

/// <summary>A content control around inlines inside a paragraph.</summary>
public sealed class InlineContentControl : CompositeInline
{
    /// <summary>An empty inline content control of <paramref name="document"/>, not yet in a paragraph.</summary>
    public InlineContentControl(Document document)
        : base(document)
    {
        Properties = new ContentControlProperties(this);
    }

    /// <summary>The control's settings.</summary>
    public ContentControlProperties Properties { get; }

    /// <inheritdoc/>
    public override InlineContentControl Clone(bool deep = true)
    {
        return (InlineContentControl)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitInlineContentControlStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitInlineContentControlEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        InlineContentControl copy = CopyInline(new InlineContentControl(context.Target));
        copy.Properties.CopyFrom(Properties);
        return copy;
    }
}

/// <summary>A content control around blocks: paragraphs, tables and other controls.</summary>
public sealed class ContentControl : Block
{
    /// <summary>An empty content control of <paramref name="document"/>, not yet in the tree.</summary>
    public ContentControl(Document document)
        : base(document)
    {
        Blocks = new ChildCollection<Block>(this);
        Properties = new ContentControlProperties(this);
    }

    /// <summary>The blocks inside, in order.</summary>
    public NodeCollection<Block> Blocks { get; }

    /// <summary>The control's settings.</summary>
    public ContentControlProperties Properties { get; }

    internal override int ChildCount => Blocks.Count;

    /// <summary>Adds a paragraph at the end, holding <paramref name="text"/> as one run when it is given.</summary>
    public Paragraph AddParagraph(string? text = null)
    {
        Paragraph paragraph = Blocks.AddTrusted(new Paragraph(Document));
        if (!string.IsNullOrEmpty(text))
        {
            paragraph.AddRun(text);
        }

        return paragraph;
    }

    /// <inheritdoc/>
    public override ContentControl Clone(bool deep = true)
    {
        return (ContentControl)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Blocks[index];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitContentControlStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitContentControlEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        ContentControl copy = new(context.Target);
        copy.Properties.CopyFrom(Properties);
        return copy;
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Block> blocks = ((ContentControl)copy).Blocks;
        foreach (Block block in Blocks)
        {
            blocks.AddTrusted(CloneChild(block, context, pending));
        }
    }
}

/// <summary>What a kept island holds (Phase 6 design Q4): content the model keeps whole, as raw markup, drawn as its picture.</summary>
public enum IslandKind
{
    /// <summary>Something else the reader kept whole.</summary>
    Other,

    /// <summary>An equation (Office Math).</summary>
    Math,

    /// <summary>A chart.</summary>
    Chart,

    /// <summary>A SmartArt diagram.</summary>
    SmartArt,

    /// <summary>An embedded or linked OLE object.</summary>
    OleObject,

    /// <summary>Ink.</summary>
    Ink,

    /// <summary>A group of shapes.</summary>
    ShapeGroup,

    /// <summary>A legacy VML picture or shape.</summary>
    Vml,
}

/// <summary>
/// An inline kept whole (Phase 6 design Q4): a format's raw markup for an equation, chart, SmartArt diagram, OLE object
/// and the like, with the picture the file stores for it, so it survives a round trip and draws as that picture.
/// </summary>
public sealed class Island : Inline
{
    /// <summary>An island holding <paramref name="markup"/> in <paramref name="format"/>, not yet in a paragraph.</summary>
    public Island(Document document, IslandKind kind, string format, string markup)
        : base(document)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(markup);
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        Format = format;
        Markup = markup;
    }

    /// <summary>What the island holds.</summary>
    public IslandKind Kind { get; }

    /// <summary>The format the markup is in, such as "docx".</summary>
    public string Format { get; }

    /// <summary>The raw markup, as the format's reader found it.</summary>
    public string Markup { get; }

    /// <summary>The picture to draw in its place, or null; with no picture, layout leaves an empty box of <see cref="Width"/> by <see cref="Height"/>.</summary>
    public ImageData? Fallback { get; init; }

    /// <summary>The width in points, or 0 when not known.</summary>
    public double Width { get; init; }

    /// <summary>The height in points, or 0 when not known.</summary>
    public double Height { get; init; }

    /// <summary>The text an island stands for, such as an equation's linear form, for text output; empty when unknown.</summary>
    public string Text { get; init; } = "";

    /// <summary>Where the island floats, for one anchored to the paragraph; null when it sits in the line.</summary>
    public FloatingPlacement? Floating { get; init; }

    /// <inheritdoc/>
    public override Island Clone(bool deep = true)
    {
        return (Island)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitIsland(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Island(context.Target, Kind, Format, Markup)
        {
            Fallback = Fallback,
            Width = Width,
            Height = Height,
            Text = Text,
            Floating = Floating,
        });
    }
}

/// <summary>A block kept whole, such as an embedded document part (Word's altChunk), as raw markup.</summary>
public sealed class BlockIsland : Block
{
    /// <summary>A block island holding <paramref name="markup"/> in <paramref name="format"/>, not yet in the tree.</summary>
    public BlockIsland(Document document, string format, string markup)
        : base(document)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(markup);
        Format = format;
        Markup = markup;
    }

    /// <summary>The format the markup is in.</summary>
    public string Format { get; }

    /// <summary>The raw markup.</summary>
    public string Markup { get; }

    /// <inheritdoc/>
    public override BlockIsland Clone(bool deep = true)
    {
        return (BlockIsland)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitBlockIsland(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new BlockIsland(context.Target, Format, Markup);
    }
}

/// <summary>The rules for what may go where among inlines, beyond their types.</summary>
internal static class InlineRules
{
    public static string? Check(Node owner, Inline node)
    {
        Story? story = owner as Story ?? owner.OwnerStory;
        switch (node)
        {
            case Footnote when story is NoteStory or CommentStory:
                return "A footnote or endnote cannot go into a note or comment.";
            case Comment when story is CommentStory:
                return "A comment cannot go into a comment.";
            case Hyperlink when owner is Hyperlink || owner.GetAncestor<Hyperlink>() is not null:
                return "A hyperlink cannot go into a hyperlink.";
            default:
                return null;
        }
    }
}
