<#
.SYNOPSIS
Copies and cuts ranges around hyperlinks in Excel and writes the links left, for RangeCopyTests.

.DESCRIPTION
For each case, a fresh book gets numbers in A1:J10 and the case's links; the range is copied or cut and pasted at the
cell. Each output line is the links (cell=address), Copy or Cut, range>cell and the links after, tab separated.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-link-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/link.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
# links (cell=url) ; kind ; source ; destination
$cases = @(
    @('E5=http://e5/', 'Copy', 'A1:C3', 'D4'),
    @('E5=http://e5/', 'Cut', 'A1:C3', 'D4'),
    @('E5=http://e5/ B2=http://b2/', 'Copy', 'A1:C3', 'D4'),
    @('E5=http://e5/ B2=http://b2/', 'Cut', 'A1:C3', 'D4'),
    @('B2=http://b2/ C3=http://c3/', 'Copy', 'A1:C3', 'B3'),
    @('G2=http://g2/', 'Copy', 'A1:C3', 'F1'),
    @('G2=http://g2/', 'Copy', 'H1:J3', 'F1'),
    @('G2=http://g2/', 'Cut', 'H1:J3', 'F1'),
    @('B4=http://b4/', 'Copy', 'A1:C5', 'B3'),
    @('C3=http://c3/', 'Copy', 'A1:C5', 'B3'),
    @('B4=http://b4/ D6=http://d6/', 'Copy', 'A1:C5', 'B3'),
    @('A1=http://a1/ D6=http://d6/', 'Copy', 'A1:C5', 'B3'),
    @('D6=http://d6/', 'Copy', 'A1:C5', 'B3'),
    @('C5=http://c5/', 'Copy', 'A1:C5', 'B3'),
    @('B4=http://b4/ B4:B5=http://b45/', 'Copy', 'A1:C5', 'B3'),
    @('A3:F3=http://a3/', 'Copy', 'A2:C4', 'A20'),
    @('B4:B5=http://b45/', 'Cut', 'A2:H4', 'A4'),
    @('B4:B5=http://b45/', 'Cut', 'A2:H4', 'A10'),
    @('A1=http://a1/ B7=http://b7/', 'Copy', 'A1:C5', 'B3')
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
        for ($r = 1; $r -le 10; $r++) { for ($c = 1; $c -le 10; $c++) { $s.Cells.Item($r, $c).Value2 = [double]($r * 100 + $c) } }
        foreach ($l in $case[0].Split(' ')) { $p = $l.Split('='); [void]$s.Hyperlinks.Add($s.Range($p[0]), $p[1]) }
        if ($case[1] -eq 'Cut') { [void]$s.Range($case[2]).Cut($s.Range($case[3])) } else { [void]$s.Range($case[2]).Copy($s.Range($case[3])) }
        $links = [System.Collections.Generic.List[string]]::new()
        foreach ($h in $s.Hyperlinks) { $links.Add("$($h.Range.Address($false, $false))=$($h.Address)") }
        $lines.Add("$($case[0])`t$($case[1])`t$($case[2])>$($case[3])`t$([string]::Join(' ', $links))")
        try { $excel.CutCopyMode = 0 } catch { }
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n")
