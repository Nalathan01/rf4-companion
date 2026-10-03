$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root "version.txt") -Raw).Trim()
$dist = Join-Path $root "dist"
$stage = Join-Path $env:TEMP ("RF4Companion_build_" + [guid]::NewGuid().ToString("N"))
$top = Join-Path $stage "RF4 Companion Setup"
$app = Join-Path $top "app"

New-Item -ItemType Directory -Path (Join-Path $top "installer") -Force | Out-Null
New-Item -ItemType Directory -Path $app -Force | Out-Null

foreach ($f in @("RF4Companion.ps1", "Uninstall.ps1", "lang.json", "app.ico", "version.txt")) {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination (Join-Path $app $f)
}
foreach ($d in @("data", "Libs")) {
    & robocopy.exe (Join-Path $root $d) (Join-Path $app $d) /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $d" }
}
Copy-Item -LiteralPath (Join-Path $root "installer\Setup.ps1") -Destination (Join-Path $top "installer\Setup.ps1")
Copy-Item -LiteralPath (Join-Path $root "installer\Setup.cmd") -Destination (Join-Path $top "Setup.cmd")

New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist ("RF4Companion-Setup-" + $version + ".zip")
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $entryName = $file.FullName.Substring($stage.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host $zip
