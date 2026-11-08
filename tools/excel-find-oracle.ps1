<#
.SYNOPSIS
Finds and replaces text in Excel and records the cells found and the cells after each replace, for FindTests.

.DESCRIPTION
Each case fills a fresh sheet with text, numbers, formulas, a date, logicals, errors and a note, then runs Range.Find and
FindNext over the sheet, or Range.Replace. A find line is the search (text, look in: -4123 formulas, -4163 values, -4144
notes; look at: 1 whole, 2 part; order: 1 by rows, 2 by columns; match case) and the cells found in order; a replace line is
the replace (text, replacement, look at, match case), what Replace returned, and each cell's Formula afterwards. Number
text follows Windows' locale, which the first line records.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-find-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/find.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build) locale $([System.Globalization.CultureInfo]::CurrentCulture.Name)")
function Fill($s) {
    $s.Range('A1').Value2 = 'Apple pie'
    $s.Range('A2').Value2 = 'apple'
    $s.Range('A3').Value2 = [double]1234.5
    $s.Range('A4').Formula = '=SUM(B1:B2)'
    $s.Range('A5').Formula = '=TRUE'
    $s.Range('A6').Formula = '=NA()'
    $s.Range('A7').Value2 = 'a*b'
    $s.Range('A8').Value2 = 'a~b'
    $s.Range('A9').Value2 = 'xapplex'
    $s.Range('B1').Value2 = [double]10
    $s.Range('B2').Value2 = [double]20
    $s.Range('B3').Formula = '=A2&"x"'
    $s.Range('B4').Value2 = [double]43831
    $s.Range('B4').NumberFormat = 'yyyy-mm-dd'
    $s.Range('B5').Formula = '=1/3'
    $s.Range('B6').Value2 = [double]0.1
    $s.Range('B7').Formula = '="apple"'
    $s.Range('B8').Value2 = 'TRUE'
    $s.Range('C2').Value2 = 'apple'
    [void]$s.Range('C1').AddComment('apple note')
}
# what, lookin (-4123 formulas, -4163 values, -4144 notes), lookat (1 whole, 2 part), order (1 rows, 2 cols), matchcase
$finds = @(
    @('apple', -4123, 2, 1, $false), @('apple', -4163, 2, 1, $false), @('apple', -4123, 1, 1, $false), @('Apple', -4123, 2, 1, $true), @('apple', -4123, 2, 2, $false),
    @('SUM', -4123, 2, 1, $false), @('SUM', -4163, 2, 1, $false), @('30', -4163, 2, 1, $false), @('30', -4123, 2, 1, $false), @('1234', -4123, 2, 1, $false),
    @('1234,5', -4163, 1, 1, $false), @('1234.5', -4123, 1, 1, $false), @('a~*b', -4123, 1, 1, $false), @('a*b', -4123, 1, 1, $false), @('a~~b', -4123, 1, 1, $false),
    @('?pple', -4123, 1, 1, $false), @('TRUE', -4123, 1, 1, $false), @('TRUE', -4163, 1, 1, $false), @('#N/A', -4163, 1, 1, $false), @('NA', -4123, 2, 1, $false),
    @('B1', -4123, 2, 1, $false), @('2020', -4123, 2, 1, $false), @('note', -4144, 2, 1, $false), @('0,333', -4163, 2, 1, $false),
    @('0.1', -4123, 1, 1, $false), @('*', -4123, 1, 1, $false), @('p*e', -4123, 2, 1, $false), @('x', -4123, 2, 1, $false), @('=', -4123, 2, 1, $false), @('"', -4123, 2, 1, $false)
)
try {
    foreach ($f in $finds) {
        $book = $excel.Workbooks.Add()
        $s = $book.Worksheets.Item(1)
        Fill $s
        $found = [System.Collections.Generic.List[string]]::new()
        $first = $s.Cells.Find($f[0], $s.Cells.Item(1048576, 16384), $f[1], $f[2], $f[3], 1, $f[4])
        if ($first) {
            $cell = $first
            do {
                $found.Add($cell.Address($false, $false))
                $cell = $s.Cells.FindNext($cell)
            } while ($cell -and $cell.Address() -ne $first.Address() -and $found.Count -lt 50)
        }
        $lines.Add("find $($f -join '|')`t$([string]::Join(',', $found))")
        $book.Close($false)
    }
    # what, replacement, lookat, matchcase
    $replaces = @(@('apple', 'pear', 2, $false), @('apple', 'pear', 1, $false), @('Apple', 'Pear', 2, $true), @('SUM', 'MAX', 2, $false), @('1', '9', 2, $false), @('B1', 'B9', 2, $false),
        @('pie', '', 2, $false), @('apple', '5', 1, $false), @('B1:B2', 'B1:(', 2, $false), @('a*', 'z', 2, $false), @('p?e', 'Q', 2, $false), @('TRUE', 'FALSE', 1, $false), @('x', '~', 2, $false),
        @('1234,5', '7', 1, $false), @('apple', '=1+1', 1, $false), @('2020', '1999', 2, $false), @('a', 'A', 2, $false))
    foreach ($r in $replaces) {
        $book = $excel.Workbooks.Add()
        $s = $book.Worksheets.Item(1)
        Fill $s
        $result = ''
        try { $result = $s.Cells.Replace($r[0], $r[1], $r[2], 1, $r[3]) } catch { $result = "ERR $($_.Exception.Message)" }
        $out = [System.Collections.Generic.List[string]]::new()
        foreach ($a in @('A1', 'A2', 'A3', 'A4', 'A5', 'A6', 'A7', 'A8', 'A9', 'B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7', 'B8', 'C2')) { $out.Add("$a=$($s.Range($a).Formula)") }
        $out.Add("C1note=$($s.Range('C1').Comment.Text())")
        $lines.Add("replace $($r -join '|')`t$result`t$([string]::Join(' ; ', $out))")
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n", [System.Text.UTF8Encoding]::new($false))
