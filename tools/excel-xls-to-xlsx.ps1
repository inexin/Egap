<#
.SYNOPSIS
Opens each .xls file of a folder in Excel and saves it as .xlsx, for the XLS reader's gate.

.DESCRIPTION
Excel opens each file read-only, without updating links or running macros, and saves it with the values it shows as an
.xlsx of the same name in the output folder. A file that needs a password, or that Excel cannot open, is listed as failed
and skipped. Files already converted are skipped, so a stopped run can go on. The Excel version goes into
excel-version.txt in the output folder.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-xls-to-xlsx.ps1 <xls folder> <output folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path $Source).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AskToUpdateLinks = $false
    $excel.AutomationSecurity = 3   # msoAutomationSecurityForceDisable: no macros run
    Set-Content -Path (Join-Path $outputPath 'excel-version.txt') -Value ("Excel " + $excel.Version + " build " + $excel.Build)
    $done = 0
    $failed = 0
    foreach ($file in Get-ChildItem -Path $sourcePath -Filter '*.xls' | Sort-Object Name) {
        $target = Join-Path $outputPath ([System.IO.Path]::GetFileNameWithoutExtension($file.Name) + '.xlsx')
        if (Test-Path $target) { continue }
        $book = $null
        try {
            # UpdateLinks 0, ReadOnly, Format, and a password that is wrong for a protected file, so Excel fails rather than asks.
            $book = $excel.Workbooks.Open($file.FullName, 0, $true, [Type]::Missing, 'egap-gate-no-password')
            $book.SaveAs($target, 51)   # xlOpenXMLWorkbook
            $done++
        }
        catch {
            $failed++
            Write-Output ("FAILED`t" + $file.Name + "`t" + $_.Exception.Message.Split("`n")[0])
        }
        finally {
            if ($null -ne $book) { $book.Close($false) | Out-Null }
        }
    }

    Write-Output ("converted $done, failed $failed")
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
