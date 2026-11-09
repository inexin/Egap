<#
.SYNOPSIS
Has Excel AutoFit columns and rows of a workbook it fills, for AutoFitTests.

.DESCRIPTION
Builds a workbook of cases: on sheet "cols" each column holds a case for Columns.AutoFit, on sheet "rows" each row holds
a case for Rows.AutoFit, once with Calibri 11 as the normal font and once (sheets "cols-arial", "rows-arial") with Arial 10
in a second book. Saves the books before fitting as <Folder>/autofit.xlsx and autofit-arial.xlsx, fits every case, saves
the fitted copies beside them and writes autofit.excel.tsv: one line per case of the sheet, the column or row, its width
as the file stores it (characters with padding) or its height in points, and Excel's ColumnWidth or RowHeight.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-autofit-oracle.ps1 -Folder tests/Egap.Spreadsheets.Tests/Oracle
#>
param([Parameter(Mandatory = $true)][string]$Folder)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folderPath = (Resolve-Path $Folder).Path
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$lf = [string][char]10

# Each column case: cells from row 1 down, as kind:text, with optional ";font=Name;size=n;bold;italic;wrap;format=...;style=...;indent=n;rotate=n".
# Excel's object model refuses format codes with a decimal point under some locales, so the cases keep to codes without one.
$columnCases = [ordered]@{
    'A' = @('s:a')
    'B' = @('s:Hello')
    'C' = @('s:Hello world, this is a longer text')
    'D' = @('s:WWWWWWWWWW')
    'E' = @('s:iiiiiiiiii')
    'F' = @('n:1234567890')
    'G' = @('n:1234567.891')
    'H' = @('n:0.1', 'n:12.5', 'n:-3')
    'I' = @('n:123456789012345')
    'J' = @('n:43831;format=yyyy-mm-dd')
    'K' = @('n:1234567.5;format=# ##0')
    'L' = @('n:0.256;style=Percent')
    'M' = @('s:Bold text here;bold')
    'N' = @('s:Big text;size=14')
    'O' = @('s:Small text here;size=8')
    'P' = @('s:Arial text here;font=Arial;size=10')
    'Q' = @('s:Times text here;font=Times New Roman;size=12')
    'R' = @('s:short', 's:a much longer line of text', 's:mid length')
    'S' = @('s:wrapped text that is long;wrap')
    'T' = @("s:first${lf}a second longer line")
    'U' = @('b:TRUE', 'f:NA()')
    'V' = @('f:REPT("x",20)')
    'W' = @('s:   leading spaces')
    'X' = @('s:indented;indent=2')
    'Y' = @('s:Huge;size=36')
    'Z' = @('s:Italic text here;italic')
    'AA' = @('s:rotated text;rotate=90')
    'AB' = @('s:Mixed case MiXeD')
    'AC' = @('n:-1234.5678')
    'AD' = @("s:$([char]0xC6)r$([char]0xF8)sk$([char]0xF8)bing $([char]0xE5)$([char]0xE5)")
    'AE' = @('n:12345;format=@')
    'AF' = @()
}

# Each row case: cells from column A on, in a column of width 10 (characters).
$rowCases = [ordered]@{
    '1' = @('s:plain')
    '2' = @('s:eight;size=8')
    '3' = @('s:fourteen;size=14')
    '4' = @('s:twenty;size=20')
    '5' = @('s:big;size=36')
    '6' = @('s:arial ten;font=Arial;size=10')
    '7' = @('s:arial big;font=Arial;size=24')
    '8' = @('s:bold;bold')
    '9' = @('s:small;size=8', 's:large;size=18')
    '10' = @('s:a long text that wraps over a few lines;wrap')
    '11' = @("s:one${lf}two${lf}three;wrap")
    '12' = @("s:one${lf}two${lf}three")
    '13' = @('s:sideways text;rotate=90')
    '14' = @()
    '15' = @('s:wraps at size 14 over lines;wrap;size=14')
    '16' = @('n:123456789;wrap')
    '17' = @('s:times;font=Times New Roman;size=12')
    '18' = @('s:x;size=11', 's:wrapping words in cell b;wrap')
    '19' = @('s:tilted;rotate=45')
    '20' = @('s:cambria;font=Cambria;size=11')
}

