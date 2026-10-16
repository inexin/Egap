namespace Egap.Documents;

/// <summary>
/// A flow document: a list of sections, each with its page setup, body and headers and footers, holding paragraphs, tables
/// and the rest of the model (Phase 6 design Q3). The root of the tree; every other node belongs to exactly one document.
/// </summary>
public sealed class Document : Node
{
    /// <summary>An empty document, with no sections.</summary>
    public Document()
        : base(null)
    {
        Sections = new ChildCollection<Section>(this);
    }

    /// <summary>The sections in order.</summary>
    public NodeCollection<Section> Sections { get; }

    internal override int ChildCount => Sections.Count;

    /// <summary>Adds a new, empty section at the end.</summary>
    public Section AddSection()
    {
        return Sections.AddTrusted(new Section(this));
    }

    /// <summary>
    /// A copy of <paramref name="node"/> (from any document) that belongs to this one, not yet in the tree. A deep import
    /// includes everything the node holds. The styles and lists the copy uses come along with it from Build Plan tasks 3 and 4.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="node"/> is a document; use <see cref="Clone"/>.</exception>
    public Node Import(Node node, bool deep = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is Document)
        {
            throw new ArgumentException("A document cannot be imported into another; clone it instead.", nameof(node));
        }

        return node.CloneInto(new CloneContext(this), deep);
    }

    /// <summary>Imports <paramref name="node"/> as <see cref="Import(Node, bool)"/> does, typed.</summary>
    public T Import<T>(T node, bool deep = true)
        where T : Node
    {
        return (T)Import((Node)node, deep);
    }

    /// <summary>A copy of the whole document (or, shallow, an empty document).</summary>
    public override Document Clone(bool deep = true)
    {
        Document copy = new();
        if (deep)
        {
            CloneContext context = new(copy);
            foreach (Section section in Sections)
            {
                copy.Sections.AddTrusted((Section)section.CloneInto(context, deep: true));
            }
        }

        return copy;
    }

    internal override Node GetChild(int index)
    {
        return Sections[index];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitDocumentStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitDocumentEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        // Clone is overridden and Import refuses documents, so nothing reaches this.
        throw new InvalidOperationException("A document is copied with Document.Clone.");
    }
}
