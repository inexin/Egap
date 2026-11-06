using Egap.Documents;

using DocumentCell = Egap.Documents.Cell;

namespace Egap.Spreadsheets;

/// <summary>Writes a workbook as a document: a section per visible sheet, each with its name as a heading and a table.</summary>
internal static class WorkbookDocument
{
    /// <summary>
    /// The most table cells a sheet becomes. A document cell costs far more than a sheet's, and a file with values far apart
    /// on a diagonal would otherwise make billions of them.
    /// </summary>
    internal const long MaxCells = 2_000_000;

    public static Document Build(Workbook book, bool sheetHeadings, out IReadOnlyList<ConversionWarning> warnings)
    {
        Document document = new();
        List<ConversionWarning> found = [];
        foreach (Worksheet sheet in book.VisibleSheets)
        {
            Section section = document.AddSection();
            if (sheetHeadings)
            {
                section.Body.AddParagraph(sheet.Name).Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1);
            }

            new SheetTableWriter(sheet, section.Body, found).Write();
            if (sheet.Drawings.Count > 0)
            {
                found.Add(new ConversionWarning(ConversionWarningKind.DrawingDropped, $"Sheet \"{sheet.Name}\": pictures, charts and shapes are left out.", sheet.Drawings.Count));
            }

            if (sheet.Comments.Count > 0)
            {
                found.Add(new ConversionWarning(ConversionWarningKind.CommentsDropped, $"Sheet \"{sheet.Name}\": cell comments are left out.", sheet.Comments.Count));
            }
        }

        if (document.Sections.Count == 0)
        {
            document.AddSection();
        }

