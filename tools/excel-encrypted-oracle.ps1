<#
.SYNOPSIS
Makes encrypted workbooks with Excel, and opens encrypted workbooks in Excel, for the encryption tests.

.DESCRIPTION
Make: fills a small workbook (text, numbers, a date, a formula, a bold cell and a second sheet) and saves it with the
password "Egap pässword 1" as XLSX (Excel's default, agile encryption), as XLS with RC4 CryptoAPI (Excel's default for
XLS), as XLS with the Office 97/2000 compatible RC4 encryption, and, when Excel offers it, as XLSX with the standard
encryption of the Enhanced RSA and AES provider. It also saves the workbook as XLS and XLSX with only a password to modify
(Excel encrypts those with its default password). It writes, for each file, the encryption Excel says it used to
encrypted.txt.

Open: opens each .xlsx and .xls file of a folder in Excel with that password and writes to opened.tsv the file name, the
sheets and the values of A1:C4 of the first sheet, or the error Excel gave.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-encrypted-oracle.ps1 -Mode Make -Folder tests/Egap.Spreadsheets.Tests/Oracle/encrypted
powershell -NoProfile -ExecutionPolicy Bypass -File tools/excel-encrypted-oracle.ps1 -Mode Open -Folder C:/scratch/egap-encrypted
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Make', 'Open')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Folder)
$ErrorActionPreference = 'Stop'
$password = 'Egap p' + [char]0x00E4 + 'ssword 1'
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$folderPath = (Resolve-Path $Folder).Path

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    if ($Mode -eq 'Make') {
        $lines = @("# Excel $($excel.Version) build $($excel.Build); password: Egap p\u00E4ssword 1")
        function Fresh() {
            $book = $excel.Workbooks.Add()
            $sheet = $book.Worksheets.Item(1)
            $sheet.Name = 'Data'
            $sheet.Cells.Item(1, 1).Value2 = 'Name'
            $sheet.Cells.Item(1, 2).Value2 = 'Amount'
            $sheet.Cells.Item(1, 3).Value2 = 'Date'
            $sheet.Cells.Item(1, 1).Font.Bold = $true
            $sheet.Cells.Item(2, 1).Value2 = 'Alpha'
            $sheet.Cells.Item(2, 2).Value2 = 12.5
            $sheet.Cells.Item(2, 3).Value2 = 45000
            $sheet.Cells.Item(2, 3).NumberFormat = 'yyyy-mm-dd'
            $sheet.Cells.Item(3, 1).Value2 = 'Beta ' + [char]0x00E6 + [char]0x00F8 + [char]0x00E5
            $sheet.Cells.Item(3, 2).Value2 = -3
            $sheet.Cells.Item(4, 1).Value2 = 'Total'
            $sheet.Cells.Item(4, 2).Formula = '=SUM(B2:B3)'
            $second = $book.Worksheets.Add([Type]::Missing, $sheet)
            $second.Name = 'Second'
            $second.Cells.Item(1, 1).Value2 = 'On the second sheet'
            # Long enough that the workbook stream spans several 1024-byte blocks.
            for ($r = 2; $r -le 400; $r++) { $second.Cells.Item($r, 1).Value2 = "Row $r of filler text"; $second.Cells.Item($r, 2).Value2 = $r * 1.5 }
            $sheet.Activate()
            return $book
        }

        function Save($name, $format, $provider, $algorithm, $bits, $modifyOnly) {
            $book = Fresh
            if ($provider) {
                try { $book.SetPasswordEncryptionOptions($provider, $algorithm, $bits, $true) }
                catch { $script:lines += "$name`tskipped: $($_.Exception.Message)"; $book.Close($false); return }
            }

            $path = Join-Path $folderPath $name
            if ($modifyOnly) { $book.SaveAs($path, $format, [Type]::Missing, $password) }
            else { $book.SaveAs($path, $format, $password) }
            $script:lines += "$name`t$($book.PasswordEncryptionProvider)`t$($book.PasswordEncryptionAlgorithm)`t$($book.PasswordEncryptionKeyLength)"
            $book.Close($false)
        }

        Save 'agile.xlsx' 51 $null $null 0 $false
        Save 'cryptoapi.xls' 56 $null $null 0 $false
        Save 'rc4.xls' 56 'Office 97/2000 Compatible' 'RC4' 40 $false
        Save 'standard.xlsx' 51 'Microsoft Enhanced RSA and AES Cryptographic Provider' 'AES 128' 128 $false
        Save 'modify-only.xls' 56 $null $null 0 $true
        Save 'modify-only.xlsx' 51 $null $null 0 $true
        Set-Content -Path (Join-Path $folderPath 'encrypted.txt') -Value $lines -Encoding UTF8
    }
    else {
        $lines = @()
        foreach ($file in Get-ChildItem -Path $folderPath -File | Where-Object { $_.Extension -in '.xlsx', '.xls' } | Sort-Object Name) {
            try {
                $book = $excel.Workbooks.Open($file.FullName, 0, $true, [Type]::Missing, $password)
                $sheets = @($book.Worksheets | ForEach-Object { $_.Name }) -join ','
                $cells = @()
                for ($r = 1; $r -le 4; $r++) { for ($c = 1; $c -le 3; $c++) { $cells += [string]$book.Worksheets.Item(1).Cells.Item($r, $c).Text } }
                $lines += "$($file.Name)`topened`t$sheets`t$($cells -join '|')"
                $book.Close($false)
            }
            catch { $lines += "$($file.Name)`tfailed`t$($_.Exception.Message)" }
        }

        Set-Content -Path (Join-Path $folderPath 'opened.tsv') -Value $lines -Encoding UTF8
    }
}
finally {
    $excel.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
}
