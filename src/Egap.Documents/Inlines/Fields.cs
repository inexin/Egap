using System.Text;

namespace Egap.Documents;

/// <summary>
/// The kind of a field, from the first word of its code. Fields Egap does not know are <see cref="Unknown"/> and keep their
/// stored result (Phase 6 design Q8).
/// </summary>
public enum FieldKind
{
    /// <summary>A field whose code Egap does not know.</summary>
    Unknown,

    /// <summary>PAGE: the page number.</summary>
    Page,

    /// <summary>NUMPAGES: the number of pages.</summary>
    NumPages,

    /// <summary>SECTIONPAGES: the number of pages in the section.</summary>
    SectionPages,

    /// <summary>PAGEREF: the page a bookmark is on.</summary>
    PageRef,

    /// <summary>REF: a bookmark's text.</summary>
    Ref,

    /// <summary>NOTEREF: a footnote's or endnote's number.</summary>
    NoteRef,

    /// <summary>SEQ: a numbered sequence, such as figure numbers.</summary>
    Seq,

    /// <summary>STYLEREF: the text of the nearest paragraph with a style.</summary>
    StyleRef,

    /// <summary>HYPERLINK: a link.</summary>
    Hyperlink,

    /// <summary>IF: a comparison choosing between two texts.</summary>
    If,

    /// <summary>TOC: a table of contents.</summary>
    Toc,

    /// <summary>DATE: today's date.</summary>
    Date,

    /// <summary>TIME: the time now.</summary>
    Time,

    /// <summary>CREATEDATE: when the document was made.</summary>
    CreateDate,

    /// <summary>SAVEDATE: when the document was last saved.</summary>
    SaveDate,

    /// <summary>PRINTDATE: when the document was last printed.</summary>
    PrintDate,

    /// <summary>AUTHOR: the document's author.</summary>
    Author,

    /// <summary>TITLE: the document's title.</summary>
    Title,

    /// <summary>SUBJECT: the document's subject.</summary>
    Subject,

    /// <summary>KEYWORDS: the document's keywords.</summary>
    Keywords,

    /// <summary>COMMENTS: the document's comments property.</summary>
    Comments,

    /// <summary>LASTSAVEDBY: who last saved the document.</summary>
    LastSavedBy,

    /// <summary>FILENAME: the file's name.</summary>
    FileName,

    /// <summary>DOCPROPERTY: a document property by name.</summary>
    DocProperty,

    /// <summary>MERGEFIELD: a mail merge field (kept, not merged).</summary>
    MergeField,

    /// <summary>FORMTEXT, FORMCHECKBOX or FORMDROPDOWN: a legacy form field.</summary>
    FormField,

    /// <summary>SYMBOL: a character from a font.</summary>
    Symbol,

    /// <summary>INCLUDEPICTURE, INCLUDETEXT and other links to outside content (kept, never fetched).</summary>
    Include,

    /// <summary>EQ, the old equation field (kept).</summary>
    Equation,
}

/// <summary>
/// The start of a field (Word's fldChar begin). A field is a start, its code (runs), an optional separator, its result
/// (runs, maybe over several paragraphs) and an end; <see cref="GetField"/> gathers them.
/// </summary>
public sealed class FieldStart : Inline
{
    private bool locked;
    private bool dirty;

    /// <summary>A field start of <paramref name="document"/>, not yet in a paragraph.</summary>
    public FieldStart(Document document)
        : base(document)
    {
    }

    /// <summary>The field is locked: updating it leaves the result as it is.</summary>
    public bool IsLocked
    {
        get => locked;
        set => SetProperty(ref locked, value);
    }

    /// <summary>The result is out of date and should be updated before it is shown.</summary>
    public bool IsDirty
    {
        get => dirty;
        set => SetProperty(ref dirty, value);
    }

    /// <summary>The field this start begins, its parts found in document order; null when its end is missing.</summary>
    public Field? GetField()
    {
        return Field.From(this);
    }