function Put($cell, [string]$spec) {
    $parts = $spec.Split(';')
    $kind = $parts[0].Substring(0, 2)
    $body = $parts[0].Substring(2)
    foreach ($p in $parts | Select-Object -Skip 1) {
        $kv = $p.Split('=', 2)
        switch ($kv[0]) {
            'font' { $cell.Font.Name = $kv[1] }
            'size' { $cell.Font.Size = [double]$kv[1] }
            'bold' { $cell.Font.Bold = $true }
            'italic' { $cell.Font.Italic = $true }
            'wrap' { $cell.WrapText = $true }
            'format' { $cell.NumberFormat = $kv[1] }
            'style' { $cell.Style = $kv[1] }
            'indent' { $cell.IndentLevel = [int]$kv[1] }
            'rotate' { $cell.Orientation = [int]$kv[1] }
        }
    }
    switch ($kind) {
        's:' { $cell.NumberFormat = $(if ($cell.NumberFormat -eq 'General') { '@' } else { $cell.NumberFormat }); $cell.Value2 = $body }
        'n:' { $cell.Value2 = [double]$body }
        'b:' { $cell.Formula = '=' + $body }
        'f:' { $cell.Formula = '=' + $body }
    }
}

function StoredWidths([string]$path, [string]$sheetFile) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = [System.IO.StreamReader]::new($zip.GetEntry($sheetFile).Open())
        [xml]$xml = $reader.ReadToEnd()
        $reader.Close()
    }
    finally { $zip.Dispose() }
    $widths = @{}
    foreach ($col in $xml.worksheet.cols.col) { for ($c = [int]$col.min; $c -le [int]$col.max; $c++) { $widths[$c] = $col.width } }
    $heights = @{}
    foreach ($row in $xml.worksheet.sheetData.row) { if ($row.ht) { $heights[[int]$row.r] = $row.ht } }
    return @($widths, $heights)
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build), $([System.Globalization.CultureInfo]::CurrentCulture.Name) locale, written by tools/excel-autofit-oracle.ps1")
try {
    foreach ($variant in @(@('autofit', 'Calibri', 11, ''), @('autofit-arial', 'Arial', 10, '-arial'))) {
        $book = $excel.Workbooks.Add()
        $book.Styles.Item('Normal').Font.Name = $variant[1]
        $book.Styles.Item('Normal').Font.Size = $variant[2]
        while ($book.Worksheets.Count -lt 2) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
        $cols = $book.Worksheets.Item(1); $cols.Name = 'cols' + $variant[3]
        $rows = $book.Worksheets.Item(2); $rows.Name = 'rows' + $variant[3]
        foreach ($name in $columnCases.Keys) {
            $r = 1
            foreach ($spec in $columnCases[$name]) { Put $cols.Range("$name$r") $spec; $r++ }
        }
        $cols.Range('AG1').Value2 = 'merged across'
        [void]$cols.Range('AG1:AH1').Merge()
        $rows.Columns.Item(1).ColumnWidth = 10
        $rows.Columns.Item(2).ColumnWidth = 10
        foreach ($r in $rowCases.Keys) {
            $c = 1
            foreach ($spec in $rowCases[$r]) { Put $rows.Cells.Item([int]$r, $c) $spec; $c++ }
        }
        $rows.Rows.Item(14).RowHeight = 30
        $before = Join-Path $folderPath "$($variant[0]).xlsx"
        $book.SaveAs($before, 51)

        foreach ($name in $columnCases.Keys) { [void]$cols.Columns.Item($name).AutoFit() }
        [void]$cols.Columns.Item('AG').AutoFit()
        foreach ($r in $rowCases.Keys) { [void]$rows.Rows.Item([int]$r).AutoFit() }
        $after = Join-Path $env:TEMP "$($variant[0])-fitted-$([guid]::NewGuid().ToString('N')).xlsx"
        $book.SaveCopyAs($after)
        $stored = StoredWidths $after 'xl/worksheets/sheet1.xml'
        $storedRows = StoredWidths $after 'xl/worksheets/sheet2.xml'
        foreach ($name in @($columnCases.Keys) + @('AG')) {
            $index = $cols.Columns.Item($name).Column
            $file = if ($stored[0].ContainsKey($index)) { $stored[0][$index] } else { '' }
            $lines.Add("$($cols.Name)`t$name`t$file`t$($cols.Columns.Item($name).ColumnWidth.ToString($inv))")
        }
        foreach ($r in $rowCases.Keys) {
            $file = if ($storedRows[1].ContainsKey([int]$r)) { $storedRows[1][[int]$r] } else { '' }
            $lines.Add("$($rows.Name)`t$r`t$file`t$($rows.Rows.Item([int]$r).RowHeight.ToString($inv))")
        }
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText((Join-Path $folderPath 'autofit.excel.tsv'), [string]::Join("`n", $lines) + "`n", [System.Text.UTF8Encoding]::new($false))
