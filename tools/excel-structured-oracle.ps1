<#
.SYNOPSIS
Runs the structured reference oracle cases through Excel and writes what Excel calculates and stores, for StructuredReferenceTests.

.DESCRIPTION
Builds the workbook StructuredReferenceTests builds: on Sheet1 table Sales at A1:AR5 with a totals row (columns Name, Qty,
"Un it", "a#b", "x[y]" and calculated columns C1 up), and Plain at A8:B10 with no header row; Sheet2 is empty. Each line of the
cases file is a place and a formula typed as a person types it: "in" for a cell in Sales's data, "row" for a cell beside Sales in
the same row, "out" for a cell in no row of a table, "other" for a cell on Sheet2.

The output has a header with Excel's version and one line per case: place, kind, value, stored formula, 1 if it is stored as a dynamic array formula, typed formula. The
kind is "invalid" for what Excel refuses, else "value", with the value as ARRAYTOTEXT gives it. The stored formula is what
Excel wrote to the file.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-structured-oracle.ps1 <cases.txt> <out.tsv>
#>
param(
    [Parameter(Mandatory = $true)][string]$Cases,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$list = @()
foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path $Cases))) {
    $text = $line.Trim()
    if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
    $space = $text.IndexOf(' ')
    $list += , @($text.Substring(0, $space), $text.Substring($space + 1))
}

function Place($sheet1, $sheet2, $where, $index) {
    switch ($where) {
        'in' { return $sheet1.Cells.Item(3, 6 + $index) }
        'row' { return $sheet1.Cells.Item(3, 60 + $index) }
        'out' { return $sheet1.Cells.Item(20, 60 + $index) }
        'other' { return $sheet2.Cells.Item(3, 1 + $index) }
    }
}

$save = Join-Path ([System.IO.Path]::GetTempPath()) ("structured-oracle-" + [guid]::NewGuid().ToString('N') + ".xlsx")
$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AutoCorrect.AutoFillFormulasInLists = $false
    $book = $excel.Workbooks.Add()
    while ($book.Worksheets.Count -lt 2) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
    $sheet1 = $book.Worksheets.Item(1)
    $sheet1.Name = 'Sheet1'
    $sheet2 = $book.Worksheets.Item(2)
    $sheet2.Name = 'Sheet2'

    $headers = @('Name', 'Qty', 'Un it', 'a#b', 'x[y]')
    for ($c = 0; $c -lt 44; $c++) {
        $sheet1.Cells.Item(1, $c + 1).Value2 = [string]$(if ($c -lt 5) { $headers[$c] } else { 'C' + ($c - 4) })
    }

    $rows = @(@('a', 1, 10, 100, 1000), @('b', 2, 20, 200, 2000), @('c', 3, 30, 300, 3000))
    for ($r = 0; $r -lt 3; $r++) {
        for ($c = 0; $c -lt 5; $c++) { $sheet1.Cells.Item($r + 2, $c + 1).Formula = [string]$rows[$r][$c] }
    }

    $t1 = $sheet1.ListObjects.Add(1, $sheet1.Range('A1:AR4'), $null, 1)
    $t1.Name = 'Sales'
    $t1.ShowTotals = $true
    $t1.ListColumns.Item(1).TotalsCalculation = 0
    $sheet1.Range('A5').Value2 = 'Total'
    $t1.ListColumns.Item(2).TotalsCalculation = 1
    $sheet1.Range('C5').Value2 = 60
    $sheet1.Range('D5').Value2 = 600
    $sheet1.Range('E5').Value2 = 6000

    $sheet1.Range('A8').Value2 = 7
    $sheet1.Range('B8').Value2 = 8
    $sheet1.Range('A9').Value2 = 9
    $sheet1.Range('B9').Value2 = 10
    $t2 = $sheet1.ListObjects.Add(1, $sheet1.Range('A7:B9'), $null, 1)
    $t2.Name = 'Plain'
    $sheet1.Range('A7').Value2 = 'One'
    $sheet1.Range('B7').Value2 = 'Two'
    $t2.ShowHeaders = $false

    # Each case as ARRAYTOTEXT gives it, then left as typed, so that the save holds what Excel stores for it.
    $results = New-Object System.Collections.Generic.List[object]
    $inIndex = 0
    for ($i = 0; $i -lt $list.Count; $i++) {
        $where = $list[$i][0]
        $formula = '=' + $list[$i][1]
        $cell = Place $sheet1 $sheet2 $where $(if ($where -eq 'in') { $inIndex } else { $i })
        if ($where -eq 'in') { $inIndex++ }
        $kind = 'value'; $value = ''
        try {
            $cell.Formula2 = $formula
            $cell.Formula2 = '=ARRAYTOTEXT(' + $list[$i][1] + ')'
            $excel.Calculate()
            $text = $cell.Text
            if ($text -match '^#+$') { $cell.EntireColumn.ColumnWidth = 100; $text = $cell.Text }
            $value = $text
            $cell.Formula2 = $formula
        }
        catch { $kind = 'invalid'; $cell.ClearContents() | Out-Null }

        $address = $cell.Worksheet.Name + '!' + $cell.Address($false, $false)
        $results.Add(@($where, $kind, $value, $address, $list[$i][1]))
    }

    $book.SaveAs($save, 51)
    $copy = $save + ".copy"; [System.IO.File]::Copy($save, $copy, $true); $zip = [System.IO.Compression.ZipFile]::OpenRead($copy)
    $parts = @{}
    # The sheets' parts, in the order of the sheets: Sheet1 was there first.
    $names = @('Sheet1', 'Sheet2')
    $found = @($zip.Entries | Where-Object { $_.FullName -match '^xl/worksheets/sheet\d+\.xml$' } | Sort-Object FullName)
    for ($p = 0; $p -lt $found.Count; $p++) {
        $reader = New-Object System.IO.StreamReader($found[$p].Open())
        $parts[$names[$p]] = $reader.ReadToEnd()
        $reader.Close()
    }

    $zip.Dispose()

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Excel $($excel.Version) build $($excel.Build), written by tools/excel-structured-oracle.ps1")
    foreach ($r in $results) {
        $form = ''
        $parts2 = $r[3].Split('!')
        $match = [regex]::Match([string]$parts[$parts2[0]], '<c r="' + $parts2[1] + '"([^>]*)><f[^>]*>(.*?)</f>')
        $dynamic = 0
        if ($match.Success) {
            $form = [System.Net.WebUtility]::HtmlDecode($match.Groups[2].Value)
            if ($match.Groups[1].Value.Contains('cm="1"')) { $dynamic = 1 }
        }

        $lines.Add("$($r[0])`t$($r[1])`t$($r[2])`t$form`t$dynamic`t$($r[4])")
    }

    [System.IO.File]::WriteAllLines($Output, $lines, (New-Object System.Text.UTF8Encoding($false)))
    $book.Close($false)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
    Remove-Item $save, ($save + ".copy") -ErrorAction SilentlyContinue
}
