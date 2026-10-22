# Reports in code

`Report.Create` builds a document from code with nested lambdas: pages with a header, a footer and content, and in them
columns, rows, tables, grids, text, pictures, barcodes and form fields. It builds an ordinary `Document`, so the same report
saves as PDF, DOCX or HTML and lays out with the same page flow as any other document. The types are in
`Egap.Documents.Reports`.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Documents.Reports;
using Egap.Pdf;

(string Name, int Qty, decimal Price)[] lines = [("Paper", 2, 40m), ("Toner", 1, 590m)];
Document invoice = Report.Create(r => r
    .Title("Invoice 2026-118")
    .DefaultTextStyle(s => s.FontSize(10))
    .Page(p => p
        .Size(PdfPageSize.A4).Margins(Length.Mm(15))
        .Header(h => h.BorderBottom(1).Row(row =>
        {
            row.Grow().Text("Acme Ltd").Style(s => s.FontSize(20).Bold());
            row.Auto().AlignRight().Text("Invoice 2026-118");
        }))
        .Content(c => c.Column(col =>
        {
            col.Spacing(8);
            col.Item().Text("Thank you for your order.");
            col.Item().Table(t => t
                .Columns(cols => cols.Relative(3).Fixed(40).Fixed(70))
                .Lines(0.5)
                .Header("Item", "Qty", "Price")
                .Rows(lines, l => [l.Name, l.Qty, l.Price]));
            col.Item().KeepTogether().Background("F4F8FC").CornerRadius(6).Padding(8)
                .AlignRight().Text("Total: 670").Bold();
        }))
        .Footer(f => f.AlignCenter().Text($"Page {Fields.Page} of {Fields.Pages}"))));
invoice.SaveAsPdf("invoice.pdf");
invoice.Save("invoice.docx");
```

## Containers

Each place in a report is a `Container`. Settings come first and return the container: a box (`Padding`, `Background`,
`Border`, `BorderBottom`, `CornerRadius`, `Shadow`, `Width`, `MinHeight`, `MarginTop`), the alignment of text inside
(`AlignLeft`, `AlignCenter`, `AlignRight`, `AlignJustify`), a `DefaultTextStyle`, and paging (`KeepTogether`,
`EnsureSpace`, `ShowEntire`, `StopPaging`, `ScaleToFit`). One element ends the chain; a container holds one, and
`Column` puts several one under another.

| Element | Builds |
| --- | --- |
| `Text("...")`, `Text(t => ...)` | A paragraph; spans with their own styles, page numbers, line breaks and links |
| `Image`, `Barcode` | A picture, held to the width there is unless a size is given; a barcode drawn as vectors |
| `Column` | Elements one under another, with `Spacing` between them |
| `Row` | A flex container: `Fixed` widths, `Grow` shares and `Auto` content widths side by side |
| `Grid` | A grid container: column tracks, spans and named areas |
| `Table` | A table: `Columns`, `Header` and `Footer` rows that repeat on every page, cells in order, `Rows` from data |
| `Line`, `PageBreak` | A rule across the width; a break to the next page |
| `TextField`, `CheckBox`, `DropDown` | Form controls that PDF export can turn into fields |
| `Component` | An `IComponent`, which composes itself into the container |

`{page}` and `{total-pages}` in text (`Fields.Page` and `Fields.Pages`) become page fields.

## Styles

A `TextStyle` is immutable: each method returns a changed copy, so a style can be kept in a field and shared. What a style
leaves unset comes from the text around it: the report's `DefaultTextStyle`, the page's, then the container's. A named
style made with `ReportBuilder.Style` is also a character style in the document, so DOCX keeps it by name.

```csharp
using Egap.Documents;
using Egap.Documents.Reports;

TextStyle note = TextStyle.Default.FontSize(8).Italic().FontColor("666666");
Document letter = Report.Create(r => r
    .Style("Strong", s => s.Bold().FontColor("993300"))
    .Page(p => p.Content(c => c.Column(col =>
    {
        col.Item().Text("Payment is due within 14 days.").Style("Strong");
        col.Item().Text("Prices include VAT.").Style(note);
    }))));
```

## Components

A component is a class with a `Compose` method. It fills the container it is given, with any settings the caller put on it.

```csharp
using Egap.Documents;
using Egap.Documents.Reports;

Document report = Report.Create(r => r.Page(p => p.Content(c => c.Row(row =>
{
    row.Grow().Component(new AddressBlock("Acme Ltd", "Storgata 1, Oslo"));
    row.Grow().Component(new AddressBlock("Customer AS", "Kirkegata 9, Bergen"));
}))));

/// <summary>A name and a street in a thin box.</summary>
sealed class AddressBlock(string name, string street) : IComponent
{
    public void Compose(Container container)
    {
        container.Padding(4).Border(0.5).Column(col =>
        {
            col.Item().Text(name).Bold();
            col.Item().Text(street);
        });
    }
}
```

## Live preview

The `egap-preview` dotnet tool shows documents on a local web page, drawn by Egap's own renderer, and shows them again when
they change. `ShowPreview()` sends a document's pages to it; run the program under `dotnet watch` and the page follows each
edit. Given a DOCX file instead, the tool draws the file again whenever it is saved. It listens on
127.0.0.1 only, on port 12500 unless `--port` says otherwise, and `ShowPreview()` returns false when it is not running.
It answers only requests addressed to 127.0.0.1 or localhost from programs and its own page, so other web pages can
neither send it pages nor read the ones it shows.

```sh
dotnet tool install -g Egap.Preview
egap-preview                  # waits for ShowPreview()
egap-preview letter.docx      # or shows a file
```

```csharp
using Egap.Documents;
using Egap.Documents.Reports;

Document report = Report.Create(r => r.Page(p => p.Content(c => c.Text("Draft"))));
report.ShowPreview();                                            // dotnet watch run: the page follows every edit
```

HTML and Markdown are previewed from code, with the add-on that reads them: the tool itself only shows pages. To redraw
an HTML template on every save, have `dotnet watch` watch it too, with `<Watch Include="**/*.html" />` in an item group
of the project file.

```csharp
using Egap.Documents;

HtmlFormat.Register();                                           // Egap.Documents.Html
Document invoice = Document.Load("invoice.html");
invoice.ShowPreview();
```

A strict layout that fails shows its message on the page, above the last pages that laid out, and is thrown as well.

## Strict layout and portable fonts

A report's layout is strict and uses only portable fonts (`Document.DefaultLayoutOptions`): text that does not fit its
column, a character no font has or a group that cannot be shown entire throws a `LayoutException` naming the node, and the
report prints the same on every machine. Pass other `LayoutOptions` to `Report.Create`, or set `DefaultLayoutOptions`, to
change that.

DOCX keeps what Word can hold: rows and grids become borderless tables, and boxes become one-cell tables, without rounded
corners or shadows. See [Flex and grid layout](flow-documents.md#flex-and-grid-layout).
