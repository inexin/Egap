# Editing PDF files

`PdfDocument` opens a file, lets you change its pages, navigation, annotations and forms, and saves it.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf", new PdfReadOptions { Password = "secret" });
foreach (PdfWarning warning in pdf.Warnings) Console.WriteLine(warning);

pdf.Pages.Move(0, pdf.Pages.Count - 1);                  // pages: insert, remove, move, labels
pdf.Metadata.Title = "Quarterly report";                 // Info and XMP kept in step
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Pages[0].Annotations.Add(new PdfHighlightAnnotation(PdfTextMarkupAnnotation.Quad(new PdfRectangle(72, 700, 300, 714))));

if (pdf.Form.TryGetField("customer.name", out PdfField field) && field is PdfTextField name)
{
    name.Value = "Ada Lovelace";                         // the widget is redrawn
}

pdf.Form.Flatten();                                      // fields drawn into the pages and removed
pdf.Save("out.pdf");                                     // incremental: the original bytes plus the changes
```

## Damaged and encrypted files

Egap repairs what it can while reading: broken cross-reference tables, wrong lengths, bad page trees. Each repair adds a
`PdfWarning`. With `PdfReadOptions.Strict` set, the first problem throws a `PdfException` instead. Encrypted files open with
the user or owner password; RC4 and AES up to AES-256 are read and written.

## Saving

By default a save appends only the changes to the original bytes (an incremental update), so signatures stay valid. A full
rewrite writes a fresh, compact file:

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
using PdfDocument other = PdfDocument.Open("other.pdf");
using PdfDocument merged = PdfDocument.Merge(pdf, other);  // fields renamed on a clash, outlines appended
merged.Save("merged.pdf", new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
```
