using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Paragraph content (ODF 1.3 Part 3, 6): text with its white space, spans, links, bookmarks, notes, annotations, fields and
/// tracked changes (5.5).
/// </summary>
internal sealed partial class OdtWriter
{
    private readonly List<XElement> changedRegions = [];
    private readonly HashSet<int> annotationsWritten = [];
    private readonly Dictionary<int, string> editableNames = [];
    private Dictionary<Footnote, string>? noteNames;
    private HashSet<string>? noteNameSet;
    private int changeCount;
    private int noteCount;
    private bool allowChanges = true;
    private StoryState story = new();

    /// <summary>What a story being written carries from paragraph to paragraph.</summary>
    private sealed class StoryState
    {
        /// <summary>The insertion open now, and its change id.</summary>
        public (Revision Revision, string Id)? Insertion { get; set; }

        /// <summary>Where the open insertion ends when it ends.</summary>
        public XElement? LastTarget { get; set; }

        /// <summary>A field's code is skipped up to this node.</summary>
        public Inline? SkipUntil { get; set; }

        /// <summary>The table of contents being written, whose field the index stands for.</summary>
        public FieldStart? TocField { get; set; }

        public bool InNote { get; set; }
    }

    /// <summary>Writes one ODF paragraph: where its content goes, the white space so far, and the deletion open now.</summary>
    private sealed class ParagraphWriter(OdtWriter owner, XElement element, Func<string?, bool, XElement> make)
    {
        private readonly List<XElement> wrappers = [];
        private readonly List<FieldEnd?> wrapperEnds = [];
        private (Revision Revision, XElement Change, XElement Paragraph)? deletion;
        private XElement? spaceOwner;
        private bool lastSpace;
        private XElement? lastSpan;
        private string? lastSpanStyle;

        public XElement Element { get; private set; } = element;

        public Paragraph? Paragraph { get; set; }

        /// <summary>A break written as the paragraph's break after, not in its text.</summary>
        public Inline? Skip { get; init; }

        /// <summary>Where content goes outside a deletion.</summary>
        public XElement Live => wrappers.Count > 0 ? wrappers[^1] : Element;

        /// <summary>Where content goes now.</summary>
        public XElement Target => deletion?.Paragraph ?? Live;

        public bool InDeletion => deletion is not null;

        public bool InLink => wrappers.Any(w => w.Name == TextNs + "a");

        public void Push(XElement wrapper, FieldEnd? end)
        {
            Live.Add(wrapper);
            wrappers.Add(wrapper);
            wrapperEnds.Add(end);
        }

        public int Depth => wrappers.Count;

        public void PopTo(int depth)
        {
            wrappers.RemoveRange(depth, wrappers.Count - depth);
            wrapperEnds.RemoveRange(depth, wrapperEnds.Count - depth);
        }

        /// <summary>Closes the wrapper a field's end closes, if one is open.</summary>
        public void EndField(FieldEnd end)
        {
            int index = wrapperEnds.LastIndexOf(end);
            if (index >= 0)
            {
                PopTo(index);
            }
        }

        /// <summary>Adds an element; <paramref name="content"/> when it shows something, so that a space after it is kept.</summary>
        public void Add(XNode node, bool content = false)
        {
            Target.Add(node);
            lastSpan = null;
            if (content)
            {
                lastSpace = false;
                spaceOwner = deletion?.Paragraph ?? Element;
            }
        }

        /// <summary>Text, in a span of <paramref name="style"/> when it has one, with ODF's white space (ODF 1.3 Part 3, 6.1.2).</summary>
        public void Text(string text, string? style)
        {
            text = Clean(text);
            if (text.Length == 0)
            {
                return;
            }

            XElement target = Target;
            if (spaceOwner != (deletion?.Paragraph ?? Element))
            {
                spaceOwner = deletion?.Paragraph ?? Element;
                lastSpace = true;
            }

            XElement container = target;
            if (style is not null)
            {
                if (lastSpan is not null && lastSpanStyle == style && target.LastNode == lastSpan)
                {
                    container = lastSpan;
                }
                else
                {
                    container = new XElement(TextNs + "span", new XAttribute(TextNs + "style-name", style));
                    target.Add(container);
                }
            }

            StringBuilder literal = new();
            int spaces = 0;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    if (lastSpace)
                    {
                        spaces++;
                    }
                    else
                    {
                        literal.Append(' ');
                        lastSpace = true;
                    }

                    continue;
                }

