using System.Globalization;
using System.Text;

namespace Egap.Documents.Benchmarks;

/// <summary>Builds the benchmark inputs: the same mix of headings, paragraphs, lists, tables and code, repeated.</summary>
internal static class Samples
{
    /// <summary>The sizes the loading benchmarks run at, as a number of repeated sections (about 1 KB, 45 KB and 900 KB).</summary>
    public static IEnumerable<string> Sizes => ["Small", "Medium", "Large"];

    public static int Sections(string size) => size switch
    {
        "Small" => 4,
        "Medium" => 150,
        "Large" => 3000,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    public static string Markdown(int sections)
    {
        StringBuilder text = new("---\ntitle: Benchmark\n---\n\n");
        for (int i = 0; i < sections; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"## Section {i}\n\n");
            text.Append("A paragraph with *emphasis*, **strong text**, `code`, a [link](https://example.com/page) and ~~struck~~ words ");
            text.Append("that runs long enough to wrap across a few lines in the source file.\n\n");
            text.Append("- First item\n- Second item with **bold**\n  - Nested item\n- [ ] A task\n\n");
            if (i % 3 == 0)
            {
                text.Append("| Name | Value |\n| --- | ---: |\n| Alpha | 1 |\n| Beta | 22 |\n\n");
            }

            if (i % 4 == 1)
            {
                text.Append("```csharp\nvar doc = MarkdownFormat.Parse(text);\nConsole.WriteLine(doc);\n```\n\n");
            }

            if (i % 5 == 2)
            {
                text.Append("> A quoted line with a footnote.[^n]\n\n[^n]: The footnote text.\n\n");
            }
        }

        return text.ToString();
    }

    public static string Html(int sections)
    {
        StringBuilder text = new("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Benchmark</title>\n");
        text.Append("<style>body { font-family: serif; } h2 { color: #336; margin-top: 1.5em; } .note { font-style: italic; } ");
        text.Append("td, th { border: 1px solid #999; padding: 2pt; } p > a { color: blue; }</style></head>\n<body>\n");
        for (int i = 0; i < sections; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"<h2 id=\"s{i}\">Section {i}</h2>\n");
            text.Append("<p>A paragraph with <em>emphasis</em>, <strong>strong text</strong>, <code>code</code>, a ");
            text.Append("<a href=\"https://example.com/page\">link</a> &amp; an entity &mdash; that runs long enough to wrap.</p>\n");
            text.Append("<ul><li>First item</li><li>Second item with <b>bold</b><ul><li>Nested item</li></ul></li></ul>\n");
            if (i % 3 == 0)
            {
                text.Append("<table><tr><th>Name</th><th>Value</th></tr><tr><td>Alpha</td><td>1</td></tr><tr><td>Beta</td><td>22</td></tr></table>\n");
            }

            if (i % 4 == 1)
            {
                text.Append("<pre><code>var doc = HtmlFormat.Parse(text);\nConsole.WriteLine(doc);</code></pre>\n");
            }

            if (i % 5 == 2)
            {
                text.Append("<blockquote class=\"note\"><p style=\"margin-left: 12pt\">A quoted <span class=\"note\">line</span>.</p></blockquote>\n");
            }
        }

        text.Append("</body></html>\n");
        return text.ToString();
    }
}
