<#
.SYNOPSIS
Runs the insert and delete oracle cases through Excel and writes where each formula went, its formula there and the value it
showed, for RowColumnEditTests.

.DESCRIPTION
For each edit, builds the book RowColumnEditTests builds: sheet S with numbers in A1:E30 (row times 10 plus column) and
sheet Other. Each line of the cases file is a sheet and a formula as a person types it; the S formulas go from H20 down and
the Other formulas from A1 down, all at once, as dynamic array formulas (Formula2). The edit is made on S, and every formula
is read back.

The edits, one-based as Excel counts: insert rows 5:6, delete rows 5:6, insert row 1, delete row 7, insert columns C:D,
delete columns C:D, delete column B. RowColumnEditTests has the same list.

The output has a header with Excel's version and one line per case: sheet, typed formula, then for each edit the cell the
formula is in, its formula and the text the cell shows, as "H22 A7 = 51".

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-edit-oracle.ps1 <cases.txt> <out.tsv>
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

$edits = @(@('InsertRows', 5, 2), @('DeleteRows', 5, 2), @('InsertRows', 1, 1), @('DeleteRows', 7, 1), @('InsertColumns', 3, 2), @('DeleteColumns', 3, 2), @('DeleteColumns', 2, 1))

function Read-Formulas($sheet) {
    $found = [System.Collections.ArrayList]::new()
    foreach ($cell in $sheet.UsedRange.Cells) {
        if ($cell.HasFormula) { [void]$found.Add("$($cell.Address($false, $false)) $($cell.Formula2.Substring(1)) = $($cell.Text)") }
    }
    return , $found
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$answers = @{}
try {
    $version = "Excel $($excel.Version) build $($excel.Build)"
    foreach ($edit in $edits) {
        $book = $excel.Workbooks.Add()
        while ($book.Worksheets.Count -lt 2) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
        $s = $book.Worksheets.Item(1)
        $s.Name = 'S'
        $other = $book.Worksheets.Item(2)
        $other.Name = 'Other'
        for ($r = 1; $r -le 30; $r++) { for ($c = 1; $c -le 5; $c++) { $s.Cells.Item($r, $c).Value2 = [double]($r * 10 + $c) } }
        $onS = 20
        $onOther = 1
        foreach ($case in $list) {
            if ($case[0] -eq 'S') { $s.Cells.Item($onS, 8).Formula2 = '=' + $case[1]; $onS++ }
            else { $other.Cells.Item($onOther, 1).Formula2 = '=' + $case[1]; $onOther++ }
        }

        $first = $edit[1]
        $last = $first + $edit[2] - 1
        switch ($edit[0]) {
            'InsertRows' { [void]$s.Range("$($first):$last").EntireRow.Insert() }
            'DeleteRows' { [void]$s.Range("$($first):$last").EntireRow.Delete() }
            'InsertColumns' { [void]$s.Range($s.Cells.Item(1, $first), $s.Cells.Item(1, $last)).EntireColumn.Insert() }
            'DeleteColumns' { [void]$s.Range($s.Cells.Item(1, $first), $s.Cells.Item(1, $last)).EntireColumn.Delete() }
        }

        $excel.Calculate()
        $answers["$($edit[0]) $first $($edit[2]) S"] = Read-Formulas $s
        $answers["$($edit[0]) $first $($edit[2]) Other"] = Read-Formulas $other
        $book.Close($false)
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# $version")
$index = @{ 'S' = 0; 'Other' = 0 }
foreach ($case in $list) {
    $row = [System.Collections.Generic.List[string]]::new()
    $row.Add($case[0])
    $row.Add($case[1])
    foreach ($edit in $edits) { $row.Add($answers["$($edit[0]) $($edit[1]) $($edit[2]) $($case[0])"][$index[$case[0]]]) }
    $index[$case[0]]++
    $lines.Add([string]::Join("`t", $row))
}

[System.IO.File]::WriteAllLines($Output, $lines, [System.Text.UTF8Encoding]::new($false))