                Flush();
                switch (c)
                {
                    case '\t':
                        FlushLiteral();
                        container.Add(new XElement(TextNs + "tab"));
                        lastSpace = true;
                        break;
                    case '\n' or '\r' or '\v':
                        FlushLiteral();
                        container.Add(new XElement(TextNs + "line-break"));
                        lastSpace = true;
                        break;
                    default:
                        literal.Append(c);
                        lastSpace = false;
                        break;
                }
            }

            Flush();
            FlushLiteral();
            if (style is not null)
            {
                lastSpan = container;
                lastSpanStyle = style;
            }
            else
            {
                lastSpan = null;
            }

            void Flush()
            {
                if (spaces > 0)
                {
                    FlushLiteral();
                    XElement s = new(TextNs + "s");
                    if (spaces > 1)
                    {
                        s.Add(new XAttribute(TextNs + "c", Int(spaces)));
                    }

                    container.Add(s);
                    spaces = 0;
                }
            }

            void FlushLiteral()
            {
                if (literal.Length > 0)
                {
                    container.Add(new XText(literal.ToString()));
                    literal.Clear();
                }
            }
        }

        public void Tab(string? style) => Text("\t", style);

        public void LineBreak() => Text("\n", null);

        /// <summary>A page or column break: the paragraph ends here and the rest starts a new one with the break before it.</summary>
        public void Break(string kind)
        {
            if (deletion is not null)
            {
                return;
            }

            if (wrappers.Count == 0 && Element.Parent is not null && Element.Nodes().All(IsMarker))
            {
                // A break at the start, after bookmarks at most: the paragraph itself starts with it.
                XElement replacement = make(kind, true);
                replacement.Remove();
                replacement.Add(Element.Nodes());
                Element.ReplaceWith(replacement);
                Element = replacement;
                return;
            }

            List<XElement> open = [.. wrappers];
            XElement next = make(kind, false);
            Element = next;
            spaceOwner = null;
            lastSpan = null;
            wrappers.Clear();
            XElement into = next;
            foreach (XElement wrapper in open)
            {
                XElement copy = new(wrapper.Name, wrapper.Attributes());
                into.Add(copy);
                wrappers.Add(copy);
                into = copy;
            }

            if (owner.story.Insertion is not null)
            {
                owner.story.LastTarget = Live;
            }
        }

        private static bool IsMarker(XNode node)
        {
            return node is XElement e && e.Name.Namespace == TextNs && e.Name.LocalName is "bookmark-start" or "bookmark-end" or "bookmark" or "change-start" or "change-end";
        }

        public void EnterDeletion(Revision revision)
        {
            if (deletion?.Revision == revision)
            {
                return;
            }

            deletion = null;
            XElement change = owner.NewRegion(revision);
            XElement paragraph = new(TextNs + "p");
            change.Add(paragraph);
            Live.Add(new XElement(TextNs + "change", new XAttribute(TextNs + "change-id", change.Parent!.Attribute(TextNs + "id")!.Value)));
            deletion = (revision, change, paragraph);
            lastSpan = null;
        }

        public void LeaveDeletion()
        {
            if (deletion is not null)
            {
                deletion = null;
                lastSpan = null;
            }
        }

        /// <summary>A deleted paragraph mark: the deletion holds the paragraph's end (ODF 1.3 Part 3, 5.5.4).</summary>
        public void DeleteMark(Revision revision)
        {
            EnterDeletion(revision);
            XElement paragraph = new(TextNs + "p");
            deletion!.Value.Change.Add(paragraph);
            deletion = (revision, deletion.Value.Change, paragraph);
        }

        public void EndParagraph(Revision? mark)
        {
            LeaveDeletion();
            PopTo(0);
            if (Element.DescendantNodes().LastOrDefault() is XText { Value: [.., ' '] } last && last.Parent?.Name != TextNs + "s")
            {
                // A space at the end of a paragraph is white space ODF drops; text:s keeps it.
                last.Value = last.Value[..^1];
                last.AddAfterSelf(new XElement(TextNs + "s"));
                if (last.Value.Length == 0)
                {
                    last.Remove();
                }
            }

            if (owner.allowChanges && mark is { Removes: false })
            {
                owner.OpenInsertion(mark, Element);
            }
            else
            {
                owner.story.LastTarget = Element;
                owner.CloseInsertion();
            }
        }
    }

    /// <summary>A changed region for a revision, listed in <c>text:tracked-changes</c>; returns its insertion or deletion element.</summary>
    private XElement NewRegion(Revision revision)
    {
        string id = "ct" + Int(++changeCount);
        XElement change = new(TextNs + (revision.Removes ? "deletion" : "insertion"),
            new XElement(OfficeNs + "change-info", new XElement(DcNs + "creator", Clean(revision.Author)),
                new XElement(DcNs + "date", Date(revision.Date) ?? "1970-01-01T00:00:00")));
        XElement region = new(TextNs + "changed-region", new XAttribute(TextNs + "id", id), change);
        if (Odf13)
        {
            region.Add(new XAttribute(XmlNs + "id", id));
        }

        changedRegions.Add(region);
        return change;
    }

    private void OpenInsertion(Revision revision, XElement target)
    {
        if (story.Insertion?.Revision == revision)
        {
            story.LastTarget = target;
            return;
        }

        CloseInsertion();
        XElement change = NewRegion(revision);
        string id = change.Parent!.Attribute(TextNs + "id")!.Value;
        target.Add(new XElement(TextNs + "change-start", new XAttribute(TextNs + "change-id", id)));
        story.Insertion = (revision, id);
        story.LastTarget = target;
    }

    private void CloseInsertion()
    {
        if (story.Insertion is { } insertion && story.LastTarget is { } target)
        {
            target.Add(new XElement(TextNs + "change-end", new XAttribute(TextNs + "change-id", insertion.Id)));
        }

        story.Insertion = null;
    }

    private void WriteInlines(Paragraph paragraph, ParagraphWriter writer)
    {
        List<(int Index, XElement Element)>? kept = options.KeepUnknownMarkup ? paragraph.Extensions.Get<OdtExtension>(OdtExtension.Format)?.Inline : null;
        int k = 0;
        for (int i = 0; i < paragraph.Inlines.Count; i++)
        {
            while (kept is not null && k < kept.Count && kept[k].Index <= i)
            {
                writer.Add(Keep(kept[k++].Element));
            }

            WriteInline(paragraph.Inlines[i], writer, null);
        }

        while (kept is not null && k < kept.Count)
        {
            writer.Add(Keep(kept[k++].Element));
        }
    }

    private void WriteInline(Inline inline, ParagraphWriter writer, Revision? inherited)
    {
        if (story.SkipUntil is { } skip)
        {
            if (inline == skip)
            {
                story.SkipUntil = null;
                if (inline is FieldEnd end)
                {
                    writer.EndField(end);
                }

                return;
            }

            if (inline is CompositeInline composite && skip.Ancestors().Contains(composite))
            {
                foreach (Inline child in composite.Inlines)
                {
                    WriteInline(child, writer, inherited);
                }
            }

            return;
        }

        Revision? revision = inline.Revision ?? inherited;
        if (inline is not CompositeInline && !Track(revision, writer))
        {
            return;
        }

        switch (inline)
        {
            case Run run:
                writer.Text(run.Text, RunStyle(run));
                break;
            case Tab:
                writer.Tab(null);
                break;
            case Break { Kind: BreakKind.Line }:
                writer.LineBreak();
                break;
            case Break brk when brk == writer.Skip:
                break;
            case Break brk:
                writer.Break(brk.Kind == BreakKind.Page ? "page" : "column");
                break;
            case Hyperlink link:
                WriteLink(link, writer, revision);
                break;
            case InlineContentControl control:
                warnings.Add(ConversionWarningKind.ContentControlsDropped, "OpenDocument has no content controls; their content was kept.");
                foreach (Inline child in control.Inlines)
                {
                    WriteInline(child, writer, revision);
                }

                break;
            case CompositeInline other:
                foreach (Inline child in other.Inlines)
                {
                    WriteInline(child, writer, revision);
                }

                break;
            case BookmarkStart or BookmarkEnd when NoteNames().Count > 0 && noteNameSet!.Contains(inline is BookmarkStart b ? b.Name : ((BookmarkEnd)inline).Name)
                && IsNoteBracket(inline):
                break;
            case BookmarkStart start:
                writer.Add(new XElement(TextNs + "bookmark-start", new XAttribute(TextNs + "name", Clean(start.Name))));
                break;
            case BookmarkEnd end:
                writer.Add(new XElement(TextNs + "bookmark-end", new XAttribute(TextNs + "name", Clean(end.Name))));
                break;
            case EditableRangeStart range:
                {
                    // As LibreOffice writes Word's editable ranges: bookmarks named for who may edit.
                    string name = range.Editor is { Length: > 0 } editor
                        ? "permission-for-user:" + Int(range.Id) + ":" + Clean(editor)
                        : "permission-for-group:" + Int(range.Id) + ":" + (range.Group == EditorGroup.None ? "everyone" : range.Group.ToString().ToLowerInvariant());
                    editableNames[range.Id] = name;
                    writer.Add(new XElement(TextNs + "bookmark-start", new XAttribute(TextNs + "name", name)));
                    break;
                }

            case EditableRangeEnd rangeEnd:
                if (editableNames.TryGetValue(rangeEnd.Id, out string? rangeName))
                {
                    writer.Add(new XElement(TextNs + "bookmark-end", new XAttribute(TextNs + "name", rangeName)));
                }

                break;
            case CommentRangeStart commentStart:
                if (commentStart.Comment is { } started && annotationsWritten.Add(started.Id))
                {
                    writer.Add(Annotation(started, named: true));
                }

                break;
            case CommentRangeEnd commentEnd:
                if (annotationsWritten.Contains(commentEnd.CommentId) && commentEnd.Comment is not null)
                {
                    writer.Add(new XElement(OfficeNs + "annotation-end", new XAttribute(OfficeNs + "name", AnnotationName(commentEnd.CommentId))));
                }

                break;
            case Comment comment:
                if (annotationsWritten.Add(comment.Id))
                {
                    writer.Add(Annotation(comment, named: false));
                }

                break;
            case Footnote note:
                WriteNote(note, writer);
                break;
            case FieldStart fieldStart:
                WriteField(fieldStart, writer);
                break;
            case FieldEnd fieldEnd:
                writer.EndField(fieldEnd);
                break;
            case FieldSeparator:
                break;
            case Picture or Shape or Island:
                WriteDrawing(inline, writer);
                break;
        }
    }

    /// <summary>Opens or closes tracked changes for an inline with <paramref name="revision"/>; false when it is left out.</summary>
    private bool Track(Revision? revision, ParagraphWriter writer)
    {
        if (!allowChanges)
        {
            if (revision is not null)
            {
                warnings.Add(ConversionWarningKind.ChangesAccepted, "Tracked changes in headers and footers were accepted: OpenDocument tracks changes in the body only.");
            }

            return revision is not { Removes: true };
        }

        if (revision is { Removes: true })
        {
            writer.EnterDeletion(revision);
            return true;
        }

        writer.LeaveDeletion();
        if (revision is not null)
        {
            OpenInsertion(revision, writer.Live);
        }
        else
        {
            story.LastTarget = writer.Live;
            CloseInsertion();
        }

        story.LastTarget = writer.Live;
        return true;
    }

    /// <summary>The text style a run is written in: its own formatting over its character style; null for none.</summary>
    private string? RunStyle(Run run)
    {
        string? parent = StyleName(run.Style);
        XElement? props = run.Format.IsEmpty ? null : TextProperties(run.Format, null);
        return props is null ? parent : Auto("text", parent, null, props);
    }

    private void WriteLink(Hyperlink link, ParagraphWriter writer, Revision? revision)
    {
        string? href = link.Address is { Length: > 0 } address
            ? address + (link.Anchor is { Length: > 0 } a ? "#" + a : "")
            : link.Anchor is { Length: > 0 } anchor ? "#" + anchor : null;
        if (href is null || writer.InLink || writer.InDeletion)
        {
            foreach (Inline child in link.Inlines)
            {
                WriteInline(child, writer, revision);
            }

            return;
        }

        XElement element = new(TextNs + "a", new XAttribute(XLinkNs + "type", "simple"), new XAttribute(XLinkNs + "href", Clean(href)));
        if (link.Tooltip is { Length: > 0 } tooltip)
        {
            element.Add(new XAttribute(OfficeNs + "title", Clean(tooltip)));
        }

        if (link.Target is { Length: > 0 } frame)
        {
            element.Add(new XAttribute(OfficeNs + "target-frame-name", Clean(frame)));
        }

        int depth = writer.Depth;
        writer.Push(element, null);
        foreach (Inline child in link.Inlines)
        {
            WriteInline(child, writer, revision);
        }

        writer.PopTo(depth);
    }

    private static string AnnotationName(int id) => "__Annotation__" + Int(id);

    /// <summary>A comment as <c>office:annotation</c> (ODF 1.3 Part 3, 14.1), named when a range end refers to it.</summary>
    private XElement Annotation(Comment comment, bool named)
    {
        XElement annotation = new(OfficeNs + "annotation");
        if (named)
        {
            annotation.Add(new XAttribute(OfficeNs + "name", AnnotationName(comment.Id)));
        }

        if (comment.Done)
        {
            annotation.Add(new XAttribute(LoExtNs + "resolved", "true"));
        }

        annotation.Add(new XElement(DcNs + "creator", Clean(comment.Author)));
        if (Date(comment.Date) is { } date)
        {
            annotation.Add(new XElement(DcNs + "date", date));
        }

        if (comment.Initials is { Length: > 0 } initials)
        {
            annotation.Add(Odf13 ? new XElement(MetaNs + "creator-initials", Clean(initials)) : new XElement(LoExtNs + "sender-initials", Clean(initials)));
        }

        bool saved = allowChanges;
        WriteStory(comment.Story.Blocks, annotation);
        allowChanges = saved;
        return annotation;
    }

    /// <summary>A footnote or endnote (ODF 1.3 Part 3, 6.3), with the name of a bookmark around it as its id for references to it.</summary>
    private void WriteNote(Footnote note, ParagraphWriter writer)
    {
        if (story.InNote)
        {
            writer.Text(note.CustomMark ?? note.Mark, null);
            return;
        }

        string id = NoteNames().TryGetValue(note, out string? name) ? name : "ftn" + Int(++noteCount);
        XElement citation = new(TextNs + "note-citation", Clean(note.CustomMark ?? note.Mark));
        if (note.CustomMark is { Length: > 0 } custom)
        {
            citation.Add(new XAttribute(TextNs + "label", Clean(custom)));
        }

        XElement body = new(TextNs + "note-body");
        XElement element = new(TextNs + "note", new XAttribute(TextNs + "id", Clean(id)),
            new XAttribute(TextNs + "note-class", note.Kind == NoteKind.Endnote ? "endnote" : "footnote"), citation, body);
        writer.Add(element, content: true);
        StoryState saved = story;
        WriteStory(note.Story.Blocks, body, inNote: true);
        story = saved;
    }

    /// <summary>The footnotes that have a bookmark right around their reference, with the bookmark's name.</summary>
    private Dictionary<Footnote, string> NoteNames()
    {
        if (noteNames is not null)
        {
            return noteNames;
        }

        noteNames = [];
        noteNameSet = new(StringComparer.Ordinal);

        // Only bookmarks fields refer to: a note reference names the note, and the reader makes the bookmark from it again.
        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (FieldStart field in document.Descendants<FieldStart>())
        {
            if (field.GetField() is { } f && FieldCode.Parse(f.Code) is { Instruction: "REF" or "NOTEREF" or "PAGEREF", Arguments: [var target, ..] })
            {
                referenced.Add(target);
            }
        }

        foreach (Footnote note in document.Descendants<Footnote>())
        {
            if (note.Parent is not CompositeInline and not Paragraph)
            {
                continue;
            }

            NodeCollection<Inline> inlines = note.Parent is Paragraph p ? p.Inlines : ((CompositeInline)note.Parent).Inlines;
            int index = inlines.IndexOf(note);
            if (index > 0 && index + 1 < inlines.Count && inlines[index - 1] is BookmarkStart start && inlines[index + 1] is BookmarkEnd end
                && start.Name == end.Name && referenced.Contains(start.Name) && noteNameSet.Add(start.Name))
            {
                noteNames[note] = start.Name;
            }
        }

        return noteNames;
    }

    /// <summary>Whether a bookmark start or end is right next to a footnote that takes the bookmark's name.</summary>
    private bool IsNoteBracket(Inline mark)
    {
        NodeCollection<Inline>? inlines = mark.Parent switch
        {
            Paragraph p => p.Inlines,
            CompositeInline c => c.Inlines,
            _ => null,
        };
        int index = inlines?.IndexOf(mark) ?? -1;
        int at = mark is BookmarkStart ? index + 1 : index - 1;
        return inlines is not null && index >= 0 && at >= 0 && at < inlines.Count && inlines[at] is Footnote note && noteNames!.ContainsKey(note);
    }

    // ---- Fields ----

    private void WriteField(FieldStart start, ParagraphWriter writer)
    {
        Field? field = start.GetField();
        if (field is null)
        {
            return;
        }

        if (story.TocField == start)
        {
            // The table of contents stands for the field: its entries are written, its code is not.
            story.SkipUntil = field.Separator ?? (Inline)field.End;
            return;
        }

        FieldCode code = FieldCode.Parse(field.Code);
        bool samePara = field.End.Paragraph == start.Paragraph;
        if (code.Instruction == "HYPERLINK" && samePara && !writer.InLink && !writer.InDeletion && code.Arguments.Count + (code.Switch("\\L") is null ? 0 : 1) > 0)
        {
            string href = (code.Arguments.FirstOrDefault() ?? "") + (code.Switch("\\L") is { Length: > 0 } anchor ? "#" + anchor : "");
            XElement link = new(TextNs + "a", new XAttribute(XLinkNs + "type", "simple"), new XAttribute(XLinkNs + "href", Clean(href)));
            if (code.Switch("\\O") is { Length: > 0 } tip)
            {
                link.Add(new XAttribute(OfficeNs + "title", Clean(tip)));
            }

            writer.Push(link, field.End);
            story.SkipUntil = field.Separator ?? (Inline)field.End;
            return;
        }

        if (samePara && field.ResultNodes.All(n => n is Run or Tab) && SimpleField(code, ResultText(field)) is { } element)
        {
            string? style = field.ResultNodes.OfType<Run>().FirstOrDefault() is { } first ? RunStyle(first) : null;
            if (start.IsLocked)
            {
                element.SetAttributeValue(TextNs + "fixed", "true");
            }

            writer.Add(style is null ? element : new XElement(TextNs + "span", new XAttribute(TextNs + "style-name", style), element), content: true);
            story.SkipUntil = field.End;
            return;
        }

        if (code.Instruction is not ("" or "MERGEFORMAT"))
        {
            warnings.Add(ConversionWarningKind.FieldsFlattened, "Fields OpenDocument has nothing like were written as their results.");
        }

        story.SkipUntil = field.Separator ?? (Inline)field.End;
    }

    private static string ResultText(Field field)
    {
        StringBuilder text = new();
        foreach (Node node in field.ResultNodes)
        {
            text.Append(node is Run run ? run.Text : "\t");
        }

        return text.ToString();
    }

    /// <summary>A field ODF has (ODF 1.3 Part 3, 7) for a Word field code, holding its result; null when ODF has none like it.</summary>
    private XElement? SimpleField(FieldCode code, string result)
    {
        string? number = code.Switch("\\*") switch
        {
            "roman" => "i",
            "ROMAN" => "I",
            "alphabetic" => "a",
            "ALPHABETIC" => "A",
            "ARABIC" => "1",
            _ => null,
        };
        string? argument = code.Arguments.FirstOrDefault();
        XElement? element = code.Instruction switch
        {
            "PAGE" => Field("page-number", new XAttribute(TextNs + "select-page", "current"), NumFormatAttribute(number)),
            "NUMPAGES" => Field("page-count", NumFormatAttribute(number)),
            "DATE" => Field("date"),
            "TIME" => Field("time"),
            "CREATEDATE" => Field(IsTime(code.Switch("\\@")) ? "creation-time" : "creation-date"),
            "SAVEDATE" => Field("modification-date"),
            "PRINTDATE" => Field("print-date"),
            "TITLE" => Field("title"),
            "SUBJECT" => Field("subject"),
            "COMMENTS" => Field("description"),
            "KEYWORDS" => Field("keywords"),
            "AUTHOR" => Field("initial-creator"),
            "FILENAME" => Field("file-name", new XAttribute(TextNs + "display", code.Has("\\P") ? "full" : "name-and-extension")),
            "NUMWORDS" => Field("word-count", NumFormatAttribute(number)),
            "NUMCHARS" => Field("character-count", NumFormatAttribute(number)),
            "STYLEREF" when int.TryParse(argument, out int level) && level is >= 1 and <= 9 =>
                Field("chapter", new XAttribute(TextNs + "display", code.Has("\\N") ? "number" : "name"), new XAttribute(TextNs + "outline-level", Int(level))),
            "SEQ" when argument is { Length: > 0 } && Encode(argument) == argument =>
                Field("sequence", new XAttribute(TextNs + "name", argument), new XAttribute(TextNs + "formula", "ooow:" + argument + "+1"),
                    NumFormatAttribute(number)),
            "REF" when argument is { Length: > 0 } => Reference(argument, code.Has("\\P") ? "direction" : code.Has("\\R") || code.Has("\\N") || code.Has("\\W") ? "number" : "text"),
            "PAGEREF" when argument is { Length: > 0 } => Reference(argument, "page"),
            "NOTEREF" when argument is { Length: > 0 } => Reference(argument, "note"),
            "DOCPROPERTY" when argument is { Length: > 0 } => argument.ToUpperInvariant() switch
            {
                "TITLE" => Field("title"),
                "SUBJECT" => Field("subject"),
                "AUTHOR" => Field("initial-creator"),
                "KEYWORDS" => Field("keywords"),
                "COMMENTS" => Field("description"),
                _ => Field("user-defined", new XAttribute(TextNs + "name", Clean(argument))),
            },
            "MERGEFIELD" when argument is { Length: > 0 } => Field("database-display", new XAttribute(TextNs + "table-name", ""),
                new XAttribute(TextNs + "column-name", Clean(argument))),
            _ => null,
        };
        element?.Add(Clean(result));
        return element;

        static XElement Field(string name, params object?[] attributes) => new(TextNs + name, attributes);

        static XAttribute? NumFormatAttribute(string? number) => number is null ? null : new XAttribute(StyleNs + "num-format", number);

        static bool IsTime(string? picture) => picture is { Length: > 0 } && !picture.AsSpan().ContainsAny("dMy") && picture.AsSpan().ContainsAny("Hhms");
    }

    /// <summary>A reference to a bookmark, or to a note when the bookmark is the one around its reference.</summary>
    private XElement Reference(string name, string format)
    {
        NoteNames();
        if (noteNameSet!.Contains(name))
        {
            Footnote note = noteNames!.First(n => n.Value == name).Key;
            return new XElement(TextNs + "note-ref", new XAttribute(TextNs + "note-class", note.Kind == NoteKind.Endnote ? "endnote" : "footnote"),
                new XAttribute(TextNs + "reference-format", format == "page" ? "page" : "text"), new XAttribute(TextNs + "ref-name", Clean(name)));
        }

        return new XElement(TextNs + "bookmark-ref", new XAttribute(TextNs + "reference-format", format == "note" ? "number" : format),
            new XAttribute(TextNs + "ref-name", Clean(name)));
    }
}
