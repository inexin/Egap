using System.Text;

namespace Egap.Documents.Markdown;

/// <summary>
/// The Markdown syntax tree the parser builds (CommonMark 0.31 plus GitHub's tables, task lists, strikethrough, autolinks
/// and footnotes) before it is mapped onto the document model. Internal: the model is the public result.
/// </summary>
internal abstract class MdNode
{
    public MdNode? Parent { get; set; }

    public List<MdNode> Children { get; } = [];

    public MdNode? LastChild => Children.Count > 0 ? Children[^1] : null;

    public void Append(MdNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertAfter(MdNode existing, MdNode child)
    {
        child.Parent = this;
        Children.Insert(Children.IndexOf(existing) + 1, child);
    }

    public void Remove()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    // While inlines are parsed they sit in a linked list, so emphasis and links move ranges of them in constant time;
    // Materialize turns the lists into Children when the paragraph is done.
    internal MdNode? LinkPrevious { get; set; }

    internal MdNode? LinkNext { get; set; }

    internal MdNode? LinkFirst { get; set; }

    internal MdNode? LinkLast { get; set; }

    internal void LinkAppend(MdNode child)
    {
        child.Parent = this;
        child.LinkPrevious = LinkLast;
        child.LinkNext = null;
        if (LinkLast is null)
        {
            LinkFirst = child;
        }
        else
        {
            LinkLast.LinkNext = child;
        }

        LinkLast = child;
    }

    internal void LinkUnlink()
    {
        MdNode? parent = Parent;
        if (LinkPrevious is null)
        {
            parent?.LinkFirst = LinkNext;
        }
        else
        {
            LinkPrevious.LinkNext = LinkNext;
        }

        if (LinkNext is null)
        {
            parent?.LinkLast = LinkPrevious;
        }
        else
        {
            LinkNext.LinkPrevious = LinkPrevious;
        }

        LinkPrevious = null;
        LinkNext = null;
        Parent = null;
    }

    /// <summary>Moves the nodes after <paramref name="after"/> and before <paramref name="before"/> (null: to the end) into <paramref name="target"/>.</summary>
    internal static void LinkMoveRange(MdNode after, MdNode? before, MdNode target)
    {
        MdNode parent = after.Parent!;
        MdNode? first = after.LinkNext;
        if (first is null || first == before)
        {
            return;
        }

        MdNode last = before is null ? parent.LinkLast! : before.LinkPrevious!;
        after.LinkNext = before;
        if (before is null)
        {
            parent.LinkLast = after;
        }
        else
        {
            before.LinkPrevious = after;
        }

        first.LinkPrevious = target.LinkLast;
        if (target.LinkLast is null)
        {
            target.LinkFirst = first;
        }
        else
        {
            target.LinkLast.LinkNext = first;
        }

        target.LinkLast = last;
        last.LinkNext = null;
        for (MdNode? n = first; n is not null; n = n.LinkNext)
        {
            n.Parent = target;
        }
    }

    /// <summary>Puts <paramref name="node"/> right after this one in the parent's linked list.</summary>
    internal void LinkInsertAfter(MdNode node)
    {
        MdNode parent = Parent!;
        node.Parent = parent;
        node.LinkPrevious = this;
        node.LinkNext = LinkNext;
        if (LinkNext is null)
        {
            parent.LinkLast = node;
        }
        else
        {
            LinkNext.LinkPrevious = node;
        }

        LinkNext = node;
    }

    /// <summary>Turns the linked lists under this node into <see cref="Children"/>, without recursion.</summary>
    internal void Materialize()
    {
        Stack<MdNode> pending = new();
        pending.Push(this);
        while (pending.Count > 0)
        {
            MdNode node = pending.Pop();
            if (node.LinkFirst is null)
            {
                continue;
            }

            node.Children.Clear();
            for (MdNode? n = node.LinkFirst; n is not null;)
            {
                MdNode? next = n.LinkNext;
                n.LinkPrevious = null;
                n.LinkNext = null;
                node.Children.Add(n);
                pending.Push(n);
                n = next;
            }

            node.LinkFirst = null;
            node.LinkLast = null;
        }
    }
}

/// <summary>A block: open while lines may still be added to it.</summary>
internal abstract class MdBlock : MdNode
{
    public bool Open { get; set; } = true;

    public bool LastLineBlank { get; set; }

    /// <summary>Raw lines for paragraphs, headings, code and HTML blocks.</summary>
    public StringBuilder Content { get; } = new();

