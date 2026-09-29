param(
    [string]$InstallDir,
    [string]$Language,
    [switch]$Silent,
    [switch]$NoDesktop,
    [switch]$NoStartMenu,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml

$appName = "RF4 Companion"
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root "app"
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RF4Companion"
$defaultDir = Join-Path $env:LOCALAPPDATA "Programs\RF4 Companion"
$userDir = Join-Path $env:APPDATA "RF4Companion"
$shortcutName = "RF4 Companion.lnk"

function Show-Message([string]$text, [string]$icon) {
    [System.Windows.MessageBox]::Show($text, "$appName Setup", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]$icon) | Out-Null
}

$langFile = Join-Path $payload "lang.json"
if (-not (Test-Path -LiteralPath $langFile)) {
    $msg = "Please extract the ZIP file first and run Setup from the extracted folder." + [Environment]::NewLine + [Environment]::NewLine + "Bitte entpacke zuerst die ZIP-Datei und starte Setup dann aus dem entpackten Ordner."
    if ($Silent) { Write-Error $msg } else { Show-Message $msg "Warning" }
    exit 1
}

$langs = Get-Content -LiteralPath $langFile -Raw -Encoding UTF8 | ConvertFrom-Json
$version = (Get-Content -LiteralPath (Join-Path $payload "version.txt") -Raw).Trim()

function Get-SystemLanguage {
    $c = [System.Globalization.CultureInfo]::CurrentUICulture
    if ($c.Name -in @("zh-TW", "zh-HK", "zh-MO")) { return "zh-TW" }
    $two = $c.TwoLetterISOLanguageName
    if ($langs.PSObject.Properties.Name -contains $two) { return $two }
    return "en"
}

function Get-SavedLanguage {
    $f = Join-Path $userDir "user.json"
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    try {
        $u = Get-Content -LiteralPath $f -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($u.lang -and ($langs.PSObject.Properties.Name -contains $u.lang)) { return [string]$u.lang }
    } catch { }
    return $null
}

if (-not $Language) { $Language = Get-SavedLanguage }
if (-not $Language) { $Language = Get-SystemLanguage }
$script:lang = $Language

function T([string]$key) {
    $v = $langs.($script:lang).$key
    if ($v) { return $v }
    return $langs.en.$key
}

function Get-PreviousInstallDir {
    try {
        $p = (Get-ItemProperty -LiteralPath $regKey -ErrorAction Stop).InstallLocation
        if ($p) { return [string]$p }
    } catch { }
    return $null
}

function Test-AppRunning {
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match "RF4Companion\.ps1" })
    return ($procs.Count -gt 0)
}

function New-AppShortcut([string]$path, [string]$dir) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = Join-Path $env:WINDIR "System32\wscript.exe"
    $lnk.Arguments = '"' + (Join-Path $dir "Start RF4 Companion.vbs") + '"'
    $lnk.WorkingDirectory = $dir
    $lnk.IconLocation = (Join-Path $dir "app.ico") + ",0"
    $lnk.Description = $appName
    $lnk.Save()
}

function Install-App([string]$dir, [bool]$desktop, [bool]$startMenu) {
    if (Test-AppRunning) { throw (T "setupRunning") }
    try {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $probe = Join-Path $dir ".write_test"
        [System.IO.File]::WriteAllText($probe, "x")
        Remove-Item -LiteralPath $probe -Force
    } catch {
        throw (T "setupNoAccess")
    }

    & robocopy.exe $payload $dir /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ("robocopy exit code " + $LASTEXITCODE) }
    Get-ChildItem -LiteralPath $dir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

    $desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) $shortcutName
    $startLnk = Join-Path ([Environment]::GetFolderPath("Programs")) $shortcutName
    if ($desktop) { New-AppShortcut $desktopLnk $dir } elseif (Test-Path -LiteralPath $desktopLnk) { Remove-Item -LiteralPath $desktopLnk -Force }
    if ($startMenu) { New-AppShortcut $startLnk $dir } elseif (Test-Path -LiteralPath $startLnk) { Remove-Item -LiteralPath $startLnk -Force }

    $sizeKb = [int]((Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    New-Item -Path $regKey -Force | Out-Null
    $props = [ordered]@{
        DisplayName = $appName
        DisplayVersion = $version
        Publisher = "Nalathan"
        DisplayIcon = (Join-Path $dir "app.ico")
        InstallLocation = $dir
        InstallDate = (Get-Date).ToString("yyyyMMdd")
        UninstallString = ('"' + (Join-Path $env:WINDIR "System32\wscript.exe") + '" "' + (Join-Path $dir "Uninstall.vbs") + '"')
    }
    foreach ($k in $props.Keys) { New-ItemProperty -LiteralPath $regKey -Name $k -Value $props[$k] -PropertyType String -Force | Out-Null }
    foreach ($k in @("NoModify", "NoRepair")) { New-ItemProperty -LiteralPath $regKey -Name $k -Value 1 -PropertyType DWord -Force | Out-Null }
    New-ItemProperty -LiteralPath $regKey -Name "EstimatedSize" -Value $sizeKb -PropertyType DWord -Force | Out-Null

    $userFile = Join-Path $userDir "user.json"
    if (-not (Test-Path -LiteralPath $userFile)) {
        New-Item -ItemType Directory -Path $userDir -Force | Out-Null
        [System.IO.File]::WriteAllText($userFile, ('{"lang":"' + $script:lang + '"}'), (New-Object System.Text.UTF8Encoding $false))
    }
}

function Start-App([string]$dir) {
    Start-Process -FilePath (Join-Path $env:WINDIR "System32\wscript.exe") -ArgumentList ('"' + (Join-Path $dir "Start RF4 Companion.vbs") + '"')
}

if (-not $InstallDir) { $InstallDir = Get-PreviousInstallDir }
if (-not $InstallDir) { $InstallDir = $defaultDir }

if ($Silent) {
    Install-App $InstallDir (-not $NoDesktop) (-not $NoStartMenu)
    if (-not $NoLaunch) { Start-App $InstallDir }
    exit 0
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class ModernFolderPicker {
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    internal class FileOpenDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialog {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] object pFilter);
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOpenDialog {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] object pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    public static string ShowDialog(string title, string initialFolder, IntPtr owner) {
        IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogRCW();
        uint options;
        dialog.GetOptions(out options);
        dialog.SetOptions(options | 0x20 | 0x800);
        if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
        if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder)) {
            Guid shellItemGuid = typeof(IShellItem).GUID;
            IShellItem item;
            int hrItem = SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref shellItemGuid, out item);
            if (hrItem == 0) { dialog.SetFolder(item); }
        }
        int hr = dialog.Show(owner);
        if (hr != 0) return null;
        IShellItem result;
        dialog.GetResult(out result);
        IntPtr pszPath;
        result.GetDisplayName(0x80058000, out pszPath);
        string path = Marshal.PtrToStringUni(pszPath);
        Marshal.FreeCoTaskMem(pszPath);
        return path;
    }
}
"@

