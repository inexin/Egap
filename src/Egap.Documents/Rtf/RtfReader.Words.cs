using System.Globalization;

namespace Egap.Documents.Rtf;

/// <summary>The control words of the body: character, paragraph and section formatting, special characters and destinations.</summary>
internal sealed partial class RtfReader
{
    private readonly PageSetup documentSetup = DefaultPageSetup();
    private PageSetup sectionSetup = DefaultPageSetup();
    private int sectionColumn;
    private int? sectionPageStart;
    private bool sectionPageRestart;
    private bool pendingNoteMark;

    /// <summary>Reads a destination, counted against the nesting limit: a destination can hold another one.</summary>
    private bool NestedDestination(string word, bool ignorable)
    {
        Enter();
        try
        {
            return Destination(word, ignorable);
        }
        finally
        {
            nesting--;
        }
    }

    /// <summary>
    /// Handles a control word. Returns true when it was a destination that read its whole group (the group is closed then).
    /// </summary>
    private bool ControlWord(string word, bool has, int p)
    {
        if (state.Ignorable)
        {
            // The word after \* names the destination; one the reader does not know is kept as it is.
            state.Ignorable = false;
            if (!NestedDestination(word, ignorable: true))
            {
                SkipDestination(keep: !DroppedDestinations.Contains(word));
            }

            return true;
        }

        if (NestedDestination(word, ignorable: false))
        {
            return true;
        }

        if ((text.Length > 0 || byteCount > 0) && word is not ("u" or "uc") && SpecialCharacter(word) is null)
        {
            // A word that may change the formatting ends the run so far; one that stands for a character continues it.
            FlushText();
        }

        if (CharacterWord(word, has, p) || ParagraphWord(word, has, p) || TableWord(word, has, p) || SectionWord(word, has, p) || DocumentWord(word, has, p))
        {
            return false;
        }

        switch (word)
        {
            case "par":
                EndParagraph(Level);
                pendingNoteMark = false;
                break;
            case "sect":
                EndSection();
                break;
            case "line":
                AddInline(new Break(document));
                break;
            case "lbr":
                AddInline(new Break(document));
                break;
            case "page":
                AddInline(new Break(document, BreakKind.Page));
                break;
            case "column":
                AddInline(new Break(document, BreakKind.Column));
                break;
            case "tab":
                AddInline(new Tab(document));
                break;
            case "cell":
                EndCell(1);
                break;
            case "nestcell":
                EndCell(Math.Max(2, state.Itap));
                break;
            case "row":
                EndRow(1);
                break;
            case "nestrow":
                EndRow(Math.Max(2, state.Itap));
                break;
            case "u" when has:
                AppendChar((char)(ushort)(short)p);
                skip = state.UnicodeSkip;
                break;
            case "uc" when has:
                state.UnicodeSkip = Math.Clamp(p, 0, 16);
                break;
            case "bin" when has:
                if (p > options.Limits.MaxPartSize)
                {
                    throw new DocumentLimitException($"RTF binary data of {p} bytes is past the limit of {options.Limits.MaxPartSize}.");
                }

                // Binary data outside a picture is not text.
                _ = tokens.ReadBinary(p);
                break;
            case "chftn":
                if (noteDepth == 0)
                {
                    pendingNoteMark = true;
                }

                break;
            case "ftnalt":
                if (currentNote is not null)
                {
                    currentNote.Kind = NoteKind.Endnote;
                }

                break;
            case "chpgn":
                AddSimpleField("PAGE");
                break;
            case "sectnum":
                AddSimpleField("SECTION");
                break;
            case "chdate":
                AddSimpleField("DATE");
                break;
            case "chdpl":
                AddSimpleField("DATE \\@ \"dddd, MMMM d, yyyy\"");
                break;
            case "chdpa":
                AddSimpleField("DATE \\@ \"ddd, MMM d, yyyy\"");
                break;
            case "chtime":
                AddSimpleField("TIME");
                break;
            case "fldlock":
                if (fields.Count > 0)
                {
                    fields.Peek().Start.IsLocked = true;
                }

                break;
            case "flddirty":
                if (fields.Count > 0)
                {
                    fields.Peek().Start.IsDirty = true;
                }

                break;
            default:
                if (SpecialCharacter(word) is char special)
                {
                    AppendChar(special);
                }

                break;
        }

        return false;
    }

    /// <summary>Groups marked <c>\*</c> that carry nothing to keep: Word's revision-save ids, its generator name, editing state.</summary>
    private static readonly HashSet<string> DroppedDestinations = new(StringComparer.Ordinal)
    {
        "rsidtbl", "generator", "blipuid", "wgrffmtfilter", "bkmkpub", "pgdsctbl", "listpicture", "pnseclvl",
    };

    private void AddSimpleField(string code)
    {
        AddInline(new FieldStart(document));
        AddInline(new Run(document, " " + code + " "));
        AddInline(new FieldSeparator(document));
        AddInline(new FieldEnd(document));
    }

    // ---- Character formatting ----

