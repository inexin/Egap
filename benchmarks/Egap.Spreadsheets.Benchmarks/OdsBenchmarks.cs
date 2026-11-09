using System.Globalization;
using System.IO.Compression;
using System.Text;

using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Loading an ODS spreadsheet of 10,000 rows by 10 columns (100,000 cells), the same data as <see cref="XlsxBenchmarks"/>:
/// numbers, text from 1,000 distinct strings, an OpenFormula formula per row and two cell styles.
/// </summary>
[MemoryDiagnoser]
public class OdsBenchmarks
{
    private const int Rows = 10_000;
    private const int Columns = 10;

    private byte[] file = [];

    [GlobalSetup]
    public void Setup()
    {
        StringBuilder content = new();
        content.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
              xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
              xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:number="urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0" office:version="1.3">
            <office:automatic-styles>
              <number:number-style style:name="N4"><number:number number:decimal-places="2" number:min-decimal-places="2" number:min-integer-digits="1" number:grouping="true"/></number:number-style>
              <style:style style:name="ce1" style:family="table-cell" style:data-style-name="N4"/>
              <style:style style:name="ce2" style:family="table-cell"><style:text-properties fo:font-weight="bold"/></style:style>
            </office:automatic-styles>
            <office:body><office:spreadsheet><table:table table:name="Sheet1"><table:table-column table:number-columns-repeated="10"/>
            """);
        for (int row = 0; row < Rows; row++)
        {
            content.Append("<table:table-row>");
            for (int column = 0; column < Columns - 2; column++)
            {
                string style = row == 0 ? " table:style-name=\"ce2\"" : column == 0 ? " table:style-name=\"ce1\"" : "";
                if (column % 2 == 0)
                {
                    string value = (row * 1.5 + column).ToString(CultureInfo.InvariantCulture);
                    content.Append(CultureInfo.InvariantCulture, $"<table:table-cell{style} office:value-type=\"float\" office:value=\"{value}\"><text:p>{value}</text:p></table:table-cell>");
                }
                else
                {
                    content.Append(CultureInfo.InvariantCulture, $"<table:table-cell{style} office:value-type=\"string\"><text:p>word{(row + column) % 1000}</text:p></table:table-cell>");
                }
            }

            string doubled = ((row * 1.5) * 2).ToString(CultureInfo.InvariantCulture);
            content.Append(CultureInfo.InvariantCulture, $"<table:table-cell table:formula=\"of:=[.A{row + 1}]*2\" office:value-type=\"float\" office:value=\"{doubled}\"><text:p>{doubled}</text:p></table:table-cell>");
            content.Append(CultureInfo.InvariantCulture, $"<table:table-cell office:value-type=\"float\" office:value=\"{row}\"><text:p>{row}</text:p></table:table-cell>");
            content.Append("</table:table-row>");
        }

        content.Append("</table:table></office:spreadsheet></office:body></office:document-content>");
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", "application/vnd.oasis.opendocument.spreadsheet", CompressionLevel.NoCompression);
            Write(zip, "content.xml", content.ToString(), CompressionLevel.Optimal);
            Write(zip, "META-INF/manifest.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """, CompressionLevel.Optimal);
        }

        file = stream.ToArray();
    }

    [Benchmark(Description = "Load ODS, 100k cells")]
    public Workbook Load() => Workbook.Load(new MemoryStream(file, writable: false));

    private static void Write(ZipArchive zip, string name, string text, CompressionLevel level)
    {
        using Stream entry = zip.CreateEntry(name, level).Open();
        entry.Write(Encoding.UTF8.GetBytes(text));
    }
}
