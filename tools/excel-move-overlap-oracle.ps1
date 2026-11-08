<#
.SYNOPSIS
Moves bands of cells a short way along their own sheet in Excel and writes what each probe formula became, for
RangeCopyTests.

.DESCRIPTION
For each move, a fresh book gets numbers in A1:K40 (row times 100 plus column) and the probe formulas from N20 down; the
band is cut and pasted, onto itself in part, next to itself or clear of it, and the formulas are read back. Each output
line is the move (range>cell) and then probe=formula for every probe, tab separated.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-move-overlap-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/move-overlap.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$formulas = @('SUM(B5:B7)', 'SUM(B6:B9)', 'SUM(B4:B9)', 'SUM(B1:B5)', 'SUM(B3:B5)', 'SUM(B1:B3)', 'SUM(B5:B6)', 'B5', 'B6', 'SUM(B7:B9)',
    'SUM(B2:B4)', 'SUM(B3:B8)', 'SUM(B1:B20)', 'SUM(B5:C7)', 'SUM(A5:K7)', 'SUM(B8:B12)', 'SUM(B9:B12)', 'SUM(B4:B5)', 'SUM(B3:B4)', 'SUM(B6:B7)', 'SUM(B2:B6)', 'SUM(B6:B12)')
$moves = @(@('A2:J4', 'A4'), @('A2:J4', 'A6'), @('A2:J4', 'A10'), @('A2:A4', 'A4'), @('A6:J8', 'A4'), @('A5', 'A6'), @('A2:J4', 'A3'), @('A6:J8', 'A7'), @('B2:B4', 'B4'))
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build)")
try {
    foreach ($move in $moves) {
        $book = $excel.Workbooks.Add()
        $s = $book.Worksheets.Item(1)
        for ($r = 1; $r -le 40; $r++) { for ($c = 1; $c -le 11; $c++) { $s.Cells.Item($r, $c).Value2 = [double]($r * 100 + $c) } }
        for ($i = 0; $i -lt $formulas.Count; $i++) { $s.Cells.Item(20 + $i, 14).Formula = '=' + $formulas[$i] }
        [void]$s.Range($move[0]).Cut($s.Range($move[1]))
        $row = [System.Collections.Generic.List[string]]::new()
        $row.Add("$($move[0])>$($move[1])")
        for ($i = 0; $i -lt $formulas.Count; $i++) { $row.Add($formulas[$i] + '=' + $s.Cells.Item(20 + $i, 14).Formula.Substring(1)) }
        $lines.Add([string]::Join("`t", $row))
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n")
