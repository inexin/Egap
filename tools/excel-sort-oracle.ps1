<#
.SYNOPSIS
Sorts ranges in Excel and writes each sheet before and after, for SortTests.

.DESCRIPTION
Each case fills a fresh book, sorts a range with Range.Sort and records the cells before and after. Each output line is the
case name, its options (range; keys as column:a or column:d; header; match case; rows or columns; rows hidden), the cells
before and the cells after, tab separated. Cells are address=kind:content joined by |, where the kind is n (number), s (text),
b (logical) or f (formula); notes are !address=text, links @address=url and filled cells #address. A sort Excel refuses
records ERROR and its message. The text order depends on Windows' locale, which the first line records.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-sort-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/sort.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build) locale $([System.Globalization.CultureInfo]::CurrentCulture.Name)")
$ae = [string][char]0xC6
$aa = [string][char]0xE5
$oe = [string][char]0xF8
$ee = [string][char]0xE9
$mixed = @('n:3', 's:b', 's:B', 'b:TRUE', 'f:NA()', '', 's:a-c', 's:ab', 's:10', 'n:2.5', 'b:FALSE', 's:ac', 'f:1/0', 's:Ab', 's:a c', 's:ab', 'n:-1', 's:zz', "s:$($ae)b", "s:$($aa)a")
$texts = @('a', 'A', 'b', 'B', 'a-c', 'ac', 'a c', "a'c", 'a_c', 'a.c', '1', '10', '2', ' a', 'ab', 'aB', 'Ab', 'AB', "$($ae)b", "$($aa)a", "$($oe)a", 'zz', 'aa', 'ae', 'a', '-a', 'co-op', 'coop', 'co op', 'x', 'e', $ee, 'f', '')

function Put($s, $r, $c, $v) {
    if ($v -eq '') { return }
    $kind = $v.Substring(0, 2)
    $body = $v.Substring(2)
    switch ($kind) {
        'n:' { $s.Cells.Item($r, $c).Value2 = [double]$body }
        's:' { $s.Cells.Item($r, $c).NumberFormat = '@'; $s.Cells.Item($r, $c).Value2 = $body }
        'b:' { $s.Cells.Item($r, $c).Formula = $body }
        'f:' { $s.Cells.Item($r, $c).Formula = '=' + $body }
    }
}

function Dump($s, $area) {
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($cell in $s.Range($area).Cells) {
        $a = $cell.Address($false, $false)
        if ($cell.HasFormula) { $out.Add("$a=f:$($cell.Formula.Substring(1))") }
        elseif ($null -ne $cell.Value2) {
            $v = $cell.Value2
            if ($v -is [double]) { $out.Add("$a=n:$($v.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture))") }
            elseif ($v -is [bool]) { $out.Add("$a=b:$(if ($v) { 'TRUE' } else { 'FALSE' })") }
            else { $out.Add("$a=s:$v") }
        }
        if ($cell.Comment) { $out.Add("!$a=$($cell.Comment.Text())") }
        if ($cell.Hyperlinks.Count -gt 0) { $out.Add("@$a=$($cell.Hyperlinks.Item(1).Address)") }
        if ($cell.Interior.ColorIndex -ne -4142) { $out.Add("#$a") }
    }
    return [string]::Join('|', $out)
}

function FillMixed($s) {
    for ($i = 0; $i -lt $mixed.Count; $i++) {
        $r = $i + 1
        Put $s $r 1 $mixed[$i]
        $s.Cells.Item($r, 2).Formula = "=A$r&""~""&ROW()"
        $s.Cells.Item($r, 3).Formula = "=A`$1&A$([Math]::Max(1, $r - 1))&C`$30"
        $s.Cells.Item($r, 4).Value2 = [double]$r
    }
    $s.Cells.Item(30, 3).Value2 = 'x'
    $s.Cells.Item(31, 1).Formula = '=D3&D5'
    $s.Cells.Item(32, 1).Formula = '=SUM(D1:D4)'
    [void]$s.Cells.Item(3, 4).AddComment('note3')
    [void]$s.Hyperlinks.Add($s.Cells.Item(5, 4), 'http://example.com/5')
    $s.Cells.Item(7, 4).Interior.ColorIndex = 6
}

function FillTexts($s) {
    for ($i = 0; $i -lt $texts.Count; $i++) {
        if ($texts[$i] -ne '') { Put $s ($i + 1) 1 "s:$($texts[$i])" }
        $s.Cells.Item($i + 1, 2).Value2 = [double]($i + 1)
    }
}

function FillGrid($s) {
    for ($r = 1; $r -le 6; $r++) { $s.Cells.Item($r, 1).Value2 = [double](7 - $r); Put $s $r 2 "s:r$r"; $s.Cells.Item($r, 3).Formula = "=A$r*10"; Put $s $r 4 "s:d$r" }
    for ($c = 1; $c -le 6; $c++) { $s.Cells.Item(8, $c).Value2 = [double]((($c * 5) % 7) + 1); $s.Cells.Item(9, $c).Formula = '=' + $s.Cells.Item(8, $c).Address($false, $false) + '*2+A$8' }
    $s.Cells.Item(1, 1).Formula = '=ROW()'
    $s.Cells.Item(2, 1).Formula = '=A3+1'
}

