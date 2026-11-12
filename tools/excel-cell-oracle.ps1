<#
.SYNOPSIS
Has Excel calculate the CELL workbook and save it with its values, for CellInfoOracleTests.

.DESCRIPTION
The workbook comes from the tests, so its number formats are the invariant codes a file stores (Excel's COM interface reads
them in the PC's language). Make it, then run this script on it:

  $env:EGAP_WRITE_CELL_ORACLE = "$PWD\cell-info.input.xlsx"
  dotnet test tests/Egap.Spreadsheets.Tests -c Release --filter "FullyQualifiedName~CellInfoOracleTests.WritesTheInputWhenAsked"
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-cell-oracle.ps1 cell-info.input.xlsx tests/Egap.Spreadsheets.Tests/Oracle/cell-info.xlsx

CELL's format codes follow the PC's currency (kr here); the test reads the file as nb-NO.
#>
param(
    [Parameter(Mandatory = $true)][string]$Workbook,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$inputPath = (Resolve-Path $Workbook).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Open($inputPath)
    # Saved first, so CELL names the file it is in.
    $book.SaveAs($outputPath, 51)
    $excel.CalculateFull()
    $book.Save()
    $book.Close($false)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
