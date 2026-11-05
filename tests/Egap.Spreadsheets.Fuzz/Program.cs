using System.Globalization;
using System.Text;

using Egap.Documents;
using Egap.Pdf.Fuzz;
using Egap.Spreadsheets;

using SharpFuzz;

// Fuzz targets for Egap.Spreadsheets: number format codes, CSV and XLSX. A format code never makes Parse or Format throw;
// CSV may refuse its input only with an InvalidDataException, and XLSX only with a DocumentException (a
// DocumentLimitException among them). Anything else is a finding.
Dictionary<string, Action<Stream>> targets = new(StringComparer.Ordinal)
{
    ["numberformat"] = NumberFormat,
    ["csv"] = Csv,
    ["xlsx"] = Xlsx,
};

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(targets[mutateTarget], TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    targets[target](input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(targets[stdinTarget]);
    return 0;
}

Console.Error.WriteLine($"Usage: Egap.Spreadsheets.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings>. Targets: {string.Join(", ", targets.Keys)}.");
return 2;

static byte[] Bytes(Stream input)
{
    using MemoryStream buffer = new();
    input.CopyTo(buffer);
    return buffer.ToArray();
}

// The input is a format code; its bytes, read eight at a time, are also numbers to show through it.
static void NumberFormat(Stream input)
{
    byte[] bytes = Bytes(input);
    NumberFormatCode code = NumberFormatCode.Parse(Encoding.UTF8.GetString(bytes));
    List<double> values = [0, 1, -1, 0.5, 1e-300, 1e300, -double.MaxValue, 45366.75, 2958465.99999999];
    for (int i = 0; i + 8 <= bytes.Length && values.Count < 32; i += 8)
    {
        double value = BitConverter.ToDouble(bytes, i);
        if (double.IsFinite(value))
        {
            values.Add(value);
        }
    }

    CultureInfo[] cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("ar-SA")];
    foreach (CultureInfo culture in cultures)
    {
        foreach (double value in values)
        {
            _ = code.Format(value, culture, date1904: false);
            _ = code.Format(value, culture, date1904: true);
            _ = code.GetColor(value);
        }

        _ = code.Format("text", culture);
    }
}

// The input is CSV text: read with type detection, written back, and read again.
static void Csv(Stream input)
{
    Workbook book;
    try
    {
        book = Workbook.LoadCsv(input);
    }
    catch (InvalidDataException)
    {
        return;
    }

    MemoryStream output = new();
    book.Worksheets[0].SaveCsv(output);
    output.Position = 0;
    Workbook.LoadCsv(output);

    input.Position = 0;
    using SheetReader reader = SheetReader.OpenCsv(input);
    while (reader.Read())
    {
        for (int column = 0; column < reader.ColumnCount; column++)
        {
            _ = reader.GetText(column);
        }
    }
}

// The input is an XLSX package: loaded, saved and loaded again, and every sheet streamed through SheetReader.
static void Xlsx(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    byte[] bytes = Bytes(input);
    try
    {
        Workbook book = Workbook.Load(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = limits });
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.UsedRange?.Address is { } used)
            {
                for (int row = used.First.Row; row <= Math.Min(used.Last.Row, used.First.Row + 200); row++)
                {
                    for (int column = used.First.Column; column <= Math.Min(used.Last.Column, used.First.Column + 50); column++)
                    {
                        _ = sheet[row, column].Text;
                    }
                }
            }

            foreach (SheetTable table in sheet.Tables)
            {
                table.Name = table.Name;
            }

            CellAddress last = new(CellAddress.MaxRows - 1, 0);
            if (sheet.Comments[last] is null)
            {
                sheet.Comments.Add(last, "fuzz");
            }
        }

        MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Workbook.Load(saved);
    }
    catch (DocumentException)
    {
    }
    catch (InvalidOperationException e) when (e.Message.Contains("sheet", StringComparison.Ordinal))
    {
        // A workbook with no worksheets cannot be saved.
    }

    try
    {
        using SheetReader reader = SheetReader.Open(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = limits });
        for (int sheet = 0; sheet < reader.SheetNames.Count; sheet++)
        {
            reader.SelectSheet(sheet);
            while (reader.Read())
            {
                for (int column = 0; column < Math.Min(reader.ColumnCount, 64); column++)
                {
                    _ = reader.GetText(column);
                }
            }
        }
    }
    catch (DocumentException)
    {
    }
}
