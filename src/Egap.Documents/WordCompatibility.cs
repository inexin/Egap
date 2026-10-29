namespace Egap.Documents;

/// <summary>
/// Word's compatibility options that change how Egap lays a document out, one named flag each (Word's <c>w:compat</c>
/// settings). A flag's value is the one the file or code set, else the default for the document's
/// <see cref="DocumentSettings.CompatibilityMode"/>, so changing the mode changes every flag nobody set. Word's other
/// compatibility options are read too, kept for round trips and listed in <see cref="OtherSettings"/>.
/// </summary>
/// <remarks>
/// A new document has Word 2013's settings: <see cref="OverrideTableStyleFontSizeAndJustification"/> and
/// <see cref="EnableOpenTypeFeatures"/> on, and Word's <c>doNotFlipMirrorIndents</c> and
/// <c>differentiateMultirowTableHeaders</c> in <see cref="OtherSettings"/>. A DOCX file's flags are what its settings part says; a flag the file does not
/// set is off, as ECMA-376 and Word's own extensions define it.
/// </remarks>
public sealed class WordCompatibility
{
    /// <summary>The flags, each with its name in DOCX and whether that name is a <c>w:compatSetting</c> (else a <c>w:compat</c> child).</summary>
    internal static readonly (string Name, bool IsSetting)[] Flags =
    [
        ("allowSpaceOfSameStyleInTable", false),
        ("doNotUseIndentAsNumberingTabStop", false),
        ("splitPgBreakAndParaMark", false),
        ("doNotBreakWrappedTables", false),
        ("overrideTableStyleFontSizeAndJustification", true),
        ("enableOpenTypeFeatures", true),
        ("allowTextAfterFloatingTableBreak", true),
        ("", false),
    ];

    internal const int AllowSpaceOfSameStyleInTableId = 0;
    internal const int DoNotUseIndentAsNumberingTabStopId = 1;
    internal const int SplitPageBreakAndParagraphMarkId = 2;
    internal const int DoNotBreakWrappedTablesId = 3;
    internal const int OverrideTableStyleFontSizeAndJustificationId = 4;
    internal const int EnableOpenTypeFeaturesId = 5;
    internal const int AllowTextAfterFloatingTableBreakId = 6;
    internal const int TableIndentFromCellTextId = 7;

    private readonly DocumentSettings settings;
    private readonly Document document;
    private readonly bool?[] explicitValues = new bool?[Flags.Length];
    private readonly Dictionary<string, string> other = new(StringComparer.Ordinal);

    internal WordCompatibility(DocumentSettings settings, Document document)
    {
        this.settings = settings;
        this.document = document;
        explicitValues[OverrideTableStyleFontSizeAndJustificationId] = true;
        explicitValues[EnableOpenTypeFeaturesId] = true;
        other["doNotFlipMirrorIndents"] = "1";
        other["differentiateMultirowTableHeaders"] = "1";
    }

    /// <summary>
    /// Contextual spacing (<see cref="ParagraphFormat.ContextualSpacing"/>) is ignored in table cells, so paragraphs of the
    /// same style there keep their space (Word's <c>allowSpaceOfSameStyleInTable</c>).
    /// </summary>
    public bool AllowSpaceOfSameStyleInTable
    {
        get => Get(AllowSpaceOfSameStyleInTableId);
        set => Set(AllowSpaceOfSameStyleInTableId, value);
    }

    /// <summary>
    /// A hanging indent is not a tab stop for the tab after a list label, which then goes to the next tab stop
    /// (Word's <c>doNotUseIndentAsNumberingTabStop</c>).
    /// </summary>
    public bool DoNotUseIndentAsNumberingTabStop
    {
        get => Get(DoNotUseIndentAsNumberingTabStopId);
        set => Set(DoNotUseIndentAsNumberingTabStopId, value);
    }

    /// <summary>
    /// The paragraph mark after a page break that ends a paragraph moves to the next page and takes a line there, as before
    /// Word 2013 (Word's <c>splitPgBreakAndParaMark</c>).
    /// </summary>
    public bool SplitPageBreakAndParagraphMark
    {
        get => Get(SplitPageBreakAndParagraphMarkId);
        set => Set(SplitPageBreakAndParagraphMarkId, value);
    }

