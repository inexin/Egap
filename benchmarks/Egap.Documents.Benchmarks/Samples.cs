using System.Globalization;
using System.IO.Compression;
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

    /// <summary>The same mix as OpenDocument Text: a package with the content, styles and manifest, as LibreOffice writes it.</summary>
    public static byte[] Odt(int sections)
    {
        const string ns = "xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0' "
            + "xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' "
            + "xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0' xmlns:xlink='http://www.w3.org/1999/xlink'";
        StringBuilder content = new($"<?xml version='1.0' encoding='UTF-8'?>\n<office:document-content {ns} office:version='1.3'><office:automatic-styles>");
        content.Append("<style:style style:name='T1' style:family='text'><style:text-properties fo:font-style='italic'/></style:style>");
        content.Append("<style:style style:name='T2' style:family='text'><style:text-properties fo:font-weight='bold'/></style:style>");
        content.Append("<style:style style:name='T3' style:family='text'><style:text-properties style:font-name='Courier New'/></style:style>");
        content.Append("<style:style style:name='P1' style:family='paragraph' style:parent-style-name='Standard'><style:paragraph-properties fo:margin-left='0.17in'/><style:text-properties fo:font-style='italic'/></style:style>");
        content.Append("<style:style style:name='Table1' style:family='table'><style:table-properties style:width='2.78in'/></style:style>");
        content.Append("<style:style style:name='Table1.A' style:family='table-column'><style:table-column-properties style:column-width='1.39in'/></style:style>");
        content.Append("<style:style style:name='Table1.A1' style:family='table-cell'><style:table-cell-properties fo:padding='0.07in' fo:border='0.5pt solid #000000'/></style:style>");
        content.Append("<text:list-style style:name='L1'><text:list-level-style-bullet text:level='1' text:bullet-char='\u2022'><style:list-level-properties text:list-level-position-and-space-mode='label-alignment'>");
        content.Append("<style:list-level-label-alignment text:label-followed-by='listtab' fo:text-indent='-0.25in' fo:margin-left='0.5in'/></style:list-level-properties></text:list-level-style-bullet>");
        content.Append("<text:list-level-style-bullet text:level='2' text:bullet-char='o'><style:list-level-properties text:list-level-position-and-space-mode='label-alignment'>");
        content.Append("<style:list-level-label-alignment text:label-followed-by='listtab' fo:text-indent='-0.25in' fo:margin-left='1in'/></style:list-level-properties></text:list-level-style-bullet></text:list-style>");
        content.Append("</office:automatic-styles><office:body><office:text>\n");
        for (int i = 0; i < sections; i++)
        {
            content.Append(CultureInfo.InvariantCulture, $"<text:h text:style-name='Heading_20_2' text:outline-level='2'>Section {i}</text:h>\n");
            content.Append("<text:p text:style-name='Standard'>A paragraph with <text:span text:style-name='T1'>emphasis</text:span>, <text:span text:style-name='T2'>strong text</text:span>, ");
            content.Append("<text:span text:style-name='T3'>code</text:span>, a <text:a xlink:type='simple' xlink:href='https://example.com/page'>link</text:a> &amp; an entity \u2014 that runs long enough to wrap.</text:p>\n");
            content.Append("<text:list text:style-name='L1'><text:list-item><text:p>First item</text:p></text:list-item><text:list-item><text:p>Second item with <text:span text:style-name='T2'>bold</text:span></text:p>");
            content.Append("<text:list><text:list-item><text:p>Nested item</text:p></text:list-item></text:list></text:list-item></text:list>\n");
            if (i % 3 == 0)
            {
                content.Append("<table:table table:name='Table1' table:style-name='Table1'><table:table-column table:style-name='Table1.A' table:number-columns-repeated='2'/>");
                foreach ((string name, string value) in new[] { ("<text:span text:style-name='T2'>Name</text:span>", "<text:span text:style-name='T2'>Value</text:span>"), ("Alpha", "1"), ("Beta", "22") })
                {
                    content.Append(CultureInfo.InvariantCulture, $"<table:table-row><table:table-cell table:style-name='Table1.A1'><text:p>{name}</text:p></table:table-cell><table:table-cell table:style-name='Table1.A1'><text:p>{value}</text:p></table:table-cell></table:table-row>");
                }

                content.Append("</table:table>\n");
            }

            if (i % 4 == 1)
            {
                content.Append("<text:p text:style-name='Preformatted_20_Text'>var doc = OdtFormat.Load(path);<text:line-break/>Console.WriteLine(doc);</text:p>\n");
            }

            if (i % 5 == 2)
            {
                content.Append("<text:p text:style-name='P1'>A quoted <text:span text:style-name='T3'>line</text:span>.</text:p>\n");
            }
        }

        content.Append("</office:text></office:body></office:document-content>");
        string styles = $"<?xml version='1.0' encoding='UTF-8'?>\n<office:document-styles {ns} office:version='1.3'><office:styles>"
            + "<style:default-style style:family='paragraph'><style:text-properties fo:font-size='12pt'/></style:default-style>"
            + "<style:style style:name='Standard' style:family='paragraph' style:class='text'/>"
            + "<style:style style:name='Heading_20_2' style:display-name='Heading 2' style:family='paragraph' style:parent-style-name='Standard' style:next-style-name='Standard' style:default-outline-level='2'>"
            + "<style:paragraph-properties fo:margin-top='18pt' fo:margin-bottom='3pt' fo:keep-with-next='always'/><style:text-properties fo:font-size='14pt' fo:font-weight='bold' fo:color='#333366'/></style:style>"
            + "<style:style style:name='Preformatted_20_Text' style:display-name='Preformatted Text' style:family='paragraph' style:parent-style-name='Standard'><style:text-properties style:font-name='Courier New' fo:font-size='10pt'/></style:style>"
            + "</office:styles><office:automatic-styles><style:page-layout style:name='pm1'><style:page-layout-properties fo:page-width='8.5in' fo:page-height='11in' fo:margin='1in'/></style:page-layout></office:automatic-styles>"
            + "<office:master-styles><style:master-page style:name='Standard' style:page-layout-name='pm1'/></office:master-styles></office:document-styles>";
        const string manifest = "<?xml version='1.0' encoding='UTF-8'?>\n<manifest:manifest xmlns:manifest='urn:oasis:names:tc:opendocument:xmlns:manifest:1.0' manifest:version='1.3'>"
            + "<manifest:file-entry manifest:full-path='/' manifest:media-type='application/vnd.oasis.opendocument.text'/><manifest:file-entry manifest:full-path='content.xml' manifest:media-type='text/xml'/>"
            + "<manifest:file-entry manifest:full-path='styles.xml' manifest:media-type='text/xml'/></manifest:manifest>";

        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
            Add(zip, "content.xml", content.ToString(), CompressionLevel.Optimal);
            Add(zip, "styles.xml", styles, CompressionLevel.Optimal);
            Add(zip, "META-INF/manifest.xml", manifest, CompressionLevel.Optimal);
        }

        return output.ToArray();

        static void Add(ZipArchive zip, string name, string text, CompressionLevel level)
        {
            using Stream stream = zip.CreateEntry(name, level).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }
}
