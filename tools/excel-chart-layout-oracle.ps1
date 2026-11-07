<#
.SYNOPSIS
Builds a workbook of charts in Excel, and reads where Excel lays out their parts, for ChartLayoutOracleTests.

.DESCRIPTION
  -Build <file.xlsx>   Excel makes column charts over many data ranges and sizes (for its automatic axis scales) and one chart
                       of each 2D type with each legend position, with and without titles, and saves the workbook.
  -Read <file.xlsx>    Excel opens the workbook and writes each chart's layout to the -Out file, one fact to a line: sheet and
                       chart, key, value, in points from the chart area's corner. With -Pdf, it also exports the book as a PDF.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-chart-layout-oracle.ps1 -Build <file.xlsx>
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-chart-layout-oracle.ps1 -Read <file.xlsx> -Out <facts.tsv> [-Pdf <file.pdf>]
#>
param(
    [string]$Build,
    [string]$Read,
    [string]$Out,
    [string]$Pdf
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Num($value) { return ([double]$value).ToString('R', $invariant) }

function Fact($facts, $chart, $key, [scriptblock]$value) {
    try {
        $v = & $value
        if ($null -ne $v) { [void]$facts.Add("$chart`t$key`t" + (Num $v)) }
    }
    catch { }
}

function Box($facts, $chart, $name, $element) {
    Fact $facts $chart "$name.left" { $element.Left }
    Fact $facts $chart "$name.top" { $element.Top }
    Fact $facts $chart "$name.width" { $element.Width }
    Fact $facts $chart "$name.height" { $element.Height }
}

function ReadChart($facts, $name, $chart) {
    Fact $facts $name 'area.width' { $chart.ChartArea.Width }
    Fact $facts $name 'area.height' { $chart.ChartArea.Height }
    try {
        $p = $chart.PlotArea
        Fact $facts $name 'plot.left' { $p.InsideLeft }
        Fact $facts $name 'plot.top' { $p.InsideTop }
        Fact $facts $name 'plot.width' { $p.InsideWidth }
        Fact $facts $name 'plot.height' { $p.InsideHeight }
        Box $facts $name 'outer' $p
    }
    catch { }
    if ($chart.HasTitle) { Box $facts $name 'title' $chart.ChartTitle }
    if ($chart.HasLegend) { Box $facts $name 'legend' $chart.Legend }
    foreach ($type in 1, 2) {
        foreach ($group in 1, 2) {
            try {
                if (-not $chart.HasAxis($type, $group)) { continue }
                $axis = $chart.Axes($type, $group)
                $label = "axis$type$group"
                Box $facts $name $label $axis
                Fact $facts $name "$label.min" { $axis.MinimumScale }
                Fact $facts $name "$label.max" { $axis.MaximumScale }
                Fact $facts $name "$label.major" { $axis.MajorUnit }
                if ($axis.HasTitle) { Box $facts $name "$label.title" $axis.AxisTitle }
            }
            catch { }
        }
    }
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    if ($Build) {
        $book = $excel.Workbooks.Add()
        $sheet = $book.Worksheets.Item(1)
        $sheet.Name = 'Data'
        $charts = $book.Worksheets.Add()
        $charts.Name = 'Charts'

        # The data ranges: one row per range, five points a row.
        $ranges = @(
            @(1, 5), @(0, 10), @(0, 100), @(3, 97), @(12, 37), @(5, 6), @(100, 110), @(1000, 1250), @(950, 1000), @(0.1, 0.9),
            @(0.001, 0.005), @(-5, 5), @(-120, -20), @(-3, 17), @(1, 2), @(1234, 98765), @(1e6, 7e6), @(83, 100), @(5, 6.2), @(42, 42),
            @(0, 0), @(0.5, 0.75), @(-1, 0), @(7, 73), @(250, 2500)
        )
        $row = 1
        $top = 10
        $sizes = @(@(360, 216), @(480, 288), @(240, 150), @(720, 432))
        $index = 0
        foreach ($range in $ranges) {
            $lo = [double]$range[0]
            $hi = [double]$range[1]
            $sheet.Cells.Item($row, 1).Value2 = "Range $index"
            foreach ($i in 0..4) {
                $sheet.Cells.Item($row, $i + 2).Value2 = [double]($lo + (($hi - $lo) * $i / 4))
            }

            foreach ($size in $sizes) {
                $shape = $charts.Shapes.AddChart(51, 10, $top, $size[0], $size[1])
                $c = $shape.Chart
                $c.SetSourceData($sheet.Range($sheet.Cells.Item($row, 1), $sheet.Cells.Item($row, 6)), 1)
                $c.HasTitle = $false
                $c.HasLegend = $false
                $shape.Name = "Scale$index-$($size[0])"
                $top += $size[1] + 10
            }

            $row++
            $index++
        }

        # Three series over four categories, for the types.
        $row++
        $first = $row
        $sheet.Cells.Item($row, 1).Value2 = 'Cat'
        foreach ($s in 1..3) { $sheet.Cells.Item($row, $s + 1).Value2 = "Series $s" }
        $values = @(@(4.3, 2.4, 2), @(2.5, 4.4, 2), @(3.5, 1.8, 3), @(4.5, 2.8, 5))
        foreach ($r in 0..3) {
            $row++
            $sheet.Cells.Item($row, 1).Value2 = "Category $($r + 1)"
            foreach ($s in 0..2) { $sheet.Cells.Item($row, $s + 2).Value2 = [double]$values[$r][$s] }
        }

        $data = $sheet.Range($sheet.Cells.Item($first, 1), $sheet.Cells.Item($row, 4))
        $types = @{
            Column = 51; ColumnStacked = 52; Column100 = 53; Bar = 57; BarStacked = 58; Line = 4; LineMarkers = 65; LineStacked = 63
            Area = 1; AreaStacked = 76; Pie = 5; Doughnut = -4120; Scatter = -4169; ScatterLines = 74; Bubble = 15; Radar = -4151; RadarFilled = 82
        }
        foreach ($type in $types.GetEnumerator() | Sort-Object Name) {
            foreach ($legend in 'none', 'r', 'b', 't', 'l') {
                foreach ($titled in $false, $true) {
                    $shape = $charts.Shapes.AddChart($type.Value, 10, $top, 360, 216)
                    $c = $shape.Chart
                    $c.SetSourceData($data, 2)
                    $c.HasTitle = $titled
                    if ($titled) { $c.ChartTitle.Text = 'A title' }
                    $c.HasLegend = $legend -ne 'none'
                    if ($legend -ne 'none') {
                        $c.Legend.Position = @{ r = -4152; b = -4107; t = -4160; l = -4131 }[$legend]
                    }

                    if ($titled -and $type.Name -notin 'Pie', 'Doughnut', 'Radar', 'RadarFilled') {
                        foreach ($axisType in 1, 2) {
                            $axis = $c.Axes($axisType)
                            $axis.HasTitle = $true
                            $axis.AxisTitle.Text = 'Axis'
                        }
                    }

                    $shape.Name = "$($type.Name)-$legend-$(if ($titled) { 't' } else { 'n' })"
                    $top += 226
                }
            }
        }

        $book.SaveAs([IO.Path]::GetFullPath($Build), 51)
        $book.Close($false)
    }

    if ($Read) {
        $facts = [System.Collections.ArrayList]::new()
        $book = $excel.Workbooks.Open([IO.Path]::GetFullPath($Read), 0, $true)
        foreach ($sheet in $book.Worksheets) {
            foreach ($object in $sheet.ChartObjects()) {
                ReadChart $facts "$($sheet.Name)!$($object.Name)" $object.Chart
            }
        }

        foreach ($sheet in $book.Charts) {
            ReadChart $facts "$($sheet.Name)!" $sheet
        }

        if ($Pdf) { $book.ExportAsFixedFormat(0, [IO.Path]::GetFullPath($Pdf)) }
        $book.Close($false)
        [IO.File]::WriteAllLines([IO.Path]::GetFullPath($Out), [string[]]$facts, [Text.UTF8Encoding]::new($false))
    }
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel)
}
