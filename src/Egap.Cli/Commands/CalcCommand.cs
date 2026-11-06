using System.CommandLine;
using System.Globalization;

using Egap.Cli.Host;
using Egap.Spreadsheets;

namespace Egap.Cli.Commands;

/// <summary><c>egap calc</c>: every formula of a workbook calculated again, and the workbook saved with the results.</summary>
internal static class CalcCommand
{
    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new("XLSX, XLSM, XLTX or XLTM files, globs (*, ?, **), or folders with --recursive.");
        OutputOptions output = new();
        Command command = new("calc", "Calculate every formula of workbooks again and save them with the results. Warns of circular references and of cells that show errors.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            return Batch.Run(session, files, batch, file =>
            {
                Workbook book = WorkbookFiles.Load(file);
                string extension = Path.GetExtension(file.Path!).TrimStart('.').ToLowerInvariant();
                string path = template is null ? file.Path! : OutputOptions.PathFor(session, template, file, extension);
                string written = path == "-" ? extension : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (written is not ("xlsx" or "xlsm" or "xltx" or "xltm"))
                {
                    throw new UsageException($"calc writes XLSX, XLSM, XLTX and XLTM; '{written}' is none of them. Use egap convert for other formats.");
                }

                CalculationResult calculation = book.CalculateFull();
                Report(session, file, book, calculation);
                SaveOptions.WriteFile(session, path, written, temporary => book.Save(temporary));
                return [path];
            });
        });
        return command;
    }

    private static void Report(EgapSession session, InputFile file, Workbook book, CalculationResult calculation)
    {
        if (calculation.CircularReferences.Count > 0)
        {
            session.Warn(string.Create(CultureInfo.InvariantCulture, $"{file.Display}: {calculation.CircularReferences.Count} circular references."));
        }

        Dictionary<string, int> errors = new(StringComparer.Ordinal);
        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (Cell cell in sheet.Cells)
            {
                if (cell.HasFormula && cell.Value.Kind == CellValueKind.Error)
                {
                    string text = CellValue.ErrorText(cell.Value.Error);
                    errors[text] = errors.GetValueOrDefault(text) + 1;
                }
            }
        }

        if (errors.Count > 0)
        {
            string list = string.Join(", ", errors.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Value} {e.Key}")));
            session.Warn($"{file.Display}: formula cells show errors: {list}.");
        }
    }
}
