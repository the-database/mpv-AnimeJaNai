#requires -Version 7
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$pages = @(Get-ChildItem -LiteralPath $root -Filter '*.md' -File)
if ($pages.Count -eq 0) { throw 'No Markdown guides to render.' }
$style = @'
:root { color-scheme:light dark; font:16px/1.65 system-ui,Segoe UI,sans-serif; }
body { max-width:960px; margin:auto; padding:24px; background:#f8fafc; color:#182331; }
nav { display:flex; flex-wrap:wrap; gap:8px 22px; padding:10px 0 20px; border-bottom:1px solid #b5c3d2; }
a { color:#075fa1; text-underline-offset:3px; } h1,h2,h3 { line-height:1.25; scroll-margin-top:20px; }
h1 { font-size:2.1rem; } h2 { margin-top:2em; } pre { overflow:auto; padding:18px; border-radius:8px; background:#eaf0f7; }
code { font:0.9em/1.55 ui-monospace,Consolas,monospace; overflow-wrap:anywhere; } pre code { overflow-wrap:normal; }
table { display:block; overflow-x:auto; border-collapse:collapse; width:100%; } th,td { text-align:left; vertical-align:top; padding:10px; border:1px solid #b5c3d2; }
th { background:#eaf0f7; } li { margin:7px 0; } img { max-width:100%; } footer { border-top:1px solid #b5c3d2; margin-top:40px; padding-top:12px; }
.contents { margin:24px 0; padding:14px 20px; border:1px solid #b5c3d2; border-radius:8px; } summary { cursor:pointer; font-weight:600; }
.contents ul { columns:2; padding-left:22px; } .contents li { break-inside:avoid; } @media(max-width:600px) { .contents ul { columns:1; } }
@media(prefers-color-scheme:dark) { body { background:#161c24; color:#e2e9f2; } a { color:#8dceff; } pre,th { background:#222d3b; } }
@media print { nav,footer { display:none; } body { max-width:none; background:white; color:black; } pre,table { overflow:visible; } }
'@
foreach ($page in $pages) {
    $destination = Join-Path $root ($page.BaseName + '.html')
    if (Test-Path -LiteralPath $destination) { throw "Documentation output already exists: $destination" }
    $markdown = Get-Content -LiteralPath $page.FullName -Raw
    $titleLine = ($markdown -split '\r?\n' | Where-Object { $_ -match '^# ' } | Select-Object -First 1) -replace '^# ', ''
    $title = [Net.WebUtility]::HtmlEncode($titleLine)
    $body = (ConvertFrom-Markdown -LiteralPath $page.FullName).Html
    # Local guide links become offline HTML links. Source/example links retain
    # their real file extensions; remote URLs are never rewritten.
    $body = [regex]::Replace($body, 'href="([^"/:#]+\.md)(#[^"]*)?"', { param($match)
        $name = $match.Groups[1].Value
        if (Test-Path -LiteralPath (Join-Path $root $name)) { return 'href="' + $name.Substring(0, $name.Length - 3) + '.html' + $match.Groups[2].Value + '"' }
        return $match.Value
    })
    $sections = @([regex]::Matches($body, '<h2 id="([^"]+)">(.*?)</h2>'))
    $contents = ''
    if ($sections.Count -gt 1) {
        $links = foreach ($section in $sections) { '<li><a href="#' + $section.Groups[1].Value + '">' + $section.Groups[2].Value + '</a></li>' }
        $contents = '<details class="contents"><summary>On this page</summary><ul>' + ($links -join '') + '</ul></details>'
    }
    $html = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>$title · AJN addons</title><style>$style</style></head>
<body><nav aria-label="Addon documentation"><a href="USER-GUIDE.html">Using addons</a><a href="CREATOR-GUIDE.html">Create an addon</a><a href="CREATOR-RECIPES.html">Recipes</a><a href="API.html">API reference</a><a href="TROUBLESHOOTING.html">Troubleshooting</a></nav>$contents<main>$body</main><footer>AJN addon developer preview · API 1.8 · Offline documentation</footer></body></html>
"@
    [IO.File]::WriteAllText($destination, $html, [Text.UTF8Encoding]::new($false))
}
Write-Host "Rendered $($pages.Count) offline addon guides."
