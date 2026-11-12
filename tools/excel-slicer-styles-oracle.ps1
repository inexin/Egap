<#
.SYNOPSIS
Reads Excel's 14 built-in slicer styles, for tools/gen-slicer-styles.py.

.DESCRIPTION
ECMA-376 does not list the slicer styles, so Excel tells them: for each built-in style (SlicerStyleLight1 to 6,
SlicerStyleOther1 and 2, SlicerStyleDark1 to 6) and each of its elements that has a format, the fill, the font and the
borders, with theme colours and their tint where Excel gives them. One line per element goes to the output file:
style, element, then key=value parts.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-slicer-styles-oracle.ps1 -Output slicer-styles.txt
#>
param([Parameter(Mandatory = $true)][string]$Output)

$ErrorActionPreference = 'Stop'
$elements = @{ 0 = 'WholeTable'; 1 = 'HeaderRow'; 28 = 'UnselectedItemWithData'; 29 = 'UnselectedItemWithNoData'; 30 = 'SelectedItemWithData';
    31 = 'SelectedItemWithNoData'; 32 = 'HoveredUnselectedItemWithData'; 33 = 'HoveredSelectedItemWithData';
    34 = 'HoveredUnselectedItemWithNoData'; 35 = 'HoveredSelectedItemWithNoData' }
$edges = @{ 7 = 'L'; 8 = 'T'; 9 = 'B'; 10 = 'R'; 11 = 'V'; 12 = 'H' }

function Color($thing) {
    $text = ''
    try {
        $theme = $thing.ThemeColor
        if ($theme -gt 0) { $text = "theme$theme" }
    }
    catch { }

    if ($text -eq '') { $text = 'rgb' + ('{0:X6}' -f [int]$thing.Color) }
    try {
        $tint = $thing.TintAndShade
        if ($tint -ne 0) { $text += "~$tint" }
    }
    catch { }

    return $text
}

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $book = $excel.Workbooks.Add()
    $lines = @("Excel $($excel.Version) build $($excel.Build)")
    $names = @(1..6 | ForEach-Object { "SlicerStyleLight$_" }) + @('SlicerStyleOther1', 'SlicerStyleOther2') + @(1..6 | ForEach-Object { "SlicerStyleDark$_" })
    foreach ($name in $names) {
        $style = $book.TableStyles.Item($name)
        foreach ($type in ($elements.Keys | Sort-Object)) {
            try { $element = $style.TableStyleElements.Item($type) } catch { continue }
            if (-not $element.HasFormat) { continue }
            $parts = @()
            $interior = $element.Interior
            if ($interior.Pattern -eq 1) { $parts += 'fill=' + (Color $interior) + "/pattern$($interior.Pattern)" }
            $font = $element.Font
            if ($font.Bold -eq $true) { $parts += 'bold' }
            try { if ($font.Color -ne $null) { $parts += 'font=' + (Color $font) } } catch { }
            foreach ($edge in ($edges.Keys | Sort-Object)) {
                $border = $element.Borders.Item($edge)
                if ($border.LineStyle -ne -4142 -and $border.LineStyle -ne $null) {
                    $parts += "$($edges[$edge])=$($border.LineStyle)/$($border.Weight)/" + (Color $border)
                }
            }

            $lines += "$name`t$($elements[$type])`t$($parts -join ' ')"
        }
    }

    $lines | Set-Content -Path $Output -Encoding UTF8
    $book.Close($false)
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
