using System.Text;

using Egap.Documents;

namespace Egap.Spreadsheets.Tests;

/// <summary>SheetReader and SheetWriter: rows streamed through XLSX and CSV without a workbook.</summary>
public sealed class StreamingTests
{
    [Fact]
    public void RowsWrittenComeBackThroughTheWorkbookAndTheReader()
    {
        MemoryStream file = new();
        using (SheetWriter writer = SheetWriter.Create(file))
        {
            writer.StartSheet("Orders");
            writer.SetColumnWidth(0, 20);
            writer.SetColumnFormat(1, "yyyy-mm-dd");
            writer.FreezeTopRows(1);
            writer.WriteRow(CellStyle.Default with { Font = CellFont.Default with { Bold = true } }, "Order", "Date", "Paid", "Note");
            writer.WriteRow("A-1", 45000, true, "  spaced\nline _x0041_");
            writer.SkipRows(2);
            writer.WriteRow(CellValue.Empty, 45001.5, CellValue.Empty, CellError.NotAvailable);
            writer.StartSheet("Second");
            writer.WriteRow(1, 2, 3);
        }

        file.Position = 0;
        Workbook book = Workbook.Load(file);
        Worksheet orders = book.Worksheets["Orders"];
        Assert.Equal("Order", orders["A1"].Value);
        Assert.True(orders["A1"].Style.Font.Bold);
        Assert.Equal("2023-03-15", orders["B2"].Text);
        Assert.Equal(true, orders["C2"].Value);
        Assert.Equal("  spaced\nline _x0041_", orders["D2"].Value);
        Assert.Equal(CellError.NotAvailable, orders["D5"].Value);
        Assert.Equal(20, orders.Columns[0].Width);
        Assert.Equal(1, orders.View.FrozenRows);
        Assert.Equal(3.0, book.Worksheets["Second"]["C1"].Value);

        file.Position = 0;
        using SheetReader reader = SheetReader.Open(file);
        Assert.Equal(["Orders", "Second"], reader.SheetNames);
        Assert.True(reader.Read());
        Assert.Equal((0, 4), (reader.Row, reader.ColumnCount));
        Assert.Equal("Date", reader[1]);
        Assert.True(reader.Read());
        Assert.Equal(45000.0, reader[1]);
        Assert.Equal("yyyy-mm-dd", reader.GetNumberFormat(1));
        Assert.Equal("2023-03-15", reader.GetText(1));
        Assert.Equal(CellValue.Empty, reader[9]);
        Assert.True(reader.Read());
        Assert.Equal(4, reader.Row);
        Assert.Equal("2023-03-16", reader.GetText(1));
        Assert.Equal([(1, CellValue.FromNumber(45001.5)), (3, CellValue.FromError(CellError.NotAvailable))], reader.Cells);
        Assert.False(reader.Read());
        reader.SelectSheet("second");
        Assert.True(reader.Read());
        Assert.Equal(2.0, reader[1]);
        Assert.False(reader.Read());
    }

    [Fact]
    public void TheReaderReadsWhatTheWorkbookWrites()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        MemoryStream saved = new();
        book.Save(saved);

