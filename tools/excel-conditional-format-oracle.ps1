<#
.SYNOPSIS
Measures how Excel evaluates conditional formatting and data validation, for the conditional format tests.

.DESCRIPTION
Builds a workbook in Excel: on the sheet Rules, each column holds one of four sets of values in rows 2 to 21 under a label in
row 1, and gets one conditional formatting rule (a few columns get two); on the sheet Valid, each column gets one data
validation rule. Excel then reports, for every cell, the fill and font it shows (Range.DisplayFormat), the text it shows,
the icon an icon set gives it (found with the icon filter) and whether its value passes its validation (Validation.Value).
The workbook is saved next to the output, with .xlsx added, so the tests read the same rules as Excel wrote them.

Formulas are given in English and turned into the PC's language through a scratch cell, as FormatConditions.Add and
Validation.Add take formulas in the language of the user interface.

Lines of the output, tab-separated:
  F  label  row  fill  font colour  bold  italic  text   (colours as Excel's BGR numbers)
  I  label  row  icon   (the icon's one-based place in its set, or 0 for none)
  V  label  row  valid  (1 or 0)

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-conditional-format-oracle.ps1 -Output tests/Egap.Spreadsheets.Tests/Oracle/conditional-formats.excel.tsv
#>
param([Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
try {
    $book = $excel.Workbooks.Add()
    while ($book.Worksheets.Count -lt 3) { [void]$book.Worksheets.Add() }
    $rules = $book.Worksheets.Item(1); $rules.Name = 'Rules'
    $valid = $book.Worksheets.Item(2); $valid.Name = 'Valid'
    $scratch = $book.Worksheets.Item(3); $scratch.Name = 'Scratch'
    $today = [int][math]::Floor([double]$excel.Evaluate("TODAY()"))
    $lines.Add("# Excel $($excel.Version) build $($excel.Build), $([System.Globalization.CultureInfo]::CurrentCulture.Name): conditional formats and data validation, today $today, written by tools/excel-conditional-format-oracle.ps1")

    function Local([string]$formula, [string]$at) {
        $cell = $scratch.Range($at)
        $cell.Formula = $formula
        $text = $cell.FormulaLocal
        [void]$cell.Clear()
        return $text
    }

    # PowerShell keeps the type of the first value given to a COM property, so values of other types go through reflection.
    function Put($cell, $value) {
        [void]$cell.GetType().InvokeMember('Value2', [System.Reflection.BindingFlags]::SetProperty, $null, $cell, @($value))
    }

    function Letter([int]$column) {
        $name = ''
        while ($column -gt 0) { $m = ($column - 1) % 26; $name = [char](65 + $m) + $name; $column = [math]::Floor(($column - $m) / 26) }
        return $name
    }

    # The four sets of values: mixed, numbers, dates around today, and values to validate. A string starting with = is a
    # formula, one starting with ' is text, $null is a blank.
    $sets = @{
        M = @(5, 12, -3, 12, 40, "'apple", "'Banana", $null, '=NA()', 7.5, 0, 100, "'apple pie", 33, 12, "'APPLE", 2.5, 18, "' ", $true)
        N = @(5, 12, -3, 12, 40, 1, 8, $null, "'text", 7.5, 0, 100, 64, 33, 12, -20, 2.5, 18, 55, 9)
        D = @(-40, -31, -10, -8, -7, -6, -2, -1, 0, 1, 2, 6, 7, 8, 13, 25, 31, 40, 'text', $null)
        V = @(5, 5.5, 11, "'5", $null, $true, 0, -1, "'abc", "'abcd", 45000, 0.5, "'a", "'A", "'d", 1, '=NA()', "' ", 10, 10000000000)
    }

    function Fill($sheet, [int]$column, [string]$set) {
        $values = $sets[$set]
        for ($i = 0; $i -lt 20; $i++) {
            $cell = $sheet.Cells.Item($i + 2, $column)
            $v = $values[$i]
            if ($null -eq $v) { continue }
            if ($set -eq 'D' -and $v -is [int]) { Put $cell ($today + $v); $cell.NumberFormat = 'yyyy-mm-dd'; continue }
            if ($v -is [string] -and $v.StartsWith('=')) { $cell.Formula = $v }
            elseif ($v -is [string] -and $v.StartsWith("'")) { $cell.NumberFormat = '@'; Put $cell $v.Substring(1) }
            else { Put $cell $v }
        }
    }

    # Conditional formatting: label, set, and the code that adds the rule to the range; rules that format are given a red
    # fill, unless the code sets its own.
    $cf = @(
        @('cellIs between 5 and 20', 'M', { param($r, $a) $r.FormatConditions.Add(1, 1, (Local '=5' $a), (Local '=20' $a)) }),
        @('cellIs not between 5 and 20', 'M', { param($r, $a) $r.FormatConditions.Add(1, 2, (Local '=5' $a), (Local '=20' $a)) }),
        @('cellIs equal 12', 'M', { param($r, $a) $r.FormatConditions.Add(1, 3, (Local '=12' $a)) }),
        @('cellIs not equal 12', 'M', { param($r, $a) $r.FormatConditions.Add(1, 4, (Local '=12' $a)) }),
        @('cellIs greater than 10', 'M', { param($r, $a) $r.FormatConditions.Add(1, 5, (Local '=10' $a)) }),
        @('cellIs less than 10', 'M', { param($r, $a) $r.FormatConditions.Add(1, 6, (Local '=10' $a)) }),
        @('cellIs greater or equal 12', 'M', { param($r, $a) $r.FormatConditions.Add(1, 7, (Local '=12' $a)) }),
        @('cellIs less or equal 0', 'M', { param($r, $a) $r.FormatConditions.Add(1, 8, (Local '=0' $a)) }),
        @('cellIs equal apple', 'M', { param($r, $a) $r.FormatConditions.Add(1, 3, (Local '="apple"' $a)) }),
        @('cellIs greater than b', 'M', { param($r, $a) $r.FormatConditions.Add(1, 5, (Local '="b"' $a)) }),
        @('cellIs equal blank cell', 'M', { param($r, $a) $r.FormatConditions.Add(1, 3, (Local '=$AZ$1' $a)) }),
        @('expression above median', 'N', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local "=$a>MEDIAN($($a[0])`$2:$($a[0])`$21)" $a)) }),
        @('expression is text', 'M', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local "=ISTEXT($a)" $a)) }),
        @('expression every third row', 'M', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local '=MOD(ROW(),3)=0' $a)) }),
        @('expression range sum', 'N', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local "=SUM(($a`:$($a[0])$([int]$a.Substring(1) + 2)>5)*1)>1" $a)) }),
        @('expression number', 'N', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local "=$a" $a)) }),
        @('expression text', 'M', { param($r, $a) $r.FormatConditions.Add(2, [Type]::Missing, (Local '="TRUE"' $a)) }),
        @('top 3', 'N', { param($r, $a) $t = $r.FormatConditions.AddTop10(); $t.TopBottom = 1; $t.Rank = 3; $t.Percent = $false; $t }),
        @('bottom 3', 'N', { param($r, $a) $t = $r.FormatConditions.AddTop10(); $t.TopBottom = 0; $t.Rank = 3; $t.Percent = $false; $t }),
        @('top 25 percent', 'N', { param($r, $a) $t = $r.FormatConditions.AddTop10(); $t.TopBottom = 1; $t.Rank = 25; $t.Percent = $true; $t }),
        @('bottom 10 percent', 'N', { param($r, $a) $t = $r.FormatConditions.AddTop10(); $t.TopBottom = 0; $t.Rank = 10; $t.Percent = $true; $t }),
        @('top 2 mixed', 'M', { param($r, $a) $t = $r.FormatConditions.AddTop10(); $t.TopBottom = 1; $t.Rank = 2; $t.Percent = $false; $t }),
        @('above average', 'N', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 0; $t }),
        @('below average', 'N', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 1; $t }),
        @('equal or above average', 'M', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 2; $t }),
        @('equal or below average', 'N', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 3; $t }),
        @('one std dev above', 'N', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 4; $t.NumStdDev = 1; $t }),
        @('two std dev below', 'N', { param($r, $a) $t = $r.FormatConditions.AddAboveAverage(); $t.AboveBelow = 5; $t.NumStdDev = 2; $t }),
        @('duplicates', 'M', { param($r, $a) $t = $r.FormatConditions.AddUniqueValues(); $t.DupeUnique = 1; $t }),
        @('uniques', 'M', { param($r, $a) $t = $r.FormatConditions.AddUniqueValues(); $t.DupeUnique = 0; $t }),
        @('contains app', 'M', { param($r, $a) $r.FormatConditions.Add(9, [Type]::Missing, [Type]::Missing, [Type]::Missing, 'app', 0) }),
        @('contains 1', 'M', { param($r, $a) $r.FormatConditions.Add(9, [Type]::Missing, [Type]::Missing, [Type]::Missing, '1', 0) }),
        @('not contains app', 'M', { param($r, $a) $r.FormatConditions.Add(9, [Type]::Missing, [Type]::Missing, [Type]::Missing, 'app', 1) }),
        @('begins with a', 'M', { param($r, $a) $r.FormatConditions.Add(9, [Type]::Missing, [Type]::Missing, [Type]::Missing, 'a', 2) }),
        @('ends with e', 'M', { param($r, $a) $r.FormatConditions.Add(9, [Type]::Missing, [Type]::Missing, [Type]::Missing, 'e', 3) }),
        @('blanks', 'M', { param($r, $a) $r.FormatConditions.Add(10) }),
        @('no blanks', 'M', { param($r, $a) $r.FormatConditions.Add(13) }),
        @('errors', 'M', { param($r, $a) $r.FormatConditions.Add(16) }),
        @('no errors', 'M', { param($r, $a) $r.FormatConditions.Add(17) }),
        @('today', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 0) }),
        @('yesterday', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 1) }),
        @('last 7 days', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 2) }),
        @('this week', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 3) }),
        @('last week', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 4) }),
        @('last month', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 5) }),
        @('tomorrow', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 6) }),
        @('next week', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 7) }),
        @('next month', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 8) }),
        @('this month', 'D', { param($r, $a) $r.FormatConditions.Add(11, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, [Type]::Missing, 9) }),
        @('scale 2 min max', 'N', { param($r, $a) $s = $r.FormatConditions.AddColorScale(2); $s.ColorScaleCriteria.Item(1).FormatColor.Color = 16777215; $s.ColorScaleCriteria.Item(2).FormatColor.Color = 8109667; $s }),
        @('scale 3 default', 'N', { param($r, $a) $r.FormatConditions.AddColorScale(3) }),
        @('scale 3 numbers', 'N', { param($r, $a) $s = $r.FormatConditions.AddColorScale(3); $c = $s.ColorScaleCriteria; $c.Item(1).Type = 0; $c.Item(1).Value = 0; $c.Item(2).Type = 0; $c.Item(2).Value = 10; $c.Item(3).Type = 0; $c.Item(3).Value = 50; $s }),
        @('scale 3 percent', 'N', { param($r, $a) $s = $r.FormatConditions.AddColorScale(3); $c = $s.ColorScaleCriteria; $c.Item(1).Type = 3; $c.Item(1).Value = 10; $c.Item(2).Type = 3; $c.Item(2).Value = 50; $c.Item(3).Type = 3; $c.Item(3).Value = 90; $s }),
        @('scale 3 percentiles', 'N', { param($r, $a) $s = $r.FormatConditions.AddColorScale(3); $c = $s.ColorScaleCriteria; $c.Item(1).Type = 5; $c.Item(1).Value = 20; $c.Item(2).Type = 5; $c.Item(2).Value = 60; $c.Item(3).Type = 5; $c.Item(3).Value = 80; $s }),
        @('scale 2 formula', 'N', { param($r, $a) $s = $r.FormatConditions.AddColorScale(2); $c = $s.ColorScaleCriteria; $c.Item(1).Type = 4; $c.Item(1).Value = (Local '=AVERAGE($A$1:$A$1)+1' $a); $s }),
        @('scale 2 mixed', 'M', { param($r, $a) $r.FormatConditions.AddColorScale(2) }),
        @('icons 3 arrows', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(1); $i }),
        @('icons 3 arrows reversed', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(1); $i.ReverseOrder = $true; $i }),
        @('icons 4 arrows', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(9); $i }),
        @('icons 5 rating', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(15); $i }),
        @('icons 3 numbers', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(4); $c = $i.IconCriteria; $c.Item(2).Type = 0; $c.Item(2).Value = 12; $c.Item(2).Operator = 5; $c.Item(3).Type = 0; $c.Item(3).Value = 40; $c.Item(3).Operator = 7; $i }),
        @('icons 3 percentiles', 'N', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(4); $c = $i.IconCriteria; $c.Item(2).Type = 5; $c.Item(2).Value = 30; $c.Item(3).Type = 5; $c.Item(3).Value = 70; $i }),
        @('icons 3 mixed', 'M', { param($r, $a) $i = $r.FormatConditions.AddIconSetCondition(); $i.IconSet = $r.Parent.Parent.IconSets.Item(4); $i })
    )

    $column = 1
    $icons = [System.Collections.Generic.List[object]]::new()
    foreach ($spec in $cf) {
        $label, $set, $add = $spec
        Put $rules.Cells.Item(1, $column) $label
        Fill $rules $column $set
        $a = (Letter $column) + '2'
        $range = $rules.Range("$a`:$(Letter $column)21")
        $made = & $add $range $a
        if ($label -notmatch '^(scale|icons)') { $made.Interior.Color = 255 }
        if ($label -match '^icons') { $icons.Add(@($column, $label, $made.IconSet)) }
        $column++
    }

    # Two rules on one range: the first fills red over 10 and the second fills green and turns bold over 0; then the same
    # with stop if true on the first, and a third pair where both set the number format.
    foreach ($pair in @(@('two rules', $false), @('two rules stop', $true))) {
        $label, $stop = $pair
        Put $rules.Cells.Item(1, $column) $label
        Fill $rules $column 'N'
        $a = (Letter $column) + '2'
        $range = $rules.Range("$a`:$(Letter $column)21")
        $second = $range.FormatConditions.Add(1, 5, (Local '=0' $a))
        $second.Interior.Color = 65280; $second.Font.Bold = $true
        $first = $range.FormatConditions.Add(1, 5, (Local '=10' $a))
        $first.Interior.Color = 255; $first.Font.Italic = $true
        $first.SetFirstPriority()
        $first.StopIfTrue = $stop
        $second.StopIfTrue = $false
        $column++
    }
    Put $rules.Cells.Item(1, $column) 'number format'
    Fill $rules $column 'N'
    $a = (Letter $column) + '2'
    $nf = $rules.Range("$a`:$(Letter $column)21").FormatConditions.Add(1, 5, (Local '=10' $a))
    # The rule's number format is given in the PC's language too.
    $nf.NumberFormat = '0' + $excel.International(3) + '00'
    $nf.Font.Color = 255
    $column++
    Put $rules.Cells.Item(1, $column) 'two areas'
    Fill $rules $column 'N'
    Fill $rules ($column + 1) 'N'
    $a = (Letter $column) + '2'
    $both = $rules.Range("$a`:$(Letter $column)21;$(Letter ($column + 1))2:$(Letter ($column + 1))21")
    $twoAreas = $both.FormatConditions.Add(2, [Type]::Missing, (Local "=$a>$(Letter ($column + 1))`$5" $a))
    $twoAreas.Interior.Color = 255
    $column += 2
    $last = $column - 1

    # Data validation: label, set, type, operator, formulas, and whether blanks pass.
    $dv = @(
        @('whole between 1 and 10', 1, 1, '=1', '=10', $true),
        @('whole greater than 0 no blanks', 1, 5, '=0', $null, $false),
        @('decimal at least 2.5', 2, 7, '=2.5', $null, $true),
        @('decimal not between 0 and 1', 2, 2, '=0', '=1', $true),
        @('list a b c', 3, 1, '="a,b,c"', $null, $true),
        @('list range', 3, 1, '=$Z$1:$Z$3', $null, $true),
        @('list spaced', 3, 1, '=" a, 5,abcd"', $null, $true),
        @('list range with blank', 3, 1, '=$Z$1:$Z$4', $null, $true),
        @('list range with blank no blanks', 3, 1, '=$Z$1:$Z$4', $null, $false),
        @('list name', 3, 1, '=ListFull', $null, $true),
        @('list name with blank', 3, 1, '=ListBlank', $null, $true),
        @('list name with blank no blanks', 3, 1, '=ListBlank', $null, $false),
        @('date after', 4, 5, '=DATE(2023,1,1)', $null, $true),
        @('time before noon', 5, 6, '=TIME(12,0,0)', $null, $true),
        @('text length at most 3', 6, 8, '=3', $null, $true),
        @('text length equal 1', 6, 3, '=1', $null, $true),
        @('custom is number', 7, 1, '=ISNUMBER(A2)', $null, $true),
        @('custom is text no blanks', 7, 1, '=ISTEXT(A2)', $null, $false),
        @('any value', 0, 1, $null, $null, $true)
    )
    Put $valid.Range('Z1') 'a'; Put $valid.Range('Z2') 5; Put $valid.Range('Z3') 10
    # A list by a name whose cells include a blank one takes any value when blanks are ignored; by the cells themselves it does not.
    [void]$book.Names.Add('ListFull', "=Valid!`$Z`$1:`$Z`$3")
    [void]$book.Names.Add('ListBlank', "=Valid!`$Z`$1:`$Z`$4")
    $column = 1
    foreach ($spec in $dv) {
        $label, $type, $operator, $f1, $f2, $blank = $spec
        Put $valid.Cells.Item(1, $column) $label
        Fill $valid $column 'V'
        $a = (Letter $column) + '2'
        $range = $valid.Range("$a`:$(Letter $column)21")
        $f1 = if ($f1) { (Local ($f1 -replace 'A2', $a) $a) } else { [Type]::Missing }
        if ($type -eq 3 -and $f1 -match '^="(.*)"$') { $f1 = $Matches[1] -replace ',', [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ListSeparator }
        $f2 = if ($f2) { (Local $f2 $a) } else { [Type]::Missing }
        if ($type -eq 0) { $range.Validation.Add(0) } else { $range.Validation.Add($type, 1, $operator, $f1, $f2) }
        $range.Validation.IgnoreBlank = $blank
        $column++
    }

    foreach ($c in 1..$last) {
        $label = $rules.Cells.Item(1, $c).Value2
        if (-not $label) { $label = $rules.Cells.Item(1, $c - 1).Value2 + ' 2' }
        foreach ($r in 2..21) {
            $cell = $rules.Cells.Item($r, $c)
            $f = $cell.DisplayFormat
            $lines.Add("F`t$label`t$r`t$([int]$f.Interior.Color)`t$([int]$f.Font.Color)`t$([int][bool]$f.Font.Bold)`t$([int][bool]$f.Font.Italic)`t$($cell.Text)")
        }
    }
    foreach ($c in 1..($dv.Count)) {
        $label = $valid.Cells.Item(1, $c).Value2
        foreach ($r in 2..21) { $lines.Add("V`t$label`t$r`t$([int][bool]$valid.Cells.Item($r, $c).Validation.Value)") }
    }

    # Icons last: the icon filter hides the rows without the icon asked for.
    $book.SaveAs((Resolve-Path -LiteralPath (Split-Path -Parent $Output)).Path + '\' + [System.IO.Path]::GetFileNameWithoutExtension($Output) + '.xlsx', 51)
    $table = $rules.Range("A1:$(Letter $last)21")
    foreach ($icon in $icons) {
        $c, $label, $set = $icon
        $found = @{}
        for ($i = 1; $i -le $set.Count; $i++) {
            [void]$table.AutoFilter($c, $set.Item($i), 10)
            foreach ($r in 2..21) { if (-not $rules.Rows.Item($r).Hidden) { $found[$r] = $i } }
            $rules.AutoFilterMode = $false
        }
        foreach ($r in 2..21) { $lines.Add("I`t$label`t$r`t$(if ($found.ContainsKey($r)) { $found[$r] } else { 0 })") }
    }
    $book.Close($false)
    [System.IO.File]::WriteAllLines($Output, $lines)
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
