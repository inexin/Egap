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

    /// <summary>
    /// The same mix as RTF in Word's shape: a font table, a colour table, a style sheet with headings, a list table, then
    /// styled paragraphs, list items with their <c>\listtext</c>, tables with row definitions and hyperlink fields.
    /// </summary>
    public static string Rtf(int sections)
    {
        StringBuilder text = new(@"{\rtf1\ansi\ansicpg1252\deff0\deflang1033");
        text.Append(@"{\fonttbl{\f0\froman\fcharset0 Times New Roman;}{\f1\fswiss\fcharset0 Arial;}{\f2\fmodern\fcharset0 Courier New;}{\f3\fnil\fcharset2 Symbol;}}");
        text.Append(@"{\colortbl;\red0\green0\blue255;\red51\green51\blue102;}");
        text.Append(@"{\stylesheet{\ql\widctlpar\f0\fs24 \snext0 Normal;}{\s2\ql\sb360\sa60\keepn\b\f1\fs28\cf2 \sbasedon0 \snext0 heading 2;}");
        text.Append(@"{\*\cs10 \additive \ssemihidden Default Paragraph Font;}{\s20\li240\i \sbasedon0 \snext20 Quote;}}");
        text.Append(@"{\*\listtable{\list\listtemplateid1\listhybrid{\listlevel\levelnfc23\levelnfcn23\leveljc0\levelfollow0\levelstartat1{\leveltext\'01\'b7;}{\levelnumbers;}\f3\fi-360\li720}");
        text.Append(@"{\listlevel\levelnfc23\levelnfcn23\leveljc0\levelfollow0\levelstartat1{\leveltext\'01o;}{\levelnumbers;}\f2\fi-360\li1440}\listid1}}");
        text.Append(@"{\*\listoverridetable{\listoverride\listid1\listoverridecount0\ls1}}");
        text.Append(@"{\info{\title Benchmark}}\viewkind4\uc1" + "\r\n");
        for (int i = 0; i < sections; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $@"\pard\plain\s2\ql\sb360\sa60\keepn\b\f1\fs28\cf2 Section {i}\par").Append("\r\n");
            text.Append(@"\pard\plain\ql\widctlpar\f0\fs24 A paragraph with {\i emphasis}, {\b strong text}, {\f2 code}, a ");
            text.Append(@"{\field{\*\fldinst HYPERLINK ""https://example.com/page""}{\fldrslt {\ul\cf1 link}}} \'26 an entity \emdash  that runs long enough to wrap.\par" + "\r\n");
            text.Append(@"{\listtext\pard\plain\f3 \'b7\tab}\pard\plain\ls1\ilvl0\fi-360\li720\f0\fs24 First item\par" + "\r\n");
            text.Append(@"{\listtext\pard\plain\f3 \'b7\tab}\pard\plain\ls1\ilvl0\fi-360\li720\f0\fs24 Second item with {\b bold}\par" + "\r\n");
            text.Append(@"{\listtext\pard\plain\f2 o\tab}\pard\plain\ls1\ilvl1\fi-360\li1440\f0\fs24 Nested item\par" + "\r\n");
            if (i % 3 == 0)
            {
                const string row = @"\trowd\trgaph108\trleft-108\trbrdrt\brdrs\brdrw10 \trbrdrl\brdrs\brdrw10 \trbrdrb\brdrs\brdrw10 \trbrdrr\brdrs\brdrw10 \clbrdrt\brdrs\brdrw10 \clbrdrl\brdrs\brdrw10 \clbrdrb\brdrs\brdrw10 \clbrdrr\brdrs\brdrw10 \cellx2000\clbrdrt\brdrs\brdrw10 \clbrdrl\brdrs\brdrw10 \clbrdrb\brdrs\brdrw10 \clbrdrr\brdrs\brdrw10 \cellx4000";
                text.Append(row).Append(@"\pard\plain\intbl\f0\fs24 {\b Name}\cell {\b Value}\cell\row" + "\r\n");
                text.Append(row).Append(@"\pard\plain\intbl\f0\fs24 Alpha\cell 1\cell\row" + "\r\n");
                text.Append(row).Append(@"\pard\plain\intbl\f0\fs24 Beta\cell 22\cell\row" + "\r\n");
            }

            if (i % 4 == 1)
            {
                text.Append(@"\pard\plain\ql\f2\fs20 var doc = RtfFormat.Parse(text);\line Console.WriteLine(doc);\par" + "\r\n");
            }

            if (i % 5 == 2)
            {
                text.Append(@"\pard\plain\s20\li240\i\f0\fs24 A quoted {\i0 line}.\par" + "\r\n");
            }
        }

        text.Append('}');
        return text.ToString();
    }
}
