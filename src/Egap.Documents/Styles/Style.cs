namespace Egap.Documents;

/// <summary>The kind of a <see cref="Style"/>.</summary>
public enum StyleType
{
    /// <summary>A paragraph style: paragraph and character formatting.</summary>
    Paragraph,

    /// <summary>A character style: character formatting for runs.</summary>
    Character,

    /// <summary>A table style: table, row, cell, paragraph and character formatting, with conditional parts.</summary>
    Table,

    /// <summary>A list style: names a list definition (Build Plan task 4).</summary>
    List,
}

/// <summary>
/// A named set of formatting that paragraphs, runs or tables point at, as in Word (Phase 6 design Q5). A style can be based
/// on another of the same type, taking every property it does not set from it. Styles belong to one document's
/// <see cref="StyleCollection"/>.
/// </summary>
public abstract class Style : IFormatOwner
{
    private string name;
    private Style? basedOn;
    private bool hidden;
    private int? priority;
    private bool primary;

    private protected Style(Document document, string id, string name)
    {
        Document = document;
        Id = id;
        this.name = name;
    }

    /// <summary>The document this style belongs to.</summary>
    public Document Document { get; }

    /// <summary>The identifier files use (Word's styleId), such as "Heading1"; unique in the document, fixed once made.</summary>
    public string Id { get; }

    /// <summary>The name people see, such as "heading 1".</summary>
    public string Name
    {
        get => name;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (name != value)
            {
                name = value;
                Notify(nameof(Name));
            }
        }
    }

    /// <summary>The kind of style.</summary>
    public abstract StyleType Type { get; }

    /// <summary>The style this one takes unset properties from, of the same type, or null.</summary>
    /// <exception cref="ArgumentException">The style is of another type or document, or would make a loop.</exception>
    public Style? BasedOn
    {
        get => basedOn;
        set
        {
            if (value is not null)
            {
                CheckSameDocument(value);
                if (value.Type != Type)
                {
                    throw new ArgumentException($"A {Type} style cannot be based on a {value.Type} style.", nameof(value));
                }

                for (Style? s = value; s is not null; s = s.basedOn)
                {
                    if (s == this)
                    {
                        throw new ArgumentException($"Basing {Id} on {value.Id} would make a loop.", nameof(value));
                    }
                }
            }

            if (basedOn != value)
            {
                basedOn = value;
                Notify(nameof(BasedOn));
            }
        }
    }

    /// <summary>Whether this is the document's default style of its type.</summary>
    public bool IsDefault => Document.Styles.GetDefault(Type) == this;

    /// <summary>Whether the style is hidden from style lists.</summary>
    public bool Hidden
    {
        get => hidden;
        set => Set(ref hidden, value, nameof(Hidden));
    }

    /// <summary>Where the style sorts in style lists (Word's uiPriority), or null.</summary>
    public int? Priority
    {
        get => priority;
        set => Set(ref priority, value, nameof(Priority));
    }

    /// <summary>Whether the style is offered in the quick styles gallery (Word's qFormat).</summary>
    public bool IsPrimary
    {
        get => primary;
        set => Set(ref primary, value, nameof(IsPrimary));
    }

    /// <summary>Whether the style is in its document's collection; a removed style no longer formats anything.</summary>
    public bool IsInDocument => Document.Styles.Contains(this);

    /// <summary>This style and the ones it is based on, nearest first.</summary>
    public IEnumerable<Style> BasedOnChain()
    {
        for (Style? s = this; s is not null; s = s.basedOn)
        {
            yield return s;
        }
    }

    /// <summary>The id, for debugging.</summary>
    public override string ToString()
    {
        return $"{Type} style {Id}";
    }

    void IFormatOwner.FormatChanged(FormatBase format, string property)
    {
        Notify(format.GetType().Name + "." + property);
    }

    /// <summary>A copy of this style, without its links to other styles, for <paramref name="target"/>.</summary>
    internal abstract Style CopyTo(Document target, string id);

    /// <summary>Copies links to other styles through <paramref name="map"/>, once every style has a copy.</summary>
    internal virtual void CopyLinks(Style copy, Func<Style, Style> map)
    {
        copy.basedOn = basedOn is null ? null : map(basedOn);
    }

    /// <summary>Copies the settings every style has.</summary>
    private protected void CopyCommon(Style copy)
    {
        copy.hidden = hidden;
        copy.priority = priority;
        copy.primary = primary;
    }

    private protected T Own<T>(T format)
        where T : FormatBase
    {
        format.Owner = this;
        return format;
    }

    private protected void CheckSameDocument(Style other)
    {
        if (other.Document != Document)
        {
            throw new ArgumentException($"Style {other.Id} belongs to another document.", nameof(other));
        }
    }

    private protected void Set<T>(ref T field, T value, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Notify(property);
        }
    }

    private protected void Notify(string property)
    {
        if (IsInDocument)
        {
            Document.NotifyFormattingChanged($"Styles.{Id}.{property}");
        }
    }
}