        foreach (Stream source in (Stream[])[XlsxSeed.Package(XlsxSeed.Features()), saved])
        {
            source.Position = 0;
            using SheetReader reader = SheetReader.Open(source);
            Assert.Equal(["Data", "Hidden", "Table"], reader.SheetNames);
            Worksheet data = book.Worksheets["Data"];
            int rows = 0;
            while (reader.Read())
            {
                rows++;
                for (int column = 0; column < reader.ColumnCount; column++)
                {
                    Assert.Equal(data.Cells.GetValue(reader.Row, column), reader[column]);
                    Assert.Equal(data[reader.Row, column].Text, reader.GetText(column));
                }
            }

            Assert.Equal(9, rows);
            reader.SelectSheet(2);
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.Equal((1, "Ann", 7.0), (reader.Row, reader[0].Text, reader[1].Number));
        }
    }

    [Fact]
    public void PhoneticRunsAreLeftOutAsTheWorkbookLeavesThemOut()
    {
        Dictionary<string, string> parts = XlsxSeed.Features();
        parts["xl/sharedStrings.xml"] = parts["xl/sharedStrings.xml"]
            .Replace("<si><t>Item</t></si>", "<si><t>東京</t><rPh sb=\"0\" eb=\"2\"><t>トウキョウ</t></rPh><phoneticPr fontId=\"1\"/></si>", StringComparison.Ordinal)
            .Replace("<si><t>Price</t></si>", "<si><r><t>大</t></r><rPh sb=\"0\" eb=\"1\"><t>オオ</t></rPh><r><rPr><b/></rPr><t>阪</t></r><rPh sb=\"1\" eb=\"2\"><t>サカ</t></rPh><phoneticPr fontId=\"1\" type=\"Hiragana\"/></si>", StringComparison.Ordinal);
        parts["xl/worksheets/sheet1.xml"] = parts["xl/worksheets/sheet1.xml"].Replace("<c r=\"D1\" s=\"1\" t=\"inlineStr\"><is><t>Total</t></is></c>", "<c r=\"D1\" s=\"1\" t=\"inlineStr\"><is><t>京都</t><rPh sb=\"0\" eb=\"2\"><t>キョウト</t></rPh><phoneticPr fontId=\"1\"/></is></c>", StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(parts));
        Worksheet data = book.Worksheets["Data"];

        using SheetReader reader = SheetReader.Open(XlsxSeed.Package(parts));
        Assert.True(reader.Read());
        Assert.Equal(["東京", "大阪", "Qty", "京都", "When"], Enumerable.Range(0, 5).Select(c => reader[c].Text));
        Assert.Equal(["東京", "大阪", "Qty", "京都", "When"], Enumerable.Range(0, 5).Select(c => data[0, c].Value.Text));
        while (reader.Read())
        {
            for (int column = 0; column < reader.ColumnCount; column++)
            {
                Assert.Equal(data.Cells.GetValue(reader.Row, column), reader[column]);
            }
        }
    }

    [Fact]
    public void AnEntityBombInTheSharedStringsIsRefused()
    {
        Dictionary<string, string> parts = XlsxSeed.Features();
        parts["xl/sharedStrings.xml"] = SheetObjectTests.Bomb("sst") + parts["xl/sharedStrings.xml"].Replace("<t>", "<t>&g;", StringComparison.Ordinal);
        MemoryStream input = XlsxSeed.Package(parts);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<DocumentException>(() =>
        {
            using SheetReader reader = SheetReader.Open(input);
            while (reader.Read())
            {
            }
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4_000_000, $"{allocated:N0} bytes allocated");
    }

    [Fact]
    public void CsvStreamsBothWays()
    {
        MemoryStream text = new();
        CsvOptions options = new() { Culture = new System.Globalization.CultureInfo("de-DE"), NewLine = "\n" };
        using (SheetWriter writer = SheetWriter.CreateCsv(text, options))
        {
            writer.SetColumnFormat(1, "0.00");
            writer.WriteRow("Name", "Amount");
            writer.WriteRow("Ann; Bo", 1234.5);
            Assert.Throws<InvalidOperationException>(() => writer.SetColumnWidth(0, 10));
        }

        Assert.Equal("﻿Name;Amount\n\"Ann; Bo\";1234,50\n", Encoding.UTF8.GetString(text.ToArray()));

        text.Position = 0;
        using SheetReader reader = SheetReader.OpenCsv(text, new CsvOptions { Culture = options.Culture });
        Assert.Equal(["Sheet1"], reader.SheetNames);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(("Ann; Bo", 1234.5), (reader[0].Text, reader[1].Number));
        Assert.Equal("1234,5", reader.GetText(1));
        Assert.False(reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.SelectSheet(0));
    }

    [Fact]
    public void PathsPickTheFormat()
    {
        string folder = Directory.CreateTempSubdirectory("egap-stream").FullName;
        try
        {
            foreach (string name in (string[])["rows.xlsx", "rows.tsv"])
            {
                string path = Path.Combine(folder, name);
                using (SheetWriter writer = SheetWriter.Create(path))
                {
                    writer.WriteRow("a", 1);
                }

                using SheetReader reader = SheetReader.Open(path);
                Assert.True(reader.Read());
                Assert.Equal(("a", 1.0), (reader[0].Text, reader[1].Number));
            }

            Assert.Contains("a\t1", File.ReadAllText(Path.Combine(folder, "rows.tsv")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheWriterChecksWhatItIsGiven()
    {
        using SheetWriter writer = SheetWriter.Create(new MemoryStream());
        writer.StartSheet("One");
        Assert.Throws<ArgumentException>(() => writer.StartSheet("one"));
        Assert.Throws<ArgumentException>(() => writer.StartSheet("Bad/Name"));
        writer.WriteRow(1);
        Assert.Throws<InvalidOperationException>(() => writer.FreezeTopRows(1));
        Assert.Throws<InvalidOperationException>(() => writer.Date1904 = true);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetColumnFormat(-1, "0"));
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.WriteRow(1));
    }

    [Fact]
    public void BrokenFilesAreRefused()
    {
        Assert.Throws<DocumentException>(() => SheetReader.Open(new MemoryStream([1, 2, 3, 4])));
        Dictionary<string, string> parts = XlsxSeed.Features();
        parts["xl/worksheets/sheet1.xml"] = parts["xl/worksheets/sheet1.xml"].Replace("</sheetData>", "", StringComparison.Ordinal);
        MemoryStream broken = XlsxSeed.Package(parts);
        // A sheet entry whose compressed bytes are damaged is refused as a broken file, not a ZIP library error.
        byte[] whole = XlsxSeed.Package(XlsxSeed.Features()).ToArray();
        int at = whole.AsSpan().IndexOf("xl/worksheets/sheet1.xml"u8) + 40;
        whole.AsSpan(at, 64).Fill(0xFF);
        Assert.Throws<DocumentException>(() =>
        {
            using SheetReader damaged = SheetReader.Open(new MemoryStream(whole));
            while (damaged.Read())
            {
            }
        });

        using SheetReader reader = SheetReader.Open(broken);
        Assert.Throws<DocumentException>(() =>
        {
            while (reader.Read())
            {
            }
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void MemoryStaysFlatAsRowsGrow()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("egap-stream").FullName, "rows.xlsx");
        long Peak(int rows)
        {
            GC.Collect();
            long start = GC.GetTotalMemory(true);
            long peak = 0;
            using (SheetWriter writer = SheetWriter.Create(path))
            {
                for (int i = 0; i < rows; i++)
                {
                    writer.WriteRow(i, "row text", i * 0.5);
                    if (i % 5_000 == 0)
                    {
                        peak = Math.Max(peak, GC.GetTotalMemory(false) - start);
                    }
                }
            }

            using (SheetReader reader = SheetReader.Open(path))
            {
                while (reader.Read())
                {
                    if (reader.Row % 5_000 == 0)
                    {
                        peak = Math.Max(peak, GC.GetTotalMemory(false) - start);
                    }
                }
            }

            return peak;
        }

        try
        {
            Peak(20_000);
            long small = Peak(20_000);
            long large = Peak(400_000);
            // Twenty times the rows; what the writer and reader hold stays within the garbage collector's slack.
            Assert.True(large < small + (32L * 1024 * 1024), $"{small} bytes for 20,000 rows, {large} for 400,000");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
