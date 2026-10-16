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

    /// <summary>A field's start, U+0013 as in Word.</summary>
    public const char FieldStart = '\u0013';

    /// <summary>A field's separator, U+0014 as in Word.</summary>
    public const char FieldSeparator = '\u0014';

    /// <summary>A field's end, U+0015 as in Word.</summary>
    public const char FieldEnd = '\u0015';

    /// <summary>A footnote or endnote reference, U+0002 as in Word; the note's text is not part of the paragraph's.</summary>
    public const char FootnoteReference = '\u0002';

    /// <summary>A comment reference, U+0005 as in Word.</summary>
    public const char CommentReference = '\u0005';

    /// <summary>A picture, shape or island without text, U+0001 as in Word.</summary>
    public const char Drawing = '\u0001';
}
