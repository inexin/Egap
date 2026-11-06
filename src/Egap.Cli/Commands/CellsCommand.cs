using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Spreadsheets;

namespace Egap.Cli.Commands;

/// <summary><c>egap cells</c>: a range of a workbook, as tab-separated text or JSON; the MCP tool that reads cells.</summary>
internal static class CellsCommand
{
    /// <summary>The version of the <c>cells --json</c> schema, docs/schemas/egap-cells-1.json.</summary>
    public const int Schema = 1;

    /// <summary>The most cells one file gives unless <c>--max-cells</c> says otherwise.</summary>
    public const int DefaultMaxCells = 10_000;

    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new("XLSX, XLSM, XLTX, XLTM, CSV or TSV files, globs (*, ?, **), or folders with --recursive.");
        Option<string?> range = new("--range", "-r") { Description = "The cells, such as A1:C10, 'Sheet 2'!A1:C10 or Sheet2; the first visible sheet's used range by default.", HelpName = "range" };
        Option<bool> formulas = new("--formulas") { Description = "Also give each cell's formula." };
        Option<bool> shown = new("--shown") { Description = "Give values as the cells show them, formatted, instead of as they are." };
        Option<bool> calculate = new("--calculate") { Description = "Calculate every formula first, instead of reading the values the file saved." };
        Option<int> maxCells = new("--max-cells") { Description = $"The most cells to give for one file; {DefaultMaxCells} by default. Whole rows past it are left out, and the JSON says truncated.", HelpName = "count", DefaultValueFactory = _ => DefaultMaxCells };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-cells-1.json) instead of tab-separated text." };
        Command command = new("cells", "Read a range of cells from workbooks, as tab-separated text or JSON. Nothing is written.");
        inputs.AddTo(command);
        foreach (Option option in (Option[])[range, formulas, shown, calculate, maxCells, json])
        {
            command.Options.Add(option);
        }

        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            int most = result.GetValue(maxCells);
            if (most <= 0)
            {
                throw new UsageException("--max-cells must be above 0.");
            }

            string? wanted = result.GetValue(range);
            bool asShown = result.GetValue(shown), withFormulas = result.GetValue(formulas), recalculate = result.GetValue(calculate);
            Found?[] found = new Found?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                Workbook book = WorkbookFiles.Load(file);
                if (recalculate)
                {
                    book.CalculateFull();
                }

                found[file.Index] = Read(file, book, wanted, most, asShown, withFormulas);
                return [];
            });

            if (result.GetValue(json))
            {
                JsonOut.Write(session.Console.Text, writer => WriteJson(writer, found));
            }
            else
            {
                foreach (Found? one in found)
                {
                    if (one is not null)
                    {
                        WriteText(session.Console.Text, one);
                    }
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>The cells read from one file: per cell a value and, when asked for, a formula.</summary>
    private sealed record Found(string Path, string Sheet, string? Range, List<List<(object? Value, string? Formula)>> Rows, bool Truncated, bool WithFormulas);

    private sealed record ErrorValue(string Text);

    private static Found Read(InputFile file, Workbook book, string? wanted, int most, bool shown, bool withFormulas)
    {
        (Worksheet sheet, RangeAddress? address) = Resolve(book, wanted);
        List<List<(object? Value, string? Formula)>> rows = [];
        if (address is not { } cells)
        {
            return new Found(file.Display, sheet.Name, null, rows, false, withFormulas);
        }

        int columns = cells.Last.Column - cells.First.Column + 1, last = cells.First.Row - 1;
        bool truncated = false;
        long total = 0;
        for (int r = cells.First.Row; r <= cells.Last.Row; r++)
        {
            if (total + columns > most)
            {
                truncated = true;
                break;
            }

            total += columns;
            last = r;
            List<(object? Value, string? Formula)> row = new(columns);
            for (int c = cells.First.Column; c <= cells.Last.Column; c++)
            {
                Cell cell = sheet[r, c];
                row.Add((shown ? cell.Text : Value(cell.Value), withFormulas && cell.HasFormula ? cell.Formula : null));
            }

            rows.Add(row);
        }

        string text = rows.Count == 0 ? cells.ToString() : new RangeAddress(cells.First, new CellAddress(last, cells.Last.Column)).ToString();
        return new Found(file.Display, sheet.Name, text, rows, truncated, withFormulas);
    }

    /// <summary>The sheet and cells <paramref name="wanted"/> names: <c>A1:C3</c>, <c>Sheet!A1:C3</c> or <c>Sheet</c>.</summary>
    private static (Worksheet Sheet, RangeAddress? Cells) Resolve(Workbook book, string? wanted)
    {
        Worksheet First() => book.Worksheets.FirstOrDefault(s => s.Visibility == SheetVisibility.Visible) ?? (book.Worksheets.Count > 0 ? book.Worksheets[0] : null)
            ?? throw new UsageException("The workbook has no sheets.");
        Worksheet? Named(string name) => book.Worksheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (wanted is null)
        {
            Worksheet sheet = First();
            return (sheet, sheet.UsedRange?.Address);
        }

        int bang = wanted.LastIndexOf('!');
        if (bang < 0 && Named(wanted) is { } whole)
        {
            return (whole, whole.UsedRange?.Address);
        }

        Worksheet target = First();
        if (bang >= 0)
        {
            string name = Unquote(wanted[..bang]);
            target = Named(name) ?? throw new UsageException($"The workbook has no sheet '{name}'; it has {string.Join(", ", book.Worksheets.Select(s => s.Name))}.");
        }

        try
        {
            return (target, RangeAddress.Parse(wanted[(bang + 1)..]));
        }
        catch (FormatException)
        {
            throw new UsageException($"--range '{wanted}' is no cells, such as A1:C10 or 'Sheet 2'!A1:C10, nor a sheet of the workbook.");
        }
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && name[0] == '\'' && name[^1] == '\'' ? name[1..^1].Replace("''", "'", StringComparison.Ordinal) : name;

    private static object? Value(CellValue value) => value.Kind switch
    {
        CellValueKind.Number => value.Number,
        CellValueKind.Text => value.Text,
        CellValueKind.Boolean => value.Boolean,
        CellValueKind.Error => new ErrorValue(CellValue.ErrorText(value.Error)),
        _ => null,
    };

    private static void WriteText(TextWriter output, Found found)
    {
        foreach (List<(object? Value, string? Formula)> row in found.Rows)
        {
            output.WriteLine(string.Join('\t', row.Select(c => found.WithFormulas && c.Formula is not null ? "=" + c.Formula : Text(c.Value))));
        }
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "TRUE" : "FALSE",
        ErrorValue error => error.Text,
        _ => (string)value,
    };

    private static void WriteJson(Utf8JsonWriter json, Found?[] found)
    {
        json.WriteStartObject();
        json.WriteNumber("schema", Schema);
        json.WriteStartArray("files");
        foreach (Found? one in found)
        {
            if (one is null)
            {
                continue;
            }

            json.WriteStartObject();
            json.WriteString("path", one.Path);
            json.WriteString("sheet", one.Sheet);
            json.WriteString("range", one.Range);
            json.WriteBoolean("truncated", one.Truncated);
            json.WriteStartArray("rows");
            foreach (List<(object? Value, string? Formula)> row in one.Rows)
            {
                json.WriteStartArray();
                foreach ((object? value, _) in row)
                {
                    WriteValue(json, value);
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            if (one.WithFormulas)
            {
                json.WriteStartArray("formulas");
                foreach (List<(object? Value, string? Formula)> row in one.Rows)
                {
                    json.WriteStartArray();
                    foreach ((_, string? formula) in row)
                    {
                        json.WriteStringValue(formula);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case double number:
                json.WriteNumberValue(number);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case ErrorValue error:
                json.WriteStartObject();
                json.WriteString("error", error.Text);
                json.WriteEndObject();
                break;
            default:
                json.WriteStringValue((string)value);
                break;
        }
    }
}
