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
}
