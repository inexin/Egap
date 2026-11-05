<#
.SYNOPSIS
Has Excel print the print oracle's workbooks to PDF and show theme colours with a tint, for PrintOracleTests.

.DESCRIPTION
The input folder holds the workbooks PrintOracleTests.WriteCasesWhenAsked writes (set EGAP_PRINT_ORACLE to the folder and
run that test), tints.xlsx, whose column A has a fill of a theme colour and tint in each row, and tints.txt, the
"theme<TAB>tint" of each row. Excel exports each workbook to PDF in the folder
(name.excel.pdf), and writes excel.txt, the header line with its version and printer; PrintOracleTests.ReadExcelPdfsWhenAsked
then reads the pages the PDFs print into print.excel.tsv. (The page breaks Excel's page break preview reports can differ
from the pages its PDF export prints, so the PDFs are the oracle.) For each row of tints.xlsx, tints.excel.tsv records the
colour Excel shows, with the theme's own colour, as theme, tint, shown RGB, base RGB.

Excel lays pages out for the default printer, so the header records it with Excel's version.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-print-oracle.ps1 <folder> <tints.excel.tsv>
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][string]$TintOutput
)

$ErrorActionPreference = 'Stop'
$folderPath = (Resolve-Path $Folder).Path

# A cell's ThemeColor is SpreadsheetML's theme index plus one (lt1, dk1, lt2, dk2, the accents, the links); the theme's
# colour scheme counts dk1 first.
$schemeIndex = @{ 0 = 2; 1 = 1; 2 = 4; 3 = 3 }

function Hex($bgr) {
    $value = [int]$bgr
    return ('{0:X2}{1:X2}{2:X2}' -f ($value -band 0xFF), (($value -shr 8) -band 0xFF), (($value -shr 16) -band 0xFF))
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $printer = $excel.ActivePrinter
    $header = "# Excel $($excel.Version) build $($excel.Build), printer $printer, written by tools/excel-print-oracle.ps1"
    [System.IO.File]::WriteAllText((Join-Path $folderPath 'excel.txt'), $header + "`n")
    foreach ($file in Get-ChildItem -Path $folderPath -Filter '*.xlsx' | Sort-Object Name) {
        $book = $excel.Workbooks.Open($file.FullName)
        try {
            $book.ExportAsFixedFormat(0, [System.IO.Path]::ChangeExtension($file.FullName, '.excel.pdf'))
        }
        finally {
            $book.Close($false)
        }
    }

    $tints = New-Object System.Collections.Generic.List[string]
    $book = $excel.Workbooks.Open((Join-Path $folderPath 'tints.xlsx'))
    try {
        $sheet = $book.Worksheets.Item(1)
        $tints.Add("# Excel $($excel.Version) build $($excel.Build): theme, tint, the colour Excel shows, the theme colour; written by tools/excel-print-oracle.ps1")
        $row = 1
        foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $folderPath 'tints.txt'))) {
            if ($line.Trim().Length -eq 0) { continue }
            $fields = $line.Split("`t")
            $theme = [int]$fields[0]
            $scheme = if ($schemeIndex.ContainsKey($theme)) { $schemeIndex[$theme] } else { $theme + 1 }
            $shown = Hex $sheet.Range("A$row").Interior.Color
            $base = Hex $book.Theme.ThemeColorScheme.Colors($scheme).RGB
            $tints.Add("$theme`t$($fields[1])`t$shown`t$base")
            $row++
        }
    }
    finally {
        $book.Close($false)
    }

    [System.IO.File]::WriteAllText($TintOutput, ($tints -join "`n") + "`n")
}
finally {
    $excel.Quit()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
