<#
.SYNOPSIS
Opens workbooks in Excel, refreshes their pivot tables and saves them as .xlsx, for the pivot refresh gate.

.DESCRIPTION
The list file names one workbook per line. Excel opens each read-only, without updating links or running macros,
refreshes every pivot cache that has pivot tables, and saves the workbook as an .xlsx of the same name in the output
folder. Each pivot table's refresh goes into pivots.tsv in the output folder: the file, the sheet's index, the pivot
table's index on the sheet, and ok or the error Excel gave. A file that needs a password, or that Excel cannot open, is
listed as failed and skipped. Files already done are skipped, so a stopped run can go on. The Excel version goes into
excel-version.txt in the output folder.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-refresh-pivots.ps1 <list file> <output folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$List,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path
$log = Join-Path $outputPath 'pivots.tsv'

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AskToUpdateLinks = $false
    $excel.AutomationSecurity = 3   # msoAutomationSecurityForceDisable: no macros run
    Set-Content -Path (Join-Path $outputPath 'excel-version.txt') -Value ("Excel " + $excel.Version + " build " + $excel.Build)
    $done = 0
    $failed = 0
    foreach ($line in Get-Content -Path $List) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $path = (Resolve-Path $line.Trim()).Path
        $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
        $target = Join-Path $outputPath ($name + '.xlsx')
        if (Test-Path $target) { continue }
        $book = $null
        try {
            # UpdateLinks 0, ReadOnly, Format, and a password that is wrong for a protected file, so Excel fails rather than asks.
            $book = $excel.Workbooks.Open($path, 0, $true, [Type]::Missing, 'egap-gate-no-password')
            $sheetIndex = 0
            foreach ($sheet in $book.Worksheets) {
                $sheetIndex++
                $count = $sheet.PivotTables().Count
                for ($i = 1; $i -le $count; $i++) {
                    $status = 'ok'
                    try {
                        $sheet.PivotTables($i).PivotCache().Refresh() | Out-Null
                    }
                    catch {
                        $status = 'failed: ' + $_.Exception.Message.Split("`n")[0]
                    }

                    Add-Content -Path $log -Value ($name + "`t" + $sheetIndex + "`t" + $i + "`t" + $status)
                }
            }

            $book.SaveAs($target, 51)   # xlOpenXMLWorkbook
            $done++
        }
        catch {
            $failed++
            Write-Output ("FAILED`t" + $name + "`t" + $_.Exception.Message.Split("`n")[0])
        }
        finally {
            if ($null -ne $book) { $book.Close($false) | Out-Null }
        }
    }

    Write-Output ("refreshed $done, failed $failed")
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
