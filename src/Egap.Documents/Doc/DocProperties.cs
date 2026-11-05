using System.Buffers.Binary;

using Egami.Pixels;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>A run's character properties as the sprms leave them: its format, and what the format does not hold.</summary>
internal sealed class DocChp
{
    public RunFormat Format { get; set; } = new();

    /// <summary>The character style's istd, or -1 for the default paragraph font.</summary>
    public int Style { get; set; } = -1;

    /// <summary>sprmCFSpec: the run's characters are special (a picture, a note mark, a field character).</summary>
    public bool Special { get; set; }

    /// <summary>sprmCPicLocation: where a picture's data lies, or -1.</summary>
    public int PicLocation { get; set; } = -1;

    public bool Ole2 { get; set; }

    public bool Data { get; set; }

    public bool Obj { get; set; }

    public bool FieldVanish { get; set; }

    public bool Inserted { get; set; }

    public bool Deleted { get; set; }

    public int InsertAuthor { get; set; }

    public int DeleteAuthor { get; set; }

    public int InsertDate { get; set; }

    public int DeleteDate { get; set; }

    /// <summary>sprmCSymbol: the font index and the character a symbol run shows.</summary>
    public (int Font, char Char)? Symbol { get; set; }

    public DocChp Clone()
    {
        DocChp copy = (DocChp)MemberwiseClone();
        copy.Format = new RunFormat();
        copy.Format.CopyRaw(Format);
        return copy;
    }
}

/// <summary>A paragraph's properties as the sprms leave them: its format, and its table and list place.</summary>
internal sealed class DocPap
{
    public ParagraphFormat Format { get; set; } = new();

    /// <summary>The paragraph style's istd.</summary>
    public int Style { get; set; }

    public bool InTable { get; set; }

    public bool Ttp { get; set; }

    public bool InnerCell { get; set; }

    public bool InnerTtp { get; set; }

    public int Itap { get; set; }

    public int? Ilfo { get; set; }

    public int? Ilvl { get; set; }

    /// <summary>A Word 6 or Word 95 list (sprmPAnld) and its level (sprmPNLvlAnm), or null.</summary>
    public byte[]? Anld { get; set; }

    public int NLvlAnm { get; set; }

    /// <summary>A Word 97 paragraph's sprmPHugePapx: where its grpprl lies in the Data stream, or -1.</summary>
    public long HugePapx { get; set; } = -1;

    /// <summary>The logical (bidi-aware) alignment and indents seen, which the physical ones written for older readers do not override.</summary>
    public int Logical { get; set; }

    public FramePosition? Frame { get; set; }

    public DocPap Clone()
    {
        DocPap copy = (DocPap)MemberwiseClone();
        copy.Format = new ParagraphFormat();
        copy.Format.CopyRaw(Format);
        return copy;
    }
}

/// <summary>A section's properties: its page setup and its page numbering restart.</summary>
internal sealed class DocSep
{
    public PageSetup Setup { get; } = new();

    public bool PageRestart { get; set; }

    public int PageStart { get; set; } = 1;
}

/// <summary>
/// Applies sprms ([MS-DOC] 2.6) to the model through the shared Word property stream, in Word's own units: character
/// sprms to a run, paragraph sprms to a paragraph, section sprms to a page setup and table sprms to a row definition.
/// </summary>
internal static class DocProperties
{
    private const int LogicalAlignment = 1;
    private const int LogicalLeft = 2;
    private const int LogicalRight = 4;
    private const int LogicalFirst = 8;

    // The Ico colours ([MS-DOC] 2.9.119): 0 automatic, then black, blue, cyan, green, magenta, red, yellow, white, and the
    // dark ones and greys.
    private static readonly (byte R, byte G, byte B)[] Icos =
    [
        (0, 0, 0), (0, 0, 0), (0, 0, 0xFF), (0, 0xFF, 0xFF), (0, 0xFF, 0), (0xFF, 0, 0xFF), (0xFF, 0, 0), (0xFF, 0xFF, 0), (0xFF, 0xFF, 0xFF),
        (0, 0, 0x80), (0, 0x80, 0x80), (0, 0x80, 0), (0x80, 0, 0x80), (0x80, 0, 0), (0x80, 0x80, 0), (0x80, 0x80, 0x80), (0xC0, 0xC0, 0xC0),
    ];

    // ---- Characters ([MS-DOC] 2.6.1) ----

    /// <summary>
    /// Applies a character grpprl. <paramref name="style"/> gives the style's value of a toggle property, for the
    /// ToggleOperand values $80 (the style's value) and $81 (its opposite); <paramref name="plain"/> is what sprmCPlain
    /// resets the run to.
    /// </summary>
    public static void ApplyChp(DocChp chp, ReadOnlySpan<byte> grpprl, DocContext context, Func<WordRunProperty, bool> style, Func<DocChp>? plain = null)
    {
        DocSprms sprms = new(grpprl, context.Word6);
        foreach (DocSprms prl in sprms)
        {
            ApplyChp(chp, prl.Sprm, prl.Operand, context, style, plain);
        }
    }

