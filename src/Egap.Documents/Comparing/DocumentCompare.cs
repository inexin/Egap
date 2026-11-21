namespace Egap.Documents.Comparing;

/// <summary>
/// Compares two documents and returns a third that holds every difference as a tracked change, like Word's Compare. The
/// documents may come from any format Egap reads, also from two different formats, as every reader fills the same model.
/// </summary>
/// <remarks>
/// The comparison lines up blocks first (paragraphs and tables, by a hash of their content), pairs changed paragraphs that
/// share at least half their words, and then marks the words that differ inside each pair. Accepting every change of the
/// result gives the revised content; rejecting every change gives the original. Main body text is compared; tables that
/// differ are marked deleted and inserted whole.
/// </remarks>
public static class DocumentCompare
{
    /// <summary>Compares <paramref name="original"/> with <paramref name="revised"/>; neither is changed.</summary>
    /// <param name="original">The earlier version.</param>
    /// <param name="revised">The later version.</param>
    /// <param name="options">The comparison's settings, or null for Word's defaults.</param>
    /// <returns>The result document with tracked changes, and the list of changes.</returns>
    /// <exception cref="InvalidOperationException">
    /// An input holds tracked changes and <see cref="DocumentCompareOptions.ExistingRevisions"/> is
    /// <see cref="ExistingRevisionHandling.Throw"/>.
    /// </exception>
    public static DocumentComparison Run(Document original, Document revised, DocumentCompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(revised);
        options ??= DocumentCompareOptions.Default;
        ArgumentNullException.ThrowIfNull(options.Author, nameof(options));

        List<DocumentCompareWarning> warnings = [];
        bool baseIsRevised = options.Base == CompareBase.Revised;
        Document a = Prepare(original, "original", copy: !baseIsRevised, options, warnings);
        Document b = Prepare(revised, "revised", copy: baseIsRevised, options, warnings);
        BlockComparer comparer = new(baseIsRevised ? b : a, baseIsRevised, options, warnings);
        comparer.CompareBodies(a, b);
        return comparer.Finish();
    }

    /// <summary>The input to compare: a copy for the base (the result is built on it), and the accepted view when it has changes.</summary>
    private static Document Prepare(Document input, string name, bool copy, DocumentCompareOptions options, List<DocumentCompareWarning> warnings)
    {
        int count = input.Revisions.Count;
        if (count == 0)
        {
            return copy ? input.Clone() : input;
        }

        if (options.ExistingRevisions == ExistingRevisionHandling.Throw)
        {
            throw new InvalidOperationException($"The {name} document holds {count} tracked changes.");
        }

        Document accepted = input.Clone();
        accepted.Revisions.AcceptAll();
        warnings.Add(new DocumentCompareWarning(
            DocumentCompareWarningKind.ExistingRevisionsAccepted, $"The {name} document's tracked changes were accepted before comparing.", count));
        return accepted;
    }
}
