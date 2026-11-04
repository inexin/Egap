namespace Egap.Spreadsheets.Tests;

/// <summary>Workbooks, sheets and cells: values, formulas, the sparse block store and shared strings.</summary>
public sealed class CellTests
{
    [Fact]
    public void ANewWorkbookHasOneEmptySheet()
    {
        Workbook book = Workbook.Create();

        Worksheet sheet = Assert.Single(book.Worksheets);
        Assert.Equal("Sheet1", sheet.Name);
        Assert.Same(book, sheet.Workbook);
        Assert.Equal(0, sheet.Index);
        Assert.Null(sheet.UsedRange);
        Assert.Equal(0, sheet.Cells.Count);
        Assert.Empty(Workbook.CreateEmpty().Worksheets);
    }

    [Fact]
    public void CellsHoldEveryKindOfValue()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        sheet["A1"].Value = 42;
        sheet["A2"].Value = 1.5;
        sheet["A3"].Value = "Total";
        sheet["A4"].Value = true;
        sheet["A5"].Value = CellError.DivisionByZero;
        sheet["A6"].Value = -0.0;

        Assert.Equal(42, sheet["A1"].Value.Number);
        Assert.Equal(1.5, sheet.Cells[1, 0].Value.Number);
        Assert.Equal("Total", sheet["A3"].Value.Text);
        Assert.True(sheet["A4"].Value.Boolean);
        Assert.Equal(CellError.DivisionByZero, sheet["A5"].Value.Error);
        Assert.Equal("#DIV/0!", sheet["A5"].Value.ToString());
        Assert.False(double.IsNegative(sheet["A6"].Value.Number));
        Assert.Equal(CellValueKind.Text, sheet["A3"].ValueKind);
        Assert.True(sheet["A7"].IsEmpty);
        Assert.Equal(CellValue.Empty, sheet["A7"].Value);
        Assert.Equal(6, sheet.Cells.Count);
        Assert.Equal("A1:A6", sheet.UsedRange!.Value.Address.ToString());
    }

    [Fact]
    public void ValuesOutsideExcelsLimitsAreRefused()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => sheet["A1"].Value = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet["A1"].SetNumber(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => sheet["A1"].Value = new string('x', CellValue.MaxTextLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet[CellAddress.MaxRows, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Cells[0, CellAddress.MaxColumns]);
        Assert.Throws<InvalidOperationException>(() => CellValue.FromNumber(1).Text);
        sheet["A1"].Value = new string('x', CellValue.MaxTextLength);
        Assert.Equal(CellValue.MaxTextLength, sheet["A1"].Value.Text.Length);
    }

    [Fact]
    public void ErrorTextsRoundTrip()
    {
        foreach (CellError error in Enum.GetValues<CellError>())
        {
            Assert.True(CellValue.TryParseError(CellValue.ErrorText(error), out CellError parsed));
            Assert.Equal(error, parsed);
        }

        Assert.True(CellValue.TryParseError("#n/a", out CellError na));
        Assert.Equal(CellError.NotAvailable, na);
        Assert.False(CellValue.TryParseError("#OOPS!", out _));
    }

    [Fact]
    public void SettingAValueKeepsTheStyleAndClearRemovesBoth()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Cell cell = sheet["C3"];

        cell.NumberFormat = "0.00";
        Assert.Equal(1, sheet.Cells.Count);
        Assert.True(cell.IsEmpty);
        cell.Value = 3.14159;
        Assert.Equal("0.00", cell.NumberFormat);
        cell.Value = CellValue.Empty;
        Assert.Equal("0.00", cell.Style.NumberFormat);
        Assert.Equal(1, sheet.Cells.Count);

        cell.Clear();
        Assert.Equal(0, sheet.Cells.Count);
        Assert.Same(CellStyle.Default, cell.Style);
        Assert.Equal(0, sheet.BlockCount);
    }

    [Fact]
    public void FormulasAreKeptWithoutTheirEqualsSign()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        Cell total = sheet["A3"];

        total.Formula = "=SUM(A1:A2)";
        Assert.Equal("SUM(A1:A2)", total.Formula);
        Assert.True(total.HasFormula);
        Assert.False(total.IsEmpty);
        Assert.True(total.Value.IsEmpty);
        Assert.Equal(CellValueKind.Empty, total.ValueKind);
        Assert.Equal(1, sheet.Formulas.Count);

        total.Formula = "A1+A2";
        Assert.Equal("A1+A2", total.Formula);
        Assert.Equal(1, sheet.Formulas.Count);

        total.Value = 3;
        Assert.Null(total.Formula);
        Assert.Equal(0, sheet.Formulas.Count);

        total.Formula = "A1*2";
        total.Formula = null;
        Assert.False(total.HasFormula);
        Assert.Equal(0, sheet.Formulas.Count);
        Assert.Throws<ArgumentException>(() => total.Formula = new string('1', 8193));
    }

    [Fact]
    public void TextIsSharedAcrossSheets()
    {
        Workbook book = Workbook.Create();
        Worksheet second = book.Worksheets.Add();

        for (int row = 0; row < 100; row++)
        {
            book.Worksheets[0][row, 0].Value = "North";
            second[row, 0].Value = row % 2 == 0 ? "North" : "South";
        }

        Assert.Equal(2, book.SharedStrings.Count);
        Assert.Equal("South", second["A2"].Value.Text);
    }

    [Fact]
    public void FarApartCellsCostOneBlockEach()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        sheet["A1"].Value = 1;
        sheet["XFD1048576"].Value = 2;

        Assert.Equal(2, sheet.BlockCount);
        Assert.Equal(2, sheet.Cells.Count);
        Assert.Equal(new RangeAddress(0, 0, CellAddress.MaxRows - 1, CellAddress.MaxColumns - 1), sheet.UsedRange!.Value.Address);
        Assert.Equal(2, sheet["XFD1048576"].Value.Number);
        Assert.True(sheet["XFD1048575"].IsEmpty);

        sheet["A1"].Clear();
        Assert.Equal(1, sheet.BlockCount);
        Assert.Equal("XFD1048576", sheet.UsedRange!.Value.Address.ToString());
    }

    [Fact]
    public void ScatteredCellsStaySmall()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Random random = new(3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sheet.Cells.SetValue(random.Next(CellAddress.MaxRows), random.Next(CellAddress.MaxColumns), i);
        }

        long perCell = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        Assert.InRange(perCell, 1, 5000);
    }

    [Fact]
    public void BlocksAreThirtyTwoByThirtyTwo()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        sheet.Range("A1:AF32").SetValue(1);
        Assert.Equal(1, sheet.BlockCount);
        sheet["AG1"].Value = 1;
        sheet["A33"].Value = 1;
        Assert.Equal(3, sheet.BlockCount);
    }

    [Fact]
    public void CellsEnumerateRowByRowAndMayChangeMeanwhile()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        string[] addresses = ["C1", "A2", "AH2", "B40", "A1000"];
        foreach (string address in addresses.Reverse())
        {
            sheet[address].Value = address;
        }

        Assert.Equal(addresses, sheet.Cells.Select(cell => cell.Address.ToString()));

        foreach (Cell cell in sheet.Cells)
        {
            cell.Clear();
        }

        Assert.Equal(0, sheet.Cells.Count);
        Assert.Equal(0, sheet.BlockCount);
    }

    [Fact]
    public void ValuesMatchAPlainArrayUnderRandomEdits()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Dictionary<(int, int), CellValue> expected = [];
        Random random = new(7);

        for (int i = 0; i < 20_000; i++)
        {
            int row = random.Next(3) == 0 ? random.Next(CellAddress.MaxRows) : random.Next(200);
            int column = random.Next(3) == 0 ? random.Next(CellAddress.MaxColumns) : random.Next(70);
            CellValue value = random.Next(5) switch
            {
                0 => CellValue.Empty,
                1 => random.NextDouble(),
                2 => "s" + random.Next(50),
                3 => random.Next(2) == 0,
                _ => (CellError)random.Next(10),
            };

            sheet[row, column].Value = value;
            if (value.IsEmpty)
            {
                expected.Remove((row, column));
            }
            else
            {
                expected[(row, column)] = value;
            }
        }

        Assert.Equal(expected.Count, sheet.Cells.Count);
        foreach (((int row, int column), CellValue value) in expected)
        {
            Assert.Equal(value, sheet[row, column].Value);
        }

        Assert.Equal(expected.Keys.Order(), sheet.Cells.Select(cell => (cell.Row, cell.Column)));
    }

    [Fact]
    public void CellHandlesCompareByPosition()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];

        Assert.Equal(sheet["B4"], sheet.Cells[3, 1]);
        Assert.NotEqual(sheet["B4"], book.Worksheets.Add()["B4"]);
        sheet["B4"].Value = 7;
        Assert.Equal("B4: 7", sheet["B4"].ToString());
    }
}
