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

            if (Fitted(row, c, available, meter) is { } fitted)
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
        bool text = false;
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
                    text = true;
                }

                continue;
            }

            if (Fitted(r, column, ColumnPixels(column, meter) - TextMeter.Padding, meter) is { } fitted)
            {
                tallest = Math.Max(tallest, fitted.Height);
                text = true;
            }
        }

        bool customDefault = DefaultRowHeight is not null && !DefaultRowFollowsFont;
        if (!text)
        {
            // A row without text takes the sheet's default height.
            tallest = normal;
        }

        double points = Math.Min(409.5, tallest * 0.75);
        RowRecord record = RowRecords.TryGetValue(row, out RowRecord? known) ? known : RowRecords[row] = new RowRecord();
        record.Height = double.NaN;
        record.Extra = record.Extra?.Where(a => a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("ht" or "customHeight")).ToArray() is { Length: > 0 } kept ? kept : null;
        if (!text || (tallest == normal && !customDefault))
        {
            // A row without text, or as high as the normal font's: the row follows the sheet's default again, as Excel
            // leaves it.
            record.FittedHeight = double.NaN;
            if (customDefault)
            {
                points = DefaultRowHeight!.Value;
            }
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

    private static string XlsxNamesNumber(double value) => Xlsx.XlsxNames.Number(value);

    /// <summary>The merges that cover part of a range, which AutoFit leaves out or counts by their first cell.</summary>
    private List<RangeAddress> Merges(RangeAddress range) => [.. MergedCells.Where(range.Intersects)];

    /// <summary>
    /// The pixels a cell's text takes across and down as Excel lays it out on screen: its widest line (lines wrapped at
    /// <paramref name="available"/> pixels when the cell wraps) with its indent, turned by its rotation; null for an empty cell.
    /// </summary>
    private (int Width, int Height)? Fitted(int row, int column, int available, TextMeter meter)
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
    private static int Across(string line, CellFont font, char separator, TextMeter meter) =>
        Math.Max((int)meter.Exact(line, font, separator), meter.Width(line, font, separator) + meter.Lead(font));

    /// <summary>The pixels of one step of indent: three spaces of the normal font.</summary>
    private int IndentPixels(TextMeter meter) => 3 * meter.Width(" ", Normal);

    /// <summary>The workbook's normal font: the font of the default cell format.</summary>
    private CellFont Normal => Workbook.Styles.Count > 0 ? Workbook.Styles[0].Font : CellFont.Default;

    /// <summary>A text's lines, wrapped at spaces to fit <paramref name="available"/> pixels.</summary>
    private static List<string> Wrap(string text, CellFont font, int available, TextMeter meter)
    {
        List<string> lines = [];
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            int start = 0, lineStart = 0, lineEnd = 0;
            while (start <= paragraph.Length)
            {
                int end = paragraph.IndexOf(' ', start);
                end = end < 0 ? paragraph.Length : end;
                if (meter.Exact(paragraph.AsSpan(lineStart, end - lineStart), font) > available && start > lineStart)
                {
                    // The word goes onto a new line; the line ends before the space that went before it.
                    lines.Add(paragraph[lineStart..lineEnd]);
                    lineStart = start;
                }

                lineEnd = end;
                start = end + 1;
            }

            lines.Add(paragraph[lineStart..lineEnd]);
        }

        return lines;
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
