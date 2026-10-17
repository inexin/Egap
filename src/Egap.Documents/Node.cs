namespace Egap.Documents;

/// <summary>
/// One node of a document tree: the document itself, a section, a story, a block such as a paragraph or table, or an inline
/// such as a run of text. Every node belongs to one <see cref="Documents.Document"/> for its whole life and sits in at most one
/// place in that document's tree; a node made for one document goes into another through <see cref="Document.Import"/>.
/// </summary>
/// <remarks>One document is changed by one thread at a time; reading from several threads is safe while nobody changes it.</remarks>
public abstract class Node : IFormatOwner
{
    private ExtensionData? extensions;

    private protected Node(Document? document)
    {
        Document = document ?? this as Document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>The document this node belongs to; a document belongs to itself.</summary>
    public Document Document { get; }

    /// <summary>The node that holds this one, or null while it is not in the tree (and for the document).</summary>
    public Node? Parent { get; private set; }

    /// <summary>The node after this one in the same collection, or null when this is the last or is not in a collection.</summary>
    public Node? NextSibling => Collection is { } list && IndexInParent + 1 < list.Count ? list.GetNode(IndexInParent + 1) : null;

    /// <summary>The node before this one in the same collection, or null when this is the first or is not in a collection.</summary>
    public Node? PreviousSibling => Collection is { } list && IndexInParent > 0 ? list.GetNode(IndexInParent - 1) : null;

    /// <summary>
    /// What format readers kept on this node that the model does not name (Phase 6 design Q7), for their writers to put
    /// back. Made on first use.
    /// </summary>
    public ExtensionData Extensions => extensions ??= new ExtensionData();

    /// <summary>Whether a format kept anything on this node.</summary>
    public bool HasExtensions => extensions is { IsEmpty: false };

    /// <summary>Copies another node's extension data, for whole-document copies.</summary>
    internal void CopyExtensionsFrom(Node source)
    {
        extensions = source.extensions?.Copy();
    }

    /// <summary>The nodes this one holds, in document order; empty for an inline.</summary>
    public IEnumerable<Node> ChildNodes
    {
        get
        {
            for (int i = 0; i < ChildCount; i++)
            {
                yield return GetChild(i);
            }
        }
    }

    /// <summary>The collection this node is in, or null for a node in a fixed slot (a section's body) or not in the tree.</summary>
    internal INodeCollection? Collection { get; private set; }

    /// <summary>
    /// The story this node is in (itself for a story), or null outside any story, kept up to date on attach and detach so
    /// a change finds the story to stamp without walking up through nested tables.
    /// </summary>
    internal Story? OwnerStory { get; private set; }

    /// <summary>This node's index in <see cref="Collection"/>, kept up to date by the collection.</summary>
    internal int IndexInParent { get; set; }

    /// <summary>How many children this node has, for the walks that must not recurse.</summary>
    internal virtual int ChildCount => 0;

    /// <summary>
    /// A copy of this node in the same document, not yet in the tree. A deep copy includes everything the node holds; a shallow
    /// one only the node itself, such as a paragraph without its runs.
    /// </summary>
    public virtual Node Clone(bool deep = true)
    {
        return CloneInto(new CloneContext(Document), deep);
    }

    /// <summary>Takes this node out of the tree; it stays in its document and can be put back elsewhere.</summary>
    /// <exception cref="InvalidOperationException">The node sits in a fixed slot, as a section's body does.</exception>
    public void Remove()
    {
        if (Collection is { } list)
        {
            list.RemoveAt(IndexInParent);
        }
        else if (Parent is not null)
        {
            throw new InvalidOperationException($"{GetType().Name} cannot be removed from its {Parent.GetType().Name}.");
        }
    }

    /// <summary>The nodes that hold this one, nearest first.</summary>
    public IEnumerable<Node> Ancestors()
    {
        for (Node? node = Parent; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }

    /// <summary>The nearest node of type <typeparamref name="T"/> that holds this one, or null.</summary>
    public T? GetAncestor<T>()
        where T : Node
    {
        for (Node? node = Parent; node is not null; node = node.Parent)
        {
            if (node is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Every node under this one in document order (parents before their children), without recursion.</summary>
    public IEnumerable<Node> Descendants()
    {
        Stack<(Node Node, int Next)> stack = new();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            (Node node, int next) = stack.Pop();
            if (next < node.ChildCount)
            {
                stack.Push((node, next + 1));
                Node child = node.GetChild(next);
                yield return child;
                stack.Push((child, 0));
            }
        }
    }

    /// <summary>Every node of type <typeparamref name="T"/> under this one, in document order.</summary>
    public IEnumerable<T> Descendants<T>()
        where T : Node
    {
        return Descendants().OfType<T>();
    }

    /// <summary>
    /// The plain text of this node in Word's conventions: tab, line break (<see cref="ControlChar.LineBreak"/>), page and
    /// column breaks as control characters, and a <see cref="ControlChar.ParagraphBreak"/> after each paragraph inside a block
    /// container. A paragraph's own text has no mark at its end.
    /// </summary>
    public string GetText()
    {
        TextVisitor visitor = new(this);
        Accept(visitor);
        return visitor.Text.ToString();
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="find"/> in the paragraphs under this node (or in this paragraph) with
    /// <paramref name="replacement"/>, also where the text crosses run boundaries; the replacement takes the formatting of
    /// the first run a match touches.
    /// </summary>
    /// <returns>How many occurrences were replaced.</returns>
    public int Replace(string find, string replacement, FindOptions? options = null)
    {
        return FindReplace.Replace(this, find, replacement, options);
    }

    /// <summary>Replaces every match of <paramref name="regex"/>; <paramref name="replacement"/> may name groups as $1 or ${name}.</summary>
    /// <returns>How many matches were replaced.</returns>
    public int Replace(System.Text.RegularExpressions.Regex regex, string replacement, FindOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return FindReplace.Replace(this, regex, m => m.Result(replacement), options);
    }

    /// <summary>Replaces every match of <paramref name="regex"/> with what <paramref name="evaluator"/> returns for it.</summary>
    /// <returns>How many matches were replaced.</returns>
    public int Replace(System.Text.RegularExpressions.Regex regex, Func<System.Text.RegularExpressions.Match, string> evaluator, FindOptions? options = null)
    {
        return FindReplace.Replace(this, regex, evaluator, options);
    }

    /// <summary>
    /// Walks this node and everything under it, calling the visitor on the way in and out of each node, without recursion.
    /// </summary>
    /// <returns>False when the visitor stopped the walk with <see cref="VisitorAction.Stop"/>.</returns>
    public bool Accept(DocumentVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Stack<(Node Node, int Next)> stack = new();
        VisitorAction start = VisitStart(visitor);
        if (start == VisitorAction.Stop)
        {
            return false;
        }

        stack.Push((this, start == VisitorAction.SkipChildren ? int.MaxValue : 0));
        while (stack.Count > 0)
        {
            (Node node, int next) = stack.Pop();
            if (next < node.ChildCount)
            {
                stack.Push((node, next + 1));
                Node child = node.GetChild(next);
                VisitorAction action = child.VisitStart(visitor);
                if (action == VisitorAction.Stop)
                {
                    return false;
                }

                stack.Push((child, action == VisitorAction.SkipChildren ? int.MaxValue : 0));
            }
            else if (node.VisitEnd(visitor) == VisitorAction.Stop)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Child <paramref name="index"/>, for the walks that must not recurse.</summary>
    internal virtual Node GetChild(int index)
    {
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>Calls the visitor's method for entering this node (the only call for a node without children).</summary>
    private protected abstract VisitorAction VisitStart(DocumentVisitor visitor);

    /// <summary>Calls the visitor's method for leaving this node; nodes without children have none.</summary>
    private protected virtual VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A node of the same kind and settings in <paramref name="context"/>'s document, holding nothing.</summary>
    private protected abstract Node CloneShallow(CloneContext context);

    /// <summary>Copies this node's children into <paramref name="copy"/> (a shallow clone of this node) and queues them.</summary>
    private protected virtual void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
    }

    /// <summary>A copy of this node in <paramref name="context"/>'s document, deep or shallow, without recursion.</summary>
    internal Node CloneInto(CloneContext context, bool deep, bool finish = true)
    {
        Node copy = CloneShallow(context);
        copy.extensions = extensions?.Copy();
        if (deep)
        {
            Stack<(Node Source, Node Copy)> pending = new();
            pending.Push((this, copy));
            while (pending.Count > 0)
            {
                (Node source, Node target) = pending.Pop();
                source.CloneChildren(target, context, pending);
            }
        }

        if (finish)
        {
            context.Finish();
        }

        return copy;
    }

    /// <summary>Shallow copy of a child for <see cref="CloneChildren"/>, queued for its own children.</summary>
    private protected static T CloneChild<T>(T child, CloneContext context, Stack<(Node Source, Node Copy)> pending)
        where T : Node
    {
        T copy = (T)child.CloneShallow(context);
        copy.extensions = child.extensions?.Copy();
        pending.Push((child, copy));
        return copy;
    }

    void IFormatOwner.FormatChanged(FormatBase format, string property)
    {
        Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: FormatPropertyPrefix(format) + property);
    }

    /// <summary>A format owned by this node, reporting its changes through the node.</summary>
    private protected T Own<T>(T format)
        where T : FormatBase
    {
        format.Owner = this;
        return format;
    }

    /// <summary>The prefix of a format property's name in change notifications, such as "Format." or "MarkFormat.".</summary>
    private protected virtual string FormatPropertyPrefix(FormatBase format)
    {
        return "Format.";
    }

    /// <summary>Sets a property and reports the change when the value is new.</summary>
    private protected void SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: property);
        }
    }

    /// <summary>Sets a style reference, checking the style is in this document and reporting the change.</summary>
    private protected void SetStyle<T>(ref T? field, T? value)
        where T : Style
    {
        if (value is not null && (value.Document != Document || !value.IsInDocument))
        {
            throw new ArgumentException($"Style {value.Id} is not in this node's document.", nameof(value));
        }

        if (field != value)
        {
            field = value;
            Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: "Style");
        }
    }

    /// <summary>Sets where this node sits. Only collections and fixed slots call it.</summary>
    internal void Attach(Node parent, INodeCollection? collection, int index)
    {
        Parent = parent;
        Collection = collection;
        IndexInParent = index;
        SetOwnerStory(parent as Story ?? parent.OwnerStory);
    }

    /// <summary>Marks this node as out of the tree.</summary>
    internal void Detach()
    {
        Parent = null;
        Collection = null;
        IndexInParent = 0;
        SetOwnerStory(null);
    }

    /// <summary>Makes a story its own <see cref="OwnerStory"/>.</summary>
    private protected void InitOwnerStory()
    {
        OwnerStory = (Story)this;
    }

    /// <summary>Sets <see cref="OwnerStory"/> on this node and every node under it that is not a story itself.</summary>
    private void SetOwnerStory(Story? story)
    {
        if (this is Story || OwnerStory == story)
        {
            return;
        }

        OwnerStory = story;
        if (ChildCount == 0)
        {
            return;
        }

        Stack<Node> pending = new();
        pending.Push(this);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            for (int i = 0; i < node.ChildCount; i++)
            {
                Node child = node.GetChild(i);
                if (child is not Story)
                {
                    child.OwnerStory = story;
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// Checks that <paramref name="child"/> may go under <paramref name="parent"/>: same document, not already in the tree, and
    /// not the parent itself or one of its ancestors.
    /// </summary>
    internal static void CheckInsert(Node parent, Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Document != parent.Document)
        {
            throw new ArgumentException($"The {child.GetType().Name} belongs to another document; bring it over with Document.Import.", nameof(child));
        }

        if (child.Parent is not null)
        {
            throw new ArgumentException($"The {child.GetType().Name} is already in the tree; remove it first.", nameof(child));
        }

        for (Node? node = parent; node is not null; node = node.Parent)
        {
            if (node == child)
            {
                throw new ArgumentException($"The {child.GetType().Name} holds the node it would be added to.", nameof(child));
            }
        }
    }

    /// <summary>Gathers <see cref="GetText"/>.</summary>
    private sealed class TextVisitor(Node root) : DocumentVisitor
    {
        public System.Text.StringBuilder Text { get; } = new();

        public override VisitorAction VisitRun(Run run)
        {
            Text.Append(run.Text);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitTab(Tab tab)
        {
            Text.Append(ControlChar.Tab);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitBreak(Break breakNode)
        {
            Text.Append(breakNode.Kind switch
            {
                BreakKind.Page => ControlChar.PageBreak,
                BreakKind.Column => ControlChar.ColumnBreak,
                _ => ControlChar.LineBreak,
            });
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitFieldStart(FieldStart fieldStart)
        {
            Text.Append(ControlChar.FieldStart);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitFieldSeparator(FieldSeparator separator)
        {
            Text.Append(ControlChar.FieldSeparator);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitFieldEnd(FieldEnd fieldEnd)
        {
            Text.Append(ControlChar.FieldEnd);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitFootnoteStart(Footnote note)
        {
            Text.Append(ControlChar.FootnoteReference);
            return note == root ? VisitorAction.Continue : VisitorAction.SkipChildren;
        }

        public override VisitorAction VisitCommentStart(Comment comment)
        {
            Text.Append(ControlChar.CommentReference);
            return comment == root ? VisitorAction.Continue : VisitorAction.SkipChildren;
        }

        public override VisitorAction VisitShapeStart(Shape shape)
        {
            Text.Append(ControlChar.Drawing);
            return shape == root ? VisitorAction.Continue : VisitorAction.SkipChildren;
        }

        public override VisitorAction VisitPicture(Picture picture)
        {
            Text.Append(ControlChar.Drawing);
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitIsland(Island island)
        {
            Text.Append(island.Text.Length > 0 ? island.Text : ControlChar.Drawing.ToString());
            return VisitorAction.Continue;
        }

        public override VisitorAction VisitParagraphEnd(Paragraph paragraph)
        {
            if (paragraph != root)
            {
                Text.Append(ControlChar.ParagraphBreak);
            }

            return VisitorAction.Continue;
        }
    }
}
