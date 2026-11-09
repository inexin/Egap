using System.Xml.Linq;

using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets;

/// <summary>Fitting columns to their text and rows to their fonts, as Excel's AutoFit does.</summary>
public sealed partial class Worksheet
{
    /// <summary>
    /// Fits a column to the widest text of its cells from <paramref name="firstRow"/> to <paramref name="lastRow"/>, as Excel's
    /// AutoFit does; null, and the column left as it is, when none of those cells has text.
    /// </summary>
    internal double? AutoFitColumn(int column, int firstRow, int lastRow, TextMeter meter)
    {
        List<RangeAddress> merges = Merges(new RangeAddress(firstRow, column, lastRow, column));
        int widest = -1;
        int available = ColumnPixels(column, meter) - TextMeter.Padding;
        foreach ((int row, int c) in store.Occupied(new RangeAddress(firstRow, column, lastRow, column)))
        {
            // A merge across columns is left out; one down a single column counts by its first cell, as Excel has it.
            if (merges.Count > 0 && merges.Exists(m => m.Contains(new CellAddress(row, c)) && (m.ColumnCount > 1 || m.First.Row != row)))
            {
                continue;
            }

            if (Fitted(row, c, available, meter, RowPixels(row, meter)) is { } fitted)
            {
                widest = Math.Max(widest, fitted.Width);
            }
        }

        if (widest < 0)
        {
            return null;
        }

        int digit = meter.MaxDigitWidth;
        double width = Math.Min(255, Math.Truncate((widest + TextMeter.Padding) * 256.0 / digit) / 256);
        ColumnSpans.Change(column, span => span.Width = width);
        return width;
    }

    /// <summary>Fits a row to the tallest text of its cells, as Excel's AutoFit does, and gives its height in points.</summary>
    internal double AutoFitRow(int row, TextMeter meter)
    {
        int normal = meter.LineHeight(Normal);
        int tallest = EmptyCellsLineHeight(row, meter);
        RangeAddress line = new(row, 0, row, CellAddress.MaxColumns - 1);
        List<RangeAddress> merges = Merges(line);
        foreach ((int r, int column) in store.Occupied(line))
        {
            int at = merges.Count > 0 ? merges.FindIndex(m => m.Contains(new CellAddress(r, column))) : -1;
            if (at >= 0)
            {
                RangeAddress merge = merges[at];
                // A merge within the row holds it to one line of its font; Excel leaves taller merges out.
                if (merge.RowCount == 1 && merge.First == new CellAddress(r, column) && !this[r, column].Value.IsEmpty)
                {
                    tallest = Math.Max(tallest, meter.LineHeight(this[r, column].Style.Font));
                }

                continue;
            }

            if (Fitted(r, column, ColumnPixels(column, meter) - TextMeter.Padding, meter, null) is { } fitted)
            {
                tallest = Math.Max(tallest, fitted.Height);
            }
            else
            {
                // An empty cell with a format holds the row to a line of its font.
                tallest = Math.Max(tallest, meter.LineHeight(this[r, column].Style.Font));
            }
        }

        tallest += BorderPixels(row);
        bool customDefault = DefaultRowHeight is not null && !DefaultRowFollowsFont;
        double points = Math.Min(409.5, tallest * 0.75);
        RowRecord record = RowRecords.TryGetValue(row, out RowRecord? known) ? known : RowRecords[row] = new RowRecord();
        record.Height = double.NaN;
        record.Extra = record.Extra?.Where(a => a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("ht" or "customHeight")).ToArray() is { Length: > 0 } kept ? kept : null;
        if (tallest == normal && !customDefault)
        {
            // A row as high as the normal font's follows the sheet's default again, as Excel leaves it.
            record.FittedHeight = double.NaN;
        }
        else
        {
            record.FittedHeight = points;
            record.Extra = [.. record.Extra ?? [], new XAttribute("ht", XlsxNamesNumber(points))];
        }

        if (record.IsDefault)
        {
            RowRecords.Remove(row);
        }

        return points;
    }

    /// <summary>
    /// The line height the row's empty cells hold it to: the font of the row's style, or else the tallest of the columns'
    /// styles, with the normal font for columns without one.
    /// </summary>
    private int EmptyCellsLineHeight(int row, TextMeter meter)
    {
        if (RowRecords.TryGetValue(row, out RowRecord? record) && record.Style >= 0)
        {
            return meter.LineHeight(Workbook.Styles[record.Style].Font);
        }

        int tallest = 0;
        long styled = 0;
        foreach (ColumnSpan span in ColumnSpans.Spans)
        {
            if (span.Style >= 0)
            {
                styled += span.Last - span.First + 1;
                tallest = Math.Max(tallest, meter.LineHeight(Workbook.Styles[span.Style].Font));
            }
        }

        return styled < CellAddress.MaxColumns ? Math.Max(tallest, meter.LineHeight(Normal)) : tallest;
    }

