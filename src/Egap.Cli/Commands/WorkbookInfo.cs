using System.Globalization;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Spreadsheets;

namespace Egap.Cli.Commands;

/// <summary>What <c>egap info</c> shows about a workbook: its sheets, their sizes, and the formula errors it saved.</summary>
internal static class WorkbookInfo
{
    /// <summary>One workbook.</summary>
    public sealed record Report(string Path, string Format, IReadOnlyList<SheetReport> Sheets, int Names);

    /// <summary>One sheet; the used range is null for an empty one.</summary>
    public sealed record SheetReport(string Name, string Visibility, string? UsedRange, int Rows, int Columns, long Formulas, IReadOnlyDictionary<string, long> FormulaErrors, int Tables);

    public static Report Read(InputFile file)
    {
        Workbook book = WorkbookFiles.Load(file);
        List<SheetReport> sheets = [];
        foreach (Worksheet sheet in book.Worksheets)
        {
            long formulas = 0;
            SortedDictionary<string, long> errors = new(StringComparer.Ordinal);
            foreach (Cell cell in sheet.Cells)
            {
                if (cell.HasFormula)
                {
                    formulas++;
                    if (cell.Value.Kind == CellValueKind.Error)
                    {
                        string text = CellValue.ErrorText(cell.Value.Error);
                        errors[text] = errors.GetValueOrDefault(text) + 1;
                    }
                }
            }

            RangeAddress? used = sheet.UsedRange?.Address;
            sheets.Add(new SheetReport(
                sheet.Name,
                sheet.Visibility.ToString().ToLowerInvariant(),
                used?.ToString(),
                used is { } rows ? rows.Last.Row - rows.First.Row + 1 : 0,
                used is { } columns ? columns.Last.Column - columns.First.Column + 1 : 0,
                formulas,
                errors,
                sheet.Tables.Count));
        }

        string format = Path.GetExtension(file.Path!).TrimStart('.').ToLowerInvariant();
        return new Report(file.Display, format, sheets, book.Names.Count);
    }

    public static void WriteText(TextWriter output, Report report)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{report.Path}: {report.Format.ToUpperInvariant()} workbook, {report.Sheets.Count} sheet{(report.Sheets.Count == 1 ? "" : "s")}, {report.Names} defined name{(report.Names == 1 ? "" : "s")}"));
        foreach (SheetReport sheet in report.Sheets)
        {
            string errors = sheet.FormulaErrors.Count == 0 ? "" : ", errors: " + string.Join(", ", sheet.FormulaErrors.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Value} {e.Key}")));
            string hidden = sheet.Visibility == "visible" ? "" : $" ({sheet.Visibility})";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {sheet.Name}{hidden}: {sheet.UsedRange ?? "empty"}, {sheet.Rows} x {sheet.Columns}, {sheet.Formulas} formulas{errors}"));
        }
    }

    public static void WriteJson(Utf8JsonWriter json, Report report)
    {
        json.WriteStartObject();
        json.WriteString("path", report.Path);
        json.WriteString("format", report.Format);
        json.WriteNumber("definedNames", report.Names);
        json.WriteStartArray("sheets");
        foreach (SheetReport sheet in report.Sheets)
        {
            json.WriteStartObject();
            json.WriteString("name", sheet.Name);
            json.WriteString("visibility", sheet.Visibility);
            json.WriteString("usedRange", sheet.UsedRange);
            json.WriteNumber("rows", sheet.Rows);
            json.WriteNumber("columns", sheet.Columns);
            json.WriteNumber("formulas", sheet.Formulas);
            json.WriteNumber("tables", sheet.Tables);
            json.WriteStartObject("formulaErrors");
            foreach ((string error, long count) in sheet.FormulaErrors)
            {
                json.WriteNumber(error, count);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}
