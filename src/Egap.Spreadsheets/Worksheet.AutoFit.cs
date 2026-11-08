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
            if (merges.Count > 0 && merges.Exists(m => m.Contains(new CellAddress(row, c))))
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
        int least = meter.LineHeight(Normal);
        int tallest = least;
        RangeAddress line = new(row, 0, row, CellAddress.MaxColumns - 1);
        List<RangeAddress> merges = Merges(line);
        foreach ((int r, int column) in store.Occupied(line))
        {
            if (merges.Count > 0 && merges.Exists(m => m.Contains(new CellAddress(r, column))))
            {
                continue;
            }

            if (Fitted(r, column, ColumnPixels(column, meter) - TextMeter.Padding, meter) is { } fitted)
            {
                tallest = Math.Max(tallest, fitted.Height);
            }
        }

        double points = Math.Min(409.5, tallest * 0.75);
        RowRecord record = RowRecords.TryGetValue(row, out RowRecord? known) ? known : RowRecords[row] = new RowRecord();
        record.Height = double.NaN;
        record.Extra = record.Extra?.Where(a => a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("ht" or "customHeight")).ToArray() is { Length: > 0 } kept ? kept : null;
        if (tallest == least && DefaultRowHeight is null)
        {
            // The normal font's own row: the row follows the sheet's default again, as Excel leaves it.
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

    private static string XlsxNamesNumber(double value) => Xlsx.XlsxNames.Number(value);

    /// <summary>The merges that cover part of a range: AutoFit leaves merged cells out.</summary>
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
        int lineHeight = meter.LineHeight(font);
        int indent = alignment.Indent * IndentPixels(meter);
        int widest;
        int lines;
        if (alignment.WrapText)
        {
            List<int> wrapped = Wrap(text, font, Math.Max(1, available - indent), meter);
            widest = wrapped.Max();
            lines = wrapped.Count;
        }
        else
        {
            char separator = cell.Value.Kind == CellValueKind.Number && string.Equals(style.NumberFormat, "General", StringComparison.OrdinalIgnoreCase)
                ? Workbook.Culture.NumberFormat.NumberDecimalSeparator[0]
                : '\0';
            widest = meter.Width(text, font, separator);
            lines = 1;
        }

        // Excel widens text by 29/28, but never by less than a lead of a few pixels.
        int across = Math.Max((int)(widest * TextMeter.Scale), widest + TextMeter.Lead(font)) + indent;
        int down = lines * lineHeight;
        int rotation = alignment.TextRotation;
        if (rotation == 0)
        {
            return (across, down);
        }

        // Turned text fits its row by the box around it, its line taking the padding by the square of the slant: all of it
        // upright, as a column fits its text, and half at 45 degrees, as Excel measures both.
        double angle = (rotation == 255 ? 90 : rotation > 90 ? rotation - 90 : rotation) * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
        int height = (int)Math.Round((sin * (across + (TextMeter.Padding * sin * sin))) + (cos * down));
        return ((int)Math.Round((cos * across) + (sin * down * TextMeter.Scale)), height);
    }

    /// <summary>The pixels of one step of indent: three spaces of the normal font.</summary>
    private int IndentPixels(TextMeter meter) => 3 * meter.Width(" ", Normal);

    /// <summary>The workbook's normal font: the font of the default cell format.</summary>
    private CellFont Normal => Workbook.Styles.Count > 0 ? Workbook.Styles[0].Font : CellFont.Default;

    /// <summary>The widths in pixels of a text's lines, wrapped at spaces to fit <paramref name="available"/> pixels.</summary>
    private static List<int> Wrap(string text, CellFont font, int available, TextMeter meter)
    {
        List<int> lines = [];
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            int start = 0, width = 0, lineStart = 0;
            while (start <= paragraph.Length)
            {
                int end = paragraph.IndexOf(' ', start);
                end = end < 0 ? paragraph.Length : end;
                int candidate = meter.Width(paragraph.AsSpan(lineStart, end - lineStart), font);
                if (candidate * TextMeter.Scale > available && start > lineStart)
                {
                    // The word goes onto a new line; the line ends before the space that went before it.
                    lines.Add(width);
                    lineStart = start;
                    candidate = meter.Width(paragraph.AsSpan(lineStart, end - lineStart), font);
                }

                width = candidate;
                start = end + 1;
            }

            lines.Add(width);
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
