# Opens each field fixture in Word, updates every field and saves Word's results next to it as <name>.word.docx, which
# FormulaFieldTests then compares Egap with. Needs Microsoft Word on Windows. Run it from the repository root:
#
#     powershell -ExecutionPolicy Bypass -File tools/update-field-fixtures.ps1
#
# Word evaluates formulas with the decimal symbol and list separator of Windows' region settings; the script prints them,
# and the fixtures expect a period and a comma (formulas-comma.docx a comma and a semicolon).

$ErrorActionPreference = 'Stop'
$folder = Join-Path $PSScriptRoot '..\tests\files\fields' | Resolve-Path
$fixtures = Get-ChildItem -Path $folder -Filter '*.docx' | Where-Object { $_.Name -notlike '*.word.docx' -and $_.Name -notlike '~$*' }

$word = New-Object -ComObject Word.Application
try {
    $word.Visible = $false
    $word.DisplayAlerts = 0
    # wdDecimalSeparator = 18, wdListSeparator = 17
    Write-Host ("Word's decimal symbol '{0}', list separator '{1}'" -f $word.International(18), $word.International(17))
    foreach ($fixture in $fixtures) {
        $target = Join-Path $folder ($fixture.BaseName + '.word.docx')
        $document = $word.Documents.Open($fixture.FullName, $false, $true)
        try {
            [void]$document.Fields.Update()
            $document.Repaginate()
            # wdFormatDocumentDefault = 16
            $document.SaveAs2([string]$target, 16)
            Write-Host "$($fixture.Name) -> $([System.IO.Path]::GetFileName($target)) ($($document.Fields.Count) fields)"
        }
        finally {
            $document.Close($false)
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($document)
        }
    }
}
finally {
    $word.Quit($false)
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($word)
}
