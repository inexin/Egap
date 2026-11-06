<#
.SYNOPSIS
Records what Excel calculates for tests/Egap.Spreadsheets.Tests/Oracle/links.xlsx, for LinkOracleTests.

.DESCRIPTION
Opens the workbook tools/make-links-oracle.py wrote, read-only and with links not updated, so its formulas read the values
kept for the linked book, runs Application.CalculateFull, and writes a tab-separated file: a header with Excel's version, then
one line per formula cell: address, kind (number, text, boolean, error or blank), value. The workbook is not saved.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-links-oracle.ps1 <links.xlsx> <out.tsv>
#>
param(
    [Parameter(Mandatory = $true)][string]$Workbook,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$errors = @{}
foreach ($e in @(@(-2146826288, '#NULL!'), @(-2146826281, '#DIV/0!'), @(-2146826273, '#VALUE!'), @(-2146826265, '#REF!'), @(-2146826259, '#NAME?'), @(-2146826252, '#NUM!'), @(-2146826246, '#N/A'))) { $errors[[int]$e[0]] = $e[1] }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$excel.AskToUpdateLinks = $false
$excel.AutomationSecurity = 3   # msoAutomationSecurityForceDisable
try {
    $book = $excel.Workbooks.Open((Resolve-Path $Workbook).Path, 0, $true)
    $excel.CalculateFull()
    while ($excel.CalculationState -ne 0) { Start-Sleep -Milliseconds 100 }   # xlDone
    $lines = @("# Excel $($excel.Version) build $($excel.Build)")
    $sheet = $book.Worksheets.Item(1)
    foreach ($cell in $sheet.UsedRange.Cells) {
        if (-not $cell.HasFormula) { continue }
        $value = $cell.Value2
        if ($null -eq $value) { $kind = 'blank'; $text = '' }
        elseif ($value -is [int]) { $kind = 'error'; $text = $errors[$value] }
        elseif ($value -is [double]) { $kind = 'number'; $text = $value.ToString('R', $invariant) }
        elseif ($value -is [bool]) { $kind = 'boolean'; $text = $value.ToString().ToUpperInvariant() }
        else { $kind = 'text'; $text = [string]$value }
        $lines += "$($cell.Address($false, $false))`t$kind`t$text"
    }
    $book.Close($false)
    [System.IO.File]::WriteAllText($Output, ($lines -join "`n") + "`n")
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
