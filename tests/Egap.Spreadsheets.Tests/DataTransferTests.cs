using System.Data;
using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>Writing lists, tables and readers into cells, and reading ranges back as tables and objects.</summary>
public sealed class DataTransferTests
{
    public enum Status
    {
        Open,
        Shipped,
    }

    public sealed class Order
    {
        public int Id { get; set; }

        public string? Customer { get; set; }

        public decimal Total { get; set; }

        public DateTime Placed { get; set; }

        public bool Paid { get; set; }

        public Status Status { get; set; }

        public double? Discount { get; set; }

        public TimeSpan Duration { get; set; }
    }

    public sealed record Line(string Product, int Quantity, DateOnly Due);

    private static readonly Order[] Orders =
    [
        new() { Id = 1, Customer = "Ada", Total = 12.5m, Placed = new DateTime(2024, 3, 15), Paid = true, Status = Status.Shipped, Discount = 0.1, Duration = TimeSpan.FromHours(1.5) },
        new() { Id = 2, Customer = null, Total = 7m, Placed = new DateTime(2024, 3, 16, 14, 30, 0), Paid = false, Status = Status.Open, Discount = null, Duration = TimeSpan.FromHours(30) },
    ];

    [Fact]
    public void ImportWritesAHeaderAndARowPerItem()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange written = sheet["B2"].ImportData(Orders);

