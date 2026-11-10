<#
.SYNOPSIS
Builds pivot tables in Excel over made-up data, for PivotTableTests.

.DESCRIPTION
Fills a Data sheet with sales rows (region, rep, product, year, a date, units, revenue, with blanks, a text among the
numbers and an error), then makes one pivot table per case on a sheet of its own: compact, outline and tabular layouts,
the classic layout with drop zones, values on rows and on columns, every summary function, page fields with an item or
(All), hidden items, sorting, custom subtotals and no totals. It saves the book as pivots.xlsx. Then it changes the data
(new rows, a new region, changed numbers, a removed product) and refreshes every pivot table, and saves the book again as
pivots-edited.xlsx; then it puts in a column (pivots-column.xlsx) and renames a header (pivots-renamed.xlsx), each
refreshed. The test makes the same changes and checks that a refresh writes what Excel wrote. The first lines of
pivots.txt record Excel's version and the changes.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-pivot-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path

$xlRow = 1; $xlColumn = 2; $xlPage = 3
$functions = @{ sum = -4157; count = -4112; average = -4106; max = -4136; min = -4139; product = -4149; countNums = -4113; stdDev = -4155; stdDevp = -4156; var = -4164; varp = -4165 }

$regions = @('North', 'South', 'East', 'West')
$reps = @('Ann', 'Bob', 'Cy', 'Di', 'Ed', 'Flo')
$products = @('Apples', 'Pears', 'Plums', 'Figs', 'Kiwis')

function Fill($sheet, $rows, $seed) {
    $sheet.Cells.Item(1, 1).Value2 = 'Region'
    $sheet.Cells.Item(1, 2).Value2 = 'Rep'
    $sheet.Cells.Item(1, 3).Value2 = 'Product'
    $sheet.Cells.Item(1, 4).Value2 = 'Year'
    $sheet.Cells.Item(1, 5).Value2 = 'Day'
    $sheet.Cells.Item(1, 6).Value2 = 'Units'
    $sheet.Cells.Item(1, 7).Value2 = 'Revenue'
    for ($i = 0; $i -lt $rows; $i++) {
        $r = $i + 2
        $k = $i * 7 + $seed
        $sheet.Cells.Item($r, 1).Value2 = $regions[$k % 4]
        $sheet.Cells.Item($r, 2).Value2 = $reps[($k * 5) % 6]
        $sheet.Cells.Item($r, 3).Value2 = $products[($k * 3) % 5]
        $sheet.Cells.Item($r, 4).Value2 = [double](2019 + ($k % 3))
        $sheet.Cells.Item($r, 5).Value2 = [double](43831 + ($k % 9) * 31)
        $sheet.Cells.Item($r, 5).NumberFormat = 'yyyy-mm-dd'
        $sheet.Cells.Item($r, 6).Value2 = [double](($k * 13) % 17 + 1)
        $sheet.Cells.Item($r, 7).Value2 = [double][math]::Round((($k * 37) % 101) * 1.25 + 0.5, 2)
    }

    # A blank region, a blank product, a text among the units and an error among the revenue.
    $sheet.Cells.Item(5, 1).Value2 = $null
    $sheet.Cells.Item(9, 3).Value2 = $null
    $sheet.Cells.Item(12, 6).Value2 = 'n/a'
    $sheet.Cells.Item(15, 7).Formula = '=NA()'
}

function Pivot($book, $cache, $name, $layout, $build) {
    $sheet = $book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count))
    $sheet.Name = $name
    $table = $cache.CreatePivotTable($sheet.Range('A6'), $name)
    if ($layout -eq 'classic') {
        $table.RowAxisLayout(1)
        $table.InGridDropZones = $true
    }
    else {
        $table.RowAxisLayout(@{ compact = 0; tabular = 1; outline = 2 }[$layout])
    }

    & $build $table
    $table.RefreshTable() | Out-Null
}

