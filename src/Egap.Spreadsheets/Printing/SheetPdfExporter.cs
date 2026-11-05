using System.Globalization;

using Egap.Pdf;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// Writes laid-out sheets to a new PDF (Build Plan task 18): a page per printed page, drawn through the PDF canvas, and for a
/// tagged PDF a structure tree built before drawing: a section per sheet, a table per print range with a row per printed row
/// that has text and a cell per cell with text (a header cell in the title rows, spans for merged ranges), and a figure per
/// picture with its alternative text. What repeats (titles on later pages) and what decorates is an artifact.
/// </summary>
internal sealed class SheetPdfExporter : IPageTagger
{
    private readonly SheetPrintLayout layout;
    private readonly SheetPdfOptions options;
    private readonly Dictionary<(Worksheet Sheet, int Row, int Column), PdfStructureElement> cells = [];
    private readonly HashSet<(Worksheet Sheet, int Row, int Column)> drawn = [];
    private readonly Dictionary<SheetDrawing, PdfStructureElement> figures = [];
    private PdfCanvas? canvas;

    public SheetPdfExporter(SheetPrintLayout layout, SheetPdfOptions options)
    {
        this.layout = layout;
        this.options = options;
    }

    public PdfDocument Export(Workbook book, string? title)
    {
        PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = options.Conformance, UniversalAccessibility = options.UniversalAccessibility });
        try
        {
            string? language = options.Language ?? (book.Culture.Equals(CultureInfo.InvariantCulture) ? null : book.Culture.Name);
            if (options.UniversalAccessibility && string.IsNullOrEmpty(language))
            {
                language = "en-US";
            }

            if (options.IsTagged)
            {
                BuildStructure(pdf, language);
            }

            foreach (SheetPage page in layout.Pages)
            {
                PdfPage pdfPage = pdf.Pages.AddBlank(new PdfRectangle(0, 0, page.Width, page.Height));
                using PdfCanvas pageCanvas = new(pdfPage);
                canvas = pageCanvas;
                new SheetPageRenderer(page, pageCanvas, options.IsTagged ? this : null).Draw();
                canvas = null;
                if (options.IsTagged)
                {
                    pdfPage.Dictionary["Tabs"] = PdfName.Get("S");
                }
            }

            if (layout.Pages.Count == 0)
            {
                // Nothing to print still makes a valid file: one empty page of the first sheet's paper.
                (double width, double height) = PaperSizes.Get(book.Worksheets.Count > 0 ? book.Worksheets[0].PageSetup.PaperSize : null);
                pdf.Pages.AddBlank(new PdfRectangle(0, 0, width, height));
            }

            PdfMetadata metadata = pdf.Metadata;
            metadata.Title = options.Title ?? title ?? (book.Worksheets.Count > 0 ? book.Worksheets[0].Name : null);
            metadata.Creator = "Egap.Spreadsheets";
            if (!string.IsNullOrWhiteSpace(language))
            {
                pdf.Catalog["Lang"] = PdfString.FromText(language);
            }

            return pdf;
        }
        catch
        {
            pdf.Dispose();
            throw;
        }
    }

    /// <summary>The structure tree in reading order: sheet by sheet, range by range, row by row, cell by cell.</summary>
    private void BuildStructure(PdfDocument pdf, string? language)
    {
        PdfStructureElement root = pdf.Structure.Add(PdfStructureType.Document);
        if (!string.IsNullOrWhiteSpace(language))
        {
            root.Language = language;
        }

        foreach (IGrouping<Worksheet, SheetPage> pages in layout.Pages.GroupBy(p => p.Worksheet))
        {
            Worksheet sheet = pages.Key;
            SheetPaginator paginator = pages.First().Paginator;
            SheetGrid grid = paginator.Grid;
            PdfStructureElement section = root.Add(PdfStructureType.Sect);
            section.Dictionary["T"] = PdfString.FromText(sheet.Name);
            foreach (RangeAddress area in paginator.Areas)
            {
                PdfStructureElement table = section.Add(PdfStructureType.Table);
                SortedDictionary<int, SortedDictionary<int, RangeAddress?>> rows = [];
                foreach ((int r, int c) in sheet.Store.Occupied(area))
                {
                    if (sheet.GetValue(r, c).IsEmpty || grid.Row(r) <= 0 || grid.Column(c) <= 0)
                    {
                        continue;
                    }

                    RangeAddress? merged = sheet.MergedCells.Find(new CellAddress(r, c));
                    if (merged is { } m && (m.First.Row != r || m.First.Column != c))
                    {
                        continue;
                    }

                    if (!rows.TryGetValue(r, out SortedDictionary<int, RangeAddress?>? row))
                    {
                        rows[r] = row = [];
                    }

                    row[c] = merged;
                }

                foreach ((int r, SortedDictionary<int, RangeAddress?> row) in rows)
                {
                    PdfStructureElement tr = table.Add(PdfStructureType.TR);
                    bool header = paginator.TitleRows is { } titles && r >= titles.First && r <= titles.Last;
                    foreach ((int c, RangeAddress? merged) in row)
                    {
                        PdfStructureElement cell = tr.Add(header ? PdfStructureType.TH : PdfStructureType.TD);
                        if (header)
                        {
                            PdfDictionary scope = [];
                            scope.Set("Scope", PdfName.Get("Column"));
                            cell.AddAttributes("Table", scope);
                        }

                        if (merged is { } m && (m.RowCount > 1 || m.ColumnCount > 1))
                        {
                            PdfDictionary spans = [];
                            spans.Set("RowSpan", m.RowCount);
                            spans.Set("ColSpan", m.ColumnCount);
                            cell.AddAttributes("Table", spans);
                        }

                        cells[(sheet, r, c)] = cell;
                    }
                }

                if (rows.Count == 0)
                {
                    section.Remove(table);
                }
            }

            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                if (drawing.Kind == SheetDrawingKind.Picture && drawing.ImageData is not null)
                {
                    PdfStructureElement figure = section.Add(PdfStructureType.Figure);
                    figure.AlternateText = string.IsNullOrWhiteSpace(drawing.Description) ? (drawing.Name.Length > 0 ? drawing.Name : "Picture") : drawing.Description;
                    figures[drawing] = figure;
                }
            }
        }
    }

    public void BeginArtifact() => canvas!.BeginArtifact();

    public void BeginCell(SheetPage page, int row, int column, RangeAddress? merged)
    {
        (Worksheet, int, int) key = (page.Worksheet, row, column);
        if (cells.TryGetValue(key, out PdfStructureElement? element) && drawn.Add(key))
        {
            canvas!.BeginTag(element);
        }
        else
        {
            canvas!.BeginArtifact();
        }
    }

    public void BeginFigure(SheetPage page, SheetDrawing drawing)
    {
        if (figures.TryGetValue(drawing, out PdfStructureElement? figure))
        {
            canvas!.BeginTag(figure);
        }
        else
        {
            canvas!.BeginArtifact();
        }
    }

    public void End() => canvas!.EndTag();
}
