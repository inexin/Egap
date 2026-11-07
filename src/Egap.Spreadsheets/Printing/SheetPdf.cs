using Egap.Pdf;

namespace Egap.Spreadsheets.Printing;

/// <summary>Saving printed sheets as PDF, for <see cref="Worksheet"/> and <see cref="Workbook"/>.</summary>
internal static class SheetPdf
{
    public static void Save(Workbook book, IEnumerable<Worksheet> sheets, string? path, Stream? stream, SheetPdfOptions? options, bool chartSheets = false)
    {
        options ??= SheetPdfOptions.Default;
        SheetPrintLayout layout = SheetPrintLayout.Create(book, sheets, options.Print, path is null ? null : Path.ChangeExtension(path, ".xlsx"), chartSheets);
        using PdfDocument pdf = new SheetPdfExporter(layout, options).Export(book, path is null ? null : Path.GetFileNameWithoutExtension(path));
        if (path is not null)
        {
            pdf.Save(path);
        }
        else
        {
            pdf.Save(stream!);
        }
    }
}
