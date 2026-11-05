using System.Globalization;

using Egap.Documents.Word;

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

        if (CharacterWord(word, has, p) || ParagraphWord(word, has, p) || TableWord(word, has, p) || SectionWord(word, has, p) || DocumentWord(word, has, p) || ProtectionWord(word, has, p))
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
                    WordProperties.Set(fields.Peek().Start, WordFieldProperty.Locked, 1);
                }

                break;
            case "flddirty":
                if (fields.Count > 0)
                {
                    WordProperties.Set(fields.Peek().Start, WordFieldProperty.Dirty, 1);
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
        int toggle = on ? 1 : 0;
        switch (word)
        {
            case "plain":
                state.ResetCharacter();
                return true;
            case "b":
                Run(WordRunProperty.Bold, toggle);
                return true;
            case "i":
                Run(WordRunProperty.Italic, toggle);
                return true;
            case "ab":
                Run(WordRunProperty.BoldComplexScript, toggle);
                return true;
            case "ai":
                Run(WordRunProperty.ItalicComplexScript, toggle);
                return true;
            case "strike":
                Run(WordRunProperty.Strikethrough, toggle);
                return true;
            case "striked":
                Run(WordRunProperty.DoubleStrikethrough, toggle);
                return true;
            case "caps":
                Run(WordRunProperty.AllCaps, toggle);
                return true;
            case "scaps":
                Run(WordRunProperty.SmallCaps, toggle);
                return true;
            case "v":
                Run(WordRunProperty.Hidden, toggle);
                return true;
            case "outl":
                Run(WordRunProperty.Outline, toggle);
                return true;
            case "shad":
                Run(WordRunProperty.Shadow, toggle);
                return true;
            case "embo":
                Run(WordRunProperty.Emboss, toggle);
                return true;
            case "impr":
                Run(WordRunProperty.Imprint, toggle);
                return true;
            case "noproof":
                Run(WordRunProperty.NoProof, toggle);
                return true;
            case "ul":
                Run(WordRunProperty.Underline, (int)(on ? UnderlineStyle.Single : UnderlineStyle.None));
                return true;
            case "ulnone":
                Run(WordRunProperty.Underline, (int)UnderlineStyle.None);
                return true;
            case "uld" or "uldash" or "uldashd" or "uldashdd" or "uldb" or "ulhwave" or "ulldash" or "ulth" or "ulthd" or "ulthdash"
                or "ulthdashd" or "ulthdashdd" or "ulthldash" or "ululdbwave" or "ulw" or "ulwave":
                Run(WordRunProperty.Underline, (int)(!on ? UnderlineStyle.None : word switch
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
                }));
                return true;
            case "ulc" when has:
                WordProperties.Set(state.MutableChar(), WordRunColor.UnderlineColor, Color(p));
                return true;
            case "fs" when has && p > 0:
                Run(WordRunProperty.Size, p);
                return true;
            case "afs" when has && p > 0:
                Run(WordRunProperty.SizeComplexScript, p);
                return true;
            case "f" when has:
                state.Font = p;
                if (FontName(p) is { } name)
                {
                    WordProperties.SetFont(state.MutableChar(), WordFontSlot.Latin, name);
                }

                return true;
            case "af" when has:
                if (FontName(p) is { } associated)
                {
                    WordProperties.SetFont(state.MutableChar(), state.Slot, associated);
                }

                return true;
            case "loch":
                state.Slot = WordFontSlot.Ascii;
                return true;
            case "hich":
                state.Slot = WordFontSlot.HighAnsi;
                return true;
            case "dbch":
                state.Slot = WordFontSlot.EastAsia;
                return true;
            case "rtlch":
                state.Slot = WordFontSlot.ComplexScript;
                Run(WordRunProperty.RightToLeft, 1);
                return true;
            case "ltrch":
                state.Slot = WordFontSlot.ComplexScript;
                if (state.Char.RightToLeft is not null)
                {
                    WordProperties.Clear(state.MutableChar(), WordRunProperty.RightToLeft);
                }

                return true;
            case "cf" when has:
                WordProperties.Set(state.MutableChar(), WordRunColor.Color, Color(p));
                return true;
            case "cb" when has:
                state.MutableChar().Shading = p > 0 ? new Shading(ShadingPattern.Clear, Color(p)) : null;
                return true;
            case "chcbpat" when has:
                state.MutableChar().Shading = WordProperties.Shade(state.Char.Shading, fill: Color(p));
                return true;
            case "chcfpat" when has:
                state.MutableChar().Shading = WordProperties.Shade(state.Char.Shading, patternColor: Color(p));
                return true;
            case "chshdng" when has:
                state.MutableChar().Shading = WordProperties.Shade(state.Char.Shading, percent: p);
                return true;
            case "highlight" when has:
                Run(WordRunProperty.Highlight, (int)HighlightOf(p));
                return true;
            case "up":
                Run(WordRunProperty.Position, has ? p : 6);
                return true;
            case "dn":
                Run(WordRunProperty.Position, -(has ? p : 6));
                return true;
            case "super":
                Run(WordRunProperty.VerticalPosition, (int)VerticalTextPosition.Superscript);
                return true;
            case "sub":
                Run(WordRunProperty.VerticalPosition, (int)VerticalTextPosition.Subscript);
                return true;
            case "nosupersub":
                Run(WordRunProperty.VerticalPosition, (int)VerticalTextPosition.Baseline);
                return true;
            case "expnd" when has:
                Run(WordRunProperty.SpacingQuarterPoints, p);
                return true;
            case "expndtw" when has:
                Run(WordRunProperty.Spacing, p);
                return true;
            case "charscalex" when has && p > 0:
                Run(WordRunProperty.Scale, p);
                return true;
            case "kerning" when has:
                Run(WordRunProperty.Kerning, p);
                return true;
            case "lang" when has:
                Run(WordRunProperty.Language, p);
                return true;
            case "langfe" when has:
                Run(WordRunProperty.LanguageEastAsia, p);
                return true;
            case "alang" when has:
                Run(WordRunProperty.LanguageBidi, p);
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
                state.CharChange = (state.CharChange ?? new FormatChange<RunFormat>(0, null, null)) with { Date = WordProperties.Date(p) };
                return true;
            default:
                return BorderWord(word, has, p) || ShadingPatternWord(word);
        }
    }

    // ---- Paragraph formatting ----

    private bool ParagraphWord(string word, bool has, int p)
    {
        bool on = !has || p != 0;
        int toggle = on ? 1 : 0;
        switch (word)
        {
            case "pard":
                state.ResetParagraph();
                return true;
            case "s" when has:
                state.ParagraphStyle = p;
                return true;
            case "ql":
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Left);
                return true;
            case "qc":
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Center);
                return true;
            case "qr":
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Right);
                return true;
            case "qj":
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Justify);
                return true;
            case "qd" or "qt":
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Distribute);
                return true;
            case "qk" when has:
                Para(WordParagraphProperty.Alignment, (int)ParagraphAlignment.Justify);
                Para(WordParagraphProperty.Kashida, (int)(p switch { 0 => KashidaLevel.Low, 10 => KashidaLevel.Medium, _ => KashidaLevel.High }));
                return true;
            case "li" or "lin" when has:
                Para(WordParagraphProperty.LeftIndent, p);
                return true;
            case "ri" or "rin" when has:
                Para(WordParagraphProperty.RightIndent, p);
                return true;
            case "fi" when has:
                Para(WordParagraphProperty.FirstLineIndent, p);
                return true;
            case "sb" when has:
                Para(WordParagraphProperty.SpaceBefore, p);
                return true;
            case "sa" when has:
                Para(WordParagraphProperty.SpaceAfter, p);
                return true;
            case "sbauto":
                Para(WordParagraphProperty.SpaceBeforeAuto, toggle);
                return true;
            case "saauto":
                Para(WordParagraphProperty.SpaceAfterAuto, toggle);
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
                Para(WordParagraphProperty.KeepTogether, toggle);
                return true;
            case "keepn":
                Para(WordParagraphProperty.KeepWithNext, toggle);
                return true;
            case "pagebb":
                Para(WordParagraphProperty.PageBreakBefore, toggle);
                return true;
            case "widctlpar":
                Para(WordParagraphProperty.WidowControl, 1);
                return true;
            case "nowidctlpar":
                Para(WordParagraphProperty.WidowControl, 0);
                return true;
            case "outlinelevel" when has:
                Para(WordParagraphProperty.OutlineLevel, p);
                return true;
            case "contextualspace":
                Para(WordParagraphProperty.ContextualSpacing, toggle);
                return true;
            case "rtlpar":
                Para(WordParagraphProperty.Bidi, 1);
                return true;
            case "ltrpar":
                if (state.Para.Bidi is not null)
                {
                    WordProperties.Clear(state.MutablePara(), WordParagraphProperty.Bidi);
                }

                return true;
            case "noline":
                Para(WordParagraphProperty.SuppressLineNumbers, 1);
                return true;
            case "hyphpar":
                Para(WordParagraphProperty.SuppressAutoHyphens, 1 - toggle);
                return true;
            case "nosnaplinegrid":
                Para(WordParagraphProperty.SnapToGrid, 0);
                return true;
            case "indmirror":
                Para(WordParagraphProperty.MirrorIndents, toggle);
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
                AddTabStop(new TabStop(Math.Max(0, WordProperties.Twips(p)), state.TabAlignment, state.TabLeader));
                return true;
            case "tb" when has:
                AddTabStop(new TabStop(Math.Max(0, WordProperties.Twips(p)), TabAlignment.Bar));
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
                state.MutablePara().Shading = WordProperties.Shade(state.Para.Shading, percent: p);
                return true;
            case "cbpat" when has:
                state.MutablePara().Shading = WordProperties.Shade(state.Para.Shading, fill: Color(p));
                return true;
            case "cfpat" when has:
                state.MutablePara().Shading = WordProperties.Shade(state.Para.Shading, patternColor: Color(p));
                return true;
            case "prauth" when has:
                state.ParaChange = (state.ParaChange ?? new FormatChange<ParagraphFormat>(0, null, null)) with { Author = p };
                return true;
            case "prdate" when has:
                state.ParaChange = (state.ParaChange ?? new FormatChange<ParagraphFormat>(0, null, null)) with { Date = WordProperties.Date(p) };
                return true;
            default:
                return FrameWord(word, has, p);
        }
    }

    private void ApplyLineSpacing()
    {
        WordProperties.SetLineSpacing(state.MutablePara(), state.LineSpacing, state.LineMultiple);
    }

    private void AddTabStop(TabStop stop)
    {
        WordProperties.AddTabStop(state.MutablePara(), stop);
        state.TabAlignment = TabAlignment.Left;
        state.TabLeader = TabLeader.None;
    }

    private void Run(WordRunProperty property, int value) => WordProperties.Set(state.MutableChar(), property, value);

    private void Para(WordParagraphProperty property, int value) => WordProperties.Set(state.MutablePara(), property, value);

    /// <summary>A positioned frame's words (<c>\absw</c>, <c>\posx</c>, ...), which make the paragraph a frame.</summary>
    private bool FrameWord(string word, bool has, int p)
    {
        (WordFrameProperty Property, int Value)? change = word switch
        {
            "absw" when has => (WordFrameProperty.Width, p),
            "absh" when has => (WordFrameProperty.Height, p),
            "phmrg" => (WordFrameProperty.HorizontalAnchor, (int)HorizontalAnchor.Margin),
            "phpg" => (WordFrameProperty.HorizontalAnchor, (int)HorizontalAnchor.Page),
            "phcol" => (WordFrameProperty.HorizontalAnchor, (int)HorizontalAnchor.Column),
            "posx" or "posnegx" when has => (WordFrameProperty.HorizontalOffset, p),
            "posxl" => (WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.Start),
            "posxc" => (WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.Center),
            "posxr" => (WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.End),
            "posxi" => (WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.Inside),
            "posxo" => (WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.Outside),
            "pvmrg" => (WordFrameProperty.VerticalAnchor, (int)VerticalAnchor.Margin),
            "pvpg" => (WordFrameProperty.VerticalAnchor, (int)VerticalAnchor.Page),
            "pvpara" => (WordFrameProperty.VerticalAnchor, (int)VerticalAnchor.Paragraph),
            "posy" or "posnegy" when has => (WordFrameProperty.VerticalOffset, p),
            "posyt" => (WordFrameProperty.VerticalAlignment, (int)FloatingAlignment.Start),
            "posyc" => (WordFrameProperty.VerticalAlignment, (int)FloatingAlignment.Center),
            "posyb" => (WordFrameProperty.VerticalAlignment, (int)FloatingAlignment.End),
            "posyin" => (WordFrameProperty.VerticalAlignment, (int)FloatingAlignment.Inside),
            "posyout" => (WordFrameProperty.VerticalAlignment, (int)FloatingAlignment.Outside),
            "dxfrtext" when has => (WordFrameProperty.Distance, p),
            "dfrmtxtx" when has => (WordFrameProperty.DistanceHorizontal, p),
            "dfrmtxty" when has => (WordFrameProperty.DistanceVertical, p),
            "nowrap" => (WordFrameProperty.Wrap, (int)FrameWrap.NotBeside),
            "wraparound" => (WordFrameProperty.Wrap, (int)FrameWrap.Around),
            "wraptight" => (WordFrameProperty.Wrap, (int)FrameWrap.Tight),
            "wrapthrough" => (WordFrameProperty.Wrap, (int)FrameWrap.Through),
            "dropcapli" when has => (WordFrameProperty.DropCapLines, p),
            "dropcapt" when has => (WordFrameProperty.DropCap, (int)(p == 2 ? DropCap.Margin : DropCap.Drop)),
            "abslock" => (WordFrameProperty.AnchorLock, 1),
            _ => null,
        };
        if (change is not { } c)
        {
            return false;
        }

        state.MutablePara().Frame = WordProperties.Set(state.Para.Frame ?? new FramePosition(), c.Property, c.Value);
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
                UpdateBorder(b => b with { Space = Math.Clamp(WordProperties.Twips(p), 0, 31) });
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
            state.MutableChar().Shading = WordProperties.Shade(state.Char.Shading, pattern: kind);
        }
        else if (word.StartsWith("cl", StringComparison.Ordinal))
        {
            UpdateCellShading(s => WordProperties.Shade(s, pattern: kind));
        }
        else
        {
            state.MutablePara().Shading = WordProperties.Shade(state.Para.Shading, pattern: kind);
        }

        return true;
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
                Sect(WordSectionProperty.Start, (int)SectionStart.Continuous);
                return true;
            case "sbkcol":
                Sect(WordSectionProperty.Start, (int)SectionStart.NewColumn);
                return true;
            case "sbkpage":
                Sect(WordSectionProperty.Start, (int)SectionStart.NewPage);
                return true;
            case "sbkeven":
                Sect(WordSectionProperty.Start, (int)SectionStart.EvenPage);
                return true;
            case "sbkodd":
                Sect(WordSectionProperty.Start, (int)SectionStart.OddPage);
                return true;
            case "pgwsxn" when has && p > 0:
                Sect(WordSectionProperty.PageWidth, p);
                return true;
            case "pghsxn" when has && p > 0:
                Sect(WordSectionProperty.PageHeight, p);
                return true;
            case "marglsxn" when has:
                Sect(WordSectionProperty.MarginLeft, p);
                return true;
            case "margrsxn" when has:
                Sect(WordSectionProperty.MarginRight, p);
                return true;
            case "margtsxn" when has:
                Sect(WordSectionProperty.MarginTop, p);
                return true;
            case "margbsxn" when has:
                Sect(WordSectionProperty.MarginBottom, p);
                return true;
            case "guttersxn" when has:
                Sect(WordSectionProperty.Gutter, p);
                return true;
            case "headery" when has:
                Sect(WordSectionProperty.HeaderDistance, p);
                return true;
            case "footery" when has:
                Sect(WordSectionProperty.FooterDistance, p);
                return true;
            case "lndscpsxn":
                Sect(WordSectionProperty.Orientation, (int)PageOrientation.Landscape);
                return true;
            case "titlepg":
                Sect(WordSectionProperty.TitlePage, 1);
                return true;
            case "cols" when has:
                Sect(WordSectionProperty.ColumnCount, p);
                return true;
            case "colsx" when has:
                Sect(WordSectionProperty.ColumnSpacing, p);
                return true;
            case "colno" when has:
                sectionColumn = Math.Clamp(p, 1, 45) - 1;
                return true;
            case "colw" when has:
                SetColumn(c => c with { Width = Math.Max(0, WordProperties.Twips(p)) });
                return true;
            case "colsr" when has:
                SetColumn(c => c with { SpaceAfter = Math.Max(0, WordProperties.Twips(p)) });
                return true;
            case "linebetcol":
                Sect(WordSectionProperty.ColumnSeparator, 1);
                return true;
            case "pgnstarts" when has:
                sectionPageStart = Math.Max(0, p);
                return true;
            case "pgnrestart":
                sectionPageRestart = true;
                return true;
            case "pgndec":
                Sect(WordSectionProperty.PageNumberStyle, (int)NumberStyle.Decimal);
                return true;
            case "pgnucrm":
                Sect(WordSectionProperty.PageNumberStyle, (int)NumberStyle.UpperRoman);
                return true;
            case "pgnlcrm":
                Sect(WordSectionProperty.PageNumberStyle, (int)NumberStyle.LowerRoman);
                return true;
            case "pgnucltr":
                Sect(WordSectionProperty.PageNumberStyle, (int)NumberStyle.UpperLetter);
                return true;
            case "pgnlcltr":
                Sect(WordSectionProperty.PageNumberStyle, (int)NumberStyle.LowerLetter);
                return true;
            case "vertalt":
                Sect(WordSectionProperty.VerticalAlignment, (int)VerticalAlignment.Top);
                return true;
            case "vertalc":
                Sect(WordSectionProperty.VerticalAlignment, (int)VerticalAlignment.Center);
                return true;
            case "vertalb":
                Sect(WordSectionProperty.VerticalAlignment, (int)VerticalAlignment.Bottom);
                return true;
            case "vertalj":
                Sect(WordSectionProperty.VerticalAlignment, (int)VerticalAlignment.Justify);
                return true;
            case "rtlsect":
                Sect(WordSectionProperty.Bidi, 1);
                return true;
            case "pgbrdrt" or "pgbrdrb" or "pgbrdrl" or "pgbrdrr":
                state.Border = BorderTarget.Page;
                return true;

            // Document-wide page setup, which every \sectd starts from.
            case "paperw" when has && p > 0:
                Both(WordSectionProperty.PageWidth, p);
                return true;
            case "paperh" when has && p > 0:
                Both(WordSectionProperty.PageHeight, p);
                return true;
            case "margl" when has:
                Both(WordSectionProperty.MarginLeft, p);
                return true;
            case "margr" when has:
                Both(WordSectionProperty.MarginRight, p);
                return true;
            case "margt" when has:
                Both(WordSectionProperty.MarginTop, p);
                return true;
            case "margb" when has:
                Both(WordSectionProperty.MarginBottom, p);
                return true;
            case "gutter" when has:
                Both(WordSectionProperty.Gutter, p);
                return true;
            case "landscape":
                Both(WordSectionProperty.Orientation, (int)PageOrientation.Landscape);
                return true;
            default:
                return false;
        }

        void Sect(WordSectionProperty property, int value) => WordProperties.Set(s, property, value);

        void Both(WordSectionProperty property, int value)
        {
            WordProperties.Set(documentSetup, property, value);
            WordProperties.Set(sectionSetup, property, value);
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
                document.Styles.DefaultRunFormat.Language ??= WordProperties.Language(p);
                return true;
            case "deflangfe" when has:
                document.Styles.DefaultRunFormat.LanguageEastAsia ??= WordProperties.Language(p);
                return true;
            case "deftab" when has && p > 0:
                settings.DefaultTabStop = WordProperties.Twips(p);
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
                WordProperties.Set(settings, WordNoteProperty.FootnoteStart, p);
                return true;
            case "aftnstart" when has && p >= 1:
                WordProperties.Set(settings, WordNoteProperty.EndnoteStart, p);
                return true;
            case "ftnnar" or "ftnnalc" or "ftnnauc" or "ftnnrlc" or "ftnnruc":
                WordProperties.Set(settings, WordNoteProperty.FootnoteNumberStyle, (int)NoteNumberStyle(word[4..]));
                return true;
            case "aftnnar" or "aftnnalc" or "aftnnauc" or "aftnnrlc" or "aftnnruc":
                WordProperties.Set(settings, WordNoteProperty.EndnoteNumberStyle, (int)NoteNumberStyle(word[5..]));
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