        Assert.Equal("B2:I4", written.Address.ToString());
        Assert.Equal(["Id", "Customer", "Total", "Placed", "Paid", "Status", "Discount", "Duration"], Enumerable.Range(1, 8).Select(c => sheet.Cells[1, c].Value.Text));
        Assert.Equal(1, sheet["B3"].Value.Number);
        Assert.Equal("Ada", sheet["C3"].Value.Text);
        Assert.True(sheet["C4"].IsEmpty);
        Assert.Equal(12.5, sheet["D3"].Value.Number);
        Assert.Equal(45366, sheet["E3"].Value.Number);
        Assert.Equal("2024-03-15", sheet["E3"].Text);
        Assert.Equal("2024-03-16 14:30:00", sheet["E4"].Text);
        Assert.True(sheet["F3"].Value.Boolean);
        Assert.Equal("Shipped", sheet["G3"].Value.Text);
        Assert.True(sheet["H4"].IsEmpty);
        Assert.Equal("1:30:00", sheet["I3"].Text);
        Assert.Equal("30:00:00", sheet["I4"].Text);
    }

    [Fact]
    public void ImportPicksAndOrdersColumns()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange written = sheet["A1"].ImportData(Orders, new DataImportOptions { Columns = ["total", "Id"], Header = false });

        Assert.Equal("A1:B2", written.Address.ToString());
        Assert.Equal(12.5, sheet["A1"].Value.Number);
        Assert.Equal(1, sheet["B1"].Value.Number);
        Assert.Throws<ArgumentException>(() => sheet["A1"].ImportData(Orders, new DataImportOptions { Columns = ["Nope"] }));
    }

    [Fact]
    public void ImportOfPlainValuesFillsOneColumn()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].ImportData(new double[] { 1, double.NaN, 3 });

        Assert.Equal("Value", sheet["A1"].Value.Text);
        Assert.Equal(CellError.Number, sheet["A3"].Value.Error);
        Assert.Equal(3, sheet["A4"].Value.Number);
    }

    [Fact]
    public void ImportCanMakeATable()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].ImportData(Orders, new DataImportOptions { TableName = "Orders" });

        SheetTable table = Assert.Single(sheet.Tables);
        Assert.Equal("Orders", table.Name);
        Assert.Equal("A1:H3", table.Range.ToString());
        Assert.Throws<ArgumentException>(() => sheet["K1"].ImportData(Orders, new DataImportOptions { TableName = "X", Header = false }));
    }

    [Fact]
    public void ImportKeepsTheCellsOtherStyleParts()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A2"].UpdateStyle(s => s with { Font = s.Font with { Bold = true } });
        sheet["A1"].ImportData(new[] { new DateTime(2024, 1, 2) }, new DataImportOptions { DateFormat = "d.m.yyyy" });

        Assert.True(sheet["A2"].Style.Font.Bold);
        Assert.Equal("d.m.yyyy", sheet["A2"].NumberFormat);
    }

    [Fact]
    public void DatesBeforeTheDateSystemBecomeText()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].ImportData(new[] { new DateTime(1850, 6, 1) }, new DataImportOptions { Header = false });

        Assert.Equal("1850-06-01", sheet["A1"].Value.Text);
    }

    [Fact]
    public void DataTableRoundTrips()
    {
        DataTable source = new();
        source.Columns.Add("Name", typeof(string));
        source.Columns.Add("Count", typeof(int));
        source.Columns.Add("When", typeof(DateTime));
        source.Columns.Add("Ok", typeof(bool));
        source.Rows.Add("a", 1, new DateTime(2020, 1, 1), true);
        source.Rows.Add(DBNull.Value, 2, DBNull.Value, false);
        source.Rows.Add("gone", 3, new DateTime(2020, 1, 3), true);
        source.AcceptChanges();
        source.Rows[2].Delete();

        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange written = sheet["A1"].ImportData(source);
        Assert.Equal("A1:D3", written.Address.ToString());

        DataTable back = written.ToDataTable();
        Assert.Equal(["Name", "Count", "When", "Ok"], back.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal([typeof(string), typeof(double), typeof(DateTime), typeof(bool)], back.Columns.Cast<DataColumn>().Select(c => c.DataType));
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal("a", back.Rows[0]["Name"]);
        Assert.Equal(DBNull.Value, back.Rows[1]["Name"]);
        Assert.Equal(2.0, back.Rows[1]["Count"]);
        Assert.Equal(new DateTime(2020, 1, 1), back.Rows[0]["When"]);
        Assert.Equal(DBNull.Value, back.Rows[1]["When"]);
        Assert.Equal(false, back.Rows[1]["Ok"]);
    }

    [Fact]
    public void DataReaderImports()
    {
        DataTable source = new();
        source.Columns.Add("X", typeof(double));
        source.Columns.Add("Y", typeof(string));
        source.Rows.Add(1.5, "one");
        source.Rows.Add(2.5, "two");

        Worksheet sheet = Workbook.Create().Worksheets[0];
        using DataTableReader reader = source.CreateDataReader();
        CellRange written = sheet["C3"].ImportData(reader, new DataImportOptions { Columns = ["Y"] });

        Assert.Equal("C3:C5", written.Address.ToString());
        Assert.Equal("two", sheet["C5"].Value.Text);
        Assert.False(reader.Read());
    }

    [Fact]
    public void ToDataTableTypesMixedColumnsAsObject()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("Mixed");
        sheet["A2"].Value = CellValue.FromNumber(1);
        sheet["A3"].Value = CellValue.FromText("x");
        sheet["B1"].Value = CellValue.FromText("Time");
        sheet["B2"].Value = CellValue.FromNumber(0.5);
        sheet["B2"].NumberFormat = "hh:mm";
        sheet["C1"].Value = CellValue.FromText("Mixed");

        DataTable table = sheet.Range("A1:C3").ToDataTable();

        Assert.Equal(typeof(object), table.Columns[0].DataType);
        Assert.Equal(1.0, table.Rows[0][0]);
        Assert.Equal("x", table.Rows[1][0]);
        Assert.Equal(typeof(TimeSpan), table.Columns[1].DataType);
        Assert.Equal(TimeSpan.FromHours(12), table.Rows[0][1]);
        Assert.Equal("Mixed2", table.Columns[2].ColumnName);
    }

    [Fact]
    public void DateFormattedNumbersThatAreNoDatesStayNumbers()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("When");
        sheet["A2"].Value = CellValue.FromNumber(45366);
        sheet["A3"].Value = CellValue.FromNumber(-685484.5);
        sheet.Range("A2:A3").UpdateStyle(s => s with { NumberFormat = "yyyy-mm-dd" });

        DataTable table = sheet.Range("A1:A3").ToDataTable();

        Assert.Equal(typeof(object), table.Columns[0].DataType);
        Assert.Equal(new DateTime(2024, 3, 15), table.Rows[0][0]);
        Assert.Equal(-685484.5, table.Rows[1][0]);
    }

    [Fact]
    public void WholeColumnsReadOnlyAsFarAsTheSheetHoldsAnything()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].ImportData(Orders);

        DataTable table = sheet.Range("A:B").ToDataTable();
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void ToListRoundTripsObjects()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange written = sheet["A1"].ImportData(Orders);

        List<Order> back = written.ToList<Order>();

        Assert.Equal(2, back.Count);
        for (int i = 0; i < Orders.Length; i++)
        {
            Assert.Equal(Orders[i].Id, back[i].Id);
            Assert.Equal(Orders[i].Customer, back[i].Customer);
            Assert.Equal(Orders[i].Total, back[i].Total);
            Assert.Equal(Orders[i].Placed, back[i].Placed);
            Assert.Equal(Orders[i].Paid, back[i].Paid);
            Assert.Equal(Orders[i].Status, back[i].Status);
            Assert.Equal(Orders[i].Discount, back[i].Discount);
            Assert.Equal(Orders[i].Duration, back[i].Duration);
        }
    }

    [Fact]
    public void ToListFillsRecordsByHeaderName()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("Due");
        sheet["B1"].Value = CellValue.FromText("unused");
        sheet["C1"].Value = CellValue.FromText("PRODUCT");
        sheet["D1"].Value = CellValue.FromText("quantity");
        sheet["A2"].Value = CellValue.FromNumber(45366);
        sheet["C2"].Value = CellValue.FromText("Pen");
        sheet["D2"].Value = CellValue.FromText("4");

        Line line = Assert.Single(sheet.Range("A1:D3").ToList<Line>());

        Assert.Equal(new Line("Pen", 4, new DateOnly(2024, 3, 15)), line);
    }

    [Fact]
    public void ToListReadsTextInTheWorkbookCulture()
    {
        Workbook book = Workbook.Create();
        book.Culture = CultureInfo.GetCultureInfo("nb-NO");
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("1,5");

        Assert.Equal([1.5], sheet.Range("A1:A1").ToList<double>(new DataExportOptions { Header = false }));
    }

    [Fact]
    public void ToListNamesTheCellThatDoesNotConvert()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("Id");
        sheet["A2"].Value = CellValue.FromNumber(1);
        sheet["A3"].Value = CellValue.FromNumber(2.5);

        FormatException error = Assert.Throws<FormatException>(() => sheet.Range("A1:A3").ToList<Order>());
        Assert.Contains("A3", error.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToListWithoutAHeaderFillsPropertiesInOrder()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromNumber(7);
        sheet["B1"].Value = CellValue.FromText("Bo");

        Order order = Assert.Single(sheet.Range("A1:B1").ToList<Order>(new DataExportOptions { Header = false }));
        Assert.Equal(7, order.Id);
        Assert.Equal("Bo", order.Customer);
    }

    [Fact]
    public void EmptyRowsAreSkippedUnlessAsked()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("V");
        sheet["A2"].Value = CellValue.FromNumber(1);
        sheet["A4"].Value = CellValue.FromNumber(2);

        Assert.Equal([1.0, 2.0], sheet.Range("A1:A4").ToList<double>());
        Assert.Equal([1.0, 0.0, 2.0], sheet.Range("A1:A4").ToList<double>(new DataExportOptions { SkipEmptyRows = false }));
        Assert.Equal(3, sheet.Range("A1:A4").ToDataTable(new DataExportOptions { SkipEmptyRows = false }).Rows.Count);
    }

    [Fact]
    public void ImportSurvivesSaveAndLoad()
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].ImportData(Orders, new DataImportOptions { TableName = "Orders" });
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;

        Worksheet sheet = Workbook.Load(stream).Worksheets[0];
        List<Order> back = sheet.Tables[0].Range is var range ? sheet.Range(range).ToList<Order>() : [];
        Assert.Equal(Orders.Select(o => o.Placed), back.Select(o => o.Placed));
        Assert.Equal("Orders", sheet.Tables[0].Name);
    }
}