    public virtual bool AcceptsLines => false;

    public virtual bool CanContain(MdBlock block)
    {
        return false;
    }
}

internal sealed class MdDocument : MdBlock
{
    public Dictionary<string, (string Destination, string? Title)> References { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MdFootnoteDefinition> Footnotes { get; } = new(StringComparer.Ordinal);

    public string? FrontMatter { get; set; }

    public override bool CanContain(MdBlock block)
    {
        return block is not MdListItem;
    }
}

internal sealed class MdBlockQuote : MdBlock
{
    public override bool CanContain(MdBlock block)
    {
        return block is not MdListItem;
    }
}

internal sealed class MdList : MdBlock
{
    public bool Ordered { get; set; }

    public int Start { get; set; } = 1;

    public char BulletChar { get; set; }

    public char Delimiter { get; set; }

    public bool Tight { get; set; } = true;

    public override bool CanContain(MdBlock block)
    {
        return block is MdListItem;
    }
}

internal sealed class MdListItem : MdBlock
{
    public bool Ordered { get; set; }

    public int Start { get; set; }

    public char BulletChar { get; set; }

    public char Delimiter { get; set; }

    public int MarkerOffset { get; set; }

    public int Padding { get; set; }

    /// <summary>GitHub task list item: null when it is not one, otherwise whether it is checked.</summary>
    public bool? TaskChecked { get; set; }

    public override bool CanContain(MdBlock block)
    {
        return block is not MdListItem;
    }
}

internal sealed class MdFootnoteDefinition : MdBlock
{
    public string Label { get; set; } = "";

    /// <summary>The order of first reference, 1-based, or 0 when not referenced.</summary>
    public int Number { get; set; }

    public int ReferenceCount { get; set; }

    public override bool CanContain(MdBlock block)
    {
        return block is not MdListItem;
    }
}

internal sealed class MdParagraph : MdBlock
{
    public override bool AcceptsLines => true;
}

internal sealed class MdHeading : MdBlock
{
    public int Level { get; set; }

    public bool Setext { get; set; }
}

internal sealed class MdThematicBreak : MdBlock;

internal sealed class MdCodeBlock : MdBlock
{
    public bool Fenced { get; set; }

    public char FenceChar { get; set; }

    public int FenceLength { get; set; }

    public int FenceOffset { get; set; }

    public string Info { get; set; } = "";

    public string Literal { get; set; } = "";

    public override bool AcceptsLines => true;
}

internal sealed class MdHtmlBlock : MdBlock
{
    public int HtmlType { get; set; }

    public string Literal { get; set; } = "";

    public override bool AcceptsLines => true;
}

/// <summary>Column alignment in a GitHub table.</summary>
internal enum MdAlign
{
    None,
    Left,
    Center,
    Right,
}

internal sealed class MdTable : MdBlock
{
    public List<MdAlign> Alignments { get; } = [];

    public override bool AcceptsLines => true;
}

internal sealed class MdTableRow : MdBlock
{
    public bool Header { get; set; }
}

internal sealed class MdTableCell : MdBlock
{
    public MdAlign Align { get; set; }
}

internal sealed class MdText(string literal) : MdNode
{
    public string Literal { get; set; } = literal;
}

internal sealed class MdSoftBreak : MdNode;

internal sealed class MdHardBreak : MdNode;

internal sealed class MdCode(string literal) : MdNode
{
    public string Literal { get; } = literal;
}

internal sealed class MdEmphasis : MdNode;

internal sealed class MdStrong : MdNode;

internal sealed class MdStrikethrough : MdNode;

internal sealed class MdLink(string destination, string? title) : MdNode
{
    public string Destination { get; } = destination;

    public string? Title { get; } = title;

    /// <summary>An autolink in angle brackets or a GitHub extended autolink.</summary>
    public bool Auto { get; init; }
}

internal sealed class MdImage(string destination, string? title) : MdNode
{
    public string Destination { get; } = destination;

    public string? Title { get; } = title;
}

internal sealed class MdHtmlInline(string literal) : MdNode
{
    public string Literal { get; } = literal;
}

internal sealed class MdFootnoteReference(MdFootnoteDefinition definition) : MdNode
{
    public MdFootnoteDefinition Definition { get; } = definition;

    /// <summary>Which reference to the definition this is, 1-based.</summary>
    public int Ordinal { get; init; }
}
