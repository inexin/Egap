<#
.SYNOPSIS
Makes an XLS workbook with Excel that has the objects Egap reads from XLS files, and Excel's own XLSX of it; and counts the
objects Excel sees in XLSX files.

.DESCRIPTION
  -Mode Make  -Folder <folder>  Excel makes a workbook with notes, hyperlinks, pictures, shapes, a group, embedded charts,
                                a chart sheet, a pivot table and defined names, saves it as objects.xls (Excel 97-2003), opens that again
                                and saves it as objects.xlsx, and writes its version to objects.txt. Pictures come from
                                picture.png and picture.jpg, which the script draws first.
  -Mode Count -Folder <folder>  Excel opens each .xlsx file of the folder read-only and writes to counts.tsv, a line to a
                                sheet: file, sheet, shapes, embedded charts, notes, hyperlinks, pivot tables; and a line to a workbook for
                                its chart sheets.

Needs Excel on the PC. Run it as one plain command:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-xls-objects-oracle.ps1 -Mode Make -Folder tests/Egap.Spreadsheets.Tests/Oracle/xlsobjects
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-xls-objects-oracle.ps1 -Mode Count -Folder C:/scratch/egap-saved
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Make', 'Count')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Folder
)

$ErrorActionPreference = 'Stop'
$folderPath = [System.IO.Path]::GetFullPath($Folder)
[void][System.IO.Directory]::CreateDirectory($folderPath)

Add-Type -AssemblyName System.Drawing