    public static void ApplyChp(DocChp chp, ushort sprm, ReadOnlySpan<byte> operand, DocContext context, Func<WordRunProperty, bool> style, Func<DocChp>? plain = null)
    {
        RunFormat run = chp.Format;
        switch (sprm)
        {
            case 0x0835:
                Toggle(run, WordRunProperty.Bold, operand, style);
                break;
            case 0x0836:
                Toggle(run, WordRunProperty.Italic, operand, style);
                break;
            case 0x0837:
                Toggle(run, WordRunProperty.Strikethrough, operand, style);
                break;
            case 0x0838:
                Toggle(run, WordRunProperty.Outline, operand, style);
                break;
            case 0x0839:
                Toggle(run, WordRunProperty.Shadow, operand, style);
                break;
            case 0x083A:
                Toggle(run, WordRunProperty.SmallCaps, operand, style);
                break;
            case 0x083B:
                Toggle(run, WordRunProperty.AllCaps, operand, style);
                break;
            case 0x083C:
                Toggle(run, WordRunProperty.Hidden, operand, style);
                break;
            case 0x2A53:
                Toggle(run, WordRunProperty.DoubleStrikethrough, operand, style);
                break;
            case 0x0854:
                Toggle(run, WordRunProperty.Imprint, operand, style);
                break;
            case 0x0858:
                Toggle(run, WordRunProperty.Emboss, operand, style);
                break;
            case 0x085A:
                Toggle(run, WordRunProperty.RightToLeft, operand, style);
                break;
            case 0x085C:
                Toggle(run, WordRunProperty.BoldComplexScript, operand, style);
                break;
            case 0x085D:
                Toggle(run, WordRunProperty.ItalicComplexScript, operand, style);
                break;
            case 0x0875:
                Toggle(run, WordRunProperty.NoProof, operand, style);
                break;
            case 0x0818:
                Toggle(run, WordRunProperty.SpecialHidden, operand, style);
                break;
            case 0x0882:
                Toggle(run, WordRunProperty.ComplexScript, operand, style);
                break;
            case 0x2A3E:
                WordProperties.Set(run, WordRunProperty.Underline, (int)Underline(operand[0]));
                break;
            case 0x2A42:
                WordProperties.Set(run, WordRunColor.Color, Ico(operand[0]));
                break;
            case 0x6870:
                WordProperties.Set(run, WordRunColor.Color, ColorRef(operand));
                break;
            case 0x6877:
                WordProperties.Set(run, WordRunColor.UnderlineColor, ColorRef(operand));
                break;
            case 0x4A43:
                WordProperties.Set(run, WordRunProperty.Size, U16(operand));
                break;
            case 0x4A61:
                WordProperties.Set(run, WordRunProperty.SizeComplexScript, U16(operand));
                break;
            case 0x4845:
                WordProperties.Set(run, WordRunProperty.Position, S16(operand));
                break;
            case 0x8840:
                WordProperties.Set(run, WordRunProperty.Spacing, S16(operand));
                break;
            case 0x484B:
                WordProperties.Set(run, WordRunProperty.Kerning, S16(operand));
                break;
            case 0x4852:
                WordProperties.Set(run, WordRunProperty.Scale, U16(operand));
                break;
            case 0x2A48:
                WordProperties.Set(run, WordRunProperty.VerticalPosition, operand[0] <= 2 ? operand[0] : 0);
                break;
            case 0x2A0C:
                if (operand[0] == 0)
                {
                    WordProperties.Clear(run, WordRunProperty.Highlight);
                }
                else
                {
                    WordProperties.Set(run, WordRunProperty.Highlight, operand[0] <= 16 ? operand[0] : 0);
                }

                break;
            case 0x486D or 0x4873:
                WordProperties.Set(run, WordRunProperty.Language, U16(operand));
                break;
            case 0x486E or 0x4874:
                WordProperties.Set(run, WordRunProperty.LanguageEastAsia, U16(operand));
                break;
            case 0x485F:
                WordProperties.Set(run, WordRunProperty.LanguageBidi, U16(operand));
                break;
            case 0x4A4F:
                Font(run, context.Word6 ? WordFontSlot.Latin : WordFontSlot.Ascii, U16(operand), context);
                break;
            case 0x4A50:
                Font(run, WordFontSlot.EastAsia, U16(operand), context);
                break;
            case 0x4A51:
                Font(run, WordFontSlot.HighAnsi, U16(operand), context);
                break;
            case 0x4A5E:
                Font(run, WordFontSlot.ComplexScript, U16(operand), context);
                break;
            case 0x4A30:
                chp.Style = U16(operand);
                break;
            case 0x2A33:
                // sprmCPlain: the run's own formatting goes; what the paragraph style gives stays.
                if (plain?.Invoke() is { } reset)
                {
                    chp.Format = reset.Format;
                    chp.Style = reset.Style;
                }
                else
                {
                    run.ClearFormatting();
                    chp.Style = -1;
                }

                break;
            case 0x2A32:
                // sprmCDefault: the toggles and the underline go back to the style's.
                foreach (WordRunProperty property in (ReadOnlySpan<WordRunProperty>)[WordRunProperty.Bold, WordRunProperty.Italic, WordRunProperty.Strikethrough,
                    WordRunProperty.Outline, WordRunProperty.Shadow, WordRunProperty.SmallCaps, WordRunProperty.AllCaps, WordRunProperty.Hidden,
                    WordRunProperty.DoubleStrikethrough, WordRunProperty.Imprint, WordRunProperty.Emboss])
                {
                    ClearToggle(run, property);
                }

                run.Underline = null;
                break;
            case 0x4866:
                if (operand.Length >= 2)
                {
                    run.Shading = Shd80(U16(operand));
                }

                break;
            case 0xCA71:
                if (operand.Length >= 11)
                {
                    run.Shading = Shd(operand[1..]);
                }

                break;
            case 0x6865:
                run.Border = Brc80(operand);
                break;
            case 0xCA72:
                if (operand.Length >= 9)
                {
                    run.Border = Brc(operand[1..]);
                }

                break;
            case 0x0855:
                chp.Special = On(operand);
                break;
            case 0x6A03:
                if (operand.Length >= 4)
                {
                    chp.PicLocation = BinaryPrimitives.ReadInt32LittleEndian(operand[^4..]);
                }

                break;
            case 0x080A:
                chp.Ole2 = On(operand);
                break;
            case 0x0806:
                chp.Data = On(operand);
                break;
            case 0x0856:
                chp.Obj = On(operand);
                break;
            case 0x0802:
                chp.FieldVanish = On(operand);
                break;
            case 0x0800:
                chp.Deleted = On(operand);
                break;
            case 0x0801:
                chp.Inserted = On(operand);
                break;
            case 0x4804:
                chp.InsertAuthor = S16(operand);
                break;
            case 0x6805:
                chp.InsertDate = BinaryPrimitives.ReadInt32LittleEndian(operand);
                break;
            case 0x4863:
                chp.DeleteAuthor = S16(operand);
                break;
            case 0x6864:
                chp.DeleteDate = BinaryPrimitives.ReadInt32LittleEndian(operand);
                break;
            case 0x6A09:
                chp.Symbol = (U16(operand), (char)BinaryPrimitives.ReadUInt16LittleEndian(operand[2..]));
                break;
        }
    }