    /// <summary>A floating table is kept on one page instead of continuing on the next (Word's <c>doNotBreakWrappedTables</c>).</summary>
    public bool DoNotBreakWrappedTables
    {
        get => Get(DoNotBreakWrappedTablesId);
        set => Set(DoNotBreakWrappedTablesId, value);
    }

    /// <summary>
    /// A table style's font size and alignment win over the default paragraph style's, as the style hierarchy says. When off,
    /// as in files older than Word 2010, a default paragraph style of 11 or 12 points leaves table text at the table style's
    /// size, and a left-aligned default paragraph style leaves it at the table style's alignment (Word's
    /// <c>overrideTableStyleFontSizeAndJustification</c>).
    /// </summary>
    public bool OverrideTableStyleFontSizeAndJustification
    {
        get => Get(OverrideTableStyleFontSizeAndJustificationId);
        set => Set(OverrideTableStyleFontSizeAndJustificationId, value);
    }

    /// <summary>
    /// The OpenType features runs ask for (<see cref="RunFormat.Features"/>: ligatures, contextual alternates, stylistic
    /// sets, number forms and spacing) are used. When off, as in files older than Word 2010, text is shaped without them
    /// (Word's <c>enableOpenTypeFeatures</c>); kerning (<see cref="RunFormat.Kerning"/>) still applies.
    /// </summary>
    public bool EnableOpenTypeFeatures
    {
        get => Get(EnableOpenTypeFeaturesId);
        set => Set(EnableOpenTypeFeaturesId, value);
    }

    /// <summary>Text may follow a floating table on the page where the table breaks (Word's <c>allowTextAfterFloatingTableBreak</c>).</summary>
    public bool AllowTextAfterFloatingTableBreak
    {
        get => Get(AllowTextAfterFloatingTableBreakId);
        set => Set(AllowTextAfterFloatingTableBreakId, value);
    }

    /// <summary>
    /// A left-aligned table's indent is measured to its first cell's text, so the table's edge sits the cell's left margin
    /// further left. Word has no setting of its own for this: it is on below compatibility mode 15 (Word 2013), and a value
    /// set here is not saved to DOCX.
    /// </summary>
    public bool TableIndentFromCellText
    {
        get => Get(TableIndentFromCellTextId);
        set => Set(TableIndentFromCellTextId, value);
    }

    /// <summary>
    /// Word's compatibility options read from the file that Egap keeps but does not act on, by name: a <c>w:compat</c>
    /// child's local name with its value (<c>1</c> when on), or a <c>w:compatSetting</c>'s name with its <c>val</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> OtherSettings => other;

    /// <summary>Forgets every flag value the file or code set, so each flag follows <see cref="DocumentSettings.CompatibilityMode"/>.</summary>
    public void ResetToModeDefaults()
    {
        if (Array.Exists(explicitValues, v => v is not null))
        {
            Array.Clear(explicitValues);
            document.NotifyFormattingChanged("Settings.WordCompatibility");
        }
    }

    /// <summary>The value a flag has when nobody set it, for <paramref name="mode"/>.</summary>
    internal static bool ModeDefault(int id, int mode)
    {
        return id == TableIndentFromCellTextId && mode < 15;
    }

    internal bool? GetExplicit(int id) => explicitValues[id];

    /// <summary>Sets what a reader found, without a change notification.</summary>
    internal void Load(bool?[] values, IEnumerable<KeyValuePair<string, string>> others)
    {
        values.CopyTo(explicitValues, 0);
        other.Clear();
        foreach ((string name, string value) in others)
        {
            other[name] = value;
        }
    }

    internal void CopyFrom(WordCompatibility source)
    {
        Load(source.explicitValues, source.other);
    }

    private bool Get(int id)
    {
        return explicitValues[id] ?? ModeDefault(id, settings.CompatibilityMode);
    }

    private void Set(int id, bool value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (explicitValues[id] != value)
        {
            explicitValues[id] = value;
            document.NotifyFormattingChanged("Settings.WordCompatibility." + property);
        }
    }
}
