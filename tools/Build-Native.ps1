param([string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "bin"))
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
$wpf = Join-Path $fw "WPF"

function Get-TypeSources([string]$file) {
    $tokens = $null; $errs = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errs)
    $cmds = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq "Add-Type" }, $true)
    $out = @()
    foreach ($c in $cmds) {
        $els = $c.CommandElements
        for ($i = 0; $i -lt $els.Count; $i++) {
            if ($els[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $els[$i].ParameterName -eq "TypeDefinition") {
                $v = $els[$i + 1]
                if ($v -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and $v.NestedExpressions.Count) { throw "Expandable C# block at line $($v.Extent.StartLineNumber)" }
                $out += [string]$v.Value
            }
        }
    }
    $out
}

$srcDir = Join-Path $env:TEMP ("rf4native_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $srcDir | Out-Null
$n = 0
foreach ($s in (Get-TypeSources (Join-Path $root "RF4Companion.ps1"))) {
    $n++
    [System.IO.File]::WriteAllText((Join-Path $srcDir ("t$n.cs")), $s, (New-Object System.Text.UTF8Encoding $false))
}

$refs = @("System.dll", "System.Core.dll", "System.Drawing.dll", "System.Net.Http.dll", "System.Xaml.dll", "System.Web.Extensions.dll", "System.Runtime.dll",
    (Join-Path $wpf "PresentationFramework.dll"), (Join-Path $wpf "PresentationCore.dll"), (Join-Path $wpf "WindowsBase.dll"))
$dll = Join-Path $OutDir "RF4Comp.dll"
& $csc /nologo /target:library /optimize+ /out:$dll ($refs | ForEach-Object { "/reference:$_" }) (Join-Path $srcDir "*.cs")
if ($LASTEXITCODE -ne 0) { throw "RF4Comp.dll build failed" }
Remove-Item -Recurse -Force $srcDir

$sma = (Get-ChildItem (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation") -Recurse -Filter System.Management.Automation.dll | Select-Object -First 1).FullName
$cfg = '<?xml version="1.0" encoding="utf-8"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><runtime><loadFromRemoteSources enabled="true"/></runtime></configuration>'
foreach ($t in @(@{ Name = "RF4 Companion.exe"; Def = "APP" })) {
    $exe = Join-Path $OutDir $t.Name
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$(Join-Path $root 'app.ico')" "/define:SCRIPT_$($t.Def)" /out:$exe "/reference:$sma" /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot "Launcher.cs")
    if ($LASTEXITCODE -ne 0) { throw "$($t.Name) build failed" }
    [System.IO.File]::WriteAllText($exe + ".config", $cfg, (New-Object System.Text.UTF8Encoding $false))
}
"ok: $n C# blocks, $dll"
