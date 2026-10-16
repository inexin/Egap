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

    /// <summary>
    /// Counts the changes made to this document: every change, through any public member, adds one and stamps the paragraph
    /// and story it touched with the new value (<see cref="Paragraph.Version"/>, <see cref="Story.Version"/>).
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// Raised after every change to the document or any of its nodes, in or out of the tree, on the thread that made it. All
    /// changes go through one path, so nothing changes without it.
    /// </summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

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

    /// <summary>
    /// The one path every change takes: counts it, stamps the nearest paragraph and story, and raises <see cref="Changed"/>.
    /// <paramref name="where"/> is the node the change is inside (the parent for an insertion or removal, the node itself
    /// otherwise).
    /// </summary>
    internal void NotifyChanged(Node node, DocumentChangeKind kind, Node? where, int index = -1, string? property = null)
    {
        long version = ++Version;
        if ((where as Paragraph ?? where?.Parent as Paragraph) is { } paragraph)
        {
            paragraph.Version = version;
        }

        if (where?.OwnerStory is { } story)
        {
            story.Version = version;
        }

        Changed?.Invoke(this, new DocumentChangedEventArgs(node, kind, kind is DocumentChangeKind.NodeInserted or DocumentChangeKind.NodeRemoved ? where : node.Parent, index, property, version));
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
