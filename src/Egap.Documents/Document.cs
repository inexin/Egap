namespace Egap.Documents;

/// <summary>
/// A flow document: a list of sections, each with its page setup, body and headers and footers, holding paragraphs, tables
/// and the rest of the model (Phase 6 design Q3). The root of the tree; every other node belongs to exactly one document.
/// </summary>
public sealed partial class Document : Node
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
        Properties = new DocumentProperties(this);
        Settings = new DocumentSettings(this);
        Revisions = new TrackedChangeCollection(this);
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

    /// <summary>Title, author, dates and custom properties.</summary>
    public DocumentProperties Properties { get; }

    /// <summary>Document-wide settings: default tab stop, odd and even headers, note numbering, compatibility mode.</summary>
    public DocumentSettings Settings { get; }

    /// <summary>The font table's hints by font name: the alternative name and the kind Word substitutes a missing font by.</summary>
    internal Dictionary<string, (string? AltName, string? Kind)> FontHints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fonts the file carries for its text, read from a DOCX's font table and written back with it.</summary>
    public IList<EmbeddedFont> EmbeddedFonts { get; } = new List<EmbeddedFont>();

    /// <summary>The tracked changes, to accept or reject (Phase 6 design Q8).</summary>
    public TrackedChangeCollection Revisions { get; }

    private Layout.LayoutOptions? defaultLayoutOptions;

    /// <summary>The file the document was loaded from or last saved to, for FILENAME fields; null when not known.</summary>
    public string? FileName
    {
        get => fileName;
        set => SetProperty(ref fileName, value);
    }

    /// <summary>
    /// Lays the document out into pages (Phase 6 design Q11 and Q13): an immutable snapshot linked to this document's nodes;
    /// an edit needs a new layout. Paragraphs that did not change since the last layout are reused.
    /// </summary>
    public Layout.DocumentLayout Layout(Layout.LayoutOptions? options = null)
    {
        return new Layout.Paginator(this, options ?? DefaultLayoutOptions).Run();
    }

    /// <summary>
    /// The layout options used when a layout or an export is asked for without any, or null for the defaults. A report built
    /// with <see cref="Reports.Report.Create"/> keeps strict layout with portable fonts here.
    /// </summary>
    public Layout.LayoutOptions? DefaultLayoutOptions
    {
        get => defaultLayoutOptions;
        set => SetProperty(ref defaultLayoutOptions, value);
    }

    /// <summary>
    /// Recomputes the fields Egap understands (Phase 6 design Q8): DATE, TIME and the other date fields, the document
    /// property fields, REF, NOTEREF, SEQ, STYLEREF, HYPERLINK, IF on simple comparisons and TOC. Page fields are filled by
    /// layout; locked fields and others keep their stored result.
    /// </summary>
    /// <param name="now">The time DATE and TIME show; the current time when null.</param>
    /// <returns>How many fields were updated.</returns>
    public int UpdateFields(DateTimeOffset? now = null)
    {
        return FieldUpdater.Update(this, now ?? DateTimeOffset.Now);
    }

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

    private int nextCommentId;
    private string? fileName;

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
        copy.Properties.CopyFrom(Properties);
        copy.Settings.CopyFrom(Settings);
        foreach ((string name, (string? AltName, string? Kind) hint) in FontHints)
        {
            copy.FontHints[name] = hint;
        }

        copy.fileName = fileName;
        copy.CopyExtensionsFrom(this);
        foreach (EmbeddedFont font in EmbeddedFonts)
        {
            copy.EmbeddedFonts.Add(font);
        }
        if (deep)
        {
            foreach (Section section in Sections)
            {
                copy.Sections.AddTrusted((Section)section.CloneInto(context, deep: true, finish: false));
            }

            context.Finish();
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
        // A node not in the tree (built but not inserted yet, or taken out) changes nothing the document shows.
        Node? at0 = where ?? node;
        while (at0 is not null && at0 != this)
        {
            at0 = at0.Parent;
        }

        if (at0 is null)
        {
            return;
        }

        long version = ++Version;
        // The paragraph a change is in, through hyperlinks and content controls but not out of a story (a text box or note).
        Node? at = where;
        while (at is not null and not Paragraph and not Story)
        {
            at = at.Parent;
        }

        if (at is Paragraph paragraph)
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

    /// <summary>The next unused comment id.</summary>
    internal int NextCommentId()
    {
        return nextCommentId++;
    }

    /// <summary>Makes sure new comment ids come after <paramref name="id"/>, for readers and whole-document copies.</summary>
    internal void ReserveCommentId(int id)
    {
        nextCommentId = Math.Max(nextCommentId, id + 1);
    }

    /// <summary>The comment with <paramref name="id"/>, or null.</summary>
    internal Comment? FindComment(int id)
    {
        foreach (Comment comment in Descendants<Comment>())
        {
            if (comment.Id == id)
            {
                return comment;
            }
        }

        return null;
    }

    /// <summary>Every bookmark, in document order, each start paired with the next end of the same name in its story.</summary>
    public IEnumerable<Bookmark> GetBookmarks()
    {
        Dictionary<(Story?, string), BookmarkEnd> ends = [];
        List<BookmarkStart> starts = [];
        foreach (Node node in Descendants())
        {
            switch (node)
            {
                case BookmarkStart start:
                    starts.Add(start);
                    break;
                case BookmarkEnd end:
                    ends.TryAdd((end.OwnerStory, end.Name), end);
                    break;
            }
        }

        return starts.Select(s => new Bookmark(s, ends.GetValueOrDefault((s.OwnerStory, s.Name))));
    }

    /// <summary>The bookmark named <paramref name="name"/> (Word compares bookmark names without case), or null.</summary>
    public Bookmark? GetBookmark(string name)
    {
        return GetBookmarks().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every field in document order, nested ones after the field holding them; fields with no end are left out.</summary>
    public IEnumerable<Field> GetFields()
    {
        return Descendants<FieldStart>().Select(s => s.GetField()).OfType<Field>();
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