    private bool CharacterWord(string word, bool has, int p)
    {
        bool on = !has || p != 0;
        switch (word)
        {
            case "plain":
                state.ResetCharacter();
                return true;
            case "b":
                state.MutableChar().Bold = on;
                return true;
            case "i":
                state.MutableChar().Italic = on;
                return true;
            case "ab":
                state.MutableChar().BoldComplexScript = on;
                return true;
            case "ai":
                state.MutableChar().ItalicComplexScript = on;
                return true;
            case "strike":
                state.MutableChar().Strikethrough = on;
                return true;
            case "striked":
                state.MutableChar().DoubleStrikethrough = on;
                return true;
            case "caps":
                state.MutableChar().AllCaps = on;
                return true;
            case "scaps":
                state.MutableChar().SmallCaps = on;
                return true;
            case "v":
                state.MutableChar().Hidden = on;
                return true;
            case "outl":
                state.MutableChar().Outline = on;
                return true;
            case "shad":
                state.MutableChar().Shadow = on;
                return true;
            case "embo":
                state.MutableChar().Emboss = on;
                return true;
            case "impr":
                state.MutableChar().Imprint = on;
                return true;
            case "noproof":
                state.MutableChar().NoProof = on;
                return true;
            case "ul":
                state.MutableChar().Underline = on ? UnderlineStyle.Single : UnderlineStyle.None;
                return true;
            case "ulnone":
                state.MutableChar().Underline = UnderlineStyle.None;
                return true;
            case "uld" or "uldash" or "uldashd" or "uldashdd" or "uldb" or "ulhwave" or "ulldash" or "ulth" or "ulthd" or "ulthdash"
                or "ulthdashd" or "ulthdashdd" or "ulthldash" or "ululdbwave" or "ulw" or "ulwave":
                state.MutableChar().Underline = !on ? UnderlineStyle.None : word switch
                {
                    "uld" => UnderlineStyle.Dotted,
                    "uldash" => UnderlineStyle.Dash,
                    "uldashd" => UnderlineStyle.DotDash,
                    "uldashdd" => UnderlineStyle.DotDotDash,
                    "uldb" => UnderlineStyle.Double,
                    "ulhwave" => UnderlineStyle.WavyHeavy,
                    "ulldash" => UnderlineStyle.DashLong,
                    "ulth" => UnderlineStyle.Thick,
                    "ulthd" => UnderlineStyle.DottedHeavy,
                    "ulthdash" => UnderlineStyle.DashedHeavy,
                    "ulthdashd" => UnderlineStyle.DashDotHeavy,
                    "ulthdashdd" => UnderlineStyle.DashDotDotHeavy,
                    "ulthldash" => UnderlineStyle.DashLongHeavy,
                    "ululdbwave" => UnderlineStyle.WavyDouble,
                    "ulw" => UnderlineStyle.Words,
                    _ => UnderlineStyle.Wave,
                };
                return true;
            case "ulc" when has:
                state.MutableChar().UnderlineColor = Color(p);
                return true;
            case "fs" when has && p > 0:
                state.MutableChar().Size = Math.Min(p, 3276) / 2.0;
                return true;
            case "afs" when has && p > 0:
                state.MutableChar().SizeComplexScript = Math.Min(p, 3276) / 2.0;
                return true;
            case "f" when has:
                state.Font = p;
                if (FontName(p) is { } name)
                {
                    RunFormat run = state.MutableChar();
                    run.FontAscii = name;
                    run.FontHighAnsi = name;
                }

                return true;
            case "af" when has:
                if (FontName(p) is { } associated)
                {
                    RunFormat run = state.MutableChar();
                    switch (state.Slot)
                    {
                        case FontSlot.Ascii:
                            run.FontAscii = associated;
                            break;
                        case FontSlot.HighAnsi:
                            run.FontHighAnsi = associated;
                            break;
                        case FontSlot.EastAsia:
                            run.FontEastAsia = associated;
                            break;
                        default:
                            run.FontComplexScript = associated;
                            break;
                    }
                }

                return true;
            case "loch":
                state.Slot = FontSlot.Ascii;
                return true;
            case "hich":
                state.Slot = FontSlot.HighAnsi;
                return true;
            case "dbch":
                state.Slot = FontSlot.EastAsia;
                return true;
            case "rtlch":
                state.Slot = FontSlot.ComplexScript;
                state.MutableChar().RightToLeft = true;
                return true;
            case "ltrch":
                state.Slot = FontSlot.ComplexScript;
                if (state.Char.RightToLeft is not null)
                {
                    state.MutableChar().RightToLeft = null;
                }

                return true;
            case "cf" when has:
                state.MutableChar().Color = Color(p);
                return true;
            case "cb" when has:
                state.MutableChar().Shading = p > 0 ? new Shading(ShadingPattern.Clear, Color(p)) : null;
                return true;
            case "chcbpat" when has:
                state.MutableChar().Shading = UpdateShading(state.Char.Shading, fill: Color(p));
                return true;
            case "chcfpat" when has:
                state.MutableChar().Shading = UpdateShading(state.Char.Shading, patternColor: Color(p));
                return true;
            case "chshdng" when has:
                state.MutableChar().Shading = UpdateShading(state.Char.Shading, percent: p);
                return true;
            case "highlight" when has:
                state.MutableChar().Highlight = HighlightOf(p);
                return true;
            case "up":
                state.MutableChar().Position = (has ? p : 6) / 2.0;
                return true;
            case "dn":
                state.MutableChar().Position = -(has ? p : 6) / 2.0;
                return true;
            case "super":
                state.MutableChar().VerticalPosition = VerticalTextPosition.Superscript;
                return true;
            case "sub":
                state.MutableChar().VerticalPosition = VerticalTextPosition.Subscript;
                return true;
            case "nosupersub":
                state.MutableChar().VerticalPosition = VerticalTextPosition.Baseline;
                return true;
            case "expnd" when has:
                state.MutableChar().Spacing = p / 4.0;
                return true;
            case "expndtw" when has:
                state.MutableChar().Spacing = p / 20.0;
                return true;
            case "charscalex" when has && p > 0:
                state.MutableChar().Scale = Math.Min(p, 600);
                return true;
            case "kerning" when has:
                state.MutableChar().Kerning = Math.Max(0, p) / 2.0;
                return true;
            case "lang" when has:
                state.MutableChar().Language = LanguageOf(p);
                return true;
            case "langfe" when has:
                state.MutableChar().LanguageEastAsia = LanguageOf(p);
                return true;
            case "alang" when has:
                state.MutableChar().LanguageBidi = LanguageOf(p);
                return true;
            case "cs" when has:
                state.CharacterStyle = p;
                return true;
            case "chbrdr":
                state.Border = BorderTarget.Character;
                return true;
            case "revised":
                state.Inserted = on ? (state.Inserted ?? new RevisionMark(0, 0)) : null;
                return true;
            case "revauth" when has:
                state.Inserted = (state.Inserted ?? new RevisionMark(0, 0)) with { Author = p };
                return true;
            case "revdttm" when has:
                state.Inserted = (state.Inserted ?? new RevisionMark(0, 0)) with { Date = p };
                return true;
            case "deleted":
                state.Deleted = on ? (state.Deleted ?? new RevisionMark(0, 0)) : null;
                return true;
            case "revauthdel" when has:
                state.Deleted = (state.Deleted ?? new RevisionMark(0, 0)) with { Author = p };
                return true;
            case "revdttmdel" when has:
                state.Deleted = (state.Deleted ?? new RevisionMark(0, 0)) with { Date = p };
                return true;
            case "crauth" when has:
                state.CharChange = (state.CharChange ?? new FormatChange<RunFormat>(0, null, null)) with { Author = p };
                return true;
            case "crdate" when has:
                state.CharChange = (state.CharChange ?? new FormatChange<RunFormat>(0, null, null)) with { Date = DateOf(p) };
                return true;
            default:
                return BorderWord(word, has, p) || ShadingPatternWord(word);
        }
    }

