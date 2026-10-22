param(
    [string]$OutFile = (Join-Path $PSScriptRoot "dist\RF4 Companion.exe"),
    [string]$Version = "2.0.0.0"
)

$ErrorActionPreference = "Stop"

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$buildDir = Join-Path $env:TEMP "RF4CompanionBuild"
if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
New-Item -ItemType Directory -Path $buildDir | Out-Null

$assemblyInfo = Join-Path $buildDir "AssemblyInfo.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("RF4 Companion")]
[assembly: AssemblyProduct("RF4 Companion")]
[assembly: AssemblyDescription("RF4 Companion")]
[assembly: AssemblyCompany("Nalathan")]
[assembly: AssemblyCopyright("Nalathan")]
[assembly: AssemblyVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
"@ | Set-Content -Path $assemblyInfo -Encoding UTF8

function Add-GzResource([string]$src, [string]$name) {
    $dst = Join-Path $buildDir ($name.Replace("/", "_") + ".gz")
    $in = [System.IO.File]::OpenRead($src)
    $out = [System.IO.File]::Create($dst)
    $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($gz)
    $gz.Dispose(); $out.Dispose(); $in.Dispose()
    "/resource:$dst,$name.gz"
}

$resources = @()
$resources += "/resource:$(Join-Path $PSScriptRoot 'ui\index.html'),ui/index.html"
$resources += Add-GzResource (Join-Path $PSScriptRoot "lang.json") "data/lang.json"
foreach ($f in @("gamedata.json", "itemnames.json", "itemcats.json", "dips.json", "marine_rigs.json", "sea_marks.json")) {
    $resources += Add-GzResource (Join-Path $PSScriptRoot "data\$f") "data/$f"
}
foreach ($m in Get-ChildItem (Join-Path $PSScriptRoot "data\maps") -Filter *.jpg) {
    $resources += "/resource:$($m.FullName),maps/$($m.Name)"
}

$references = @(
    "System.dll", "System.Core.dll", "System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "System.Security.dll",
    "System.Net.Http.dll", "System.IO.Compression.dll", (Join-Path $fw "System.Runtime.dll"), (Join-Path $fw "System.Runtime.InteropServices.WindowsRuntime.dll")
)
$wm = Join-Path $env:WINDIR "System32\WinMetadata"
foreach ($w in @("Windows.Foundation", "Windows.Graphics", "Windows.Media", "Windows.Storage", "Windows.Globalization")) { $references += (Join-Path $wm "$w.winmd") }

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
$cscArgs = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu", "/out:$OutFile", "/win32icon:$(Join-Path $PSScriptRoot 'app.ico')")
$cscArgs += $references | ForEach-Object { "/reference:$_" }
$cscArgs += $resources
$cscArgs += @(Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter *.cs | ForEach-Object { $_.FullName })
$cscArgs += $assemblyInfo
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Remove-Item $buildDir -Recurse -Force
"{0}  {1:N1} MB" -f $OutFile, ((Get-Item $OutFile).Length / 1MB)
