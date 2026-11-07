<#
.SYNOPSIS
Runs the copy and move oracle cases through Excel and writes where each formula went, its formula there and the value it
showed, for RangeCopyTests.

.DESCRIPTION
Builds the book RowColumnEditTests builds: sheet S with numbers in A1:E30 (row times 10 plus column) and sheet Other. Each
line of the cases file is a sheet and a formula as a person types it; the S formulas go from H20 down and the Other
formulas from A1 down, as dynamic array formulas (Formula2).

Copies, one formula cell at a time, each into a fresh book: to three columns right, to ten rows down, to nineteen rows up
(so a reference above row 20 falls off the sheet) and to the same cell of Other.

Moves, as Cut and paste, each into a fresh book with every formula in place: A5:A6 to C40, A4:A8 to G2, A5 onto A6, H20:H30
(the first formulas) to J50, A5:A6 to Other!B2, A5:A6 to A40, E5 to F5, B5 to B6, A7:A8 to A3 and H20:H30 to Other!J50.

The output has a header with Excel's version and one line per case: sheet, typed formula, then the four copies (S formulas
only) and the ten moves, each as the cell the formula is in, its formula and the text the cell shows: "H22 A7 = 51".

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-copy-oracle.ps1 <cases.txt> <out.tsv>
#>
param(
    [Parameter(Mandatory = $true)][string]$Cases,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$list = @()
foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path $Cases))) {
    $text = $line.Trim()
    if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
    $space = $text.IndexOf(' ')
    $list += , @($text.Substring(0, $space), $text.Substring($space + 1))
}

# Copies: row and column offsets, and whether the copy goes to Other.
$copies = @(@(0, 3, $false), @(10, 0, $false), @(-19, 0, $false), @(0, 0, $true))

# Moves: source range on S, and destination top-left as sheet and cell.
$moves = @(@('A5:A6', 'S', 'C40'), @('A4:A8', 'S', 'G2'), @('A5', 'S', 'A6'), @('H20:H30', 'S', 'J50'), @('A5:A6', 'Other', 'B2'),
    @('A5:A6', 'S', 'A40'), @('E5', 'S', 'F5'), @('B5', 'S', 'B6'), @('A7:A8', 'S', 'A3'), @('H20:H30', 'Other', 'J50'))

function New-Book($excel) {
    $book = $excel.Workbooks.Add()
    while ($book.Worksheets.Count -lt 2) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
    $s = $book.Worksheets.Item(1)
    $s.Name = 'S'
    $book.Worksheets.Item(2).Name = 'Other'
    for ($r = 1; $r -le 30; $r++) { for ($c = 1; $c -le 5; $c++) { $s.Cells.Item($r, $c).Value2 = [double]($r * 10 + $c) } }
    return $book
}

function Answer($cell) {
    if (-not $cell.HasFormula) { return "$($cell.Address($false, $false)) (no formula) = $($cell.Text)" }
    return "$($cell.Address($false, $false)) $($cell.Formula2.Substring(1)) = $($cell.Text)"
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$rows = @{}
try {
    $version = "Excel $($excel.Version) build $($excel.Build)"
    for ($i = 0; $i -lt $list.Count; $i++) { $rows[$i] = [System.Collections.Generic.List[string]]::new() }

    # Copies of each S formula, one book for each copy so no copy sees another.
    foreach ($copy in $copies) {
        $book = New-Book $excel
        $s = $book.Worksheets.Item(1)
        $other = $book.Worksheets.Item(2)
        $at = 20
        for ($i = 0; $i -lt $list.Count; $i++) {
            if ($list[$i][0] -ne 'S') { $rows[$i].Add(''); continue }
            $from = $s.Cells.Item($at, 8)
            $from.Formula2 = '=' + $list[$i][1]
            $to = if ($copy[2]) { $other.Cells.Item($at, 8) } else { $s.Cells.Item($at + $copy[0], 8 + $copy[1]) }
            [void]$from.Copy($to)
            $excel.Calculate()
            $rows[$i].Add((Answer $to))
            $to.Clear()
            $from.Clear()
            $at++
        }
        $book.Close($false)
    }

    foreach ($move in $moves) {
        $book = New-Book $excel
        $s = $book.Worksheets.Item(1)
        $other = $book.Worksheets.Item(2)
        $places = @{}
        $onS = 20
        $onOther = 1
        for ($i = 0; $i -lt $list.Count; $i++) {
            if ($list[$i][0] -eq 'S') { $s.Cells.Item($onS, 8).Formula2 = '=' + $list[$i][1]; $places[$i] = @('S', $onS, 8); $onS++ }
            else { $other.Cells.Item($onOther, 1).Formula2 = '=' + $list[$i][1]; $places[$i] = @('Other', $onOther, 1); $onOther++ }
        }

        $source = $s.Range($move[0])
        $dest = $book.Worksheets.Item($move[1]).Range($move[2])
        # The Range objects follow or lose the cells they name once the cut is made, so where they are is read first.
        $top = $source.Row; $left = $source.Column; $height = $source.Rows.Count; $width = $source.Columns.Count
        $toSheet = $dest.Worksheet; $toRow = $dest.Row; $toColumn = $dest.Column
        [void]$source.Cut($dest)
        $excel.Calculate()
        for ($i = 0; $i -lt $list.Count; $i++) {
            $p = $places[$i]
            $sheet = $book.Worksheets.Item($p[0])
            $row = $p[1]; $column = $p[2]
            if ($p[0] -eq 'S' -and $row -ge $top -and $row -lt $top + $height -and $column -ge $left -and $column -lt $left + $width) {
                $sheet = $toSheet
                $row = $toRow + $row - $top
                $column = $toColumn + $column - $left
            }
            $cell = $sheet.Cells.Item($row, $column)
            $prefix = if ($sheet.Name -ne $p[0]) { "$($sheet.Name)!" } else { '' }
            $rows[$i].Add($prefix + (Answer $cell))
        }
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# $version")
for ($i = 0; $i -lt $list.Count; $i++) {
    $lines.Add([string]::Join("`t", @($list[$i][0], $list[$i][1]) + $rows[$i]))
}

[System.IO.File]::WriteAllLines($Output, $lines, [System.Text.UTF8Encoding]::new($false))