$libs = Join-Path $payload "Libs"
Add-Type -Path (Join-Path $libs "Microsoft.Xaml.Behaviors\Microsoft.Xaml.Behaviors.dll")
Add-Type -Path (Join-Path $libs "ControlzEx\ControlzEx.dll")
Add-Type -Path (Join-Path $libs "MahApps.Metro\MahApps.Metro.dll")

[xml]$xaml = @"
<Controls:MetroWindow
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:Controls="clr-namespace:MahApps.Metro.Controls;assembly=MahApps.Metro"
    Title="RF4 Companion Setup" TitleCharacterCasing="Normal" Width="600" SizeToContent="Height" ResizeMode="NoResize"
    WindowStartupLocation="CenterScreen" Background="#FF151A1F"
    WindowTitleBrush="#FF1F272F" NonActiveWindowTitleBrush="#FF1F272F" GlowBrush="#FF2F3A44">
    <Controls:MetroWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml"/>
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <Style TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Controls:ControlsHelper.ContentCharacterCasing" Value="Normal"/>
                <Setter Property="Controls:ControlsHelper.CornerRadius" Value="6"/>
                <Setter Property="Padding" Value="16,7"/>
                <Setter Property="FontSize" Value="13"/>
            </Style>
            <Style TargetType="CheckBox" BasedOn="{StaticResource {x:Type CheckBox}}">
                <Setter Property="Margin" Value="0,0,0,10"/>
                <Setter Property="FontSize" Value="13"/>
            </Style>
        </ResourceDictionary>
    </Controls:MetroWindow.Resources>
    <StackPanel Margin="26,20,26,22">
        <DockPanel Margin="0,0,0,14">
            <ComboBox x:Name="cmbLang" DockPanel.Dock="Right" Width="170" VerticalAlignment="Top"/>
            <Image x:Name="imgIcon" Width="48" Height="48" Margin="0,0,14,0" DockPanel.Dock="Left"/>
            <StackPanel VerticalAlignment="Center">
                <TextBlock Text="RF4 Companion" FontFamily="Bahnschrift SemiBold" FontSize="22" Foreground="#FF4FA8C9"/>
                <TextBlock x:Name="txtVersion" Foreground="#FF8FA0AB" FontSize="12"/>
            </StackPanel>
        </DockPanel>
        <TextBlock x:Name="txtIntro" Foreground="#FFE6ECEF" TextWrapping="Wrap" FontSize="13" Margin="0,0,0,16"/>
        <TextBlock x:Name="txtFolderLabel" Foreground="#FF8FA0AB" FontSize="12" Margin="0,0,0,4"/>
        <DockPanel Margin="0,0,0,8">
            <Button x:Name="btnBrowse" DockPanel.Dock="Right" Margin="8,0,0,0"/>
            <TextBox x:Name="txtFolder" FontSize="13" VerticalContentAlignment="Center"/>
        </DockPanel>
        <TextBlock x:Name="txtUpdate" Foreground="#FF7FD18B" TextWrapping="Wrap" FontSize="12" Margin="0,0,0,8" Visibility="Collapsed"/>
        <CheckBox x:Name="chkDesktop" IsChecked="True" Margin="0,8,0,10"/>
        <CheckBox x:Name="chkStartMenu" IsChecked="True"/>
        <CheckBox x:Name="chkLaunch" IsChecked="True"/>
        <TextBlock x:Name="txtStatus" Foreground="#FFE0A63A" TextWrapping="Wrap" FontSize="12" Margin="0,4,0,0"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button x:Name="btnInstall" Background="#FF4FA8C9" Foreground="#FF0B1820" BorderThickness="0" FontWeight="SemiBold" Margin="0,0,8,0"/>
            <Button x:Name="btnCancel"/>
        </StackPanel>
    </StackPanel>