    // ---- Paragraph formatting ----

    private bool ParagraphWord(string word, bool has, int p)
    {
        bool on = !has || p != 0;
        switch (word)
        {
            case "pard":
                state.ResetParagraph();
                return true;
            case "s" when has:
                state.ParagraphStyle = p;
                return true;
            case "ql":
                state.MutablePara().Alignment = ParagraphAlignment.Left;
                return true;
            case "qc":
                state.MutablePara().Alignment = ParagraphAlignment.Center;
                return true;
            case "qr":
                state.MutablePara().Alignment = ParagraphAlignment.Right;
                return true;
            case "qj":
                state.MutablePara().Alignment = ParagraphAlignment.Justify;
                return true;
            case "qd" or "qt":
                state.MutablePara().Alignment = ParagraphAlignment.Distribute;
                return true;
            case "qk" when has:
                state.MutablePara().Alignment = ParagraphAlignment.Justify;
                state.MutablePara().Kashida = p switch { 0 => KashidaLevel.Low, 10 => KashidaLevel.Medium, _ => KashidaLevel.High };
                return true;
            case "li" or "lin" when has:
                state.MutablePara().LeftIndent = Twips(p);
                return true;
            case "ri" or "rin" when has:
                state.MutablePara().RightIndent = Twips(p);
                return true;
            case "fi" when has:
                state.MutablePara().FirstLineIndent = Twips(p);
                return true;
            case "sb" when has:
                state.MutablePara().SpaceBefore = Math.Max(0, Twips(p));
                return true;
            case "sa" when has:
                state.MutablePara().SpaceAfter = Math.Max(0, Twips(p));
                return true;
            case "sbauto":
                state.MutablePara().SpaceBeforeAuto = on;
                return true;
            case "saauto":
                state.MutablePara().SpaceAfterAuto = on;
                return true;
            case "sl" when has:
                state.LineSpacing = p;
                ApplyLineSpacing();
                return true;
            case "slmult" when has:
                state.LineMultiple = p != 0;
                ApplyLineSpacing();
                return true;
            case "keep":
                state.MutablePara().KeepTogether = on;
                return true;
            case "keepn":
                state.MutablePara().KeepWithNext = on;
                return true;
            case "pagebb":
                state.MutablePara().PageBreakBefore = on;
                return true;
            case "widctlpar":
                state.MutablePara().WidowControl = true;
                return true;
            case "nowidctlpar":
                state.MutablePara().WidowControl = false;
                return true;
            case "outlinelevel" when has:
                state.MutablePara().OutlineLevel = Math.Clamp(p, 0, 9);
                return true;
            case "contextualspace":
                state.MutablePara().ContextualSpacing = on;
                return true;
            case "rtlpar":
                state.MutablePara().Bidi = true;
                return true;
            case "ltrpar":
                if (state.Para.Bidi is not null)
                {
                    state.MutablePara().Bidi = null;
                }

                return true;
            case "noline":
                state.MutablePara().SuppressLineNumbers = true;
                return true;
            case "hyphpar":
                state.MutablePara().SuppressAutoHyphens = !on;
                return true;
            case "nosnaplinegrid":
                state.MutablePara().SnapToGrid = false;
                return true;
            case "indmirror":
                state.MutablePara().MirrorIndents = on;
                return true;
            case "intbl":
                state.InTable = true;
                return true;
            case "itap" when has:
                state.Itap = Math.Clamp(p, 0, options.Limits.MaxDepth);
                state.InTable = p > 0;
                return true;
            case "ls" when has:
                state.ListOverride = p;
                return true;
            case "ilvl" when has:
                state.ListLevel = Math.Clamp(p, 0, 8);
                return true;
            case "tqr":
                state.TabAlignment = TabAlignment.Right;
                return true;
            case "tqc":
                state.TabAlignment = TabAlignment.Center;
                return true;
            case "tqdec":
                state.TabAlignment = TabAlignment.Decimal;
                return true;
            case "tldot":
                state.TabLeader = TabLeader.Dot;
                return true;
            case "tlhyph":
                state.TabLeader = TabLeader.Hyphen;
                return true;
            case "tlul":
                state.TabLeader = TabLeader.Underscore;
                return true;
            case "tlth":
                state.TabLeader = TabLeader.Heavy;
                return true;
            case "tlmdot":
                state.TabLeader = TabLeader.MiddleDot;
                return true;
            case "tleq":
                state.TabLeader = TabLeader.None;
                return true;
            case "tx" when has:
                AddTabStop(new TabStop(Math.Max(0, Twips(p)), state.TabAlignment, state.TabLeader));
                return true;
            case "tb" when has:
                AddTabStop(new TabStop(Math.Max(0, Twips(p)), TabAlignment.Bar));
                return true;
            case "brdrt":
                state.Border = BorderTarget.ParagraphTop;
                return true;
            case "brdrb":
                state.Border = BorderTarget.ParagraphBottom;
                return true;
            case "brdrl":
                state.Border = BorderTarget.ParagraphLeft;
                return true;
            case "brdrr":
                state.Border = BorderTarget.ParagraphRight;
                return true;
            case "brdrbtw":
                state.Border = BorderTarget.ParagraphBetween;
                return true;
            case "brdrbar":
                state.Border = BorderTarget.ParagraphBar;
                return true;
            case "box":
                state.Border = BorderTarget.ParagraphBox;
                return true;
            case "shading" when has:
                state.MutablePara().Shading = UpdateShading(state.Para.Shading, percent: p);
                return true;
            case "cbpat" when has:
                state.MutablePara().Shading = UpdateShading(state.Para.Shading, fill: Color(p));
                return true;
            case "cfpat" when has:
                state.MutablePara().Shading = UpdateShading(state.Para.Shading, patternColor: Color(p));
                return true;
            case "prauth" when has:
                state.ParaChange = (state.ParaChange ?? new FormatChange<ParagraphFormat>(0, null, null)) with { Author = p };
                return true;
            case "prdate" when has:
                state.ParaChange = (state.ParaChange ?? new FormatChange<ParagraphFormat>(0, null, null)) with { Date = DateOf(p) };
                return true;
            default:
                return FrameWord(word, has, p);
        }
    }