    /// <inheritdoc/>
    public override FieldStart Clone(bool deep = true)
    {
        return (FieldStart)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFieldStart(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new FieldStart(context.Target) { locked = locked, dirty = dirty });
    }
}

/// <summary>The separator between a field's code and its result (Word's fldChar separate).</summary>
public sealed class FieldSeparator : Inline
{
    /// <summary>A field separator of <paramref name="document"/>, not yet in a paragraph.</summary>
    public FieldSeparator(Document document)
        : base(document)
    {
    }

    /// <inheritdoc/>
    public override FieldSeparator Clone(bool deep = true)
    {
        return (FieldSeparator)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFieldSeparator(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new FieldSeparator(context.Target));
    }
}

/// <summary>The end of a field (Word's fldChar end).</summary>
public sealed class FieldEnd : Inline
{
    /// <summary>A field end of <paramref name="document"/>, not yet in a paragraph.</summary>
    public FieldEnd(Document document)
        : base(document)
    {
    }

    /// <inheritdoc/>
    public override FieldEnd Clone(bool deep = true)
    {
        return (FieldEnd)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFieldEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new FieldEnd(context.Target));
    }
}

/// <summary>
/// A field as a whole: its start, separator and end and the nodes between them. The code is the text between the start
/// and the separator (or the end); the result is between the separator and the end. Fields nest, as in Word.
/// </summary>
public sealed class Field
{
    private Field(FieldStart start, FieldSeparator? separator, FieldEnd end, List<Inline> code, List<Node> result)
    {
        Start = start;
        Separator = separator;
        End = end;
        CodeNodes = code;
        ResultNodes = result;
    }

    /// <summary>The field's start.</summary>
    public FieldStart Start { get; }

    /// <summary>The separator, or null for a field with no result.</summary>
    public FieldSeparator? Separator { get; }

    /// <summary>The field's end.</summary>
    public FieldEnd End { get; }

    /// <summary>The inlines of the code, nested fields included.</summary>
    public IReadOnlyList<Inline> CodeNodes { get; }

    /// <summary>The inlines and paragraphs of the result, in document order (paragraph marks count as paragraphs).</summary>
    public IReadOnlyList<Node> ResultNodes { get; }

    /// <summary>The code, such as " PAGE \* MERGEFORMAT ", nested fields' results in place of the fields.</summary>
    public string Code => string.Concat(CodeNodes.OfType<Run>().Select(r => r.Text));

    /// <summary>The kind, from the code's first word.</summary>
    public FieldKind Kind => KindOf(Code);

    /// <summary>
    /// Recomputes this field's result, as <see cref="Document.UpdateFields"/> does for every field: a table of contents is
    /// rebuilt from the headings, for example. Page fields are filled by layout instead.
    /// </summary>
    /// <param name="now">The time DATE and TIME show; the current time when null.</param>
    /// <returns>False when the field is locked, no longer in the document, or of a kind Egap does not compute.</returns>
    public bool Update(DateTimeOffset? now = null)
    {
        return FieldUpdater.UpdateOne(this, now ?? DateTimeOffset.Now);
    }

    /// <summary>The result's text, paragraph breaks as <see cref="ControlChar.ParagraphBreak"/>.</summary>
    public string Result
    {
        get
        {
            StringBuilder text = new();
            foreach (Node node in ResultNodes)
            {
                switch (node)
                {
                    case Run run:
                        text.Append(run.Text);
                        break;
                    case Tab:
                        text.Append(ControlChar.Tab);
                        break;
                    case Break b:
                        text.Append(b.Kind == BreakKind.Page ? ControlChar.PageBreak : b.Kind == BreakKind.Column ? ControlChar.ColumnBreak : ControlChar.LineBreak);
                        break;
                    case Paragraph:
                        text.Append(ControlChar.ParagraphBreak);
                        break;
                }
            }

            return text.ToString();
        }
    }

