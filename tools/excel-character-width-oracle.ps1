<#
.SYNOPSIS
Measures the width Excel gives each character in the fonts AutoFit meets most, for tools/make-character-widths.py.

.DESCRIPTION
For each font, size and weight, fills one row of a workbook with runs of 40 copies of each character, has Excel AutoFit the
columns and reads the widths it stores. Excel lays text out in whole pixels per character at 96 dpi and AutoFits a column
to 29/28 of that plus 5 pixels of padding, so the run's width gives the character's pixels. Writes one line per font of
its name, size, bold (0 or 1) and the columns' widths in pixels, in the order of the header line's characters.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-character-width-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/character-widths.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output, [int]$Repeat = 40)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$chars = @(32..126) + @(160..255) + @(0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x20AC, 0x2122)
$specs = [System.Collections.Generic.List[string]]::new()
function Add([string]$font, [double[]]$sizes, [int]$bold) { foreach ($size in $sizes) { $specs.Add("$font,$($size.ToString($inv)),$bold") } }
Add 'Arial' @(7, 8, 9, 10, 11, 12, 14, 16, 18, 20) 0
Add 'Arial' @(8, 9, 10, 11, 12, 14, 16) 1
Add 'Calibri' @(8, 9, 10, 11, 12, 14, 16, 18, 20) 0
Add 'Calibri' @(9, 10, 11, 12, 14, 16) 1
Add 'Times New Roman' @(8, 9, 10, 11, 12, 14) 0
Add 'Times New Roman' @(10, 11, 12, 14) 1
Add 'Verdana' @(8, 9, 10, 11, 12) 0
Add 'Verdana' @(10, 11) 1
Add 'Tahoma' @(8, 9, 10, 11) 0
Add 'Tahoma' @(10) 1
Add 'MS Sans Serif' @(8, 10, 12) 0
Add 'MS Sans Serif' @(10) 1
Add 'Arial Narrow' @(8, 9, 10, 11) 0
Add 'Arial Narrow' @(10) 1
Add 'Courier New' @(9, 10, 11, 12) 0
Add 'Cambria' @(10, 11, 12) 0
Add 'Aptos Narrow' @(10, 11, 12) 0
Add 'Aptos Narrow' @(11) 1
Add 'Segoe UI' @(9, 10) 0
Add 'Garamond' @(10, 11, 12) 0
Add 'Century Gothic' @(10, 11) 0
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Excel $($excel.Version) build $($excel.Build), written by tools/excel-character-width-oracle.ps1; characters $([string]::Join(',', $chars))")
    foreach ($spec in $specs) {
        $p = $spec.Split(',')
        $Font = $p[0]; $Size = [double]$p[1]; $Bold = [int]$p[2]
        $book = $excel.Workbooks.Add()
        $book.Styles.Item('Normal').Font.Name = 'Calibri'
        $book.Styles.Item('Normal').Font.Size = 11
        $s = $book.Worksheets.Item(1)
        $range = $s.Range($s.Cells.Item(1, 1), $s.Cells.Item(1, $chars.Count))
        $range.NumberFormat = '@'
        $range.Font.Name = $Font
        $range.Font.Size = $Size
        $range.Font.Bold = [bool]$Bold
        $values = New-Object 'object[,]' 1, $chars.Count
        for ($i = 0; $i -lt $chars.Count; $i++) { $values[0, $i] = ([string][char]$chars[$i]) * $Repeat }
        $range.Value2 = $values
        [void]$range.Columns.AutoFit()
        $after = Join-Path $env:TEMP "charwidths-$([guid]::NewGuid().ToString('N')).xlsx"
        $book.SaveAs($after, 51)
        $book.Close($false)
        $zip = [System.IO.Compression.ZipFile]::OpenRead($after)
        $reader = [System.IO.StreamReader]::new($zip.GetEntry('xl/worksheets/sheet1.xml').Open())
        [xml]$xml = $reader.ReadToEnd()
        $reader.Close(); $zip.Dispose()
        $widths = @{}
        foreach ($col in $xml.worksheet.cols.col) { for ($c = [int]$col.min; $c -le [int]$col.max; $c++) { $widths[$c] = [double]::Parse($col.width, $inv) } }
        $px = for ($i = 0; $i -lt $chars.Count; $i++) { [math]::Truncate(((256 * $widths[$i + 1]) + [math]::Truncate(128 / 7)) / 256 * 7) }
        $lines.Add("$Font`t$Size`t$Bold`t$([string]::Join(',', $px))")
    }
    [System.IO.File]::WriteAllLines($Output, $lines)
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