    private void ApplyLineSpacing()
    {
        ParagraphFormat format = state.MutablePara();
        int value = state.LineSpacing;
        if (value == 0)
        {
            format.LineSpacing = null;
            format.LineSpacingRule = null;
        }
        else if (state.LineMultiple && value > 0)
        {
            format.LineSpacing = value / 240.0;
            format.LineSpacingRule = LineSpacingRule.Multiple;
        }
        else
        {
            format.LineSpacing = Math.Abs(Twips(value));
            format.LineSpacingRule = value < 0 ? LineSpacingRule.Exactly : LineSpacingRule.AtLeast;
        }
    }

    private void AddTabStop(TabStop stop)
    {
        ParagraphFormat format = state.MutablePara();
        format.TabStops = [.. (format.TabStops ?? []).Where(t => t.Position != stop.Position), stop];
        state.TabAlignment = TabAlignment.Left;
        state.TabLeader = TabLeader.None;
    }

    /// <summary>A positioned frame's words (<c>\absw</c>, <c>\posx</c>, ...), which make the paragraph a frame.</summary>
    private bool FrameWord(string word, bool has, int p)
    {
        FramePosition? frame = state.Para.Frame;
        FramePosition next;
        FramePosition f = frame ?? new FramePosition();
        switch (word)
        {
            case "absw" when has:
                next = f with { Width = p > 0 ? Twips(p) : null };
                break;
            case "absh" when has:
                next = f with { Height = p == 0 ? null : Math.Abs(Twips(p)), HeightRule = p < 0 ? RowHeightRule.Exactly : p > 0 ? RowHeightRule.AtLeast : RowHeightRule.Auto };
                break;
            case "phmrg":
                next = f with { HorizontalAnchor = HorizontalAnchor.Margin };
                break;
            case "phpg":
                next = f with { HorizontalAnchor = HorizontalAnchor.Page };
                break;
            case "phcol":
                next = f with { HorizontalAnchor = HorizontalAnchor.Column };
                break;
            case "posx" when has:
                next = f with { HorizontalOffset = Twips(p), HorizontalAlignment = FloatingAlignment.None };
                break;
            case "posxl":
                next = f with { HorizontalAlignment = FloatingAlignment.Start };
                break;
            case "posxc":
                next = f with { HorizontalAlignment = FloatingAlignment.Center };
                break;
            case "posxr":
                next = f with { HorizontalAlignment = FloatingAlignment.End };
                break;
            case "posxi":
                next = f with { HorizontalAlignment = FloatingAlignment.Inside };
                break;
            case "posxo":
                next = f with { HorizontalAlignment = FloatingAlignment.Outside };
                break;
            case "pvmrg":
                next = f with { VerticalAnchor = VerticalAnchor.Margin };
                break;
            case "pvpg":
                next = f with { VerticalAnchor = VerticalAnchor.Page };
                break;
            case "pvpara":
                next = f with { VerticalAnchor = VerticalAnchor.Paragraph };
                break;
            case "posy" when has:
                next = f with { VerticalOffset = Twips(p), VerticalAlignment = FloatingAlignment.None };
                break;
            case "posyt":
                next = f with { VerticalAlignment = FloatingAlignment.Start };
                break;
            case "posyc":
                next = f with { VerticalAlignment = FloatingAlignment.Center };
                break;
            case "posyb":
                next = f with { VerticalAlignment = FloatingAlignment.End };
                break;
            case "posyin":
                next = f with { VerticalAlignment = FloatingAlignment.Inside };
                break;
            case "posyout":
                next = f with { VerticalAlignment = FloatingAlignment.Outside };
                break;
            case "dxfrtext" when has:
                next = f with { DistanceHorizontal = Twips(p), DistanceVertical = Twips(p) };
                break;
            case "dfrmtxtx" when has:
                next = f with { DistanceHorizontal = Twips(p) };
                break;
            case "dfrmtxty" when has:
                next = f with { DistanceVertical = Twips(p) };
                break;
            case "nowrap":
                next = f with { Wrap = FrameWrap.NotBeside };
                break;
            case "wraparound":
                next = f with { Wrap = FrameWrap.Around };
                break;
            case "wraptight":
                next = f with { Wrap = FrameWrap.Tight };
                break;
            case "wrapthrough":
                next = f with { Wrap = FrameWrap.Through };
                break;
            case "dropcapli" when has:
                next = f with { DropCapLines = Math.Clamp(p, 1, 10) };
                break;
            case "dropcapt" when has:
                next = f with { DropCap = p == 2 ? DropCap.Margin : DropCap.Drop };
                break;
            case "abslock":
                next = f with { AnchorLock = true };
                break;
            default:
                return false;
        }

        state.MutablePara().Frame = next;
        return true;
    }

