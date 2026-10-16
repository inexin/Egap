namespace Egap.Documents;

/// <summary>
/// One node of a document tree: the document itself, a section, a story, a block such as a paragraph or table, or an inline
/// such as a run of text. Every node belongs to one <see cref="Documents.Document"/> for its whole life and sits in at most one
/// place in that document's tree; a node made for one document goes into another through <see cref="Document.Import"/>.
/// </summary>
/// <remarks>One document is changed by one thread at a time; reading from several threads is safe while nobody changes it.</remarks>
public abstract class Node
{
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
    internal Node CloneInto(CloneContext context, bool deep)
    {
        Node copy = CloneShallow(context);
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

        return copy;
    }

    /// <summary>Shallow copy of a child for <see cref="CloneChildren"/>, queued for its own children.</summary>
    private protected static T CloneChild<T>(T child, CloneContext context, Stack<(Node Source, Node Copy)> pending)
        where T : Node
    {
        T copy = (T)child.CloneShallow(context);
        pending.Push((child, copy));
        return copy;
    }

    /// <summary>Sets where this node sits. Only collections and fixed slots call it.</summary>
    internal void Attach(Node parent, INodeCollection? collection, int index)
    {
        Parent = parent;
        Collection = collection;
        IndexInParent = index;
    }

    /// <summary>Marks this node as out of the tree.</summary>
    internal void Detach()
    {
        Parent = null;
        Collection = null;
        IndexInParent = 0;
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
