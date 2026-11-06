using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// LET and LAMBDA beyond what <c>Oracle/functions-lambda.txt</c> compares: what cannot be typed, how they are stored, what a
/// cell shows and what depends on what. The stored forms are what Excel 16.0 build 20527 wrote for the same typed formulas.
/// </summary>
public sealed partial class LambdaTests
{
    private static Worksheet Sheet()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        sheet["A3"].Value = 3;
        return sheet;
    }

    [Theory]
    [InlineData("=LET(x,1)")]
    [InlineData("=LET(x,1,y,2)")]
    [InlineData("=LET(x,1,x,2,x)")]
    [InlineData("=LET(X,1,x,2,x)")]
    [InlineData("=LET(1,1,1)")]
    [InlineData("=LET(TRUE,1,1)")]
    [InlineData("=LET(R1,1,R1)")]
    [InlineData("=LET(x.y,1,1)")]
    [InlineData("=LAMBDA()")]
    [InlineData("=LAMBDA(1,2)")]
    [InlineData("=LAMBDA(a,a,1)")]
    [InlineData("=LAMBDA(a,[b],c,a)")]
    public void ExcelRefusesThese(string formula)
    {
        Assert.Throws<FormulaException>(() => Sheet()["C1"].Formula = formula);
    }

    [Theory]
    [InlineData("=LET(x,1,x+1)", "_xlfn.LET(_xlpm.x,1,_xlpm.x+1)", false)]
    [InlineData("=LET(A1,5,A1)", "_xlfn.LET(_xlpm.A1,5,_xlpm.A1)", false)]
    [InlineData("=LET(x,A1:A3,ROWS(x))", "_xlfn.LET(_xlpm.x,A1:A3,ROWS(_xlpm.x))", false)]
    [InlineData("=LET(x,1,y,x+1,x+y)", "_xlfn.LET(_xlpm.x,1,_xlpm.y,_xlpm.x+1,_xlpm.x+_xlpm.y)", false)]
    [InlineData("=LET(x,x,x)", "_xlfn.LET(_xlpm.x,x,_xlpm.x)", true)]
    [InlineData("=LAMBDA(x,x+1)(2)", "_xlfn.LAMBDA(_xlpm.x,_xlpm.x+1)(2)", true)]
    [InlineData("=LAMBDA(x,[y],IF(ISOMITTED(y),x,x+y))(1)", "_xlfn.LAMBDA(_xlpm.x,_xlop.y,IF(_xlfn.ISOMITTED(_xlpm.y),_xlpm.x,_xlpm.x+_xlpm.y))(1)", true)]
    [InlineData("=LET(f,LAMBDA(n,n+1),f(1))", "_xlfn.LET(_xlpm.f,_xlfn.LAMBDA(_xlpm.n,_xlpm.n+1),_xlpm.f(1))", true)]
    [InlineData("=SUM(MAP(A1:A3,LAMBDA(v,v*v)))", "SUM(_xlfn.MAP(A1:A3,_xlfn.LAMBDA(_xlpm.v,_xlpm.v*_xlpm.v)))", true)]
    [InlineData("=SUM(MAP(A1:A3,ABS))", "SUM(_xlfn.MAP(A1:A3,_xleta.ABS))", false)]
    [InlineData("=TEXTJOIN(\",\",,MAP(A1:A3,LAMBDA(v,v)))", "_xlfn.TEXTJOIN(\",\",,_xlfn.MAP(A1:A3,_xlfn.LAMBDA(_xlpm.v,_xlpm.v)))", false)]
    [InlineData("=LAMBDA(x,SUM(x))", "_xlfn.LAMBDA(_xlpm.x,SUM(_xlpm.x))", false)]
    [InlineData("=LET(f,LAMBDA(x,x),1)", "_xlfn.LET(_xlpm.f,_xlfn.LAMBDA(_xlpm.x,_xlpm.x),1)", false)]
    [InlineData("=LET(x,A1:A3,x+1)", "_xlfn.LET(_xlpm.x,A1:A3,_xlpm.x+1)", true)]
    [InlineData("=LET(x,{1,2},SUM(x))", "_xlfn.LET(_xlpm.x,{1,2},SUM(_xlpm.x))", false)]
    [InlineData("=SUM(NOSUCH(1))", "SUM(NOSUCH(1))", false)]
    [InlineData("=(LAMBDA(x,x+1))(2)", "(_xlfn.LAMBDA(_xlpm.x,_xlpm.x+1))(2)", true)]
    [InlineData("=LAMBDA(x,LAMBDA(y,x+y))(1)(2)", "_xlfn.LAMBDA(_xlpm.x,_xlfn.LAMBDA(_xlpm.y,_xlpm.x+_xlpm.y))(1)(2)", true)]
    [InlineData("=BYROW(A1:A3,SUM)", "_xlfn.BYROW(A1:A3,_xleta.SUM)", true)]
    [InlineData("=LET(SUM,5,SUM(1,2))", "_xlfn.LET(_xlpm.SUM,5,SUM(1,2))", false)]
    [InlineData("=NOSUCH(1)", "NOSUCH(1)", true)]
    public void AFormulaIsStoredAsExcelStoresIt(string typed, string stored, bool dynamic)
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Formula = typed;
        sheet.Workbook.Calculate();
        string xml = SheetXml(sheet.Workbook);
        Match cell = Regex.Match(xml, "<c r=\"C1\"[^>]*>");
        Assert.Contains(stored, xml, StringComparison.Ordinal);
        Assert.Equal(dynamic, cell.Value.Contains("cm=\"1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameThatHoldsALambdaIsStoredAsExcelStoresIt()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Names.Add("OPT", "=LAMBDA(a,[b],IF(ISOMITTED(b),a,a+b))");
        Assert.Contains("_xlfn.LAMBDA(_xlpm.a,_xlop.b,IF(_xlfn.ISOMITTED(_xlpm.b),_xlpm.a,_xlpm.a+_xlpm.b))", WorkbookXml(sheet.Workbook), StringComparison.Ordinal);
    }

    [Fact]
    public void AFormulaReadBackGivesTheSameResults()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Names.Add("OPT", "=LAMBDA(a,[b],IF(ISOMITTED(b),a,a+b))");
        sheet.Workbook.Names.Add("TIMES", "=LET(k,2,LAMBDA(z,z*k))");
        sheet["C1"].Formula = "=OPT(1)+OPT(1,2)";
        sheet["C2"].Formula = "=TIMES(4)";
        sheet["C3"].Formula = "=LET(x,5,f,LAMBDA(y,[z],y+x),f(1))";
        sheet["E1"].Formula = "=BYROW(A1:A3,SUM)";
        sheet["F1"].Formula = "=MAP(A1:A3,LAMBDA(v,v*2))";
        using MemoryStream stream = new();
        sheet.Workbook.Save(stream);
        stream.Position = 0;
        Workbook read = Workbook.Load(stream);
        read.CalculateFull();
        Worksheet back = read.Worksheets[0];
        Assert.Equal(4, back["C1"].Value.Number);
        Assert.Equal(8, back["C2"].Value.Number);
        Assert.Equal(6, back["C3"].Value.Number);
        Assert.Equal(3, back["E3"].Value.Number);
        Assert.Equal(6, back["F3"].Value.Number);
        Assert.Equal("F1:F3", back["F1"].SpillRange!.Value.Address.ToString());
    }

    [Fact]
    public void WhatALambdaGivesSpills()
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Formula = "=LAMBDA(x,x+1)(A1:A3)";
        sheet["D1"].Formula = "=MAKEARRAY(2,2,LAMBDA(rr,cc,rr*10+cc))";
        sheet["F1"].Formula = "=REDUCE(0,A1:A3,LAMBDA(a,b,VSTACK(a,b)))";
        sheet.Workbook.Calculate();
        Assert.Equal(4, sheet["C3"].Value.Number);
        Assert.Equal(22, sheet["E2"].Value.Number);
        Assert.Equal(3, sheet["F4"].Value.Number);
    }

    [Fact]
    public void ALambdaInACellShowsCalc()
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Formula = "=LAMBDA(x,x+1)";
        sheet["C2"].Formula = "=SUM";
        sheet.Workbook.Calculate();
        Assert.Equal(CellError.Calc, sheet["C1"].Value.Error);
        Assert.Equal(CellError.Calc, sheet["C2"].Value.Error);
    }

    [Fact]
    public void ACellThatCallsANameFollowsWhatTheNameReads()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Names.Add("PLUSA2", "=LAMBDA(n,n+Sheet1!$A$2)");
        sheet["C1"].Formula = "=PLUSA2(1)";
        sheet["C2"].Formula = "=LET(x,A3,x*2)";
        sheet.Workbook.Calculate();
        Assert.Equal(3, sheet["C1"].Value.Number);
        Assert.Equal(6, sheet["C2"].Value.Number);
        sheet["A2"].Value = 10;
        sheet["A3"].Value = 10;
        sheet.Workbook.Calculate();
        Assert.Equal(11, sheet["C1"].Value.Number);
        Assert.Equal(20, sheet["C2"].Value.Number);
    }

    [Fact]
    public void RecursionEndsAtItsLimit()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Names.Add("DEEP", "=LAMBDA(n,IF(n=0,0,1+DEEP(n-1)))");
        sheet.Workbook.Names.Add("WIDE", "=LAMBDA(n,WIDE(n+1)+WIDE(n+1))");
        sheet.Workbook.Names.Add("FOREVER", "=LAMBDA(FOREVER())");
        sheet.Workbook.Names.Add("LOOPS", "=LOOPS");
        sheet["C1"].Formula = "=DEEP(8000)";
        sheet["C2"].Formula = "=DEEP(100000)";
        sheet["C3"].Formula = "=WIDE(1)";
        sheet["C4"].Formula = "=FOREVER()";
        sheet["C5"].Formula = "=LOOPS(1)";
        sheet.Workbook.Calculate();
        Assert.Equal(8000, sheet["C1"].Value.Number);
        Assert.Equal(CellError.Number, sheet["C2"].Value.Error);
        Assert.Equal(CellError.Number, sheet["C3"].Value.Error);
        Assert.Equal(CellError.Number, sheet["C4"].Value.Error);
        Assert.Equal(CellError.Name, sheet["C5"].Value.Error);
    }

    [Fact]
    public void ABuiltInNameDefinedByTheWorkbookStaysThatName()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Names.Add("RATE", "=2");
        sheet["C1"].Formula = "=LET(x,RATE,x+1)";
        sheet.Workbook.Calculate();
        Assert.Equal(3, sheet["C1"].Value.Number);
        Assert.Contains("_xlfn.LET(_xlpm.x,RATE,_xlpm.x+1)", SheetXml(sheet.Workbook), StringComparison.Ordinal);
    }

    private static string SheetXml(Workbook book) => Part(book, "xl/worksheets/sheet1.xml");

    private static string WorkbookXml(Workbook book) => Part(book, "xl/workbook.xml");

    private static string Part(Workbook book, string name)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        using ZipArchive zip = new(stream);
        using StreamReader reader = new(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