    // ---- Borders and shading ----

    private bool BorderWord(string word, bool has, int p)
    {
        BorderStyle? style = word switch
        {
            "brdrs" or "brdrhair" => BorderStyle.Single,
            "brdrth" => BorderStyle.Thick,
            "brdrdb" => BorderStyle.Double,
            "brdrdot" => BorderStyle.Dotted,
            "brdrdash" => BorderStyle.Dashed,
            "brdrdashsm" => BorderStyle.DashSmallGap,
            "brdrdashd" => BorderStyle.DotDash,
            "brdrdashdd" => BorderStyle.DotDotDash,
            "brdrdashdotstr" => BorderStyle.DashDotStroked,
            "brdrtriple" => BorderStyle.Triple,
            "brdrtnthsg" => BorderStyle.ThinThickSmallGap,
            "brdrthtnsg" => BorderStyle.ThickThinSmallGap,
            "brdrtnthtnsg" => BorderStyle.ThinThickThinSmallGap,
            "brdrtnthmg" => BorderStyle.ThinThickMediumGap,
            "brdrthtnmg" => BorderStyle.ThickThinMediumGap,
            "brdrtnthtnmg" => BorderStyle.ThinThickThinMediumGap,
            "brdrtnthlg" => BorderStyle.ThinThickLargeGap,
            "brdrthtnlg" => BorderStyle.ThickThinLargeGap,
            "brdrtnthtnlg" => BorderStyle.ThinThickThinLargeGap,
            "brdrwavy" => BorderStyle.Wave,
            "brdrwavydb" => BorderStyle.DoubleWave,
            "brdremboss" => BorderStyle.ThreeDEmboss,
            "brdrengrave" => BorderStyle.ThreeDEngrave,
            "brdrinset" => BorderStyle.Inset,
            "brdroutset" => BorderStyle.Outset,
            "brdrnone" or "brdrnil" or "brdrtbl" => BorderStyle.None,
            _ => null,
        };
        if (style is { } s)
        {
            UpdateBorder(b => b with { Style = s, Width = s == BorderStyle.None ? 0 : b.Width });
            return true;
        }

        switch (word)
        {
            case "brdrw" when has:
                UpdateBorder(b => b with { Width = Math.Clamp(p, 0, 255) / 20.0 });
                return true;
            case "brdrcf" when has:
                UpdateBorder(b => b with { Color = Color(p) });
                return true;
            case "brsp" when has:
                UpdateBorder(b => b with { Space = Math.Clamp(Twips(p), 0, 31) });
                return true;
            case "brdrsh":
                UpdateBorder(b => b with { Shadow = true });
                return true;
            case "brdrframe":
                UpdateBorder(b => b with { Frame = true });
                return true;
            default:
                return false;
        }
    }

