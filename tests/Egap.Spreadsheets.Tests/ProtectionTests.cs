namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Sheet and workbook protection against Excel: <c>tools/excel-protection-oracle.ps1</c> opened the files
/// <see cref="WritesTheFilesExcelChecks"/> saves and recorded in <c>Oracle/protection.excel.tsv</c> what Excel found, and
/// protected <c>Oracle/protection.excel.xlsx</c> in Excel for Egap to unlock.
/// </summary>
public sealed class ProtectionTests
{
    private static readonly string Oracle = Path.Combine(AppContext.BaseDirectory, "Oracle");

    private static Dictionary<string, string> Excel => File.ReadAllLines(Path.Combine(Oracle, "protection.excel.tsv"))
        .Select(line => line.Split('\t')).ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void WritesTheFilesExcelChecks()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("locked");
        sheet["C2"].Value = CellValue.FromNumber(5);
        sheet["C2"].UpdateStyle(s => s with { Protection = s.Protection with { Locked = false } });
        sheet.Protection.EditableRanges.Add("Inputs", "B2:B5", "range-pw1");
        sheet.Protection.EditableRanges.Add("Open", "D2:D3");
        sheet.Protection.Protect("Egap-pass1", SheetActions.Default | SheetActions.Sort | SheetActions.FormatColumns);
        book.Protection.Protect("book-pw1");
        book.Save(Path.Combine(AppContext.BaseDirectory, "protection.egap.xlsx"));

        Workbook old = Workbook.Create();
        old.Worksheets[0]["A1"].Value = CellValue.FromText("x");
        old.Worksheets[0].Protection.Set(true, SheetActions.Default, PasswordHash.Read(null, null, null, null, PasswordHash.LegacyHash("legacy1").ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        old.Protection.LockStructure = true;
        old.Protection.Password = PasswordHash.Read(null, null, null, null, PasswordHash.LegacyHash("legacy2").ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        old.Save(Path.Combine(AppContext.BaseDirectory, "protection.legacy.xlsx"));

        Workbook back = Workbook.Load(Path.Combine(AppContext.BaseDirectory, "protection.egap.xlsx"));
        Assert.True(back.Worksheets[0].Protection.Verify("Egap-pass1"));
        Assert.True(Workbook.Load(Path.Combine(AppContext.BaseDirectory, "protection.legacy.xlsx")).Protection.Verify("legacy2"));
    }

    [Fact]
    public void ExcelOpensEgapsFilesProtected()
    {
        Assert.Equal("True", Excel["egap sheet protected"]);
        Assert.Equal("True", Excel["egap objects protected"]);
        Assert.Equal("True", Excel["egap allow sorting"]);
        Assert.Equal("True", Excel["egap allow formatting columns"]);
        Assert.Equal("False", Excel["egap allow formatting cells"]);
        Assert.Equal("False", Excel["egap allow inserting rows"]);
        Assert.Equal("False", Excel["egap C2 locked"]);
        Assert.Equal("B2:B5", Excel["egap edit range Inputs"]);
        Assert.Equal("D2:D3", Excel["egap edit range Open"]);
        Assert.Equal("True", Excel["egap book structure"]);
        Assert.Equal("True", Excel["legacy sheet protected"]);
        Assert.Equal("True", Excel["legacy book structure"]);
    }

    [Theory]
    [InlineData("egap sheet")]
    [InlineData("egap book")]
    [InlineData("legacy sheet")]
    [InlineData("legacy book")]
    public void ExcelUnlocksEgapsFilesWithThePasswordOnly(string what)
    {
        Assert.Equal("refused", Excel[what + " wrong password"]);
        Assert.Equal("unlocked", Excel[what + " right password"]);
    }

    [Fact]
    public void EgapUnlocksExcelsFile()
    {
        Workbook book = Workbook.Load(Path.Combine(Oracle, "protection.excel.xlsx"));
        SheetProtection protection = book.Worksheets[0].Protection;

        Assert.True(protection.IsProtected);
        Assert.True(protection.HasPassword);
        Assert.Equal(SheetActions.Default | SheetActions.FormatCells | SheetActions.InsertRows | SheetActions.AutoFilter, protection.AllowedActions);
        Assert.False(protection.Verify("xl-pass1"));
        Assert.True(protection.Verify("Xl-pass1"));

        EditableRange range = Assert.Single(protection.EditableRanges);
        Assert.Equal("XlRange", range.Name);
        Assert.Equal("E2:E4", range.Ranges.Single().ToString());
        Assert.False(range.Verify("nope"));
        Assert.True(range.Verify("Xl-range1"));

        Assert.True(book.Protection.LockStructure);
        Assert.False(book.Protection.LockWindows);
        Assert.False(book.Protection.Unprotect("x"));
        Assert.True(book.Protection.IsProtected);
        Assert.True(book.Protection.Unprotect("Xl-book1"));
        Assert.False(book.Protection.IsProtected);

        Assert.False(protection.Unprotect("bad"));
        Assert.True(protection.Unprotect("Xl-pass1"));
        Assert.False(protection.IsProtected);
    }

    [Fact]
    public void ExcelsFileKeepsItsProtectionThroughASave()
    {
        Workbook book = Workbook.Load(Path.Combine(Oracle, "protection.excel.xlsx"));
        Workbook back = SaveAndLoad(book);

        Assert.True(back.Worksheets[0].Protection.Verify("Xl-pass1"));
        Assert.Equal(book.Worksheets[0].Protection.AllowedActions, back.Worksheets[0].Protection.AllowedActions);
        Assert.True(back.Worksheets[0].Protection.EditableRanges[0].Verify("Xl-range1"));
        Assert.True(back.Protection.Verify("Xl-book1"));
        Assert.True(back.Protection.LockStructure);
    }

    [Theory]
    [InlineData("legacy1", 0xC088)]
    [InlineData("legacy2", 0xC108)]
    [InlineData("", 0)]
    public void LegacyHashIsTheOneExcelChecks(string password, int hash)
    {
        Assert.Equal(hash, PasswordHash.LegacyHash(password));
    }

    [Fact]
    public void ProtectHashesWithSha512AndAFreshSalt()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.Protection.Protect("pw");
        PasswordHash first = sheet.Protection.Password!;
        sheet.Protection.Protect("pw");
        PasswordHash second = sheet.Protection.Password!;

        Assert.Equal("SHA-512", first.Algorithm);
        Assert.Equal(100_000, first.SpinCount);
        Assert.Equal(16, first.Salt!.Length);
        Assert.Equal(64, first.Hash!.Length);
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.True(sheet.Protection.Verify("pw"));
    }

    [Fact]
    public void WithoutAPasswordAnyoneUnprotects()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Protection.Protect();
        book.Protection.Protect(windows: true);

        Assert.False(sheet.Protection.HasPassword);
        Assert.True(book.Protection.LockWindows);
        Workbook back = SaveAndLoad(book);
        Assert.True(back.Worksheets[0].Protection.IsProtected);
        Assert.True(back.Protection.LockWindows);
        Assert.True(back.Worksheets[0].Protection.Unprotect());
        Assert.True(back.Protection.Unprotect());

        Workbook open = SaveAndLoad(back);
        Assert.False(open.Worksheets[0].Protection.IsProtected);
        Assert.False(open.Protection.IsProtected);
    }

