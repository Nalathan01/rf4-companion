param(
    [string]$InstallDir,
    [switch]$Silent,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml

$appName = "RF4 Companion"
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RF4Companion"
$userDir = Join-Path $env:APPDATA "RF4Companion"
$shortcutName = "RF4 Companion.lnk"

if (-not $InstallDir) {
    $InstallDir = $PSScriptRoot
    $tmp = Join-Path $env:TEMP ("RF4Companion_uninstall_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $tmp "Uninstall.ps1")
    Copy-Item -LiteralPath (Join-Path $InstallDir "lang.json") -Destination (Join-Path $tmp "lang.json") -ErrorAction SilentlyContinue
    $argList = @("-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", ('"' + (Join-Path $tmp "Uninstall.ps1") + '"'), "-InstallDir", ('"' + $InstallDir + '"'))
    if ($Silent) { $argList += "-Silent" }
    if ($KeepData) { $argList += "-KeepData" }
    if ($Silent) {
        Start-Process -FilePath "powershell.exe" -ArgumentList $argList -WindowStyle Hidden -Wait
    } else {
        Start-Process -FilePath "powershell.exe" -ArgumentList $argList -WindowStyle Hidden
    }
    exit 0
}

$langs = $null
$langFile = Join-Path $PSScriptRoot "lang.json"
if (Test-Path -LiteralPath $langFile) { $langs = Get-Content -LiteralPath $langFile -Raw -Encoding UTF8 | ConvertFrom-Json }

$script:lang = "en"
try {
    $u = Get-Content -LiteralPath (Join-Path $userDir "user.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($u.lang) { $script:lang = [string]$u.lang }
} catch { }

function T([string]$key, [string]$fallback) {
    if ($langs) {
        $v = $langs.($script:lang).$key
        if ($v) { return $v }
        $v = $langs.en.$key
        if ($v) { return $v }
    }
    return $fallback
}

function Show-Message([string]$text, [string]$icon) {
    [System.Windows.MessageBox]::Show($text, $appName, [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]$icon) | Out-Null
}

function Test-AppRunning {
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match "RF4Companion\.ps1" })
    return ($procs.Count -gt 0)
}

if (-not $Silent) {
    $win = New-Object System.Windows.Window
    $win.Title = $appName
    $win.SizeToContent = "WidthAndHeight"
    $win.ResizeMode = "NoResize"
    $win.WindowStartupLocation = "CenterScreen"
    $win.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x15, 0x1A, 0x1F))
    $panel = New-Object System.Windows.Controls.StackPanel
    $panel.Margin = New-Object System.Windows.Thickness 24
    $panel.MinWidth = 380
    $q = New-Object System.Windows.Controls.TextBlock
    $q.Text = T "uninstallConfirm" "Really uninstall RF4 Companion?"
    $q.FontSize = 15
    $q.Foreground = [System.Windows.Media.Brushes]::White
    $q.Margin = New-Object System.Windows.Thickness 0, 0, 0, 16
    $panel.Children.Add($q) | Out-Null
    $chk = New-Object System.Windows.Controls.CheckBox
    $chk.Content = T "uninstallKeepData" "Keep my data (spots, catches, recipes)"
    $chk.IsChecked = $true
    $chk.Foreground = [System.Windows.Media.Brushes]::White
    $chk.Margin = New-Object System.Windows.Thickness 0, 0, 0, 20
    $panel.Children.Add($chk) | Out-Null
    $buttons = New-Object System.Windows.Controls.StackPanel
    $buttons.Orientation = "Horizontal"
    $buttons.HorizontalAlignment = "Right"
    $ok = New-Object System.Windows.Controls.Button
    $ok.Content = T "uninstallButton" "Uninstall"
    $ok.Padding = New-Object System.Windows.Thickness 16, 6, 16, 6
    $ok.Margin = New-Object System.Windows.Thickness 0, 0, 8, 0
    $cancel = New-Object System.Windows.Controls.Button
    $cancel.Content = T "setupCancel" "Cancel"
    $cancel.Padding = New-Object System.Windows.Thickness 16, 6, 16, 6
    $cancel.IsCancel = $true
    $buttons.Children.Add($ok) | Out-Null
    $buttons.Children.Add($cancel) | Out-Null
    $panel.Children.Add($buttons) | Out-Null
    $win.Content = $panel
    $script:confirmed = $false
    $ok.Add_Click({ $script:confirmed = $true; $win.Close() })
    $win.ShowDialog() | Out-Null
    if (-not $script:confirmed) { exit 0 }
    $KeepData = [bool]$chk.IsChecked
}

if (Test-AppRunning) {
    if (-not $Silent) { Show-Message (T "setupRunning" "RF4 Companion is still running. Please close it and try again.") "Warning" }
    exit 1
}

foreach ($lnk in @(
    (Join-Path ([Environment]::GetFolderPath("Desktop")) $shortcutName),
    (Join-Path ([Environment]::GetFolderPath("Programs")) $shortcutName)
)) {
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force }
}

if (Test-Path -LiteralPath $regKey) { Remove-Item -LiteralPath $regKey -Recurse -Force }

if ((Test-Path -LiteralPath (Join-Path $InstallDir "RF4Companion.ps1"))) {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
            break
        } catch {
            Start-Sleep -Milliseconds 800
        }
    }
}

if (-not $KeepData -and (Test-Path -LiteralPath $userDir)) {
    Remove-Item -LiteralPath $userDir -Recurse -Force
}

if (-not $Silent) { Show-Message (T "uninstallDone" "RF4 Companion has been uninstalled.") "Information" }
