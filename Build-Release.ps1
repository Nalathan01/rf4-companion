param([switch]$Private)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root "version.txt") -Raw).Trim()
$dist = Join-Path $root $(if ($Private) { "dist\private" } else { "dist" })
$stage = Join-Path $env:TEMP ("RF4Companion_build_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$exe = Join-Path $stage "RF4 Companion.exe"
& (Join-Path $root "build.ps1") -OutFile $exe -Version "$version.0" -Private:$Private
if (-not (Test-Path -LiteralPath $exe)) { throw "Build failed" }

$iscc = @((Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"), (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"), (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DMyAppVersion=$version" "/DStageDir=$stage" "/O$dist" (Join-Path $root "installer\RF4Companion.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Copy-Item -LiteralPath $exe -Destination (Join-Path $dist "RF4.Companion.exe") -Force
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host (Join-Path $dist ("RF4Companion-Setup-" + $version + ".exe"))
Write-Host (Join-Path $dist "RF4.Companion.exe")
