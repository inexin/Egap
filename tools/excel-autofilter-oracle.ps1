<#
.SYNOPSIS
Sets filters in Excel and records the rows each one hides, for AutoFilterTests.

.DESCRIPTION
Builds one book with a sheet per case: the same numbers, text, dates, mixed values, formatted numbers and formulas in A1:F16
under a header row, and a filter set with Range.AutoFilter (one table case filters a table instead). The book is saved
as -Book, and each output line is the sheet name, the criteria given to Excel and the rows Excel hid, tab separated.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-autofilter-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/autofilter.excel.tsv -Book tests/Egap.Spreadsheets.Tests/Oracle/autofilter.xlsx
#>
param([Parameter(Mandatory = $true)][string]$Output, [Parameter(Mandatory = $true)][string]$Book)
$ErrorActionPreference = 'Stop'
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Excel $($excel.Version) build $($excel.Build)")
$nums = @(5, 12, 1.5, -3, 100, 7, 12, 0, 42, 3.25, 8, 1000, 15, 2, 9)
$texts = @('apple', 'Banana', 'cherry', 'apple pie', 'b?x', 'a*b', '', 'date', 'Apple', '12', 'kiwi', 'fig', 'grape', 'ab', 'banana')
$dates = @(43831, 43862, 44000, 44197, 44562, 45000, 45292, 43900, 44300, 44927, 45100, 43500, 44700, 45200, 45350)
$mixed = @('x', '', 'TRUE', '#N/A', '3', 'y', '', '4', 'x', 'FALSE', '', '5', 'z', '1', '')
# Each case is one or more criteria: field, criteria1, operator (1 and, 2 or, 3-6 top and bottom, 7 values, 11 dynamic), criteria2.
$cases = @(
    @(, @(1, '>5', 1, $null)), @(, @(1, '>=12', 1, $null)), @(, @(1, '<>12', 1, $null)), @(, @(1, '=12', 1, $null)), @(, @(1, '>2', 1, '<10')), @(, @(1, '<0', 2, '>50')),
    @(, @(1, '=1*', 1, $null)), @(, @(1, '<>1*', 1, $null)), @(, @(1, '=*5', 1, $null)), @(, @(1, '3', 3, $null)), @(, @(1, '20', 5, $null)), @(, @(1, '2', 4, $null)), @(, @(1, '30', 6, $null)),
    @(, @(1, 33, 11, $null)), @(, @(1, 34, 11, $null)), @(, @(1, 'VALUES', 7, @('5', '12', '100'))),
    @(, @(2, 'apple', 1, $null)), @(, @(2, '=a*', 1, $null)), @(, @(2, '=*e*', 1, $null)), @(, @(2, '<>*an*', 1, $null)), @(, @(2, '=?????', 1, $null)),
    @(, @(2, '=b~?x', 1, $null)), @(, @(2, '=a~*b', 1, $null)), @(, @(2, '=', 1, $null)), @(, @(2, '<>', 1, $null)), @(, @(2, '>b', 1, $null)), @(, @(2, '<=cherry', 1, $null)), @(, @(2, '=12', 1, $null)), @(, @(2, '>10', 1, $null)),
    @(, @(2, 'VALUES', 7, @('apple', 'banana', ''))), @(, @(2, 'VALUES', 7, @('Apple Pie', 'kiwi'))),
    @(, @(3, '>1/1/2021', 1, $null)), @(, @(3, '<=6/14/2020', 1, $null)),
    @(, @(4, '=x', 1, $null)), @(, @(4, '>2', 1, $null)), @(, @(4, '=TRUE', 1, $null)), @(, @(4, '=#N/A', 1, $null)), @(, @(4, '<>x', 1, $null)), @(, @(4, '=', 1, $null)), @(, @(4, '<3', 1, $null)),
    @(, @(4, 'VALUES', 7, @('x', '3', 'TRUE', '#N/A'))),
    @(, @(5, '>=12', 1, $null)), @(, @(5, '=1*', 1, $null)),
    @(, @(6, '>B', 1, $null)), @(, @(6, '=*', 1, $null)), @(, @(6, '=b*', 1, $null)), @(, @(6, '<>', 1, $null)),
    @(@(1, '>2', 1, $null), @(2, '=a*', 1, $null)), @(@(1, '3', 4, $null), @(4, '<>', 1, $null)), @(@(1, 33, 11, $null), @(2, '<>', 1, $null))
)
try {
    $wb = $excel.Workbooks.Add()
    while ($wb.Worksheets.Count -lt $cases.Count + 1) { [void]$wb.Worksheets.Add([Type]::Missing, $wb.Worksheets.Item($wb.Worksheets.Count)) }
    for ($i = 0; $i -le $cases.Count; $i++) {
        $s = $wb.Worksheets.Item($i + 1)
        $s.Name = "c$($i + 1)"
        $s.Cells.Item(1, 1).Value2 = 'Num'; $s.Cells.Item(1, 2).Value2 = 'Text'; $s.Cells.Item(1, 3).Value2 = 'Date'; $s.Cells.Item(1, 4).Value2 = 'Mixed'; $s.Cells.Item(1, 5).Value2 = 'Fmt'; $s.Cells.Item(1, 6).Value2 = 'Form'
        for ($r = 0; $r -lt 15; $r++) {
            $s.Cells.Item($r + 2, 1).Value2 = [double]$nums[$r]
            if ($texts[$r] -ne '') { $s.Cells.Item($r + 2, 2).NumberFormat = '@'; $s.Cells.Item($r + 2, 2).Value2 = $texts[$r] }
            $s.Cells.Item($r + 2, 3).Value2 = [double]$dates[$r]
            $m = $mixed[$r]
            if ($m -eq 'TRUE' -or $m -eq 'FALSE' -or $m -eq '#N/A') { $s.Cells.Item($r + 2, 4).Formula = "=$m" } elseif ($m -match '^\d+$') { $s.Cells.Item($r + 2, 4).Value2 = [double]$m } elseif ($m -ne '') { $s.Cells.Item($r + 2, 4).Value2 = $m }
            $s.Cells.Item($r + 2, 5).Value2 = [double]$nums[$r]
            $s.Cells.Item($r + 2, 6).Formula = "=IF(MOD(ROW(),3)=0,`"`",B$($r + 2))"
        }
        $s.Range('C2:C16').NumberFormat = 'yyyy-mm-dd'
        $s.Range('E2:E16').NumberFormat = '#,##0'
        $range = $s.Range('A1:F16')
        if ($i -eq $cases.Count) {
            # A table filtered by its own filter.
            $table = $s.ListObjects.Add(1, $range, [Type]::Missing, 1)
            [void]$table.Range.AutoFilter(1, '>7')
            $criteria = '1|>7|table'
        }
        else {
            $parts = [System.Collections.Generic.List[string]]::new()
            foreach ($c in $cases[$i]) {
                if ($c[2] -eq 7) { [void]$range.AutoFilter($c[0], [object[]]$c[3], 7) }
                elseif ($c[2] -eq 11) { [void]$range.AutoFilter($c[0], [int]$c[1], 11) }
                elseif ($c[2] -ge 3) { [void]$range.AutoFilter($c[0], $c[1], $c[2]) }
                elseif ($null -ne $c[3]) { [void]$range.AutoFilter($c[0], $c[1], $c[2], $c[3]) }
                else { [void]$range.AutoFilter($c[0], $c[1]) }
                $parts.Add("$($c[0])|$($c[1])|$($c[2])|$($c[3] -join ',')")
            }
            $criteria = [string]::Join(' & ', $parts)
        }
        $hidden = [System.Collections.Generic.List[string]]::new()
        for ($r = 2; $r -le 16; $r++) { if ($s.Rows.Item($r).Hidden) { $hidden.Add("$r") } }
        $lines.Add("$($s.Name)`t$criteria`t$([string]::Join(',', $hidden))")
    }
    $wb.SaveAs([System.IO.Path]::GetFullPath($Book), 51)
    $wb.Close($false)
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
[System.IO.File]::WriteAllText($Output, [string]::Join("`n", $lines) + "`n", [System.Text.UTF8Encoding]::new($false))
