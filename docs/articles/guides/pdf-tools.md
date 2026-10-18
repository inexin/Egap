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

```csharp
using Egap.Pdf;

using PdfDocument contract = PdfDocument.Open("contract.pdf");
contract.Stamp(PdfStamp.Text("Page {page} of {pages}") with { Anchor = StampAnchor.BottomCenter, Offset = new(0, 24) });
contract.Stamp(PdfStamp.Bates(new PdfBatesSequence("ACME-", digits: 6)) with { Anchor = StampAnchor.BottomRight });
using PdfDocument handout = PdfImpose.NUp(contract, new NUpOptions { Columns = 2, Rows = 2 });
handout.Save("handout.pdf");
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
