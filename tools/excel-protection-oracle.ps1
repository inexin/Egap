<#
.SYNOPSIS
Checks sheet and workbook protection between Egap and Excel, for ProtectionTests.

.DESCRIPTION
Opens the files ProtectionTests.WritesTheFilesExcelChecks saves (protection.egap.xlsx, with SHA-512 hashes, and
protection.legacy.xlsx, with 16-bit hashes) and records in protection.excel.tsv whether Excel opens them protected, with the
allowed actions and editable ranges Egap set, refuses a wrong password and unlocks with the right one. Then it protects a
sheet, an editable range and a workbook in Excel and saves them as protection.excel.xlsx, which ProtectionTests unlocks in Egap.

.EXAMPLE
dotnet test tests/Egap.Spreadsheets.Tests -c Release --filter WritesTheFilesExcelChecks
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-protection-oracle.ps1 -Source tests/Egap.Spreadsheets.Tests/bin/Release/net10.0 -Output tests/Egap.Spreadsheets.Tests/Oracle
#>
param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path
$Output = (Resolve-Path $Output).Path
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$lines = [System.Collections.Generic.List[string]]::new()
function Unlock($target, $password) { try { $target.Unprotect($password); return 'unlocked' } catch { return 'refused' } }
function Add($key, $value) { $lines.Add("$key`t$value") }
try {
    $book = $excel.Workbooks.Open((Join-Path $Source 'protection.egap.xlsx'))
    $sheet = $book.Worksheets.Item(1)
    $p = $sheet.Protection
    Add 'egap sheet protected' $sheet.ProtectContents
    Add 'egap objects protected' $sheet.ProtectDrawingObjects
    Add 'egap allow sorting' $p.AllowSorting
    Add 'egap allow formatting columns' $p.AllowFormattingColumns
    Add 'egap allow formatting cells' $p.AllowFormattingCells
    Add 'egap allow inserting rows' $p.AllowInsertingRows
    Add 'egap C2 locked' $sheet.Range('C2').Locked
    foreach ($range in $p.AllowEditRanges) { Add "egap edit range $($range.Title)" $range.Range.Address($false, $false) }
    Add 'egap book structure' $book.ProtectStructure
    Add 'egap sheet wrong password' (Unlock $sheet 'egap-pass1')
    Add 'egap sheet right password' (Unlock $sheet 'Egap-pass1')
    Add 'egap book wrong password' (Unlock $book 'Book-pw1')
    Add 'egap book right password' (Unlock $book 'book-pw1')
    $book.Close($false)

    $book = $excel.Workbooks.Open((Join-Path $Source 'protection.legacy.xlsx'))
    $sheet = $book.Worksheets.Item(1)
    Add 'legacy sheet protected' $sheet.ProtectContents
    Add 'legacy sheet wrong password' (Unlock $sheet 'legacy9')
    Add 'legacy sheet right password' (Unlock $sheet 'legacy1')
    Add 'legacy book structure' $book.ProtectStructure
    Add 'legacy book wrong password' (Unlock $book 'legacy9')
    Add 'legacy book right password' (Unlock $book 'legacy2')
    $book.Close($false)

    # Password, objects, contents, scenarios, interface only, then the allowed actions in the dialog's order: format cells,
    # columns, rows, insert columns, rows, hyperlinks, delete columns, rows, sort, filter, pivot tables.
    $book = $excel.Workbooks.Add()
    $sheet = $book.Worksheets.Item(1)
    $sheet.Range('A1').Value2 = 'excel'
    [void]$sheet.Protection.AllowEditRanges.Add('XlRange', $sheet.Range('E2:E4'), 'Xl-range1')
    $sheet.Protect('Xl-pass1', $true, $true, $true, $false, $true, $false, $false, $false, $true, $false, $false, $false, $false, $true, $false)
    [void]$book.Protect('Xl-book1', $true, $false)
    $book.SaveAs((Join-Path $Output 'protection.excel.xlsx'), 51)
    $book.Close($false)
}
finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}

[System.IO.File]::WriteAllLines((Join-Path $Output 'protection.excel.tsv'), $lines)
