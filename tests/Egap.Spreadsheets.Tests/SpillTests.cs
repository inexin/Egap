using System.IO.Compression;

namespace Egap.Spreadsheets.Tests;

/// <summary>Dynamic array formulas: what spills, what stops a spill, and how they are saved.</summary>
public sealed class SpillTests
{
    private static Worksheet Sheet() => Workbook.Create().Worksheets[0];

    private static void Numbers(Worksheet sheet, string column, params double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            sheet[column + (i + 1)].Value = values[i];
        }
    }

    [Fact]
    public void TakingAHugeNegativeCountTakesEverything()
    {
        Worksheet sheet = Sheet();
        Numbers(sheet, "A", 1, 2, 3);
        sheet["C1"].Formula = "=TAKE(A1:A3,-1E+308)";
        sheet.Workbook.Calculate();
        Assert.Equal("C1:C3", sheet["C1"].SpillRange!.Value.Address.ToString());
        Assert.Equal(3, sheet["C3"].Value.Number);
    }

    [Fact]
    public void AnArrayResultFillsTheCellsBelowAndBeside()
    {
        Worksheet sheet = Sheet();
        sheet["B2"].Formula = "=SEQUENCE(2,3)";
        sheet.Workbook.Calculate();
        Assert.Equal(1, sheet["B2"].Value.Number);
        Assert.Equal(3, sheet["D2"].Value.Number);
        Assert.Equal(6, sheet["D3"].Value.Number);
        Assert.Equal("B2:D3", sheet["B2"].SpillRange!.Value.Address.ToString());
        Assert.False(sheet["D3"].HasFormula);
    }

    [Fact]
    public void ARangeTypedInAFormulaSpillsAndAtMeetsTheCell()
    {
        Worksheet sheet = Sheet();
        Numbers(sheet, "A", 1, 2, 3);
        sheet["C1"].Formula = "=A1:A3*2";
        sheet["D2"].Formula = "=@A1:A3*2";
        sheet.Workbook.Calculate();
        Assert.Equal(6, sheet["C3"].Value.Number);
        Assert.Equal(4, sheet["D2"].Value.Number);
        Assert.Null(sheet["D2"].SpillRange);
    }

    [Fact]
    public void AnOrdinaryFormulaStaysOrdinary()
    {
        Worksheet sheet = Sheet();
        Numbers(sheet, "A", 1, 2, 3);
        sheet["C1"].Formula = "=SUM(A1:A3)+SUMPRODUCT(A1:A3*2)";
        sheet.Workbook.Calculate();
        Assert.Equal(18, sheet["C1"].Value.Number);
        Assert.Null(sheet["C1"].SpillRange);
    }

    [Fact]
    public void ACellInTheWayGivesSpillAndTheSpillComesBackWhenItIsGone()
    {
        Worksheet sheet = Sheet();
        sheet["A3"].Value = "here";
        sheet["A1"].Formula = "=SEQUENCE(3)";
        sheet.Workbook.Calculate();
        Assert.Equal(CellError.Spill, sheet["A1"].Value.Error);
        Assert.True(sheet["A2"].IsEmpty);
        Assert.Equal("here", sheet["A3"].Value.Text);

        sheet["A3"].Value = CellValue.Empty;
        sheet.Workbook.Calculate();
        Assert.Equal(3, sheet["A3"].Value.Number);
    }

    [Fact]
    public void AMergedCellOrATableStopsASpill()
    {
        Worksheet merged = Sheet();
        merged.MergedCells.Add(RangeAddress.Parse("A2:B2"));
        merged["A1"].Formula = "=SEQUENCE(3)";
        merged.Workbook.Calculate();
        Assert.Equal(CellError.Spill, merged["A1"].Value.Error);
    }

    [Fact]
    public void WritingOverASpilledCellStopsTheSpill()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Formula = "=SEQUENCE(3)";
        sheet.Workbook.Calculate();
        sheet["A2"].Value = 99;
        sheet.Workbook.Calculate();
        Assert.Equal(CellError.Spill, sheet["A1"].Value.Error);
        Assert.Equal(99, sheet["A2"].Value.Number);
        Assert.True(sheet["A3"].IsEmpty);
    }

    [Fact]
    public void ASpillThatShrinksEmptiesWhatItLeaves()
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Value = 3;
        sheet["A1"].Formula = "=SEQUENCE(C1)";
        sheet.Workbook.Calculate();
        Assert.Equal(3, sheet["A3"].Value.Number);

        sheet["C1"].Value = 1;
        sheet.Workbook.Calculate();
        Assert.True(sheet["A2"].IsEmpty);
        Assert.True(sheet["A3"].IsEmpty);
        Assert.Equal("A1", sheet["A1"].SpillRange!.Value.Address.ToString());
    }

    [Fact]
    public void ClearingTheFormulaEmptiesItsSpill()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Formula = "=SEQUENCE(3)";
        sheet.Workbook.Calculate();
        sheet["A1"].Value = 5;
        sheet.Workbook.Calculate();
        Assert.Equal(5, sheet["A1"].Value.Number);
        Assert.True(sheet["A3"].IsEmpty);
    }

    [Fact]
    public void WhatReadsASpillFollowsIt()
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Value = 2;

        // The reader is entered first, and reads cells that only the spill fills.
        sheet["E1"].Formula = "=SUM(A1:A5)";
        sheet["F1"].Formula = "=SUM(A1#)";
        sheet["A1"].Formula = "=SEQUENCE(C1)";
        sheet.Workbook.Calculate();
        Assert.Equal(3, sheet["E1"].Value.Number);
        Assert.Equal(3, sheet["F1"].Value.Number);

        sheet["C1"].Value = 4;
        sheet.Workbook.Calculate();
        Assert.Equal(10, sheet["E1"].Value.Number);
        Assert.Equal(10, sheet["F1"].Value.Number);
    }

    [Fact]
    public void ASpillIsSavedAsExcelMarksOneAndReadsBack()
    {
        Worksheet sheet = Sheet();
        Numbers(sheet, "A", 3, 1, 2);
        sheet["C1"].Formula = "=SORT(A1:A3)";
        sheet["D1"].Formula = "=SUM(C1#)";
        sheet["E1"].Formula = "=@A1:A3";
        sheet["F1"].Formula = "=STDEV.S(A1:A3)";
        sheet.Workbook.Calculate();

        using MemoryStream stream = new();
        sheet.Workbook.Save(stream);
        stream.Position = 0;
        using (ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            string xml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
            Assert.Contains("cm=\"1\"", xml, StringComparison.Ordinal);
            Assert.Contains("<f t=\"array\" ref=\"C1:C3\">_xlfn._xlws.SORT(A1:A3)</f>", xml, StringComparison.Ordinal);
            Assert.Contains(">SUM(_xlfn.ANCHORARRAY(C1))</f>", xml, StringComparison.Ordinal);
            Assert.Contains(">A1:A3</f>", xml, StringComparison.Ordinal);
            Assert.Contains(">_xlfn.STDEV.S(A1:A3)</f>", xml, StringComparison.Ordinal);
            string metadata = new StreamReader(zip.GetEntry("xl/metadata.xml")!.Open()).ReadToEnd();
            Assert.Contains("XLDAPR", metadata, StringComparison.Ordinal);
            Assert.Contains("fDynamic=\"1\"", metadata, StringComparison.Ordinal);
        }

        stream.Position = 0;
        Workbook back = Workbook.Load(stream);
        Worksheet read = back.Worksheets[0];
        Assert.Equal("C1:C3", read["C1"].SpillRange!.Value.Address.ToString());
        read["A1"].Value = 0;
        back.Calculate();
        Assert.Equal(0, read["C1"].Value.Number);
        Assert.Equal(2, read["C3"].Value.Number);
        Assert.Equal(3, read["D1"].Value.Number);
    }

    [Fact]
    public void AFileWithSpillsKeepsItsMarkWithoutANewOne()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Formula = "=SEQUENCE(2)";
        sheet.Workbook.Calculate();
        using MemoryStream first = new(), second = new();
        sheet.Workbook.Save(first);
        first.Position = 0;
        Workbook.Load(first).Save(second);
        second.Position = 0;
        using ZipArchive zip = new(second);
        string xml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "cm=\"1\""));
    }
}
