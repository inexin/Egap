namespace Egap.Documents;

/// <summary>What follows a list label before the paragraph's text.</summary>
public enum ListLabelSuffix
{
    /// <summary>A tab to the next tab stop or the hanging indent.</summary>
    Tab,

    /// <summary>A space.</summary>
    Space,

    /// <summary>Nothing.</summary>
    Nothing,
}

/// <summary>How a list definition's levels are meant to be used, as Word records it.</summary>
public enum ListKind
{
    /// <summary>One level only.</summary>
    SingleLevel,

    /// <summary>Levels that build on each other, such as 1, 1.1, 1.1.1.</summary>
    Multilevel,

    /// <summary>Levels used independently, Word's usual bulleted and numbered lists.</summary>
    HybridMultilevel,
}

/// <summary>
/// One level of a list definition: where numbering starts, how numbers are written and the label's text, alignment and
/// formatting, and the indents its paragraphs get (Phase 6 design Q6, Word's lvl).
/// </summary>
public sealed class ListLevel : IFormatOwner
{
    Document IFormatOwner.Document => Owner.Document;

    private int startAt = 1;
    private NumberStyle numberStyle = NumberStyle.Decimal;
    private string levelText;
    private ParagraphAlignment alignment = ParagraphAlignment.Left;
    private ListLabelSuffix suffix = ListLabelSuffix.Tab;
    private int? restartAfterLevel;
    private bool isLegal;
    private ParagraphStyle? linkedStyle;
    private readonly Action<string> notify;

    internal ListLevel(ListDefinition owner, int index, Action<string>? notify = null)
    {
        Owner = owner;
        this.notify = notify ?? (property => owner.Notify($"Levels[{index}].{property}"));
        Index = index;
        levelText = "%" + (index + 1) + ".";
        ParagraphFormat = new ParagraphFormat { Owner = this };
        RunFormat = new RunFormat { Owner = this };
    }

    /// <summary>The definition this level belongs to.</summary>
    public ListDefinition Owner { get; }

    /// <summary>The level, 0 to 8.</summary>
    public int Index { get; }