    /// <summary>The kind a field code names, by its first word (case-insensitive).</summary>
    public static FieldKind KindOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        string word = code.TrimStart().Split([' ', '\t', '\\', '"'], 2)[0].ToUpperInvariant();
        return word switch
        {
            "PAGE" => FieldKind.Page,
            "NUMPAGES" => FieldKind.NumPages,
            "SECTIONPAGES" => FieldKind.SectionPages,
            "PAGEREF" => FieldKind.PageRef,
            "REF" => FieldKind.Ref,
            "NOTEREF" => FieldKind.NoteRef,
            "SEQ" => FieldKind.Seq,
            "STYLEREF" => FieldKind.StyleRef,
            "HYPERLINK" => FieldKind.Hyperlink,
            "IF" => FieldKind.If,
            "TOC" => FieldKind.Toc,
            "DATE" => FieldKind.Date,
            "TIME" => FieldKind.Time,
            "CREATEDATE" => FieldKind.CreateDate,
            "SAVEDATE" => FieldKind.SaveDate,
            "PRINTDATE" => FieldKind.PrintDate,
            "AUTHOR" => FieldKind.Author,
            "TITLE" => FieldKind.Title,
            "SUBJECT" => FieldKind.Subject,
            "KEYWORDS" => FieldKind.Keywords,
            "COMMENTS" => FieldKind.Comments,
            "LASTSAVEDBY" => FieldKind.LastSavedBy,
            "FILENAME" => FieldKind.FileName,
            "DOCPROPERTY" => FieldKind.DocProperty,
            "MERGEFIELD" => FieldKind.MergeField,
            "FORMTEXT" or "FORMCHECKBOX" or "FORMDROPDOWN" => FieldKind.FormField,
            "SYMBOL" => FieldKind.Symbol,
            "INCLUDEPICTURE" or "INCLUDETEXT" or "LINK" or "DDE" or "DDEAUTO" => FieldKind.Include,
            "EQ" => FieldKind.Equation,
            _ => FieldKind.Unknown,
        };
    }

    /// <summary>The field <paramref name="start"/> begins: walks forward in document order, counting nested starts.</summary>
    internal static Field? From(FieldStart start)
    {
        if (start.GetAncestor<Story>() is not { } story)
        {
            return FromSiblings(start);
        }

        int depth = 0;
        bool started = false, inResult = false;
        FieldSeparator? separator = null;
        List<Inline> code = [];
        List<Node> result = [];
        foreach (Node node in story.Descendants())
        {
            if (!started)
            {
                started = node == start;
                continue;
            }

            // Notes, comments and text boxes inside the story have their own fields.
            if (node.OwnerStory != story)
            {
                continue;
            }

            switch (node)
            {
                case FieldStart:
                    depth++;
                    break;
                case FieldSeparator s when depth == 0 && separator is null:
                    separator = s;
                    inResult = true;
                    continue;
                case FieldEnd e when depth == 0:
                    return new Field(start, separator, e, code, result);
                case FieldEnd:
                    depth--;
                    break;
            }

            if (inResult)
            {
                // A paragraph met inside the result means the previous one ended: count its mark.
                if (node is not Paragraph || result.Count > 0)
                {
                    result.Add(node);
                }
            }
            else if (node is Inline inline)
            {
                code.Add(inline);
            }
        }

        return null;
    }

    /// <summary>A field in a paragraph not yet in a story: its parts must be in that paragraph.</summary>
    private static Field? FromSiblings(FieldStart start)
    {
        if (start.Parent is null)
        {
            return null;
        }

        int depth = 0;
        FieldSeparator? separator = null;
        List<Inline> code = [];
        List<Node> result = [];
        for (Node? node = start.NextSibling; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case FieldStart:
                    depth++;
                    break;
                case FieldSeparator s when depth == 0 && separator is null:
                    separator = s;
                    continue;
                case FieldEnd e when depth == 0:
                    return new Field(start, separator, e, code, result);
                case FieldEnd:
                    depth--;
                    break;
            }

            if (separator is null)
            {
                code.Add((Inline)node);
            }
            else
            {
                result.Add(node);
            }
        }

        return null;
    }
}
