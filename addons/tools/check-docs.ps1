#requires -Version 7
param([string]$Directory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$checked = 0
foreach ($page in Get-ChildItem -LiteralPath $root -Filter '*.md' -File) {
    $content = Get-Content -LiteralPath $page.FullName -Raw
    foreach ($match in [regex]::Matches($content, '\[[^\]\r\n]+\]\(([^)\s]+)\)')) {
        $link = $match.Groups[1].Value
        if ($link -match '^[a-z]+:' -or $link.StartsWith('#')) { continue }
        $file = [Uri]::UnescapeDataString(($link -split '#', 2)[0])
        if (-not (Test-Path -LiteralPath (Join-Path $root $file))) { throw "Broken documentation link in $($page.Name): $link" }
        $checked++
    }
}
Write-Host "PASS $checked local documentation links."