    [Fact]
    public void CanEditFollowsLockedCellsAndEditableRanges()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["B1"].UpdateStyle(s => s with { Protection = s.Protection with { Locked = false } });
        sheet.Protection.EditableRanges.Add("Free", "C1:C3");
        sheet.Protection.EditableRanges.Add("Closed", "D1:D3", "pw");

        Assert.True(sheet.Protection.CanEdit(CellAddress.Parse("A1")));
        sheet.Protection.Protect("x");
        Assert.False(sheet.Protection.CanEdit(CellAddress.Parse("A1")));
        Assert.True(sheet.Protection.CanEdit(CellAddress.Parse("B1")));
        Assert.True(sheet.Protection.CanEdit(CellAddress.Parse("C2")));
        Assert.False(sheet.Protection.CanEdit(CellAddress.Parse("D2")));
    }

    [Fact]
    public void EditableRangesMoveWithInsertedRows()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        EditableRange range = sheet.Protection.EditableRanges.Add("R", "B3:C4");
        sheet.Protection.EditableRanges.Add("Gone", "A1:A1");

        sheet.InsertRows(0, 2);
        Assert.Equal("B5:C6", range.Ranges.Single().ToString());

        sheet.DeleteRows(0, 3);
        Assert.Same(range, Assert.Single(sheet.Protection.EditableRanges));
        Assert.Equal("B2:C3", range.Ranges.Single().ToString());
    }

    private static Workbook SaveAndLoad(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }
}
