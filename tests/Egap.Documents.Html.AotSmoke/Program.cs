using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

// HTML read through the registry and the members, with a style sheet and an encoding to sniff, written back, and laid out as
// PDF with an HTML header, all ahead-of-time compiled.
HtmlFormat.Register();
using MemoryStream input = new("<!DOCTYPE html><meta charset=utf-8><style>p.note { font-style: italic }</style><h1>Smoke</h1><p class=note>Styled text</p><table><tr><td>1<td>2</table>"u8.ToArray());
Document document = Document.Load(input);
string html = document.ToHtml();
using PdfDocument pdf = PdfConvert.FromHtml(html, new PdfConvertOptions { Header = PageBand.Html("<b>{page}</b>") });
if (!document.GetText().Contains("Styled text", StringComparison.Ordinal) || pdf.Pages.Count != 1)
{
    Console.Error.WriteLine("HTML smoke test failed.");
    return 1;
}

Console.WriteLine($"HTML: {html.Length} characters written, {pdf.Pages.Count} PDF page.");
return 0;
