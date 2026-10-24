using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

// HTML read through the registry and the members, with a style sheet and an encoding to sniff, written back, and laid out as
// PDF with an HTML header, then as EPUB and MHTML, all ahead-of-time compiled.
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

// EPUB and MHTML written and read back through the registry: the book's chapters, its table of contents and the archive.
byte[] book = document.ToEpub();
Document read = Document.Load(new MemoryStream(book));
string mhtml = read.ToMhtml();
Document archive = Document.Load(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(mhtml)));
if (Document.Identify(new MemoryStream(book)) != DocumentFormatKind.Epub || !read.GetText().Contains("Styled text", StringComparison.Ordinal)
    || !archive.GetText().Contains("Styled text", StringComparison.Ordinal))
{
    Console.Error.WriteLine("EPUB and MHTML smoke test failed.");
    return 1;
}

Console.WriteLine($"HTML: {html.Length} characters written, {pdf.Pages.Count} PDF page; EPUB {book.Length} bytes; MHTML {mhtml.Length} characters.");
return 0;