    /// <summary>
    /// The pixels a row's borders add to it, as Excel draws lines wider than a pixel between rows: a medium line under the row
    /// adds one, and a thick or double line adds one to the rows on both sides of it.
    /// </summary>
    private int BorderPixels(int row)
    {
        int above = 0, below = 0;
        RangeAddress rows = new(Math.Max(0, row - 1), 0, Math.Min(CellAddress.MaxRows - 1, row + 1), CellAddress.MaxColumns - 1);
        foreach ((int r, int column) in store.Occupied(rows))
        {
            CellBorders borders = this[r, column].Style.Borders;
            if (r == row - 1)
            {
                above = Math.Max(above, Weight(borders.Bottom.Style));
            }
            else if (r == row)
            {
                above = Math.Max(above, Weight(borders.Top.Style));
                below = Math.Max(below, Weight(borders.Bottom.Style));
            }
            else
            {
                below = Math.Max(below, Weight(borders.Top.Style));
            }
        }

        return (below >= 2 ? 1 : 0) + (above >= 3 ? 1 : 0);

        static int Weight(CellBorderStyle style) => style switch
        {
            CellBorderStyle.None => 0,
            CellBorderStyle.Medium or CellBorderStyle.MediumDashed or CellBorderStyle.MediumDashDot or CellBorderStyle.MediumDashDotDot
                or CellBorderStyle.SlantDashDot => 2,
            CellBorderStyle.Thick or CellBorderStyle.Double => 3,
            _ => 1,
        };
    }

    private static string XlsxNamesNumber(double value) => Xlsx.XlsxNames.Number(value);

    /// <summary>The merges that cover part of a range, which AutoFit leaves out or counts by their first cell.</summary>
    private List<RangeAddress> Merges(RangeAddress range) => [.. MergedCells.Where(range.Intersects)];

    /// <summary>
    /// The pixels a cell's text takes across and down as Excel lays it out on screen: its widest line (lines wrapped at
    /// <paramref name="available"/> pixels when the cell wraps) with its indent, turned by its rotation; null for an empty cell.
    /// To fit a column, <paramref name="rowPixels"/> is the height of the cell's row: wrapped text that needs more lines than
    /// the row shows is wrapped wider until it fits them, as Excel widens the column.
    /// </summary>
    private (int Width, int Height)? Fitted(int row, int column, int available, TextMeter meter, int? rowPixels)
    {
        Cell cell = this[row, column];
        if (cell.Value.IsEmpty)
        {
            return null;
        }

        string text = cell.Text;
        if (text.Length == 0)
        {
            return null;
        }

        CellStyle style = cell.Style;
        CellFont font = style.Font;
        CellAlignment alignment = style.Alignment;

        // Excel measures text across at the sheet's zoom, its font at the zoomed size, and scales the result back; rows
        // keep their font's height at any zoom.
        int lineHeight = meter.LineHeight(font);
        double zoom = View.ZoomScale / 100.0;
        if (zoom != 1)
        {
            font = font with { Size = font.Size * zoom };
            available = (int)(available * zoom);
        }

        int indent = alignment.Indent * IndentPixels(meter);
        int across;
        int lines;
        if (alignment.WrapText)
        {
            List<string> wrapped = Wrap(text, font, Math.Max(1, available - indent), meter);
            int shown = rowPixels is { } rowHeight ? Math.Max(1, rowHeight / lineHeight) : int.MaxValue;
            if (wrapped.Count > shown)
            {
                // The fewest pixels that wrap the text into the lines the row shows, but no wider than a column can be.
                int low = Math.Max(1, available - indent), high = (int)(((255 * meter.MaxDigitWidth) + TextMeter.Padding) * zoom);
                List<string> widest = Wrap(text, font, high, meter);
                if (widest.Count <= shown)
                {
                    wrapped = widest;
                    while (high - low > 1)
                    {
                        int middle = (low + high) / 2;
                        List<string> tried = Wrap(text, font, middle, meter);
                        if (tried.Count <= shown)
                        {
                            high = middle;
                            wrapped = tried;
                        }
                        else
                        {
                            low = middle;
                        }
                    }
                }
                else
                {
                    wrapped = widest;
                }
            }

            across = wrapped.Max(l => Across(l, font, '\0', meter)) + indent;
            lines = wrapped.Count;
        }
        else
        {
            char separator = cell.Value.Kind == CellValueKind.Number && string.Equals(style.NumberFormat, "General", StringComparison.OrdinalIgnoreCase)
                ? Workbook.Culture.NumberFormat.NumberDecimalSeparator[0]
                : '\0';
            across = Across(text, font, separator, meter) + indent;
            lines = 1;
        }

        int down = lines * lineHeight;
        int rotation = alignment.TextRotation;
        if (rotation == 0)
        {
            return Unzoomed(across, down, zoom);
        }

        // Turned text is measured in whole pixels widened by 29/28, not by Excel's measure of its line.
        if (!alignment.WrapText)
        {
            int whole = meter.Width(text, font);
            across = Math.Max((int)(whole * TextMeter.Scale), whole + meter.Lead(font)) + indent;
        }

        // Turned text fits its row by the box around it, its line taking the padding by the square of the slant: all of it
        // upright, as a column fits its text, and half at 45 degrees, as Excel measures both.
        double angle = (rotation == 255 ? 90 : rotation > 90 ? rotation - 90 : rotation) * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
        int height = (int)Math.Round((sin * (across + (TextMeter.Padding * sin * sin))) + (cos * down));
        return Unzoomed((int)Math.Round((cos * across) + (sin * down * TextMeter.Scale)), height, zoom);
    }

