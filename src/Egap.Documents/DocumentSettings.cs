namespace Egap.Documents;

/// <summary>
/// Document-wide settings that change layout and numbering: the default tab stop, odd and even headers, footnote and endnote
/// numbering, change tracking and Word's compatibility mode. Word's other settings are kept by the DOCX reader as extension
/// data on the document.
/// </summary>
public sealed class DocumentSettings
{
    private readonly Document document;
    private double defaultTabStop = 36;
    private bool evenAndOddHeaders;
    private NumberStyle footnoteNumberStyle = NumberStyle.Decimal;
    private NumberStyle endnoteNumberStyle = NumberStyle.LowerRoman;
    private int footnoteStartAt = 1;
    private int endnoteStartAt = 1;
    private bool trackRevisions;
    private int compatibilityMode = 15;

    internal DocumentSettings(Document document)
    {
        this.document = document;
    }

    /// <summary>The distance between default tab stops, in points (Word's default 0.5 inch).</summary>
    public double DefaultTabStop
    {
        get => defaultTabStop;
        set => Set(ref defaultTabStop, double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Odd and even pages have their own headers and footers.</summary>
    public bool EvenAndOddHeaders
    {
        get => evenAndOddHeaders;
        set => Set(ref evenAndOddHeaders, value);
    }

    /// <summary>How footnote marks are numbered.</summary>
    public NumberStyle FootnoteNumberStyle
    {
        get => footnoteNumberStyle;
        set => Set(ref footnoteNumberStyle, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>How endnote marks are numbered.</summary>
    public NumberStyle EndnoteNumberStyle
    {
        get => endnoteNumberStyle;
        set => Set(ref endnoteNumberStyle, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The first footnote number.</summary>
    public int FootnoteStartAt
    {
        get => footnoteStartAt;
        set => Set(ref footnoteStartAt, value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The first endnote number.</summary>
    public int EndnoteStartAt
    {
        get => endnoteStartAt;
        set => Set(ref endnoteStartAt, value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Changes are tracked (a flag for editors and writers; the model does not record changes by itself).</summary>
    public bool TrackRevisions
    {
        get => trackRevisions;
        set => Set(ref trackRevisions, value);
    }

    /// <summary>Word's compatibility mode: 15 for Word 2013 and later, 14 for 2010, 12 for 2007, 11 and below for older layouts.</summary>
    public int CompatibilityMode
    {
        get => compatibilityMode;
        set => Set(ref compatibilityMode, value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    internal void CopyFrom(DocumentSettings source)
    {
        defaultTabStop = source.defaultTabStop;
        evenAndOddHeaders = source.evenAndOddHeaders;
        footnoteNumberStyle = source.footnoteNumberStyle;
        endnoteNumberStyle = source.endnoteNumberStyle;
        footnoteStartAt = source.footnoteStartAt;
        endnoteStartAt = source.endnoteStartAt;
        trackRevisions = source.trackRevisions;
        compatibilityMode = source.compatibilityMode;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            document.NotifyFormattingChanged("Settings." + property);
        }
    }
}