    /// <summary>The first number at this level.</summary>
    public int StartAt
    {
        get => startAt;
        set => Set(ref startAt, value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>How the level's number is written.</summary>
    public NumberStyle NumberStyle
    {
        get => numberStyle;
        set => Set(ref numberStyle, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>
    /// The label: literal text with %1 to %9 standing for the numbers of levels 1 to 9, such as "%1.%2."; for a bullet, the
    /// bullet character itself.
    /// </summary>
    public string LevelText
    {
        get => levelText;
        set => Set(ref levelText, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>How the label lines up at its position: left, centred or right (Word's lvlJc).</summary>
    public ParagraphAlignment Alignment
    {
        get => alignment;
        set => Set(ref alignment, value is ParagraphAlignment.Left or ParagraphAlignment.Center or ParagraphAlignment.Right ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>What follows the label.</summary>
    public ListLabelSuffix Suffix
    {
        get => suffix;
        set => Set(ref suffix, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>
    /// When numbering at this level starts again: null after any paragraph of a higher level (the usual case), -1 never, or
    /// 0 to 8 only after a paragraph of that level or higher (Word's lvlRestart, made 0-based).
    /// </summary>
    public int? RestartAfterLevel
    {
        get => restartAfterLevel;
        set => Set(ref restartAfterLevel, value is null or (>= -1 and <= 8) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Numbers of every level in the label are written as decimals (legal numbering, Word's isLgl).</summary>
    public bool IsLegal
    {
        get => isLegal;
        set => Set(ref isLegal, value);
    }

    /// <summary>The paragraph style tied to this level (Word's pStyle in a level), or null.</summary>
    public ParagraphStyle? LinkedStyle
    {
        get => linkedStyle;
        set
        {
            if (value is not null && value.Document != Owner.Document)
            {
                throw new ArgumentException("The style belongs to another document.", nameof(value));
            }

            Set(ref linkedStyle, value);
        }
    }

    /// <summary>Paragraph formatting for the level's paragraphs, mainly indents and the tab after the label.</summary>
    public ParagraphFormat ParagraphFormat { get; }

    /// <summary>Character formatting for the label, over the paragraph mark's.</summary>
    public RunFormat RunFormat { get; }

    void IFormatOwner.FormatChanged(FormatBase format, string property)
    {
        Notify(format.GetType().Name + "." + property);
    }

    internal void CopyFrom(ListLevel source, CloneContext? context)
    {
        startAt = source.startAt;
        numberStyle = source.numberStyle;
        levelText = source.levelText;
        alignment = source.alignment;
        suffix = source.suffix;
        restartAfterLevel = source.restartAfterLevel;
        isLegal = source.isLegal;
        linkedStyle = context is null ? source.linkedStyle : context.Map(source.linkedStyle);
        ParagraphFormat.CopyRaw(source.ParagraphFormat);
        RunFormat.CopyRaw(source.RunFormat);
        ParagraphFormat.SetRawSilently(ParagraphFormat.ListId, null);
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Notify(property);
        }
    }

    private void Notify(string property)
    {
        notify(property);
    }
}

/// <summary>
/// A list definition (Word's abstractNum): nine levels with their numbering, labels and indents. Lists (<see cref="List"/>)
/// use a definition; lists that share one continue each other's numbering unless one restarts.
/// </summary>
public sealed class ListDefinition
{
    private readonly ListLevel[] levels = new ListLevel[9];
    private ListKind kind = ListKind.HybridMultilevel;
    private string? name;

    internal ListDefinition(Document document, int id)
    {
        Document = document;
        Id = id;
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = new ListLevel(this, i);
        }
    }

    /// <summary>The document this definition belongs to.</summary>
    public Document Document { get; }

    /// <summary>The definition's number in the document (Word's abstractNumId).</summary>
    public int Id { get; }

    /// <summary>The nine levels, 0 to 8.</summary>
    public IReadOnlyList<ListLevel> Levels => levels;

    /// <summary>How the levels are meant to be used.</summary>
    public ListKind Kind
    {
        get => kind;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (kind != value)
            {
                kind = value;
                Notify(nameof(Kind));
            }
        }
    }

    /// <summary>The definition's name, or null.</summary>
    public string? Name
    {
        get => name;
        set
        {
            if (name != value)
            {
                name = value;
                Notify(nameof(Name));
            }
        }
    }

    internal void Notify(string property)
    {
        if (Document.Lists.Contains(this))
        {
            Document.NotifyFormattingChanged($"ListDefinitions.{Id}.{property}");
        }
    }

    internal void CopyFrom(ListDefinition source, CloneContext? context)
    {
        kind = source.kind;
        name = source.name;
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i].CopyFrom(source.levels[i], context);
        }
    }
}

/// <summary>A change to one level of one list: a new start number, or a whole level of its own (Word's lvlOverride).</summary>
public sealed class ListLevelOverride
{
    private readonly List owner;
    private int? startAt;
    private ListLevel? level;

    internal ListLevelOverride(List owner, int index)
    {
        this.owner = owner;
        Index = index;
    }

    /// <summary>The level, 0 to 8.</summary>
    public int Index { get; }

    /// <summary>The number this list starts the level at, restarting it where the list is first used; null for none.</summary>
    public int? StartAt
    {
        get => startAt;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0);
            if (startAt != value)
            {
                startAt = value;
                owner.Notify($"Overrides[{Index}].StartAt");
            }
        }
    }

    /// <summary>A level of this list's own, replacing the definition's, or null; <see cref="CreateLevel"/> makes one.</summary>
    public ListLevel? Level => level;

    /// <summary>Gives the list its own copy of the definition's level, to change without touching other lists.</summary>
    public ListLevel CreateLevel()
    {
        if (level is null)
        {
            level = NewLevel();
            level.CopyFrom(owner.Definition.Levels[Index], null);
            owner.Notify($"Overrides[{Index}].Level");
        }

        return level;
    }

    /// <summary>Drops the list's own level, so the definition's applies again.</summary>
    public void RemoveLevel()
    {
        if (level is not null)
        {
            level = null;
            owner.Notify($"Overrides[{Index}].Level");
        }
    }

    internal void CopyFrom(ListLevelOverride source, CloneContext? context)
    {
        startAt = source.startAt;
        if (source.level is not null)
        {
            level = NewLevel();
            level.CopyFrom(source.level, context);
        }
    }

    private ListLevel NewLevel()
    {
        return new ListLevel(owner.Definition, Index, property => owner.Notify($"Overrides[{Index}].Level.{property}"));
    }
}

/// <summary>
/// A list paragraphs belong to (Word's num): a <see cref="ListDefinition"/> and, per level, an optional new start or level of
/// its own. Paragraphs join it through <see cref="Paragraph.SetList"/> or their style; <see cref="Paragraph.ListLabel"/> gives
/// the computed label, such as "3.2.a".
/// </summary>
public sealed class List
{
    private readonly ListLevelOverride[] overrides = new ListLevelOverride[9];

    internal List(Document? document, int id, ListDefinition? definition)
    {
        Document = document;
        Id = id;
        Definition = definition!;
        for (int i = 0; i < overrides.Length; i++)
        {
            overrides[i] = new ListLevelOverride(this, i);
        }
    }

    /// <summary>
    /// "No list": in a paragraph's format it takes a paragraph out of the list its style puts it in (Word's numId 0). It belongs
    /// to no document.
    /// </summary>
    public static List None { get; } = new(null, 0, null);

    /// <summary>The document this list belongs to; null for <see cref="None"/>.</summary>
    public Document? Document { get; }

    /// <summary>The list's number in the document (Word's numId); 0 for <see cref="None"/>.</summary>
    public int Id { get; }

    /// <summary>The definition the list uses; null for <see cref="None"/>.</summary>
    public ListDefinition Definition { get; }

    /// <summary>The list's changes to each level, 0 to 8.</summary>
    public IReadOnlyList<ListLevelOverride> Overrides => overrides;

    /// <summary>The level <paramref name="index"/> as this list uses it: its own override, or the definition's.</summary>
    public ListLevel GetLevel(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 8);
        if (this == None)
        {
            throw new InvalidOperationException("List.None has no levels.");
        }

        return overrides[index].Level ?? Definition.Levels[index];
    }

    internal void Notify(string property)
    {
        if (Document is { } document && document.Lists.Contains(this))
        {
            document.NotifyFormattingChanged($"Lists.{Id}.{property}");
        }
    }

    internal void CopyOverrides(List source, CloneContext? context)
    {
        for (int i = 0; i < overrides.Length; i++)
        {
            overrides[i].CopyFrom(source.overrides[i], context);
        }
    }
}

/// <summary>A paragraph's computed list label.</summary>
/// <param name="Text">The label, such as "3.2.a." or a bullet character.</param>
/// <param name="List">The list the paragraph is in.</param>
/// <param name="Level">The paragraph's level, 0 to 8.</param>
/// <param name="Format">The level as the list uses it: label alignment, suffix and formatting.</param>
public sealed record ListLabel(string Text, List List, int Level, ListLevel Format);
