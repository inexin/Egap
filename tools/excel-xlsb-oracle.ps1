<#
.SYNOPSIS
Has Excel save workbooks as XLSB and as XLSX, for the XLSB reader's tests and gate.

.DESCRIPTION
  -Mode Convert -Source <folder> -Output <folder>
      Excel opens each .xls and .xlsx file of the source folder read-only, without updating links or running macros, and
      saves it twice from the same open: as an .xlsb and as an .xlsx of the same name in the output folder. The gate
      compares Egap's reading of the one with its reading of the other. A file that needs a password, or that Excel cannot
      open, is listed as failed and skipped. Files already converted are skipped, so a stopped run can go on.
  -Mode Make -Output <folder>
      Makes cells.xlsx with the workbook features the XLSB reader reads (values, styles, formulas of each kind, names,
      merges, rows and columns, hyperlinks, notes, data validation and conditional formats), then has Excel save it as
      cells.xlsb, and saves cells.xlsb again encrypted as encrypted.xlsb with the password "Egap xlsb 1". Document
      properties and personal information are removed.

The Excel version goes into excel-version.txt in the output folder. Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-xlsb-oracle.ps1 -Mode Convert -Source <folder> -Output <folder>
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-xlsb-oracle.ps1 -Mode Make -Output <folder>
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Convert', 'Make')][string]$Mode,
    [string]$Source,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path

# A value set through a variable: PowerShell's COM binder refuses a number set on a chained Range call.
function Set-Value($range, $value) {
    if ($value -is [string]) { $range.Value2 = [string]$value } elseif ($value -is [bool]) { $range.Value2 = [bool]$value } else { $range.Value2 = [double]$value }
}

