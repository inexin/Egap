using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

// Markdown read through the registry and the members, written back, and laid out as PDF, all ahead-of-time compiled.
MarkdownFormat.Register();
using MemoryStream input = new("# Smoke\n\nSome *emphasis*, a [link](https://example.com) and a table:\n\n| a | b |\n| - | - |\n| 1 | 2 |\n"u8.ToArray());
Document document = Document.Load(input);
string markdown = document.ToMarkdown();
using PdfDocument pdf = PdfConvert.FromMarkdown(markdown);
if (!document.GetText().Contains("emphasis", StringComparison.Ordinal) || pdf.Pages.Count != 1)
{
    Console.Error.WriteLine("Markdown smoke test failed.");
    return 1;
}

Console.WriteLine($"Markdown: {markdown.Length} characters written, {pdf.Pages.Count} PDF page.");
return 0;
