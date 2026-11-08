<#
.SYNOPSIS
Copies and cuts ranges around merged cells in Excel and writes whether Excel made each paste and the merges left, for
RangeCopyTests.

.DESCRIPTION
For each case, a fresh book gets numbers in A1:L30 and the case's merges; the range is copied or cut and pasted at the
cell. Each output line is the merges, Copy or Cut, range>cell, ok or refused, and the merges after, tab separated.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-merge-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/merge.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
# merge(s) ; kind ; source ; destination
$cases = @(
    @('A5:A10', 'Cut', 'A2:H4', 'A4'),
    @('A5:A10', 'Cut', 'A12:H14', 'A4'),
    @('A5:A10', 'Copy', 'A12:H14', 'A4'),
    @('A5:A10', 'Copy', 'A2:H4', 'A4'),
    @('A3:A4', 'Cut', 'A2:H4', 'A4'),
    @('A3:A4', 'Copy', 'A2:H4', 'A4'),
    @('A3:A4 B3:B4', 'Copy', 'A1:C5', 'B3'),
    @('A3:A4 B3:B4', 'Cut', 'A1:C5', 'B3'),
    @('A4:A8', 'Copy', 'A2:E6', 'A20'),
    @('A4:A8', 'Cut', 'A2:E6', 'A20'),
    @('A4:A8', 'Cut', 'A2:E6', 'K2'),
    @('A1:C1', 'Cut', 'A1:B5', 'E1'),
    @('A1:C1', 'Copy', 'A1:B5', 'E1'),
    @('A5:A10', 'Cut', 'A2:H4', 'A6'),
    @('A5:A10', 'Cut', 'A6:H8', 'A5'),
    @('A5:A10', 'Cut', 'A6:H8', 'A3'),
    @('A5:A10', 'Copy', 'A6:H8', 'A3'),
    @('A5:A10', 'Cut', 'A2:H4', 'A3'),
    @('A5:A10', 'Copy', 'A2:H4', 'A3'),
    @('A5:B6', 'Cut', 'A2:H4', 'A4'),
    @('A5:B6', 'Copy', 'A2:H4', 'A4'),
    @('A5:B6', 'Cut', 'A2:H5', 'A4'),
    @('A5:B6', 'Copy', 'A2:H5', 'A4'),
    @('A5:B6', 'Cut', 'A6:H8', 'A4'),
    @('A5:B6', 'Cut', 'A5:H8', 'A3'),
    @('A5:A10', 'Cut', 'A20:H22', 'A9'),
    @('A5:A10', 'Copy', 'A20:H22', 'A9'),
    @('A5:A10', 'Cut', 'A2:H6', 'A20'),
    @('A5:A10', 'Cut', 'A5:H6', 'A9')
)
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build)")
try {
    foreach ($case in $cases) {
        $book = $excel.Workbooks.Add()
        $s = $book.Worksheets.Item(1)
        for ($r = 1; $r -le 30; $r++) { for ($c = 1; $c -le 12; $c++) { $s.Cells.Item($r, $c).Value2 = [double]($r * 100 + $c) } }
        foreach ($m in $case[0].Split(' ')) { $s.Range($m).MergeCells = $true }
        $result = 'ok'
        try {
            if ($case[1] -eq 'Cut') { [void]$s.Range($case[2]).Cut($s.Range($case[3])) } else { [void]$s.Range($case[2]).Copy($s.Range($case[3])) }
        }
        catch { $result = 'refused' }
        $merges = [System.Collections.Generic.List[string]]::new()
        for ($r = 1; $r -le 30; $r++) {
            for ($c = 1; $c -le 12; $c++) {
                $cell = $s.Cells.Item($r, $c)
                if ($cell.MergeCells) {
                    $area = $cell.MergeArea
                    if ($area.Row -eq $r -and $area.Column -eq $c) { $merges.Add($area.Address($false, $false)) }
                }
            }
        }
        $lines.Add("$($case[0])`t$($case[1])`t$($case[2])>$($case[3])`t$result`t$([string]::Join(' ', $merges))")
        try { $excel.CutCopyMode = 0 } catch { }
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n")
