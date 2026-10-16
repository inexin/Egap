using System.Collections;

namespace Egap.Documents;

/// <summary>What part of a node a <see cref="TrackedChange"/> is about.</summary>
public enum TrackedChangeTarget
{
    /// <summary>The node itself: an inline inserted, deleted or moved.</summary>
    Node,

    /// <summary>A paragraph's mark: inserted or deleted (a deleted mark joins the paragraph to the next).</summary>
    ParagraphMark,

    /// <summary>A table row, inserted or deleted.</summary>
    Row,

    /// <summary>A run's or paragraph's formatting.</summary>
    Format,
}

/// <summary>
/// One tracked change in a document (Phase 6 design Q8): a <see cref="Revision"/> or <see cref="FormatRevision"/> on a node,
/// which can be accepted or rejected on its own.
/// </summary>
public sealed class TrackedChange
{
    internal TrackedChange(Node node, TrackedChangeTarget target)
    {
        Node = node;
        Target = target;
    }

    /// <summary>The node the change is on.</summary>
    public Node Node { get; }

    /// <summary>What part of the node it is about.</summary>
    public TrackedChangeTarget Target { get; }

    /// <summary>The insertion, deletion or move; null for a format change.</summary>
    public Revision? Revision => Target switch
    {
        TrackedChangeTarget.Node => ((Inline)Node).Revision,
        TrackedChangeTarget.ParagraphMark => ((Paragraph)Node).MarkRevision,
        TrackedChangeTarget.Row => ((Row)Node).Revision,
        _ => null,
    };

    /// <summary>The format change; null for an insertion, deletion or move.</summary>
    public FormatRevision? FormatRevision => Target == TrackedChangeTarget.Format
        ? Node switch { Run run => run.FormatRevision, Paragraph paragraph => paragraph.FormatRevision, _ => null }
        : null;

    /// <summary>Who made the change.</summary>
    public string Author => Revision?.Author ?? FormatRevision?.Author ?? "";

    /// <summary>When, if known.</summary>
    public DateTimeOffset? Date => Revision?.Date ?? FormatRevision?.Date;

    /// <summary>Makes the change part of the document: inserted content stays, deleted content goes, new formatting stays.</summary>
    public void Accept()
    {
        Apply(accept: true);
    }

    /// <summary>Undoes the change: inserted content goes, deleted content stays, the old formatting comes back.</summary>
    public void Reject()
    {
        Apply(accept: false);
    }

    private void Apply(bool accept)
    {
        switch (Target)
        {
            case TrackedChangeTarget.Node when Node is Inline inline && inline.Revision is { } revision:
                if (revision.Removes == accept)
                {
                    inline.Remove();
                }
                else
                {
                    inline.Revision = null;
                }

                break;
            case TrackedChangeTarget.Row when Node is Row row && row.Revision is { } revision:
                if (revision.Removes == accept)
                {
                    row.Remove();
                }
                else
                {
                    row.Revision = null;
                }

                break;
            case TrackedChangeTarget.ParagraphMark when Node is Paragraph paragraph && paragraph.MarkRevision is { } revision:
                paragraph.MarkRevision = null;
                if (revision.Removes == accept)
                {
                    JoinWithNext(paragraph);
                }

                break;
            case TrackedChangeTarget.Format when Node is Run run && run.FormatRevision is { } change:
                if (!accept)
                {
                    run.Format.ClearFormatting();
                    run.Format.Apply(change.OldFormat);
                }

                run.FormatRevision = null;
                break;
            case TrackedChangeTarget.Format when Node is Paragraph paragraph && paragraph.FormatRevision is { } change:
                if (!accept)
                {
                    paragraph.Format.ClearFormatting();
                    paragraph.Format.Apply(change.OldFormat);
                }

                paragraph.FormatRevision = null;
                break;
        }
    }

    /// <summary>
    /// Removes a paragraph's mark: its inlines move to the start of the next paragraph, which keeps its own formatting, as when
    /// a paragraph mark is deleted in Word. A paragraph with no next paragraph in its container stays.
    /// </summary>
    private static void JoinWithNext(Paragraph paragraph)
    {
        if (paragraph.NextSibling is not Paragraph next)
        {
            return;
        }

        List<Inline> inlines = [.. paragraph.Inlines];
        paragraph.Inlines.Clear();
        for (int i = 0; i < inlines.Count; i++)
        {
            next.Inlines.Insert(i, inlines[i]);
        }

        paragraph.Remove();
    }
}

/// <summary>A document's tracked changes in document order, to read, accept or reject.</summary>
public sealed class TrackedChangeCollection : IEnumerable<TrackedChange>
{
    private readonly Document document;

    internal TrackedChangeCollection(Document document)
    {
        this.document = document;
    }

    /// <summary>How many tracked changes the document has.</summary>
    public int Count => Find().Count();

    /// <summary>Accepts every change.</summary>
    /// <returns>How many changes were accepted.</returns>
    public int AcceptAll()
    {
        return ApplyAll(accept: true);
    }

    /// <summary>Rejects every change.</summary>
    /// <returns>How many changes were rejected.</returns>
    public int RejectAll()
    {
        return ApplyAll(accept: false);
    }

    /// <summary>The changes in document order.</summary>
    public IEnumerator<TrackedChange> GetEnumerator()
    {
        return Find().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private int ApplyAll(bool accept)
    {
        // Format changes first, then node changes from the end, so removing nodes and joining paragraphs moves nothing still to do.
        List<TrackedChange> changes = [.. Find()];
        foreach (TrackedChange change in changes.Where(c => c.Target == TrackedChangeTarget.Format))
        {
            Do(change, accept);
        }

        foreach (TrackedChange change in Enumerable.Reverse(changes).Where(c => c.Target != TrackedChangeTarget.Format))
        {
            Do(change, accept);
        }

        return changes.Count;
    }

    private static void Do(TrackedChange change, bool accept)
    {
        if (accept)
        {
            change.Accept();
        }
        else
        {
            change.Reject();
        }
    }

    private IEnumerable<TrackedChange> Find()
    {
        foreach (Node node in document.Descendants())
        {
            switch (node)
            {
                case Run { FormatRevision: not null } run:
                    yield return new TrackedChange(run, TrackedChangeTarget.Format);
                    break;
                case Paragraph { FormatRevision: not null } paragraph:
                    yield return new TrackedChange(paragraph, TrackedChangeTarget.Format);
                    break;
            }

            switch (node)
            {
                case Inline { Revision: not null } inline:
                    yield return new TrackedChange(inline, TrackedChangeTarget.Node);
                    break;
                case Row { Revision: not null } row:
                    yield return new TrackedChange(row, TrackedChangeTarget.Row);
                    break;
            }
        }

        // Paragraph marks come after the paragraph's content in document order.
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            if (paragraph.MarkRevision is not null)
            {
                yield return new TrackedChange(paragraph, TrackedChangeTarget.ParagraphMark);
            }
        }
    }
}
