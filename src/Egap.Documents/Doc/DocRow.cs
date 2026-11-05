using System.Buffers.Binary;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// A table row's properties ([MS-DOC] 2.6.3), from the table sprms of its row-end paragraph: gathered as they come, then
/// put on a <see cref="WordRowDefinition"/> with sprmTDefTable first, since the other cell sprms name its cells.
/// </summary>
internal sealed class DocRow(bool word6)
{
    private readonly List<(ushort Sprm, byte[] Operand)> sprms = [];

    public bool IsEmpty => sprms.Count == 0;

    public void Add(ushort sprm, ReadOnlySpan<byte> operand)
    {
        sprms.Add((sprm, operand.ToArray()));
    }

    /// <summary>The row definition the gathered sprms make.</summary>
    public WordRowDefinition Build()
    {
        WordRowDefinition definition = new();
        Shading?[]? shading = null;
        Shading?[]? raw = null;
        foreach ((ushort sprm, byte[] operand) in sprms)
        {
            if (sprm == 0xD608)
            {
                DefineCells(definition, operand);
            }
        }

        TablePosition? position = null;
        foreach ((ushort sprm, byte[] bytes) in sprms)
        {
            ReadOnlySpan<byte> operand = bytes;
            switch (sprm)
            {
                case 0x9602:
                    WordProperties.Set(definition, WordTableProperty.Gap, DocProperties.S16(operand));
                    break;
                case 0x9601:
                    if (definition.Cells.Count == 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.Left, DocProperties.S16(operand));
                    }

                    break;
                case 0x9407:
                    WordProperties.Set(definition, WordTableProperty.RowHeight, DocProperties.S16(operand));
                    break;
                case 0x3404:
                    WordProperties.Set(definition, WordTableProperty.HeaderRow, operand[0]);
                    break;
                case 0x3403 or 0x3466:
                    WordProperties.Set(definition, WordTableProperty.CantSplit, operand[0]);
                    break;
                case 0x5400 or 0x548A:
                    WordProperties.Set(definition, WordTableProperty.Alignment, (int)(DocProperties.U16(operand) switch
                    {
                        1 => TableAlignment.Center,
                        2 => TableAlignment.Right,
                        _ => TableAlignment.Left,
                    }));
                    break;
                case 0xF614 when operand.Length >= 3:
                    WordProperties.Set(definition, WordTableProperty.WidthUnit, operand[0]);
                    WordProperties.Set(definition, WordTableProperty.Width, BinaryPrimitives.ReadInt16LittleEndian(operand[1..]));
                    break;
                case 0x3615:
                    WordProperties.Set(definition, WordTableProperty.AutoFit, operand[0]);
                    break;
                case 0x563A:
                    WordProperties.Set(definition, WordTableProperty.Style, DocProperties.U16(operand));
                    break;
                case 0x740A when operand.Length >= 4:
                    {
                        // TLP: the table style's options (fatlHdrRows $20, fatlLastRow $40, fatlHdrCols $80, fatlLastCol
                        // $100, fatlNoRowBands $200, fatlNoColBands $400).
                        int grfatl = BinaryPrimitives.ReadUInt16LittleEndian(operand[2..]);
                        WordProperties.Set(definition.Table, WordTableProperty.LookFirstRow, grfatl & 0x0020);
                        WordProperties.Set(definition.Table, WordTableProperty.LookLastRow, grfatl & 0x0040);
                        WordProperties.Set(definition.Table, WordTableProperty.LookFirstColumn, grfatl & 0x0080);
                        WordProperties.Set(definition.Table, WordTableProperty.LookLastColumn, grfatl & 0x0100);
                        WordProperties.Set(definition.Table, WordTableProperty.LookNoRowBands, grfatl & 0x0200);
                        WordProperties.Set(definition.Table, WordTableProperty.LookNoColumnBands, grfatl & 0x0400);
                        break;
                    }

                case 0x560B or 0x5664:
                    WordProperties.Set(definition, WordTableProperty.Bidi, DocProperties.U16(operand) != 0 ? 1 : 0);
                    break;
                case 0x3488:
                    WordProperties.Set(definition, WordTableProperty.RowBandSize, operand[0]);
                    break;
                case 0x3489:
                    WordProperties.Set(definition, WordTableProperty.ColumnBandSize, operand[0]);
                    break;
                case 0xF661 when operand.Length >= 3 && operand[0] == 3:
                    WordProperties.Set(definition, WordTableProperty.Indent, BinaryPrimitives.ReadInt16LittleEndian(operand[1..]));
                    break;
                case 0x360D:
                    {
                        position ??= new TablePosition();
                        int vertical = (operand[0] >> 4) & 3;
                        int horizontal = (operand[0] >> 6) & 3;
                        if (vertical != 3)
                        {
                            position = position with { VerticalAnchor = vertical switch { 0 => VerticalAnchor.Margin, 1 => VerticalAnchor.Page, _ => VerticalAnchor.Paragraph } };
                        }

                        if (horizontal != 3)
                        {
                            position = position with { HorizontalAnchor = horizontal switch { 0 => HorizontalAnchor.Column, 1 => HorizontalAnchor.Margin, _ => HorizontalAnchor.Page } };
                        }

                        break;
                    }

                case 0x940E or 0x940F:
                    {
                        position ??= new TablePosition();
                        bool horizontal = sprm == 0x940E;
                        int value = DocProperties.S16(operand);
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
                        double offset = WordProperties.Twips(value == 0 ? 0 : value - 1);
                        position = (horizontal, alignment) switch
                        {
                            (true, { } a) => position with { HorizontalAlignment = a },
                            (true, null) => position with { HorizontalOffset = offset, HorizontalAlignment = FloatingAlignment.None },
                            (false, { } a) => position with { VerticalAlignment = a },
                            _ => position with { VerticalOffset = offset, VerticalAlignment = FloatingAlignment.None },
                        };
                        break;
                    }

                case 0x9410:
                    position = (position ?? new TablePosition()) with { DistanceLeft = WordProperties.Twips(Math.Max(0, DocProperties.S16(operand))) };
                    break;
                case 0x9411:
                    position = (position ?? new TablePosition()) with { DistanceTop = WordProperties.Twips(Math.Max(0, DocProperties.S16(operand))) };
                    break;
                case 0x941E:
                    position = (position ?? new TablePosition()) with { DistanceRight = WordProperties.Twips(Math.Max(0, DocProperties.S16(operand))) };
                    break;
                case 0x941F:
                    position = (position ?? new TablePosition()) with { DistanceBottom = WordProperties.Twips(Math.Max(0, DocProperties.S16(operand))) };
                    break;
                case 0xD634 or 0xD632 or 0xD633:
                    Cssa(definition, sprm, operand);
                    break;
                case 0xD635 when operand.Length >= 6:
                    for (int i = operand[1]; i < operand[2] && i < definition.Cells.Count; i++)
                    {
                        definition.Cells[i].WidthUnit = operand[3];
                        definition.Cells[i].Width = BinaryPrimitives.ReadInt16LittleEndian(operand[4..]);
                    }

                    break;
                case 0xD62B when operand.Length >= 3:
                    if (operand[1] < definition.Cells.Count)
                    {
                        WordProperties.Set(definition.Cells[operand[1]].Format, WordTableProperty.CellVerticalMerge, (int)(operand[2] switch
                        {
                            1 => VerticalMerge.Continue,
                            3 => VerticalMerge.Restart,
                            _ => VerticalMerge.None,
                        }));
                    }

                    break;
                case 0xD62C when operand.Length >= 4:
                    for (int i = operand[1]; i < operand[2] && i < definition.Cells.Count; i++)
                    {
                        WordProperties.Set(definition.Cells[i].Format, WordTableProperty.CellVerticalAlignment, (int)VerticalOf(operand[3]));
                    }

                    break;
                case 0xD639 or 0xD642 when operand.Length >= 4:
                    for (int i = operand[1]; i < operand[2] && i < definition.Cells.Count; i++)
                    {
                        WordProperties.Set(definition.Cells[i].Format, sprm == 0xD639 ? WordTableProperty.CellNoWrap : WordTableProperty.CellHideMark, operand[3]);
                    }

                    break;
                case 0xD613 when operand.Length >= 1 + 48:
                    {
                        TableFormat table = definition.Table;
                        ReadOnlySpan<byte> brcs = operand[1..];
                        table.BorderTop = DocProperties.Brc(brcs);
                        table.BorderLeft = DocProperties.Brc(brcs[8..]);
                        table.BorderBottom = DocProperties.Brc(brcs[16..]);
                        table.BorderRight = DocProperties.Brc(brcs[24..]);
                        table.BorderInsideHorizontal = DocProperties.Brc(brcs[32..]);
                        table.BorderInsideVertical = DocProperties.Brc(brcs[40..]);
                        break;
                    }

                case 0xD605 when operand.Length >= 1 + 24:
                    {
                        TableFormat table = definition.Table;
                        ReadOnlySpan<byte> brcs = operand[1..];
                        table.BorderTop = DocProperties.Brc80(brcs);
                        table.BorderLeft = DocProperties.Brc80(brcs[4..]);
                        table.BorderBottom = DocProperties.Brc80(brcs[8..]);
                        table.BorderRight = DocProperties.Brc80(brcs[12..]);
                        table.BorderInsideHorizontal = DocProperties.Brc80(brcs[16..]);
                        table.BorderInsideVertical = DocProperties.Brc80(brcs[20..]);
                        break;
                    }

                case 0xD62F when operand.Length >= 4 + 8:
                    SetBorders(definition, operand[1], operand[2], operand[3], DocProperties.Brc(operand[4..]));
                    break;
                case 0xD620 when operand.Length >= 4 + 4:
                    SetBorders(definition, operand[1], operand[2], operand[3], DocProperties.Brc80(operand[4..]));
                    break;
                case 0xD612 or 0xD616 or 0xD60C:
                    Shadings(ref shading, operand, sprm switch { 0xD616 => 22, 0xD60C => 44, _ => 0 });
                    break;
                case 0xD670 or 0xD671 or 0xD672:
                    Shadings(ref raw, operand, sprm switch { 0xD671 => 22, 0xD672 => 44, _ => 0 });
                    break;
                case 0xD609:
                    shading ??= new Shading?[WordProperties.MaxCells];
                    for (int i = 0; (2 * i) + 3 <= operand.Length && i < shading.Length; i++)
                    {
                        shading[i] ??= DocProperties.Shd80(BinaryPrimitives.ReadUInt16LittleEndian(operand[(1 + (2 * i))..]));
                    }

                    break;
                case 0xD660 when operand.Length >= 11:
                    definition.Table.Shading = DocProperties.Shd(operand[1..]);
                    break;
            }
        }

