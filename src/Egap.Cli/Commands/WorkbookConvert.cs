using Egami;
using Egami.Pixels;

using Egap.Cli.Host;
using Egap.Documents;
using Egap.Pdf;
using Egap.Spreadsheets;

namespace Egap.Cli.Commands;

/// <summary>The directions of <c>egap convert</c> that use a workbook's own printing, its CSV per sheet, or tables put on sheets.</summary>
internal static class WorkbookConvert
{
    /// <summary>The image formats a workbook's printed pages are written as.</summary>
    public static readonly string[] Images = ["png", "jpg", "webp", "tif"];

    /// <summary>A workbook as Excel prints it: its own page setup decides the pages.</summary>
    public static void ToPdf(EgapSession session, InputFile file, string path, bool tagged, PdfAConformance conformance)
    {
        Workbook book = WorkbookFiles.Load(file);
        SheetPdfOptions pdf = new() { Tagged = tagged, Conformance = conformance };
        SaveOptions.WriteFile(session, path, "pdf", temporary => book.SaveAsPdf(temporary, pdf));
    }

    /// <summary>A workbook's printed pages, one image per page.</summary>
    public static List<string> ToImages(EgapSession session, InputFile file, OutputTemplate template, string extension, double dpi)
    {
        if (template.IsStdout)
        {
            throw new UsageException("A workbook's pages are written to one file each; give a folder or a template such as out/{name}-{page}.png.");
        }

        Workbook book = WorkbookFiles.Load(file);
        List<string> written = [];
        int number = 0;
        foreach (SheetPage page in book.Paginate().Pages)
        {
            session.Cancellation.ThrowIfCancellationRequested();
            string path = RenderCommand.PagePath(session, template, file, extension, ++number);
            using Raster<Rgba8> image = page.Render(dpi);
            Output.Write(session, path, stream => image.Save(stream, extension));
            written.Add(path);
        }

        return written;
    }

    /// <summary>Each visible sheet as CSV; with more than one, each file has the sheet's name after the output's.</summary>
    public static List<string> ToCsv(EgapSession session, InputFile file, string path, string? password)
    {
        Workbook book = Sheets(session, file, password);
        List<Worksheet> sheets = [.. book.Worksheets.Where(s => s.Visibility == SheetVisibility.Visible)];
        if (sheets.Count == 0)
        {
            throw new UsageException($"{file.Display} has no visible sheet to write as CSV.");
        }

        if (sheets.Count > 1 && path == "-")
        {
            throw new UsageException($"{file.Display} has {sheets.Count} sheets, which make a CSV file each; give a file or a folder instead of -.");
        }

        List<string> written = [];
        foreach (Worksheet sheet in sheets)
        {
            string target = sheets.Count == 1 ? path : WorkbookFiles.SheetPath(path, sheet.Name);
            Output.Write(session, target, stream => sheet.SaveCsv(stream));
            written.Add(target);
        }

        return written;
    }

    /// <summary>A workbook, or the tables of a document or PDF on sheets, saved as XLSX.</summary>
    public static void ToXlsx(EgapSession session, InputFile file, string path, string? password)
    {
        Workbook book = Sheets(session, file, password);
        SaveOptions.WriteFile(session, path, "xlsx", temporary => book.Save(temporary));
    }

    private static Workbook Sheets(EgapSession session, InputFile file, string? password)
    {
        if (file.Path is null)
        {
            throw new UsageException("convert reads files, not standard input; give the file's name.");
        }

        if (WorkbookFiles.IsWorkbook(file.Path))
        {
            return Workbook.Load(file.Path);
        }

        if (Document.Identify(file.Path) == DocumentFormatKind.Pdf)
        {
            using PdfDocument pdf = PdfInput.Open(session, file, password);
            return Workbook.FromTables(pdf);
        }

        return Workbook.FromTables(Document.Load(file.Path));
    }
}