    // ---- Paragraphs ([MS-DOC] 2.6.2) ----

    /// <summary>Applies a paragraph grpprl; table sprms go to <paramref name="row"/> when one is given.</summary>
    public static void ApplyPap(DocPap pap, ReadOnlySpan<byte> grpprl, DocContext context, DocRow? row = null)
    {
        DocSprms sprms = new(grpprl, context.Word6);
        foreach (DocSprms prl in sprms)
        {
            ushort sprm = prl.Sprm;
            if ((sprm & 0x1C00) == 0x1400)
            {
                // sgc 5: a table sprm.
                row?.Add(sprm, prl.Operand);
                continue;
            }

            ApplyPap(pap, sprm, prl.Operand, context);
        }
    }

    public static void ApplyPap(DocPap pap, ushort sprm, ReadOnlySpan<byte> operand, DocContext context)
    {
        ParagraphFormat format = pap.Format;
        switch (sprm)
        {
            case 0x4600:
                pap.Style = U16(operand);
                break;
            case 0x2403 when (pap.Logical & LogicalAlignment) == 0:
            case 0x2461:
                if (sprm == 0x2461)
                {
                    pap.Logical |= LogicalAlignment;
                }

                (ParagraphAlignment alignment, KashidaLevel? kashida) = operand[0] switch
                {
                    1 => (ParagraphAlignment.Center, (KashidaLevel?)null),
                    2 => (ParagraphAlignment.Right, null),
                    3 => (ParagraphAlignment.Justify, null),
                    4 => (ParagraphAlignment.Distribute, null),
                    5 => (ParagraphAlignment.Justify, KashidaLevel.Medium),
                    7 => (ParagraphAlignment.Justify, KashidaLevel.High),
                    8 => (ParagraphAlignment.Justify, KashidaLevel.Low),
                    9 => (ParagraphAlignment.Distribute, null),
                    _ => (ParagraphAlignment.Left, null),
                };
                WordProperties.Set(format, WordParagraphProperty.Alignment, (int)alignment);
                if (kashida is { } level)
                {
                    WordProperties.Set(format, WordParagraphProperty.Kashida, (int)level);
                }

                break;
            case 0x2405:
                WordProperties.Set(format, WordParagraphProperty.KeepTogether, operand[0]);
                break;
            case 0x2406:
                WordProperties.Set(format, WordParagraphProperty.KeepWithNext, operand[0]);
                break;
            case 0x2407:
                WordProperties.Set(format, WordParagraphProperty.PageBreakBefore, operand[0]);
                break;
            case 0x2431:
                WordProperties.Set(format, WordParagraphProperty.WidowControl, operand[0]);
                break;
            case 0x242A:
                WordProperties.Set(format, WordParagraphProperty.SuppressAutoHyphens, operand[0]);
                break;
            case 0x240C:
                WordProperties.Set(format, WordParagraphProperty.SuppressLineNumbers, operand[0]);
                break;
            case 0x246D:
                WordProperties.Set(format, WordParagraphProperty.ContextualSpacing, operand[0]);
                break;
            case 0x2470:
                WordProperties.Set(format, WordParagraphProperty.MirrorIndents, operand[0]);
                break;
            case 0x2441:
                WordProperties.Set(format, WordParagraphProperty.Bidi, operand[0]);
                break;
            case 0x245B:
                WordProperties.Set(format, WordParagraphProperty.SpaceBeforeAuto, operand[0]);
                break;
            case 0x245C:
                WordProperties.Set(format, WordParagraphProperty.SpaceAfterAuto, operand[0]);
                break;
            case 0x2640:
                if (operand[0] <= 8)
                {
                    WordProperties.Set(format, WordParagraphProperty.OutlineLevel, operand[0]);
                }
                else
                {
                    format.OutlineLevel = null;
                }

                break;
            case 0x840E when (pap.Logical & LogicalRight) == 0:
            case 0x845D:
                pap.Logical |= sprm == 0x845D ? LogicalRight : 0;
                WordProperties.Set(format, WordParagraphProperty.RightIndent, S16(operand));
                break;
            case 0x840F when (pap.Logical & LogicalLeft) == 0:
            case 0x845E:
                pap.Logical |= sprm == 0x845E ? LogicalLeft : 0;
                WordProperties.Set(format, WordParagraphProperty.LeftIndent, S16(operand));
                break;
            case 0x8411 when (pap.Logical & LogicalFirst) == 0:
            case 0x8460:
                pap.Logical |= sprm == 0x8460 ? LogicalFirst : 0;
                WordProperties.Set(format, WordParagraphProperty.FirstLineIndent, S16(operand));
                break;
            case 0x4610 or 0x465F:
                // sprmPNest: the left indent moves by the operand.
                WordProperties.Set(format, WordParagraphProperty.LeftIndent, Math.Max(0, ((format.LeftIndent ?? 0) * 20) + S16(operand)));
                break;
            case 0xA413:
                WordProperties.Set(format, WordParagraphProperty.SpaceBefore, U16(operand));
                break;
            case 0xA414:
                WordProperties.Set(format, WordParagraphProperty.SpaceAfter, U16(operand));
                break;
            case 0x6412:
                // LSPD: dyaLine, then fMultLinespace.
                WordProperties.SetLineSpacing(format, S16(operand), BinaryPrimitives.ReadInt16LittleEndian(operand[2..]) == 1);
                break;
            case 0xC60D or 0xC615:
                ChangeTabs(format, operand, close: sprm == 0xC615);
                break;
            case 0x2416:
                pap.InTable = On(operand);
                break;
            case 0x2417:
                pap.Ttp = On(operand);
                break;
            case 0x6649:
                pap.Itap = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(operand));
                break;
            case 0x664A:
                pap.Itap = Math.Max(0, pap.Itap + BinaryPrimitives.ReadInt32LittleEndian(operand));
                break;
            case 0x244B:
                pap.InnerCell = On(operand);
                break;
            case 0x244C:
                pap.InnerTtp = On(operand);
                break;
            case 0x260A:
                pap.Ilvl = Math.Min((int)operand[0], 8);
                break;
            case 0x460B:
                pap.Ilfo = S16(operand);
                break;
            case 0xC63E:
                pap.Anld = operand.Length > 1 ? operand[1..].ToArray() : null;
                break;
            case 0x240D:
                pap.NLvlAnm = operand[0];
                break;
            case 0x6646:
                pap.HugePapx = BinaryPrimitives.ReadUInt32LittleEndian(operand);
                break;
            case 0x6424:
                format.BorderTop = Brc80(operand);
                break;
            case 0x6425:
                format.BorderLeft = Brc80(operand);
                break;
            case 0x6426:
                format.BorderBottom = Brc80(operand);
                break;
            case 0x6427:
                format.BorderRight = Brc80(operand);
                break;
            case 0x6428:
                format.BorderBetween = Brc80(operand);
                break;
            case 0x6629:
                format.BorderBar = Brc80(operand);
                break;
            case 0xC64E or 0xC64F or 0xC650 or 0xC651 or 0xC652 or 0xC653 when operand.Length >= 9:
                Border border = Brc(operand[1..]);
                _ = sprm switch
                {
                    0xC64E => format.BorderTop = border,
                    0xC64F => format.BorderLeft = border,
                    0xC650 => format.BorderBottom = border,
                    0xC651 => format.BorderRight = border,
                    0xC652 => format.BorderBetween = border,
                    _ => format.BorderBar = border,
                };
                break;
            case 0x442D:
                format.Shading = Shd80(U16(operand));
                break;
            case 0xC64D when operand.Length >= 11:
                format.Shading = Shd(operand[1..]);
                break;
            case 0x261B:
                {
                    // PositionCodeOperand: pcVert (bits 4-5: margin, page, paragraph, none) and pcHorz (bits 6-7: column,
                    // margin, page, none).
                    FramePosition frame = pap.Frame ?? new FramePosition();
                    int vertical = (operand[0] >> 4) & 3;
                    int horizontal = (operand[0] >> 6) & 3;
                    if (vertical != 3)
                    {
                        frame = WordProperties.Set(frame, WordFrameProperty.VerticalAnchor, (int)(vertical switch { 0 => VerticalAnchor.Margin, 1 => VerticalAnchor.Page, _ => VerticalAnchor.Paragraph }));
                    }

                    if (horizontal != 3)
                    {
                        frame = WordProperties.Set(frame, WordFrameProperty.HorizontalAnchor, (int)(horizontal switch { 0 => HorizontalAnchor.Column, 1 => HorizontalAnchor.Margin, _ => HorizontalAnchor.Page }));
                    }

                    pap.Frame = frame;
                    break;
                }

            case 0x8418:
                pap.Frame = Position(pap.Frame ?? new FramePosition(), S16(operand), horizontal: true);
                break;
            case 0x8419:
                pap.Frame = Position(pap.Frame ?? new FramePosition(), S16(operand), horizontal: false);
                break;
            case 0x841A:
                pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.Width, S16(operand));
                break;
            case 0x442B:
                {
                    // WHeightAbs: 15 bits of height, then fMinHeight.
                    int value = U16(operand);
                    int height = value & 0x7FFF;
                    pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.Height, (value & 0x8000) != 0 ? height : -height);
                    break;
                }

            case 0x842E:
                pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.DistanceVertical, Math.Max(0, S16(operand)));
                break;
            case 0x842F:
                pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.DistanceHorizontal, Math.Max(0, S16(operand)));
                break;
            case 0x2423:
                pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.Wrap, (int)(operand[0] switch
                {
                    1 => FrameWrap.NotBeside,
                    2 => FrameWrap.Around,
                    3 => FrameWrap.None,
                    4 => FrameWrap.Tight,
                    5 => FrameWrap.Through,
                    _ => FrameWrap.Auto,
                }));
                break;
            case 0x442C:
                {
                    // DCS: fdct (3 bits: none, drop, margin), then the lines (5 bits).
                    int dcs = U16(operand);
                    int kind = dcs & 7;
                    if (kind is 1 or 2)
                    {
                        FramePosition frame = pap.Frame ?? new FramePosition();
                        frame = WordProperties.Set(frame, WordFrameProperty.DropCap, (int)(kind == 1 ? DropCap.Drop : DropCap.Margin));
                        pap.Frame = WordProperties.Set(frame, WordFrameProperty.DropCapLines, Math.Max(1, (dcs >> 3) & 0x1F));
                    }

                    break;
                }

            case 0x2430:
                pap.Frame = WordProperties.Set(pap.Frame ?? new FramePosition(), WordFrameProperty.AnchorLock, operand[0]);
                break;
        }
    }

    // XAS_plusOne and YAS_plusOne: 0 and the negative codes align the frame; otherwise the position plus one.
    private static FramePosition Position(FramePosition frame, int value, bool horizontal)
    {
        FloatingAlignment? alignment = (horizontal, value) switch
        {
            (true, 0) => FloatingAlignment.Start,
            (_, -4) => horizontal ? FloatingAlignment.Center : FloatingAlignment.Start,
            (_, -8) => horizontal ? FloatingAlignment.End : FloatingAlignment.Center,
            (_, -12) => horizontal ? FloatingAlignment.Inside : FloatingAlignment.End,
            (_, -16) => horizontal ? FloatingAlignment.Outside : FloatingAlignment.Inside,
            (false, -20) => FloatingAlignment.Outside,
            _ => null,
        };
        return alignment is { } align
            ? WordProperties.Set(frame, horizontal ? WordFrameProperty.HorizontalAlignment : WordFrameProperty.VerticalAlignment, (int)align)
            : WordProperties.Set(frame, horizontal ? WordFrameProperty.HorizontalOffset : WordFrameProperty.VerticalOffset, value == 0 ? 0 : value - 1);
    }

    // PChgTabsPapxOperand and PChgTabsOperand ([MS-DOC] 2.9.183, 2.9.184): the size, the deletions (and, for sprmPChgTabs,
    // the range each deletion clears), then the additions with a TBD each (jc in bits 0-2, tlc in bits 3-5). A deletion is
    // kept as a clearing tab, so the paragraph can take away a style's tab.
    private static void ChangeTabs(ParagraphFormat format, ReadOnlySpan<byte> operand, bool close)
    {
        if (operand.Length < 2)
        {
            return;
        }

        ReadOnlySpan<byte> rest = operand[1..];
        int deleted = rest[0];
        int at = 1;
        List<TabStop> stops = [.. format.TabStops ?? []];
        int width = close ? 4 : 2;
        for (int i = 0; i < deleted && at + (2 * i) + 2 <= rest.Length; i++)
        {
            int position = BinaryPrimitives.ReadInt16LittleEndian(rest[(at + (2 * i))..]);
            stops.RemoveAll(t => t.Position == WordProperties.Twips(position));
            stops.Add(new TabStop(WordProperties.Twips(position), TabAlignment.Clear));
        }

        at += width * deleted;
        if (at < rest.Length)
        {
            int added = rest[at];
            at++;
            for (int i = 0; i < added && at + (2 * added) + i < rest.Length; i++)
            {
                int position = BinaryPrimitives.ReadInt16LittleEndian(rest[(at + (2 * i))..]);
                byte tbd = rest[at + (2 * added) + i];
                TabAlignment alignment = (tbd & 7) switch
                {
                    1 => TabAlignment.Center,
                    2 => TabAlignment.Right,
                    3 => TabAlignment.Decimal,
                    4 => TabAlignment.Bar,
                    _ => TabAlignment.Left,
                };
                TabLeader leader = ((tbd >> 3) & 7) switch
                {
                    1 => TabLeader.Dot,
                    2 => TabLeader.Hyphen,
                    3 => TabLeader.Underscore,
                    4 => TabLeader.Heavy,
                    5 => TabLeader.MiddleDot,
                    _ => TabLeader.None,
                };
                double place = WordProperties.Twips(position);
                stops.RemoveAll(t => t.Position == place);
                stops.Add(new TabStop(place, alignment, leader));
            }
        }

        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        WordProperties.SetTabStops(format, stops);
    }

    // ---- Sections ([MS-DOC] 2.6.4) ----

    /// <summary>The section properties before a SEPX changes them ([MS-DOC] 2.9.253, Sep's defaults).</summary>
    public static DocSep DefaultSep()
    {
        DocSep sep = new();
        PageSetup setup = sep.Setup;
        WordProperties.Set(setup, WordSectionProperty.Start, (int)SectionStart.NewPage);
        WordProperties.Set(setup, WordSectionProperty.PageWidth, 12240);
        WordProperties.Set(setup, WordSectionProperty.PageHeight, 15840);
        WordProperties.Set(setup, WordSectionProperty.MarginLeft, 1800);
        WordProperties.Set(setup, WordSectionProperty.MarginRight, 1800);
        WordProperties.Set(setup, WordSectionProperty.MarginTop, 1440);
        WordProperties.Set(setup, WordSectionProperty.MarginBottom, 1440);
        WordProperties.Set(setup, WordSectionProperty.HeaderDistance, 720);
        WordProperties.Set(setup, WordSectionProperty.FooterDistance, 720);
        WordProperties.Set(setup, WordSectionProperty.ColumnSpacing, 720);
        return sep;
    }

    public static void ApplySep(DocSep sep, ReadOnlySpan<byte> grpprl, DocContext context)
    {
        PageSetup setup = sep.Setup;
        DocSprms sprms = new(grpprl, context.Word6);
        foreach (DocSprms prl in sprms)
        {
            ReadOnlySpan<byte> operand = prl.Operand;
            switch (prl.Sprm)
            {
                case 0x3009:
                    WordProperties.Set(setup, WordSectionProperty.Start, (int)(operand[0] switch
                    {
                        0 => SectionStart.Continuous,
                        1 => SectionStart.NewColumn,
                        3 => SectionStart.EvenPage,
                        4 => SectionStart.OddPage,
                        _ => SectionStart.NewPage,
                    }));
                    break;
                case 0x300A:
                    WordProperties.Set(setup, WordSectionProperty.TitlePage, operand[0]);
                    break;
                case 0x500B:
                    WordProperties.Set(setup, WordSectionProperty.ColumnCount, U16(operand) + 1);
                    break;
                case 0x900C:
                    WordProperties.Set(setup, WordSectionProperty.ColumnSpacing, S16(operand));
                    break;
                case 0x3019:
                    WordProperties.Set(setup, WordSectionProperty.ColumnSeparator, operand[0]);
                    break;
                case 0x3005:
                    WordProperties.Set(setup, WordSectionProperty.EqualColumnWidths, operand[0]);
                    break;
                case 0x300E:
                    WordProperties.Set(setup, WordSectionProperty.PageNumberStyle, (int)WordProperties.NumberStyleOf(operand[0]));
                    break;
                case 0x3011:
                    sep.PageRestart = On(operand);
                    break;
                case 0x501C:
                    sep.PageStart = U16(operand);
                    break;
                case 0x7044:
                    sep.PageStart = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(operand), int.MaxValue);
                    break;
                case 0x301D:
                    WordProperties.Set(setup, WordSectionProperty.Orientation, (int)(operand[0] == 2 ? PageOrientation.Landscape : PageOrientation.Portrait));
                    break;
                case 0xB01F:
                    WordProperties.Set(setup, WordSectionProperty.PageWidth, U16(operand));
                    break;
                case 0xB020:
                    WordProperties.Set(setup, WordSectionProperty.PageHeight, U16(operand));
                    break;
                case 0xB021:
                    WordProperties.Set(setup, WordSectionProperty.MarginLeft, S16(operand));
                    break;
                case 0xB022:
                    WordProperties.Set(setup, WordSectionProperty.MarginRight, S16(operand));
                    break;
                case 0x9023:
                    WordProperties.Set(setup, WordSectionProperty.MarginTop, S16(operand));
                    break;
                case 0x9024:
                    WordProperties.Set(setup, WordSectionProperty.MarginBottom, S16(operand));
                    break;
                case 0xB025:
                    WordProperties.Set(setup, WordSectionProperty.Gutter, U16(operand));
                    break;
                case 0xB017:
                    WordProperties.Set(setup, WordSectionProperty.HeaderDistance, S16(operand));
                    break;
                case 0xB018:
                    WordProperties.Set(setup, WordSectionProperty.FooterDistance, S16(operand));
                    break;
                case 0x301A:
                    WordProperties.Set(setup, WordSectionProperty.VerticalAlignment, (int)(operand[0] switch
                    {
                        1 => VerticalAlignment.Center,
                        2 => VerticalAlignment.Justify,
                        3 => VerticalAlignment.Bottom,
                        _ => VerticalAlignment.Top,
                    }));
                    break;
                case 0x3228:
                    WordProperties.Set(setup, WordSectionProperty.Bidi, operand[0]);
                    break;
                case 0x5033:
                    WordProperties.Set(setup, WordSectionProperty.TextDirection, (int)(U16(operand) switch
                    {
                        1 or 5 => TextDirection.TopToBottom,
                        3 => TextDirection.BottomToTop,
                        _ => TextDirection.Horizontal,
                    }));
                    break;
            }
        }
    }

    // ---- Values ----

    /// <summary>A toggle property from a ToggleOperand: 0 off, 1 on, $80 the style's value, $81 its opposite.</summary>
    private static void Toggle(RunFormat run, WordRunProperty property, ReadOnlySpan<byte> operand, Func<WordRunProperty, bool> style)
    {
        switch (operand[0])
        {
            case 0:
                WordProperties.Set(run, property, 0);
                break;
            case 1:
                WordProperties.Set(run, property, 1);
                break;
            case 0x80:
                ClearToggle(run, property);
                break;
            case 0x81:
                WordProperties.Set(run, property, style(property) ? 0 : 1);
                break;
        }
    }

    private static void ClearToggle(RunFormat run, WordRunProperty property)
    {
        switch (property)
        {
            case WordRunProperty.Bold:
                run.Bold = null;
                break;
            case WordRunProperty.Italic:
                run.Italic = null;
                break;
            case WordRunProperty.Strikethrough:
                run.Strikethrough = null;
                break;
            case WordRunProperty.Outline:
                run.Outline = null;
                break;
            case WordRunProperty.Shadow:
                run.Shadow = null;
                break;
            case WordRunProperty.SmallCaps:
                run.SmallCaps = null;
                break;
            case WordRunProperty.AllCaps:
                run.AllCaps = null;
                break;
            case WordRunProperty.Hidden:
                run.Hidden = null;
                break;
            case WordRunProperty.DoubleStrikethrough:
                run.DoubleStrikethrough = null;
                break;
            case WordRunProperty.Imprint:
                run.Imprint = null;
                break;
            case WordRunProperty.Emboss:
                run.Emboss = null;
                break;
            case WordRunProperty.BoldComplexScript:
                run.BoldComplexScript = null;
                break;
            case WordRunProperty.ItalicComplexScript:
                run.ItalicComplexScript = null;
                break;
            default:
                WordProperties.Clear(run, property);
                break;
        }
    }

    /// <summary>A toggle property's value in a resolved format: off when the format does not set it.</summary>
    public static bool ToggleOf(RunFormat format, WordRunProperty property) => property switch
    {
        WordRunProperty.Bold => format.Bold ?? false,
        WordRunProperty.Italic => format.Italic ?? false,
        WordRunProperty.Strikethrough => format.Strikethrough ?? false,
        WordRunProperty.Outline => format.Outline ?? false,
        WordRunProperty.Shadow => format.Shadow ?? false,
        WordRunProperty.SmallCaps => format.SmallCaps ?? false,
        WordRunProperty.AllCaps => format.AllCaps ?? false,
        WordRunProperty.Hidden => format.Hidden ?? false,
        WordRunProperty.DoubleStrikethrough => format.DoubleStrikethrough ?? false,
        WordRunProperty.Imprint => format.Imprint ?? false,
        WordRunProperty.Emboss => format.Emboss ?? false,
        WordRunProperty.BoldComplexScript => format.BoldComplexScript ?? false,
        WordRunProperty.ItalicComplexScript => format.ItalicComplexScript ?? false,
        WordRunProperty.RightToLeft => format.RightToLeft ?? false,
        WordRunProperty.NoProof => format.NoProof ?? false,
        WordRunProperty.SpecialHidden => format.SpecialHidden ?? false,
        WordRunProperty.ComplexScript => format.ComplexScript ?? false,
        _ => false,
    };

    private static void Font(RunFormat run, WordFontSlot slot, int index, DocContext context)
    {
        if (index >= 0 && index < context.Fonts.Count && context.Fonts[index] is { Length: > 0 } name)
        {
            WordProperties.SetFont(run, slot, name);
        }
    }

    // The Kul underline codes ([MS-DOC] 2.9.135).
    private static UnderlineStyle Underline(byte kul) => kul switch
    {
        0 => UnderlineStyle.None,
        2 => UnderlineStyle.Words,
        3 => UnderlineStyle.Double,
        4 => UnderlineStyle.Dotted,
        6 => UnderlineStyle.Thick,
        7 => UnderlineStyle.Dash,
        9 => UnderlineStyle.DotDash,
        10 => UnderlineStyle.DotDotDash,
        11 => UnderlineStyle.Wave,
        0x14 => UnderlineStyle.DottedHeavy,
        0x17 => UnderlineStyle.DashedHeavy,
        0x19 => UnderlineStyle.DashDotHeavy,
        0x1A => UnderlineStyle.DashDotDotHeavy,
        0x1B => UnderlineStyle.WavyHeavy,
        0x27 => UnderlineStyle.DashLong,
        0x2B => UnderlineStyle.WavyDouble,
        0x37 => UnderlineStyle.DashLongHeavy,
        _ => UnderlineStyle.Single,
    };

    /// <summary>An Ico colour; 0 is automatic.</summary>
    public static DocumentColor Ico(int ico)
    {
        if (ico <= 0 || ico >= Icos.Length)
        {
            return DocumentColor.Auto;
        }

        (byte r, byte g, byte b) = Icos[ico];
        return DocumentColor.FromRgb(new Rgba8(r, g, b, 255));
    }

    /// <summary>A COLORREF ([MS-DOC] 2.9.43): red, green, blue and fAuto ($FF for automatic).</summary>
    public static DocumentColor ColorRef(ReadOnlySpan<byte> value)
    {
        return value.Length < 4 || value[3] == 0xFF ? DocumentColor.Auto : DocumentColor.FromRgb(new Rgba8(value[0], value[1], value[2], 255));
    }

    // Brc80 ([MS-DOC] 2.9.17): dptLineWidth in eighths of a point, brcType, ico, then dptSpace (5 bits, points), fShadow and
    // fFrame. $FFFFFFFF is no border.
    public static Border Brc80(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(value) == 0xFFFFFFFF)
        {
            return Border.None;
        }

        return MakeBorder(value[1], value[0], Ico(value[2]), value[3]);
    }

    // Brc ([MS-DOC] 2.9.16): cv, dptLineWidth, brcType, then dptSpace, fShadow and fFrame.
    public static Border Brc(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(value) == 0xFFFFFFFF && BinaryPrimitives.ReadUInt32LittleEndian(value[4..]) == 0xFFFFFFFF)
        {
            return Border.None;
        }

        return MakeBorder(value[5], value[4], ColorRef(value), value[6]);
    }

    private static Border MakeBorder(int type, int width, DocumentColor color, int flags)
    {
        BorderStyle style = type switch
        {
            0 or 0xFF => BorderStyle.None,
            1 or 4 or 5 => BorderStyle.Single,
            2 => BorderStyle.Thick,
            3 => BorderStyle.Double,
            >= 6 and <= 27 => (BorderStyle)(type - 2),
            _ => BorderStyle.Single,
        };
        if (style == BorderStyle.None)
        {
            return Border.None;
        }

        return new Border(style, width / 8.0, color, flags & 0x1F, (flags & 0x20) != 0, (flags & 0x40) != 0);
    }

    // Shd80 ([MS-DOC] 2.9.245): icoFore (5 bits), icoBack (5 bits), ipat (6 bits).
    public static Shading Shd80(int value)
    {
        return Shading(Ico(value & 0x1F), Ico((value >> 5) & 0x1F), (value >> 10) & 0x3F);
    }

    // Shd ([MS-DOC] 2.9.244): cvFore, cvBack, ipat.
    public static Shading Shd(ReadOnlySpan<byte> value)
    {
        return Shading(ColorRef(value), ColorRef(value[4..]), BinaryPrimitives.ReadUInt16LittleEndian(value[8..]));
    }

    // The pattern of an Ipat ([MS-DOC] 2.9.121), its foreground the pattern's colour and its background the fill.
    private static Shading Shading(DocumentColor fore, DocumentColor back, int ipat)
    {
        (ShadingPattern pattern, double percent) = ipat switch
        {
            0 => (ShadingPattern.Clear, 0),
            1 => (ShadingPattern.Solid, 0),
            2 => (ShadingPattern.Percent, 5),
            3 => (ShadingPattern.Percent, 10),
            4 => (ShadingPattern.Percent, 20),
            5 => (ShadingPattern.Percent, 25),
            6 => (ShadingPattern.Percent, 30),
            7 => (ShadingPattern.Percent, 40),
            8 => (ShadingPattern.Percent, 50),
            9 => (ShadingPattern.Percent, 60),
            10 => (ShadingPattern.Percent, 70),
            11 => (ShadingPattern.Percent, 75),
            12 => (ShadingPattern.Percent, 80),
            13 => (ShadingPattern.Percent, 90),
            14 => (ShadingPattern.HorizontalStripe, 0),
            15 => (ShadingPattern.VerticalStripe, 0),
            16 => (ShadingPattern.ReverseDiagonalStripe, 0),
            17 => (ShadingPattern.DiagonalStripe, 0),
            18 => (ShadingPattern.HorizontalCross, 0),
            19 => (ShadingPattern.DiagonalCross, 0),
            20 => (ShadingPattern.ThinHorizontalStripe, 0),
            21 => (ShadingPattern.ThinVerticalStripe, 0),
            22 => (ShadingPattern.ThinReverseDiagonalStripe, 0),
            23 => (ShadingPattern.ThinDiagonalStripe, 0),
            24 => (ShadingPattern.ThinHorizontalCross, 0),
            25 => (ShadingPattern.ThinDiagonalCross, 0),
            >= 35 and <= 62 => (ShadingPattern.Percent, ExtraPercents[ipat - 35]),
            0xFFFF => (ShadingPattern.Nil, 0),
            _ => (ShadingPattern.Clear, 0),
        };
        return new Shading(pattern, back, fore, percent);
    }

    // Ipat's later percentages ([MS-DOC] 2.9.121), from 35.
    private static readonly double[] ExtraPercents =
    [
        2.5, 7.5, 12.5, 15, 17.5, 22.5, 27.5, 32.5, 35, 37.5, 42.5, 45, 47.5, 52.5, 55, 57.5, 62.5, 65, 67.5, 72.5, 77.5, 82.5, 85,
        87.5, 92.5, 95, 97.5, 97,
    ];

    public static bool On(ReadOnlySpan<byte> operand) => operand.Length > 0 && operand[0] != 0;

    public static int U16(ReadOnlySpan<byte> operand) => operand.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(operand) : operand.Length == 1 ? operand[0] : 0;

    public static int S16(ReadOnlySpan<byte> operand) => operand.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(operand) : operand.Length == 1 ? (sbyte)operand[0] : 0;
}

/// <summary>What applying sprms needs from the document: the version, the fonts and the warnings.</summary>
internal sealed class DocContext(bool word6, IReadOnlyList<string> fonts, WarningCollector warnings)
{
    public bool Word6 { get; } = word6;

    public IReadOnlyList<string> Fonts { get; set; } = fonts;

    public WarningCollector Warnings { get; } = warnings;
}
