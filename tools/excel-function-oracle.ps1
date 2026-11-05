<#
.SYNOPSIS
Runs the function oracle cases through Excel and writes what Excel calculates, for FunctionOracleTests.

.DESCRIPTION
Reads a cases file (tests/Egap.Spreadsheets.Tests/Oracle/functions.txt): lines "@A1 value" are inputs, every other line
that is not blank or a "#" comment is a formula. Each formula goes into column L, row 20 * i + 1 for the i-th formula
(from 0), as an ordinary formula (Range.Formula, not Formula2): ranges meet the cell by implicit intersection and nothing
spills, as in a cell saved without dynamic arrays and as Worksheet.Evaluate works. The output is a tab-separated file: a
header with Excel's version, build, locale and the decimal and thousands separators it uses, then one line per formula:
index, kind (number, text, boolean, error, blank or invalid), value, formula.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-function-oracle.ps1 <cases.txt> <out.tsv>
#>
param(
    [Parameter(Mandatory = $true)][string]$Cases,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$errors = @{ 2000 = '#NULL!'; 2007 = '#DIV/0!'; 2015 = '#VALUE!'; 2023 = '#REF!'; 2029 = '#NAME?'; 2036 = '#NUM!';
    2042 = '#N/A'; 2043 = '#GETTING_DATA'; 2045 = '#SPILL!'; 2047 = '#BLOCKED!'; 2048 = '#UNKNOWN!'; 2049 = '#FIELD!';
    2050 = '#CALC!' }
$errorFormulas = @{ '#NULL!' = '=A1 B1'; '#DIV/0!' = '=1/0'; '#VALUE!' = '="a"+1'; '#REF!' = '=#REF!'; '#NAME?' = '=#NAME?';
    '#NUM!' = '=SQRT(-1)'; '#N/A' = '=NA()' }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

$inputs = @()
$formulas = @()
foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path $Cases))) {
    $text = $line.Trim()
    if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
    if ($text.StartsWith('@')) {
        $space = $text.IndexOf(' ')
        $inputs += , @($text.Substring(1, $space - 1), $text.Substring($space + 1))
    }
    else {
        $formulas += $text
    }
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Add()
    $sheet = $book.Worksheets.Item(1)
    $sheet.Name = 'Sheet1'
    $excel.Calculation = -4135   # manual while the cells are filled

    foreach ($pair in $inputs) {
        $cell = $sheet.Range($pair[0])
        $value = $pair[1]
        if ($value.StartsWith('"')) {
            $content = $value.Substring(1, $value.Length - 2)
            if ($content.Length -eq 0) { $cell.Formula = '=""' }
            else { $cell.Value2 = [string]("'" + $content) }
        }
        elseif ($value -eq 'TRUE' -or $value -eq 'FALSE') { $cell.Formula = $value }
        elseif ($value.StartsWith('#')) { $cell.Formula = $errorFormulas[$value] }
        else { $cell.Value2 = [double]::Parse($value, $invariant) }
    }

    $invalid = @{}
    for ($i = 0; $i -lt $formulas.Count; $i++) {
        try { $sheet.Range("L$(20 * $i + 1)").Formula = '=' + $formulas[$i] }
        catch { $invalid[$i] = $true }
    }

    $excel.CalculateFull()
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Excel $($excel.Version) build $($excel.Build), locale $([System.Globalization.CultureInfo]::CurrentCulture.Name), decimal '$($excel.International(3))', thousands '$($excel.International(4))', written by tools/excel-function-oracle.ps1")
    for ($i = 0; $i -lt $formulas.Count; $i++) {
        if ($invalid.ContainsKey($i)) { $kind = 'invalid'; $text = '' }
        else {
            $value = $sheet.Range("L$(20 * $i + 1)").Value2
            if ($null -eq $value) { $kind = 'blank'; $text = '' }
            elseif ($value -is [int]) { $kind = 'error'; $code = $value + 2146828288; $text = $errors[$code]; if ($null -eq $text) { $text = "#$code" } }
            elseif ($value -is [bool]) { $kind = 'boolean'; $text = $(if ($value) { 'TRUE' } else { 'FALSE' }) }
            elseif ($value -is [double]) { $kind = 'number'; $text = $value.ToString('R', $invariant) }
            else { $kind = 'text'; $text = ([string]$value).Replace('\', '\').Replace("`t", '\t').Replace("`r", '\r').Replace("`n", '\n') }
        }
        $lines.Add("$i`t$kind`t$text`t$($formulas[$i])")
    }

    [System.IO.File]::WriteAllLines($Output, $lines, (New-Object System.Text.UTF8Encoding($false)))
    $book.Close($false)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