/// <summary>A paragraph style: paragraph formatting and the character formatting of its text.</summary>
public sealed class ParagraphStyle : Style
{
    private ParagraphStyle? next;
    private CharacterStyle? linked;

    internal ParagraphStyle(Document document, string id, string name)
        : base(document, id, name)
    {
        ParagraphFormat = Own(new ParagraphFormat());
        RunFormat = Own(new RunFormat());
    }

    /// <inheritdoc/>
    public override StyleType Type => StyleType.Paragraph;

    /// <summary>The paragraph formatting the style sets.</summary>
    public ParagraphFormat ParagraphFormat { get; }

    /// <summary>The character formatting the style sets for its paragraphs' text.</summary>
    public RunFormat RunFormat { get; }

    /// <summary>The style for the paragraph a writer starts after this one, or null for the same style.</summary>
    public ParagraphStyle? Next
    {
        get => next;
        set
        {
            if (value is not null)
            {
                CheckSameDocument(value);
            }

            Set(ref next, value, nameof(Next));
        }
    }

    /// <summary>The character style linked to this one (Word's linked styles), or null.</summary>
    public CharacterStyle? Linked
    {
        get => linked;
        set
        {
            if (value is not null)
            {
                CheckSameDocument(value);
            }

            Set(ref linked, value, nameof(Linked));
        }
    }

    internal override Style CopyTo(Document target, string id)
    {
        ParagraphStyle copy = new(target, id, Name);
        CopyCommon(copy);
        copy.ParagraphFormat.CopyRaw(ParagraphFormat);
        copy.RunFormat.CopyRaw(RunFormat);
        return copy;
    }

    internal override void CopyLinks(Style copy, Func<Style, Style> map)
    {
        base.CopyLinks(copy, map);
        ParagraphStyle target = (ParagraphStyle)copy;
        target.next = next is null ? null : (ParagraphStyle)map(next);
        target.linked = linked is null ? null : (CharacterStyle)map(linked);
    }
}

/// <summary>A character style: character formatting for runs.</summary>
public sealed class CharacterStyle : Style
{
    private ParagraphStyle? linked;

    internal CharacterStyle(Document document, string id, string name)
        : base(document, id, name)
    {
        RunFormat = Own(new RunFormat());
    }

    /// <inheritdoc/>
    public override StyleType Type => StyleType.Character;

    /// <summary>The character formatting the style sets.</summary>
    public RunFormat RunFormat { get; }

    /// <summary>The paragraph style linked to this one, or null.</summary>
    public ParagraphStyle? Linked
    {
        get => linked;
        set
        {
            if (value is not null)
            {
                CheckSameDocument(value);
            }

            Set(ref linked, value, nameof(Linked));
        }
    }

    internal override Style CopyTo(Document target, string id)
    {
        CharacterStyle copy = new(target, id, Name);
        CopyCommon(copy);
        copy.RunFormat.CopyRaw(RunFormat);
        return copy;
    }

    internal override void CopyLinks(Style copy, Func<Style, Style> map)
    {
        base.CopyLinks(copy, map);
        ((CharacterStyle)copy).linked = linked is null ? null : (ParagraphStyle)map(linked);
    }
}

/// <summary>The parts of a table a table style can format apart (Word's conditional formatting).</summary>
public enum TableStyleRegion
{
    /// <summary>The first row (the header row).</summary>
    FirstRow,

    /// <summary>The last row.</summary>
    LastRow,

    /// <summary>The first column.</summary>
    FirstColumn,

    /// <summary>The last column.</summary>
    LastColumn,

    /// <summary>Odd row bands.</summary>
    OddRowBand,

    /// <summary>Even row bands.</summary>
    EvenRowBand,

    /// <summary>Odd column bands.</summary>
    OddColumnBand,

