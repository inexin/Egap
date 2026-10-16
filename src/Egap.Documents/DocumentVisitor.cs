namespace Egap.Documents;

/// <summary>What a <see cref="DocumentVisitor"/> wants the walk to do next.</summary>
public enum VisitorAction
{
    /// <summary>Go on, into this node's children.</summary>
    Continue,

    /// <summary>Leave this node's children out; its end method is still called. On an end method, the same as Continue.</summary>
    SkipChildren,

    /// <summary>End the walk now; <see cref="Node.Accept"/> returns false.</summary>
    Stop,
}

/// <summary>
/// Walks a document tree through <see cref="Node.Accept"/>: a start and an end method for each node that holds others, one
/// method for each inline. Override the ones you need; the rest continue. The walk does not recurse, so deeply nested tables
/// cannot overflow the stack.
/// </summary>
public abstract class DocumentVisitor
{
    /// <summary>Entering the document.</summary>
    public virtual VisitorAction VisitDocumentStart(Document document)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving the document.</summary>
    public virtual VisitorAction VisitDocumentEnd(Document document)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a section; its body comes first, then its headers and footers.</summary>
    public virtual VisitorAction VisitSectionStart(Section section)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a section.</summary>
    public virtual VisitorAction VisitSectionEnd(Section section)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a story: a section's body, or a header or footer.</summary>
    public virtual VisitorAction VisitStoryStart(Story story)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a story.</summary>
    public virtual VisitorAction VisitStoryEnd(Story story)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a paragraph.</summary>
    public virtual VisitorAction VisitParagraphStart(Paragraph paragraph)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a paragraph.</summary>
    public virtual VisitorAction VisitParagraphEnd(Paragraph paragraph)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a table.</summary>
    public virtual VisitorAction VisitTableStart(Table table)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a table.</summary>
    public virtual VisitorAction VisitTableEnd(Table table)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a table row.</summary>
    public virtual VisitorAction VisitRowStart(Row row)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a table row.</summary>
    public virtual VisitorAction VisitRowEnd(Row row)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a table cell.</summary>
    public virtual VisitorAction VisitCellStart(Cell cell)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a table cell.</summary>
    public virtual VisitorAction VisitCellEnd(Cell cell)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A run of text.</summary>
    public virtual VisitorAction VisitRun(Run run)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A tab.</summary>
    public virtual VisitorAction VisitTab(Tab tab)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A line, page or column break.</summary>
    public virtual VisitorAction VisitBreak(Break breakNode)
    {
        return VisitorAction.Continue;
    }
}
