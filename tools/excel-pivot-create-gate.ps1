<#
.SYNOPSIS
Opens workbooks with pivot tables Egap made, checks Excel took them without repair, refreshes them and saves Excel's result.

.DESCRIPTION
For each .xlsx in the input folder Excel opens the workbook, without updating links or running macros. A workbook Excel
cannot open, or one it repairs, is a miss: Excel writes a repair log (error*.xml) to the temp folder when it repairs a file,
so a new log after the open marks the file as repaired. Excel then refreshes every pivot table on the sheets whose names
start with EgapPivot, saves the workbook as an .xlsx of the same name in the output folder and exports each of those
sheets to a PDF of the same name, the second and later with -2, -3 and so on. Each workbook goes into gate.tsv in the output folder: the file, opened or repaired or failed, the
pivot tables Excel found on each EgapPivot sheet, and ok or the first refresh error. Files already done are skipped, so a
stopped run can go on. The Excel version goes into excel-version.txt in the output folder.

Needs Excel on the PC. Run it as:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-pivot-create-gate.ps1 <input folder> <output folder>
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outputPath = (Resolve-Path $Output).Path
$inputPath = (Resolve-Path $Folder).Path
$log = Join-Path $outputPath 'gate.tsv'
$temp = [System.IO.Path]::GetTempPath()

function RepairLogs {
    return @(Get-ChildItem -Path $temp -Filter 'error*.xml' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}

$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AskToUpdateLinks = $false
    $excel.AutomationSecurity = 3   # msoAutomationSecurityForceDisable: no macros run
    Set-Content -Path (Join-Path $outputPath 'excel-version.txt') -Value ("Excel " + $excel.Version + " build " + $excel.Build)
    foreach ($file in Get-ChildItem -Path $inputPath -Filter '*.xlsx' -File | Sort-Object Name) {
        $name = $file.BaseName
        $target = Join-Path $outputPath ($name + '.xlsx')
        if (Test-Path $target) { continue }
        $before = RepairLogs
        $book = $null
        $state = 'opened'
        $counts = @()
        $status = 'ok'
        try {
            $book = $excel.Workbooks.Open($file.FullName, 0, $true)
            $after = RepairLogs
            if (@($after | Where-Object { $before -notcontains $_ }).Count -gt 0) { $state = 'repaired' }
            $sheets = @()
            foreach ($sheet in $book.Worksheets) {
                if (-not $sheet.Name.StartsWith('EgapPivot')) { continue }
                $sheets += $sheet
                $count = $sheet.PivotTables().Count
                $counts += ($sheet.Name + '=' + $count)
                for ($i = 1; $i -le $count; $i++) {
                    try {
                        $sheet.PivotTables($i).PivotCache().Refresh() | Out-Null
                    }
                    catch {
                        if ($status -eq 'ok') { $status = 'refresh failed: ' + $_.Exception.Message.Split("`n")[0] }
                    }
                }
            }

            $book.SaveAs($target, 51)
            $n = 0
            foreach ($sheet in $sheets) {
                $n++
                $pdf = if ($n -eq 1) { $name + '.pdf' } else { $name + '-' + $n + '.pdf' }
                $sheet.ExportAsFixedFormat(0, (Join-Path $outputPath $pdf))
            }
        }
        catch {
            $state = 'failed'
            $status = $_.Exception.Message.Split("`n")[0]
        }
        finally {
            if ($null -ne $book) { $book.Close($false) }
        }

        Add-Content -Path $log -Value ($name + "`t" + $state + "`t" + ($counts -join ' ') + "`t" + $status)
    }
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
