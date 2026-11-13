<#
.SYNOPSIS
Adds a sheet of formulas to a workbook, has Excel calculate it and saves the result, for tests that compare Egap's values
with Excel's on a workbook of their own (GETPIVOTDATA on tests/Egap.Spreadsheets.Tests/Oracle/pivots.xlsx, for one).

.DESCRIPTION
Each line of the formulas file that is not blank or a "#" comment is a formula in English, as Range.Formula takes it,
written into column A of a new sheet named Formulas, one per row. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-formula-sheet-oracle.ps1 <workbook.xlsx> <formulas.txt> <out.xlsx>
#>
param(
    [Parameter(Mandatory = $true)][string]$Workbook,
    [Parameter(Mandatory = $true)][string]$Formulas,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$inputPath = (Resolve-Path $Workbook).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$lines = [System.IO.File]::ReadAllLines((Resolve-Path $Formulas)) | Where-Object { $_.Trim().Length -gt 0 -and -not $_.Trim().StartsWith('#') }
$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Open($inputPath)
    $sheet = $book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count))
    $sheet.Name = 'Formulas'
    $row = 1
    foreach ($line in $lines) {
        $sheet.Cells.Item($row, 1).Formula = '=' + $line.Trim()
        $row++
    }

    $excel.CalculateFull()
    $book.SaveAs($outputPath, 51)
    $book.Close($false)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
