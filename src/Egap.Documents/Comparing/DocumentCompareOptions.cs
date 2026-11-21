namespace Egap.Documents.Comparing;

/// <summary>How finely <see cref="DocumentCompare"/> marks changed text.</summary>
public enum CompareGranularity
{
    /// <summary>Whole words: a changed letter marks its whole word as deleted and inserted again, as Word does by default.</summary>
    Word,

    /// <summary>Single characters (grapheme clusters): only the letters that changed are marked.</summary>
    Character,
}

/// <summary>Which input the comparison's result document is built on.</summary>
public enum CompareBase
{
    /// <summary>The revised document: the result keeps its styles, sections and settings (Word's "new document").</summary>
    Revised,

    /// <summary>The original document: the result keeps its styles, sections and settings.</summary>
    Original,
}

/// <summary>What <see cref="DocumentCompare"/> does with inputs that already hold tracked changes.</summary>
public enum ExistingRevisionHandling
{
    /// <summary>Compares each input as if its changes were accepted (on a copy) and adds a warning.</summary>
    Accept,

    /// <summary>Throws an <see cref="InvalidOperationException"/>.</summary>
    Throw,
}

/// <summary>Settings for <see cref="DocumentCompare.Run"/>; the defaults match Word's Compare.</summary>
public sealed record DocumentCompareOptions
{
    /// <summary>The default settings.</summary>
    public static DocumentCompareOptions Default { get; } = new();

    /// <summary>Whether changed text is marked by whole words (the default) or by characters.</summary>
    public CompareGranularity Granularity { get; init; } = CompareGranularity.Word;

    /// <summary>
    /// Whether formatting is compared: the same text in another look, or a paragraph with another style or paragraph
    /// formatting, becomes a format change. Null (the default) compares formatting unless the two inputs were read from
    /// different formats (<see cref="Document.SourceFormat"/>), as a DOCX file and an HTML page of the same text differ in
    /// nearly every run's formatting; the comparison then warns. True or false always or never compares it.
    /// </summary>
    public bool? Formatting { get; init; }

    /// <summary>
    /// Whether moved paragraphs are found (the default): a run of deleted paragraphs that is inserted again elsewhere, with
    /// at least six words, becomes a move whose source and destination share a name. As in Word, moves inside a paragraph
    /// are not looked for. When false they are a deletion and an insertion.
    /// </summary>
    public bool Moves { get; init; } = true;

    /// <summary>Whether a change of case only (such as "word" to "Word") counts as a change. Default true.</summary>
    public bool CaseChanges { get; init; } = true;

    /// <summary>Whether a change of white space only (such as one space to two) counts as a change. Default true.</summary>
    public bool Whitespace { get; init; } = true;

    /// <summary>The author every tracked change is stamped with. Default "Egap".</summary>
    public string Author { get; init; } = "Egap";

    /// <summary>The date every tracked change is stamped with, or null (the default) to write none.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>Which input the result is built on. Default <see cref="CompareBase.Revised"/>.</summary>
    public CompareBase Base { get; init; } = CompareBase.Revised;

    /// <summary>What to do with inputs that already hold tracked changes. Default <see cref="ExistingRevisionHandling.Accept"/>.</summary>
    public ExistingRevisionHandling ExistingRevisions { get; init; } = ExistingRevisionHandling.Accept;

    /// <summary>
    /// Whether headers and footers are compared, each with the one of the same kind (first, even, default) in the section at
    /// the same place. Default true; when false the base document's are kept as they are.
    /// </summary>
    public bool HeadersFooters { get; init; } = true;

    /// <summary>
    /// Whether the text of footnotes and endnotes is compared. A note's reference mark is always compared as one token; with
    /// this on (the default), the text of a note in both documents is compared as a story of its own. When false the base
    /// document's note text is kept.
    /// </summary>
    public bool Notes { get; init; } = true;

    /// <summary>
    /// Whether the result gets the revised document's comments (the default): comments are matched by the text they anchor
    /// and their own text, a comment only in the original is removed and one only in the revised document is added, at the
    /// same anchor text. Comments are never tracked changes. When false the base document's comments are kept.
    /// </summary>
    public bool Comments { get; init; } = true;

    /// <summary>
    /// Whether the text of text boxes is compared as a story of its own (the default). A shape is always compared by its kind
    /// and size as one token: a shape whose geometry changed is marked deleted and inserted. When false the base document's
    /// text box text is kept.
    /// </summary>
    public bool TextBoxes { get; init; } = true;

    /// <summary>
    /// Whether fields are compared by their code and result (the default) or by their result only. Either way a field is one
    /// token, marked deleted and inserted whole when it differs.
    /// </summary>
    public bool Fields { get; init; } = true;
}