</Controls:MetroWindow>
"@

$window = [System.Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
foreach ($n in @("cmbLang", "imgIcon", "txtVersion", "txtIntro", "txtFolderLabel", "btnBrowse", "txtFolder", "txtUpdate", "chkDesktop", "chkStartMenu", "chkLaunch", "txtStatus", "btnInstall", "btnCancel")) {
    Set-Variable -Name $n -Value $window.FindName($n) -Scope Script
}

$accent = [System.Windows.Media.Color]::FromRgb(0x4F, 0xA8, 0xC9)
$theme = [ControlzEx.Theming.RuntimeThemeGenerator]::Current.GenerateRuntimeTheme("Dark", $accent)
[ControlzEx.Theming.ThemeManager]::Current.ChangeTheme($window, $theme) | Out-Null

$icoPath = Join-Path $payload "app.ico"
if (Test-Path -LiteralPath $icoPath) {
    $imgIcon.Source = [System.Windows.Media.Imaging.BitmapFrame]::Create((New-Object System.Uri $icoPath))
    $window.Icon = $imgIcon.Source
}

$txtVersion.Text = "Version $version"
$txtFolder.Text = $InstallDir

$langItems = @()
foreach ($p in $langs.PSObject.Properties) { $langItems += [pscustomobject]@{ Key = $p.Name; Label = $p.Value.langName } }
$cmbLang.DisplayMemberPath = "Label"
$cmbLang.ItemsSource = $langItems
$cmbLang.SelectedItem = $langItems | Where-Object { $_.Key -eq $script:lang } | Select-Object -First 1

function Update-UpdateHint {
    $d = "$($txtFolder.Text)".Trim()
    if ($d -and (Test-Path -LiteralPath (Join-Path $d "RF4Companion.ps1"))) {
        $txtUpdate.Text = T "setupUpdate"
        $txtUpdate.Visibility = "Visible"
    } else {
        $txtUpdate.Visibility = "Collapsed"
    }
}

function Apply-SetupLanguage {
    $txtIntro.Text = T "setupIntro"
    $txtFolderLabel.Text = T "setupFolder"
    $btnBrowse.Content = T "setupBrowse"
    $chkDesktop.Content = T "setupDesktop"
    $chkStartMenu.Content = T "setupStartMenu"
    $chkLaunch.Content = T "setupLaunch"
    $btnInstall.Content = T "setupInstall"
    $btnCancel.Content = T "setupCancel"
    Update-UpdateHint
}

$cmbLang.Add_SelectionChanged({
    if ($cmbLang.SelectedItem) {
        $script:lang = $cmbLang.SelectedItem.Key
        Apply-SetupLanguage
    }
})

$txtFolder.Add_TextChanged({ Update-UpdateHint })

$btnBrowse.Add_Click({
    $start = "$($txtFolder.Text)".Trim()
    while ($start -and -not (Test-Path -LiteralPath $start)) { $start = Split-Path -Parent $start }
    $helper = New-Object System.Windows.Interop.WindowInteropHelper $window
    $picked = [ModernFolderPicker]::ShowDialog((T "setupFolder"), $start, $helper.Handle)
    if ($picked) {
        if ((Split-Path -Leaf $picked) -ne "RF4 Companion" -and -not (Test-Path -LiteralPath (Join-Path $picked "RF4Companion.ps1"))) {
            $picked = Join-Path $picked "RF4 Companion"
        }
        $txtFolder.Text = $picked
    }
})

$btnCancel.Add_Click({ $window.Close() })

$btnInstall.Add_Click({
    $dir = "$($txtFolder.Text)".Trim()
    if (-not $dir) { return }
    $btnInstall.IsEnabled = $false
    $btnCancel.IsEnabled = $false
    $txtStatus.Text = "..."
    $window.Dispatcher.Invoke([action]{ }, [System.Windows.Threading.DispatcherPriority]::Render)
    try {
        Install-App $dir ([bool]$chkDesktop.IsChecked) ([bool]$chkStartMenu.IsChecked)
        $txtStatus.Text = ""
        Show-Message (T "setupDone") "Information"
        if ($chkLaunch.IsChecked) { Start-App $dir }
        $window.Close()
    } catch {
        $txtStatus.Text = (T "setupFailed") + " " + $_.Exception.Message
        $btnInstall.IsEnabled = $true
        $btnCancel.IsEnabled = $true
    }
})

Apply-SetupLanguage
$window.ShowDialog() | Out-Null
