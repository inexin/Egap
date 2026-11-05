using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Csv;

/// <summary>Writes a sheet as RFC 4180 text, from A1 to the last used cell, every row with the same number of fields.</summary>
internal static class CsvWriter
{
    public static void Write(Worksheet sheet, Stream stream, string? path, CsvOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new CsvOptions();
        CultureInfo culture = options.Culture;
        char separator = options.SeparatorFor(path) ?? (culture.TextInfo.ListSeparator is [char list] ? list : ',');
        if (separator is '"' or '\r' or '\n')
        {
            throw new ArgumentException("The separator cannot be a quote or a line break.", nameof(options));
        }

        using StreamWriter writer = new(stream, options.Encoding ?? new UTF8Encoding(true), bufferSize: 65536, leaveOpen: true);
        writer.NewLine = options.NewLine;
        if (options.WriteSeparatorHint)
        {
            writer.Write("sep=");
            writer.Write(separator);
            writer.Write(options.NewLine);
        }

        if (sheet.Store.Bounds() is not { } bounds)
        {
            return;
        }

        int width = bounds.Last.Column + 1;
        RangeAddress all = new(new CellAddress(0, 0), bounds.Last);
        int row = 0;
        int column = 0;
        bool date1904 = sheet.Workbook.Date1904;
        NumberFormatCode? lastCode = null;
        int lastStyle = -1;
        foreach ((int r, int c) in sheet.Store.Occupied(all))
        {
            while (row < r)
            {
                EndRow(writer, separator, width, ref column);
                row++;
            }

            for (; column < c; column++)
            {
                writer.Write(separator);
            }

            CellValue value = sheet.GetValue(r, c);
            string text;
            if (options.WriteFormattedText)
            {
                int style = sheet.GetStyleIndex(r, c);
                if (style != lastStyle)
                {
                    lastCode = NumberFormatCode.Get(sheet.Workbook.Styles[style].NumberFormat);
                    lastStyle = style;
                }

                text = lastCode!.Format(value, culture, date1904);
            }
            else
            {
                text = value.Kind == CellValueKind.Number ? value.Number.ToString("R", culture) : NumberFormatCode.General.Format(value, culture);
            }

            WriteField(writer, text, separator);
        }

        while (row <= bounds.Last.Row)
        {
            EndRow(writer, separator, width, ref column);
            row++;
        }
    }

    private static void EndRow(StreamWriter writer, char separator, int width, ref int column)
    {
        for (; column < width - 1; column++)
        {
            writer.Write(separator);
        }

        writer.Write(writer.NewLine);
        column = 0;
    }

    /// <summary>Writes a field, quoted when it holds the separator, a quote or a line break, or starts or ends with a space.</summary>
    private static void WriteField(StreamWriter writer, string text, char separator)
    {
        bool quote = text.Length > 0 && (text[0] == ' ' || text[^1] == ' ' || text.AsSpan().IndexOfAny(separator, '"', '\n') >= 0 || text.Contains('\r', StringComparison.Ordinal));
        if (!quote)
        {
            writer.Write(text);
            return;
        }

        writer.Write('"');
        writer.Write(text.Replace("\"", "\"\"", StringComparison.Ordinal));
        writer.Write('"');
    }
}
