using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// CELL, SHEET and SHEETS on a workbook with number formats, alignments, column widths, unlocked cells and a second sheet,
/// against Excel: <see cref="Build"/> makes the workbook, <c>tools/excel-cell-oracle.ps1</c> has Excel calculate and save it
/// as <c>Oracle/cell-info.xlsx</c>, and the test calculates that file again and compares every formula.
/// </summary>
public sealed class CellInfoOracleTests
{
    /// <summary>Formats whose CELL codes Excel was asked for; codes depend on the PC's currency, which here is kr.</summary>
    private static readonly string[] Formats =
    [
        "General", "0", "0.00", "#,##0", "#,##0.00", "$#,##0_);($#,##0)", "$#,##0_);[Red]($#,##0)", "$#,##0.00_);[Red]($#,##0.00)",
        "0%", "0.00%", "0.00E+00", "# ?/?", "# ??/??", "m/d/yy", "m/d/yy h:mm", "mm/dd/yy", "d-mmm-yy", "dd-mmm-yy", "d-mmm",
        "dd-mmm", "mmm-yy", "mm/dd", "h:mm AM/PM", "h:mm:ss AM/PM", "h:mm", "h:mm:ss", "0.000", "#,##0.0", "0;[Red]-0", "0;(0)",
        "(0)", "0.0%;[Red]-0.0%", "@", "yyyy-mm-dd", "[$-409]mmmm d, yyyy", "#,##0 \"kr\"", "[$€-2] #,##0.00", "0_);(0)",
        "#,##0_);[Red](#,##0)", "[h]:mm:ss", "mm:ss", "0.0E+0", "#,##0;-#,##0", "[Blue]0", "dddd", "mmmm", "yy", "d", "hh",
        "ss.0", "\"x\"0", "0.00_)", "# ?/4", "0.##", "#", "#.000", "0.0;(0.0)", "0;0;0;[Red]@", "[Red]0;0", "0;[Color10]0",
        "yyyy", "mmm", "d/m", "m/d", "h", "AM/PM h", "ddd", "0.000000000000000000000", "$0", "[$$-409]#,##0.00",
        "#,##0.00 [$kr-414]", "0%;(0%)", "General;(General)", "[<100]0;0.00", "* #,##0", "_(0_)", ";;;", "0.00;-0.00;\"zero\"",
        "dd.mm.yyyy", "d.m.yy", "dd/mm/yyyy", "yyyy/mm/dd", "mmm d", "d mmmm yyyy", "hh:mm:ss", "h:mm:ss.0", "0.0,", "kr 0", "0 kr",
        "\"kr\" #,##0.00", "#,##0.00\\ \"kr\"", "[Red](0)", "0;-0;;@", "\\(0\\)",
    ];

    /// <summary>The workbook Excel calculates: values in A, their formats, and CELL, SHEET and SHEETS formulas beside them.</summary>
    internal static Workbook Build()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        Worksheet other = book.Worksheets.Add("Other Sheet");
        other["B2"].Value = "o";
        for (int i = 0; i < Formats.Length; i++)
        {
            string row = (i + 1).ToString(CultureInfo.InvariantCulture);
            sheet["A" + row].Value = 1.5;
            sheet["A" + row].NumberFormat = Formats[i];
            sheet["B" + row].Formula = $"=CELL(\"format\",A{row})";
            sheet["C" + row].Formula = $"=CELL(\"color\",A{row})";
            sheet["D" + row].Formula = $"=CELL(\"parentheses\",A{row})";
        }

        (string Cell, object Value, CellHorizontalAlignment Alignment)[] aligned =
        [
            ("F1", "t", CellHorizontalAlignment.Center), ("F2", "t", CellHorizontalAlignment.Right), ("F3", "t", CellHorizontalAlignment.Left),
            ("F4", "t", CellHorizontalAlignment.Fill), ("F5", "t", CellHorizontalAlignment.CenterContinuous), ("F6", "t", CellHorizontalAlignment.Justify),
            ("F7", 5.0, CellHorizontalAlignment.Center), ("F8", "t", CellHorizontalAlignment.General), ("F9", "t", CellHorizontalAlignment.Distributed),
        ];
        foreach ((string cell, object value, CellHorizontalAlignment alignment) in aligned)
        {
            sheet[cell].Value = value is string text ? text : (double)value;
            sheet[cell].UpdateStyle(s => s with { Alignment = s.Alignment with { Horizontal = alignment } });
        }

        sheet["F10"].UpdateStyle(s => s with { Protection = s.Protection with { Locked = false } });
        sheet.Columns[6].Width = 12.7;
        sheet.Columns[7].Width = 3.2;
        sheet.Columns[8].Hidden = true;
        sheet.Columns[9].Width = 2.5;
        string[] formulas =
        [
            "CELL(\"prefix\",F1)", "CELL(\"prefix\",F2)", "CELL(\"prefix\",F3)", "CELL(\"prefix\",F4)", "CELL(\"prefix\",F5)",
            "CELL(\"prefix\",F6)", "CELL(\"prefix\",F7)", "CELL(\"prefix\",F8)", "CELL(\"prefix\",F9)", "CELL(\"prefix\",F11)",
            "CELL(\"protect\",F10)", "CELL(\"protect\",F11)", "CELL(\"width\",G1)", "CELL(\"width\",H1)", "CELL(\"width\",I1)",
            "CELL(\"width\",J1)", "CELL(\"width\",A1)", "INDEX(CELL(\"width\",G1),1,2)", "INDEX(CELL(\"width\",A1),1,2)",
            "CELL(\"address\",'Other Sheet'!B2)", "CELL(\"contents\",'Other Sheet'!B2)", "SHEET('Other Sheet'!A1)", "SHEET(\"Other Sheet\")",
            "SHEET(\"other sheet\")", "SHEET()", "SHEETS()", "CELL(\"type\",F7)", "CELL(\"format\",F1)", "CELL(\"format\",'Other Sheet'!B2)",
        ];
        for (int i = 0; i < formulas.Length; i++)
        {
            sheet["L" + (i + 1).ToString(CultureInfo.InvariantCulture)].Formula = "=" + formulas[i];
        }

        return book;
    }

    /// <summary>Writes the workbook for Excel when EGAP_WRITE_CELL_ORACLE names a file; otherwise does nothing.</summary>
    [Fact]
    public void WritesTheInputWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("EGAP_WRITE_CELL_ORACLE") is { Length: > 0 } path)
        {
            Build().Save(path);
        }
    }

    [Fact]
    public void CellAndSheetFunctionsMatchExcel()
    {
        Workbook book = Workbook.Load(Path.Combine(AppContext.BaseDirectory, "Oracle", "cell-info.xlsx"));
        book.Culture = CultureInfo.GetCultureInfo("nb-NO");
        List<(Cell Cell, CellValue Saved)> formulas = [];
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.UsedRange is { } used)
            {
                formulas.AddRange(used.UsedCells.Where(c => c.Formula is not null).Select(c => (c, c.Value)));
            }
        }

        book.CalculateFull();
        StringBuilder failures = new();
        foreach ((Cell cell, CellValue saved) in formulas)
        {
            if (!cell.Value.Equals(saved))
            {
                string format = cell.Column == 1 ? " " + cell.Worksheet[cell.Row, 0].NumberFormat : "";
                failures.AppendLine(CultureInfo.InvariantCulture, $"{cell.Address} {cell.Formula}{format}: Excel {saved}, Egap {cell.Value}");
            }
        }

        Assert.True(formulas.Count > 200, $"only {formulas.Count} formulas");
        Assert.True(failures.Length == 0, failures.ToString());
    }
}
