# Cookbook

Short recipes for everyday jobs. Each one is complete: open, change, save.

## Merge files and keep some pages

```csharp
using Egap.Pdf;

using PdfDocument cover = PdfDocument.Open("cover.pdf");
using PdfDocument report = PdfDocument.Open("report.pdf");
using PdfDocument merged = PdfDocument.Merge(cover, report);
using PdfDocument chosen = merged.ExtractPages(PdfPageRange.Parse("1-3,5,z"));
chosen.Save("merged.pdf");
```

## Add page numbers

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("report.pdf");
pdf.Stamp(PdfStamp.Text("Page {page} of {total-pages}") with
{
    Anchor = StampAnchor.BottomCenter,
    Offset = new(0, Length.Mm(10)),
    PageRange = PdfPageRange.Parse("2-z"),              // no number on the cover
});
pdf.Save("report-numbered.pdf");
```

## Sign a document

```csharp
using System.Security.Cryptography.X509Certificates;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("contract.pdf");
using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("me.pfx", "password");
await using FileStream signed = File.Create("contract-signed.pdf");
await pdf.SignAsync("Signature1", new PdfSignOptions { Signer = PdfSigner.FromCertificate(certificate) }, signed);
```

A key on a smart card or an HSM signs the same way through an `RSA` that calls the device; the
[signatures guide](guides/signatures.md#smart-cards-and-hsms-pkcs11) shows a PKCS#11 wrapper.

## Redact a phrase

```csharp
using System.Text.RegularExpressions;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("statement.pdf");
pdf.RedactText("Ola Nordmann", new PdfFindOptions { MatchCase = false });
pdf.RedactText(new Regex(@"\b\d{11}\b"));                 // Norwegian national identity numbers
pdf.Save("statement-redacted.pdf");                      // written whole, so nothing redacted survives
```

## Turn an HTML invoice into a Factur-X PDF/A-3

```csharp
using Egap.Pdf;
using Egap.Pdf.Conversion;

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
