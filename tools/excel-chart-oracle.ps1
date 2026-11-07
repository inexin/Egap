<#
.SYNOPSIS
Builds a workbook of charts in Excel, and reads what Excel sees in the charts of a workbook, for ChartOracleTests.

.DESCRIPTION
  -Build <file.xlsx>   Excel makes one chart of every type it has on a data sheet, some with titles, labels, trendlines,
                       error bars, a second axis and fixed scales, a chart sheet too, and saves the workbook.
  -Read <file.xlsx>    Excel opens the workbook (recovering nothing: a file it has to repair is an error) and writes what
                       it sees in each chart to the -Out file, one fact to a line: chart, key, value.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-chart-oracle.ps1 -Build <file.xlsx>
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-chart-oracle.ps1 -Read <file.xlsx> -Out <facts.tsv>
#>
param(
    [string]$Build,
    [string]$Read,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Number($value) {
    if ($null -eq $value) { return '' }
    if ($value -is [double] -or $value -is [single] -or $value -is [int] -or $value -is [decimal]) { return ([double]$value).ToString('R', $invariant) }
    return [string]$value
}

function Clean($text) { return ([string]$text) -replace "[\r\n\t]", ' ' }

# A fact a chart does not have (a pie's axis, say) throws in Excel; it is left out of the list.
function Fact($facts, $chart, $key, [scriptblock]$value) {
    try {
        $text = & $value
        if ($null -ne $text) { [void]$facts.Add("$chart`t$key`t" + (Clean $text)) }
    }
    catch { }
}

function ReadChart($facts, $name, $chart) {
    Fact $facts $name 'type' { $chart.ChartType }
    Fact $facts $name 'hasTitle' { $chart.HasTitle }
    if ($chart.HasTitle) { Fact $facts $name 'title' { $chart.ChartTitle.Text } }
    Fact $facts $name 'hasLegend' { $chart.HasLegend }
    if ($chart.HasLegend) { Fact $facts $name 'legendPosition' { $chart.Legend.Position } }
    Fact $facts $name 'plotVisibleOnly' { $chart.PlotVisibleOnly }
    Fact $facts $name 'displayBlanksAs' { $chart.DisplayBlanksAs }
    $series = $chart.FullSeriesCollection()
    Fact $facts $name 'seriesCount' { $series.Count }
    for ($i = 1; $i -le $series.Count; $i++) {
        $one = $series.Item($i)
        $s = "series$i"
        Fact $facts $name "$s.name" { $one.Name }
        Fact $facts $name "$s.formula" { $one.Formula }
        Fact $facts $name "$s.type" { $one.ChartType }
        Fact $facts $name "$s.axisGroup" { $one.AxisGroup }
        Fact $facts $name "$s.values" { (@($one.Values) | ForEach-Object { Number $_ }) -join ';' }
        Fact $facts $name "$s.categories" { (@($one.XValues) | ForEach-Object { Number $_ }) -join ';' }
        Fact $facts $name "$s.hasDataLabels" { $one.HasDataLabels }
        Fact $facts $name "$s.trendlines" { $one.Trendlines().Count }
        Fact $facts $name "$s.hasErrorBars" { $one.HasErrorBars }
        Fact $facts $name "$s.smooth" { $one.Smooth }
        Fact $facts $name "$s.marker" { $one.MarkerStyle }
    }

    foreach ($axis in @(@('category', 1, 1), @('value', 2, 1), @('category2', 1, 2), @('value2', 2, 2))) {
        $a = $axis[0]
        try {
            if (-not $chart.HasAxis($axis[1], $axis[2])) { continue }
            $one = $chart.Axes($axis[1], $axis[2])
        }
        catch { continue }

        Fact $facts $name "$a.hasTitle" { $one.HasTitle }
        if ($one.HasTitle) { Fact $facts $name "$a.title" { $one.AxisTitle.Text } }
        Fact $facts $name "$a.reversed" { $one.ReversePlotOrder }
        Fact $facts $name "$a.majorGridlines" { $one.HasMajorGridlines }
        Fact $facts $name "$a.minorGridlines" { $one.HasMinorGridlines }
        Fact $facts $name "$a.tickLabelPosition" { $one.TickLabelPosition }
        Fact $facts $name "$a.majorTick" { $one.MajorTickMark }
        Fact $facts $name "$a.minorTick" { $one.MinorTickMark }
        Fact $facts $name "$a.numberFormat" { $one.TickLabels.NumberFormat }
        if (-not $one.MinimumScaleIsAuto) { Fact $facts $name "$a.min" { Number $one.MinimumScale } }
        if (-not $one.MaximumScaleIsAuto) { Fact $facts $name "$a.max" { Number $one.MaximumScale } }
        if (-not $one.MajorUnitIsAuto) { Fact $facts $name "$a.majorUnit" { Number $one.MajorUnit } }
        if ($one.ScaleType -eq -4133) { Fact $facts $name "$a.logBase" { Number $one.LogBase } }
    }
}

function NewChart($sheet, $type, $data, $name, [ref]$row) {
    $top = $sheet.Cells.Item($row.Value, 1).Top
    # Some types, such as surface, can only be made once the chart has its data; the newer types only directly.
    $newer = $type -ge 117 -and $type -le 123
    $made = [int]$(if ($newer) { $type } else { 51 })
    for ($try = 1; ; $try++) {
        try {
            if ($newer) {
                # These take their data from the selection, and only on the sheet that holds it.
                $sheet = $data.Worksheet
                $sheet.Activate()
                [void]$data.Select()
                $top = $sheet.Cells.Item($row.Value + 20, 1).Top
                $shape = $sheet.Shapes.AddChart2(-1, $made, 10, $top, 360, 220)
                $shape.Name = $name
            }
            else {
                $shape = $sheet.Shapes.AddChart2(-1, $made, 10, $top, 360, 220)
                $shape.Name = $name
                $shape.Chart.SetSourceData($data)
                $shape.Chart.ChartType = [int]$type
            }

            break
        }
        catch {
            # Excel's late binding now and then gives a cast error it does not give a moment later.
            if ($try -ge 3) { throw "$name (type $type): $_" }
            if ($shape) { try { $shape.Delete() } catch { } }
            Start-Sleep -Milliseconds 500
        }
    }

    $row.Value += 16
    return $shape.Chart
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    if ($Build) {
        $book = $excel.Workbooks.Add()
        while ($book.Worksheets.Count -gt 1) { $book.Worksheets.Item(2).Delete() }
        $data = $book.Worksheets.Item(1)
        $data.Name = 'Data'
        $rows = @(
            @('Item', 'North', 'South', 'East', 'West', 'Volume'),
            @('Jan', 12, 7, 15, 3, 120),
            @('Feb', 18, 9, 21, 6, 90),
            @('Mar', 9, 14, 17, 4, 150),
            @('Apr', 22, 11, 26, 9, 60),
            @('May', 16, 13, 19, 7, 110),
            @('Jun', 25, 8, 28, 5, 75))
        for ($r = 0; $r -lt $rows.Count; $r++) {
            for ($c = 0; $c -lt 6; $c++) { $data.Cells.Item($r + 1, $c + 1).Formula = [string]$rows[$r][$c] }
        }

        # A hierarchy for the treemap and sunburst, and steps for the waterfall.
        $tree = @(@('Group', 'Part', 'Size'), @('A', 'a1', 10), @('A', 'a2', 20), @('B', 'b1', 5), @('B', 'b2', 15), @('B', 'b3', 25), @('C', 'c1', 30))
        for ($r = 0; $r -lt $tree.Count; $r++) {
            for ($c = 0; $c -lt 3; $c++) { $data.Cells.Item($r + 1, $c + 8).Formula = [string]$tree[$r][$c] }
        }

        $steps = @(@('Step', 'Change'), @('Start', 100), @('Up', 40), @('Down', -30), @('End', 110))
        for ($r = 0; $r -lt $steps.Count; $r++) {
            for ($c = 0; $c -lt 2; $c++) { $data.Cells.Item($r + 1, $c + 12).Formula = [string]$steps[$r][$c] }
        }

        $dates = @(@('Day', 'Open', 'High', 'Low', 'Close'), @(45000, 10, 14, 9, 12), @(45001, 12, 15, 11, 13), @(45002, 13, 13, 8, 9), @(45005, 9, 12, 8, 11))
        for ($r = 0; $r -lt $dates.Count; $r++) {
            for ($c = 0; $c -lt 5; $c++) { $data.Cells.Item($r + 10, $c + 1).Formula = [string]$dates[$r][$c] }
        }

        $data.Range('A11:A14').NumberFormat = 'yyyy\-mm\-dd'

        $sheet = $book.Worksheets.Add([Type]::Missing, $data)
        $sheet.Name = 'Charts'
        $row = 1
        $plain = $data.Range('A1:D7')
        $types = [ordered]@{
            ColumnClustered = 51; ColumnStacked = 52; ColumnStacked100 = 53; Column3D = -4100; ColumnClustered3D = 54; ColumnStacked3D = 55; ColumnStacked1003D = 56
            BarClustered = 57; BarStacked = 58; BarStacked100 = 59; BarClustered3D = 60; BarStacked3D = 61; BarStacked1003D = 62
            Line = 4; LineStacked = 63; LineStacked100 = 64; LineMarkers = 65; LineMarkersStacked = 66; LineMarkersStacked100 = 67; Line3D = -4101
            Area = 1; AreaStacked = 76; AreaStacked100 = 77; Area3D = -4098; AreaStacked3D = 78; AreaStacked1003D = 79
            Radar = -4151; RadarMarkers = 81; RadarFilled = 82; Surface = 83; SurfaceWireframe = 84; SurfaceTop = 85; SurfaceTopWireframe = 86
            Scatter = -4169; ScatterSmooth = 72; ScatterSmoothNoMarkers = 73; ScatterLines = 74; ScatterLinesNoMarkers = 75
        }
        foreach ($type in $types.GetEnumerator()) { [void](NewChart $sheet $type.Value $plain $type.Key ([ref]$row)) }
        $one = $data.Range('A1:B7')
        foreach ($type in ([ordered]@{ Pie = 5; PieExploded = 69; Pie3D = -4102; PieExploded3D = 70; PieOfPie = 68; BarOfPie = 71; Doughnut = -4120; DoughnutExploded = 80 }).GetEnumerator()) {
            [void](NewChart $sheet $type.Value $one $type.Key ([ref]$row))
        }

        [void](NewChart $sheet 15 $data.Range('B1:D7') 'Bubble' ([ref]$row))
        [void](NewChart $sheet 87 $data.Range('B1:D7') 'Bubble3D' ([ref]$row))
        [void](NewChart $sheet 88 $data.Range('A10:A14' + $excel.International(5) + 'C10:E14') 'StockHLC' ([ref]$row))
        [void](NewChart $sheet 89 $data.Range('A10:E14') 'StockOHLC' ([ref]$row))

        # What the model has to carry beyond a chart's type.
        $chart = NewChart $sheet 51 $plain 'Dressed' ([ref]$row)
        $chart.HasTitle = $true
        $chart.ChartTitle.Text = 'Sales by month'
        $chart.HasLegend = $true
        $chart.Legend.Position = -4107
        $value = $chart.Axes(2, 1)
        $value.HasTitle = $true
        $value.AxisTitle.Text = 'Units'
        $value.MinimumScale = 0
        $value.MaximumScale = 40
        $value.MajorUnit = 5
        $value.TickLabels.NumberFormat = '0.0'
        $value.HasMinorGridlines = $true
        $category = $chart.Axes(1, 1)
        $category.HasTitle = $true
        $category.AxisTitle.Text = 'Month'
        $category.ReversePlotOrder = $true
        $first = $chart.FullSeriesCollection(1)
        $first.HasDataLabels = $true
        $first.DataLabels().ShowCategoryName = $true
        $first.Format.Fill.ForeColor.RGB = 0x3366CC
        $first.Points(2).Format.Fill.ForeColor.RGB = 0x00C0FF
        [void]$first.Trendlines().Add(-4132)
        [void]$first.ErrorBar(1, 1, 1, 5)

        $chart = NewChart $sheet 51 $plain 'Combo' ([ref]$row)
        $line = $chart.FullSeriesCollection(2)
        $line.ChartType = 65
        $line.AxisGroup = 2
        $chart.FullSeriesCollection(3).ChartType = 1

        $chart = NewChart $sheet 4 $data.Range('A10:B14') 'DateAxis' ([ref]$row)
        $chart.Axes(1, 1).CategoryType = 3
        $chart.DisplayBlanksAs = 1

        $chart = NewChart $sheet -4169 $data.Range('B1:C7') 'LogAxis' ([ref]$row)
        $chart.Axes(2, 1).ScaleType = -4133
        $chart.Axes(2, 1).LogBase = 10
        $chart.FullSeriesCollection(1).MarkerStyle = 2
        $chart.FullSeriesCollection(1).Smooth = $true

        $chart = NewChart $sheet -4120 $one 'DoughnutHole' ([ref]$row)
        $chart.ChartGroups(1).DoughnutHoleSize = 40
        $chart.ChartGroups(1).FirstSliceAngle = 30

        $chart = NewChart $sheet 51 $data.Range('H1:J7') 'MultiLevel' ([ref]$row)

        # The newer types Excel keeps in chartEx parts.
        [void](NewChart $sheet 119 $data.Range('L1:M5') 'Waterfall' ([ref]$row))
        [void](NewChart $sheet 118 $data.Range('B1:B7') 'Histogram' ([ref]$row))
        [void](NewChart $sheet 122 $data.Range('A1:B7') 'Pareto' ([ref]$row))
        [void](NewChart $sheet 121 $data.Range('B1:D7') 'BoxWhisker' ([ref]$row))
        [void](NewChart $sheet 117 $data.Range('H1:J7') 'Treemap' ([ref]$row))
        [void](NewChart $sheet 120 $data.Range('H1:J7') 'Sunburst' ([ref]$row))
        [void](NewChart $sheet 123 $data.Range('A1:B7') 'Funnel' ([ref]$row))

        $page = $book.Charts.Add()
        $page.Name = 'ChartPage'
        $page.ChartType = 51
        $page.SetSourceData($plain)
        $page.HasTitle = $true
        $page.ChartTitle.Text = 'On its own sheet'

        $path = [System.IO.Path]::GetFullPath($Build)
        if (Test-Path $path) { Remove-Item $path }
        $book.SaveAs($path, 51)
        $book.Close($false)
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::Open($path, 'Update')
        try {
            $entry = $zip.GetEntry('docProps/core.xml')
            $reader = New-Object System.IO.StreamReader($entry.Open())
            $xml = $reader.ReadToEnd()
            $reader.Close()
            $entry.Delete()
            $xml = $xml -replace '<dc:creator>[^<]*</dc:creator>', '<dc:creator></dc:creator>' -replace '<cp:lastModifiedBy>[^<]*</cp:lastModifiedBy>', '<cp:lastModifiedBy></cp:lastModifiedBy>'
            $writer = New-Object System.IO.StreamWriter($zip.CreateEntry('docProps/core.xml').Open())
            $writer.Write($xml)
            $writer.Close()
        }
        finally { $zip.Dispose() }
        Write-Output ('Excel ' + $excel.Version + ' build ' + $excel.Build)
    }

    if ($Read) {
        $facts = New-Object System.Collections.ArrayList
        # xlRepairFile would hide a broken chart: open as is, and report whether Excel dropped a drawing.
        $book = $excel.Workbooks.Open([System.IO.Path]::GetFullPath($Read), 0, $true)
        foreach ($sheet in $book.Worksheets) {
            foreach ($object in $sheet.ChartObjects()) {
                ReadChart $facts ($sheet.Name + '!' + $object.Name) $object.Chart
            }
        }
        foreach ($page in $book.Charts) { ReadChart $facts $page.Name $page }
        $book.Close($false)
        [System.IO.File]::WriteAllLines([System.IO.Path]::GetFullPath($Out), [string[]]$facts, (New-Object System.Text.UTF8Encoding($false)))
        Write-Output ([string]$facts.Count + ' facts; Excel ' + $excel.Version + ' build ' + $excel.Build)
    }
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
