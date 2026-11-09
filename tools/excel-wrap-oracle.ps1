<#
.SYNOPSIS
Measures how Excel wraps text in cells, for the AutoFit tests.

.DESCRIPTION
Puts each text in a wrapped cell of each font and column width, one cell per row, has Excel AutoFit the rows and reads their
heights. Writes one line per case: the font as name, size and style (0 regular, 1 bold), the column's width in pixels at
96 dpi, the row's height in pixels and the text. The height over the font's line height is the number of lines Excel broke
the text into.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-wrap-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/wrap.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$texts = @(
    'Agencies must NOT alter the format of this worksheet.',
    'Total number of households receiving assistance during the year',
    'State-by-state estimates of long-term care expenditures',
    'Fiscal year 2007/2008 appropriations (in thousands of dollars)',
    'Notes: data for 2006 are preliminary; see the technical documentation.',
    'Supercalifragilisticexpialidocious words break where?',
    'Department of Health and Human Services, Centers for Medicare & Medicaid Services',
    'a b c d e f g h i j k l m n o p q r s t u v w x y z',
    'one  two   three    four (double spaces)',
    'Reimbursement-eligible non-profit organizations',
    'http://www.example.gov/data/statistics/annual-report.xls',
    'PERCENT OF TOTAL ENROLLMENT BY RACE/ETHNICITY AND SEX')
$fonts = @('Arial,10,0', 'Calibri,11,0', 'Times New Roman,10,0', 'Arial,8,1', 'Arial,12,0', 'Verdana,10,0')
$widths = @(4, 6, 8, 10, 12, 14, 16, 18, 20, 23, 26, 30, 35, 40, 45, 50, 60)
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Excel $($excel.Version) build $($excel.Build): wrapped cells, AutoFit, written by tools/excel-wrap-oracle.ps1")
    $book = $excel.Workbooks.Add()
    $book.Styles.Item('Normal').Font.Name = 'Arial'
    $book.Styles.Item('Normal').Font.Size = 10
    $s = $book.Worksheets.Item(1)
    for ($j = 0; $j -lt $widths.Count; $j++) { $s.Columns.Item($j + 1).ColumnWidth = $widths[$j] }
    $row = 1
    $cases = [System.Collections.Generic.List[object[]]]::new()
    foreach ($font in $fonts) {
        $f = $font.Split(',')
        for ($t = 0; $t -lt $texts.Count; $t++) {
            for ($j = 0; $j -lt $widths.Count; $j++) {
                $c = $s.Cells.Item($row, $j + 1)
                $c.NumberFormat = '@'
                $c.WrapText = $true
                $c.Font.Name = $f[0]; $c.Font.Size = [double]$f[1]; $c.Font.Bold = [bool][int]$f[2]
                $c.Value2 = $texts[$t]
                $cases.Add(@($row, $j, $font, $t))
                $row++
            }
        }
    }
    [void]$s.Range($s.Cells.Item(1, 1), $s.Cells.Item($row - 1, 1)).EntireRow.AutoFit()
    $px = for ($j = 0; $j -lt $widths.Count; $j++) { [math]::Round($s.Columns.Item($j + 1).Width / 0.75) }
    foreach ($case in $cases) {
        $h = [math]::Round($s.Rows.Item($case[0]).RowHeight / 0.75)
        $lines.Add("$($case[2])`t$($px[$case[1]])`t$h`t$($texts[$case[3]])")
    }
    $book.Close($false)
    [System.IO.File]::WriteAllLines($Output, $lines)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