    /// <summary>A cell's pixels across at a zoom, without the column's padding, as they come to at 100 %.</summary>
    private static (int Width, int Height) Unzoomed(int across, int down, double zoom) => zoom == 1
        ? (across, down)
        : ((int)((across + TextMeter.Padding) / zoom) - TextMeter.Padding, down);

    /// <summary>
    /// The pixels a line takes across: Excel's measure of it, but never less than its whole pixels and a lead.
    /// </summary>
    private static int Across(ReadOnlySpan<char> line, CellFont font, char separator, TextMeter meter) =>
        Math.Max((int)meter.Exact(line, font, separator), meter.Width(line, font, separator) + meter.Lead(font));

    /// <summary>The pixels of one step of indent: three spaces of the normal font.</summary>
    private int IndentPixels(TextMeter meter) => 3 * meter.Width(" ", Normal);

    /// <summary>The workbook's normal font: the font of the default cell format.</summary>
    private CellFont Normal => Workbook.Styles.Count > 0 ? Workbook.Styles[0].Font : CellFont.Default;

    /// <summary>
    /// A text's lines as Excel wraps them to fit <paramref name="available"/> pixels: each line as long as it fits, measured
    /// as a column fits its text, broken at a space or after a hyphen, and a word wider than the line cut between characters.
    /// </summary>
    private static List<string> Wrap(string text, CellFont font, int available, TextMeter meter)
    {
        List<string> lines = [];
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            int start = 0;
            do
            {
                if (Fits(paragraph, start, paragraph.Length))
                {
                    lines.Add(paragraph[start..]);
                    break;
                }

                int end = -1;
                for (int i = start; i < paragraph.Length; i++)
                {
                    int stop = paragraph[i] == ' ' ? i : paragraph[i] == '-' ? i + 1 : -1;
                    if (stop < 0)
                    {
                        continue;
                    }

                    if (!Fits(paragraph, start, stop))
                    {
                        break;
                    }

                    end = stop;
                }

                if (end <= start)
                {
                    // No break fits: the word is cut after the last character that fits, and at least one.
                    end = start + 1;
                    while (end < paragraph.Length && paragraph[end] != ' ' && Fits(paragraph, start, end + 1))
                    {
                        end++;
                    }
                }

                lines.Add(paragraph[start..end]);
                start = end;
                while (start < paragraph.Length && paragraph[start] == ' ')
                {
                    start++;
                }
            }
            while (start < paragraph.Length);
        }

        return lines;

        bool Fits(string paragraph, int from, int to) => Across(paragraph.AsSpan(from, to - from), font, '\0', meter) <= available;
    }

    /// <summary>A row's height in pixels on screen: its own height, else the sheet's default, else the normal font's line.</summary>
    private int RowPixels(int row, TextMeter meter)
    {
        double points = RowRecords.TryGetValue(row, out RowRecord? record) && !double.IsNaN(record.Height) ? record.Height
            : record is not null && !double.IsNaN(record.FittedHeight) ? record.FittedHeight
            : DefaultRowHeight ?? (meter.LineHeight(Normal) * 0.75);
        return (int)Math.Round(points / 0.75);
    }

    /// <summary>A column's width in pixels on screen, from its width in characters and the normal font's widest digit.</summary>
    private int ColumnPixels(int column, TextMeter meter)
    {
        int digit = meter.MaxDigitWidth;
        double? width = ColumnSpans.Find(column) is { } span && !double.IsNaN(span.Width) ? span.Width : DefaultColumnWidth;
        if (width is not { } characters)
        {
            return (((8 * digit) + 5 + 7) / 8) * 8;
        }

        return (int)Math.Truncate(((256 * characters) + Math.Truncate(128.0 / digit)) / 256 * digit);
    }
}