    /// <summary>Changes the border the last border word named.</summary>
    private void UpdateBorder(Func<Border, Border> change)
    {
        static Border Start(Border? border) => border ?? new Border(BorderStyle.Single);
        switch (state.Border)
        {
            case BorderTarget.ParagraphTop:
                state.MutablePara().BorderTop = change(Start(state.Para.BorderTop));
                break;
            case BorderTarget.ParagraphBottom:
                state.MutablePara().BorderBottom = change(Start(state.Para.BorderBottom));
                break;
            case BorderTarget.ParagraphLeft:
                state.MutablePara().BorderLeft = change(Start(state.Para.BorderLeft));
                break;
            case BorderTarget.ParagraphRight:
                state.MutablePara().BorderRight = change(Start(state.Para.BorderRight));
                break;
            case BorderTarget.ParagraphBetween:
                state.MutablePara().BorderBetween = change(Start(state.Para.BorderBetween));
                break;
            case BorderTarget.ParagraphBar:
                state.MutablePara().BorderBar = change(Start(state.Para.BorderBar));
                break;
            case BorderTarget.ParagraphBox:
                {
                    ParagraphFormat format = state.MutablePara();
                    Border box = change(Start(format.BorderTop));
                    format.BorderTop = box;
                    format.BorderBottom = box;
                    format.BorderLeft = box;
                    format.BorderRight = box;
                    break;
                }

            case BorderTarget.Character:
                state.MutableChar().Border = change(Start(state.Char.Border));
                break;
            case >= BorderTarget.TableTop and <= BorderTarget.CellDiagonalUp:
                UpdateTableBorder(state.Border, change);
                break;
        }
    }

    private bool ShadingPatternWord(string word)
    {
        // The pattern words come bare (paragraph), after "ch" (character) or "cl" (cell).
        string bare = word.StartsWith("ch", StringComparison.Ordinal) ? word[2..] : word.StartsWith("cl", StringComparison.Ordinal) ? word[2..] : word;
        ShadingPattern? pattern = bare switch
        {
            "bghoriz" => ShadingPattern.ThinHorizontalStripe,
            "bgvert" => ShadingPattern.ThinVerticalStripe,
            "bgfdiag" => ShadingPattern.ThinReverseDiagonalStripe,
            "bgbdiag" => ShadingPattern.ThinDiagonalStripe,
            "bgcross" => ShadingPattern.ThinHorizontalCross,
            "bgdcross" => ShadingPattern.ThinDiagonalCross,
            "bgdkhoriz" => ShadingPattern.HorizontalStripe,
            "bgdkvert" => ShadingPattern.VerticalStripe,
            "bgdkfdiag" => ShadingPattern.ReverseDiagonalStripe,
            "bgdkbdiag" => ShadingPattern.DiagonalStripe,
            "bgdkcross" => ShadingPattern.HorizontalCross,
            "bgdkdcross" => ShadingPattern.DiagonalCross,
            _ => null,
        };
        if (pattern is not { } kind)
        {
            return false;
        }

        if (word.StartsWith("ch", StringComparison.Ordinal))
        {
            state.MutableChar().Shading = UpdateShading(state.Char.Shading, pattern: kind);
        }
        else if (word.StartsWith("cl", StringComparison.Ordinal))
        {
            UpdateCellShading(s => UpdateShading(s, pattern: kind));
        }
        else
        {
            state.MutablePara().Shading = UpdateShading(state.Para.Shading, pattern: kind);
        }

        return true;
    }

    /// <summary>A shading with one part changed: a percentage of pattern colour (0 to 10000), a fill, a pattern colour or a pattern.</summary>
    private static Shading UpdateShading(Shading? shading, int? percent = null, DocumentColor? fill = null, DocumentColor? patternColor = null, ShadingPattern? pattern = null)
    {
        Shading s = shading ?? new Shading(ShadingPattern.Clear);
        if (percent is int value)
        {
            double share = Math.Clamp(value, 0, 10000) / 100.0;
            s = s.Pattern is ShadingPattern.Clear or ShadingPattern.Percent or ShadingPattern.Solid
                ? s with { Pattern = share <= 0 ? ShadingPattern.Clear : share >= 100 ? ShadingPattern.Solid : ShadingPattern.Percent, Percent = share }
                : s with { Percent = share };
        }

        if (fill is { } f)
        {
            s = s with { Fill = f };
        }

        if (patternColor is { } c)
        {
            s = s with { PatternColor = c };
        }

        if (pattern is { } kind)
        {
            s = s with { Pattern = kind };
        }

        return s;
    }

    // ---- Sections and the document ----

    private static PageSetup DefaultPageSetup()
    {
        // RTF's defaults: US Letter, 1.25 inch side margins, 1 inch top and bottom, headers and footers half an inch out.
        return new PageSetup
        {
            PageWidth = 612,
            PageHeight = 792,
            MarginLeft = 90,
            MarginRight = 90,
            MarginTop = 72,
            MarginBottom = 72,
            HeaderDistance = 36,
            FooterDistance = 36,
            Gutter = 0,
        };
    }

