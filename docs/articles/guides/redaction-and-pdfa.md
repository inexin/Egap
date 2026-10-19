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
