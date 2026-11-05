<#
.SYNOPSIS
Builds workbooks in Excel and saves them with the values Excel calculated, for WorkbookOracleTests.

.DESCRIPTION
Writes two workbooks into the given folder:

  functions.xlsx    the cases of the function oracle (tests/Egap.Spreadsheets.Tests/Oracle/functions.txt), inputs in
                    columns A to J and each formula in column L at row 20 * i + 1, as tools/excel-function-oracle.ps1
                    lays them out.
  calculation.xlsx  a workbook of formulas that read each other: shared formulas filled down, a running total, lookups
                    over another sheet, a defined name, a table and its structured references, an array formula, and a
                    summary sheet reading the others.

Excel calculates each one fully and saves it, so every formula cell carries Excel's result. The tests load the files,
calculate them again with Egap and compare. The Excel version that saved them goes into workbooks.excel.txt beside them.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-workbook-oracle.ps1 <cases.txt> <folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$Cases,
    [Parameter(Mandatory = $true)][string]$Folder
)

$ErrorActionPreference = 'Stop'
$errorFormulas = @{ '#NULL!' = '=A1 B1'; '#DIV/0!' = '=1/0'; '#VALUE!' = '="a"+1'; '#REF!' = '=#REF!'; '#NAME?' = '=#NAME?';
    '#NUM!' = '=SQRT(-1)'; '#N/A' = '=NA()' }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$folderPath = (Resolve-Path $Folder).Path

function Save($book, $name) {
    $book.Application.CalculateFull()
    $path = Join-Path $folderPath $name
    if (Test-Path $path) { Remove-Item $path }
    $book.SaveAs($path, 51)   # xlOpenXMLWorkbook
    $book.Close($false)
    ClearAuthor $path
}