    private bool SectionWord(string word, bool has, int p)
    {
        PageSetup s = sectionSetup;
        switch (word)
        {
            case "sectd":
                sectionSetup = new PageSetup();
                sectionSetup.CopyRaw(documentSetup);
                sectionColumn = 0;
                sectionPageStart = null;
                sectionPageRestart = false;
                return true;
            case "sbknone":
                s.SectionStart = SectionStart.Continuous;
                return true;
            case "sbkcol":
                s.SectionStart = SectionStart.NewColumn;
                return true;
            case "sbkpage":
                s.SectionStart = SectionStart.NewPage;
                return true;
            case "sbkeven":
                s.SectionStart = SectionStart.EvenPage;
                return true;
            case "sbkodd":
                s.SectionStart = SectionStart.OddPage;
                return true;
            case "pgwsxn" when has && p > 0:
                s.PageWidth = Twips(p);
                return true;
            case "pghsxn" when has && p > 0:
                s.PageHeight = Twips(p);
                return true;
            case "marglsxn" when has:
                s.MarginLeft = Math.Max(0, Twips(p));
                return true;
            case "margrsxn" when has:
                s.MarginRight = Math.Max(0, Twips(p));
                return true;
            case "margtsxn" when has:
                s.MarginTop = Twips(p);
                return true;
            case "margbsxn" when has:
                s.MarginBottom = Twips(p);
                return true;
            case "guttersxn" when has:
                s.Gutter = Math.Max(0, Twips(p));
                return true;
            case "headery" when has:
                s.HeaderDistance = Math.Max(0, Twips(p));
                return true;
            case "footery" when has:
                s.FooterDistance = Math.Max(0, Twips(p));
                return true;
            case "lndscpsxn":
                s.Orientation = PageOrientation.Landscape;
                return true;
            case "titlepg":
                s.DifferentFirstPage = true;
                return true;
            case "cols" when has:
                s.ColumnCount = Math.Clamp(p, 1, 45);
                return true;
            case "colsx" when has:
                s.ColumnSpacing = Math.Max(0, Twips(p));
                return true;
            case "colno" when has:
                sectionColumn = Math.Clamp(p, 1, 45) - 1;
                return true;
            case "colw" when has:
                SetColumn(c => c with { Width = Math.Max(0, Twips(p)) });
                return true;
            case "colsr" when has:
                SetColumn(c => c with { SpaceAfter = Math.Max(0, Twips(p)) });
                return true;
            case "linebetcol":
                s.ColumnSeparator = true;
                return true;
            case "pgnstarts" when has:
                sectionPageStart = Math.Max(0, p);
                return true;
            case "pgnrestart":
                sectionPageRestart = true;
                return true;
            case "pgndec":
                s.PageNumberStyle = NumberStyle.Decimal;
                return true;
            case "pgnucrm":
                s.PageNumberStyle = NumberStyle.UpperRoman;
                return true;
            case "pgnlcrm":
                s.PageNumberStyle = NumberStyle.LowerRoman;
                return true;
            case "pgnucltr":
                s.PageNumberStyle = NumberStyle.UpperLetter;
                return true;
            case "pgnlcltr":
                s.PageNumberStyle = NumberStyle.LowerLetter;
                return true;
            case "vertalt":
                s.VerticalAlignment = VerticalAlignment.Top;
                return true;
            case "vertalc":
                s.VerticalAlignment = VerticalAlignment.Center;
                return true;
            case "vertalb":
                s.VerticalAlignment = VerticalAlignment.Bottom;
                return true;
            case "vertalj":
                s.VerticalAlignment = VerticalAlignment.Justify;
                return true;
            case "rtlsect":
                s.Bidi = true;
                return true;
            case "pgbrdrt" or "pgbrdrb" or "pgbrdrl" or "pgbrdrr":
                state.Border = BorderTarget.Page;
                return true;

            // Document-wide page setup, which every \sectd starts from.
            case "paperw" when has && p > 0:
                SetBoth(x => x.PageWidth = Twips(p));
                return true;
            case "paperh" when has && p > 0:
                SetBoth(x => x.PageHeight = Twips(p));
                return true;
            case "margl" when has:
                SetBoth(x => x.MarginLeft = Math.Max(0, Twips(p)));
                return true;
            case "margr" when has:
                SetBoth(x => x.MarginRight = Math.Max(0, Twips(p)));
                return true;
            case "margt" when has:
                SetBoth(x => x.MarginTop = Twips(p));
                return true;
            case "margb" when has:
                SetBoth(x => x.MarginBottom = Twips(p));
                return true;
            case "gutter" when has:
                SetBoth(x => x.Gutter = Math.Max(0, Twips(p)));
                return true;
            case "landscape":
                SetBoth(x => x.Orientation = PageOrientation.Landscape);
                return true;
            default:
                return false;
        }

        void SetBoth(Action<PageSetup> set)
        {
            set(documentSetup);
            set(sectionSetup);
        }
    }

    private void SetColumn(Func<TextColumn, TextColumn> change)
    {
        List<TextColumn> columns = [.. sectionSetup.Columns ?? []];
        while (columns.Count <= sectionColumn)
        {
            columns.Add(new TextColumn(0));
        }

        columns[sectionColumn] = change(columns[sectionColumn]);
        sectionSetup.Columns = columns;
        sectionSetup.EqualColumnWidths = false;
    }