        if (position is not null)
        {
            definition.Position = position;
        }

        // The raw shading is the cells' own; the other holds what a table style gives as well.
        Shading?[]? cells = raw ?? shading;
        if (cells is not null)
        {
            for (int i = 0; i < definition.Cells.Count && i < cells.Length; i++)
            {
                if (cells[i] is { } s && s.Pattern != ShadingPattern.Nil)
                {
                    definition.Cells[i].Format.Shading = s;
                }
            }
        }

        return definition;
    }

    // TDefTableOperand ([MS-DOC] 2.9.310): a size word, the cell count, each cell edge, then a TC80 per cell (Word 6's TC
    // is 10 bytes: its flags and four 2-byte borders, which are not read).
    private void DefineCells(WordRowDefinition definition, ReadOnlySpan<byte> operand)
    {
        if (operand.Length < 3)
        {
            return;
        }

        int count = Math.Min((int)operand[2], WordProperties.MaxCells);
        ReadOnlySpan<byte> rest = operand[3..];
        if (rest.Length < 2 * (count + 1))
        {
            count = Math.Max(0, (rest.Length / 2) - 1);
        }

        if (count == 0)
        {
            return;
        }

        WordProperties.Set(definition, WordTableProperty.Left, BinaryPrimitives.ReadInt16LittleEndian(rest));
        ReadOnlySpan<byte> tcs = rest[(2 * (count + 1))..];
        int tcSize = word6 ? 10 : 20;
        for (int i = 0; i < count; i++)
        {
            int right = BinaryPrimitives.ReadInt16LittleEndian(rest[(2 * (i + 1))..]);
            if ((i + 1) * tcSize <= tcs.Length)
            {
                ReadOnlySpan<byte> tc = tcs.Slice(i * tcSize, tcSize);
                int flags = BinaryPrimitives.ReadUInt16LittleEndian(tc);
                WordProperties.Set(definition, WordTableProperty.CellMergeFirst, flags & 0x0001);
                WordProperties.Set(definition, WordTableProperty.CellMerged, flags & 0x0002);
                if (!word6)
                {
                    // TC80's tcgrf: fVertical $4, fBackward $8, fVertMerge $20, fVertRestart $40, vertAlign (bits 7-8),
                    // ftsWidth (bits 9-11), fFitText $1000, fNoWrap $2000, fHideMark $4000; then wWidth and the borders.
                    if ((flags & 0x0004) != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellTextDirection, (int)((flags & 0x0008) != 0 ? TextDirection.BottomToTop : TextDirection.TopToBottom));
                    }

                    if ((flags & 0x0020) != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellVerticalMerge, (int)((flags & 0x0040) != 0 ? VerticalMerge.Restart : VerticalMerge.Continue));
                    }

                    int vertical = (flags >> 7) & 3;
                    if (vertical != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellVerticalAlignment, (int)VerticalOf(vertical));
                    }

                    if ((flags & 0x1000) != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellFitText, 1);
                    }

                    if ((flags & 0x2000) != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellNoWrap, 1);
                    }

                    if ((flags & 0x4000) != 0)
                    {
                        WordProperties.Set(definition, WordTableProperty.CellHideMark, 1);
                    }

                    WordProperties.Set(definition, WordTableProperty.CellWidthUnit, (flags >> 9) & 7);
                    WordProperties.Set(definition, WordTableProperty.CellWidth, BinaryPrimitives.ReadInt16LittleEndian(tc[2..]));
                    CellFormat format = definition.Pending.Format;
                    format.BorderTop = Brc80OrNull(tc[4..]);
                    format.BorderLeft = Brc80OrNull(tc[8..]);
                    format.BorderBottom = Brc80OrNull(tc[12..]);
                    format.BorderRight = Brc80OrNull(tc[16..]);
                }
            }

            WordProperties.Set(definition, WordTableProperty.CellRight, right);
        }
    }

    // A cell's border in a TC80: none set (all zero) gives the table's.
    private static Border? Brc80OrNull(ReadOnlySpan<byte> value)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(value) == 0 ? null : DocProperties.Brc80(value);
    }

    // CSSAOperand ([MS-DOC] 2.9.48): its size, the cells (itcFirst, itcLim), the sides (top 1, left 2, bottom 4, right 8),
    // ftsWidth (3 for twips) and wWidth.
    private static void Cssa(WordRowDefinition definition, ushort sprm, ReadOnlySpan<byte> operand)
    {
        if (operand.Length < 7 || operand[4] is not (0 or 3))
        {
            return;
        }

        int first = operand[1];
        int last = operand[2];
        int sides = operand[3];
        int width = BinaryPrimitives.ReadInt16LittleEndian(operand[5..]);
        if (sprm == 0xD633)
        {
            WordProperties.Set(definition, WordTableProperty.CellSpacing, width);
            return;
        }

        if (sprm == 0xD634)
        {
            Side(1, WordTableProperty.DefaultMarginTop);
            Side(2, WordTableProperty.DefaultMarginLeft);
            Side(4, WordTableProperty.DefaultMarginBottom);
            Side(8, WordTableProperty.DefaultMarginRight);
            return;
        }

        for (int i = first; i < last && i < definition.Cells.Count; i++)
        {
            CellFormat cell = definition.Cells[i].Format;
            if ((sides & 1) != 0)
            {
                WordProperties.Set(cell, WordTableProperty.CellMarginTop, width);
            }

            if ((sides & 2) != 0)
            {
                WordProperties.Set(cell, WordTableProperty.CellMarginLeft, width);
            }

            if ((sides & 4) != 0)
            {
                WordProperties.Set(cell, WordTableProperty.CellMarginBottom, width);
            }

            if ((sides & 8) != 0)
            {
                WordProperties.Set(cell, WordTableProperty.CellMarginRight, width);
            }
        }

        void Side(int bit, WordTableProperty property)
        {
            if ((sides & bit) != 0)
            {
                WordProperties.Set(definition.Table, property, width);
            }
        }
    }

    // TableBrcOperand: its size, the cells, the sides (top 1, left 2, bottom 4, right 8), the border.
    private static void SetBorders(WordRowDefinition definition, int first, int last, int sides, Border border)
    {
        for (int i = first; i < last && i < definition.Cells.Count; i++)
        {
            CellFormat cell = definition.Cells[i].Format;
            if ((sides & 1) != 0)
            {
                cell.BorderTop = border;
            }

            if ((sides & 2) != 0)
            {
                cell.BorderLeft = border;
            }

            if ((sides & 4) != 0)
            {
                cell.BorderBottom = border;
            }

            if ((sides & 8) != 0)
            {
                cell.BorderRight = border;
            }
        }
    }

    // DefTableShdOperand: its size, then a 10-byte Shd per cell from the first that the sprm covers.
    private static void Shadings(ref Shading?[]? target, ReadOnlySpan<byte> operand, int first)
    {
        target ??= new Shading?[WordProperties.MaxCells];
        for (int i = 0; 1 + (10 * (i + 1)) <= operand.Length && first + i < target.Length; i++)
        {
            target[first + i] = DocProperties.Shd(operand[(1 + (10 * i))..]);
        }
    }

    private static VerticalAlignment VerticalOf(int value) => value switch
    {
        1 => VerticalAlignment.Center,
        2 => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Top,
    };
}