    /// <summary>Even column bands.</summary>
    EvenColumnBand,

    /// <summary>The top right cell.</summary>
    TopRightCell,

    /// <summary>The top left cell.</summary>
    TopLeftCell,

    /// <summary>The bottom right cell.</summary>
    BottomRightCell,

    /// <summary>The bottom left cell.</summary>
    BottomLeftCell,
}

/// <summary>The formatting a table style gives one <see cref="TableStyleRegion"/>.</summary>
public sealed class TableStyleConditional
{
    internal TableStyleConditional(Style owner)
    {
        TableFormat = Own(new TableFormat(), owner);
        RowFormat = Own(new RowFormat(), owner);
        CellFormat = Own(new CellFormat(), owner);
        ParagraphFormat = Own(new ParagraphFormat(), owner);
        RunFormat = Own(new RunFormat(), owner);
    }

    /// <summary>Table formatting for the region (its borders).</summary>
    public TableFormat TableFormat { get; }

    /// <summary>Row formatting for the region.</summary>
    public RowFormat RowFormat { get; }

    /// <summary>Cell formatting for the region (shading, borders, margins).</summary>
    public CellFormat CellFormat { get; }

    /// <summary>Paragraph formatting for text in the region.</summary>
    public ParagraphFormat ParagraphFormat { get; }

    /// <summary>Character formatting for text in the region.</summary>
    public RunFormat RunFormat { get; }

    /// <summary>Whether nothing is set.</summary>
    public bool IsEmpty => TableFormat.IsEmpty && RowFormat.IsEmpty && CellFormat.IsEmpty && ParagraphFormat.IsEmpty && RunFormat.IsEmpty;

    internal void CopyRaw(TableStyleConditional source)
    {
        TableFormat.CopyRaw(source.TableFormat);
        RowFormat.CopyRaw(source.RowFormat);
        CellFormat.CopyRaw(source.CellFormat);
        ParagraphFormat.CopyRaw(source.ParagraphFormat);
        RunFormat.CopyRaw(source.RunFormat);
    }

    private static T Own<T>(T format, Style owner)
        where T : FormatBase
    {
        format.Owner = owner;
        return format;
    }
}

/// <summary>A table style: formatting for the whole table and, apart, for its first and last rows and columns, bands and corners.</summary>
public sealed class TableStyle : Style
{
    private readonly TableStyleConditional[] conditionals;

    internal TableStyle(Document document, string id, string name)
        : base(document, id, name)
    {
        TableFormat = Own(new TableFormat());
        RowFormat = Own(new RowFormat());
        CellFormat = Own(new CellFormat());
        ParagraphFormat = Own(new ParagraphFormat());
        RunFormat = Own(new RunFormat());
        conditionals = new TableStyleConditional[Enum.GetValues<TableStyleRegion>().Length];
        for (int i = 0; i < conditionals.Length; i++)
        {
            conditionals[i] = new TableStyleConditional(this);
        }
    }

    /// <inheritdoc/>
    public override StyleType Type => StyleType.Table;

    /// <summary>Table formatting for the whole table.</summary>
    public TableFormat TableFormat { get; }

    /// <summary>Row formatting for every row.</summary>
    public RowFormat RowFormat { get; }

    /// <summary>Cell formatting for every cell.</summary>
    public CellFormat CellFormat { get; }

    /// <summary>Paragraph formatting for text in the table.</summary>
    public ParagraphFormat ParagraphFormat { get; }

    /// <summary>Character formatting for text in the table.</summary>
    public RunFormat RunFormat { get; }

    /// <summary>The formatting for one region of the table.</summary>
    public TableStyleConditional this[TableStyleRegion region] => Enum.IsDefined(region) ? conditionals[(int)region] : throw new ArgumentOutOfRangeException(nameof(region));

    internal override Style CopyTo(Document target, string id)
    {
        TableStyle copy = new(target, id, Name);
        CopyCommon(copy);
        copy.TableFormat.CopyRaw(TableFormat);
        copy.RowFormat.CopyRaw(RowFormat);
        copy.CellFormat.CopyRaw(CellFormat);
        copy.ParagraphFormat.CopyRaw(ParagraphFormat);
        copy.RunFormat.CopyRaw(RunFormat);
        for (int i = 0; i < conditionals.Length; i++)
        {
            copy.conditionals[i].CopyRaw(conditionals[i]);
        }

        return copy;
    }
}
