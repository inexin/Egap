# Packages

| Package | What it does | Depends on |
| --- | --- | --- |
| Egap.Pdf | Reads, edits, renders, signs, redacts and saves PDF; PDF/A, tagged PDF, optimize, imposition, stamps, barcodes, compare | Egami.Drawing, Egami.Barcodes, Egami.Fonts.Substitutes |
| Egap.Documents | One document model for DOCX, HTML and Markdown, with page layout onto any Egami canvas | Egami.Drawing, Egami.Barcodes, Egami.Css, Egami.Svg, Egami.Metafiles, Egami.Fonts.Substitutes |
| Egap.Pdf.Conversion | Documents to tagged PDF, and PDF pages analysed and converted back into documents | Egap.Pdf, Egap.Documents |

Egap.Pdf and Egap.Documents do not depend on each other. Egap.Pdf.Conversion is the bridge between them, so you only take the
document model if you need it, and only take PDF if you need that.

Egami.Drawing brings in Egami and Egami.Fonts. Every package is fully managed, MIT-licensed, and targets .NET 10. The substitute fonts
for missing fonts come from Egami.Fonts.Substitutes, under the SIL Open Font License 1.1.