function FillEdge($s) {
    for ($r = 1; $r -le 8; $r++) { $s.Cells.Item($r, 1).Value2 = [double](9 - $r) }
    $f = @{
        'B1' = '=A1048570'; 'B2' = '=SUM(A1:A2)'; 'B3' = '=Sheet2!A1'; 'B4' = '=SUM(A$1:A3)'; 'B5' = '=A1'
        'B6' = '=A4'; 'B7' = '=SUM(A1:A6)'; 'B8' = '=SUM(A6:A7)'; 'C8' = '=A7'; 'C7' = '=Sheet1!A1'; 'C6' = '=SUM(Sheet1!A2:A5)'
        'C1' = '=SUM(A1:A1048575)'; 'C2' = '=SUM(A2:A1048576)'; 'C5' = '=SUM(Sheet2!A1:A3)'; 'D8' = '=SUM(A:A)'; 'D7' = '=SUM(2:3)'
        'D6' = '=SUM(1:4)'; 'D1' = '=SUM(1047000:1048575)'
    }
    foreach ($k in $f.Keys) { $s.Range($k).Formula = $f[$k] }
}

# name, fill, area to record, range, keys, header, match case, orientation, hidden rows
$cases = @(
    @('mixed-asc', 'mixed', 'A1:D32', 'A1:D20', '0:a', $false, $false, 'rows', ''),
    @('mixed-desc', 'mixed', 'A1:D32', 'A1:D20', '0:d', $false, $false, 'rows', ''),
    @('mixed-case', 'mixed', 'A1:D32', 'A1:D20', '0:a', $false, $true, 'rows', ''),
    @('mixed-hidden', 'mixed', 'A1:D32', 'A1:D20', '0:a', $false, $false, 'rows', '4 11'),
    @('mixed-two-keys', 'mixed', 'A1:D32', 'A1:D20', '3:d 0:a', $false, $false, 'rows', ''),
    @('mixed-header', 'mixed', 'A1:D32', 'A1:D20', '0:a', $true, $false, 'rows', ''),
    @('mixed-part', 'mixed', 'A1:D32', 'A5:C14', '0:d', $false, $false, 'rows', ''),
    @('text-asc', 'texts', 'A1:B34', 'A1:B34', '0:a', $false, $false, 'rows', ''),
    @('text-desc', 'texts', 'A1:B34', 'A1:B34', '0:d', $false, $false, 'rows', ''),
    @('text-case', 'texts', 'A1:B34', 'A1:B34', '0:a', $false, $true, 'rows', ''),
    @('text-case-desc', 'texts', 'A1:B34', 'A1:B34', '0:d', $false, $true, 'rows', ''),
    @('grid-rows', 'grid', 'A1:F9', 'A1:D6', '0:a', $false, $false, 'rows', ''),
    @('grid-columns', 'grid', 'A1:F9', 'A8:F9', '0:a', $false, $false, 'columns', ''),
    @('grid-columns-desc', 'grid', 'A1:F9', 'A8:F9', '0:d', $false, $false, 'columns', ''),
    @('grid-merge', 'grid', 'A1:F9', 'A1:D6', '0:a', $false, $false, 'rows', 'merge'),
    @('grid-array', 'grid', 'A1:F9', 'A1:D6', '0:a', $false, $false, 'rows', 'array'),
    @('edge-asc', 'edge', 'A1:D8', 'A1:D8', '0:a', $false, $false, 'rows', '')
)
try {
    foreach ($case in $cases) {
        $book = $excel.Workbooks.Add()
        $s = $book.Worksheets.Item(1)
        if ($book.Worksheets.Count -lt 2) { [void]$book.Worksheets.Add([Type]::Missing, $s) }
        $s.Name = 'Sheet1'; $book.Worksheets.Item(2).Name = 'Sheet2'
        switch ($case[1]) { 'mixed' { FillMixed $s } 'texts' { FillTexts $s } 'grid' { FillGrid $s } 'edge' { FillEdge $s } }
        $extra = $case[8]
        if ($extra -eq 'merge') { $s.Range('C2:D3').Merge() }
        elseif ($extra -eq 'array') { $s.Range('E2:E3').FormulaArray = '=A2:A3*3'; $s.Range('C2:C3').FormulaArray = '=B2:B3' }
        elseif ($extra -ne '') { foreach ($r in $extra.Split(' ')) { $s.Rows.Item([int]$r).Hidden = $true } }
        $before = Dump $s $case[2]
        $range = $s.Range($case[3])
        $columns = $case[7] -eq 'columns'
        $keys = [System.Collections.Generic.List[object]]::new()
        foreach ($k in $case[4].Split(' ')) {
            $p = $k.Split(':')
            $cell = if ($columns) { $range.Cells.Item([int]$p[0] + 1, 1) } else { $range.Cells.Item(1, [int]$p[0] + 1) }
            $keys.Add(@($cell, $(if ($p[1] -eq 'd') { 2 } else { 1 })))
        }
        $key2 = if ($keys.Count -gt 1) { $keys[1][0] } else { [Type]::Missing }
        $order2 = if ($keys.Count -gt 1) { $keys[1][1] } else { [Type]::Missing }
        $options = "$($case[3]);$($case[4]);$($case[5]);$($case[6]);$($case[7]);$extra"
        try {
            [void]$range.Sort($keys[0][0], $keys[0][1], $key2, [Type]::Missing, $order2, [Type]::Missing, [Type]::Missing,
                $(if ($case[5]) { 1 } else { 2 }), [Type]::Missing, $case[6], $(if ($columns) { 2 } else { 1 }))
            $lines.Add("$($case[0])`t$options`t$before`t$(Dump $s $case[2])")
        }
        catch { $lines.Add("$($case[0])`t$options`t$before`tERROR $($_.Exception.Message)") }
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n", [System.Text.UTF8Encoding]::new($false))