function AddData($table, $field, $function, $caption) {
    $table.AddDataField($table.PivotFields($field), $caption, $functions[$function]) | Out-Null
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Add()
    while ($book.Worksheets.Count -gt 1) { $book.Worksheets.Item(2).Delete() }
    $data = $book.Worksheets.Item(1)
    $data.Name = 'Data'
    Fill $data 60 3
    $cache = $book.PivotCaches().Create(1, 'Data!R1C1:R61C7')

    Pivot $book $cache 'Compact' 'compact' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Product').Orientation = $xlRow
        AddData $t 'Revenue' 'sum' 'Sum of Revenue'
    }
    Pivot $book $cache 'CompactColumns' 'compact' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Year').Orientation = $xlColumn
        AddData $t 'Units' 'sum' 'Sum of Units'
        AddData $t 'Revenue' 'average' 'Average of Revenue'
    }
    Pivot $book $cache 'Outline' 'outline' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Rep').Orientation = $xlRow
        $t.PivotFields('Product').Orientation = $xlPage
        AddData $t 'Units' 'count' 'Count of Units'
    }
    Pivot $book $cache 'Tabular' 'tabular' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Product').Orientation = $xlRow
        $t.PivotFields('Year').Orientation = $xlColumn
        AddData $t 'Revenue' 'sum' 'Sum of Revenue'
        AddData $t 'Units' 'max' 'Max of Units'
        $t.DataPivotField.Orientation = $xlRow
    }
    Pivot $book $cache 'Classic' 'classic' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Rep').Orientation = $xlRow
        $t.PivotFields('Year').Orientation = $xlColumn
        $t.PivotFields('Product').Orientation = $xlPage
        AddData $t 'Units' 'sum' 'Sum of Units'
    }
    Pivot $book $cache 'Functions' 'compact' {
        param($t)
        $t.PivotFields('Product').Orientation = $xlRow
        foreach ($f in @('sum', 'count', 'average', 'max', 'min', 'product', 'countNums', 'stdDev', 'stdDevp', 'var', 'varp')) {
            AddData $t 'Units' $f ("$f of Units")
        }
    }
    Pivot $book $cache 'Filtered' 'compact' {
        param($t)
        $t.PivotFields('Product').Orientation = $xlRow
        $t.PivotFields('Region').Orientation = $xlColumn
        $t.PivotFields('Year').Orientation = $xlPage
        AddData $t 'Revenue' 'sum' 'Sum of Revenue'
        $t.PivotFields('Year').CurrentPage = '2020'
        $t.PivotFields('Region').PivotItems('East').Visible = $false
        $t.PivotFields('Product').AutoSort(2, 'Product')
    }
    Pivot $book $cache 'Subtotals' 'tabular' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Product').Orientation = $xlRow
        AddData $t 'Units' 'sum' 'Sum of Units'
        $t.PivotFields('Region').Subtotals = [object[]]@($false, $true, $true, $false, $false, $false, $false, $false, $false, $false, $false, $false)
    }
    Pivot $book $cache 'NoTotals' 'outline' {
        param($t)
        $t.PivotFields('Region').Orientation = $xlRow
        $t.PivotFields('Rep').Orientation = $xlRow
        $t.PivotFields('Year').Orientation = $xlColumn
        AddData $t 'Units' 'sum' 'Sum of Units'
        $t.ColumnGrand = $false
        $t.RowGrand = $false
        $t.PivotFields('Region').Subtotals = [object[]]@($false, $false, $false, $false, $false, $false, $false, $false, $false, $false, $false, $false)
    }
    Pivot $book $cache 'Dates' 'tabular' {
        param($t)
        $t.PivotFields('Day').Orientation = $xlRow
        $t.PivotFields('Rep').Orientation = $xlColumn
        AddData $t 'Revenue' 'min' 'Min of Revenue'
    }
    Pivot $book $cache 'ValuesOnly' 'compact' {
        param($t)
        AddData $t 'Units' 'sum' 'Sum of Units'
        AddData $t 'Revenue' 'sum' 'Sum of Revenue'
    }

    $book.Worksheets.Item(1).Activate()
    $book.SaveAs((Join-Path $outputPath 'pivots.xlsx'), 51)

    # The change: twelve rows put in above row 61, so the source grows to A1:G73, from another seed with a new region;
    # numbers changed and Kiwis gone. Then a refresh.
    $data.Rows('61:72').Insert() | Out-Null
    for ($i = 0; $i -lt 12; $i++) {
        $r = 61 + $i
        $k = $i * 11 + 5
        $data.Cells.Item($r, 1).Value2 = @('North', 'Central', 'West')[$k % 3]
        $data.Cells.Item($r, 2).Value2 = $reps[$k % 6]
        $data.Cells.Item($r, 3).Value2 = $products[$k % 4]
        $data.Cells.Item($r, 4).Value2 = [double](2020 + ($k % 3))
        $data.Cells.Item($r, 5).Value2 = [double](44000 + $k)
        $data.Cells.Item($r, 5).NumberFormat = 'yyyy-mm-dd'
        $data.Cells.Item($r, 6).Value2 = [double]($k % 9 + 2)
        $data.Cells.Item($r, 7).Value2 = [double]($k * 2.5)
    }

    foreach ($r in @(2..60 + 73)) {
        if ($data.Cells.Item($r, 3).Value2 -eq 'Kiwis') { $data.Cells.Item($r, 3).Value2 = 'Pears' }
        if ($r % 4 -eq 0 -and $data.Cells.Item($r, 6).Value2 -is [double]) { $data.Cells.Item($r, 6).Value2 = [double]($data.Cells.Item($r, 6).Value2 * 2) }
    }

    $book.PivotCaches().Item(1).Refresh() | Out-Null
    $book.SaveAs((Join-Path $outputPath 'pivots-edited.xlsx'), 51)

    # Then a column put in before Year, which the source takes in as a new field, and a refresh.
    $data.Columns('D').Insert() | Out-Null
    $data.Cells.Item(1, 4).Value2 = 'Channel'
    for ($r = 2; $r -le 73; $r++) { $data.Cells.Item($r, 4).Value2 = @('Web', 'Store', 'Phone')[$r % 3] }
    $book.PivotCaches().Item(1).Refresh() | Out-Null
    $book.SaveAs((Join-Path $outputPath 'pivots-column.xlsx'), 51)

    # Then the Rep header renamed: the field goes, with the tables' uses of it, and a new one comes; and a refresh.
    $data.Cells.Item(1, 2).Value2 = 'Seller'
    $book.PivotCaches().Item(1).Refresh() | Out-Null
    $book.SaveAs((Join-Path $outputPath 'pivots-renamed.xlsx'), 51)
    $book.Close($false)
    Set-Content -Path (Join-Path $outputPath 'pivots.txt') -Value @(
        "# Excel $($excel.Version) build $($excel.Build) locale $([System.Globalization.CultureInfo]::CurrentCulture.Name)",
        '# pivots-edited.xlsx: rows 61-72 put in (see tools/excel-pivot-oracle.ps1), Kiwis in the other rows made Pears,',
        '# units in those rows whose new row number is a multiple of 4 doubled, then refreshed.',
        '# pivots-column.xlsx: then a Channel column put in before Year (Web, Store, Phone by row number mod 3), refreshed.',
        '# pivots-renamed.xlsx: then the Rep header renamed Seller, refreshed.')
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
