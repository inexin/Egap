# Redaction and PDF/A

## Redaction

Redaction removes the covered text, paths and image pixels from the file for real, then rewrites the whole file so nothing
is left in older revisions.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("report.pdf");
pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(72, 700, 300, 714)));
pdf.ApplyRedactions();                                            // covered text, paths and pixels removed for real
pdf.Save("redacted.pdf");
```

`RedactText` finds a phrase or a regular expression and redacts every match in one call; `MarkRedactions` only adds the
marks, so they can be checked in a viewer before `ApplyRedactions`. `ReplaceText` swaps one text for another in the same
font, size and colour. Where that font, often an embedded subset, has no glyph for a character of the new text, the match
is left as it was with a warning, unless you give a fallback font.

```csharp
using Egap.Pdf;
using Egami.Fonts;

using PdfDocument pdf = PdfDocument.Open("contract.pdf");
pdf.RedactText("Confidential", new PdfFindOptions { MatchCase = false });
PdfReplaceResult replaced = pdf.ReplaceText("2025", "2026", options: new PdfReplaceOptions { FallbackFont = Typeface.FromFile("NotoSans.ttf") });
foreach (PdfWarning warning in replaced.Warnings)
{
    Console.WriteLine(warning.Message);                           // matches left as they were
}

pdf.Save("contract-2026.pdf");
```

`Sanitize` removes hidden data in one call: metadata, scripts, attachments, hidden layers, comments, form data, thumbnails
and old revisions.

## PDF/A

The converter keeps the file's objects, fixes what it can, and reports every fix. If a rule cannot be met it changes
nothing and says why.

```csharp
using Egap.Pdf;

using PdfDocument archive = PdfDocument.Open("report.pdf");
PdfAReport conversion = PdfAConverter.Convert(archive, PdfAConformance.PdfA2u);
if (conversion.Succeeded)
{
    archive.Save("archive.pdf", new PdfSaveOptions { Linearize = true });   // linearized for fast web view
}
```

PDF/A-1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 and 4f are written; levels 2a and 3a need a tagged file. Egap checks only the rules its writer and
converter enforce, so use a validator such as veraPDF when conformance must be proven.

## E-invoices

`PdfInvoice.Embed` turns a PDF into a Factur-X or ZUGFeRD e-invoice: it attaches the invoice's XML under its standard name
with the relationship the standard asks for, converts the document to PDF/A-3 and writes the Factur-X schema into the XMP.
The profile is read from the XML unless you give it. `PdfInvoice.Read` gives the XML back, with its standard and profile,
and also reads ZUGFeRD 1.0 and 2.0 files. Egap does not write the XML itself; your invoicing system does.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("invoice.pdf");
PdfAReport report = PdfInvoice.Embed(pdf, File.ReadAllBytes("factur-x.xml"));
pdf.Save("invoice-facturx.pdf");

using PdfDocument received = PdfDocument.Open("received.pdf");
if (PdfInvoice.Read(received) is PdfInvoice invoice)
{
    Console.WriteLine($"{invoice.FileName}: {invoice.Profile}");
}

received.Attachments.Add("data.csv", File.ReadAllBytes("data.csv"), PdfAttachmentRelationship.Data, "text/csv");
```

## Recipes

Short answers to common tasks in this area. Each one is complete: open, change, save.

### Redact a phrase

```csharp
using System.Text.RegularExpressions;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("statement.pdf");
pdf.RedactText("Ola Nordmann", new PdfFindOptions { MatchCase = false });
pdf.RedactText(new Regex(@"\b\d{11}\b"));                 // Norwegian national identity numbers
pdf.Save("statement-redacted.pdf");                      // written whole, so nothing redacted survives
```

### Turn an HTML invoice into a Factur-X PDF/A-3

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

HtmlFormat.Register();                                           // once at startup: PdfConvert.FromFile reads .html
using PdfDocument invoice = PdfConvert.FromFile("invoice.html", new PdfConvertOptions
{
    PageSize = PdfPageSize.A4,
    Margins = PageMargins.All(Length.Mm(15)),
});
PdfAReport report = PdfInvoice.Embed(invoice, File.ReadAllBytes("factur-x.xml"));   // PDF/A-3b with the XML attached
if (report.Succeeded)
{
    invoice.Save("invoice-2026-118.pdf");
}
```
