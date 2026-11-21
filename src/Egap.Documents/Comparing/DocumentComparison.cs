namespace Egap.Documents.Comparing;

/// <summary>What a <see cref="DocumentChange"/> did to the text.</summary>
public enum CompareChangeKind
{
    /// <summary>Content only in the revised document: added.</summary>
    Inserted,

    /// <summary>Content only in the original document: removed.</summary>
    Deleted,

    /// <summary>Content of the original replaced by other content in the revised document.</summary>
    Replaced,

    /// <summary>The same text with other formatting, or a paragraph with other formatting or another style.</summary>
    Formatted,
}

/// <summary>
/// One difference between the two documents, in the result document's order: what changed, the text before and after, and
/// the tracked changes in <see cref="DocumentComparison.Document"/> that hold it, to accept or reject on their own.
/// </summary>
public sealed class DocumentChange
{
    internal DocumentChange(CompareChangeKind kind, string oldText, string newText, Story story, IReadOnlyList<TrackedChange> trackedChanges)
    {
        Kind = kind;
        OldText = oldText;
        NewText = newText;
        Story = story;
        TrackedChanges = trackedChanges;
    }

    /// <summary>What the change did.</summary>
    public CompareChangeKind Kind { get; }

    /// <summary>The text in the original document; empty for an insertion.</summary>
    public string OldText { get; }

    /// <summary>The text in the revised document; empty for a deletion.</summary>
    public string NewText { get; }

    /// <summary>The story of the result document the change is in, such as a section's <see cref="Body"/>.</summary>
    public Story Story { get; }

    /// <summary>The tracked changes in the result document that make up this change.</summary>
    public IReadOnlyList<TrackedChange> TrackedChanges { get; }

    /// <summary>Accepts every tracked change of this change: the result shows the revised content here.</summary>
    public void Accept()
    {
        foreach (TrackedChange change in Ordered())
        {
            change.Accept();
        }
    }

    /// <summary>Rejects every tracked change of this change: the result shows the original content here.</summary>
    public void Reject()
    {
        foreach (TrackedChange change in Ordered())
        {
            change.Reject();
        }
    }

    /// <summary>The change in one line.</summary>
    public override string ToString()
    {
        return Kind switch
        {
            CompareChangeKind.Inserted => $"Inserted \"{NewText}\"",
            CompareChangeKind.Deleted => $"Deleted \"{OldText}\"",
            _ => $"Replaced \"{OldText}\" with \"{NewText}\"",
        };
    }

    // Content first, then paragraph marks from the last, so joining paragraphs moves nothing still to do.
    private IEnumerable<TrackedChange> Ordered()
    {
        return [
            .. TrackedChanges.Where(c => c.Target != TrackedChangeTarget.ParagraphMark).Reverse(),
            .. TrackedChanges.Where(c => c.Target == TrackedChangeTarget.ParagraphMark).Reverse(),
        ];
    }
}

/// <summary>Counts of what a comparison found.</summary>
public sealed record DocumentCompareStatistics
{
    /// <summary>Words only in the revised document.</summary>
    public int InsertedWords { get; init; }

    /// <summary>Words only in the original document.</summary>
    public int DeletedWords { get; init; }

    /// <summary>Paragraphs of the result that hold a change, whole inserted and deleted ones included.</summary>
    public int ParagraphsTouched { get; init; }
}

/// <summary>What a <see cref="DocumentCompareWarning"/> is about.</summary>
public enum DocumentCompareWarningKind
{
    /// <summary>An input held tracked changes; it was compared as if they were accepted.</summary>
    ExistingRevisionsAccepted,

    /// <summary>
    /// The documents differ so much that a stretch was marked as deleted and inserted whole instead of word by word, to keep
    /// the comparison fast.
    /// </summary>
    EditLimitReached,

    /// <summary>The documents have different section breaks; the result keeps the base document's sections.</summary>
    SectionsDiffer,

    /// <summary>
    /// Content the result cannot mark as a tracked change (a kept island, or a content control, flex or grid container that
    /// could not be matched) was kept as it is in the base document.
    /// </summary>
    ContentNotTracked,
}

/// <summary>Something a comparison could not show exactly, with how many times it happened.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Message">What, in words.</param>
/// <param name="Count">How many times.</param>
public sealed record DocumentCompareWarning(DocumentCompareWarningKind Kind, string Message, int Count = 1)
{
    /// <summary>The warning as one line.</summary>
    public override string ToString()
    {
        return Count == 1 ? $"{Kind}: {Message}" : $"{Kind}: {Message} ({Count} times)";
    }
}

/// <summary>
/// The result of <see cref="DocumentCompare.Run"/>: a new document holding every difference as a tracked change, and the
/// list of those differences.
/// </summary>
public sealed class DocumentComparison
{
    internal DocumentComparison(Document document, IReadOnlyList<DocumentChange> changes, DocumentCompareStatistics statistics, IReadOnlyList<DocumentCompareWarning> warnings)
    {
        Document = document;
        Changes = changes;
        Statistics = statistics;
        Warnings = warnings;
    }

    /// <summary>
    /// The result: the base document with every difference as a tracked change. Accepting all its changes gives the revised
    /// content, rejecting all gives the original. Save it to DOCX, RTF or ODT to review it in a word processor.
    /// </summary>
    public Document Document { get; }

    /// <summary>The differences in document order.</summary>
    public IReadOnlyList<DocumentChange> Changes { get; }

    /// <summary>Whether the documents differ in what was compared.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Counts of inserted and deleted words and touched paragraphs.</summary>
    public DocumentCompareStatistics Statistics { get; }

    /// <summary>What the comparison could not show exactly.</summary>
    public IReadOnlyList<DocumentCompareWarning> Warnings { get; }

    /// <summary>A one-line summary.</summary>
    public override string ToString()
    {
        return HasChanges
            ? $"{Changes.Count} changes: {Statistics.InsertedWords} words inserted, {Statistics.DeletedWords} deleted"
            : "No changes";
    }
}