# A number format set in Excel's own (US) form: on a PC with another locale, a plain set is read in the local form.
function Set-Format($range, [string]$format) {
    [void]$range.GetType().InvokeMember('NumberFormat', [System.Reflection.BindingFlags]::SetProperty, $null, $range, @($format), [System.Globalization.CultureInfo]::GetCultureInfo('en-US'))
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AskToUpdateLinks = $false
    $excel.AutomationSecurity = 3   # msoAutomationSecurityForceDisable: no macros run
    Set-Content -Path (Join-Path $outputPath 'excel-version.txt') -Value ("Excel " + $excel.Version + " build " + $excel.Build)

    if ($Mode -eq 'Convert') {
        $sourcePath = (Resolve-Path $Source).Path
        $done = 0
        $failed = 0
        foreach ($file in Get-ChildItem -Path $sourcePath | Where-Object { $_.Extension -in '.xls', '.xlsx' } | Sort-Object Name) {
            $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
            $binary = Join-Path $outputPath ($stem + '.xlsb')
            $open = Join-Path $outputPath ($stem + '.xlsx')
            if ((Test-Path $binary) -and (Test-Path $open)) { continue }
            $book = $null
            try {
                # UpdateLinks 0, ReadOnly, Format, and a password that is wrong for a protected file, so Excel fails rather than asks.
                $book = $excel.Workbooks.Open($file.FullName, 0, $true, [Type]::Missing, 'egap-gate-no-password')
                $book.SaveAs($binary, 50)   # xlExcel12
                $book.SaveAs($open, 51)     # xlOpenXMLWorkbook
                $done++
            }
            catch {
                $failed++
                Write-Output ("FAILED`t" + $file.Name + "`t" + $_.Exception.Message.Split("`n")[0])
            }
            finally {
                # A book whose save failed may refuse to close; Excel drops it when it quits.
                if ($null -ne $book) { try { $book.Close($false) | Out-Null } catch { } }
            }
        }

        Write-Output ("converted $done, failed $failed")
        return
    }

    $book = $excel.Workbooks.Add()
    try {
        while ($book.Worksheets.Count -lt 3) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
        $data = $book.Worksheets.Item(1)
        $data.Name = 'Data'
        $book.Worksheets.Item(2).Name = 'Other Sheet'
        $book.Worksheets.Item(3).Name = 'Hidden'
        $book.Worksheets.Item(3).Visible = 0

        # Values of each kind, with styles.
        Set-Value ($data.Range('A1')) ('Name')
        Set-Value ($data.Range('B1')) ('Amount')
        Set-Value ($data.Range('C1')) ('Date')
        Set-Value ($data.Range('D1')) ('Flag')
        $data.Range('A1:D1').Font.Bold = $true
        $data.Range('A1:D1').Interior.Color = 0xCCE5FF
        $data.Range('A1:D1').Borders.Item(9).LineStyle = 1   # xlEdgeBottom
        $names = @('North', 'South', 'East', 'West', 'North', 'Centre')
        for ($i = 0; $i -lt $names.Count; $i++) {
            $row = $i + 2
            Set-Value ($data.Range("A$row")) ($names[$i])
            Set-Value ($data.Range("B$row")) (1250.5 * ($i + 1) - 7)
            $data.Range("C$row").Formula = '=DATE(2026,' + ($i + 1) + ',' + (10 + $i) + ')'
            Set-Value ($data.Range("D$row")) (($i % 2 -eq 0))
        }
        Set-Format ($data.Range('B2:B7')) '#,##0.00;[Red]-#,##0.00'
        Set-Format ($data.Range('C2:C7')) 'yyyy-mm-dd'
        Set-Value ($data.Range('A8')) ('Rich')
        $data.Range('A8').Characters(1, 2).Font.Italic = $true
        $data.Range('A9').Formula = '=1/0'
        Set-Value ($data.Range('A10')) (12345678901234)
        Set-Value ($data.Range('A11')) (-0.000125)
        Set-Value ($data.Range('A12')) ("Two`nlines")
        $data.Range('A12').WrapText = $true
        Set-Value ($data.Range('A13')) (42)
        $data.Range('A13').HorizontalAlignment = -4108   # xlCenter
        $data.Range('A13').Font.Name = 'Courier New'
        $data.Range('A13').Font.Size = 14
        $data.Range('A13').Font.Color = 0x0000C0

        # Formulas: references of each kind, functions, names, shared and array formulas, other sheets.
        $data.Range('F2').Formula = '=SUM(B2:B7)'
        $data.Range('F3').Formula = '=AVERAGE($B$2:$B$7)*2'
        $data.Range('F4').Formula = '=IF(D2,"yes","no")&" "&TEXT(C2,"mmm")'
        $data.Range('F5').Formula = '=''Other Sheet''!A1+Hidden!B2'
        $data.Range('F6').Formula = '=SUM(Data:Hidden!A1)'
        $data.Range('F7').Formula = '=VLOOKUP("East",A2:B7,2,FALSE)'
        $data.Range('F8').Formula = '=Total*Rate'
        $data.Range('F9').Formula = '=ROUND(PI(),3)-{1,2;3,4}'
        $data.Range('F10').Formula = '=IFERROR(INDEX(B:B,MATCH(MAX(B2:B7),B2:B7,0)),-1)'
        $data.Range('F11').Formula = '=SUMPRODUCT((A2:A7="North")*B2:B7)'
        $data.Range('F12').Formula = '=COUNTIFS(A2:A7,"<>West",B2:B7,">1000")'
        $data.Range('F13').Formula = '=A2 B2:B3'
        $data.Range('F14').Formula = '=Local'
        $data.Range('F15').Formula = '=ISBLANK(G1)+N(TRUE)-#N/A'
        $data.Range('F16').Formula = '=TODAY()-DATE(2000,1,1)>0'
        $data.Range('G2:G7').Formula = '=B2*2'
        $data.Range('H2:H4').FormulaArray = '=B2:B4*10'
        $data.Range('I2').Formula = '=R[1]C[-7]'
        $data.Range('I2').FormulaR1C1 = '=R[1]C[-7]&"!"'
        $data.Range('J2').Formula2 = '=SEQUENCE(3)'
        Set-Value ($book.Worksheets.Item(2).Range('A1')) (5)
        Set-Value ($book.Worksheets.Item(3).Range('A1')) (7)
        Set-Value ($book.Worksheets.Item(3).Range('B2')) (3)

        # An Excel table with a structured reference.
        $other = $book.Worksheets.Item(2)
        Set-Value ($other.Range('C1')) ('Item')
        Set-Value ($other.Range('D1')) ('Qty')
        for ($i = 2; $i -le 4; $i++) { Set-Value ($other.Range("C$i")) ('Item ' + $i); Set-Value ($other.Range("D$i")) ($i * 3) }
        $table = $other.ListObjects.Add(1, $other.Range('C1:D4'), [Type]::Missing, 1)
        $table.Name = 'Stock'
        $other.Range('F1').Formula = '=SUM(Stock[Qty])'
        $other.Range('F2').Formula = '=COUNTA(Stock[[#All],[Item]])'

        # Names: workbook and sheet scope, hidden, a constant, the print area.
        [void]$book.Names.Add('Total', '=Data!$F$2')
        [void]$book.Names.Add('Rate', '=1/4')   # no decimal point: Names.Add reads the locale's
        [void]$data.Names.Add('Local', '=Data!$B$2:$B$4')
        $hidden = $book.Names.Add('Secret', '=Hidden!$A$1')
        $hidden.Visible = $false
        $data.PageSetup.PrintArea = '$A$1:$F$16'

        # Merges, rows and columns.
        $data.Range('L2:N3').Merge()
        Set-Value ($data.Range('L2')) ('Merged')
        $data.Columns.Item(1).ColumnWidth = 18.5
        $data.Columns.Item(5).Hidden = $true
        $data.Rows.Item(5).RowHeight = 30
        $data.Rows.Item(14).Hidden = $true
        $data.Range('A20:A22').Rows.Group() | Out-Null

        # Hyperlinks and notes.
        [void]$data.Hyperlinks.Add($data.Range('K5'), 'https://example.com/report', [Type]::Missing, 'Example tip', 'Report')
        [void]$data.Hyperlinks.Add($data.Range('K6'), '', 'Data!A1', [Type]::Missing, 'Top')
        [void]$data.Range('B3').AddComment('A note')
        [void]$data.Range('C4').AddComment("Two`nlines")

        # Data validation and conditional formats.
        $data.Range('D10:D12').Validation.Add(3, 1, 1, '=$A$2:$A$4')   # xlValidateList, from cells: a list's separator is the locale's
        $data.Range('E10').Validation.Add(1, 1, 1, '1', '10')            # whole number between 1 and 10
        [void]$data.Range('B2:B7').FormatConditions.Add(1, 5, '=3000')   # cell value greater than 3000
        $data.Range('B2:B7').FormatConditions.Item(1).Font.Bold = $true
        [void]$data.Range('G2:G7').FormatConditions.AddColorScale(2)
        [void]$data.Range('A2:A7').FormatConditions.Add(2, [Type]::Missing, '=$D2')   # expression
        $data.Range('A2:A7').FormatConditions.Item(1).Interior.Color = 0x99FF99

        $data.Activate()
        $data.Range('A1').Select()
        $book.RemovePersonalInformation = $true
        $open = Join-Path $outputPath 'cells.xlsx'
        $binary = Join-Path $outputPath 'cells.xlsb'
        $book.SaveAs($open, 51)
        $book.SaveAs($binary, 50)
        $book.SaveAs((Join-Path $outputPath 'encrypted.xlsb'), 50, 'Egap xlsb 1')
    }
    finally {
        $book.Close($false) | Out-Null
    }

    Write-Output 'made cells.xlsx, cells.xlsb, encrypted.xlsb'
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