# Excel writes the PC's user name as the author; the files in the repository name nobody.
function ClearAuthor($path) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($path, 'Update')
    try {
        $entry = $zip.GetEntry('docProps/core.xml')
        $reader = New-Object System.IO.StreamReader($entry.Open())
        $xml = $reader.ReadToEnd()
        $reader.Close()
        $xml = $xml -replace '<dc:creator>[^<]*</dc:creator>', '<dc:creator></dc:creator>' -replace '<cp:lastModifiedBy>[^<]*</cp:lastModifiedBy>', '<cp:lastModifiedBy></cp:lastModifiedBy>'
        $entry.Delete()
        $writer = New-Object System.IO.StreamWriter($zip.CreateEntry('docProps/core.xml').Open(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.Write($xml)
        $writer.Close()
    }
    finally { $zip.Dispose() }
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false

    # The function cases.
    $book = $excel.Workbooks.Add()
    $sheet = $book.Worksheets.Item(1)
    $sheet.Name = 'Sheet1'
    $excel.Calculation = -4135
    $i = 0
    foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path $Cases))) {
        $text = $line.Trim()
        if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
        if ($text.StartsWith('@')) {
            $space = $text.IndexOf(' ')
            $cell = $sheet.Range($text.Substring(1, $space - 1))
            $value = $text.Substring($space + 1)
            if ($value.StartsWith('"')) {
                $content = $value.Substring(1, $value.Length - 2)
                if ($content.Length -eq 0) { $cell.Formula = '=""' } else { $cell.Value2 = [string]("'" + $content) }
            }
            elseif ($value -eq 'TRUE' -or $value -eq 'FALSE') { $cell.Formula = $value }
            elseif ($value.StartsWith('#')) { $cell.Formula = $errorFormulas[$value] }
            else { $cell.Value2 = [double]::Parse($value, $invariant) }
            continue
        }
        try { $sheet.Range("L$(20 * $i + 1)").Formula = '=' + $text } catch { }
        $i++
    }
    Save $book 'functions.xlsx'

    # Formulas that read each other.
    $book = $excel.Workbooks.Add()
    while ($book.Worksheets.Count -lt 3) { [void]$book.Worksheets.Add([Type]::Missing, $book.Worksheets.Item($book.Worksheets.Count)) }
    $data = $book.Worksheets.Item(1); $data.Name = 'Data'
    $calc = $book.Worksheets.Item(2); $calc.Name = 'Calc'
    $summary = $book.Worksheets.Item(3); $summary.Name = 'Summary'
    $excel.Calculation = -4135

    $data.Range('A1').Formula = 'N'; $data.Range('B1').Formula = 'Value'; $data.Range('C1').Formula = 'Cat'; $data.Range('D1').Formula = 'When'
    $categories = 'x', 'y', 'z', 'x', 'w'
    for ($r = 2; $r -le 201; $r++) {
        $n = $r - 1
        $data.Range("A$r").Value2 = [double]$n
        $data.Range("B$r").Value2 = [double](($n * 37) % 101) + 0.25 * ($n % 4)
        $data.Range("C$r").Formula = [string]$categories[$n % 5]
        $data.Range("D$r").Value2 = [double](44927 + 3 * $n)
    }
    $table = $data.ListObjects.Add(1, $data.Range('A1:D201'), [Type]::Missing, 1)
    $table.Name = 'Sales'
    [void]$book.Names.Add('Rate', '=1/4')   # RefersTo is read in the PC's locale: no decimal point
    [void]$book.Names.Add('Values', '=Data!$B$2:$B$201')

    # Shared formulas: one formula filled down a column.
    $calc.Range('A1').Formula = '=Data!A2*2+Data!B2'
    [void]$calc.Range('A1:A200').FillDown()
    $calc.Range('B1').Formula = '=SUM(A$1:A1)'
    [void]$calc.Range('B1:B200').FillDown()
    $calc.Range('C1').Formula = '=VLOOKUP(Data!A2,Data!$A$2:$C$201,3,FALSE)'
    [void]$calc.Range('C1:C200').FillDown()
    $calc.Range('D1').Formula = '=A1*Rate+IF(C1="x",1,0)'
    [void]$calc.Range('D1:D200').FillDown()
    $calc.Range('E1').Formula = '=ROUND(B1/ROW(),2)&" "&UPPER(C1)'
    [void]$calc.Range('E1:E200').FillDown()
    $calc.Range('F1').Formula = '=IFERROR(MATCH(A1,Data!$B$2:$B$201,0),-1)'
    [void]$calc.Range('F1:F200').FillDown()
    $calc.Range('G1').Formula = '=YEAR(Data!D2)&"-"&MONTH(Data!D2)&"/"&WEEKDAY(Data!D2,2)'
    [void]$calc.Range('G1:G200').FillDown()
    $calc.Range('H1').Formula = '=COUNTIF(C$1:C1,C1)'
    [void]$calc.Range('H1:H200').FillDown()
    $calc.Range('I1:I3').FormulaArray = '=Data!B2:B4*10'
    $calc.Range('J1').FormulaArray = '=SUM((Data!C2:C201="y")*Data!B2:B201)'

    $summary.Range('A1').Formula = '=Calc!B200'
    $summary.Range('A2').Formula = '=AVERAGE(Calc!A1:A200)'
    $summary.Range('A3').Formula = '=COUNTIFS(Data!C2:C201,"y",Data!B2:B201,">50")'
    $summary.Range('A4').Formula = '=INDEX(Calc!A1:E200,MATCH(50,Data!A2:A201,0),5)'
    $summary.Range('A5').Formula = '=SUM(Sales[Value])'
    $summary.Range('A6').Formula = '=SUMIFS(Sales[Value],Sales[Cat],"x",Sales[When],">="&DATE(2023,6,1))'
    $summary.Range('A7').Formula = '=MAX(Values)-MIN(Values)'
    $summary.Range('A8').Formula = '=SUMPRODUCT((Data!C2:C201="z")*Data!B2:B201)'
    $summary.Range('A9').Formula = '=STDEV(Calc!D1:D200)'
    $summary.Range('A10').Formula = '=Calc!J1+SUM(Calc!I1:I3)'
    $summary.Range('A11').Formula = '=TEXTJOIN(",",TRUE,Calc!C1:C5)'
    $summary.Range('A12').Formula = '=LOOKUP(2,1/(Data!C2:C201="w"),Data!A2:A201)'
    $summary.Range('A13').Formula = '=MEDIAN(Calc!H1:H200)+LARGE(Calc!A1:A200,3)'
    $summary.Range('A14').Formula = '=EOMONTH(MAX(Sales[When]),1)'
    $summary.Range('A15').Formula = '=SUBTOTAL(9,Data!B2:B201)+SUBTOTAL(1,Calc!A1:A200)'
    Save $book 'calculation.xlsx'
    [System.IO.File]::WriteAllText((Join-Path $folderPath 'workbooks.excel.txt'),
        "functions.xlsx and calculation.xlsx saved by Excel $($excel.Version) build $($excel.Build), locale $([System.Globalization.CultureInfo]::CurrentCulture.Name), with tools/excel-workbook-oracle.ps1`n")
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
