namespace Egap.Documents;

/// <summary>The characters <see cref="Node.GetText"/> uses for what is not text, as Word's own text ranges do.</summary>
public static class ControlChar
{
    /// <summary>A <see cref="Documents.Tab"/>.</summary>
    public const char Tab = '\t';

    /// <summary>A line break (<see cref="BreakKind.Line"/>), vertical tab as in Word.</summary>
    public const char LineBreak = '\v';

    /// <summary>A page break (<see cref="BreakKind.Page"/>), form feed as in Word.</summary>
    public const char PageBreak = '\f';

    /// <summary>A column break (<see cref="BreakKind.Column"/>), U+000E as in Word.</summary>
    public const char ColumnBreak = '\u000E';

    /// <summary>The end of a paragraph, carriage return as in Word.</summary>
    public const char ParagraphBreak = '\r';
}
