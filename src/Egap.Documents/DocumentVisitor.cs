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

    /// <summary>Entering a story: a section's body, a header or footer, or the text of a note, comment or text box.</summary>
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

    /// <summary>A field's start.</summary>
    public virtual VisitorAction VisitFieldStart(FieldStart fieldStart)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A field's separator.</summary>
    public virtual VisitorAction VisitFieldSeparator(FieldSeparator separator)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A field's end.</summary>
    public virtual VisitorAction VisitFieldEnd(FieldEnd fieldEnd)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A bookmark's start.</summary>
    public virtual VisitorAction VisitBookmarkStart(BookmarkStart bookmarkStart)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A bookmark's end.</summary>
    public virtual VisitorAction VisitBookmarkEnd(BookmarkEnd bookmarkEnd)
    {
        return VisitorAction.Continue;
    }

    /// <summary>The start of a comment's range.</summary>
    public virtual VisitorAction VisitCommentRangeStart(CommentRangeStart rangeStart)
    {
        return VisitorAction.Continue;
    }

    /// <summary>The end of a comment's range.</summary>
    public virtual VisitorAction VisitCommentRangeEnd(CommentRangeEnd rangeEnd)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a hyperlink.</summary>
    public virtual VisitorAction VisitHyperlinkStart(Hyperlink hyperlink)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a hyperlink.</summary>
    public virtual VisitorAction VisitHyperlinkEnd(Hyperlink hyperlink)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering an inline content control.</summary>
    public virtual VisitorAction VisitInlineContentControlStart(InlineContentControl control)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving an inline content control.</summary>
    public virtual VisitorAction VisitInlineContentControlEnd(InlineContentControl control)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a block content control.</summary>
    public virtual VisitorAction VisitContentControlStart(ContentControl control)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a block content control.</summary>
    public virtual VisitorAction VisitContentControlEnd(ContentControl control)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a footnote or endnote; its story follows.</summary>
    public virtual VisitorAction VisitFootnoteStart(Footnote note)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a footnote or endnote.</summary>
    public virtual VisitorAction VisitFootnoteEnd(Footnote note)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a comment; its story follows.</summary>
    public virtual VisitorAction VisitCommentStart(Comment comment)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a comment.</summary>
    public virtual VisitorAction VisitCommentEnd(Comment comment)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A picture.</summary>
    public virtual VisitorAction VisitPicture(Picture picture)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Entering a shape; its text box story follows, if any.</summary>
    public virtual VisitorAction VisitShapeStart(Shape shape)
    {
        return VisitorAction.Continue;
    }

    /// <summary>Leaving a shape.</summary>
    public virtual VisitorAction VisitShapeEnd(Shape shape)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A kept island.</summary>
    public virtual VisitorAction VisitIsland(Island island)
    {
        return VisitorAction.Continue;
    }

    /// <summary>A kept block island.</summary>
    public virtual VisitorAction VisitBlockIsland(BlockIsland island)
    {
        return VisitorAction.Continue;
    }
}
