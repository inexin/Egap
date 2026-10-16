using System.Collections;

namespace Egap.Documents;

/// <summary>What a node needs from the collection it is in, whatever the collection's item type.</summary>
internal interface INodeCollection
{
    int Count { get; }

    Node GetNode(int index);

    void RemoveAt(int index);
}

/// <summary>
/// The children of one kind a node holds, such as a story's blocks or a paragraph's inlines. The item type keeps wrong
/// children out at compile time (a run cannot go into a story); adding also refuses a node of another document, a node
/// already in the tree and a node that holds the parent.
/// </summary>
/// <typeparam name="T">The kind of node the collection holds.</typeparam>
public class NodeCollection<T> : IReadOnlyList<T>, INodeCollection
    where T : Node
{
    private readonly List<T> items = [];

    private protected NodeCollection(Node owner)
    {
        Owner = owner;
    }

    /// <summary>How many nodes the collection holds.</summary>
    public int Count => items.Count;

    /// <summary>The node that holds this collection.</summary>
    private protected Node Owner { get; }

    /// <summary>The node at <paramref name="index"/>.</summary>
    public T this[int index] => items[index];

    /// <summary>Adds a node at the end.</summary>
    /// <returns>The node, for chaining.</returns>
    public TNode Add<TNode>(TNode node)
        where TNode : T
    {
        Insert(items.Count, node);
        return node;
    }

    /// <summary>Puts a node at <paramref name="index"/>, moving the ones from there on one place later.</summary>
    /// <exception cref="ArgumentException">The node belongs to another document, is already in the tree, or holds the parent.</exception>
    public void Insert(int index, T node)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, items.Count);
        Node.CheckInsert(Owner, node);
        Validate(node);
        items.Insert(index, node);
        node.Attach(Owner, this, index);
        Renumber(index + 1);
        Owner.Document.NotifyChanged(node, DocumentChangeKind.NodeInserted, Owner, index);
    }

    /// <summary>
    /// Adds a node the library has just made or copied, which cannot break the tree's rules, at the end: no walk up the tree
    /// to look for a cycle, which made building or copying deeply nested tables take time squared in the depth.
    /// </summary>
    internal TNode AddTrusted<TNode>(TNode node)
        where TNode : T
    {
        Validate(node);
        items.Add(node);
        node.Attach(Owner, this, items.Count - 1);
        Owner.Document.NotifyChanged(node, DocumentChangeKind.NodeInserted, Owner, items.Count - 1);
        return node;
    }

    /// <summary>Takes the node at <paramref name="index"/> out of the collection and the tree.</summary>
    public void RemoveAt(int index)
    {
        T node = items[index];
        items.RemoveAt(index);
        node.Detach();
        Renumber(index);
        Owner.Document.NotifyChanged(node, DocumentChangeKind.NodeRemoved, Owner, index);
    }

    /// <summary>Takes <paramref name="node"/> out of the collection and the tree.</summary>
    /// <returns>False when the node was not in this collection.</returns>
    public bool Remove(T node)
    {
        if (node is null || node.Collection != this)
        {
            return false;
        }

        RemoveAt(node.IndexInParent);
        return true;
    }

    /// <summary>Takes every node out of the collection and the tree.</summary>
    public void Clear()
    {
        // From the end, so each notification's index is where the node was at that moment, as with RemoveAt.
        while (items.Count > 0)
        {
            RemoveAt(items.Count - 1);
        }
    }

    /// <summary>The index of <paramref name="node"/> in this collection, or -1.</summary>
    public int IndexOf(T node)
    {
        return node is not null && node.Collection == this ? node.IndexInParent : -1;
    }

    /// <summary>Whether <paramref name="node"/> is in this collection.</summary>
    public bool Contains(T node)
    {
        return IndexOf(node) >= 0;
    }

    /// <summary>The nodes in order; changing the collection while enumerating throws.</summary>
    public List<T>.Enumerator GetEnumerator()
    {
        return items.GetEnumerator();
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    Node INodeCollection.GetNode(int index)
    {
        return items[index];
    }

    /// <summary>Checks a rule of this collection beyond the item type, such as one header of each kind per section.</summary>
    private protected virtual void Validate(T node)
    {
    }

    private void Renumber(int from)
    {
        for (int i = from; i < items.Count; i++)
        {
            items[i].IndexInParent = i;
        }
    }
}

/// <summary>A <see cref="NodeCollection{T}"/> a node makes for itself.</summary>
internal sealed class ChildCollection<T>(Node owner) : NodeCollection<T>(owner)
    where T : Node;
