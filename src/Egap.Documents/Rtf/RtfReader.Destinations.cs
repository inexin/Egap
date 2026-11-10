using System.Globalization;

using Egap.Documents.Word;

namespace Egap.Documents.Rtf;

/// <summary>
/// Destinations: groups whose first control word says what their text is. The header tables, fields, notes, headers and
/// footers, comments, bookmarks, old-style numbering and the formatting before a tracked change.
/// </summary>
internal sealed partial class RtfReader
{
    private readonly Stack<FieldContext> fields = [];
    private readonly Dictionary<string, int> commentIds = new(StringComparer.Ordinal);
    private int noteDepth;
    private int commentDepth;
    private Footnote? currentNote;
    private CommentContext? currentComment;
    private string? pendingAuthor;
    private string? pendingInitials;

    /// <summary>A field being read: its start, and whether its result has begun.</summary>
    private sealed class FieldContext(FieldStart start)
    {
        public FieldStart Start { get; } = start;

        public bool Separated { get; set; }
    }

    /// <summary>What a comment's own group says about it: the reference that ties it to its range, the date and the parent.</summary>
    private sealed class CommentContext
    {
        public string? Reference { get; set; }

        public DateTimeOffset? Date { get; set; }

        public string? Parent { get; set; }
    }

    /// <summary>Destinations that carry nothing the model keeps: the text of list numbers, index and contents entries, fallbacks.</summary>
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "pntext", "listtext", "xe", "tc", "tcn", "txe", "rxe", "nonesttables", "nonshppict", "file", "filetbl", "mmathPict",
        "bkmkcolf", "bkmkcoll", "atntime", "atnicn", "fldtype",
    };

    /// <summary>Reads destination <paramref name="word"/>'s group if the reader knows it; false otherwise.</summary>
    private bool Destination(string word, bool ignorable)
    {
        switch (word)
        {
            case "fonttbl":
                ReadFontTable();
                return true;
            case "mmathPr":
                ReadMathProperties();
                return true;
            case "colortbl":
                ReadColorTable();
                return true;
            case "stylesheet":
                ReadStyleSheet();
                return true;
            case "listtable":
                ReadListTable();
                return true;
            case "listoverridetable":
                ReadListOverrideTable();
                return true;
            case "revtbl":
                ReadRevisionTable();
                return true;
            case "info":
                ReadInfo();
                return true;
            case "userprops":
                ReadUserProperties();
                return true;
            case "defchp":
                {
                    (RunFormat run, _) = ReadProperties();
                    RunFormat defaults = document.Styles.DefaultRunFormat;
                    defaults.CopyRaw(run);
                    defaults.Size ??= 12;
                    return true;
                }

            case "defpap":
                {
                    (_, ParagraphFormat paragraph) = ReadProperties();
                    document.Styles.DefaultParagraphFormat.CopyRaw(paragraph);
                    return true;
                }

            case "field":
                ReadField();
                return true;
            case "fldinst":
                ReadGroup(depth);
                return true;
            case "fldrslt":
                if (fields.TryPeek(out FieldContext? field) && !field.Separated)
                {
                    AddInline(new FieldSeparator(document));
                    field.Separated = true;
                }

                ReadGroup(depth);
                return true;
            case "footnote":
                ReadNote();
                return true;
            case "header" or "headerr" or "headerl" or "headerf" or "footer" or "footerr" or "footerl" or "footerf":
                ReadHeaderFooter(word);
                return true;
            case "annotation":
                ReadComment();
                return true;
            case "atnid":
                pendingInitials = ReadText();
                return true;
            case "atnauthor":
                pendingAuthor = ReadText();
                return true;
            case "atnref" or "atndate" or "atnparent":
                {
                    string value = ReadText();
                    if (currentComment is { } comment)
                    {
                        switch (word)
                        {
                            case "atnref":
                                comment.Reference = value;
                                break;
                            case "atnparent":
                                comment.Parent = value;
                                break;
                            default:
                                comment.Date = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dttm) ? WordProperties.Date((int)dttm) : null;
                                break;
                        }
                    }

                    return true;
                }

            case "atrfstart" or "atrfend":
                {
                    string name = ReadText();
                    if (commentDepth == 0 && name.Length > 0)
                    {
                        int id = CommentIdFor(name);
                        _ = word == "atrfstart" ? AddInline(new CommentRangeStart(document, id)) : (Inline)AddInline(new CommentRangeEnd(document, id));
                    }

                    return true;
                }

            case "protusertbl":
                ReadProtectionUsers();
                return true;
            case "protstart" or "protend":
                ReadProtectionRange(word == "protstart");
                return true;
            case "passwordhash":
                ReadPasswordHash();
                return true;
            case "bkmkstart" or "bkmkend":
                {
                    string name = ReadText();
                    if (name.Length > 0)
                    {
                        _ = word == "bkmkstart" ? AddInline(new BookmarkStart(document, name)) : (Inline)AddInline(new BookmarkEnd(document, name));
                    }

                    return true;
                }

            case "pn":
                ReadPn();
                return true;
            case "upr":
                ReadUpr();
                return true;
            case "ud":
                ReadGroup(depth);
                return true;
            case "oldcprops":
                {
                    (RunFormat old, _) = ReadProperties();
                    state.CharChange = (state.CharChange ?? new FormatChange<RunFormat>(0, null, null)) with { Old = old };
                    return true;
                }

            case "oldpprops":
                {
                    (_, ParagraphFormat old) = ReadProperties();
                    state.ParaChange = (state.ParaChange ?? new FormatChange<ParagraphFormat>(0, null, null)) with { Old = old };
                    return true;
                }

            case "nesttableprops":
                {
                    bool outer = inNestTableProps;
                    inNestTableProps = true;
                    ReadGroup(depth);
                    inNestTableProps = outer;
                    return true;
                }

            case "do":
                Warn(ConversionWarningKind.DrawingDropped, "An old-style RTF drawing object (\\do) was dropped.");
                SkipDestination();
                return true;
            default:
                if (SkippedDestinations.Contains(word))
                {
                    SkipDestination();
                    return true;
                }

                return DrawingDestination(word, ignorable);
        }
    }

    /// <summary>Reads the rest of the current group as text (nested groups included, <c>\*</c> groups skipped) and closes it.</summary>
    private string ReadText(bool toSemicolon = false, bool trim = true, bool skipIgnorable = true)
    {
        int groupDepth = depth;
        TextCollector collected = new(this);
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            switch (kind)
            {
                case RtfTokenKind.End:
                    return collected.Text(toSemicolon, trim);
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return collected.Text(toSemicolon, trim);
                    }

                    break;
                case RtfTokenKind.ControlSymbol when tokens.Symbol == '*':
                    if (skipIgnorable && depth > groupDepth)
                    {
                        SkipDestination();
                    }

                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "bin" && tokens.HasParameter:
                    _ = tokens.ReadBinary(Math.Max(0, tokens.Parameter));
                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "uc" && tokens.HasParameter:
                    state.UnicodeSkip = Math.Clamp(tokens.Parameter, 0, 16);
                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "f" && tokens.HasParameter:
                    state.Font = tokens.Parameter;
                    break;
                default:
                    collected.Add(kind);
                    break;
            }
        }
    }

    /// <summary>Reads a group of formatting words alone (<c>\defchp</c>, <c>\oldcprops</c>...) from the defaults, and closes it.</summary>
    private (RunFormat Run, ParagraphFormat Paragraph) ReadProperties()
    {
        int groupDepth = depth;
        state.ResetCharacter();
        state.ResetParagraph();
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return (state.Char, state.Para);
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    SkipDestination();
                    break;
                case RtfTokenKind.GroupEnd:
                    (RunFormat Run, ParagraphFormat Paragraph) result = (state.Char, state.Para);
                    Pop();
                    if (depth < groupDepth)
                    {
                        return result;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    string word = tokens.Word;
                    if (word is not ("plain" or "pard"))
                    {
                        _ = CharacterWord(word, tokens.HasParameter, tokens.Parameter) || ParagraphWord(word, tokens.HasParameter, tokens.Parameter);
                    }

                    break;
            }
        }
    }

    // ---- Fields ----

    private void ReadField()
    {
        if (fields.Count >= 64)
        {
            // Fields nested this deep are not real documents; their text is read without the field.
            ReadGroup(depth);
            return;
        }

        FieldStart start = AddInline(new FieldStart(document));
        fields.Push(new FieldContext(start));
        try
        {
            ReadGroup(depth);
        }
        finally
        {
            fields.Pop();
        }

        AddInline(new FieldEnd(document));
    }

    // ---- Notes, headers and footers, comments ----

    private void ReadNote()
    {
        FlushText();
        if (noteDepth > 0 || commentDepth > 0)
        {
            SkipDestination();
            return;
        }

        Footnote note = new(document, NoteKind.Footnote);
        if (!pendingNoteMark && story.Paragraph is { Inlines.Count: > 0 } paragraph && paragraph.Inlines[^1] is Run { Text.Length: > 0 and <= 10 } mark
            && (mark.Format.VerticalPosition == VerticalTextPosition.Superscript || mark.Style is not null))
        {
            // A note with a mark of its own: Word writes the mark as text before the note, with no \chftn.
            note.CustomMark = mark.Text;
            mark.Remove();
        }

        AddInline(note);

        // A tracked change around the note is the mark's; the note's text says its own.
        state.Inserted = null;
        state.Deleted = null;
        Footnote? outer = currentNote;
        currentNote = note;
        noteDepth++;
        try
        {
            ReadStory(note.Story.Blocks);
        }
        finally
        {
            noteDepth--;
            currentNote = outer;
            pendingNoteMark = false;
        }
    }

    private void ReadHeaderFooter(string word)
    {
        HeaderFooterKind kind = word switch
        {
            "headerl" => HeaderFooterKind.HeaderEven,
            "headerf" => HeaderFooterKind.HeaderFirst,
            "footer" or "footerr" => HeaderFooterKind.FooterPrimary,
            "footerl" => HeaderFooterKind.FooterEven,
            "footerf" => HeaderFooterKind.FooterFirst,
            _ => HeaderFooterKind.HeaderPrimary,
        };
        if (!story.IsMain || section.HeadersFooters[kind] is not null)
        {
            SkipDestination();
            return;
        }

        HeaderFooter headerFooter = section.HeadersFooters.AddTrusted(new HeaderFooter(document, kind));
        noteDepth++;
        try
        {
            ReadStory(headerFooter.Blocks);
        }
        finally
        {
            noteDepth--;
        }
    }

    private void ReadComment()
    {
        FlushText();
        if (commentDepth > 0)
        {
            SkipDestination();
            return;
        }

        Comment comment = new(document, pendingAuthor ?? "") { Initials = string.IsNullOrEmpty(pendingInitials) ? null : pendingInitials };
        pendingAuthor = null;
        pendingInitials = null;
        AddInline(comment);
        CommentContext context = new();
        currentComment = context;
        commentDepth++;
        try
        {
            ReadStory(comment.Story.Blocks);
        }
        finally
        {
            commentDepth--;
            currentComment = null;
        }

        int id = context.Reference is { Length: > 0 } reference ? CommentIdFor(reference) : CommentIdFor("\0" + commentIds.Count.ToString(CultureInfo.InvariantCulture));
        int? parent = context.Parent is { Length: > 0 } parentReference && commentIds.TryGetValue(parentReference, out int parentId) ? parentId : null;
        comment.SetIdsSilently(id, parent);
        comment.Date = context.Date;
        document.ReserveCommentId(id);
    }

    private int CommentIdFor(string reference)
    {
        if (!commentIds.TryGetValue(reference, out int id))
        {
            id = commentIds.Count;
            commentIds[reference] = id;
        }

        return id;
    }

    // ---- Old-style numbering and Unicode alternatives ----

    private void ReadPn()
    {
        int groupDepth = depth;
        PnState pn = new();
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word is "pntxtb" or "pntxta")
                    {
                        bool before = tokens.Word == "pntxtb";
                        string value = ReadText(trim: false);
                        pn = before ? pn with { Before = value } : pn with { After = value };
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        state.Pn = pn;
                        return;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    int p = tokens.Parameter;
                    bool has = tokens.HasParameter;
                    pn = tokens.Word switch
                    {
                        "pnlvl" when has => pn with { Level = Math.Clamp(p, 1, 9) },
                        "pnlvlbody" => pn with { Level = 10 },
                        "pnlvlblt" => pn with { Level = 11, Bullet = true },
                        "pnlvlcont" => pn with { Ignored = true },
                        "pndec" => pn with { Style = NumberStyle.Decimal },
                        "pnucrm" => pn with { Style = NumberStyle.UpperRoman },
                        "pnlcrm" => pn with { Style = NumberStyle.LowerRoman },
                        "pnucltr" => pn with { Style = NumberStyle.UpperLetter },
                        "pnlcltr" => pn with { Style = NumberStyle.LowerLetter },
                        "pnord" => pn with { Style = NumberStyle.Ordinal },
                        "pncard" => pn with { Style = NumberStyle.CardinalText },
                        "pnordt" => pn with { Style = NumberStyle.OrdinalText },
                        "pnstart" when has => pn with { Start = Math.Max(0, p) },
                        "pnf" when has => pn with { Font = p },
                        "pnindent" when has => pn with { Indent = WordProperties.Twips(p) },
                        _ => pn,
                    };
                    break;
            }
        }
    }

    /// <summary><c>\upr</c>: text twice, in the code page and as Unicode (<c>\*\ud</c>); the Unicode one is read.</summary>
    private void ReadUpr()
    {
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                    {
                        first = tokens.Next();
                    }

                    if (first == RtfTokenKind.ControlWord && tokens.Word == "ud")
                    {
                        ReadGroup(depth);
                    }
                    else if (first == RtfTokenKind.GroupEnd)
                    {
                        Pop();
                    }
                    else
                    {
                        if (first == RtfTokenKind.GroupStart)
                        {
                            Push(tokens.Position - 1);
                            SkipDestination();
                        }

                        SkipDestination();
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    /// <summary>The tracked insertion or deletion the state marks, or null.</summary>
    private Revision? RevisionOf(State current)
    {
        if (current.Deleted is { } deleted)
        {
            return new Revision(RevisionKind.Deletion, Author(deleted.Author), WordProperties.Date(deleted.Date));
        }

        if (current.Inserted is { } inserted)
        {
            return new Revision(RevisionKind.Insertion, Author(inserted.Author), WordProperties.Date(inserted.Date));
        }

        return null;
    }
}