    private bool DocumentWord(string word, bool has, int p)
    {
        DocumentSettings settings = document.Settings;
        switch (word)
        {
            case "ansi":
                ansiCodePage = 1252;
                return true;
            case "mac":
                ansiCodePage = 10000;
                return true;
            case "pc":
                ansiCodePage = 437;
                return true;
            case "pca":
                ansiCodePage = 850;
                return true;
            case "ansicpg" when has && p > 0:
                ansiCodePage = p;
                return true;
            case "deff" when has:
                defaultFont = p;
                return true;
            case "stshfloch" when has:
                defaultFonts[0] = p;
                return true;
            case "stshfhich" when has:
                defaultFonts[1] = p;
                return true;
            case "stshfdbch" when has:
                defaultFonts[2] = p;
                return true;
            case "stshfbi" when has:
                defaultFonts[3] = p;
                return true;
            case "deflang" when has:
                document.Styles.DefaultRunFormat.Language ??= LanguageOf(p);
                return true;
            case "deflangfe" when has:
                document.Styles.DefaultRunFormat.LanguageEastAsia ??= LanguageOf(p);
                return true;
            case "deftab" when has && p > 0:
                settings.DefaultTabStop = Twips(p);
                return true;
            case "facingp":
                settings.EvenAndOddHeaders = true;
                return true;
            case "margmirror":
                settings.MirrorMargins = true;
                return true;
            case "gutterprl":
                settings.GutterAtTop = true;
                return true;
            case "revisions":
                settings.TrackRevisions = true;
                return true;
            case "ftnstart" when has && p >= 1:
                settings.FootnoteStartAt = p;
                return true;
            case "aftnstart" when has && p >= 1:
                settings.EndnoteStartAt = p;
                return true;
            case "ftnnar" or "ftnnalc" or "ftnnauc" or "ftnnrlc" or "ftnnruc":
                settings.FootnoteNumberStyle = NoteNumberStyle(word[4..]);
                return true;
            case "aftnnar" or "aftnnalc" or "aftnnauc" or "aftnnrlc" or "aftnnruc":
                settings.EndnoteNumberStyle = NoteNumberStyle(word[5..]);
                return true;
            case "nobrkwrptbl":
                settings.WordCompatibility.DoNotBreakWrappedTables = true;
                return true;
            case "spltpgpar":
                settings.WordCompatibility.SplitPageBreakAndParagraphMark = true;
                return true;
            case "noindnmbrts":
                settings.WordCompatibility.DoNotUseIndentAsNumberingTabStop = true;
                return true;
            default:
                return false;
        }

        static NumberStyle NoteNumberStyle(string code) => code switch
        {
            "alc" => NumberStyle.LowerLetter,
            "auc" => NumberStyle.UpperLetter,
            "rlc" => NumberStyle.LowerRoman,
            "ruc" => NumberStyle.UpperRoman,
            _ => NumberStyle.Decimal,
        };
    }

    // ---- Values ----

    private static double Twips(int value) => value / 20.0;

    private static DateTimeOffset? DateOf(int dttm)
    {
        // Word's DTTM: minutes, hours, day, month and years since 1900 packed into 32 bits.
        uint value = (uint)dttm;
        int minute = (int)(value & 0x3F);
        int hour = (int)((value >> 6) & 0x1F);
        int day = (int)((value >> 11) & 0x1F);
        int month = (int)((value >> 16) & 0xF);
        int year = 1900 + (int)((value >> 20) & 0x1FF);
        if (value == 0 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }

    private static string? LanguageOf(int lcid)
    {
        if (lcid is <= 0 or 1024 or 0x0400 or 0x7F or >= 0x10000)
        {
            return null;
        }

        try
        {
            string name = CultureInfo.GetCultureInfo(lcid).Name;
            return name.Length == 0 ? null : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static readonly (HighlightColor Color, uint Rgb)[] Highlights =
    [
        (HighlightColor.Black, 0x000000), (HighlightColor.Blue, 0x0000FF), (HighlightColor.Cyan, 0x00FFFF), (HighlightColor.Green, 0x00FF00),
        (HighlightColor.Magenta, 0xFF00FF), (HighlightColor.Red, 0xFF0000), (HighlightColor.Yellow, 0xFFFF00), (HighlightColor.White, 0xFFFFFF),
        (HighlightColor.DarkBlue, 0x000080), (HighlightColor.DarkCyan, 0x008080), (HighlightColor.DarkGreen, 0x008000),
        (HighlightColor.DarkMagenta, 0x800080), (HighlightColor.DarkRed, 0x800000), (HighlightColor.DarkYellow, 0x808000),
        (HighlightColor.DarkGray, 0x808080), (HighlightColor.LightGray, 0xC0C0C0),
    ];

    /// <summary>The highlight colour nearest to colour table entry <paramref name="index"/>; none for 0 or automatic.</summary>
    private HighlightColor HighlightOf(int index)
    {
        if (index <= 0 || Color(index) is not { Kind: DocumentColorKind.Rgb } color)
        {
            return HighlightColor.None;
        }

        HighlightColor best = HighlightColor.Yellow;
        int bestDistance = int.MaxValue;
        foreach ((HighlightColor candidate, uint rgb) in Highlights)
        {
            int dr = color.Rgb.R - (int)(rgb >> 16);
            int dg = color.Rgb.G - (int)((rgb >> 8) & 0xFF);
            int db = color.Rgb.B - (int)(rgb & 0xFF);
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }
}