        warnings = found;
        return document;
    }

    /// <summary>One sheet's table. Rows and columns that are hidden or show nothing are left out.</summary>
    private sealed class SheetTableWriter(Worksheet sheet, Body body, List<ConversionWarning> warnings)
    {
        private readonly Dictionary<int, bool> hiddenColumns = [];
        private readonly List<Entry> entries = [];
        private readonly Dictionary<int, (int Start, int End)> rowEntries = [];

        // How many entries and grid rows the merges and links may visit; only a file made to be slow comes near it.
        private long budget = MaxCells * 16;

        public void Write()
        {
            ReadEntries();
            int[] rows = [.. entries.Where(e => !e.Dropped).Select(e => e.Row).Distinct().Order()];
            if (rows.Length == 0)
            {
                return;
            }

            int[] columns = [.. entries.Where(e => !e.Dropped).Select(e => e.Column).Distinct().Order()];
            List<Item>?[] items = new List<Item>?[rows.Length];
            AddMergedItems(rows, columns, items);
            foreach (Entry entry in entries)
            {
                if (!entry.Dropped && !entry.IsMergeOrigin)
                {
                    (items[Array.BinarySearch(rows, entry.Row)] ??= []).Add(new Item(Array.BinarySearch(columns, entry.Column), 1, VerticalMerge.None, entry));
                }
            }

            Table table = body.AddTable(0, 0);
            long written = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                if (written >= MaxCells)
                {
                    warnings.Add(new ConversionWarning(ConversionWarningKind.TableSimplified, $"Sheet \"{sheet.Name}\": only its first {r} rows with values fit, of {rows.Length}.", 1));
                    break;
                }

                written += WriteRow(table.AddRow(), items[r]) + 1;
            }

            if (sheet.Tables.Any(t => t.ShowHeaderRow && t.Range.First.Row == rows[0]))
            {
                table.Rows[0].Format.IsHeader = true;
            }
        }

        private static ParagraphAlignment? AlignmentOf(CellHorizontalAlignment horizontal, CellValueKind kind) => horizontal switch
        {
            CellHorizontalAlignment.Left or CellHorizontalAlignment.Fill or CellHorizontalAlignment.Distributed => ParagraphAlignment.Left,
            CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous => ParagraphAlignment.Center,
            CellHorizontalAlignment.Right => ParagraphAlignment.Right,
            CellHorizontalAlignment.Justify => ParagraphAlignment.Justify,

            // General: numbers to the right, logical values and errors centred, text to the left, as Excel shows them.
            _ => kind switch
            {
                CellValueKind.Number => ParagraphAlignment.Right,
                CellValueKind.Boolean or CellValueKind.Error => ParagraphAlignment.Center,
                _ => null,
            },
        };

        private static (int First, int Last)? Within(int[] sorted, int first, int last)
        {
            int from = Array.BinarySearch(sorted, first);
            from = from < 0 ? ~from : from;
            int to = Array.BinarySearch(sorted, last);
            to = to < 0 ? ~to - 1 : to;
            return from <= to ? (from, to) : null;
        }

        /// <summary>The row's cells; a gap gets an empty cell, the end of the row none. Returns how many.</summary>
        private long WriteRow(Row row, List<Item>? items)
        {
            if (items is null)
            {
                return 0;
            }

            items.Sort((a, b) => a.Column.CompareTo(b.Column));
            long count = 0;
            int next = 0;
            foreach (Item item in items)
            {
                // Merges that overlap, which only a damaged file holds: the first wins.
                if (item.Column < next)
                {
                    continue;
                }

                for (; next < item.Column; next++, count++)
                {
                    row.AddCell();
                }

                DocumentCell cell = row.AddCell();
                count++;
                next = item.Column + item.Span;
                if (item.Span > 1)
                {
                    cell.Format.GridSpan = item.Span;
                }

                if (item.Merge != VerticalMerge.None)
                {
                    cell.Format.VerticalMerge = item.Merge;
                }

                if (item.Merge != VerticalMerge.Continue)
                {
                    Fill(cell, item.Entry);
                }
            }

            return count;
        }

        private void Fill(DocumentCell target, Entry entry)
        {
            Paragraph paragraph = target.Paragraphs.FirstOrDefault() ?? target.AddParagraph();
            CellStyle style = sheet[entry.Row, entry.Column].Style;
            if (AlignmentOf(style.Alignment.Horizontal, sheet.GetKind(entry.Row, entry.Column)) is { } alignment)
            {
                paragraph.Format.Alignment = alignment;
            }

            if (entry.Link is { } link)
            {
                paragraph.AddHyperlink(entry.Text, link);
            }
            else if (style.Font.Bold || style.Font.Italic)
            {
                paragraph.AddRun(entry.Text, new RunFormat { Bold = style.Font.Bold ? true : null, Italic = style.Font.Italic ? true : null });
            }
            else
            {
                paragraph.AddRun(entry.Text);
            }
        }

        /// <summary>The cells that show something, row by row, with their links, marking what merged ranges cover.</summary>
        private void ReadEntries()
        {
            int previousRow = -1;
            bool rowHidden = false;
            foreach (Cell cell in sheet.Cells)
            {
                if (cell.Row != previousRow)
                {
                    previousRow = cell.Row;
                    rowHidden = sheet.Rows[cell.Row].Hidden;
                }

                if (rowHidden || cell.IsEmpty || ColumnHidden(cell.Column) || cell.Text is not { Length: > 0 } text)
                {
                    continue;
                }

                int start = rowEntries.TryGetValue(cell.Row, out (int Start, int End) span) ? span.Start : entries.Count;
                entries.Add(new Entry(cell.Row, cell.Column, text));
                rowEntries[cell.Row] = (start, entries.Count);
            }

            foreach (RangeAddress merged in sheet.MergedCells)
            {
                Each(merged, entry =>
                {
                    if (entry.Row == merged.First.Row && entry.Column == merged.First.Column)
                    {
                        entry.IsMergeOrigin = true;
                    }
                    else
                    {
                        entry.Dropped = true;
                    }
                });
            }

            foreach (Hyperlink link in sheet.Hyperlinks)
            {
                if (link.Address is { Length: > 0 } address)
                {
                    Each(link.Range, entry => entry.Link ??= address);
                }
            }
        }

        private bool ColumnHidden(int column)
        {
            if (!hiddenColumns.TryGetValue(column, out bool hidden))
            {
                hidden = sheet.Columns[column].Hidden;
                hiddenColumns[column] = hidden;
            }

            return hidden;
        }

        /// <summary>Calls <paramref name="action"/> for the entries in the range.</summary>
        private void Each(RangeAddress range, Action<Entry> action)
        {
            // A range over more rows than the sheet stores (a whole column, say) walks the stored rows instead of its own.
            if (range.RowCount <= rowEntries.Count)
            {
                for (int row = range.First.Row; row <= range.Last.Row && budget-- > 0; row++)
                {
                    if (rowEntries.TryGetValue(row, out (int Start, int End) span))
                    {
                        EachInRow(span, range, action);
                    }
                }

                return;
            }

            foreach ((int row, (int Start, int End) span) in rowEntries)
            {
                if (budget-- <= 0)
                {
                    return;
                }

                if (row >= range.First.Row && row <= range.Last.Row)
                {
                    EachInRow(span, range, action);
                }
            }
        }

        private void EachInRow((int Start, int End) span, RangeAddress range, Action<Entry> action)
        {
            for (int i = span.Start; i < span.End && budget-- > 0; i++)
            {
                if (entries[i].Column >= range.First.Column && entries[i].Column <= range.Last.Column)
                {
                    action(entries[i]);
                }
            }
        }

        /// <summary>A merged range shows as a cell spanning the kept columns, on each kept row it covers.</summary>
        private void AddMergedItems(int[] rows, int[] columns, List<Item>?[] items)
        {
            foreach (RangeAddress merged in sheet.MergedCells)
            {
                if (!rowEntries.TryGetValue(merged.First.Row, out (int Start, int End) span))
                {
                    continue;
                }

                Entry? origin = null;
                for (int i = span.Start; i < span.End && origin is null; i++)
                {
                    if (entries[i] is { IsMergeOrigin: true, Dropped: false } entry && entry.Column == merged.First.Column)
                    {
                        origin = entry;
                    }
                }

                if (origin is null || Within(columns, merged.First.Column, merged.Last.Column) is not { } across || Within(rows, merged.First.Row, merged.Last.Row) is not { } down)
                {
                    continue;
                }

                int width = across.Last - across.First + 1;
                for (int r = down.First; r <= down.Last && budget-- > 0; r++)
                {
                    VerticalMerge merge = down.First == down.Last ? VerticalMerge.None : r == down.First ? VerticalMerge.Restart : VerticalMerge.Continue;
                    (items[r] ??= []).Add(new Item(across.First, width, merge, origin));
                }
            }
        }

        /// <summary>A cell that shows something.</summary>
        private sealed class Entry(int row, int column, string text)
        {
            public int Row { get; } = row;

            public int Column { get; } = column;

            public string Text { get; } = text;

            public string? Link { get; set; }

            public bool IsMergeOrigin { get; set; }

            public bool Dropped { get; set; }
        }

        /// <summary>A table cell to write: a position in the kept columns, its span, and what it shows.</summary>
        private readonly record struct Item(int Column, int Span, VerticalMerge Merge, Entry Entry);
    }
}
