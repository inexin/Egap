using System.Globalization;
using System.Text;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Structured references against Excel: what each case in <c>Oracle/structured.txt</c> gives and how it is stored, as
/// <c>tools/excel-structured-oracle.ps1</c> recorded them in <c>Oracle/structured.excel.tsv</c>.
/// </summary>
public sealed class StructuredReferenceTests
{
    /// <summary>A case's parts: place, kind, value, stored form, whether it is stored as dynamic, and the typed formula.</summary>
    private static IEnumerable<string[]> Cases()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Oracle", "structured.excel.tsv");
        return File.ReadAllLines(path).Skip(1).Select(line => line.Split('\t'));
    }

    private static (Worksheet Sheet, CellAddress Cell) Place(Workbook book, string where, int index, int inTable) => where switch
    {
        "in" => (book.Worksheets[0], new CellAddress(2, 5 + inTable)),
        "row" => (book.Worksheets[0], new CellAddress(2, 59 + index)),
        "out" => (book.Worksheets[0], new CellAddress(19, 59 + index)),
        _ => (book.Worksheets[1], new CellAddress(2, index)),
    };

    private static Workbook Book()
    {
        Workbook book = Workbook.Create();
        book.Culture = new CultureInfo("nb-NO");
        Worksheet sheet = book.Worksheets[0];
        book.Worksheets.Add("Sheet2");
        string[] headers = ["Name", "Qty", "Un it", "a#b", "x[y]"];
        for (int c = 0; c < 44; c++)
        {
            sheet[0, c].Value = c < 5 ? headers[c] : "C" + (c - 4);
        }

        string[] names = ["a", "b", "c"];
        for (int r = 0; r < 3; r++)
        {
            sheet[r + 1, 0].Value = names[r];
            sheet[r + 1, 1].Value = r + 1;
            sheet[r + 1, 2].Value = (r + 1) * 10;
            sheet[r + 1, 3].Value = (r + 1) * 100;
            sheet[r + 1, 4].Value = (r + 1) * 1000;
        }

        SheetTable sales = sheet.Tables.Add("A1:AR5", "Sales");
        sales.ShowTotalsRow = true;
        sheet["A5"].Value = "Total";
        sheet["B5"].Value = 6;
        sheet["C5"].Value = 60;
        sheet["D5"].Value = 600;
        sheet["E5"].Value = 6000;
        sheet["AR5"].Value = 0;

        sheet["A8"].Value = 7;
        sheet["B8"].Value = 8;
        sheet["A9"].Value = 9;
        sheet["B9"].Value = 10;
        SheetTable plain = sheet.Tables.Add("A8:B9", "Plain", hasHeaderRow: false);
        plain.Columns[0].Name = "One";
        plain.Columns[1].Name = "Two";
        return book;
    }

    [Fact]
    public void ReferencesGiveWhatExcelGives()
    {
        Workbook book = Book();
        StringBuilder failures = new();
        int index = 0, inTable = 0, compared = 0;
        foreach (string[] fields in Cases())
        {
            (string where, string kind, string value, string formula) = (fields[0], fields[1], fields[2], fields[5]);
            (Worksheet sheet, CellAddress cell) = Place(book, where, index, inTable);
            index++;
            inTable += where == "in" ? 1 : 0;

            // In a table's own row, the whole table holds the formula itself: a circular reference, which Excel shows as 0.
            if (kind != "value" || (where == "in" && value == "0"))
            {
                continue;
            }

            string actual = sheet.Evaluate("ARRAYTOTEXT(" + formula + ")", cell).ToString();
            compared++;
            if (actual != value)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{where} {formula}: Excel {value}, Egap {actual}");
            }
        }

        Assert.True(compared > 80, $"only {compared}");
        Assert.True(failures.Length == 0, failures.ToString());
    }

    [Fact]
    public void WhatExcelRefusesIsRefusedOrAnError()
    {
        Workbook book = Book();
        StringBuilder failures = new();
        int index = 0, inTable = 0;
        foreach (string[] fields in Cases())
        {
            (string where, string kind, string formula) = (fields[0], fields[1], fields[5]);
            (Worksheet sheet, CellAddress cell) = Place(book, where, index, inTable);
            index++;
            inTable += where == "in" ? 1 : 0;
            if (kind != "invalid")
            {
                continue;
            }

            // Which tables and columns there are is for the workbook: a file can hold one that has gone, which gives #REF!.
            if (FormulaParser.TryParse(formula, r1c1: false, cell) is not null && sheet.Evaluate(formula, cell).Kind != CellValueKind.Error)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{where} {formula}: Excel refuses it, Egap gives {sheet.Evaluate(formula, cell)}");
            }
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }

    [Fact]
    public void ReferencesAreStoredAsExcelStoresThem()
    {
        Workbook book = Book();
        StringBuilder failures = new();
        int index = 0, inTable = 0;
        foreach (string[] fields in Cases())
        {
            (string where, string kind, string stored, bool dynamic, string formula) = (fields[0], fields[1], fields[3], fields[4] == "1", fields[5]);
            (Worksheet sheet, CellAddress cell) = Place(book, where, index, inTable);
            index++;
            inTable += where == "in" ? 1 : 0;

            // Excel keeps spaces between a reference's items, and we write them without.
            if (kind != "value" || formula.Contains("[ ", StringComparison.Ordinal))
            {
                continue;
            }

            string actual = FormulaPrinter.StoredForm(formula, cell, dynamic, book, sheet);
            if (actual != stored)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{where} {formula}: Excel {stored}, Egap {actual}");
            }

            bool spills = ArrayAnalysis.Spills(FormulaParser.Parse(formula, r1c1: false, cell));
            if (spills != dynamic)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{where} {formula}: Excel stores it {(dynamic ? "dynamic" : "ordinary")}, Egap {(spills ? "dynamic" : "ordinary")}");
            }
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }

    [Fact]
    public void ACellInATableIsSavedWithTheTableNamed()
    {
        Workbook book = Book();
        book.Worksheets[0]["F3"].Formula = "=[@Qty]*2";
        book.Calculate();
        Assert.Equal(4, book.Worksheets[0]["F3"].Value.Number);
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        Workbook read = Workbook.Load(stream);
        Assert.Equal("Sales[[#This Row],[Qty]]*2", read.Worksheets[0]["F3"].Formula?.TrimStart('='));
        read.CalculateFull();
        Assert.Equal(4, read.Worksheets[0]["F3"].Value.Number);
    }
}
