namespace Egap.Documents.Tests;

/// <summary>
/// Formula (=) fields, Word's \# numeric pictures and IF fields on formulas. The fixtures in tests/files/fields hold each
/// field's result by Word's documented rules; once tools/update-field-fixtures.ps1 has run, the &lt;name&gt;.word.docx next to
/// a fixture holds the results Word itself computed, and the fixture test compares with those instead.
/// </summary>
public sealed class FormulaFieldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("formulas", 80)]
    [InlineData("formulas-comma", 5)]
    public void FixtureFieldsGiveWordsResults(string name, int fields)
    {
        string word = Fixture(name + ".word.docx");
        Document document = Document.Load(File.Exists(word) ? word : Fixture(name + ".docx"));
        List<(FieldStart Start, string Code, string Expected)> expected = [.. document.GetFields().Select(f => (f.Start, f.Code, f.Result))];
        Assert.True(expected.Count >= fields, $"{expected.Count} fields");
        foreach ((FieldStart start, _, _) in expected)
        {
            FieldUpdater.SetResult(start.GetField()!, "");
        }

        document.UpdateFields(Now);

        List<string> failures = [];
        foreach ((FieldStart start, string code, string result) in expected)
        {
            string actual = start.GetField()!.Result;
            if (actual != result)
            {
                failures.Add($"{{{code.Trim()}}}: Word \"{result}\", Egap \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {expected.Count} fields differ:\n" + string.Join('\n', failures));
    }

    [Theory]
    [InlineData("=2+3", FieldKind.Formula)]
    [InlineData(" = SUM(ABOVE) ", FieldKind.Formula)]
    [InlineData(" IF 1 = 1 \"a\" \"b\" ", FieldKind.If)]
    public void EqualsSignNamesAFormula(string code, FieldKind kind)
    {
        Assert.Equal(kind, Field.KindOf(code));
    }

    [Fact]
    public void AddFieldWritesAnEqualsSign()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Field field = paragraph.AddField(FieldKind.Formula);
        Assert.Equal(" = ", field.Code);
        Assert.Equal(FieldKind.Formula, field.Kind);
    }

    [Theory]
    [InlineData(" = 6*7 ", "42")]
    [InlineData("=2+3", "5")]
    [InlineData(" = 2/3 ", "0.67")]
    [InlineData(" = 1/0 ", "!Zero Divide")]
    [InlineData(" = 2 * ", "!Unexpected End of Formula")]
    [InlineData(" = 2 ) ", "!Syntax Error, )")]
    [InlineData(" = SUM(1,2) \\# \"0.0\" ", "3.0")]
    [InlineData(" = 255 \\* ROMAN ", "CCLV")]
    [InlineData(" = ROUND(-2.5, 0) ", "-3")]
    [InlineData(" = 2 > 1 + 2 ", "0")]
    [InlineData(" = - - 3 ", "3")]
    [InlineData(" = A1 ", "0")]
    [InlineData(" = SUM(ABOVE) + 1 ", "1")]
    [InlineData(" = 2^3^2 ", "512")]
    [InlineData(" = -2^2 ", "4")]
    [InlineData(" = Missing ", "!Undefined Bookmark, MISSING")]
    [InlineData(" = FOO(1) ", "!Syntax Error, (")]
    [InlineData(" = IF(1, 2) ", "!Syntax Error, )")]
    [InlineData(" = ABS(1, 2) ", "!Syntax Error, ,")]
    [InlineData(" = DEFINED(1/0) ", "0")]
    public void FormulasEvaluate(string code, string result)
    {
        Document document = new();
        Field field = document.AddSection().Body.AddParagraph().AddField(code, "old");
        Assert.True(field.Update(Now));
        Assert.Equal(result, field.Start.GetField()!.Result);
    }

    [Fact]
    public void TableReferencesReadCellsByPosition()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Table table = body.AddTable(3, 3);
        string[,] cells = { { "Item", "Qty", "Price" }, { "Bolts", "4", "$1.50" }, { "Nuts", "", "€ 1,000.25" } };
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                table.Rows[r].Cells[c].AddParagraph(cells[r, c]);
            }
        }

        Row total = table.AddRow();
        Field[] fields = new Field[3];
        string[] codes = ["= COUNT(B2:C3)", "= SUM(ABOVE)", "= SUM(C2:C3) \\# \"#,##0.00\""];
        for (int c = 0; c < 3; c++)
        {
            Cell cell = total.AddCell();
            fields[c] = cell.AddParagraph().AddField(codes[c]);
        }

        document.UpdateFields(Now);

        Assert.Equal(["3", "4", "1,001.75"], fields.Select(f => f.Start.GetField()!.Result));
    }

    /// <summary>The flow documents guide's formula sample.</summary>
    [Fact]
    public void GuideSampleTotalsAColumn()
    {
        Document invoice = new();
        Table table = invoice.AddSection().Body.AddTable(3, 2);
        table.Rows[0].Cells[0].AddParagraph("Bolts");
        table.Rows[0].Cells[1].AddParagraph("$4.50");
        table.Rows[1].Cells[0].AddParagraph("Nuts");
        table.Rows[1].Cells[1].AddParagraph("$1,200.00");
        table.Rows[2].Cells[0].AddParagraph("Total");
        Field total = table.Rows[2].Cells[1].AddParagraph().AddField("= SUM(ABOVE) \\# \"$#,##0.00\"");
        invoice.UpdateFields();
        Assert.Equal("$1,204.50", total.Start.GetField()!.Result);
    }

    [Fact]
    public void IfComparesANestedFormula()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.Inlines.Add(new FieldStart(document));
        paragraph.AddRun(" IF ");
        paragraph.AddField("= 40 * 3", "0");
        paragraph.AddRun(" > 100 \"Yes\" \"No\" ");
        paragraph.Inlines.Add(new FieldSeparator(document));
        paragraph.AddRun("?");
        paragraph.Inlines.Add(new FieldEnd(document));

        document.UpdateFields(Now);

        Field condition = document.GetFields().First();
        Assert.Equal(FieldKind.If, condition.Kind);
        Assert.Equal("Yes", condition.Result);
        Assert.Equal("120", document.GetFields().ElementAt(1).Result);
    }

    [Theory]
    [InlineData(" IF 5 > 1,000 \"big\" \"small\" ", "small")]
    [InlineData(" IF $1,500.00 > 1000 \"big\" \"small\" ", "big")]
    [InlineData(" IF 2+3 = 5 \"sum\" \"no\" ", "sum")]
    [InlineData(" IF Oslo = O* \"O\" \"other\" ", "O")]
    [InlineData(" IF Oslo <> Bergen \"differ\" \"same\" ", "differ")]
    public void IfComparesNumbersAndText(string code, string result)
    {
        Document document = new();
        Field field = document.AddSection().Body.AddParagraph().AddField(code, "old");
        Assert.True(field.Update(Now));
        Assert.Equal(result, field.Start.GetField()!.Result);
    }

    [Theory]
    [InlineData(1234.5, "#,##0.00", "1,234.50")]
    [InlineData(1234.5, "0", "1235")]
    [InlineData(5, "00", "05")]
    [InlineData(15, "$###", "$ 15")]
    [InlineData(2456800, "$#,###,###", "$2,456,800")]
    [InlineData(222492, "x##", "492")]
    [InlineData(0.125, "0.00x", "0.125")]
    [InlineData(0.75, ".x", ".8")]
    [InlineData(-80, "-##", "-80")]
    [InlineData(80, "-##", " 80")]
    [InlineData(10, "+##", "+10")]
    [InlineData(-10, "+##", "-10")]
    [InlineData(0, "+##", "   ")]
    [InlineData(-1245.65, "$#,##0.00;-$#,##0.00", "-$1,245.65")]
    [InlineData(-1245.65, "$#,##0.00;($#,##0.00);$0", "($1,245.65)")]
    [InlineData(0, "$#,##0.00;($#,##0.00);$0", "$0")]
    [InlineData(33, "##%", "33%")]
    [InlineData(347.44, "$##0.00 'is sales tax'", "$347.44 is sales tax")]
    [InlineData(-5, "0.00", "-5.00")]
    [InlineData(-0.001, "0.00", "0.00")]
    [InlineData(123456789, "#,##0", "123,456,789")]
    [InlineData(2.5, "0", "3")]
    public void NumericPicturesFollowWord(double value, string picture, string text)
    {
        Assert.Equal(text, FieldCode.FormatNumber(value, picture));
    }

    [Fact]
    public void DecimalCommaPicturesSwapTheSymbols()
    {
        Assert.Equal("1.234,50", FieldCode.FormatNumber(1234.5, "#.##0,00", ','));
    }

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData(" $1,234.50 ", 1234.5)]
    [InlineData("-3", -3.0)]
    [InlineData("(5)", -5.0)]
    [InlineData("50%", 0.5)]
    [InlineData("1 000", null)]
    [InlineData("", null)]
    [InlineData("Total", null)]
    [InlineData("12 kg", null)]
    [InlineData("1,23", null)]
    public void CellTextReadsAsANumber(string text, double? value)
    {
        Assert.Equal(value, FormulaEvaluator.ParseNumber(text, '.'));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "files", "fields", name);
}
