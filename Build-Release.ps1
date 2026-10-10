$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root "version.txt") -Raw).Trim()
$dist = Join-Path $root "dist"
$stage = Join-Path $env:TEMP ("RF4Companion_build_" + [guid]::NewGuid().ToString("N"))
$app = Join-Path $stage "app"
New-Item -ItemType Directory -Path $app -Force | Out-Null

foreach ($f in @("RF4Companion.ps1", "lang.json", "app.ico", "version.txt")) {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination (Join-Path $app $f)
}
foreach ($d in @("data", "Libs")) {
    & robocopy.exe (Join-Path $root $d) (Join-Path $app $d) /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $d" }
}
$bin = Join-Path $stage "bin"
& (Join-Path $root "tools\Build-Native.ps1") -OutDir $bin | Out-Null
foreach ($f in @("RF4 Companion.exe", "RF4 Companion.exe.config", "RF4Comp.dll")) { Copy-Item -LiteralPath (Join-Path $bin $f) -Destination (Join-Path $app $f) }

$iscc = @((Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"), (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"), (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
& $iscc /Q "/DMyAppVersion=$version" "/DStageDir=$app" "/O$dist" (Join-Path $root "installer\RF4Companion.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Remove-Item -LiteralPath $stage -Recurse -Force

$exe = Join-Path $dist ("RF4Companion-Setup-" + $version + ".exe")
Write-Host $exe
