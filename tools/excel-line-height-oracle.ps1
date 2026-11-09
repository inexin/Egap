<#
.SYNOPSIS
Measures the row height Excel's AutoFit gives a line of text in each font, size and weight, for
tools/make-character-widths.py.

.DESCRIPTION
Builds a workbook per font whose normal font is Arial at 4 points, so no row is held up by the default, puts one cell of text per row
in each font, size and weight (bold 0 or 1), has Excel AutoFit the rows and reads their heights. Writes one line per font,
size and weight: its name, size, bold and the row's height in pixels at 96 dpi. Fonts the PC lacks are measured as Excel
substitutes them; a bitmap font's sizes it lacks are left out.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-line-height-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/line-heights.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$fonts = @('Arial', 'Calibri', 'Times New Roman', 'Verdana', 'Tahoma', 'Courier New', 'Arial Narrow', 'MS Sans Serif', 'Cambria',
    'Segoe UI', 'Garamond', 'Century Gothic', 'Aptos Narrow', 'Aptos', 'Book Antiqua', 'Comic Sans MS', 'Helvetica', 'Geneva',
    'Courier', 'Arial Black', 'Trebuchet MS', 'Georgia', 'Univers', 'Zurich Cn BT', 'Lucida Console', 'Palatino Linotype',
    'Bookman Old Style', 'Franklin Gothic Medium Cond', 'Bitstream Vera Sans', 'Arial MT', 'Eras Medium ITC')
$sizes = @(6, 7, 7.5, 8, 8.5, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 26, 28, 36, 48)
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Excel $($excel.Version) build $($excel.Build): row height in pixels of one line of text, AutoFit, written by tools/excel-line-height-oracle.ps1")
    foreach ($font in $fonts) {
        # A workbook per font: Excel refuses more fonts than a workbook may hold.
        $book = $excel.Workbooks.Add()
        $book.Styles.Item('Normal').Font.Name = 'Arial'
        $book.Styles.Item('Normal').Font.Size = 4
        $sheet = $book.Worksheets.Item(1)
        $cases = [System.Collections.Generic.List[object[]]]::new()
        foreach ($size in $sizes) { foreach ($bold in 0, 1) { $cases.Add(@($size, $bold)) } }
        for ($i = 0; $i -lt $cases.Count; $i++) {
            $cell = $sheet.Cells.Item($i + 1, 1)
            $cell.Value2 = 'Hxg'
            $cell.Font.Name = $font
            try { $cell.Font.Size = [double]$cases[$i][0]; $cell.Font.Bold = [bool]$cases[$i][1] } catch { $cell.Value2 = $null }   # a bitmap font without that size
        }
        [void]$sheet.Range($sheet.Cells.Item(1, 1), $sheet.Cells.Item($cases.Count, 1)).EntireRow.AutoFit()
        for ($i = 0; $i -lt $cases.Count; $i++) {
            if ($null -eq $sheet.Cells.Item($i + 1, 1).Value2) { continue }
            $pixels = [math]::Round([double]$sheet.Rows.Item($i + 1).RowHeight / 0.75)
            $lines.Add("$font`t$($cases[$i][0])`t$($cases[$i][1])`t$pixels")
        }
        $book.Close($false)
    }
    [System.IO.File]::WriteAllLines($Output, $lines)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
