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

## New form fields

`PdfForm` adds text fields, check boxes, radio groups, combo and list boxes, push buttons and empty signature fields, each
with a widget and an appearance drawn for it. A name with dots, such as `customer.name`, puts the field under a parent.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("blank.pdf");
pdf.Form.AddTextField("customer.name", 0, new PdfRectangle(72, 700, 300, 720), new PdfTextFieldOptions { MaxLength = 60, Required = true });
pdf.Form.AddCheckBox("terms", 0, new PdfRectangle(72, 660, 86, 674));
pdf.Form.AddRadioGroup("size", [(0, new PdfRectangle(72, 620, 86, 634), "S"), (0, new PdfRectangle(100, 620, 114, 634), "M")], selected: "S");
pdf.Form.AddComboBox("country", 0, new PdfRectangle(72, 580, 220, 596), ["Norway", "Sweden"]);
pdf.Form.AddSignatureField("approval", 0, new PdfRectangle(72, 480, 272, 540));
pdf.Save("form.pdf");
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
