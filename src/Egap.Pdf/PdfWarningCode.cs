namespace Egap.Pdf;

/// <summary>What kind of repair a <see cref="PdfWarning"/> records.</summary>
public enum PdfWarningCode
{
    /// <summary>A number was malformed (such as <c>--5</c>, <c>1.2.3</c> or <c>4-2</c>) and was read the way other readers do.</summary>
    MalformedNumber = 1,

    /// <summary>An integer was too large for 64 bits and was read as a real number.</summary>
    NumberOutOfRange,

    /// <summary>A name had a <c>#</c> not followed by two hex digits; the <c>#</c> was kept as is.</summary>
    MalformedName,

    /// <summary>A literal string ran to the end of the data without its closing parenthesis.</summary>
    UnterminatedString,

    /// <summary>A hex string held a character that is not a hex digit or white space, or had no closing <c>&gt;</c>.</summary>
    MalformedHexString,

    /// <summary>A stray delimiter (<c>)</c> or a single <c>&gt;</c>) was skipped.</summary>
    UnexpectedDelimiter,

    /// <summary>Two tokens were run together with no white space or delimiter between them.</summary>
    MissingSeparator,

    /// <summary>The file had so many warnings that the rest were dropped.</summary>
    TooManyWarnings,
}
