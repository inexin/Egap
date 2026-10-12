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

    /// <summary>A token that cannot appear where it was found, such as an unknown keyword or a stray <c>]</c>, was skipped.</summary>
    UnexpectedToken,

    /// <summary>An array or dictionary was not closed; it was closed where the object ended.</summary>
    UnterminatedObject,

    /// <summary>A dictionary key was not a name, or a key had no value; the entry was dropped.</summary>
    MalformedDictionary,

    /// <summary>A dictionary had the same key twice; the last value was kept.</summary>
    DuplicateKey,

    /// <summary>A reference had an object number below 1 or a generation above 65,535; it was read as null.</summary>
    InvalidReference,

    /// <summary>An indirect object had no <c>endobj</c>.</summary>
    MissingEndobj,

    /// <summary>The <c>stream</c> keyword was not followed by a line feed or carriage return and line feed.</summary>
    MalformedStreamKeyword,

    /// <summary>A stream's <c>/Length</c> was missing or wrong; its data was found by searching for <c>endstream</c>.</summary>
    WrongStreamLength,

    /// <summary>A stream had no <c>endstream</c>; its data was taken to run to <c>endobj</c> or the end of the file.</summary>
    MissingEndstream,

    /// <summary>A stream's encoded data was damaged; what decoded before the damage was kept.</summary>
    DamagedFilterData,

    /// <summary>A filter's <c>/DecodeParms</c> held values out of range; the filter ran with its defaults or without a predictor.</summary>
    MalformedFilterParameters,

    /// <summary>A cross-reference table or stream was malformed (wrong counts, bad entries, numbering off by one); it was read as far as it made sense.</summary>
    MalformedXref,

    /// <summary>A <c>/Prev</c> or <c>/XRefStm</c> pointed nowhere useful or back into the chain; older sections were not read.</summary>
    BrokenXrefChain,

    /// <summary>An object stream's header or offsets were malformed; the objects that could be found were kept.</summary>
    MalformedObjectStream,

    /// <summary>
    /// An object was not where the cross-reference said, or had another number or generation, or could not be loaded; the
    /// reference was read as null.
    /// </summary>
    MissingObject,

    /// <summary>The file has no <c>%PDF-n.n</c> header in its first kilobyte; version 1.7 was assumed.</summary>
    MissingHeader,

    /// <summary>
    /// The header was not at the start of the file, and the file's offsets count from the header; they were read that way.
    /// </summary>
    ShiftedOffsets,

    /// <summary>The cross-reference could not be used, or named no catalog; it was rebuilt by scanning the file for objects.</summary>
    XrefRebuilt,

    /// <summary>An object was not at the offset the cross-reference gave; it was found nearby or by scanning the file.</summary>
    ObjectRelocated,

    /// <summary>Encrypted data or the encryption dictionary was malformed; it was read as far as it made sense.</summary>
    MalformedEncryption,

    /// <summary>
    /// A name tree or number tree was malformed (its nodes loop, its limits or entries are missing, wrong or out of order);
    /// it was read as far as it made sense, and rebuilt from its entries when edited.
    /// </summary>
    MalformedTree,

    /// <summary>
    /// The page tree was malformed (kids that are not dictionaries or loop back, a wrong <c>/Type</c>, <c>/Parent</c> or
    /// <c>/Count</c>, or no tree at all); it was repaired in memory.
    /// </summary>
    MalformedPageTree,
}
