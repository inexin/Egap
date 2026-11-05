using System.Globalization;
using System.Text;

using Egap.Pdf.Fuzz;
using Egap.Spreadsheets;

using SharpFuzz;

// Fuzz targets for Egap.Spreadsheets: number format codes and CSV. A format code never makes Parse or Format throw; CSV
// may refuse its input only with an InvalidDataException. Anything else is a finding.
Dictionary<string, Action<Stream>> targets = new(StringComparer.Ordinal)
{
    ["numberformat"] = NumberFormat,
    ["csv"] = Csv,
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
}
