<#
.SYNOPSIS
Has Excel print the workbooks tools/make-row-height-oracle.py wrote to PDF, for RowHeightOracleTests.

.DESCRIPTION
Exports each workbook in the folder to name.pdf beside it and writes standard.txt: a header line with Excel's version and
printer, then one line per workbook of its name and the default row height Excel gives it on screen, in points.
RowHeightOracleTests.ReadExcelPdfsWhenAsked then measures the rows in the PDFs for row-heights.excel.tsv.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-row-height-oracle.ps1 <folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder
)

$ErrorActionPreference = 'Stop'
$folderPath = (Resolve-Path $Folder).Path
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $lines = @("# Excel $($excel.Version) build $($excel.Build), printer $($excel.ActivePrinter), written by tools/excel-row-height-oracle.ps1")
    foreach ($file in Get-ChildItem $folderPath -Filter '*.xlsx') {
        $book = $excel.Workbooks.Open($file.FullName, 0, $true)
        try {
            $lines += "$($file.BaseName)`t$($book.Worksheets.Item(1).StandardHeight.ToString([System.Globalization.CultureInfo]::InvariantCulture))"
            $book.ExportAsFixedFormat(0, [System.IO.Path]::ChangeExtension($file.FullName, '.pdf'))
        }
        finally {
            $book.Close($false)
        }
    }

    [System.IO.File]::WriteAllLines((Join-Path $folderPath 'standard.txt'), [string[]]$lines)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
