using Egap.Cli.Host;
using Egap.Documents;
using Egap.Spreadsheets;

namespace Egap.Cli.Commands;

/// <summary>What the commands that read workbooks share: telling a workbook from other files, and loading one.</summary>
internal static class WorkbookFiles
{
    /// <summary>Whether the file, by its contents and then its extension, is an XLSX package or CSV.</summary>
    public static bool IsWorkbook(string path)
    {
        SpreadsheetFormat.Register();
        return File.Exists(path) && Document.Identify(path) is DocumentFormatKind.Xlsx or DocumentFormatKind.Csv;
    }

    /// <summary>The workbook in an input, which must be a named file holding one.</summary>
    public static Workbook Load(InputFile file)
    {
        if (file.Path is null)
        {
            throw new UsageException("Workbooks are read from files, not standard input; give the file's name.");
        }

        if (!IsWorkbook(file.Path))
        {
            throw new UsageException($"{file.Display} is not an XLSX, XLSM, XLTX, XLTM, CSV or TSV workbook.");
        }

        return Workbook.Load(file.Path);
    }

    /// <summary><paramref name="path"/> with the sheet's name before the extension, what a file system might refuse as underscores.</summary>
    public static string SheetPath(string path, string sheet)
    {
        char[] safe = sheet.ToCharArray();
        for (int i = 0; i < safe.Length; i++)
        {
            if (safe[i] < ' ' || safe[i] is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                safe[i] = '_';
            }
        }

        string name = $"{Path.GetFileNameWithoutExtension(path)}-{new string(safe).Trim(' ', '.')}{Path.GetExtension(path)}";
        return Path.Combine(Path.GetDirectoryName(path) ?? "", name);
    }
}
