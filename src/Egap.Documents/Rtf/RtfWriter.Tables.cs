namespace Egap.Documents.Rtf;

/// <summary>
/// Tables. A row is its cells' paragraphs, marked <c>\intbl</c> (and <c>\itapN</c> when nested), each cell ended by
/// <c>\cell</c> and the row by <c>\row</c>. A top-level row's definition (<c>\trowd</c>, the cells up to their right edges at
/// <c>\cellx</c>) comes before its cells; a nested row's comes after them, in <c>\nesttableprops</c> with <c>\nestrow</c>, as
/// Word writes both. The cells' edges come from the table's grid, so cells that span columns span them in RTF too.
/// </summary>
internal sealed partial class RtfWriter
{
    private void WriteTable(Table table, int level)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        long left = Twips(table.Format.Indent ?? 0) - Twips(table.Format.CellMarginLeft ?? 0);
        long[]? edges = GridEdges(table, left);
        for (int r = 0; r < table.Rows.Count; r++)
        {
            Row row = table.Rows[r];
            List<Cell> cells = [.. row.Cells.Take(63)];
            if (row.Cells.Count > 63)
            {
                warnings.Add(ConversionWarningKind.TableSimplified, "An RTF row holds at most 63 cells; the cells past them were left out.");
            }

            if (level == 1)
            {
                RowDefinition(table, row, r, cells, left, edges);
            }

            foreach (Cell cell in cells)
            {
                WriteCell(cell, level);
            }

            if (level == 1)
            {
                Word("row");
            }
            else
            {
                Destination("nesttableprops");
                RowDefinition(table, row, r, cells, left, edges);
                Word("nestrow");
                Close();
            }

            output.AppendLine();
        }
    }

    private void WriteCell(Cell cell, int level)
    {
        string end = level == 1 ? "cell" : "nestcell";
        int outerFloor = fieldFloor;
        fieldFloor = openFields.Count;
        List<Block> blocks = Flatten(cell.Blocks);
        for (int i = 0; i < blocks.Count; i++)
        {
            bool isLast = i == blocks.Count - 1;
            switch (blocks[i])
            {
                case Paragraph paragraph:
                    WriteParagraph(paragraph, level, isLast ? end : "par");
                    break;
                case Table nested:
                    WriteTable(nested, level + 1);
                    break;
            }
        }

        if (blocks.Count == 0 || blocks[^1] is not Paragraph)
        {
            // A cell ends at a paragraph mark, and a reader makes an empty paragraph of the one after a nested table.
            WriteParagraph(new Paragraph(document), level, end);
        }

        fieldFloor = outerFloor;
    }

    /// <summary>The right edges of the table's grid columns in twips, from <paramref name="left"/>; null when the table has no grid.</summary>
    private static long[]? GridEdges(Table table, long left)
    {
        if (table.Grid is not { Count: > 0 } grid)
        {
            return null;
        }

        long[] edges = new long[Math.Min(grid.Count, 64) + 1];
        edges[0] = left;
        double sum = 0;
        for (int i = 1; i < edges.Length; i++)
        {
            sum += Math.Max(0, grid[i - 1]);
            edges[i] = left + Twips(sum);
        }

        return edges;
    }

    private void RowDefinition(Table table, Row row, int index, List<Cell> cells, long left, long[]? edges)
    {
        TableFormat t = table.Format;
        RowFormat f = row.Format;
        Word("trowd");
        Word("irow", index);
        Word("irowband", index);
        if (table.Style is { IsInDocument: true } style)
        {
            Word("ts", StyleIndex(style));
        }

        Word("trleft", left);

        // The first row's definition gives the table's formatting too; its alignment and spacing are the row's as well.
        TableAlignment? alignment = index == 0 ? t.Alignment ?? f.Alignment : f.Alignment;
        if (alignment is { } a)
        {
            Word(a switch { TableAlignment.Center => "trqc", TableAlignment.Right => "trqr", _ => "trql" });
        }

        if (f.Height is double height && f.HeightRule != RowHeightRule.Auto)
        {
            long twips = Math.Abs(Twips(height));
            Word("trrh", f.HeightRule == RowHeightRule.Exactly ? -twips : twips);
        }
        else if (f.HeightRule == RowHeightRule.Auto)
        {
            Word("trrh", 0);
        }

        if (f.IsHeader is bool header)
        {
            Toggle("trhdr", header);
        }

        if (f.CantSplit is bool cantSplit)
        {
            Toggle("trkeep", cantSplit);
        }

        BorderSide("trbrdrt", t.BorderTop);
        BorderSide("trbrdrl", t.BorderLeft);
        BorderSide("trbrdrb", t.BorderBottom);
        BorderSide("trbrdrr", t.BorderRight);
        BorderSide("trbrdrh", t.BorderInsideHorizontal);
        BorderSide("trbrdrv", t.BorderInsideVertical);
        if (t.Width is { } width)
        {
            WidthWords("trftsWidth", "trwWidth", width);
        }

        if (t.Layout == TableLayoutKind.Autofit)
        {
            Word("trautofit", 1);
        }

        CellMargins(t);
        if ((index == 0 ? t.CellSpacing ?? f.CellSpacing : f.CellSpacing) is double spacing && Twips(spacing) > 0)
        {
            foreach (string side in (ReadOnlySpan<string>)["l", "t", "r", "b"])
            {
                Word("trspd" + side, Twips(spacing));
                Word("trspdf" + side, 3);
            }
        }

        if (t.Indent is double indent)
        {
            Word("tblind", Twips(indent));
            Word("tblindtype", 3);
        }

        if (t.Shading is { } shading)
        {
            ShadingWords(shading, "trcbpat", "trcfpat", "trshdng", null);
        }

        Word(t.Bidi == true ? "rtlrow" : "ltrrow");
        LookWord("tbllkhdrrows", t.LookFirstRow);
        LookWord("tbllklastrow", t.LookLastRow);
        LookWord("tbllkhdrcols", t.LookFirstColumn);
        LookWord("tbllklastcol", t.LookLastColumn);
        LookWord("tbllknorowband", t.LookNoRowBands);
        LookWord("tbllknocolband", t.LookNoColumnBands);
        if (t.Position is { } position)
        {
            PositionWords(position);
        }

        int column = 0;
        long right = left;
        double textWidth = Docx.ContainerTables.TextWidth(table);
        foreach (Cell cell in cells)
        {
            int span = Math.Max(1, cell.Format.GridSpan ?? 1);
            if (edges is not null && column + span < edges.Length)
            {
                right = Math.Max(right, edges[column + span]);
            }
            else
            {
                // Past the grid, or with none: the cell's own width.
                right += Math.Max(1, Twips(CellWidth(cell, table, textWidth, cells.Count)));
            }

            column += span;
            CellWords(cell.Format);
            if (cell.Format.Width is null)
            {
                // A reader takes the width from the cell's edges, so the width it will find is written too.
                WidthWords("clftsWidth", "clwWidth", new TableWidth(WidthUnit.Points, CellWidth(cell, table, textWidth, cells.Count)));
            }

            Word("cellx", right);
        }
    }

    private static double CellWidth(Cell cell, Table table, double textWidth, int cells)
    {
        return cell.Format.Width switch
        {
            { Unit: WidthUnit.Points, Value: > 0 } w => w.Value,
            { Unit: WidthUnit.Percent, Value: > 0 } w => textWidth * w.Value / 100,
            _ => (table.Format.Width is { Unit: WidthUnit.Points, Value: > 0 } total ? total.Value : textWidth) / Math.Max(1, cells),
        };
    }

    private void LookWord(string word, bool? value)
    {
        if (value is bool on)
        {
            Toggle(word, on);
        }
    }

    /// <summary>A width as RTF gives it: a unit (2 fiftieths of a percent, 3 twips, 1 automatic) and the value.</summary>
    private void WidthWords(string unitWord, string valueWord, TableWidth width)
    {
        switch (width.Unit)
        {
            case WidthUnit.Points:
                Word(unitWord, 3);
                Word(valueWord, Math.Max(0, Twips(width.Value)));
                break;
            case WidthUnit.Percent:
                Word(unitWord, 2);
                Word(valueWord, Math.Max(0, Clamp(width.Value * 50)));
                break;
            default:
                Word(unitWord, 1);
                break;
        }
    }

    /// <summary>A table's default cell margins, in twips (<c>\trpaddfN3</c>).</summary>
    private void CellMargins(TableFormat f)
    {
        Margin("l", f.CellMarginLeft);
        Margin("t", f.CellMarginTop);
        Margin("b", f.CellMarginBottom);
        Margin("r", f.CellMarginRight);

        void Margin(string side, double? value)
        {
            if (value is double points)
            {
                Word("trpadd" + side, Math.Max(0, Twips(points)));
                Word("trpaddf" + side, 3);
            }
        }
    }

    private void CellWords(CellFormat f)
    {
        switch (f.VerticalMerge)
        {
            case VerticalMerge.Restart:
                Word("clvmgf");
                break;
            case VerticalMerge.Continue:
                Word("clvmrg");
                break;
        }

        if (f.VerticalAlignment is { } vertical)
        {
            Word(vertical switch { VerticalAlignment.Center => "clvertalc", VerticalAlignment.Bottom => "clvertalb", _ => "clvertalt" });
        }

        BorderSide("clbrdrt", f.BorderTop);
        BorderSide("clbrdrl", f.BorderLeft);
        BorderSide("clbrdrb", f.BorderBottom);
        BorderSide("clbrdrr", f.BorderRight);
        BorderSide("cldglu", f.BorderDiagonalUp);
        BorderSide("cldgll", f.BorderDiagonalDown);
        if (f.Shading is { } shading)
        {
            ShadingWords(shading, "clcbpat", "clcfpat", "clshdng", "cl");
        }

        if (f.TextDirection is { } direction)
        {
            Word(direction switch { TextDirection.TopToBottom => "cltxtbrl", TextDirection.BottomToTop => "cltxbtlr", _ => "cltxlrtb" });
        }

        if (f.Width is { } width)
        {
            WidthWords("clftsWidth", "clwWidth", width);
        }

        // Word writes the top margin as \clpadl and the left as \clpadt, against the specification's names; so does Egap.
        CellMargin("clpadl", "clpadfl", f.MarginTop);
        CellMargin("clpadt", "clpadft", f.MarginLeft);
        CellMargin("clpadb", "clpadfb", f.MarginBottom);
        CellMargin("clpadr", "clpadfr", f.MarginRight);
        if (f.NoWrap is bool noWrap)
        {
            Toggle("clNoWrap", noWrap);
        }

        if (f.FitText is bool fitText)
        {
            Toggle("clFitText", fitText);
        }

        if (f.HideMark is bool hideMark)
        {
            Toggle("clhidemark", hideMark);
        }
    }

    private void CellMargin(string word, string unit, double? value)
    {
        if (value is double points)
        {
            Word(word, Math.Max(0, Twips(points)));
            Word(unit, 3);
        }
    }

    private void PositionWords(TablePosition position)
    {
        Word(position.HorizontalAnchor switch { HorizontalAnchor.Page => "tphpg", HorizontalAnchor.Margin => "tphmrg", _ => "tphcol" });
        Word(position.VerticalAnchor switch { VerticalAnchor.Page => "tpvpg", VerticalAnchor.Margin => "tpvmrg", _ => "tpvpara" });
        if (position.HorizontalOffset != 0 || position.HorizontalAlignment == FloatingAlignment.None)
        {
            Word(position.HorizontalOffset < 0 ? "tposnegx" : "tposx", Twips(position.HorizontalOffset));
        }

        switch (position.HorizontalAlignment)
        {
            case FloatingAlignment.Start:
                Word("tposxl");
                break;
            case FloatingAlignment.Center:
                Word("tposxc");
                break;
            case FloatingAlignment.End:
                Word("tposxr");
                break;
            case FloatingAlignment.Inside:
                Word("tposxi");
                break;
            case FloatingAlignment.Outside:
                Word("tposxo");
                break;
        }

        if (position.VerticalOffset != 0 || position.VerticalAlignment == FloatingAlignment.None)
        {
            Word(position.VerticalOffset < 0 ? "tposnegy" : "tposy", Twips(position.VerticalOffset));
        }

        switch (position.VerticalAlignment)
        {
            case FloatingAlignment.Start:
                Word("tposyt");
                break;
            case FloatingAlignment.Center:
                Word("tposyc");
                break;
            case FloatingAlignment.End:
                Word("tposyb");
                break;
            case FloatingAlignment.Inside:
                Word("tposyin");
                break;
            case FloatingAlignment.Outside:
                Word("tposyout");
                break;
        }

        Word("tdfrmtxtLeft", Twips(position.DistanceLeft));
        Word("tdfrmtxtRight", Twips(position.DistanceRight));
        Word("tdfrmtxtTop", Twips(position.DistanceTop));
        Word("tdfrmtxtBottom", Twips(position.DistanceBottom));
        if (!position.AllowOverlap)
        {
            Word("tabsnoovrlp", 1);
        }
    }
}
