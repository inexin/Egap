# Opens each field fixture in Word, updates every field and saves Word's results next to it as <name>.word.docx, which
# FormulaFieldTests then compares Egap with. Needs Microsoft Word on Windows. Run it from the repository root:
#
#     powershell -ExecutionPolicy Bypass -File tools/update-field-fixtures.ps1
#
# Word reads and writes formula numbers with the decimal symbol, digit grouping symbol and list separator of the Windows
# region settings, not the document's. So for each fixture the script sets those three in the current user's region
# settings (HKCU:\Control Panel\International) to the fixture's own (its w:decimalSymbol and w:listSeparator, the grouping
# symbol a comma for a decimal period and a period for a decimal comma), starts Word, and puts the settings back when it
# is done, also when it fails. Close Word before running it.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folder = (Resolve-Path (Join-Path $PSScriptRoot '..\tests\files\fields')).Path
$fixtures = Get-ChildItem -Path $folder -Filter '*.docx' | Where-Object { $_.Name -notlike '*.word.docx' -and $_.Name -notlike '~$*' }
$international = 'HKCU:\Control Panel\International'
$names = 'sDecimal', 'sThousand', 'sList'
$saved = @{}
foreach ($name in $names) {
    $saved[$name] = (Get-ItemProperty -Path $international -Name $name).$name
}

# The fixture's decimal symbol and list separator, from its settings part.
function Get-Symbols([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = New-Object System.IO.StreamReader($zip.GetEntry('word/settings.xml').Open())
        $xml = $reader.ReadToEnd()
        $reader.Close()
    }
    finally {
        $zip.Dispose()
    }

    $decimal = if ($xml -match 'decimalSymbol w:val="(.)"') { $Matches[1] } else { '.' }
    $list = if ($xml -match 'listSeparator w:val="(.)"') { $Matches[1] } else { ',' }
    $group = if ($decimal -eq ',') { '.' } else { ',' }
    return @{ sDecimal = $decimal; sThousand = $group; sList = $list }
}

try {
    foreach ($fixture in $fixtures) {
        $symbols = Get-Symbols $fixture.FullName
        foreach ($name in $names) {
            Set-ItemProperty -Path $international -Name $name -Value $symbols[$name]
        }

        $word = New-Object -ComObject Word.Application
        try {
            $word.Visible = $false
            $word.DisplayAlerts = 0
            # wdDecimalSeparator = 18, wdListSeparator = 17
            $decimal = $word.International(18)
            $list = $word.International(17)
            if ($decimal -ne $symbols.sDecimal -or $list -ne $symbols.sList) {
                Write-Warning "$($fixture.Name): Word uses '$decimal' and '$list', not '$($symbols.sDecimal)' and '$($symbols.sList)'; skipped. Close every Word window and run again."
                continue
            }

            $target = Join-Path $folder ($fixture.BaseName + '.word.docx')
            $document = $word.Documents.Open($fixture.FullName, $false, $true)
            try {
                [void]$document.Fields.Update()
                $document.Repaginate()
                # wdFormatDocumentDefault = 16
                $document.SaveAs2([string]$target, 16)
                Write-Host "$($fixture.Name) -> $([System.IO.Path]::GetFileName($target)) ($($document.Fields.Count) fields, '$decimal' and '$list')"
            }
            finally {
                $document.Close()
                [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($document)
            }
        }
        finally {
            $word.Quit()
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($word)
        }
    }
}
finally {
    foreach ($name in $names) {
        Set-ItemProperty -Path $international -Name $name -Value $saved[$name]
    }

    Write-Host "Region settings put back: decimal '$($saved.sDecimal)', grouping '$($saved.sThousand)', list '$($saved.sList)'"
}
