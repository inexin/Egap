namespace Egap.Documents;

/// <summary>
/// A flow document: a list of sections, each with its page setup, body and headers and footers, holding paragraphs, tables
/// and the rest of the model (Phase 6 design Q3). The root of the tree; every other node belongs to exactly one document.
/// </summary>
public sealed class Document : Node
{
    /// <summary>
    /// A new document with no sections, Word's Office theme and Word 2013+'s defaults: Normal, Default Paragraph Font and
    /// Normal Table styles, an 11 pt body font from the theme, 8 pt after paragraphs and 1.08 line spacing.
    /// </summary>
    public Document()
        : this(blank: false)
    {
    }

    /// <summary>A document with no styles and no defaults when <paramref name="blank"/>, for readers that fill in their own.</summary>
    internal Document(bool blank)
        : base(null)
    {
        Sections = new ChildCollection<Section>(this);
        Styles = new StyleCollection(this);
        Theme = new Theme(this);
        Lists = new ListCollection(this);
        if (!blank)
        {
            BuiltInStyles.InitializeNewDocument(Styles);
        }

        Version = 0;
        FormattingVersion = 0;
    }

    /// <summary>The sections in order.</summary>
    public NodeCollection<Section> Sections { get; }

    /// <summary>The document's styles and the defaults they build on.</summary>
    public StyleCollection Styles { get; }

    /// <summary>The theme colours and fonts that styles and runs may name.</summary>
    public Theme Theme { get; }

    /// <summary>The lists and list definitions paragraphs number with.</summary>
    public ListCollection Lists { get; }

    /// <summary>
    /// Counts changes to styles, document defaults and the theme, which change the effective formatting of many paragraphs
    /// at once; each one also counts in <see cref="Version"/>.
    /// </summary>
    public long FormattingVersion { get; private set; }

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
        Document copy = new(blank: true);
        CloneContext context = new(copy, wholeDocument: true);
        copy.Lists.CopyFrom(Lists, context);
        copy.Styles.CopyFrom(Styles, context);
        copy.Theme.CopyFrom(Theme);
        if (deep)
        {
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

        if (where switch { Table t => t, Row r => r.Table, Cell c => c.Row?.Table, _ => null } is { } table)
        {
            table.Stamp = version;
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

    /// <summary>
    /// The path for changes to styles, document defaults and the theme: counts them in both versions and raises
    /// <see cref="Changed"/> with the document as the node and <paramref name="property"/> saying what changed.
    /// </summary>
    internal void NotifyFormattingChanged(string property)
    {
        FormattingVersion++;
        long version = ++Version;
        Changed?.Invoke(this, new DocumentChangedEventArgs(this, DocumentChangeKind.FormattingChanged, null, -1, property, version));
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        // Clone is overridden and Import refuses documents, so nothing reaches this.
        throw new InvalidOperationException("A document is copied with Document.Clone.");
    }
}
