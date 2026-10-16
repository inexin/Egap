namespace Egap.Documents;

/// <summary>
/// A node that goes into a paragraph: a run of text, a tab or a break. Fields, hyperlinks, bookmarks, notes, comments and
/// drawings join in Build Plan task 5.
/// </summary>
public abstract class Inline : Node
{
    private protected Inline(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
    }

    /// <summary>The paragraph this inline is in, or null.</summary>
    public Paragraph? Paragraph => Parent as Paragraph;
}

/// <summary>A run of text with one formatting (Build Plan task 3).</summary>
public sealed class Run : Inline
{
    private string text;

    /// <summary>A run of <paramref name="document"/> holding <paramref name="text"/>, not yet in a paragraph.</summary>
    public Run(Document document, string text = "")
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
    }

    /// <summary>The text; tabs and breaks are their own inlines, though a run may hold any character a file gave it.</summary>
    public string Text
    {
        get => text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value != text)
            {
                text = value;
                Document.NotifyChanged(this, DocumentChangeKind.TextChanged, this);
            }
        }
    }

    /// <inheritdoc/>
    public override Run Clone(bool deep = true)
    {
        return (Run)base.Clone(deep);
    }

    /// <summary>The text, for debugging.</summary>
    public override string ToString()
    {
        return text;
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitRun(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Run(context.Target, text);
    }
}

/// <summary>A tab: the text moves on to the paragraph's next tab stop.</summary>
public sealed class Tab : Inline
{
    /// <summary>A tab of <paramref name="document"/>, not yet in a paragraph.</summary>
    public Tab(Document document)
        : base(document)
    {
    }

    /// <inheritdoc/>
    public override Tab Clone(bool deep = true)
    {
        return (Tab)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitTab(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Tab(context.Target);
    }
}

/// <summary>What a <see cref="Break"/> ends.</summary>
public enum BreakKind
{
    /// <summary>The line: the text goes on at the start of the next line in the same paragraph.</summary>
    Line,

    /// <summary>The page: the text goes on at the top of the next page.</summary>
    Page,

    /// <summary>The column: the text goes on at the top of the next column, or the next page in a one-column section.</summary>
    Column,
}

/// <summary>A line, page or column break inside a paragraph.</summary>
public sealed class Break : Inline
{
    private BreakKind kind;

    /// <summary>A break of <paramref name="kind"/> in <paramref name="document"/>, not yet in a paragraph.</summary>
    public Break(Document document, BreakKind kind = BreakKind.Line)
        : base(document)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        this.kind = kind;
    }

    /// <summary>What the break ends.</summary>
    public BreakKind Kind
    {
        get => kind;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value != kind)
            {
                kind = value;
                Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: nameof(Kind));
            }
        }
    }

    /// <inheritdoc/>
    public override Break Clone(bool deep = true)
    {
        return (Break)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitBreak(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Break(context.Target, kind);
    }
}
