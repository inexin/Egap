<#
.SYNOPSIS
Adds slicers to the pivot tables of pivots.xlsx in Excel, for the slicer tests and the slicer drawing gate.

.DESCRIPTION
Opens the pivots.xlsx that tools/excel-pivot-oracle.ps1 made and adds slicers on its Compact sheet: one on a row field
(Region) with the default style, one on a field outside the layout (Year) in two columns with an item cleared and a dark
style, and one on Product that also filters the Outline sheet's pivot table, with no header and a light style and two
items cleared. It saves the book as slicers.xlsx, exports the Compact sheet to slicers.pdf, and writes slicers.txt: the
Excel version and each slicer's name, cache, caption, style, columns, place and size in points and selected items.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-slicer-oracle.ps1 -Book tests/Egap.Spreadsheets.Tests/Oracle/pivots.xlsx -Output scratch
#>
param(
    [Parameter(Mandatory = $true)][string]$Book,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path
$bookPath = (Resolve-Path $Book).Path

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $workbook = $excel.Workbooks.Open($bookPath, 0, $true)
    $sheet = $workbook.Worksheets.Item('Compact')
    $table = $sheet.PivotTables(1)
    $outline = $workbook.Worksheets.Item('Outline').PivotTables(1)
    $lines = @("Excel $($excel.Version) build $($excel.Build)")

    $region = $workbook.SlicerCaches.Add2($table, 'Region')
    $regionSlicer = $region.Slicers.Add($sheet, [Type]::Missing, 'Region', 'Region', 20, 400, 144, 180)

    $year = $workbook.SlicerCaches.Add2($table, 'Year')
    $yearSlicer = $year.Slicers.Add($sheet, [Type]::Missing, 'Year', 'Sales year', 20, 560, 180, 90)
    $yearSlicer.NumberOfColumns = 2
    $yearSlicer.Style = 'SlicerStyleDark2'
    $year.SlicerItems.Item('2020').Selected = $false

    $product = $workbook.SlicerCaches.Add2($table, 'Product')
    $product.PivotTables.AddPivotTable($outline)
    $productSlicer = $product.Slicers.Add($sheet, [Type]::Missing, 'Product', 'Product', 220, 400, 144, 160)
    $productSlicer.DisplayHeader = $false
    $productSlicer.Style = 'SlicerStyleLight3'
    $product.SlicerItems.Item('Figs').Selected = $false
    $product.SlicerItems.Item('Kiwis').Selected = $false

    foreach ($cache in $workbook.SlicerCaches) {
        foreach ($slicer in $cache.Slicers) {
            $selected = @($cache.SlicerItems | Where-Object { $_.Selected } | ForEach-Object { $_.Name }) -join ','
            $lines += "$($slicer.Name)`t$($cache.Name)`t$($slicer.Caption)`t$($slicer.Style.Name)`t$($slicer.NumberOfColumns)`t$($slicer.Top)`t$($slicer.Left)`t$($slicer.Width)`t$($slicer.Height)`t$($slicer.RowHeight)`t$($slicer.ColumnWidth)`t$selected"
        }
    }

    $workbook.SaveAs((Join-Path $outputPath 'slicers.xlsx'), 51)
    $sheet.ExportAsFixedFormat(0, (Join-Path $outputPath 'slicers.pdf'))
    $lines | Set-Content -Path (Join-Path $outputPath 'slicers.txt') -Encoding UTF8
    $workbook.Close($false)
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
