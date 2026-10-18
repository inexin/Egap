# Packages

| Package | What it does | Depends on |
| --- | --- | --- |
| Egap.Pdf | Reads, edits, renders, signs, redacts and saves PDF; PDF/A, tagged PDF, optimize, imposition, stamps, compare | Egami.Drawing, Egap.Fonts |
| Egap.Documents | One document model for DOCX, HTML and Markdown, with page layout onto any Egami canvas | Egami.Drawing, Egami.Css, Egami.Svg, Egami.Metafiles, Egap.Fonts |
| Egap.Pdf.Conversion | Documents to tagged PDF, and PDF pages analysed and converted back into documents | Egap.Pdf, Egap.Documents |
| Egap.Fonts | Open, metric-compatible substitutes for Helvetica, Arial, Times, Courier, Calibri and Cambria | Egami.Fonts |

Egap.Pdf and Egap.Documents do not depend on each other. Egap.Pdf.Conversion is the bridge between them, so you only take the
document model if you need it, and only take PDF if you need that.

Egami.Drawing brings in Egami and Egami.Fonts. Every package is fully managed, MIT-licensed, and targets .NET 10. Fonts in Egap.Fonts are under the SIL Open Font License
1.1, and their licences ship in the package.
