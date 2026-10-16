namespace Egap.Documents;

/// <summary>What happened in a <see cref="Document.Changed"/> notification.</summary>
public enum DocumentChangeKind
{
    /// <summary>A node was put into a collection; <see cref="DocumentChangedEventArgs.Node"/> is the node, already in place.</summary>
    NodeInserted,

    /// <summary>A node was taken out; <see cref="DocumentChangedEventArgs.Parent"/> is where it was.</summary>
    NodeRemoved,

    /// <summary>A run's text changed.</summary>
    TextChanged,

    /// <summary>A setting of the node changed, named by <see cref="DocumentChangedEventArgs.Property"/>.</summary>
    PropertyChanged,

    /// <summary>
    /// A style, the document defaults or the theme changed (<see cref="DocumentChangedEventArgs.Node"/> is the document and
    /// <see cref="DocumentChangedEventArgs.Property"/> says what), so any paragraph's effective formatting may have changed.
    /// </summary>
    FormattingChanged,
}

/// <summary>
/// One change to a document (Phase 6 design Q3, editor-ready model): the node, what happened to it and where. Code that keeps
/// a view of the document, such as another library's editor or a cache, redraws from these; undo and redo can be built on
/// them too.
/// </summary>
public sealed class DocumentChangedEventArgs : EventArgs
{
    internal DocumentChangedEventArgs(Node node, DocumentChangeKind kind, Node? parent, int index, string? property, long version)
    {
        Node = node;
        Kind = kind;
        Parent = parent;
        Index = index;
        Property = property;
        Version = version;
    }

    /// <summary>The node that changed, was inserted or was removed.</summary>
    public Node Node { get; }

    /// <summary>What happened.</summary>
    public DocumentChangeKind Kind { get; }

    /// <summary>The node's parent: for an insertion where it now is, for a removal where it was; null for a node out of the tree.</summary>
    public Node? Parent { get; }

    /// <summary>For an insertion or removal, the node's index in its parent's collection; otherwise -1.</summary>
    public int Index { get; }

    /// <summary>For <see cref="DocumentChangeKind.PropertyChanged"/>, the name of the property; otherwise null.</summary>
    public string? Property { get; }

    /// <summary>The document's <see cref="Document.Version"/> after this change.</summary>
    public long Version { get; }
}