function Draw($path, $format) {
    $bitmap = New-Object System.Drawing.Bitmap 48, 32
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 240, 200, 80))
    $graphics.FillRectangle([System.Drawing.Brushes]::SteelBlue, 4, 4, 20, 24)
    $graphics.FillEllipse([System.Drawing.Brushes]::Firebrick, 26, 8, 18, 18)
    $graphics.Dispose()
    $bitmap.Save($path, $format)
    $bitmap.Dispose()
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$user = $excel.UserName
try {
    if ($Mode -eq 'Make') {
        $png = Join-Path $folderPath 'picture.png'
        $jpg = Join-Path $folderPath 'picture.jpg'
        Draw $png ([System.Drawing.Imaging.ImageFormat]::Png)
        Draw $jpg ([System.Drawing.Imaging.ImageFormat]::Jpeg)

        # Notes carry the user's name as their author: a fixed one, put back at the end.
        $excel.UserName = 'Egap Oracle'
        $book = $excel.Workbooks.Add()
        while ($book.Worksheets.Count -gt 1) { $book.Worksheets.Item($book.Worksheets.Count).Delete() }
        $data = $book.Worksheets.Item(1)
        $data.Name = 'Data'
        $rows = @(
            @('Month', 'Sales', 'Cost', 'Units', 'Region'),
            @('Jan', 120, 80, 12, 'North'), @('Feb', 135, 90, 15, 'South'), @('Mar', 150, 85, 11, 'North'),
            @('Apr', 110, 95, 18, 'South'), @('May', 170, 100, 14, 'North'), @('Jun', 160, 92, 16, 'South'))
        for ($r = 0; $r -lt $rows.Count; $r++) {
            for ($c = 0; $c -lt 5; $c++) {
                $cell = $data.Range([string][char](65 + $c) + [string]($r + 1))
                $value = $rows[$r][$c]
                if ($value -is [int]) { $cell.Value2 = [double]$value } else { $cell.Value2 = [string]$value }
            }
        }

        # Notes: one hidden, one shown, one over two lines.
        [void]$data.Range('A2').AddComment('First month')
        $shown = $data.Range('B3').AddComment('Shown note')
        $shown.Visible = $true
        [void]$data.Range('C4').AddComment("Two`nlines")

        # Hyperlinks: a web address with a tip, a place in the workbook, a file, a mail address.
        [void]$data.Hyperlinks.Add($data.Range('F2'), 'https://example.com/a b', '', 'Example tip', 'Web link')
        [void]$data.Hyperlinks.Add($data.Range('F3'), '', 'Data!A1', '', 'Inside')
        [void]$data.Hyperlinks.Add($data.Range('F4'), '..\other.xlsx', '', '', 'File link')
        [void]$data.Hyperlinks.Add($data.Range('F5'), 'mailto:someone@example.com', '', '', 'Mail')

        # Pictures and shapes on a sheet of their own.
        $art = $book.Worksheets.Add([Type]::Missing, $data)
        $art.Name = 'Art'
        [void]$art.Shapes.AddPicture($png, $false, $true, 10, 10, 96, 64)
        [void]$art.Shapes.AddPicture($jpg, $false, $true, 150, 10, 72, 48)
        $box = $art.Shapes.AddShape(1, 10, 100, 120, 50)
        $box.TextFrame.Characters().Text = 'Box text'
        $box.Fill.ForeColor.RGB = 0x50C8F0
        $oval = $art.Shapes.AddShape(9, 150, 100, 80, 50)
        $oval.Fill.ForeColor.RGB = 0x4060C0
        [void]$art.Shapes.AddLine(10, 170, 200, 200)
        $text = $art.Shapes.AddTextbox(1, 250, 100, 120, 40)
        $text.TextFrame.Characters().Text = "A text box`nover two lines"
        $first = $art.Shapes.AddShape(5, 10, 230, 60, 40)
        $first.Name = 'Grouped 1'
        $second = $art.Shapes.AddShape(1, 80, 240, 70, 30)
        $second.Name = 'Grouped 2'
        $second.TextFrame.Characters().Text = 'In a group'
        $group = $art.Shapes.Range([object[]]@('Grouped 1', 'Grouped 2')).Group()
        $group.Name = 'Group of two'

        # Embedded charts over the data.
        $kinds = @(
            @(51, 'Clustered columns'), @(58, 'Stacked bars'), @(65, 'Lines with markers'), @(5, 'A pie'),
            @(-4169, 'Scatter'), @(54, '3D columns'), @(-4120, 'Doughnut'), @(76, 'Stacked areas'), @(-4151, 'Radar'))
        $top = 10
        foreach ($kind in $kinds) {
            $frame = $data.ChartObjects().Add(400, $top, 300, 180)
            $chart = $frame.Chart
            $chart.SetSourceData($data.Range('A1:C7'))
            $chart.ChartType = $kind[0]
            $chart.HasTitle = $true
            $chart.ChartTitle.Text = $kind[1]
            $top += 200
        }

        # A combination chart: columns, and a line on a second axis.
        $frame = $data.ChartObjects().Add(720, 10, 300, 180)
        $combo = $frame.Chart
        $combo.SetSourceData($data.Range('A1:D7'))
        $combo.ChartType = 51
        $units = $combo.SeriesCollection(3)
        $units.ChartType = 65
        $units.AxisGroup = 2
        $combo.HasLegend = $false

        # A chart sheet after the data.
        $page = $book.Charts.Add([Type]::Missing, $art)
        $page.Name = 'Chart Sheet'
        $page.SetSourceData($data.Range('A1:D7'))
        $page.ChartType = 52

        # A pivot table of the data on a sheet of its own: months down, regions across, sums of sales, units as a page field.
        $pivotSheet = $book.Worksheets.Add([Type]::Missing, $art)
        $pivotSheet.Name = 'Pivot'
        $cache = $book.PivotCaches().Create(1, 'Data!R1C1:R7C5')
        $pivot = $cache.CreatePivotTable('Pivot!R3C1', 'SalesPivot')
        $pivot.PivotFields('Month').Orientation = 1
        $pivot.PivotFields('Region').Orientation = 2
        $pivot.PivotFields('Units').Orientation = 3
        [void]$pivot.AddDataField($pivot.PivotFields('Sales'), 'Sum of Sales', -4157)

        # Names: book and sheet names, a hidden one, the print area.
        [void]$book.Names.Add('Totals', '=Data!$B$2:$B$7')
        $hidden = $book.Names.Add('Hidden_Name', '=Data!$C$2:$C$7')
        $hidden.Visible = $false
        [void]$data.Names.Add('Local', '=Data!$D$2')
        $data.PageSetup.PrintArea = '$A$1:$D$7'

        $xls = Join-Path $folderPath 'objects.xls'
        $xlsx = Join-Path $folderPath 'objects.xlsx'
        # Neither file may carry the name of whoever made it: Excel leaves author and last author out of both.
        $book.RemovePersonalInformation = $true
        $book.SaveAs($xls, 56)
        $book.Close($false)
        $again = $excel.Workbooks.Open($xls)
        $again.RemovePersonalInformation = $true
        $again.SaveAs($xlsx, 51)
        $again.Close($false)
        Set-Content -Path (Join-Path $folderPath 'objects.txt') -Value ('# Excel ' + $excel.Version + ' build ' + $excel.Build + '; objects.xls made by tools/excel-xls-objects-oracle.ps1, objects.xlsx is Excel''s save of it') -Encoding UTF8
        Write-Output ('Made objects.xls and objects.xlsx with Excel ' + $excel.Version + ' build ' + $excel.Build)
    }
    else {
        $lines = New-Object System.Collections.ArrayList
        foreach ($file in Get-ChildItem -Path $folderPath -Filter '*.xlsx' | Sort-Object Name) {
            try {
                $book = $excel.Workbooks.Open($file.FullName, 0, $true)
                foreach ($sheet in $book.Worksheets) {
                    [void]$lines.Add(($file.Name, $sheet.Name, $sheet.Shapes.Count, $sheet.ChartObjects().Count, $sheet.Comments.Count, $sheet.Hyperlinks.Count, $sheet.PivotTables().Count) -join "`t")
                }
                [void]$lines.Add(($file.Name, '(chart sheets)', $book.Charts.Count) -join "`t")
                $book.Close($false)
            }
            catch {
                [void]$lines.Add(($file.Name, 'failed', ($_.Exception.Message -replace "[\r\n\t]", ' ')) -join "`t")
            }
        }
        Set-Content -Path (Join-Path $folderPath 'counts.tsv') -Value $lines -Encoding UTF8
        Write-Output ([string]$lines.Count + ' lines; Excel ' + $excel.Version + ' build ' + $excel.Build)
    }
}
finally {
    $excel.UserName = $user
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
