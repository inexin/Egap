# PDF tools

Everyday tools for whole documents: compress, combine pages onto sheets, stamp, compare and clean up scans.

## Optimize

`Optimize` makes a file smaller. The default, `Lossless`, keeps every pixel; the `Print`, `Ebook` and `Screen` presets
downsample images and use JPEG. The report says what it saved.

```csharp
using Egap.Pdf;

using PdfDocument scan = PdfDocument.Open("scan.pdf");
IReadOnlyList<int> blank = scan.RemoveBlankPages();               // the page numbers removed
scan.DeskewScannedPages();
PdfOptimizeReport smaller = scan.Optimize(PdfOptimizeOptions.Ebook with { JpegQuality = 70 });
Console.WriteLine($"{smaller.BytesBefore} -> {smaller.BytesAfter} bytes, {smaller.Images.Count} images changed");
scan.Save("scan-small.pdf");                                      // every save after Optimize is a compact rewrite
```

## Stamps and imposition

Stamps are page numbers, headers and footers, Bates numbers, watermarks, text, images or whole pages. They are written as
pagination artifacts, so a viewer or a later pass can tell them from the content. `PdfImpose` places pages onto sheets:
N-up, booklets, posters, grids and fit to a page size.

Text stamps fill placeholders per page: `{page}`, `{total-pages}` (or `{pages}`), `{label}`, `{date}`, `{time}`, `{title}`
and `{file}`, with an optional format such as `{page:000}` or `{date:yyyy-MM-dd}`. `PdfPlaceholders.Fill` fills the same
names in your own text.

```csharp
using Egap.Pdf;

using PdfDocument contract = PdfDocument.Open("contract.pdf");
contract.Stamp(PdfStamp.Text("Page {page} of {total-pages}") with { Anchor = StampAnchor.BottomCenter, Offset = new(0, 24) });
contract.Stamp(PdfStamp.Bates(new PdfBatesSequence("ACME-", digits: 6)) with { Anchor = StampAnchor.BottomRight });
using PdfDocument handout = PdfImpose.NUp(contract, new NUpOptions { Columns = 2, Rows = 2 });
handout.Save("handout.pdf");
```

`PdfStamp.Barcode` stamps any symbol Egami.Barcodes makes (QR, Data Matrix, PDF417, Aztec, Code 128, Code 39, ITF, EAN and
UPC) as vector paths, so it scans at any zoom and in print. The draw options choose the quiet zone, bar width reduction and
the font of the digits under a linear symbol.

```csharp
using Egami.Barcodes;
using Egap.Pdf;

using PdfDocument invoice = PdfDocument.Open("invoice.pdf");
invoice.Stamp(PdfStamp.Barcode(Barcode.Qr("https://pay.example.com/2026-118"), 72, 72) with { Pages = [1], Anchor = StampAnchor.TopRight });
invoice.Stamp(PdfStamp.Barcode(Barcode.Code128("ORDER-118"), 160, 40) with { Anchor = StampAnchor.BottomLeft });
invoice.Save("invoice-coded.pdf");
```

## Page ranges and units

`PdfPageRange.Parse` reads pages written as text: numbers from 1, `z` for the last page, `r2` for the second from the end,
ranges such as `5-10` (or `10-5`, backwards), `:odd` or `:even` after a part, and `x` before a part to leave its pages out.
`Length` holds a size made from points, millimetres, centimetres or inches. It turns into points by itself, so it goes
wherever a size in points is asked for.

```csharp
using Egap.Pdf;

using PdfDocument report = PdfDocument.Open("report.pdf");
using PdfDocument summary = report.ExtractPages(PdfPageRange.Parse("1-3,z"));
report.Stamp(PdfStamp.Text("Draft") with { PageRange = PdfPageRange.Parse("2-z"), Offset = new(0, Length.Mm(10)) });
using PdfDocument handout = PdfImpose.NUp(report, new NUpOptions { Margin = Length.Mm(10), Gutter = Length.Mm(4) });
IReadOnlyList<int> evenPages = PdfPageRange.Parse("1-z:even").Resolve(report.Pages.Count);   // 0-based indices
```

## Compare

`PdfCompare` finds what changed between two versions, word by word with pages aligned, and also compares the rendered pages
pixel by pixel. It can write a copy of the new document with each change marked.

```csharp
using Egap.Pdf;

using PdfDocument before = PdfDocument.Open("contract-v1.pdf"), after = PdfDocument.Open("contract-v2.pdf");
using PdfComparison changes = PdfCompare.Run(before, after);
foreach (PdfTextChange change in changes.TextChanges) Console.WriteLine($"{change.Kind}: {change.OldText} -> {change.NewText}");
changes.WriteMarkup("contract-changes.pdf");                      // the new document with each change marked
```

## Clean-up

`ConvertToGray` turns every colour to gray, `RemoveBlankPages` drops empty pages, `DeskewScannedPages` straightens scans,
and `ConvertTextToOutlines` turns text into paths so a file shows the same without its fonts.
