# Opens each field fixture in Word, updates every field and saves Word's results next to it as <name>.word.docx, which
# the field tests then compare Egap with. Needs Microsoft Word on Windows. Run it from the repository root:
#
#     powershell -ExecutionPolicy Bypass -File tools/update-field-fixtures.ps1
#
# Word reads and writes formula numbers with the decimal symbol, digit grouping symbol and list separator of the Windows
# region settings, not the document's. So for each fixture the script sets those three in the current user's region
# settings (HKCU:\Control Panel\International) to the fixture's own (its w:decimalSymbol and w:listSeparator, the grouping
# symbol a comma for a decimal period and a period for a decimal comma), starts Word, and puts the settings back when it
# is done, also when it fails. Word windows already open keep their settings, but Word or Excel started by anything
# else during the run (about a minute) gets the fixture's symbols too.
#
# A fixture with document variables (w:docVars) gets any Word did not read from the file added through
# Document.Variables before the update, and is also saved as <name>.word.doc and <name>.word.rtf, so the DOC and RTF
# readers are checked against the variables as Word itself writes them.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folder = (Resolve-Path (Join-Path $PSScriptRoot '..\tests\files\fields')).Path
$fixtures = Get-ChildItem -Path $folder -Filter '*.docx' | Where-Object { $_.Name -notlike '*.word.docx' -and $_.Name -notlike '~$*' }
$international = 'HKCU:\Control Panel\International'
$names = 'sDecimal', 'sThousand', 'sList'
$main = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
$saved = @{}
foreach ($name in $names) {
    $saved[$name] = (Get-ItemProperty -Path $international -Name $name).$name
}

# The fixture's settings part as text.
function Get-Settings([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = New-Object System.IO.StreamReader($zip.GetEntry('word/settings.xml').Open())
        $xml = $reader.ReadToEnd()
        $reader.Close()
        return $xml
    }
    finally {
        $zip.Dispose()
    }
}

# The fixture's decimal symbol and list separator.
function Get-Symbols([string]$xml) {
    $decimal = if ($xml -match 'decimalSymbol w:val="(.)"') { $Matches[1] } else { '.' }
    $list = if ($xml -match 'listSeparator w:val="(.)"') { $Matches[1] } else { ',' }
    $group = if ($decimal -eq ',') { '.' } else { ',' }
    return @{ sDecimal = $decimal; sThousand = $group; sList = $list }
}

# The fixture's document variables, as name and value pairs.
function Get-Variables([string]$xml) {
    $settings = New-Object System.Xml.XmlDocument
    $settings.LoadXml($xml)
    $namespaces = New-Object System.Xml.XmlNamespaceManager($settings.NameTable)
    $namespaces.AddNamespace('w', $main)
    return @(foreach ($variable in $settings.SelectNodes('//w:docVars/w:docVar', $namespaces)) {
            @{ Name = $variable.GetAttribute('name', $main); Value = $variable.GetAttribute('val', $main) }
        })
}

try {
    foreach ($fixture in $fixtures) {
        $settingsXml = Get-Settings $fixture.FullName
        $symbols = Get-Symbols $settingsXml
        $variables = Get-Variables $settingsXml
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
                Write-Warning "$($fixture.Name): Word uses '$decimal' and '$list', not '$($symbols.sDecimal)' and '$($symbols.sList)'; skipped."
                continue
            }

            $target = Join-Path $folder ($fixture.BaseName + '.word.docx')
            $document = $word.Documents.Open($fixture.FullName, $false, $true)
            try {
                $present = @(foreach ($variable in $document.Variables) { $variable.Name })
                foreach ($variable in $variables) {
                    if ($present -notcontains $variable.Name) {
                        [void]$document.Variables.Add($variable.Name, $variable.Value)
                        Write-Host "$($fixture.Name): added variable $($variable.Name)"
                    }
                }

                [void]$document.Fields.Update()
                $document.Repaginate()
                # wdFormatDocumentDefault = 16
                $document.SaveAs2([string]$target, 16)
                Write-Host "$($fixture.Name) -> $([System.IO.Path]::GetFileName($target)) ($($document.Fields.Count) fields, '$decimal' and '$list')"
                if ($variables.Count -gt 0) {
                    # wdFormatDocument = 0, wdFormatRTF = 6
                    foreach ($format in @(@{ Extension = '.word.doc'; Code = 0 }, @{ Extension = '.word.rtf'; Code = 6 })) {
                        $copy = Join-Path $folder ($fixture.BaseName + $format.Extension)
                        $document.SaveAs2([string]$copy, $format.Code)
                        Write-Host "$($fixture.Name) -> $([System.IO.Path]::GetFileName($copy)) ($($document.Variables.Count) variables)"
                    }
                }
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
