param([switch]$Minimized)

$ErrorActionPreference = "Stop"

Get-ChildItem -Path $PSScriptRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml

$root = $PSScriptRoot
$libsDir = Join-Path $root "Libs"
Add-Type -Path (Join-Path $libsDir "Microsoft.Xaml.Behaviors\Microsoft.Xaml.Behaviors.dll")
Add-Type -Path (Join-Path $libsDir "ControlzEx\ControlzEx.dll")
Add-Type -Path (Join-Path $libsDir "MahApps.Metro\MahApps.Metro.dll")
Add-Type -AssemblyName System.Web.Extensions
Add-Type -AssemblyName System.Net.Http

$script:hasWebView = $false
try {
    Add-Type -Path (Join-Path $libsDir "WebView2\Microsoft.Web.WebView2.Core.dll")
    Add-Type -Path (Join-Path $libsDir "WebView2\Microsoft.Web.WebView2.Wpf.dll")
    $script:hasWebView = $true
} catch { }

$dataDir = Join-Path $root "data"
$userDir = Join-Path $env:APPDATA "RF4Companion"
$oldUserDir = Join-Path $env:APPDATA "RF4Begleiter"
if (-not (Test-Path $userDir) -and (Test-Path $oldUserDir)) { Move-Item -LiteralPath $oldUserDir -Destination $userDir }
$userFile = Join-Path $userDir "user.json"
if (-not (Test-Path $userDir)) { New-Item -ItemType Directory -Path $userDir | Out-Null }

$script:errorLog = Join-Path $userDir "error.log"

function Write-ErrorLog([string]$text) {
    try {
        $line = "{0}  {1}{2}" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"), $text, [Environment]::NewLine
        [System.IO.File]::AppendAllText($script:errorLog, $line, (New-Object System.Text.UTF8Encoding $false))
    } catch { }
}

function Read-Json([string]$path) {
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

$game = Read-Json (Join-Path $dataDir "gamedata.json")
$langs = Read-Json (Join-Path $root "lang.json")

$script:lang = "de"
$script:weekRegion = "GL"
$script:trackerLang = ""
$script:winPos = $null
$script:spots = New-Object System.Collections.ArrayList
$script:catches = New-Object System.Collections.ArrayList
$script:recipes = New-Object System.Collections.ArrayList
$recipesSeeded = $false

if (Test-Path $userFile) {
    $loaded = Read-Json $userFile
    if ($loaded.lang) { $script:lang = $loaded.lang }
    if ($loaded.weekRegion) { $script:weekRegion = $loaded.weekRegion }
    if ($loaded.trackerLang) { $script:trackerLang = $loaded.trackerLang }
    if ($loaded.winPos) { $script:winPos = $loaded.winPos }
    foreach ($s in @($loaded.spots)) { if ($s) { $script:spots.Add($s) | Out-Null } }
    foreach ($c in @($loaded.catches)) { if ($c) { $script:catches.Add($c) | Out-Null } }
    foreach ($r in @($loaded.recipes)) { if ($r) { $script:recipes.Add($r) | Out-Null } }
    if ($loaded.recipesSeeded) { $recipesSeeded = $true }
}

function New-Id { [guid]::NewGuid().ToString("N").Substring(0, 10) }

function Save-User {
    $obj = [ordered]@{
        lang = $script:lang
        weekRegion = $script:weekRegion
        trackerLang = $script:trackerLang
        winPos = $script:winPos
        recipesSeeded = $true
        spots = @($script:spots)
        catches = @($script:catches)
        recipes = @($script:recipes)
    }
    $json = ConvertTo-Json -InputObject $obj -Depth 8
    if (Test-Path $userFile) { Copy-Item -LiteralPath $userFile -Destination "$userFile.bak" -Force }
    [System.IO.File]::WriteAllText($userFile, $json, (New-Object System.Text.UTF8Encoding $false))
}

$script:names = @{}
foreach ($p in $game.names.PSObject.Properties) { $script:names[$p.Name] = $p.Value }

$script:lakeById = @{}
foreach ($l in @($game.lakes)) { $script:lakeById[$l.id] = $l }

$script:learnedFile = Join-Path $userDir "lakefish.json"

function Add-LearnedFish([string]$lakeId, [string]$fish) {
    $l = $script:lakeById[$lakeId]
    if (-not $l -or -not $fish) { return $false }
    foreach ($f in @($l.fish)) { if ($f -eq $fish) { return $false } }
    $l.fish = @(@($l.fish) + $fish | Sort-Object)
    return $true
}

function Load-LearnedFish {
    if (-not (Test-Path $script:learnedFile)) { return }
    try {
        $d = Read-Json $script:learnedFile
        foreach ($p in $d.PSObject.Properties) {
            foreach ($f in @($p.Value)) { Add-LearnedFish $p.Name ([string]$f) | Out-Null }
        }
    } catch { }
}

function Save-LearnedFish {
    $o = [ordered]@{}
    foreach ($l in @($game.lakes)) { $o[$l.id] = @($l.fish) }
    [System.IO.File]::WriteAllText($script:learnedFile, (ConvertTo-Json -InputObject $o -Depth 3), (New-Object System.Text.UTF8Encoding $false))
}

Load-LearnedFish

$script:trophyByFish = @{}
foreach ($t in @($game.trophies)) { $script:trophyByFish[$t.fish] = $t }

function T([string]$key) {
    $v = $langs.($script:lang).$key
    if ($v) { return $v }
    $v = $langs.en.$key
    if ($v) { return $v }
    return $key
}

$script:itemNames = $null
$script:itemLangIdx = @{}
$script:cultures = @{
    "en" = "en-US"; "de" = "de-DE"; "ru" = "ru-RU"; "fr" = "fr-FR"; "es" = "es-ES"; "it" = "it-IT"; "pl" = "pl-PL"; "pt" = "pt-BR"
    "nl" = "nl-NL"; "tr" = "tr-TR"; "ro" = "ro-RO"; "id" = "id-ID"; "ja" = "ja-JP"; "ko" = "ko-KR"; "zh" = "zh-CN"; "zh-TW" = "zh-TW"
}

function Get-ItemNames {
    if ($null -eq $script:itemNames) {
        $script:itemNames = @{}
        $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $ser.MaxJsonLength = 268435456
        $raw = [System.IO.File]::ReadAllText((Join-Path $dataDir "itemnames.json"), [System.Text.Encoding]::UTF8)
        $d = $ser.DeserializeObject($raw)
        $i = 0
        foreach ($c in $d["langs"]) { $script:itemLangIdx[[string]$c] = $i; $i++ }
        foreach ($row in $d["rows"]) { $script:itemNames[[string]$row[0]] = $row }
    }
    $script:itemNames
}

function Get-Culture {
    $c = $script:cultures[$script:lang]
    if (-not $c) { $c = "en-US" }
    [System.Globalization.CultureInfo]::GetCultureInfo($c)
}

function Format-LocalDate([datetime]$d) {
    if ($script:lang -eq "en") { return $d.ToString("yyyy-MM-dd") }
    $d.ToString("d", (Get-Culture))
}

function N([string]$en) {
    if (-not $en) { return "" }
    $e = $script:names[$en]
    if ($e) {
        $v = $e.($script:lang)
        if ($v) { return $v }
        return $en
    }
    if ($script:lang -eq "en") { return $en }
    $row = (Get-ItemNames)[$en]
    if ($row) {
        $idx = $script:itemLangIdx[$script:lang]
        if ($null -ne $idx -and $row[$idx]) { return [string]$row[$idx] }
    }
    return $en
}

$script:itemReverse = @{}

function Resolve-ItemAny([string]$text) {
    $t = $text.Trim()
    if (-not $t) { return "" }
    $k = Resolve-Name $t
    if ($k -ne $t -or $script:names.ContainsKey($t)) { return $k }
    $items = Get-ItemNames
    if ($items.ContainsKey($t)) { return [string]$items[$t][0] }
    $idx = $script:itemLangIdx[$script:lang]
    if ($null -ne $idx) {
        if (-not $script:itemReverse.ContainsKey($script:lang)) {
            $map = @{}
            foreach ($row in $items.Values) { $v = [string]$row[$idx]; if ($v -and -not $map.ContainsKey($v)) { $map[$v] = [string]$row[0] } }
            $script:itemReverse[$script:lang] = $map
        }
        $m = $script:itemReverse[$script:lang]
        if ($m.ContainsKey($t)) { return $m[$t] }
    }
    return $t
}

function Resolve-Name([string]$text) {
    $t = $text.Trim()
    if (-not $t) { return "" }
    foreach ($k in $script:names.Keys) {
        foreach ($p in $script:names[$k].PSObject.Properties) {
            if ($p.Value -eq $t) { return $k }
        }
    }
    return $t
}

function Get-LakeName([string]$id) {
    $l = $script:lakeById[$id]
    if ($l) {
        $v = $l.name.($script:lang)
        if ($v) { return $v }
        return $l.name.en
    }
    return $id
}

function Get-LakeIdByAnyName([string]$text) {
    $t = $text.Trim()
    foreach ($l in @($game.lakes)) {
        foreach ($p in $l.name.PSObject.Properties) {
            if ($p.Value -eq $t) { return $l.name.en }
        }
    }
    return Resolve-Name $t
}

function New-Choice($key, [string]$label) {
    [pscustomobject]@{ Key = $key; Label = $label }
}

function Set-Choices($combo, $list) {
    $combo.ItemsSource = $null
    $combo.DisplayMemberPath = "Label"
    $combo.ItemsSource = @($list)
}

function Get-ComboKey($combo) {
    if ($combo.SelectedItem) { return $combo.SelectedItem.Key }
    if ($combo.IsEditable) {
        $t = "$($combo.Text)".Trim()
        if ($t) { return Resolve-Name $t }
    }
    return ""
}

function Set-ComboKey($combo, $key) {
    $m = @($combo.ItemsSource) | Where-Object { $_.Key -eq $key } | Select-Object -First 1
    if ($m) {
        $combo.SelectedItem = $m
    } else {
        $combo.SelectedItem = $null
        if ($combo.IsEditable) { $combo.Text = (N $key) }
    }
}

function Get-FishChoices($lakeId) {
    $list = @()
    $lake = $null
    if ($lakeId) { $lake = $script:lakeById[$lakeId] }
    if ($lake) { $list = @($lake.fish) } else { $list = @($game.trophies | ForEach-Object { $_.fish }) }
    @($list | Select-Object -Unique | ForEach-Object { New-Choice $_ (N $_) } | Sort-Object Label)
}

function Get-BaitChoices {
    @($game.baits | ForEach-Object { New-Choice $_ (N $_) } | Sort-Object Label)
}

function Get-TechChoices {
    $list = @(New-Choice "" "")
    foreach ($k in @("float", "bottom", "spin", "marine", "trolling")) { $list += New-Choice $k (T "tech_$k") }
    $list
}

function Get-TempChoices {
    $list = @(New-Choice "" "")
    foreach ($k in @("cold", "normal", "warm")) { $list += New-Choice $k (T ("temp_" + $k)) }
    $list
}

function Get-LakeChoices([switch]$MapsOnly, [switch]$WithAll) {
    $list = @()
    if ($WithAll) { $list += New-Choice "" (T "all") }
    $sorted = @($game.lakes | Sort-Object @{ Expression = { [int]$_.order } })
    foreach ($l in $sorted) {
        if ($MapsOnly -and -not ($l.mapKey -and $l.bounds)) { continue }
        $list += New-Choice $l.id $l.name.($script:lang)
    }
    $list
}

function Parse-Num([string]$s) {
    if (-not $s) { return $null }
    $x = $s.Trim().Replace(",", ".")
    $d = 0.0
    if ([double]::TryParse($x, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return $null
}

function Parse-Weight([string]$s) {
    if (-not $s) { return $null }
    $x = $s.Trim().ToLower()
    if ($x -match "kg") {
        $d = Parse-Num ($x -replace "[^0-9,\.]", "")
        if ($null -eq $d) { return $null }
        return [int][math]::Round($d * 1000)
    }
    $x = $x -replace "[^0-9]", ""
    if (-not $x) { return $null }
    return [int]$x
}

function Format-Weight($g) {
    if ($null -eq $g -or "$g" -eq "") { return "" }
    $v = [double]$g
    if ($v -ge 1000) { return ("{0:0.###} kg" -f ($v / 1000)) }
    return ("{0:N0} g" -f $v)
}

function Get-TrophyMark([string]$fish, $weight) {
    $t = $script:trophyByFish[$fish]
    if (-not $t -or $null -eq $weight) { return "" }
    $star = [string][char]0x2605
    if ($t.rare -and [int]$weight -ge [int]$t.rare) { return $star + $star }
    if ($t.trophy -and [int]$weight -ge [int]$t.trophy) { return $star }
    return ""
}

function Get-MyBest {
    $best = @{}
    foreach ($c in $script:catches) {
        $w = [int]$c.weight
        if (-not $best.ContainsKey($c.fish) -or $w -gt $best[$c.fish]) { $best[$c.fish] = $w }
    }
    $best
}

function To-Game($lake, [double]$nx, [double]$ny) {
    $b = $lake.bounds
    $x = [double]$b.xMin + ($nx * ([double]$b.xMax-[double]$b.xMin))
    $y = [double]$b.yNorth-($ny * ([double]$b.yNorth-[double]$b.ySouth))
    [pscustomobject]@{ X = $x; Y = $y }
}

function From-Game($lake, [double]$x, [double]$y) {
    $b = $lake.bounds
    $nx = ($x-[double]$b.xMin) / ([double]$b.xMax-[double]$b.xMin)
    $ny = ([double]$b.yNorth-$y) / ([double]$b.yNorth-[double]$b.ySouth)
    [pscustomobject]@{ NX = $nx; NY = $ny }
}

function Format-Coords($lake, $nx, $ny) {
    if (-not $lake -or -not $lake.bounds) { return "" }
    $g = To-Game $lake $nx $ny
    "{0}:{1}" -f [int][math]::Round($g.X), [int][math]::Round($g.Y)
}

[xml]$xaml = @"
<Controls:MetroWindow
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:Controls="clr-namespace:MahApps.Metro.Controls;assembly=MahApps.Metro"
    Title="RF4" TitleCharacterCasing="Normal" Height="860" Width="1320" MinWidth="1000" MinHeight="640" WindowStartupLocation="Manual" Background="{DynamicResource AppBg}"
    WindowTitleBrush="{DynamicResource AppRail}" NonActiveWindowTitleBrush="{DynamicResource AppRail}"
    TitleForeground="{DynamicResource AppInkDim}" GlowBrush="{DynamicResource AppLine}" NonActiveGlowBrush="{DynamicResource AppLine}">
    <Controls:MetroWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml"/>
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <SolidColorBrush x:Key="AppBg" Color="#FF151A1F"/>
            <SolidColorBrush x:Key="AppSurface" Color="#FF1C232A"/>
            <SolidColorBrush x:Key="AppRail" Color="#FF1F272F"/>
            <SolidColorBrush x:Key="AppInk" Color="#FFE6ECEF"/>
            <SolidColorBrush x:Key="AppInkDim" Color="#FF8FA0AB"/>
            <SolidColorBrush x:Key="AppLine" Color="#FF2F3A44"/>
            <SolidColorBrush x:Key="AppAccent" Color="#FF4FA8C9"/>
            <SolidColorBrush x:Key="AppAccentInk" Color="#FF0B1820"/>
            <SolidColorBrush x:Key="AppDanger" Color="#FFC1503D"/>
            <FontFamily x:Key="AppDisplayFont">Bahnschrift SemiBold</FontFamily>
            <BooleanToVisibilityConverter x:Key="BoolToVis"/>
            <Style TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Controls:ControlsHelper.CornerRadius" Value="6"/>
                <Setter Property="Controls:ControlsHelper.ContentCharacterCasing" Value="Normal"/>
                <Setter Property="Padding" Value="14,7"/>
                <Setter Property="Margin" Value="0,0,8,0"/>
                <Setter Property="FontSize" Value="13"/>
            </Style>
            <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Background" Value="{DynamicResource AppAccent}"/>
                <Setter Property="Foreground" Value="{DynamicResource AppAccentInk}"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
            </Style>
            <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Background" Value="{DynamicResource AppDanger}"/>
                <Setter Property="Foreground" Value="White"/>
                <Setter Property="BorderThickness" Value="0"/>
            </Style>
            <Style TargetType="ComboBox" BasedOn="{StaticResource {x:Type ComboBox}}">
                <Setter Property="Controls:ControlsHelper.CornerRadius" Value="6"/>
                <Setter Property="Margin" Value="0,0,0,10"/>
            </Style>
            <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                <Setter Property="Controls:ControlsHelper.CornerRadius" Value="6"/>
                <Setter Property="Margin" Value="0,0,0,10"/>
            </Style>
            <Style TargetType="DatePicker" BasedOn="{StaticResource {x:Type DatePicker}}">
                <Setter Property="Margin" Value="0,0,0,10"/>
            </Style>
            <Style x:Key="Label" TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource AppInkDim}"/>
                <Setter Property="FontSize" Value="12"/>
                <Setter Property="Margin" Value="0,0,0,3"/>
            </Style>
            <Style x:Key="Heading" TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource AppInk}"/>
                <Setter Property="FontFamily" Value="{DynamicResource AppDisplayFont}"/>
                <Setter Property="FontSize" Value="17"/>
                <Setter Property="Margin" Value="0,0,0,12"/>
            </Style>
            <Style x:Key="Card" TargetType="Border">
                <Setter Property="Background" Value="{DynamicResource AppSurface}"/>
                <Setter Property="CornerRadius" Value="8"/>
                <Setter Property="Padding" Value="16"/>
            </Style>
            <Style TargetType="DataGrid" BasedOn="{StaticResource {x:Type DataGrid}}">
                <Setter Property="AutoGenerateColumns" Value="False"/>
                <Setter Property="IsReadOnly" Value="True"/>
                <Setter Property="SelectionMode" Value="Single"/>
                <Setter Property="HeadersVisibility" Value="Column"/>
                <Setter Property="GridLinesVisibility" Value="Horizontal"/>
                <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource AppLine}"/>
                <Setter Property="Background" Value="{DynamicResource AppSurface}"/>
                <Setter Property="RowBackground" Value="Transparent"/>
                <Setter Property="CanUserAddRows" Value="False"/>
                <Setter Property="FontSize" Value="13"/>
            </Style>
            <Style TargetType="DataGridColumnHeader" BasedOn="{StaticResource MahApps.Styles.DataGridColumnHeader}">
                <Setter Property="Controls:ControlsHelper.ContentCharacterCasing" Value="Normal"/>
                <Setter Property="FontSize" Value="13"/>
            </Style>
            <Style TargetType="TabItem" BasedOn="{StaticResource {x:Type TabItem}}">
                <Setter Property="Controls:HeaderedControlHelper.HeaderFontSize" Value="18"/>
                <Setter Property="Padding" Value="10,4"/>
            </Style>
            <Style x:Key="SubTab" TargetType="TabItem" BasedOn="{StaticResource {x:Type TabItem}}">
                <Setter Property="Controls:HeaderedControlHelper.HeaderFontSize" Value="15"/>
                <Setter Property="Padding" Value="8,2"/>
            </Style>
        </ResourceDictionary>
    </Controls:MetroWindow.Resources>

    <Grid x:Name="rootGrid" Margin="18,10,18,10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" Margin="0,0,0,6">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                <TextBlock Tag="t:language" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                <ComboBox x:Name="cmbLang" Width="170" Margin="0"/>
            </StackPanel>
            <TextBlock x:Name="txtAppTitle" FontFamily="{DynamicResource AppDisplayFont}" FontSize="24" Foreground="{DynamicResource AppAccent}" VerticalAlignment="Center"/>
        </DockPanel>

        <TabControl x:Name="tabs" Grid.Row="1">
            <TabItem Tag="t:tabMap">
                <Grid Margin="0,10,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="390"/>
                    </Grid.ColumnDefinitions>
                    <Grid Grid.Column="0" Margin="0,0,14,0">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="*"/>
                            <RowDefinition Height="Auto"/>
                        </Grid.RowDefinitions>
                        <DockPanel Grid.Row="1">
                            <CheckBox x:Name="chkCommunity" Tag="t:communityLayer" IsChecked="True" VerticalAlignment="Center" Margin="0,0,12,10"/>
                            <ComboBox x:Name="cmbCommPeriod" Width="150" Margin="0,0,10,10"/>
                            <ComboBox x:Name="cmbCommFish" Width="200" Margin="0,0,10,10" IsEditable="True" TextSearch.TextPath="Label"/>
                            <Button x:Name="btnCommSync" Tag="t:syncDb" Margin="0,0,10,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                            <Button x:Name="btnCommFull" Tag="t:syncFull" Margin="0,0,10,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                            <TextBlock x:Name="txtCommState" Foreground="{DynamicResource AppInkDim}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Margin="0,0,0,10" Cursor="Hand"/>
                        </DockPanel>
                        <DockPanel Grid.Row="0">
                            <TextBlock x:Name="txtCursor" DockPanel.Dock="Right" FontFamily="Cascadia Mono, Consolas" FontSize="15" Foreground="{DynamicResource AppAccent}" VerticalAlignment="Center" Margin="10,0,0,10"/>
                            <ComboBox x:Name="cmbMapLake" DockPanel.Dock="Left" Width="300" HorizontalAlignment="Left" FontSize="14"/>
                            <ToggleButton x:Name="btnRuler" Tag="t:ruler" DockPanel.Dock="Left" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="10,0,10,10" Padding="12,5" Controls:ControlsHelper.ContentCharacterCasing="Normal"/>
                            <TextBox x:Name="txtMapSearch" Tag="w:mapSearch" MaxWidth="360" HorizontalAlignment="Left" Width="340" Controls:TextBoxHelper.ClearTextButton="True"/>
                        </DockPanel>
                        <Border Grid.Row="2" Background="#FF0E1216" CornerRadius="8" ClipToBounds="True">
                            <ScrollViewer x:Name="svMap" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
                                <Grid x:Name="gridMap" Width="2048" Height="2048">
                                    <Grid.LayoutTransform>
                                        <ScaleTransform x:Name="mapScale" ScaleX="0.4" ScaleY="0.4"/>
                                    </Grid.LayoutTransform>
                                    <Image x:Name="imgMap" Width="2048" Height="2048" Stretch="Fill" RenderOptions.BitmapScalingMode="HighQuality"/>
                                    <Canvas x:Name="canvasMarkers" Background="Transparent" Cursor="Cross"/>
                                    <Canvas x:Name="canvasRuler" IsHitTestVisible="False"/>
                                </Grid>
                            </ScrollViewer>
                        </Border>
                        <TextBlock Grid.Row="3" Tag="t:mapHint" Style="{StaticResource Label}" Margin="2,8,0,0"/>
                    </Grid>
                    <Border Grid.Column="1" Style="{StaticResource Card}">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="*"/>
                                <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <Grid x:Name="panelComm" Grid.Row="0" Grid.RowSpan="2" Visibility="Collapsed">
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="*"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                <TextBlock Grid.Row="0" Tag="t:communitySpot" Style="{StaticResource Heading}"/>
                                <TextBlock Grid.Row="1" x:Name="txtCommTitle" Foreground="{DynamicResource AppAccent}" FontSize="15" FontWeight="SemiBold" Margin="0,0,0,8" TextWrapping="Wrap"/>
                                <WrapPanel Grid.Row="2" Margin="0,0,0,8">
                                    <Button x:Name="btnCommTake" Tag="t:takeOver" Style="{StaticResource PrimaryButton}" Margin="0,0,8,6"/>
                                    <Button x:Name="btnCommSource" Tag="t:openSource" Margin="0,0,8,6"/>
                                    <Button x:Name="btnCommClose" Tag="t:back" Margin="0,0,8,6"/>
                                </WrapPanel>
                                <ScrollViewer Grid.Row="3" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                                    <StackPanel>
                                        <TextBlock x:Name="txtCommInfo" Foreground="{DynamicResource AppInk}" TextWrapping="Wrap" LineHeight="20" Margin="0,0,0,12"/>
                                        <TextBlock Tag="t:singleReports" Style="{StaticResource Label}" Margin="0,0,0,6" TextWrapping="Wrap"/>
                                        <ListBox x:Name="lstCommReports" Background="Transparent" ScrollViewer.HorizontalScrollBarVisibility="Disabled" ScrollViewer.VerticalScrollBarVisibility="Disabled">
                                            <ListBox.ItemTemplate>
                                                <DataTemplate>
                                                    <StackPanel>
                                                        <TextBlock Text="{Binding Label}" TextWrapping="Wrap" Foreground="{DynamicResource AppInk}" FontSize="12" Margin="0,2,0,2"/>
                                                        <Border BorderBrush="{DynamicResource AppAccent}" BorderThickness="2,0,0,0" Padding="10,4,4,6" Margin="0,2,0,6" Visibility="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}, Converter={StaticResource BoolToVis}}">
                                                            <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" Foreground="{DynamicResource AppInk}" FontSize="12" LineHeight="19"/>
                                                        </Border>
                                                    </StackPanel>
                                                </DataTemplate>
                                            </ListBox.ItemTemplate>
                                        </ListBox>
                                    </StackPanel>
                                </ScrollViewer>
                                <TextBlock Grid.Row="4" Tag="t:dataCredit" Style="{StaticResource Label}" Margin="0,10,0,0" TextWrapping="Wrap"/>
                            </Grid>
                            <StackPanel x:Name="panelMapIdle" Grid.Row="0" Visibility="Collapsed">
                                <TextBlock Tag="t:spotsOnLake" Style="{StaticResource Heading}"/>
                                <TextBlock Tag="t:mapIdleHint" Style="{StaticResource Label}" TextWrapping="Wrap" Margin="0,0,0,10"/>
                                <Button x:Name="btnMapNewSpot" Tag="t:mapNewSpot" Style="{StaticResource PrimaryButton}" HorizontalAlignment="Left" Margin="0,0,0,12"/>
                            </StackPanel>
                            <ScrollViewer x:Name="svSpotForm" Grid.Row="0" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                            <StackPanel x:Name="panelSpotForm">
                                <TextBlock x:Name="txtSpotFormTitle" Tag="t:spot" Style="{StaticResource Heading}"/>
                                <TextBlock Tag="t:spotName" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtSpotName"/>
                                <TextBlock Tag="t:fish" Style="{StaticResource Label}"/>
                                <ComboBox x:Name="cmbSpotFish" IsEditable="True" TextSearch.TextPath="Label"/>
                                <TextBlock x:Name="lblSpotBait" Tag="t:bait" Style="{StaticResource Label}"/>
                                <ComboBox x:Name="cmbSpotBait" IsEditable="True" TextSearch.TextPath="Label"/>
                                <StackPanel x:Name="panelBottom" Visibility="Collapsed">
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="10"/>
                                            <ColumnDefinition Width="*"/>
                                        </Grid.ColumnDefinitions>
                                        <StackPanel Grid.Column="0">
                                            <TextBlock Tag="t:bait2" Style="{StaticResource Label}"/>
                                            <ComboBox x:Name="cmbSpotBait2" IsEditable="True" TextSearch.TextPath="Label"/>
                                        </StackPanel>
                                        <StackPanel Grid.Column="2">
                                            <TextBlock Tag="t:dipL" Style="{StaticResource Label}"/>
                                            <ComboBox x:Name="cmbSpotDip" IsEditable="True" TextSearch.TextPath="Label"/>
                                        </StackPanel>
                                    </Grid>
                                    <TextBlock Tag="t:groundbaitMix" Style="{StaticResource Label}"/>
                                    <DockPanel>
                                        <Button x:Name="btnSpotGroundRecipe" Tag="t:openRecipe" DockPanel.Dock="Right" Margin="8,0,0,10" Padding="10,4" VerticalAlignment="Top"/>
                                        <ComboBox x:Name="cmbSpotGround" IsEditable="True" TextSearch.TextPath="Label"/>
                                    </DockPanel>
                                    <TextBlock Tag="t:pvaL" Style="{StaticResource Label}"/>
                                    <DockPanel>
                                        <Button x:Name="btnSpotPvaRecipe" Tag="t:openRecipe" DockPanel.Dock="Right" Margin="8,0,0,10" Padding="10,4" VerticalAlignment="Top"/>
                                        <ComboBox x:Name="cmbSpotPva" IsEditable="True" TextSearch.TextPath="Label"/>
                                    </DockPanel>
                                </StackPanel>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="10"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0">
                                        <TextBlock Tag="t:technique" Style="{StaticResource Label}"/>
                                        <ComboBox x:Name="cmbSpotTech"/>
                                    </StackPanel>
                                    <StackPanel Grid.Column="2">
                                        <TextBlock Tag="t:coords" Style="{StaticResource Label}"/>
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="6"/>
                                                <ColumnDefinition Width="*"/>
                                            </Grid.ColumnDefinitions>
                                            <TextBox x:Name="txtSpotX" Grid.Column="0" Controls:TextBoxHelper.Watermark="X"/>
                                            <TextBox x:Name="txtSpotY" Grid.Column="2" Controls:TextBoxHelper.Watermark="Y"/>
                                        </Grid>
                                    </StackPanel>
                                </Grid>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="10"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0">
                                        <TextBlock Tag="t:depth" Style="{StaticResource Label}"/>
                                        <TextBox x:Name="txtSpotDepth"/>
                                    </StackPanel>
                                    <StackPanel Grid.Column="2">
                                        <TextBlock Tag="t:distance" Style="{StaticResource Label}"/>
                                        <TextBox x:Name="txtSpotDist"/>
                                    </StackPanel>
                                </Grid>
                                <TextBlock Tag="t:temperature" Style="{StaticResource Label}"/>
                                <ComboBox x:Name="cmbSpotTemp"/>
                                <TextBlock Tag="t:castDir" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtSpotDir" Tag="w:castDirHint"/>
                                <CheckBox x:Name="chkSpotShare" Tag="t:shareSpot" Margin="0,0,0,10"/>
                                <TextBlock Tag="t:notes" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtSpotNotes" Height="60" TextWrapping="Wrap" AcceptsReturn="True" VerticalScrollBarVisibility="Auto"/>
                                <StackPanel Orientation="Horizontal" Margin="0,2,0,14">
                                    <Button x:Name="btnSpotSave" Tag="t:save" Style="{StaticResource PrimaryButton}"/>
                                    <Button x:Name="btnSpotNew" Tag="t:new"/>
                                    <Button x:Name="btnSpotClose" Tag="t:closeForm"/>
                                    <Button x:Name="btnSpotDelete" Tag="t:delete" Style="{StaticResource DangerButton}"/>
                                </StackPanel>
                                <TextBlock Tag="t:spotsOnLake" Style="{StaticResource Label}"/>
                            </StackPanel>
                            </ScrollViewer>
                            <ListBox x:Name="lstLakeSpots" Grid.Row="1" DisplayMemberPath="Label" Background="Transparent" MaxHeight="200"/>
                        </Grid>
                    </Border>
                </Grid>
            </TabItem>

            <TabItem Tag="t:tabPlan">
                <TabControl x:Name="tabsPlan" Margin="0,6,0,0">
            <TabItem Tag="t:tabTarget" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <DockPanel Grid.Row="0">
                        <ComboBox x:Name="cmbTgtFish" Width="280" Margin="0,0,10,10" IsEditable="True" TextSearch.TextPath="Label"/>
                        <ComboBox x:Name="cmbTgtLake" Width="230" Margin="0,0,10,10"/>
                        <TextBlock x:Name="txtTgtHint" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="4,0,0,10" TextWrapping="Wrap"/>
                    </DockPanel>
                    <Border Grid.Row="1" Style="{StaticResource Card}" Margin="0,0,0,12" Padding="14,10,14,10">
                        <TextBlock x:Name="txtTgtVerdict" Foreground="{DynamicResource AppInk}" TextWrapping="Wrap" LineHeight="21" FontSize="14"/>
                    </Border>
                    <Grid Grid.Row="2">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Border Grid.Column="0" Style="{StaticResource Card}" Margin="0,0,14,0">
                            <Grid>
                                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                                <TextBlock Grid.Row="0" Tag="t:tgtSpots" Style="{StaticResource Heading}"/>
                                <DataGrid x:Name="dgTgtSpots" Grid.Row="1">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="t:spot" Binding="{Binding Coords}" Width="70"/>
                                        <DataGridTextColumn Header="t:tgtStatus" Binding="{Binding Status}" SortMemberPath="ScoreSort" Width="90"/>
                                        <DataGridTextColumn Header="t:lastReport" Binding="{Binding Last}" SortMemberPath="LastSort" Width="130"/>
                                        <DataGridTextColumn Header="t:reports" Binding="{Binding Count}" Width="75"/>
                                        <DataGridTextColumn Header="t:distance" Binding="{Binding Clip}" Width="60"/>
                                        <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextTrimming" Value="CharacterEllipsis"/><Setter Property="ToolTip" Value="{Binding Bait}"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                        <DataGridTextColumn x:Name="colTgtSource" Header="t:tgtSource" Binding="{Binding Host}" Width="95"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="Foreground" Value="{DynamicResource AppAccent}"/><Setter Property="TextDecorations" Value="Underline"/><Setter Property="Cursor" Value="Hand"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                    </DataGrid.Columns>
                                </DataGrid>
                            </Grid>
                        </Border>
                        <Border Grid.Column="1" Style="{StaticResource Card}">
                            <Grid>
                                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                                <TextBlock Grid.Row="0" Tag="t:tgtBaits" Style="{StaticResource Heading}"/>
                                <DataGrid x:Name="dgTgtBaits" Grid.Row="1">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                        <DataGridTextColumn Header="t:records" Binding="{Binding Count}" SortMemberPath="CountSort" Width="70"/>
                                        <DataGridTextColumn Header="t:tgtLast48" Binding="{Binding Recent}" SortMemberPath="RecentSort" Width="85"/>
                                        <DataGridTextColumn Header="t:tgtLastSeen" Binding="{Binding LastSeen}" SortMemberPath="LastSeenSort" Width="105"/>
                                        <DataGridTextColumn Header="t:prefFirstSeen" Binding="{Binding First}" SortMemberPath="FirstSort" Width="100"/>
                                        <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="120"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/><Setter Property="ToolTip" Value="{Binding FishAll}"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                    </DataGrid.Columns>
                                </DataGrid>
                            </Grid>
                        </Border>
                    </Grid>
                </Grid>
            </TabItem>
            <TabItem Tag="t:tabPrefs" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <DockPanel Grid.Row="0">
                        <ComboBox x:Name="cmbPrefLake" Width="230" Margin="0,0,10,10"/>
                        <ComboBox x:Name="cmbPrefTable" Width="190" Margin="0,0,10,10"/>
                        <ComboBox x:Name="cmbPrefWindow" Width="220" Margin="0,0,10,10"/>
                        <ComboBox x:Name="cmbPrefWeek" Width="200" Margin="0,0,10,10"/>
                        <Button x:Name="btnPrefHarvest" Tag="t:harvestNow" Margin="0,0,14,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                        <TextBlock x:Name="txtPrefState" Foreground="{DynamicResource AppInkDim}" VerticalAlignment="Center" TextWrapping="Wrap" Margin="0,0,0,10"/>
                    </DockPanel>
                    <TextBlock x:Name="txtPrefHint" Grid.Row="1" Style="{StaticResource Label}" Margin="0,0,0,8" Cursor="Help" HorizontalAlignment="Left"/>
                    <Grid Grid.Row="2">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="460"/>
                        </Grid.ColumnDefinitions>
                        <DataGrid x:Name="dgPrefs" Grid.Column="0" Margin="0,0,14,0" AutoGenerateColumns="True" CanUserSortColumns="True"/>
                        <Border Grid.Column="1" Style="{StaticResource Card}">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="*"/>
                                </Grid.RowDefinitions>
                                <TextBlock x:Name="txtPrefFish" Grid.Row="0" Tag="t:prefPickFish" Style="{StaticResource Heading}" TextWrapping="Wrap"/>
                                <DataGrid x:Name="dgPrefBaits" Grid.Row="1">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                        <DataGridTextColumn Header="t:records" Binding="{Binding Count}" SortMemberPath="CountSort" Width="70"/>
                                        <DataGridTextColumn Header="t:prefFirstSeen" Binding="{Binding First}" SortMemberPath="FirstSort" Width="110"/>
                                    </DataGrid.Columns>
                                </DataGrid>
                            </Grid>
                        </Border>
                    </Grid>
                </Grid>
            </TabItem>
            <TabItem Tag="t:tabWeekly" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <DockPanel Grid.Row="0">
                        <TextBlock Tag="t:region" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,8,10"/>
                        <ComboBox x:Name="cmbWeekRegion" Width="80" Margin="0,0,10,10"/>
                        <Button x:Name="btnWeekLoad" Tag="t:refresh" Style="{StaticResource PrimaryButton}" Margin="0,0,14,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                        <ComboBox x:Name="cmbWeekLake" Width="230" Margin="0,0,10,10"/>
                        <ComboBox x:Name="cmbWeekDays" Width="150" Margin="0,0,10,10"/>
                        <TextBlock x:Name="txtWeekState" DockPanel.Dock="Right" Foreground="{DynamicResource AppInkDim}" VerticalAlignment="Center" Margin="14,0,0,10"/>
                        <TextBox x:Name="txtWeekSearch" Tag="w:search"/>
                    </DockPanel>
                    <Grid Grid.Row="1">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="580"/>
                        </Grid.ColumnDefinitions>
                        <DataGrid x:Name="dgWeek" Grid.Column="0" Margin="0,0,14,0" EnableRowVirtualization="True">
                            <DataGrid.Columns>
                                <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="160"/>
                                <DataGridTextColumn Header="t:weight" Binding="{Binding Weight}" SortMemberPath="WeightSort" Width="90"/>
                                <DataGridTextColumn Header="t:trophy" Binding="{Binding Mark}" Width="75">
                                    <DataGridTextColumn.ElementStyle>
                                        <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#FFE0B040"/><Setter Property="FontSize" Value="15"/></Style>
                                    </DataGridTextColumn.ElementStyle>
                                </DataGridTextColumn>
                                <DataGridTextColumn Header="t:lake" Binding="{Binding Lake}" Width="150"/>
                                <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="*"/>
                                <DataGridTextColumn Header="t:player" Binding="{Binding Player}" Width="120"/>
                                <DataGridTextColumn Header="t:date" Binding="{Binding Date}" Width="75"/>
                            </DataGrid.Columns>
                        </DataGrid>
                        <Border Grid.Column="1" Style="{StaticResource Card}">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="*"/>
                                </Grid.RowDefinitions>
                                <TextBlock x:Name="txtWeekBaitsTitle" Grid.Row="0" Style="{StaticResource Heading}" TextWrapping="Wrap"/>
                                <DataGrid x:Name="dgWeekBaits" Grid.Row="1">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="5*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                        <DataGridTextColumn Header="t:records" Binding="{Binding Count}" Width="80"/>
                                        <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="4*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                    </DataGrid.Columns>
                                </DataGrid>
                            </Grid>
                        </Border>
                    </Grid>
                </Grid>
            </TabItem>
                </TabControl>
            </TabItem>

            <TabItem Tag="t:tabCatches">
                <Grid Margin="0,10,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="400"/>
                    </Grid.ColumnDefinitions>
                    <Grid Grid.Column="0" Margin="0,0,14,0">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>
                        <Border Grid.Row="1" Style="{StaticResource Card}" Margin="0,0,0,12" Padding="14,10,14,10">
                            <StackPanel>
                                <DockPanel Margin="0,0,0,6">
                                    <Button x:Name="btnTrackerRefresh" DockPanel.Dock="Right" Tag="t:refresh" Margin="8,0,0,0" Padding="12,4"/>
                                    <Button x:Name="btnTrackerNewSpot" DockPanel.Dock="Right" Tag="t:newSpotSession" Margin="8,0,0,0" Padding="12,4"/>
                                    <ComboBox x:Name="cmbTrackerLang" DockPanel.Dock="Right" Width="150" Margin="8,0,0,0"/>
                                    <TextBlock DockPanel.Dock="Right" Tag="t:trackerGameLang" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="8,0,0,0"/>
                                    <CheckBox x:Name="chkTracker" Tag="t:trackerActive" FontWeight="SemiBold" VerticalAlignment="Center"/>
                                </DockPanel>
                                <TextBlock x:Name="txtTrackerState" Style="{StaticResource Label}" TextWrapping="Wrap"/>
                                <TextBlock x:Name="txtTrackerSetup" Foreground="{DynamicResource AppInk}" TextWrapping="Wrap" Margin="0,4,0,6"/>
                                <DockPanel x:Name="panelTrackerPending" Visibility="Collapsed">
                                    <StackPanel DockPanel.Dock="Right" Margin="8,0,0,0">
                                        <Button x:Name="btnTrackerAccept" Tag="t:accept" Style="{StaticResource PrimaryButton}" Margin="0,0,0,6"/>
                                        <Button x:Name="btnTrackerAcceptAll" Tag="t:acceptAll" Margin="0,0,0,6"/>
                                        <Button x:Name="btnTrackerDiscard" Tag="t:discard" Style="{StaticResource DangerButton}" Margin="0"/>
                                    </StackPanel>
                                    <ListBox x:Name="lstTrackerPending" DisplayMemberPath="Label" Background="Transparent" MaxHeight="160" SelectionMode="Extended"/>
                                </DockPanel>
                            </StackPanel>
                        </Border>
                        <Expander x:Name="expCatchForm" Grid.Row="0" Tag="t:catchFormHeader" Margin="0,0,0,12" IsExpanded="False" Controls:HeaderedControlHelper.HeaderFontSize="14">
                        <Border Style="{StaticResource Card}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="130"/>
                                    <ColumnDefinition Width="10"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="10"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="10"/>
                                    <ColumnDefinition Width="110"/>
                                </Grid.ColumnDefinitions>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                <StackPanel Grid.Row="0" Grid.Column="0">
                                    <TextBlock Tag="t:date" Style="{StaticResource Label}"/>
                                    <DatePicker x:Name="dpCatchDate"/>
                                </StackPanel>
                                <StackPanel Grid.Row="0" Grid.Column="2">
                                    <TextBlock Tag="t:lake" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchLake"/>
                                </StackPanel>
                                <StackPanel Grid.Row="0" Grid.Column="4">
                                    <TextBlock Tag="t:fish" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchFish" IsEditable="True" TextSearch.TextPath="Label"/>
                                </StackPanel>
                                <StackPanel Grid.Row="0" Grid.Column="6">
                                    <TextBlock Tag="t:weightG" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtCatchWeight"/>
                                </StackPanel>
                                <StackPanel Grid.Row="1" Grid.Column="0">
                                    <TextBlock Tag="t:coords" Style="{StaticResource Label}"/>
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="6"/>
                                            <ColumnDefinition Width="*"/>
                                        </Grid.ColumnDefinitions>
                                        <TextBox x:Name="txtCatchX" Grid.Column="0" Controls:TextBoxHelper.Watermark="X"/>
                                        <TextBox x:Name="txtCatchY" Grid.Column="2" Controls:TextBoxHelper.Watermark="Y"/>
                                    </Grid>
                                </StackPanel>
                                <StackPanel Grid.Row="1" Grid.Column="2">
                                    <TextBlock x:Name="lblCatchSpot" Tag="t:spotNameOpt" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtCatchSpotName"/>
                                </StackPanel>
                                <StackPanel Grid.Row="1" Grid.Column="4">
                                    <TextBlock Tag="t:distance" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtCatchClip"/>
                                </StackPanel>
                                <StackPanel Grid.Row="1" Grid.Column="6">
                                    <TextBlock Tag="t:depth" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtCatchDepth"/>
                                </StackPanel>
                                <StackPanel Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="3">
                                    <TextBlock Tag="t:bait1" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchBait" IsEditable="True" TextSearch.TextPath="Label"/>
                                </StackPanel>
                                <StackPanel Grid.Row="2" Grid.Column="4" Grid.ColumnSpan="3">
                                    <TextBlock Tag="t:bait2" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchBait2" IsEditable="True" TextSearch.TextPath="Label"/>
                                </StackPanel>
                                <StackPanel Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="3">
                                    <TextBlock Tag="t:dipL" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchDip" IsEditable="True" TextSearch.TextPath="Label"/>
                                </StackPanel>
                                <StackPanel Grid.Row="3" Grid.Column="4" Grid.ColumnSpan="3">
                                    <TextBlock Tag="t:groundbaitPva" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbCatchPva" IsEditable="True" TextSearch.TextPath="Label"/>
                                </StackPanel>
                                <DockPanel Grid.Row="4" Grid.Column="0" Grid.ColumnSpan="7">
                                    <Button x:Name="btnCatchAdd" DockPanel.Dock="Right" Tag="t:addCatch" Style="{StaticResource PrimaryButton}" Margin="10,16,0,10" VerticalAlignment="Top"/>
                                    <Button x:Name="btnCatchNew" DockPanel.Dock="Right" Tag="t:new" Margin="10,16,0,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                                    <Button x:Name="btnCatchSave" DockPanel.Dock="Right" Tag="t:saveChanges" Margin="10,16,0,10" VerticalAlignment="Top" Visibility="Collapsed"/>
                                    <StackPanel DockPanel.Dock="Left" Width="150" Margin="0,0,10,0">
                                        <TextBlock Tag="t:technique" Style="{StaticResource Label}"/>
                                        <ComboBox x:Name="cmbCatchTech"/>
                                    </StackPanel>
                                    <StackPanel DockPanel.Dock="Left" Width="150" Margin="0,0,10,0">
                                        <TextBlock Tag="t:temperature" Style="{StaticResource Label}"/>
                                        <ComboBox x:Name="cmbCatchTemp"/>
                                    </StackPanel>
                                    <StackPanel DockPanel.Dock="Left" Width="170" Margin="0,0,10,0">
                                        <TextBlock Tag="t:castDir" Style="{StaticResource Label}"/>
                                        <TextBox x:Name="txtCatchDir" Tag="w:castDirHint"/>
                                    </StackPanel>
                                    <CheckBox x:Name="chkCatchShare" DockPanel.Dock="Left" Tag="t:shareNewSpot" Margin="0,18,10,0" VerticalAlignment="Top"/>
                                    <StackPanel>
                                        <TextBlock Tag="t:notes" Style="{StaticResource Label}"/>
                                        <TextBox x:Name="txtCatchNotes"/>
                                    </StackPanel>
                                </DockPanel>
                            </Grid>
                        </Border>
                        </Expander>
                        <DockPanel Grid.Row="2">
                            <CheckBox x:Name="chkCatchGroup" DockPanel.Dock="Right" Tag="t:groupCatches" IsChecked="True" Margin="12,0,0,10" VerticalAlignment="Center"/>
                            <Button x:Name="btnCatchDelete" DockPanel.Dock="Right" Tag="t:delete" Style="{StaticResource DangerButton}" Margin="10,0,0,10" VerticalAlignment="Top"/>
                            <TextBox x:Name="txtCatchSearch" Tag="w:search"/>
                        </DockPanel>
                        <DataGrid x:Name="dgCatches" Grid.Row="3">
                            <DataGrid.Columns>
                                <DataGridTextColumn Header="t:date" Binding="{Binding Date}" SortMemberPath="DateSort" Width="95"/>
                                <DataGridTextColumn Header="t:lake" Binding="{Binding Lake}" Width="150"/>
                                <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="240"/>
                                <DataGridTextColumn Header="t:weight" Binding="{Binding Weight}" SortMemberPath="WeightSort" Width="95"/>
                                <DataGridTextColumn Header="t:trophy" Binding="{Binding Mark}" Width="80">
                                    <DataGridTextColumn.ElementStyle>
                                        <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#FFE0B040"/><Setter Property="FontSize" Value="15"/></Style>
                                    </DataGridTextColumn.ElementStyle>
                                </DataGridTextColumn>
                                <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="220"/>
                                <DataGridTextColumn Header="t:spot" Binding="{Binding Spot}" Width="170"/>
                                <DataGridTextColumn Header="t:notes" Binding="{Binding Notes}" Width="*"/>
                            </DataGrid.Columns>
                        </DataGrid>
                    </Grid>
                    <Border Grid.Column="1" Style="{StaticResource Card}">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="*"/>
                            </Grid.RowDefinitions>
                            <TextBlock Grid.Row="0" Tag="t:stats" Style="{StaticResource Heading}"/>
                            <TextBlock Grid.Row="1" x:Name="txtCatchTotal" Foreground="{DynamicResource AppInk}" Margin="0,0,0,10"/>
                            <DataGrid x:Name="dgStats" Grid.Row="2">
                                <DataGrid.Columns>
                                    <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                    <DataGridTextColumn Header="t:count" Binding="{Binding Count}" Width="72"/>
                                    <DataGridTextColumn Header="t:best" Binding="{Binding Best}" SortMemberPath="BestSort" Width="85"/>
                                    <DataGridTextColumn Header="t:bestBait" Binding="{Binding BestBait}" Width="*"><DataGridTextColumn.ElementStyle><Style TargetType="TextBlock"><Setter Property="TextTrimming" Value="CharacterEllipsis"/><Setter Property="ToolTip" Value="{Binding BestBait}"/></Style></DataGridTextColumn.ElementStyle></DataGridTextColumn>
                                </DataGrid.Columns>
                            </DataGrid>
                        </Grid>
                    </Border>
                </Grid>
            </TabItem>

            <TabItem Tag="t:tabSpots">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <DockPanel Grid.Row="0">
                        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Top">
                            <Button x:Name="btnSpotsShow" Tag="t:openOnMap" Style="{StaticResource PrimaryButton}"/>
                            <Button x:Name="btnSpotsDelete" Tag="t:delete" Style="{StaticResource DangerButton}" Margin="0"/>
                        </StackPanel>
                        <ComboBox x:Name="cmbSpotsSource" Width="230" DockPanel.Dock="Left" Margin="0,0,10,10"/>
                        <ComboBox x:Name="cmbSpotsLake" Width="240" DockPanel.Dock="Left" Margin="0,0,10,10"/>
                        <TextBox x:Name="txtSpotsSearch" Tag="w:search" Margin="0,0,14,10"/>
                    </DockPanel>
                    <Grid Grid.Row="1">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="380"/>
                    </Grid.ColumnDefinitions>
                    <DataGrid x:Name="dgSpots" Grid.Column="0" Margin="0,0,14,0">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="t:lake" Binding="{Binding Lake}" Width="170"/>
                            <DataGridTextColumn Header="t:spotName" Binding="{Binding Name}" Width="170"/>
                            <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="150"/>
                            <DataGridTextColumn Header="t:bait" Binding="{Binding Bait}" Width="150"/>
                            <DataGridTextColumn Header="t:technique" Binding="{Binding Tech}" Width="100"/>
                            <DataGridTextColumn Header="t:coords" Binding="{Binding Coords}" Width="100"/>
                            <DataGridTextColumn Header="t:depthClip" Binding="{Binding Depth}" Width="120"/>
                            <DataGridTextColumn Header="t:sharedCol" Binding="{Binding Shared}" Width="75"/>
                            <DataGridTextColumn Header="t:notes" Binding="{Binding Notes}" Width="*"/>
                        </DataGrid.Columns>
                    </DataGrid>
                    <Border Grid.Column="1" Style="{StaticResource Card}">
                        <Grid>
                            <TextBlock x:Name="txtSeHint" Tag="t:spotEditHint" Style="{StaticResource Label}" TextWrapping="Wrap"/>
                            <ScrollViewer x:Name="svSpotEdit" VerticalScrollBarVisibility="Auto" Visibility="Collapsed">
                                <StackPanel>
                                    <TextBlock x:Name="txtSeTitle" Style="{StaticResource Heading}" TextWrapping="Wrap"/>
                                    <TextBlock Tag="t:spotName" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtSeName"/>
                                    <Grid>
                                        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="10"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                        <StackPanel Grid.Column="0">
                                            <TextBlock Tag="t:coords" Style="{StaticResource Label}"/>
                                            <Grid>
                                                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="6"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                                <TextBox x:Name="txtSeX" Grid.Column="0" Controls:TextBoxHelper.Watermark="X"/>
                                                <TextBox x:Name="txtSeY" Grid.Column="2" Controls:TextBoxHelper.Watermark="Y"/>
                                            </Grid>
                                        </StackPanel>
                                        <StackPanel Grid.Column="2">
                                            <TextBlock Tag="t:technique" Style="{StaticResource Label}"/>
                                            <ComboBox x:Name="cmbSeTech"/>
                                        </StackPanel>
                                    </Grid>
                                    <TextBlock Tag="t:fish" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeFish" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <TextBlock Tag="t:bait1" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeBait" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <TextBlock Tag="t:bait2" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeBait2" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <TextBlock Tag="t:dipL" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeDip" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <TextBlock Tag="t:groundbaitMix" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeGround" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <TextBlock Tag="t:pvaL" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSePva" IsEditable="True" TextSearch.TextPath="Label"/>
                                    <Grid>
                                        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="10"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                        <StackPanel Grid.Column="0">
                                            <TextBlock Tag="t:distance" Style="{StaticResource Label}"/>
                                            <TextBox x:Name="txtSeDist"/>
                                        </StackPanel>
                                        <StackPanel Grid.Column="2">
                                            <TextBlock Tag="t:depth" Style="{StaticResource Label}"/>
                                            <TextBox x:Name="txtSeDepth"/>
                                        </StackPanel>
                                    </Grid>
                                    <TextBlock Tag="t:temperature" Style="{StaticResource Label}"/>
                                    <ComboBox x:Name="cmbSeTemp"/>
                                    <TextBlock Tag="t:castDir" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtSeDir" Tag="w:castDirHint"/>
                                    <CheckBox x:Name="chkSeShare" Tag="t:shareSpot" Margin="0,0,0,10"/>
                                    <TextBlock Tag="t:notes" Style="{StaticResource Label}"/>
                                    <TextBox x:Name="txtSeNotes" Height="60" TextWrapping="Wrap" AcceptsReturn="True"/>
                                    <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                                        <Button x:Name="btnSeSave" Tag="t:save" Style="{StaticResource PrimaryButton}"/>
                                        <Button x:Name="btnSeDelete" Tag="t:delete" Style="{StaticResource DangerButton}"/>
                                    </StackPanel>
                                </StackPanel>
                            </ScrollViewer>
                        </Grid>
                    </Border>
                    </Grid>
                </Grid>
            </TabItem>

            <TabItem Tag="t:tabInfo">
                <TabControl x:Name="tabsInfo" Margin="0,6,0,0">
            <TabItem Tag="t:tabLakes" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="280"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column="0" Style="{StaticResource Card}" Margin="0,0,14,0" Padding="6">
                        <ListBox x:Name="lstLakes" DisplayMemberPath="Label" Background="Transparent" FontSize="14"/>
                    </Border>
                    <Grid Grid.Column="1">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>
                        <Border Grid.Row="0" Style="{StaticResource Card}" Margin="0,0,0,12">
                            <StackPanel>
                                <DockPanel>
                                    <Button x:Name="btnLakeMap" DockPanel.Dock="Right" Tag="t:tabMap" Style="{StaticResource PrimaryButton}" Margin="0" VerticalAlignment="Top"/>
                                    <TextBlock x:Name="txtLakeName" Style="{StaticResource Heading}" FontSize="22" Margin="0,0,0,4"/>
                                </DockPanel>
                                <TextBlock x:Name="txtLakeMeta" Foreground="{DynamicResource AppAccent}" Margin="0,0,0,10"/>
                                <TextBlock x:Name="txtLakeDesc" Foreground="{DynamicResource AppInk}" TextWrapping="Wrap" MaxHeight="120"/>
                            </StackPanel>
                        </Border>
                        <TextBlock Grid.Row="1" Tag="t:fishHere" Style="{StaticResource Label}" Margin="2,0,0,6"/>
                        <DataGrid x:Name="dgLakeFish" Grid.Row="2">
                            <DataGrid.Columns>
                                <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="280"/>
                                <DataGridTextColumn Header="t:trophy" Binding="{Binding Trophy}" SortMemberPath="TrophySort" Width="130"/>
                                <DataGridTextColumn Header="t:rare" Binding="{Binding Rare}" SortMemberPath="RareSort" Width="140"/>
                                <DataGridTextColumn Header="t:myBest" Binding="{Binding MyBest}" SortMemberPath="MyBestSort" Width="130"/>
                                <DataGridTextColumn Header="t:status" Binding="{Binding Mark}" Width="*">
                                    <DataGridTextColumn.ElementStyle>
                                        <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#FFE0B040"/><Setter Property="FontSize" Value="15"/></Style>
                                    </DataGridTextColumn.ElementStyle>
                                </DataGridTextColumn>
                            </DataGrid.Columns>
                        </DataGrid>
                    </Grid>
                </Grid>
            </TabItem>
            <TabItem Tag="t:tabTrophies" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <DockPanel Grid.Row="0">
                        <CheckBox x:Name="chkTroOnlyMine" DockPanel.Dock="Right" Tag="t:onlyMine" Margin="14,0,0,10" VerticalAlignment="Center"/>
                        <TextBox x:Name="txtTroSearch" Tag="w:search"/>
                    </DockPanel>
                    <DataGrid x:Name="dgTrophies" Grid.Row="1" EnableRowVirtualization="True">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="t:fish" Binding="{Binding Fish}" Width="300"/>
                            <DataGridTextColumn Header="t:trophy" Binding="{Binding Trophy}" SortMemberPath="TrophySort" Width="140"/>
                            <DataGridTextColumn Header="t:rare" Binding="{Binding Rare}" SortMemberPath="RareSort" Width="150"/>
                            <DataGridTextColumn Header="t:myBest" Binding="{Binding MyBest}" SortMemberPath="MyBestSort" Width="140"/>
                            <DataGridTextColumn Header="t:status" Binding="{Binding Mark}" Width="*">
                                <DataGridTextColumn.ElementStyle>
                                    <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#FFE0B040"/><Setter Property="FontSize" Value="15"/></Style>
                                </DataGridTextColumn.ElementStyle>
                            </DataGridTextColumn>
                        </DataGrid.Columns>
                    </DataGrid>
                </Grid>
            </TabItem>
            <TabItem Tag="t:tabRecipes" Style="{StaticResource SubTab}">
                <Grid Margin="0,10,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="320"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid Grid.Column="0" Margin="0,0,14,0">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="*"/>
                            <RowDefinition Height="Auto"/>
                        </Grid.RowDefinitions>
                        <TextBox x:Name="txtRecSearch" Grid.Row="0" Tag="w:search"/>
                        <Border Grid.Row="1" Style="{StaticResource Card}" Padding="6">
                            <ListBox x:Name="lstRecipes" Background="Transparent" FontSize="14" ScrollViewer.HorizontalScrollBarVisibility="Disabled"><ListBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding Label}" TextWrapping="Wrap" Margin="0,2,0,2"/></DataTemplate></ListBox.ItemTemplate></ListBox>
                        </Border>
                        <Button x:Name="btnRecNew" Grid.Row="2" Tag="t:new" Margin="0,10,0,0" HorizontalAlignment="Left"/>
                    </Grid>
                    <Border Grid.Column="1" Style="{StaticResource Card}">
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                            <StackPanel MaxWidth="640" HorizontalAlignment="Left" Width="640">
                                <TextBlock Tag="t:tabRecipes" Style="{StaticResource Heading}"/>
                                <TextBlock Tag="t:recipeName" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtRecName"/>
                                <TextBlock Tag="t:type" Style="{StaticResource Label}"/>
                                <ComboBox x:Name="cmbRecType" Width="220" HorizontalAlignment="Left"/>
                                <TextBlock Tag="t:fishList" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtRecFish"/>
                                <TextBlock Tag="t:lakes" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtRecLakes"/>
                                <TextBlock Tag="t:composition" Style="{StaticResource Label}" FontWeight="SemiBold" Margin="0,6,0,6"/>
                                <Border BorderBrush="{DynamicResource AppLine}" BorderThickness="1" CornerRadius="6" Padding="10,10,10,0" Margin="0,0,0,12">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="110"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
                                    </Grid.RowDefinitions>
                                    <TextBlock Grid.Row="0" Grid.Column="0" Tag="t:base" x:Name="lblRecBase" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="0" Grid.Column="1" x:Name="txtRecBase"/>
                                    <TextBlock Grid.Row="1" Grid.Column="0" x:Name="lblRecAdd1" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="1" Grid.Column="1" x:Name="txtRecAdd1"/>
                                    <TextBlock Grid.Row="2" Grid.Column="0" x:Name="lblRecAdd2" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="2" Grid.Column="1" x:Name="txtRecAdd2"/>
                                    <TextBlock Grid.Row="3" Grid.Column="0" x:Name="lblRecAdd3" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="3" Grid.Column="1" x:Name="txtRecAdd3"/>
                                    <TextBlock Grid.Row="4" Grid.Column="0" x:Name="lblRecAdd4" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="4" Grid.Column="1" x:Name="txtRecAdd4"/>
                                    <TextBlock Grid.Row="5" Grid.Column="0" Tag="t:attractant" x:Name="lblRecAttr" Style="{StaticResource Label}" VerticalAlignment="Center" Margin="0,0,10,10"/>
                                    <TextBox Grid.Row="5" Grid.Column="1" x:Name="txtRecAttr"/>
                                </Grid>
                                </Border>
                                <TextBlock Tag="t:notes" Style="{StaticResource Label}"/>
                                <TextBox x:Name="txtRecNotes" Height="70" TextWrapping="Wrap" AcceptsReturn="True"/>
                                <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                                    <Button x:Name="btnRecSave" Tag="t:save" Style="{StaticResource PrimaryButton}"/>
                                    <Button x:Name="btnRecDelete" Tag="t:delete" Style="{StaticResource DangerButton}"/>
                                </StackPanel>
                            </StackPanel>
                        </ScrollViewer>
                    </Border>
                </Grid>
            </TabItem>
                </TabControl>
            </TabItem>

            <TabItem Tag="t:tabCommunity">
                <Grid Margin="0,10,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <WrapPanel Grid.Row="0" Margin="0,0,0,10">
                        <ComboBox x:Name="cmbWebSite" Width="300" Margin="0,0,10,0"/>
                        <ComboBox x:Name="cmbWebLake" Width="230" Margin="0,0,10,0"/>
                        <Button x:Name="btnWebOpen" Tag="t:open" Style="{StaticResource PrimaryButton}"/>
                        <Button x:Name="btnWebBack" Tag="t:back"/>
                        <Button x:Name="btnWebExternal" Tag="t:openInBrowser"/>
                    </WrapPanel>
                    <Border x:Name="webHost" Grid.Row="1" Background="#FF0E1216" CornerRadius="8"/>
                    <Border Grid.Row="2" Style="{StaticResource Card}" Margin="0,10,0,0" Padding="12,10,12,0">
                        <DockPanel>
                            <TextBlock DockPanel.Dock="Left" Tag="t:saveAsSpot" Foreground="{DynamicResource AppInk}" VerticalAlignment="Center" Margin="0,0,12,10"/>
                            <Button x:Name="btnWebSave" DockPanel.Dock="Right" Tag="t:save" Style="{StaticResource PrimaryButton}" Margin="8,0,0,10" VerticalAlignment="Top"/>
                            <TextBox x:Name="txtWebX" DockPanel.Dock="Left" Width="60" Margin="0,0,6,10" Controls:TextBoxHelper.Watermark="X"/>
                            <TextBox x:Name="txtWebY" DockPanel.Dock="Left" Width="60" Margin="0,0,10,10" Controls:TextBoxHelper.Watermark="Y"/>
                            <ComboBox x:Name="cmbWebFish" Tag="w:fish" DockPanel.Dock="Left" Width="220" Margin="0,0,10,10" IsEditable="True" TextSearch.TextPath="Label"/>
                            <ComboBox x:Name="cmbWebBait" Tag="w:bait" IsEditable="True" TextSearch.TextPath="Label" Margin="0,0,0,10"/>
                        </DockPanel>
                    </Border>
                </Grid>
            </TabItem>












        </TabControl>

        <TextBlock x:Name="txtStatus" Grid.Row="2" Foreground="{DynamicResource AppInkDim}" Margin="2,8,0,0"/>
    </Grid>
</Controls:MetroWindow>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [System.Windows.Markup.XamlReader]::Load($reader)

$xaml.SelectNodes("//*[@*[local-name()='Name']]") | ForEach-Object {
    $n = $_.GetAttribute("Name", "http://schemas.microsoft.com/winfx/2006/xaml")
    if ($n) { Set-Variable -Name $n -Value $window.FindName($n) -Scope Script }
}

$script:mapScale = $gridMap.LayoutTransform

$accent = [System.Windows.Media.Color]::FromRgb(0x4F, 0xA8, 0xC9)
$theme = [ControlzEx.Theming.RuntimeThemeGenerator]::Current.GenerateRuntimeTheme("Dark", $accent)
[ControlzEx.Theming.ThemeManager]::Current.ChangeTheme($window, $theme) | Out-Null

$script:colKeys = New-Object System.Collections.ArrayList
foreach ($dg in @($dgSpots, $dgCatches, $dgStats, $dgLakeFish, $dgTrophies, $dgWeek, $dgWeekBaits, $dgPrefBaits, $dgTgtSpots, $dgTgtBaits)) {
    foreach ($col in $dg.Columns) {
        if ($col.Header -is [string] -and $col.Header.StartsWith("t:")) {
            $script:colKeys.Add([pscustomobject]@{ Col = $col; Key = $col.Header.Substring(2) }) | Out-Null
        }
    }
}

$script:busy = $false
$script:imgCache = @{}
$script:scale = 0.4
$script:minScale = 0.2
$script:curMapLake = $null
$script:selSpotId = $null
$script:pending = $null
$script:panning = $false
$script:selRecId = $null
$script:techColors = @{
    "float" = "#FF6EC6FF"; "bottom" = "#FFE0A63A"; "spin" = "#FF7FD18B"
    "marine" = "#FFA88CFF"; "trolling" = "#FFB0FC00"; "" = "#FFF2F2F2"
}

function Set-Status([string]$text) { $txtStatus.Text = $text }

function Localize-Tree($el) {
    if ($el -is [System.Windows.FrameworkElement] -and $el.Tag -is [string]) {
        $tag = [string]$el.Tag
        if ($tag.StartsWith("t:")) {
            $k = $tag.Substring(2)
            if ($el -is [System.Windows.Controls.TextBlock]) { $el.Text = (T $k) }
            elseif ($el -is [System.Windows.Controls.HeaderedContentControl]) { $el.Header = (T $k) }
            elseif ($el -is [System.Windows.Controls.ContentControl]) { $el.Content = (T $k) }
        } elseif ($tag.StartsWith("w:")) {
            [MahApps.Metro.Controls.TextBoxHelper]::SetWatermark($el, (T $tag.Substring(2)))
        }
    }
    foreach ($c in [System.Windows.LogicalTreeHelper]::GetChildren($el)) {
        if ($c -is [System.Windows.DependencyObject]) { Localize-Tree $c }
    }
}

function Get-Spot([string]$id) {
    foreach ($s in $script:spots) { if ($s.id -eq $id) { return $s } }
    return $null
}

function Get-SpotLabel($s) {
    if ($s.name) { return $s.name }
    if ($s.fish) { return (N $s.fish) }
    return (T "spot")
}

function Fit-Map {
    $svMap.UpdateLayout()
    $vw = $svMap.ActualWidth
    $vh = $svMap.ActualHeight
    $s = 0.4
    if ($vw -gt 0 -and $vh -gt 0) { $s = [math]::Min($vw, $vh) / 2048 }
    $script:minScale = $s * 0.9
    Set-MapScale $s
}

function Set-MapScale([double]$s) {
    $script:scale = $s
    $mapScale.ScaleX = $s
    $mapScale.ScaleY = $s
    Draw-Markers
}

function Draw-Markers {
    $canvasMarkers.Children.Clear()
    $lake = $script:curMapLake
    if (-not $lake) { return }
    Draw-CommMarkers
    $sc = $script:scale
    foreach ($s in $script:spots) {
        if ($s.lake -ne $lake.id) { continue }
        $isSel = ($s.id -eq $script:selSpotId)
        $size = 16 / $sc
        if ($isSel) { $size = 24 / $sc }
        $color = $script:techColors["$($s.tech)"]
        if (-not $color) { $color = $script:techColors[""] }
        $e = New-Object System.Windows.Shapes.Ellipse
        $e.Width = $size
        $e.Height = $size
        $e.Fill = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($color))
        if ($isSel) { $e.Stroke = [System.Windows.Media.Brushes]::White; $e.StrokeThickness = 4 / $sc }
        else { $e.Stroke = [System.Windows.Media.Brushes]::Black; $e.StrokeThickness = 2 / $sc }
        $e.Tag = $s.id
        $e.Cursor = [System.Windows.Input.Cursors]::Hand
        $e.ToolTip = (Get-SpotLabel $s) + "  " + (Format-Coords $lake $s.nx $s.ny)
        [System.Windows.Controls.Canvas]::SetLeft($e, ([double]$s.nx * 2048)-($size / 2))
        [System.Windows.Controls.Canvas]::SetTop($e, ([double]$s.ny * 2048)-($size / 2))
        $canvasMarkers.Children.Add($e) | Out-Null

        $lbl = New-Object System.Windows.Controls.Border
        $lbl.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(200, 12, 16, 20))
        $lbl.CornerRadius = New-Object System.Windows.CornerRadius (4 / $sc)
        $lbl.Padding = New-Object System.Windows.Thickness (5 / $sc), (1 / $sc), (5 / $sc), (1 / $sc)
        $lbl.IsHitTestVisible = $false
        $tb = New-Object System.Windows.Controls.TextBlock
        $tb.Text = Get-SpotLabel $s
        $tb.FontSize = 12 / $sc
        $tb.Foreground = [System.Windows.Media.Brushes]::White
        $lbl.Child = $tb
        [System.Windows.Controls.Canvas]::SetLeft($lbl, ([double]$s.nx * 2048) + ($size / 2) + (3 / $sc))
        [System.Windows.Controls.Canvas]::SetTop($lbl, ([double]$s.ny * 2048)-(9 / $sc))
        $canvasMarkers.Children.Add($lbl) | Out-Null
    }
    if ($script:searchMark) {
        $size = 34 / $sc
        $ring = New-Object System.Windows.Shapes.Ellipse
        $ring.Width = $size
        $ring.Height = $size
        $ring.Stroke = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#FF00E5FF"))
        $ring.StrokeThickness = 4 / $sc
        $ring.StrokeDashArray = [System.Windows.Media.DoubleCollection]::Parse("2 1")
        $ring.IsHitTestVisible = $false
        [System.Windows.Controls.Canvas]::SetLeft($ring, ($script:searchMark.NX * 2048)-($size / 2))
        [System.Windows.Controls.Canvas]::SetTop($ring, ($script:searchMark.NY * 2048)-($size / 2))
        $canvasMarkers.Children.Add($ring) | Out-Null
    }
    if ($script:pending) {
        $size = 22 / $sc
        $r = New-Object System.Windows.Shapes.Ellipse
        $r.Width = $size
        $r.Height = $size
        $r.Stroke = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#FF4FA8C9"))
        $r.StrokeThickness = 4 / $sc
        $r.IsHitTestVisible = $false
        [System.Windows.Controls.Canvas]::SetLeft($r, ($script:pending.NX * 2048)-($size / 2))
        [System.Windows.Controls.Canvas]::SetTop($r, ($script:pending.NY * 2048)-($size / 2))
        $canvasMarkers.Children.Add($r) | Out-Null
    }
    Draw-Ruler
}

$script:rulerA = $null
$script:rulerB = $null
$script:rulerLive = $null

function Clear-Ruler {
    $script:rulerA = $null
    $script:rulerB = $null
    $script:rulerLive = $null
    Draw-Ruler
}

function Draw-Ruler {
    $canvasRuler.Children.Clear()
    $lake = $script:curMapLake
    if (-not $lake -or -not $script:rulerA) { return }
    $sc = $script:scale
    $a = $script:rulerA
    $b = $script:rulerB
    if (-not $b) { $b = $script:rulerLive }
    if (-not $b) { $b = $a }
    $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#FFFFD54F"))
    $line = New-Object System.Windows.Shapes.Line
    $line.X1 = $a.NX * 2048
    $line.Y1 = $a.NY * 2048
    $line.X2 = $b.NX * 2048
    $line.Y2 = $b.NY * 2048
    $line.Stroke = $brush
    $line.StrokeThickness = 3 / $sc
    if (-not $script:rulerB) { $line.StrokeDashArray = [System.Windows.Media.DoubleCollection]::Parse("3 2") }
    $canvasRuler.Children.Add($line) | Out-Null
    foreach ($pt in @($a, $b)) {
        $size = 10 / $sc
        $dot = New-Object System.Windows.Shapes.Ellipse
        $dot.Width = $size
        $dot.Height = $size
        $dot.Fill = $brush
        $dot.Stroke = [System.Windows.Media.Brushes]::Black
        $dot.StrokeThickness = 1.5 / $sc
        [System.Windows.Controls.Canvas]::SetLeft($dot, ($pt.NX * 2048)-($size / 2))
        [System.Windows.Controls.Canvas]::SetTop($dot, ($pt.NY * 2048)-($size / 2))
        $canvasRuler.Children.Add($dot) | Out-Null
    }
    $ga = To-Game $lake $a.NX $a.NY
    $gb = To-Game $lake $b.NX $b.NY
    $dx = $gb.X-$ga.X
    $dy = $gb.Y-$ga.Y
    $meters = [math]::Sqrt(($dx * $dx) + ($dy * $dy)) * 5
    $lbl = New-Object System.Windows.Controls.Border
    $lbl.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(230, 12, 16, 20))
    $lbl.BorderBrush = $brush
    $lbl.BorderThickness = New-Object System.Windows.Thickness (1.5 / $sc)
    $lbl.CornerRadius = New-Object System.Windows.CornerRadius (4 / $sc)
    $lbl.Padding = New-Object System.Windows.Thickness (6 / $sc), (2 / $sc), (6 / $sc), (2 / $sc)
    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = "{0:N0} m" -f $meters
    $tb.FontSize = 15 / $sc
    $tb.FontWeight = [System.Windows.FontWeights]::SemiBold
    $tb.Foreground = $brush
    $lbl.Child = $tb
    [System.Windows.Controls.Canvas]::SetLeft($lbl, ($b.NX * 2048) + (10 / $sc))
    [System.Windows.Controls.Canvas]::SetTop($lbl, ($b.NY * 2048) + (8 / $sc))
    $canvasRuler.Children.Add($lbl) | Out-Null
}

function Center-On([double]$nx, [double]$ny) {
    $svMap.UpdateLayout()
    $svMap.ScrollToHorizontalOffset(($nx * 2048 * $script:scale)-($svMap.ViewportWidth / 2))
    $svMap.ScrollToVerticalOffset(($ny * 2048 * $script:scale)-($svMap.ViewportHeight / 2))
}

function Update-BottomPanel {
    if ((Get-ComboKey $cmbSpotTech) -eq "bottom") {
        $panelBottom.Visibility = "Visible"
        $lblSpotBait.Text = T "bait1"
    } else {
        $panelBottom.Visibility = "Collapsed"
        $lblSpotBait.Text = T "bait"
    }
}

function Get-RecipeChoices([string]$type) {
    @($script:recipes | Where-Object { $_.type -eq $type } | ForEach-Object { New-Choice $_.id $_.name } | Sort-Object Label)
}

function Refresh-SpotRecipeChoices {
    foreach ($pair in @(@($cmbSpotGround, "groundbait"), @($cmbSpotPva, "pva"))) {
        $cb = $pair[0]
        $keepItem = $cb.SelectedItem
        $keepText = "$($cb.Text)"
        Set-Choices $cb (Get-RecipeChoices $pair[1])
        if ($keepItem) { Set-ComboKey $cb $keepItem.Key } else { $cb.Text = $keepText }
    }
}

function Get-RecipeFieldValue($combo) {
    if ($combo.SelectedItem) { return [string]$combo.SelectedItem.Label }
    return "$($combo.Text)".Trim()
}

function Set-RecipeField($combo, [string]$name) {
    $m = @($combo.ItemsSource) | Where-Object { $_.Label -eq $name } | Select-Object -First 1
    if ($m) { $combo.SelectedItem = $m } else { $combo.SelectedItem = $null; $combo.Text = $name }
}

function Open-RecipeFromSpot($combo, [string]$type) {
    $name = Get-RecipeFieldValue $combo
    $r = $null
    if ($combo.SelectedItem) { $r = Get-Recipe $combo.SelectedItem.Key }
    if (-not $r -and $name) {
        foreach ($x in $script:recipes) { if ($x.name -eq $name -and $x.type -eq $type) { $r = $x; break } }
    }
    if (-not $r) {
        if (-not $name) { $name = T $type }
        $r = [pscustomobject]@{ id = (New-Id); name = $name; type = $type; fish = @(); lakes = @(); base = ""; additives = @(); attractant = ""; notes = "" }
        if ($script:curMapLake) { $r.lakes = @($script:curMapLake.name.en) }
        $f = Get-ComboKey $cmbSpotFish
        if ($f) { $r.fish = @($f) }
        $script:recipes.Add($r) | Out-Null
        Save-User
        Refresh-SpotRecipeChoices
        Set-RecipeField $combo $r.name
        Set-Status ("{0}: {1}" -f (T "recipeCreated"), $r.name)
    }
    $script:selRecId = $r.id
    $txtRecSearch.Text = ""
    Select-TabByTag "t:tabRecipes"
    Refresh-Recipes
}

function Clear-SpotForm {
    $script:selSpotId = $null
    $script:pending = $null
    foreach ($cb in @($cmbSpotBait2, $cmbSpotDip, $cmbSpotGround, $cmbSpotPva)) { $cb.SelectedItem = $null; $cb.Text = "" }
    $txtSpotName.Text = ""
    $cmbSpotFish.SelectedItem = $null
    $cmbSpotFish.Text = ""
    $cmbSpotBait.SelectedItem = $null
    $cmbSpotBait.Text = ""
    Set-ComboKey $cmbSpotTech ""
    Set-ComboKey $cmbSpotTemp ""
    $txtSpotDir.Text = ""
    $chkSpotShare.IsChecked = $false
    $txtSpotDepth.Text = ""
    $txtSpotDist.Text = ""
    $txtSpotNotes.Text = ""
    $txtSpotX.Text = ""
    $txtSpotY.Text = ""
    $btnSpotDelete.IsEnabled = $false
    $script:spotFormOpen = $false
    Update-MapPanel
}

function Fill-SpotForm($s) {
    $txtSpotName.Text = "$($s.name)"
    Set-ComboKey $cmbSpotFish "$($s.fish)"
    Set-ComboKey $cmbSpotBait "$($s.bait)"
    Set-ComboKey $cmbSpotTech "$($s.tech)"
    Set-ComboKey $cmbSpotTemp "$($s.temp)"
    $txtSpotDir.Text = "$($s.dir)"
    $chkSpotShare.IsChecked = [bool]$s.share
    $txtSpotDepth.Text = "$($s.depth)"
    $txtSpotDist.Text = "$($s.dist)"
    $txtSpotNotes.Text = "$($s.notes)"
    Set-ComboKey $cmbSpotBait2 "$($s.bait2)"
    Set-ComboKey $cmbSpotDip "$($s.dip)"
    Set-RecipeField $cmbSpotGround "$($s.groundbait)"
    Set-RecipeField $cmbSpotPva "$($s.pva)"
    Update-BottomPanel
    $g = To-Game $script:curMapLake ([double]$s.nx) ([double]$s.ny)
    $txtSpotX.Text = [string][int][math]::Round($g.X)
    $txtSpotY.Text = [string][int][math]::Round($g.Y)
    $btnSpotDelete.IsEnabled = $true
    Update-MapPanel
}

function Refresh-LakeSpotList {
    $script:busy = $true
    $items = @()
    if ($script:curMapLake) {
        foreach ($s in $script:spots) {
            if ($s.lake -ne $script:curMapLake.id) { continue }
            $items += New-Choice $s.id ((Get-SpotLabel $s) + "   " + (Format-Coords $script:curMapLake $s.nx $s.ny))
        }
    }
    $lstLakeSpots.ItemsSource = @($items | Sort-Object Label)
    $sel = @($lstLakeSpots.ItemsSource) | Where-Object { $_.Key -eq $script:selSpotId } | Select-Object -First 1
    $lstLakeSpots.SelectedItem = $sel
    $script:busy = $false
    if ($items.Count -eq 0) { Set-Status (T "noSpots") }
}

function Load-MapLake([string]$lakeId) {
    $lake = $script:lakeById[$lakeId]
    if (-not $lake -or -not $lake.mapKey) { return }
    $script:curMapLake = $lake
    $file = Join-Path $dataDir ("maps\" + $lake.mapKey + ".jpg")
    if (Test-Path $file) {
        $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
        $bmp.BeginInit()
        $bmp.UriSource = New-Object System.Uri $file
        $bmp.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bmp.EndInit()
        $bmp.Freeze()
        $imgMap.Source = $bmp
    } else {
        $imgMap.Source = $null
    }
    $script:rulerA = $null
    $script:rulerB = $null
    $script:rulerLive = $null
    $script:searchMark = $null
    $keepTech = Get-ComboKey $cmbSpotTech
    Set-Choices $cmbSpotFish (Get-FishChoices $lake.id)
    Clear-SpotForm
    Set-ComboKey $cmbSpotTech $keepTech
    if ($script:commSel) { Hide-CommCluster }
    $script:busy = $true
    Refresh-CommFishChoices
    Set-ComboKey $cmbCommFish ""
    $script:busy = $false
    Fit-Map
    Refresh-LakeSpotList
}

function Select-Spot([string]$id, [switch]$Center) {
    $s = Get-Spot $id
    if (-not $s) { return }
    if (-not $script:curMapLake -or $script:curMapLake.id -ne $s.lake) {
        $script:busy = $true
        Set-ComboKey $cmbMapLake $s.lake
        $script:busy = $false
        Load-MapLake $s.lake
    }
    $script:selSpotId = $s.id
    $script:pending = $null
    Fill-SpotForm $s
    Draw-Markers
    Refresh-LakeSpotList
    if ($Center) { Center-On ([double]$s.nx) ([double]$s.ny) }
}

function Refresh-SpotsGrid {
    $q = "$($txtSpotsSearch.Text)".Trim().ToLower()
    $lf = Get-ComboKey $cmbSpotsLake
    $src = Get-ComboKey $cmbSpotsSource
    $btnSpotsDelete.IsEnabled = ($src -eq "mine" -or -not $src)
    if ($src -and $src -ne "mine") {
        $dgSpots.ItemsSource = Get-SpotsGridCommunityRows $lf $q
        return
    }
    $rows = @()
    $cq = Parse-CoordQuery $q
    foreach ($s in $script:spots) {
        if ($lf -and $s.lake -ne $lf) { continue }
        $lake = $script:lakeById[$s.lake]
        if ($cq) {
            if (-not $lake -or -not $lake.bounds) { continue }
            $g = To-Game $lake ([double]$s.nx) ([double]$s.ny)
            if ([math]::Abs([math]::Round($g.X)-$cq.X) -gt 2 -or [math]::Abs([math]::Round($g.Y)-$cq.Y) -gt 2) { continue }
        }
        $tech = ""
        if ($s.tech) { $tech = T "tech_$($s.tech)" }
        $baitText = (@($s.bait, $s.bait2) | Where-Object { $_ } | ForEach-Object { N $_ }) -join " + "
        if ($s.dip) { $baitText = "{0}  ({1}: {2})" -f $baitText, (T "dipL"), (N $s.dip) }
        $row = [pscustomobject]@{
            Id = $s.id; Lake = (Get-LakeName $s.lake); Name = "$($s.name)"; Fish = (N $s.fish); Bait = $baitText
            Tech = $tech; Shared = $(if ($s.share) { [string][char]0x2713 } else { "" }); Coords = (Format-Coords $lake $s.nx $s.ny); Depth = (((@("$($s.dist)".Trim(), "$($s.depth)".Trim()) -join " / ").TrimEnd(" /")) + $(if ("$($s.dir)".Trim()) { "  " + "$($s.dir)".Trim() } else { "" })); Notes = "$($s.notes)"
        }
        if ($q -and -not $cq) {
            $hay = ("{0} {1} {2} {3} {4} {5} {6}" -f $row.Lake, $row.Name, $row.Fish, $row.Bait, $row.Tech, $row.Notes, $s.fish).ToLower()
            if (-not $hay.Contains($q)) { continue }
        }
        $rows += $row
    }
    $dgSpots.ItemsSource = @($rows | Sort-Object Lake, Name)
}

function Get-ClipNum([string]$t) {
    $m = [regex]::Match("$t", "\d+([\.,]\d+)?")
    if ($m.Success) { return $m.Value.Replace(",", ".") }
    return ""
}

function Find-SpotAt([string]$lakeId, $x, $y, [string]$clip = "", [string]$dir = "", [switch]$AnyCast) {
    $lake = $script:lakeById[$lakeId]
    if (-not $lake -or -not $lake.bounds -or $null -eq $x -or $null -eq $y) { return $null }
    $cn = Get-ClipNum $clip
    $dn = "$dir".Trim().ToLower()
    $loose = $null
    foreach ($sp in $script:spots) {
        if ($sp.lake -ne $lakeId) { continue }
        $g = To-Game $lake ([double]$sp.nx) ([double]$sp.ny)
        if ([math]::Abs([math]::Round($g.X)-[double]$x) -gt 2 -or [math]::Abs([math]::Round($g.Y)-[double]$y) -gt 2) { continue }
        if ($AnyCast) { return $sp }
        $sc = Get-ClipNum "$($sp.dist)"
        $sd = "$($sp.dir)".Trim().ToLower()
        $clipOk = (-not $cn -or -not $sc -or $cn -eq $sc)
        $dirOk = (-not $dn -or -not $sd -or $dn -eq $sd)
        if (-not ($clipOk -and $dirOk)) { continue }
        if ($cn -eq $sc -and $dn -eq $sd) { return $sp }
        if (-not $loose) { $loose = $sp }
    }
    return $loose
}

function Update-CatchSpotInfo {
    $x = Parse-Num $txtCatchX.Text
    $y = Parse-Num $txtCatchY.Text
    $lakeId = Get-ComboKey $cmbCatchLake
    if ($null -eq $x -or $null -eq $y) { $lblCatchSpot.Text = T "spotNameOpt"; return }
    $sp = Find-SpotAt $lakeId $x $y $txtCatchClip.Text $txtCatchDir.Text
    if ($sp) {
        $lblCatchSpot.Text = (T "spotExisting") + ": " + (Get-SpotLabel $sp)
        if (-not "$($txtCatchSpotName.Text)".Trim()) { $txtCatchSpotName.Text = "$($sp.name)" }
    } else {
        $lblCatchSpot.Text = T "spotAuto"
    }
}

function Get-FieldLabel([string]$k) {
    switch ($k) {
        "lake" { T "lake" } "fish" { T "fish" } "weight" { T "weightG" } "coords" { T "coords" } "bait" { T "bait1" }
        "bait2" { T "bait2" } "dip" { T "dipL" } "pva" { T "pvaL" } "clip" { T "distance" } "depth" { T "depth" }
        "temp" { T "temperature" } "name" { T "spotName" } "tech" { T "technique" } "notesSpin" { (T "notes") + " (" + (T "notesSpinHint") + ")" } default { $k }
    }
}

function Get-TechMissing([string]$tech, [string]$clip, [string]$depth, [string]$notes) {
    $miss = @()
    if ($tech -eq "bottom" -and -not $clip.Trim()) { $miss += "clip" }
    if ($tech -eq "float" -and -not $depth.Trim()) { $miss += "depth" }
    if ($tech -eq "spin" -and (-not $notes.Trim() -or $notes.Trim() -eq "Tracker")) { $miss += "notesSpin" }
    $miss
}

function Update-NotesHint($techCombo, $notesBox) {
    $h = ""
    if ((Get-ComboKey $techCombo) -eq "spin") { $h = T "notesSpinHint" }
    [MahApps.Metro.Controls.TextBoxHelper]::SetWatermark($notesBox, $h)
}

function Get-MissingCatchFields($f, $need) {
    $miss = @()
    if (-not "$($f.lake)") { $miss += "lake" }
    if (-not "$($f.fish)") { $miss += "fish" }
    if ($null -eq $f.x -or $null -eq $f.y -or "$($f.x)" -eq "" -or "$($f.y)" -eq "") { $miss += "coords" }
    if (-not "$($f.tech)") { $miss += "tech" }
    if (-not "$($f.bait)") { $miss += "bait" }
    foreach ($k in @("bait2", "dip", "pva")) { if ($need -and $need[$k] -and -not "$($f.$k)") { $miss += $k } }
    $miss += @(Get-TechMissing "$($f.tech)" "$($f.clip)" "$($f.depth)" "$($f.notes)")
    if (-not "$($f.temp)") { $miss += "temp" }
    $miss
}

function Get-SetupNeeds($p) {
    if ($p) { return @{ bait2 = (@($p.Baits | Where-Object { $_ }).Count -ge 2); dip = [bool]"$($p.Dip)"; pva = [bool]"$($p.Pva)" } }
    $st = $script:trackerSetup
    if ($st -and $chkTracker.IsChecked) { return @{ bait2 = (@($st.Baits | Where-Object { $_ }).Count -ge 2); dip = [bool]"$($st.Dip)"; pva = [bool]"$($st.Pva)" } }
    return @{}
}

function Show-MissingFields($keys) {
    $names = @($keys | Select-Object -Unique | ForEach-Object { "  " + [char]0x2022 + " " + (Get-FieldLabel $_) })
    [System.Windows.MessageBox]::Show(((T "missingFields") + "`n`n" + ($names -join "`n")), (T "appTitle")) | Out-Null
}

function Test-SpotFields([string]$name, $x, $y, [string]$tech, [string]$fish, [string]$bait, [string]$dist, [string]$depth, [string]$temp, [string]$notes) {
    $miss = @()
    if (-not $name) { $miss += "name" }
    if ($null -eq $x -or $null -eq $y) { $miss += "coords" }
    if (-not $tech) { $miss += "tech" }
    if (-not $fish) { $miss += "fish" }
    if (-not $bait) { $miss += "bait" }
    $miss += @(Get-TechMissing $tech $dist $depth $notes)
    if (-not $temp) { $miss += "temp" }
    if ($miss.Count -gt 0) { Show-MissingFields $miss; return $false }
    return $true
}

function Resolve-CatchSpot($f) {
    if ($null -eq $f.x -or $null -eq $f.y -or "$($f.x)" -eq "" -or "$($f.y)" -eq "") { return "" }
    $lake = $script:lakeById[$f.lake]
    if (-not $lake -or -not $lake.bounds) { return "" }
    $sp = Find-SpotAt $f.lake $f.x $f.y "$($f.clip)" "$($f.dir)"
    if ($sp) {
        if ($f.spotName -and -not $sp.name) { $sp.name = $f.spotName }
        foreach ($pair in @(@("dist", $f.clip), @("depth", $f.depth), @("temp", $f.temp), @("fish", $f.fish), @("bait", $f.bait), @("tech", $f.tech), @("dir", $f.dir))) {
            if ("$($pair[1])" -ne "" -and "$($sp.($pair[0]))" -eq "") { $sp | Add-Member -NotePropertyName $pair[0] -NotePropertyValue "$($pair[1])" -Force }
        }
        return $sp.id
    }
    $n = From-Game $lake ([double]$f.x) ([double]$f.y)
    $autoName = "$($f.spotName)"
    if (-not $autoName -and (Find-SpotAt $f.lake $f.x $f.y -AnyCast)) {
        $autoName = (@($(if (Get-ClipNum "$($f.clip)") { (Get-ClipNum "$($f.clip)") + " m" } else { "" }), "$($f.dir)".Trim()) | Where-Object { $_ }) -join " "
    }
    $tech = "$($f.tech)"
    if (-not $tech -and ($f.bait2 -or $f.dip -or $f.pva)) { $tech = "bottom" }
    $sp = [pscustomobject]@{
        id = (New-Id); lake = $f.lake; name = $autoName; fish = $f.fish; bait = $f.bait; bait2 = "$($f.bait2)"; dip = "$($f.dip)"
        groundbait = ""; pva = "$($f.pva)"; tech = $tech; depth = "$($f.depth)"; dist = "$($f.clip)"; temp = "$($f.temp)"; dir = "$($f.dir)"; share = [bool]$chkCatchShare.IsChecked; notes = ""; nx = [double]$n.NX; ny = [double]$n.NY
    }
    $script:spots.Add($sp) | Out-Null
    return $sp.id
}

function Refresh-CatchSpotChoices {
    Update-CatchSpotInfo
}

function Old-RefreshCatchSpotChoices {
    $lakeId = Get-ComboKey $cmbCatchLake
    $items = @(New-Choice "" "")
    foreach ($s in $script:spots) {
        if ($s.lake -eq $lakeId) { $items += New-Choice $s.id (Get-SpotLabel $s) }
    }
    $items | Out-Null
}

function Format-Date([string]$iso) {
    try {
        $d = [datetime]::ParseExact($iso, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
        return Format-LocalDate $d
    } catch { return $iso }
}

function Refresh-Catches {
    $q = "$($txtCatchSearch.Text)".Trim().ToLower()
    $rows = @()
    foreach ($c in $script:catches) {
        $spotName = ""
        if ($c.spotId) {
            $sp = Get-Spot $c.spotId
            if ($sp) {
                $spotName = Get-SpotLabel $sp
                $lk = $script:lakeById[$sp.lake]
                if ($lk -and $lk.bounds) { $spotName = "{0}  {1}" -f $spotName, (Format-Coords $lk $sp.nx $sp.ny) }
            }
        } elseif ($null -ne $c.x -and "$($c.x)" -ne "") { $spotName = "{0}:{1}" -f $c.x, $c.y }
        $row = [pscustomobject]@{
            Id = $c.id; Date = (Format-Date $c.date); DateSort = "$($c.date)"; Lake = (Get-LakeName $c.lake); Fish = (N $c.fish)
            Weight = (Format-Weight $c.weight); WeightSort = [int]$c.weight; Mark = (Get-TrophyMark $c.fish $c.weight)
            Bait = ((@($c.bait, $c.bait2) | Where-Object { $_ } | ForEach-Object { N $_ }) -join " + ") + $(if ($c.dip) { "  (" + (T "dipL") + ": " + (N $c.dip) + ")" } else { "" }); Spot = $spotName; Notes = "$($c.notes)"
        }
        if ($q) {
            $hay = ("{0} {1} {2} {3} {4}" -f $row.Lake, $row.Fish, $row.Bait, $row.Spot, $row.Notes).ToLower()
            if (-not $hay.Contains($q)) { continue }
        }
        $rows += $row
    }
    if ($chkCatchGroup.IsChecked) {
        $grows = @()
        foreach ($grp in ($rows | Group-Object { "{0}|{1}|{2}" -f $_.DateSort, $_.Lake, $_.Spot })) {
            $g = @($grp.Group)
            if ($g.Count -eq 1) { $grows += $g[0]; continue }
            $tot = ($g | Measure-Object -Property WeightSort -Sum).Sum
            $stars = @($g | Where-Object { $_.Mark }).Count
            $baitTop = @($g | Where-Object { $_.Bait } | Group-Object Bait | Sort-Object Count -Descending | Select-Object -First 1)
            $grows += [pscustomobject]@{
                Id = "g|" + $grp.Name; Ids = @($g | ForEach-Object { $_.Id }); Date = $g[0].Date; DateSort = $g[0].DateSort; Lake = $g[0].Lake
                Fish = ("{0} {1}: " -f $g.Count, (T "fishCount")) + ((@($g | Group-Object Fish | Sort-Object Count -Descending | ForEach-Object { "{0}× {1}" -f $_.Count, $_.Name })) -join ", ")
                Weight = (Format-Weight $tot); WeightSort = [int]$tot; Mark = $(if ($stars -gt 0) { "★ " + $stars } else { "" })
                Bait = $(if ($baitTop.Count -gt 0) { $baitTop[0].Name } else { "" }); Spot = $g[0].Spot; Notes = ""
            }
        }
        $rows = $grows
    }
    $dgCatches.ItemsSource = @($rows | Sort-Object DateSort, WeightSort -Descending)

    $groups = @{}
    foreach ($c in $script:catches) {
        if (-not $groups.ContainsKey($c.fish)) { $groups[$c.fish] = New-Object System.Collections.ArrayList }
        $groups[$c.fish].Add($c) | Out-Null
    }
    $stats = @()
    foreach ($f in $groups.Keys) {
        $list = $groups[$f]
        $max = ($list | Measure-Object -Property weight -Maximum).Maximum
        $bb = $list | Where-Object { $_.bait } | Group-Object bait | Sort-Object Count -Descending | Select-Object -First 1
        $bbName = ""
        if ($bb) { $bbName = N $bb.Name }
        $stats += [pscustomobject]@{ Fish = (N $f); Count = $list.Count; Best = (Format-Weight $max); BestSort = [int]$max; BestBait = $bbName }
    }
    $dgStats.ItemsSource = @($stats | Sort-Object Count -Descending)
    $txtCatchTotal.Text = "{0}: {1}" -f (T "catchesTotal"), $script:catches.Count
}

function New-TrophyRow($fish, $best) {
    $t = $script:trophyByFish[$fish]
    $tw = $null; $rw = $null
    if ($t) { $tw = $t.trophy; $rw = $t.rare }
    $mb = $null
    if ($best.ContainsKey($fish)) { $mb = $best[$fish] }
    $trSort = 0; if ($tw) { $trSort = [int]$tw }
    $raSort = 0; if ($rw) { $raSort = [int]$rw }
    $mbSort = 0; if ($mb) { $mbSort = [int]$mb }
    [pscustomobject]@{
        Fish = (N $fish)
        Trophy = (Format-Weight $tw); TrophySort = $trSort
        Rare = (Format-Weight $rw); RareSort = $raSort
        MyBest = (Format-Weight $mb); MyBestSort = $mbSort
        Mark = (Get-TrophyMark $fish $mb)
        FishKey = $fish
    }
}

function Refresh-Trophies {
    $best = Get-MyBest
    $q = "$($txtTroSearch.Text)".Trim().ToLower()
    $rows = @()
    foreach ($t in @($game.trophies)) {
        if ($chkTroOnlyMine.IsChecked -and -not $best.ContainsKey($t.fish)) { continue }
        $r = New-TrophyRow $t.fish $best
        if ($q -and -not $r.Fish.ToLower().Contains($q) -and -not $t.fish.ToLower().Contains($q)) { continue }
        $rows += $r
    }
    $dgTrophies.ItemsSource = @($rows | Sort-Object Fish)
}

function Show-Lake {
    $it = $lstLakes.SelectedItem
    if (-not $it) { return }
    $l = $script:lakeById[$it.Key]
    $txtLakeName.Text = $l.name.($script:lang)
    $txtLakeMeta.Text = "{0}: {1}" -f (T "fishHere"), @($l.fish).Count
    $d = $l.desc.($script:lang)
    if (-not $d) { $d = $l.desc.en }
    $txtLakeDesc.Text = "$d"
    if ($l.mapKey -and $l.bounds) { $btnLakeMap.Visibility = "Visible" } else { $btnLakeMap.Visibility = "Collapsed" }
    $best = Get-MyBest
    $rows = @()
    foreach ($f in @($l.fish)) { $rows += New-TrophyRow $f $best }
    $dgLakeFish.ItemsSource = @($rows | Sort-Object Fish)
}

function Refresh-Lakes {
    $keep = $null
    if ($lstLakes.SelectedItem) { $keep = $lstLakes.SelectedItem.Key }
    $items = @()
    foreach ($c in (Get-LakeChoices)) {
        $items += New-Choice $c.Key $c.Label
    }
    $lstLakes.ItemsSource = $items
    if (-not $keep) { $keep = "mosquito_lake" }
    $lstLakes.SelectedItem = @($items) | Where-Object { $_.Key -eq $keep } | Select-Object -First 1
    Show-Lake
}

function Join-Names($list) {
    (@($list) | Where-Object { $_ } | ForEach-Object { N $_ }) -join ", "
}

function Join-Lakes($list) {
    (@($list) | Where-Object { $_ } | ForEach-Object {
        $n = $_
        $m = @($game.lakes) | Where-Object { $_.name.en -eq $n } | Select-Object -First 1
        if ($m) { $m.name.($script:lang) } else { N $n }
    }) -join ", "
}

function Split-Names([string]$text) {
    @($text -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { Resolve-Name $_ })
}

function Split-Lakes([string]$text) {
    @($text -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { Get-LakeIdByAnyName $_ })
}

function Get-Recipe([string]$id) {
    foreach ($r in $script:recipes) { if ($r.id -eq $id) { return $r } }
    return $null
}

function Update-RecipeLabels {
    $n = 1
    foreach ($l in @($lblRecAdd1, $lblRecAdd2, $lblRecAdd3, $lblRecAdd4)) { $l.Text = "{0} {1}" -f (T "additive"), $n; $n++ }
}

function Clear-RecipeForm {
    $script:selRecId = $null
    $txtRecName.Text = ""
    Set-ComboKey $cmbRecType "groundbait"
    $txtRecFish.Text = ""
    $txtRecLakes.Text = ""
    foreach ($tb in @($txtRecBase, $txtRecAdd1, $txtRecAdd2, $txtRecAdd3, $txtRecAdd4, $txtRecAttr)) { $tb.Text = "" }
    $txtRecNotes.Text = ""
    $btnRecDelete.IsEnabled = $false
}

function Show-Recipe {
    $r = Get-Recipe $script:selRecId
    if (-not $r) { Clear-RecipeForm; return }
    $txtRecName.Text = "$($r.name)"
    Set-ComboKey $cmbRecType "$($r.type)"
    $txtRecFish.Text = Join-Names $r.fish
    $txtRecLakes.Text = Join-Lakes $r.lakes
    $txtRecBase.Text = N $r.base
    $adds = @($r.additives | Where-Object { $_ })
    $boxes = @($txtRecAdd1, $txtRecAdd2, $txtRecAdd3, $txtRecAdd4)
    for ($i = 0; $i -lt 4; $i++) { if ($i -lt $adds.Count) { $boxes[$i].Text = N $adds[$i] } else { $boxes[$i].Text = "" } }
    $txtRecAttr.Text = N $r.attractant
    $txtRecNotes.Text = "$($r.notes)"
    $btnRecDelete.IsEnabled = $true
}

function Refresh-Recipes {
    $script:busy = $true
    $q = "$($txtRecSearch.Text)".Trim().ToLower()
    $items = @()
    foreach ($r in $script:recipes) {
        $lbl = "{0}   ({1})" -f $r.name, (T "$($r.type)")
        if ($q) {
            $hay = ("{0} {1} {2}" -f $r.name, (Join-Names $r.fish), (Join-Lakes $r.lakes)).ToLower()
            if (-not $hay.Contains($q)) { continue }
        }
        $items += New-Choice $r.id $lbl
    }
    $lstRecipes.ItemsSource = @($items | Sort-Object Label)
    $lstRecipes.SelectedItem = @($lstRecipes.ItemsSource) | Where-Object { $_.Key -eq $script:selRecId } | Select-Object -First 1
    $script:busy = $false
    Show-Recipe
}

$script:weekJs = @'
(function(){var tries=0;function go(){var subs=document.querySelectorAll('.records_subtable');if(!subs.length){tries++;if(tries<60){setTimeout(go,500);}return;}var out=[];subs.forEach(function(t){var h=t.querySelector('.row.header');if(!h){return;}var fe=h.querySelector('.fish .text');var fish=fe?fe.textContent.trim():'';var rows=[h].concat(Array.prototype.slice.call(t.querySelectorAll('.rows .row')));rows.forEach(function(r){var w=r.querySelector('.weight');if(!w){return;}var g=function(c){var e=r.querySelector(c);return e?e.textContent.replace(/\u00a0/g,' ').trim():'';};var baits=Array.prototype.slice.call(r.querySelectorAll('.bait_icon')).map(function(b){return (b.getAttribute('title')||'').replace(/\u00a0/g,' ');});out.push({fish:fish,weight:g('.weight'),loc:g('.location'),bait:baits.join(';'),player:g('.gamername'),date:g('.data')});});});window.chrome.webview.postMessage(JSON.stringify(out));}go();})();
'@

$script:wvData = $null
$script:wvWeb = $null
$script:weekLoading = $false
$script:weekly = @()
$script:weekTime = $null
$script:weekRegions = @("GL", "EN", "DE", "RU", "US", "PL", "FR", "CN", "JP", "KR", "ID")

$script:weekTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:weekTimer.Interval = [TimeSpan]::FromSeconds(45)
$script:weekTimer.Add_Tick({
    $script:weekTimer.Stop()
    if ($script:weekLoading) {
        $script:weekLoading = $false
        $btnWeekLoad.IsEnabled = $true
        $txtWeekState.Text = T "loadFailed"
    }
})

function New-WebView([string]$folder, [string]$language) {
    $wv = New-Object Microsoft.Web.WebView2.Wpf.WebView2
    $cp = New-Object Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
    $cp.UserDataFolder = Join-Path $userDir $folder
    if ($language) { $cp.Language = $language }
    $wv.CreationProperties = $cp
    $wv
}

function Navigate-WebView($wv, [string]$url) {
    if ($wv.CoreWebView2) { $wv.CoreWebView2.Navigate($url) }
    else { $wv.Source = New-Object System.Uri $url }
}

function Ensure-DataWebView {
    if ($script:wvData) { return $true }
    if (-not $script:hasWebView) { return $false }
    try {
        $wv = New-WebView "webview_data" "en-US"
        $wv.Width = 1
        $wv.Height = 1
        $wv.HorizontalAlignment = "Right"
        $wv.VerticalAlignment = "Bottom"
        [System.Windows.Controls.Grid]::SetRow($wv, 2)
        $rootGrid.Children.Add($wv) | Out-Null
        $wv.Add_NavigationCompleted({
            param($sender, $e)
            if ($script:harvCur) {
                if ($e.IsSuccess) { $script:wvData.ExecuteScriptAsync($script:weekJs) | Out-Null }
                return
            }
            if ($script:weekLoading -and $e.IsSuccess) { $script:wvData.ExecuteScriptAsync($script:weekJs) | Out-Null }
        })
        $wv.Add_WebMessageReceived({
            param($sender, $e)
            if ($script:harvCur) { Receive-HarvestPage ($e.TryGetWebMessageAsString()); return }
            if (-not $script:weekLoading) { return }
            Receive-Weekly ($e.TryGetWebMessageAsString())
        })
        $script:wvData = $wv
        return $true
    } catch {
        return $false
    }
}

function Parse-RecordWeight([string]$s) {
    $x = $s.Replace([string][char]0xA0, " ").Trim().ToLower()
    $d = Parse-Num ($x -replace "[^0-9\.]", "")
    if ($null -eq $d) { return 0 }
    if ($x.EndsWith("kg")) { return [int][math]::Round($d * 1000) }
    return [int][math]::Round($d)
}

function Get-LakeIdByLocation([string]$loc) {
    $t = $loc.Trim().ToLower()
    foreach ($l in @($game.lakes)) {
        foreach ($p in $l.name.PSObject.Properties) {
            if ("$($p.Value)".ToLower() -eq $t) { return $l.id }
        }
    }
    return ""
}

function Get-WeeklyCacheFile([string]$region) {
    Join-Path $userDir ("weekly_" + $region + ".json")
}

function Save-WeeklyCache {
    $obj = [ordered]@{ region = $script:weekRegion; time = $script:weekTime.ToString("o"); items = @($script:weekly) }
    $json = ConvertTo-Json -InputObject $obj -Depth 5 -Compress
    [System.IO.File]::WriteAllText((Get-WeeklyCacheFile $script:weekRegion), $json, (New-Object System.Text.UTF8Encoding $false))
}

function Load-WeeklyCache {
    $script:weekly = @()
    $script:weekTime = $null
    $f = Get-WeeklyCacheFile $script:weekRegion
    if (-not (Test-Path $f)) { return }
    try {
        $c = Read-Json $f
        $seen = @{}
        $script:weekly = @($c.items | Where-Object { $_ } | Where-Object {
            $k = "{0}|{1}|{2}|{3}" -f $_.fish, $_.weight, $_.player, $_.date
            if ($seen.ContainsKey($k)) { $false } else { $seen[$k] = $true; $true }
        })
        $script:weekTime = [datetime]::Parse($c.time, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    } catch {
        $script:weekly = @()
    }
}

function Receive-Weekly([string]$raw) {
    if (-not $raw) { return }
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = 67108864
    $arr = $ser.DeserializeObject($raw)
    if (-not $arr -or $arr.Count -eq 0) { return }
    $seen = @{}
    $items = New-Object System.Collections.ArrayList
    foreach ($o in $arr) {
        $key = "{0}|{1}|{2}|{3}" -f $o["fish"], (Parse-RecordWeight ([string]$o["weight"])), $o["player"], $o["date"]
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $items.Add([pscustomobject]@{
            fish = [string]$o["fish"]; weight = (Parse-RecordWeight ([string]$o["weight"]))
            lake = (Get-LakeIdByLocation ([string]$o["loc"])); loc = [string]$o["loc"]
            bait = [string]$o["bait"]; player = [string]$o["player"]; date = [string]$o["date"]
        }) | Out-Null
    }
    $keepFrom = (Get-Date).Date.AddDays(-14)
    foreach ($o in @($script:weekly)) {
        if (-not $o) { continue }
        $key = "{0}|{1}|{2}|{3}" -f $o.fish, $o.weight, $o.player, $o.date
        if ($seen.ContainsKey($key)) { continue }
        $od = Parse-RecordDate "$($o.date)"
        if (-not $od -or $od -lt $keepFrom) { continue }
        $seen[$key] = $true
        $items.Add($o) | Out-Null
    }
    $script:weekly = @($items)
    $script:weekTime = Get-Date
    $learned = $false
    foreach ($it in $items) {
        if ($it.lake -and $it.fish) {
            $canon = $it.fish
            foreach ($t in @($game.trophies)) { if ($t.fish -eq $it.fish) { $canon = $t.fish; break } }
            if (Add-LearnedFish $it.lake $canon) { $learned = $true }
        }
    }
    if ($learned) { Save-LearnedFish }
    $script:weekLoading = $false
    $script:weekTimer.Stop()
    $btnWeekLoad.IsEnabled = $true
    Save-WeeklyCache
    if (-not $script:weekFromCloud) { $script:weeklyStale = $true }
    Refresh-Weekly
}

$script:archDir = Join-Path $userDir "archive"
$script:archTables = @(@{ K = "n"; P = "records" }, @{ K = "l"; P = "recordslight" }, @{ K = "b"; P = "bottomlight" })
$script:archWeeks = @{}
$script:archDirty = @{}
$script:archCurId = ""
$script:harvQueue = New-Object System.Collections.ArrayList
$script:harvCur = $null
$script:harvTotal = 0
$script:lastHarvest = $null
$script:intelBackfill = @{}
$script:intelMap = @{}

function Write-GzJson([string]$path, $obj) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((New-Serializer).Serialize($obj))
    $tmp = $path + ".tmp"
    $fs = [System.IO.File]::Create($tmp)
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionMode]::Compress)
    $gz.Write($bytes, 0, $bytes.Length)
    $gz.Dispose()
    $fs.Dispose()
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Read-GzJson([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionMode]::Decompress)
    $sr = New-Object System.IO.StreamReader($gz, [System.Text.Encoding]::UTF8)
    $t = $sr.ReadToEnd()
    $sr.Dispose()
    $fs.Dispose()
    (New-Serializer).DeserializeObject($t)
}

function Get-WeekFile([string]$id) { Join-Path $script:archDir ("week_" + $id + ".json.gz") }

function Get-ArchWeek([string]$id) {
    if ($script:archWeeks.ContainsKey($id)) { return $script:archWeeks[$id] }
    $map = @{}
    $f = Get-WeekFile $id
    if (Test-Path -LiteralPath $f) {
        try {
            $d = Read-GzJson $f
            foreach ($it in $d["items"]) { $map[[string]$it["k"]] = $it }
        } catch { Write-ErrorLog ("Archiv lesen: " + $f + " " + $_.Exception.Message) }
    }
    $script:archWeeks[$id] = $map
    $map
}

function Save-ArchWeeks {
    if (-not (Test-Path -LiteralPath $script:archDir)) { New-Item -ItemType Directory -Path $script:archDir | Out-Null }
    foreach ($id in @($script:archDirty.Keys)) {
        try { Write-GzJson (Get-WeekFile $id) @{ v = 1; week = $id; items = @($script:archWeeks[$id].Values) } } catch { Write-ErrorLog ("Archiv schreiben: " + $_.Exception.Message) }
    }
    $script:archDirty = @{}
    $st = @{ lastHarvest = $(if ($script:lastHarvest) { $script:lastHarvest.ToUniversalTime().ToString("o") } else { "" }); current = $script:archCurId }
    try { [System.IO.File]::WriteAllText((Join-Path $script:archDir "state.json"), (New-Serializer).Serialize($st), (New-Object System.Text.UTF8Encoding $false)) } catch { }
}

function Load-ArchState {
    $f = Join-Path $script:archDir "state.json"
    if (-not (Test-Path -LiteralPath $f)) { return }
    try {
        $d = (New-Serializer).DeserializeObject([System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8))
        if ($d["lastHarvest"]) { $script:lastHarvest = [datetime]::Parse([string]$d["lastHarvest"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime() }
        if ($d["current"]) { $script:archCurId = [string]$d["current"] }
    } catch { }
}

function Get-WeekIdForDate([datetime]$d) { $d.Date.AddDays(-[int]$d.DayOfWeek).ToString("yyyy-MM-dd") }

function Get-UtcNowIso { (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }

function Get-WeekResetUtc([string]$id) {
    $min = ""
    foreach ($it in (Get-ArchWeek $id).Values) { $fs = [string]$it["fs"]; if ($fs -and (-not $min -or $fs -lt $min)) { $min = $fs } }
    if (-not $min) { return $null }
    [datetime]::Parse($min, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
}

function Start-Harvest {
    if ($script:harvCur -or $script:weekLoading) { return }
    if (-not (Ensure-DataWebView)) { return }
    $script:harvQueue.Clear()
    $regions = @($script:weekRegion) + @($script:weekRegions | Where-Object { $_ -ne $script:weekRegion })
    foreach ($tb in $script:archTables) { foreach ($r in $regions) { $script:harvQueue.Add([pscustomobject]@{ T = $tb.K; R = $r; Url = ("https://rf4game.com/{0}/weekly/region/{1}/" -f $tb.P, $r) }) | Out-Null } }
    $script:harvTotal = $script:harvQueue.Count
    $btnPrefHarvest.IsEnabled = $false
    Next-HarvestPage
}

function Next-HarvestPage {
    $script:harvPageTimer.Stop()
    if ($script:harvQueue.Count -eq 0) { Finish-Harvest; return }
    $script:harvCur = $script:harvQueue[0]
    $script:harvQueue.RemoveAt(0)
    Update-PrefState
    Navigate-WebView $script:wvData $script:harvCur.Url
    $script:harvPageTimer.Start()
}

function Receive-HarvestPage([string]$raw) {
    $cur = $script:harvCur
    $script:harvPageTimer.Stop()
    try {
        $arr = (New-Serializer).DeserializeObject($raw)
        $items = @($arr)
        if ($items.Count -gt 0) {
            $minD = $null
            foreach ($o in $items) { $d = Parse-RecordDate ([string]$o["date"]); if ($d -and (-not $minD -or $d -lt $minD)) { $minD = $d } }
            if ($minD) {
                $id = Get-WeekIdForDate $minD
                if (-not $script:archCurId -or $id -gt $script:archCurId) { $script:archCurId = $id }
                $map = Get-ArchWeek $id
                $now = [string](Get-UtcNowIso)
                foreach ($o in $items) {
                    $w = Parse-RecordWeight ([string]$o["weight"])
                    $k = "{0}|{1}|{2}|{3}|{4}|{5}" -f $cur.T, $cur.R, $o["fish"], $w, $o["player"], $o["date"]
                    if ($map.ContainsKey($k)) { $map[$k]["ls"] = $now; continue }
                    $map[$k] = @{ k = [string]$k; t = [string]$cur.T; r = [string]$cur.R; f = [string]$o["fish"]; w = [int]$w; l = [string](Get-LakeIdByLocation ([string]$o["loc"])); b = [string]$o["bait"]; p = [string]$o["player"]; d = [string]$o["date"]; fs = $now; ls = $now }
                }
                $script:archDirty[$id] = $true
            }
        }
        if ($cur.T -eq "n" -and $cur.R -eq $script:weekRegion) { Receive-Weekly $raw }
    } catch {
        Write-ErrorLog ("Archiv Seite " + $cur.Url + ": " + $_.Exception.Message)
    }
    $script:harvCur = $null
    $script:harvNextTimer.Start()
}

function Finish-Harvest {
    $script:harvCur = $null
    $script:lastHarvest = Get-Date
    Save-ArchWeeks
    $btnPrefHarvest.IsEnabled = $true
    Update-PrefState
    Refresh-Prefs
    $script:weeklyStale = $true
    Refresh-Weekly
    Start-IntelBackfill
}

function Start-IntelBackfill {
    $id = $script:archCurId
    if (-not $id -or $script:intelTask) { return }
    $last = $script:intelBackfill[$id]
    if ($last -and ((Get-Date)-$last).TotalHours -lt 6) { return }
    $script:intelBackfill[$id] = Get-Date
    $script:intelOffset = 0
    $script:intelMap = @{}
    Request-IntelPage
}

function Request-IntelPage {
    $url = "https://rf4intel.com/api/records?category=Weekly&limit=500&offset={0}" -f $script:intelOffset
    $script:intelTask = $script:http.GetStringAsync($url)
    $script:intelTimer.Start()
}

function Apply-IntelBackfill {
    $map = Get-ArchWeek $script:archCurId
    $n = 0
    foreach ($it in $map.Values) {
        if ($it["t"] -ne "n" -or $it["r"] -ne "EN") { continue }
        $d = Parse-RecordDate ([string]$it["d"])
        if (-not $d) { continue }
        $key = "{0}|{1}" -f $it["w"], $d.ToString("yyyy-MM-dd")
        $fs = $script:intelMap[$key]
        if ($fs -and $fs -lt [string]$it["fs"]) { $it["fs"] = [string]$fs; $n++ }
    }
    if ($n -gt 0) { $script:archDirty[$script:archCurId] = $true; Save-ArchWeeks; Refresh-Prefs }
}

$script:intelTask = $null
$script:intelTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:intelTimer.Interval = [TimeSpan]::FromMilliseconds(400)
$script:intelTimer.Add_Tick({
    $t = $script:intelTask
    if (-not $t -or -not $t.IsCompleted) { return }
    $script:intelTimer.Stop()
    $script:intelTask = $null
    if ($t.IsFaulted -or $t.IsCanceled) { Write-ErrorLog "rf4intel Wochenrekorde: Abruf fehlgeschlagen"; return }
    try {
        $d = (New-Serializer).DeserializeObject($t.Result)
        $recs = @($d["records"])
        foreach ($r in $recs) {
            $fs = [string]$r["first_seen_at"]
            if (-not $fs) { continue }
            $fsz = [datetime]::Parse($fs, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal).ToString("yyyy-MM-ddTHH:mm:ssZ")
            $key = "{0}|{1}" -f $r["weight_grams"], $r["caught_date"]
            if (-not $script:intelMap.ContainsKey($key) -or $fsz -lt $script:intelMap[$key]) { $script:intelMap[$key] = $fsz }
        }
        $script:intelOffset += $recs.Count
        if ($d["has_more"] -and $recs.Count -gt 0 -and $script:intelOffset -lt 20000) { Request-IntelPage; return }
        Apply-IntelBackfill
    } catch { Write-ErrorLog ("rf4intel Wochenrekorde: " + $_.Exception.Message) }
})

$script:harvPageTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:harvPageTimer.Interval = [TimeSpan]::FromSeconds(50)
$script:harvPageTimer.Add_Tick({
    $script:harvPageTimer.Stop()
    if ($script:harvCur) { Write-ErrorLog ("Archiv Zeitueberschreitung: " + $script:harvCur.Url); $script:harvCur = $null }
    $script:harvNextTimer.Start()
})

$script:harvNextTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:harvNextTimer.Interval = [TimeSpan]::FromSeconds(2)
$script:harvNextTimer.Add_Tick({
    $script:harvNextTimer.Stop()
    Next-HarvestPage
})

function Get-HarvestIntervalMinutes {
    $now = Get-Date
    $sunday = Get-WeekIdForDate $now
    if ($now.DayOfWeek -eq [System.DayOfWeek]::Sunday -and $now.Hour -ge 12 -and $script:archCurId -ne $sunday) { return 30 }
    if ($script:archCurId) {
        $rs = Get-WeekResetUtc $script:archCurId
        if ($rs -and ((Get-Date).ToUniversalTime()-$rs).TotalHours -lt 12) { return 30 }
    }
    return 180
}

$script:harvCheckTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:harvCheckTimer.Interval = [TimeSpan]::FromSeconds(60)
$script:commAutoTry = $null
$script:harvCheckTimer.Add_Tick({
    if (-not $script:commSyncing -and (-not $script:commAutoTry -or ((Get-Date)-$script:commAutoTry).TotalMinutes -ge 10)) {
        if (-not $script:commSynced -or ((Get-Date).ToUniversalTime()-$script:commSynced.ToUniversalTime()).TotalMinutes -ge 30) {
            $script:commAutoTry = Get-Date
            Start-CommunitySync
        }
    }
    if (-not $script:cloudTask -and -not $script:cloudCur -and (-not $script:cloudLast -or ((Get-Date)-$script:cloudLast).TotalMinutes -ge 30)) { Start-CloudSync }
    if (-not $script:cloudError -and $script:cloudLast -and ((Get-Date)-$script:cloudLast).TotalHours -lt 6) { return }
    if ($script:harvCur -or $script:harvQueue.Count -gt 0 -or $script:weekLoading) { return }
    $iv = Get-HarvestIntervalMinutes
    if (-not $script:lastHarvest -or ((Get-Date)-$script:lastHarvest).TotalMinutes -ge $iv) { Start-Harvest }
})

$script:aromaRules = @(
    @("currant", "currant"), @("coconut", "coco"), @("banana", "banana"), @("strawberry", "strawberr"), @("vanilla", "vanill"),
    @("honey", "honey"), @("garlic", "garlic"), @("pepper", "pepper|spic|chili|chilli"), @("anise", "anis"),
    @("cream", "cream|chocolate|caramel|scopex|creme|brulee|milk|toffee"), @("fishy", "fish|krill|squid|halibut|crab|shrimp|mussel|tuna|salmon"),
    @("meat", "meat|liver|blood|sausage"), @("corn", "corn|maize"),
    @("fruit", "tutti|fruit|plum|cherry|mulberry|cranberry|pineapple|apricot|peach|berry|pear|apple|melon|mango|orange|lemon"),
    @("natural", "worm|maggot|caddis|leech|bread|dough|pea\b|semolina|barley|wheat|potato|cheese|grasshopper|cricket|beetle|frog|mouse|slug|snail|larva|nymph|shrimp|live")
)

function Get-BaitAromas([string]$bait) {
    $keys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($part in (Split-Baits $bait)) {
        $x = $part.ToLower()
        $hit = $false
        foreach ($rule in $script:aromaRules) { if ($x -match $rule[1]) { [void]$keys.Add($rule[0]); $hit = $true; break } }
        if (-not $hit) { [void]$keys.Add("other") }
    }
    @($keys)
}

function Get-PrefRecords {
    $week = Get-ComboKey $cmbPrefWeek
    $ids = @()
    if ($week -eq "all") {
        if (Test-Path -LiteralPath $script:archDir) { $ids = @(Get-ChildItem -LiteralPath $script:archDir -Filter "week_*.json.gz" | ForEach-Object { $_.Name.Substring(5, 10) }) }
        if ($script:archCurId -and $ids -notcontains $script:archCurId) { $ids += $script:archCurId }
    } elseif ($script:archCurId) {
        $cur = [datetime]::ParseExact($script:archCurId, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
        if ($week -eq "prev") { $ids = @($cur.AddDays(-7).ToString("yyyy-MM-dd")) } else { $ids = @($script:archCurId) }
    }
    $lake = Get-ComboKey $cmbPrefLake
    $tbl = Get-ComboKey $cmbPrefTable
    $win = Get-KeyDays (Get-ComboKey $cmbPrefWindow)
    $out = New-Object System.Collections.ArrayList
    foreach ($id in $ids) {
        $rs = Get-WeekResetUtc $id
        foreach ($it in (Get-ArchWeek $id).Values) {
            if ($lake -and $it["l"] -ne $lake) { continue }
            if ($tbl -and $it["t"] -ne $tbl) { continue }
            $h = $null
            if ($rs) {
                $seen = [datetime]::Parse([string]$it["fs"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
                $cd = Parse-RecordDate ([string]$it["d"])
                if ($cd) { $bound = $cd.Date.AddDays(1).ToUniversalTime(); if ($bound -lt $seen) { $seen = $bound } }
                $h = [math]::Max(0, ($seen-$rs).TotalHours)
            }
            if ($win -gt 0 -and ($null -eq $h -or $h -gt $win)) { continue }
            $out.Add([pscustomobject]@{ R = $it; H = $h }) | Out-Null
        }
    }
    $out
}

Add-Type -ReferencedAssemblies PresentationFramework, PresentationCore, WindowsBase, System.Xaml -TypeDefinition @"
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
public class RF4Heat : IValueConverter {
    public static double Max = 1;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value == null || value is DBNull) return Brushes.Transparent;
        double v = System.Convert.ToDouble(value);
        if (v <= 0) return Brushes.Transparent;
        double f = Math.Min(1.0, Math.Sqrt(v / Math.Max(1.0, Max)));
        byte a = (byte)(40 + 170 * f);
        return new SolidColorBrush(Color.FromArgb(a, 0x4F, 0xA8, 0xC9));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
}
"@
$script:heatConv = New-Object RF4Heat

function Refresh-Prefs {
    if (-not $dgPrefs) { return }
    $recs = @(Get-PrefRecords)
    $script:prefRecs = $recs
    $byFish = @{}
    $aromaTot = @{}
    foreach ($x in $recs) {
        $f = [string]$x.R["f"]
        if (-not $byFish.ContainsKey($f)) { $byFish[$f] = @{ N = 0; Baits = @{}; Aroma = @{} } }
        $e = $byFish[$f]
        $e.N = $e.N + 1
        foreach ($b in (Split-Baits ([string]$x.R["b"]))) { $e.Baits[$b.ToLower()] = $true }
        foreach ($a in (Get-BaitAromas ([string]$x.R["b"]))) {
            if ($e.Aroma.ContainsKey($a)) { $e.Aroma[$a]++ } else { $e.Aroma[$a] = 1 }
            if ($aromaTot.ContainsKey($a)) { $aromaTot[$a]++ } else { $aromaTot[$a] = 1 }
        }
    }
    $dt = New-Object System.Data.DataTable
    $cFish = "fish"
    $cRec = "rec"
    $cBait = "baits"
    $script:prefHeaders = @{ fish = (T "fish"); rec = (T "records"); baits = (T "prefDistinctBaits") }
    [void]$dt.Columns.Add($cFish, [string])
    [void]$dt.Columns.Add($cRec, [int])
    [void]$dt.Columns.Add($cBait, [int])
    $aromas = @($aromaTot.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { $_.Key })
    $script:prefAromaCols = @{}
    foreach ($a in $aromas) {
        $cn = "a_" + $a
        [void]$dt.Columns.Add($cn, [int])
        $script:prefAromaCols[$a] = $cn
        $script:prefHeaders[$cn] = T ("aroma_" + $a)
    }
    $script:prefFishByLabel = @{}
    foreach ($f in $byFish.Keys) {
        $e = $byFish[$f]
        $row = $dt.NewRow()
        $lbl = N $f
        $script:prefFishByLabel[$lbl] = $f
        $row[$cFish] = $lbl
        $row[$cRec] = $e.N
        $row[$cBait] = $e.Baits.Count
        foreach ($a in $aromas) {
            if ($e.Aroma.ContainsKey($a)) { $row[$script:prefAromaCols[$a]] = $e.Aroma[$a] } else { $row[$script:prefAromaCols[$a]] = [DBNull]::Value }
        }
        $dt.Rows.Add($row)
    }
    $mx = 1
    foreach ($e in $byFish.Values) { foreach ($v in $e.Aroma.Values) { if ($v -gt $mx) { $mx = $v } } }
    [RF4Heat]::Max = $mx
    $dv = $dt.DefaultView
    $dv.Sort = "[" + $cRec + "] DESC"
    $dgPrefs.ItemsSource = $dv
    $dgPrefBaits.ItemsSource = $null
    $txtPrefFish.Text = T "prefPickFish"
    if ($dv.Count -gt 0) { $dgPrefs.SelectedIndex = 0 }
    Update-PrefState
}

function Show-PrefFishBaits([string]$fish) {
    $baits = @{}
    foreach ($x in @($script:prefRecs)) {
        if ([string]$x.R["f"] -ne $fish) { continue }
        foreach ($b in (Split-Baits ([string]$x.R["b"]))) {
            $k = $b.ToLower()
            if (-not $baits.ContainsKey($k)) { $baits[$k] = [pscustomobject]@{ Name = $b; N = 0; H = $null } }
            $baits[$k].N++
            if ($null -ne $x.H -and ($null -eq $baits[$k].H -or $x.H -lt $baits[$k].H)) { $baits[$k].H = $x.H }
        }
    }
    $rows = @()
    foreach ($v in $baits.Values) {
        $rows += [pscustomobject]@{
            Bait = (N $v.Name); Count = $v.N; CountSort = $v.N
            First = $(if ($null -ne $v.H) { (T "prefHoursAfter") -f [math]::Max(0, [int][math]::Floor($v.H)) } else { "" }); FirstSort = $(if ($null -ne $v.H) { [double]$v.H } else { 1e9 })
        }
    }
    $dgPrefBaits.ItemsSource = @($rows | Sort-Object CountSort -Descending)
    $txtPrefFish.Text = N $fish
    $script:prefSelFish = $fish
}

function Update-PrefState {
    if (-not $txtPrefState) { return }
    if ($script:harvCur) {
        $done = $script:harvTotal-$script:harvQueue.Count
        $txtPrefState.Text = (T "harvesting") -f $done, $script:harvTotal
        return
    }
    $n = 0
    if ($script:archCurId) { $n = (Get-ArchWeek $script:archCurId).Count }
    $rs = $null
    if ($script:archCurId) { $rs = Get-WeekResetUtc $script:archCurId }
    $txtPrefState.Text = (T "prefState") -f $n, $(if ($rs) { $rs.ToLocalTime().ToString("ddd dd.MM. HH:mm", (Get-Culture)) } else { "?" }), $(if ($script:lastHarvest) { $script:lastHarvest.ToString("dd.MM. HH:mm") } else { "?" })
    if ($script:cloudError) { $txtPrefState.Text = $txtPrefState.Text + "   " + ((T "cloudFailed") -f $script:cloudError) }
    elseif ($script:cloudLast) { $txtPrefState.Text = $txtPrefState.Text + "   " + ((T "cloudOk") -f $script:cloudLast.ToString("dd.MM. HH:mm")) }
}

$script:cloudRepo = "Nalathan01/rf4-companion-data"
$script:cloudTokenFile = Join-Path $userDir "cloud_token.dat"
$script:cloudStateFile = Join-Path $userDir "cloud_sync2.json"
$script:cloudRebuilt = @{}
foreach ($old in @($script:cloudTokenFile, (Join-Path $userDir "cloud_sync.json"))) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue } }
$script:cloudShas = @{}
$script:cloudLast = $null
$script:cloudQueue = New-Object System.Collections.ArrayList
$script:cloudTask = $null
$script:cloudCur = $null
$script:cloudError = ""

function Load-CloudState {
    if (-not (Test-Path -LiteralPath $script:cloudStateFile)) { return }
    try {
        $d = (New-Serializer).DeserializeObject([System.IO.File]::ReadAllText($script:cloudStateFile, [System.Text.Encoding]::UTF8))
        if ($d["shas"]) { foreach ($k in $d["shas"].Keys) { $script:cloudShas[$k] = [string]$d["shas"][$k] } }
        if ($d["last"]) { $script:cloudLast = [datetime]::Parse([string]$d["last"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime() }
    } catch { }
}

function Save-CloudState {
    $o = @{ shas = $script:cloudShas; last = $(if ($script:cloudLast) { $script:cloudLast.ToUniversalTime().ToString("o") } else { "" }) }
    try { [System.IO.File]::WriteAllText($script:cloudStateFile, (New-Serializer).Serialize($o), (New-Object System.Text.UTF8Encoding $false)) } catch { }
}

function New-CloudRequest([string]$url) {
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Get), $url
    if ($url.StartsWith("https://api.github.com/")) {
        $req.Headers.Add("X-GitHub-Api-Version", "2022-11-28")
        $req.Headers.Add("Accept", "application/vnd.github+json")
    }
    $req
}

function Start-CloudSync {
    if ($script:cloudTask) { return }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $script:cloudQueue.Clear()
    $script:cloudRebuilt = @{}
    $script:cloudCur = @{ Kind = "list" }
    $script:cloudTask = $script:http.SendAsync((New-CloudRequest ("https://api.github.com/repos/{0}/contents/archive" -f $script:cloudRepo)))
    $script:cloudTimer.Start()
}

function Next-CloudFile {
    if ($script:cloudQueue.Count -eq 0) {
        $script:cloudCur = $null
        $script:cloudLast = Get-Date
        $script:cloudError = ""
        Save-CloudState
        Save-ArchWeeks
        if ($script:intelMap -and $script:intelMap.Count -gt 0) { Apply-IntelBackfill }
        Refresh-Prefs
        $script:weeklyStale = $true
        Refresh-Weekly
        Start-IntelBackfill
        return
    }
    $script:cloudCur = $script:cloudQueue[0]
    $script:cloudQueue.RemoveAt(0)
    $script:cloudTask = $script:http.SendAsync((New-CloudRequest $script:cloudCur.Url))
    $script:cloudTimer.Start()
}

function Merge-CloudWeek([string]$id, [byte[]]$bytes) {
    $ms = New-Object System.IO.MemoryStream (, $bytes)
    $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
    $sr = New-Object System.IO.StreamReader($gz, [System.Text.Encoding]::UTF8)
    $d = (New-Serializer).DeserializeObject($sr.ReadToEnd())
    $sr.Dispose()
    if (-not $script:cloudRebuilt.ContainsKey($id)) {
        $script:archWeeks[$id] = @{}
        $script:cloudRebuilt[$id] = $true
    }
    $map = Get-ArchWeek $id
    foreach ($it in $d["items"]) {
        $k = [string]$it["k"]
        if ($map.ContainsKey($k)) {
            $m = $map[$k]
            if ([string]$it["fs"] -lt [string]$m["fs"]) { $m["fs"] = [string]$it["fs"] }
            if ([string]$it["ls"] -gt [string]$m["ls"]) { $m["ls"] = [string]$it["ls"] }
            continue
        }
        $map[$k] = @{ k = $k; t = [string]$it["t"]; r = [string]$it["r"]; f = [string]$it["f"]; w = [int]$it["w"]; l = [string](Get-LakeIdByLocation ([string]$it["loc"])); b = [string]$it["b"]; d = [string]$it["d"]; fs = [string]$it["fs"]; ls = [string]$it["ls"] }
    }
    if (-not $script:archCurId -or $id -gt $script:archCurId) { $script:archCurId = $id }
    $script:archDirty[$id] = $true
}

$script:cloudTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:cloudTimer.Interval = [TimeSpan]::FromMilliseconds(400)
$script:cloudTimer.Add_Tick({
    $t = $script:cloudTask
    if (-not $t -or -not $t.IsCompleted) { return }
    $script:cloudTimer.Stop()
    $script:cloudTask = $null
    try {
        if ($t.IsFaulted -or $t.IsCanceled) { throw "Verbindung fehlgeschlagen" }
        $resp = $t.Result
        if (-not $resp.IsSuccessStatusCode) { throw ("HTTP " + [int]$resp.StatusCode) }
        if ($script:cloudCur.Kind -eq "list") {
            $arr = (New-Serializer).DeserializeObject($resp.Content.ReadAsStringAsync().Result)
            $present = @{}
            $byWeek = @{}
            $changed = @{}
            foreach ($f in $arr) {
                $n = [string]$f["name"]
                if ($n -notmatch "^(week|delta)_(\d{4}-\d{2}-\d{2})(_\d{8}T\d{4})?\.json\.gz$") { continue }
                $wid = $Matches[2]
                $present[$n] = $true
                if (-not $byWeek.ContainsKey($wid)) { $byWeek[$wid] = New-Object System.Collections.ArrayList }
                $byWeek[$wid].Add(@{ Kind = "week"; Name = $n; Id = $wid; Sha = [string]$f["sha"]; Url = [string]$f["download_url"] }) | Out-Null
                if ($script:cloudShas[$n] -ne [string]$f["sha"]) { $changed[$wid] = $true }
            }
            foreach ($k in @($script:cloudShas.Keys)) { if (-not $present.ContainsKey($k)) { $changed[($k -replace "^(week|delta)_(\d{4}-\d{2}-\d{2}).*$", '$2')] = $true } }
            foreach ($wid in ($changed.Keys | Sort-Object)) {
                if (-not $byWeek.ContainsKey($wid)) { continue }
                foreach ($x in ($byWeek[$wid] | Sort-Object { if ($_.Name.StartsWith("week_")) { 0 } else { 1 } }, { $_.Name })) { $script:cloudQueue.Add($x) | Out-Null }
            }
            foreach ($k in @($script:cloudShas.Keys)) { if (-not $present.ContainsKey($k)) { $script:cloudShas.Remove($k) } }
        } else {
            Merge-CloudWeek $script:cloudCur.Id ($resp.Content.ReadAsByteArrayAsync().Result)
            $script:cloudShas[$script:cloudCur.Name] = $script:cloudCur.Sha
        }
        Next-CloudFile
    } catch {
        $script:cloudError = "$($_.Exception.Message)"
        Write-ErrorLog ("Cloud-Archiv: " + $script:cloudError)
        $script:cloudCur = $null
        $script:cloudQueue.Clear()
        Update-PrefState
    }
})

$script:koiNames = @("Kohaku", "Hi Utsuri", "Orenji Ogon", "Narumi Asagi", "Mameshibori Goshiki", "Yotsushiro", "Midori goi", "Showa Sanshoku", "Taisho Sanke", "Tancho", "Shiro Utsuri", "Ki Utsuri", "Asagi", "Shusui", "Ogon", "Kujaku", "Chagoi", "Soragoi", "Karashigoi", "Benigoi", "Kumonryu", "Ochiba")

function Get-KoiInGame {
    $t = @($game.trophies | ForEach-Object { $_.fish })
    @($script:koiNames | Where-Object { $t -contains $_ })
}

function Get-TgtFishSet {
    $k = Get-ComboKey $cmbTgtFish
    if ($k -eq "grp:koi") { return @(Get-KoiInGame) }
    if ($k) { return @($k) }
    return @()
}

function Get-IsoAgeDays([string]$iso) {
    try { return ((Get-Date).ToUniversalTime()-[datetime]::Parse($iso, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)).TotalDays } catch { return 999 }
}

function Format-DayAge([datetime]$d) {
    $days = [int](((Get-Date).Date-$d.Date).TotalDays)
    if ($days -le 0) { return T "tgtToday" }
    if ($days -eq 1) { return T "tgtYesterday" }
    return (T "tgtDaysAgo") -f $days
}

function Select-TabByTag([string]$tag) {
    foreach ($ti in $tabs.Items) { if ($ti.Tag -eq $tag) { $tabs.SelectedItem = $ti; return } }
    foreach ($ti in $tabs.Items) {
        $inner = $ti.Content
        if ($inner -is [System.Windows.Controls.TabControl]) {
            foreach ($sub in $inner.Items) { if ($sub.Tag -eq $tag) { $tabs.SelectedItem = $ti; $inner.SelectedItem = $sub; return } }
        }
    }
}

function Update-MapPanel {
    if ($script:commSel) { $panelMapIdle.Visibility = "Collapsed"; return }
    $open = [bool]($script:selSpotId -or $script:pending -or $script:spotFormOpen)
    if ($open) {
        $svSpotForm.Visibility = "Visible"
        $panelSpotForm.Visibility = "Visible"
        $panelMapIdle.Visibility = "Collapsed"
        $lstLakeSpots.MaxHeight = 200
    } else {
        $svSpotForm.Visibility = "Collapsed"
        $panelMapIdle.Visibility = "Visible"
        $lstLakeSpots.MaxHeight = [double]::PositiveInfinity
    }
    $lstLakeSpots.Visibility = "Visible"
}

function Refresh-Target {
    if (-not $dgTgtSpots) { return }
    $set = @(Get-TgtFishSet)
    $script:tgtSet = $set
    if ($set.Count -eq 0) { $dgTgtSpots.ItemsSource = $null; $dgTgtBaits.ItemsSource = $null; $txtTgtVerdict.Text = T "tgtPick"; return }
    $lake = Get-ComboKey $cmbTgtLake
    $ids = @()
    if ($script:archCurId) {
        $ids = @($script:archCurId)
        $prev = ([datetime]::ParseExact($script:archCurId, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)).AddDays(-7).ToString("yyyy-MM-dd")
        if ($script:archWeeks.ContainsKey($prev) -or (Test-Path -LiteralPath (Get-WeekFile $prev))) { $ids += $prev }
    }
    $recs = New-Object System.Collections.ArrayList
    $lakeCount = @{}
    foreach ($id in $ids) {
        $rs = Get-WeekResetUtc $id
        foreach ($it in (Get-ArchWeek $id).Values) {
            if ($set -notcontains [string]$it["f"]) { continue }
            $l = [string]$it["l"]
            if ($id -eq $script:archCurId -and $l) { $lakeCount[$l] = 1 + [int]$lakeCount[$l] }
            $recs.Add([pscustomobject]@{ It = $it; Week = $id; Reset = $rs }) | Out-Null
        }
    }
    $repLake = @{}
    foreach ($r in $script:commReports) { foreach ($f in @($r["fish"])) { if ($set -contains [string]$f) { $k = [string]$r["lake"]; $repLake[$k] = 1 + [int]$repLake[$k]; break } } }
    if (-not $lake) {
        if ($lakeCount.Count -gt 0) { $lake = @($lakeCount.Keys | Sort-Object { $lakeCount[$_] } -Descending)[0] }
        elseif ($repLake.Count -gt 0) { $lake = @($repLake.Keys | Sort-Object { $repLake[$_] } -Descending)[0] }
    }
    $script:tgtLake = $lake
    $today = (Get-Date).Date
    $baits = @{}
    $nWeek = 0
    $nRecent = 0
    foreach ($x in $recs) {
        $it = $x.It
        if ($lake -and $it["l"] -ne $lake) { continue }
        $cd = Parse-RecordDate ([string]$it["d"])
        $isCur = $x.Week -eq $script:archCurId
        $recent = $cd -and $cd -ge $today.AddDays(-1)
        if ($isCur) { $nWeek++ }
        if ($recent) { $nRecent++ }
        $h = $null
        if ($isCur -and $x.Reset) {
            $seen = [datetime]::Parse([string]$it["fs"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            if ($cd) { $bound = $cd.Date.AddDays(1).ToUniversalTime(); if ($bound -lt $seen) { $seen = $bound } }
            $h = [math]::Max(0, ($seen-$x.Reset).TotalHours)
        }
        $bk = ((Split-Baits ([string]$it["b"]) | Sort-Object) -join " + ")
        if (-not $bk) { continue }
        if (-not $baits.ContainsKey($bk)) { $baits[$bk] = [pscustomobject]@{ N = 0; Cur = 0; Recent = 0; H = $null; Fish = @{}; Last = $null } }
        $e = $baits[$bk]
        $e.N++
        if ($cd -and ($null -eq $e.Last -or $cd -gt $e.Last)) { $e.Last = $cd }
        if ($isCur) { $e.Cur++ }
        if ($recent) { $e.Recent++ }
        if ($null -ne $h -and ($null -eq $e.H -or $h -lt $e.H)) { $e.H = $h }
        $e.Fish[[string]$it["f"]] = $true
    }
    $brows = @()
    foreach ($k in $baits.Keys) {
        $e = $baits[$k]
        $brows += [pscustomobject]@{
            Bait = ((@($k -split " \+ ") | ForEach-Object { N $_ }) -join " + "); Count = $e.N; CountSort = $e.N + $e.Recent * 3
            Recent = $(if ($e.Recent) { [string]$e.Recent } else { "" }); RecentSort = $e.Recent
            First = $(if ($null -ne $e.H) { (T "prefHoursAfter") -f [int][math]::Floor($e.H) } else { "" }); FirstSort = $(if ($null -ne $e.H) { [double]$e.H } else { 1e9 })
            Fish = $(if ($e.Fish.Count -gt 2) { (T "tgtKinds") -f $e.Fish.Count } else { (@($e.Fish.Keys) | ForEach-Object { N $_ }) -join ", " }); FishAll = ((@($e.Fish.Keys) | ForEach-Object { N $_ }) -join ", ")
            LastSeen = $(if ($e.Last) { Format-DayAge $e.Last } else { "" }); LastSeenSort = $(if ($e.Last) { $e.Last.ToString("yyyyMMdd") } else { "0" })
        }
    }
    $brows = @($brows | Sort-Object CountSort -Descending)
    $dgTgtBaits.ItemsSource = $brows
    $reports = New-Object System.Collections.ArrayList
    foreach ($r in $script:commReports) {
        if ($lake -and $r["lake"] -ne $lake) { continue }
        $hit = @(@($r["fish"]) | Where-Object { $set -contains [string]$_ })
        if ($hit.Count -eq 0 -or $null -eq $r["x"]) { continue }
        $bd = Clean-CommDetail ([string]$r["baitDetail"])
        if (-not $bd) { $bd = (@($r["bait"]) | Where-Object { $_ }) -join ", " }
        $reports.Add([pscustomobject]@{ X = [int]$r["x"]; Y = [int]$r["y"]; Age = (Get-IsoAgeDays ([string]$r["posted"])); Clip = $r["clip"]; Bait = $bd; Own = $false; Url = [string]$r["url"] }) | Out-Null
    }
    foreach ($c in $script:catches) {
        if ($set -notcontains [string]$c.fish -or ($lake -and $c.lake -ne $lake) -or $null -eq $c.x -or "$($c.x)" -eq "") { continue }
        $age = 999
        try { $age = ((Get-Date).Date-[datetime]::ParseExact($c.date, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)).TotalDays } catch { }
        $reports.Add([pscustomobject]@{ X = [int]$c.x; Y = [int]$c.y; Age = $age; Clip = $c.clip; Bait = ((@($c.bait, $c.bait2) | Where-Object { $_ } | ForEach-Object { N $_ }) -join " + "); Own = $true; Url = "" }) | Out-Null
    }
    $clusters = New-Object System.Collections.ArrayList
    foreach ($r in ($reports | Sort-Object Age)) {
        $cl = $null
        foreach ($c in $clusters) { if ([math]::Abs($c.X-$r.X) -le 2 -and [math]::Abs($c.Y-$r.Y) -le 2) { $cl = $c; break } }
        if (-not $cl) { $cl = [pscustomobject]@{ X = $r.X; Y = $r.Y; Items = (New-Object System.Collections.ArrayList) }; $clusters.Add($cl) | Out-Null }
        $cl.Items.Add($r) | Out-Null
    }
    $srows = @()
    foreach ($c in $clusters) {
        $score = 0.0
        foreach ($r in $c.Items) { $score += [math]::Exp(-[math]::Max(0, $r.Age) / 2.5) }
        $minAge = ($c.Items | Measure-Object Age -Minimum).Minimum
        $status = T "tgtOld"
        if ($minAge -le 3) { $status = T "tgtActive" } elseif ($minAge -le 7) { $status = T "tgtRecent" }
        $clips = @($c.Items | Where-Object { $null -ne $_.Clip -and "$($_.Clip)" -ne "" } | ForEach-Object { "$($_.Clip)" -replace "[^0-9,\.]", "" } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 2 | ForEach-Object { $_.Name })
        if ($clips.Count) { $clips = @(($clips -join ", ") + " m") }
        $modeXY = @($c.Items | Group-Object { "{0}:{1}" -f $_.X, $_.Y } | Sort-Object Count -Descending | Select-Object -First 1)[0].Name -split ":"
        $c.X = [int]$modeXY[0]
        $c.Y = [int]$modeXY[1]
        $bt = @($c.Items | Where-Object { $_.Bait } | Group-Object Bait | Sort-Object Count -Descending | Select-Object -First 1 | ForEach-Object { $_.Name })
        $own = @($c.Items | Where-Object { $_.Own }).Count
        $newest = @($c.Items | Sort-Object Age | Select-Object -First 1)[0]
        $link = @($c.Items | Where-Object { $_.Url } | Sort-Object Age | Select-Object -First 1)
        $url = $null
        $hostTxt = ""
        if ($link.Count) { $url = New-Object System.Uri $link[0].Url; $hostTxt = (Get-ReportHost $link[0].Url) + " " + [char]0x2197 }
        elseif ($newest.Own) { $hostTxt = T "tgtOwn" }
        $srows += [pscustomobject]@{
            Coords = ("{0}:{1}" -f $c.X, $c.Y); X = $c.X; Y = $c.Y; Status = $status; ScoreSort = $score
            Last = $(if ($minAge -lt 1) { T "tgtToday" } else { (T "tgtDaysAgo") -f [int][math]::Floor($minAge) }); LastSort = $minAge
            Count = $(if ($own) { "{0} ({1} {2})" -f $c.Items.Count, $own, (T "tgtOwn") } else { [string]$c.Items.Count })
            Clip = $(if ($clips.Count) { $clips[0] } else { "" }); Bait = $(if ($bt.Count) { Format-Detail $bt[0] } else { "" })
            Url = $url; Host = $hostTxt
        }
    }
    $srows = @($srows | Sort-Object ScoreSort -Descending)
    $fresh = @($srows | Where-Object { $_.LastSort -le 30 } | Select-Object -First 10)
    if ($fresh.Count -gt 0) { $srows = $fresh } else { $srows = @($srows | Select-Object -First 5) }
    $dgTgtSpots.ItemsSource = $srows
    $lines = @()
    $name = $(if ((Get-ComboKey $cmbTgtFish) -eq "grp:koi") { T "tgtKoiGroup" } else { N $set[0] })
    $lines += "{0}   {1}" -f $name, $(if ($lake) { Get-LakeName $lake } else { "" })
    if ($nWeek -gt 0) {
        $top = @($brows | Select-Object -First 2 | ForEach-Object { $_.Bait })
        $lines += (T "tgtWeekRecords") -f $nWeek, $nRecent, ($top -join "  |  ")
        if ($nRecent -eq 0) { $lines += T "tgtNoRecent" }
    } else {
        $lines += T "tgtNoRecords"
        if ($brows.Count -gt 0) { $lines += (T "tgtPrevCandidates") -f (($brows | Select-Object -First 2 | ForEach-Object { $_.Bait }) -join "  |  ") }
        elseif ((Get-ComboKey $cmbTgtFish) -ne "grp:koi" -and ($script:koiNames -contains $set[0])) { $lines += T "tgtTryKoiGroup" }
    }
    if ($srows.Count -gt 0) {
        $best = $srows[0]
        $lines += (T "tgtBestSpot") -f $best.Coords, $best.Status, $best.Last
        if ($best.LastSort -gt 3 -and ($set | Where-Object { $script:koiNames -contains $_ })) {
            $lines += T "tgtKoiMoved"
            if ((Get-ComboKey $cmbTgtFish) -ne "grp:koi") {
                $koi = @(Get-KoiInGame)
                $act = @{}
                foreach ($r in $script:commReports) {
                    if ($lake -and $r["lake"] -ne $lake) { continue }
                    if (-not (@(@($r["fish"]) | Where-Object { $koi -contains [string]$_ }).Count)) { continue }
                    if ((Get-IsoAgeDays ([string]$r["posted"])) -gt 3 -or $null -eq $r["x"]) { continue }
                    $key = $null
                    foreach ($k in $act.Keys) { $xy = $k -split ":"; if ([math]::Abs([int]$xy[0]-[int]$r["x"]) -le 2 -and [math]::Abs([int]$xy[1]-[int]$r["y"]) -le 2) { $key = $k; break } }
                    if (-not $key) { $key = "{0}:{1}" -f $r["x"], $r["y"]; $act[$key] = 0 }
                    $act[$key]++
                }
                if ($act.Count -gt 0) { $lines += (T "tgtOtherKoiActive") -f ((@($act.Keys | Sort-Object { $act[$_] } -Descending | Select-Object -First 3 | ForEach-Object { "{0} ({1})" -f $_, $act[$_] })) -join ", ") }
            }
        }
    } else {
        $lines += T "tgtNoSpots"
    }
    $txtTgtVerdict.Text = $lines -join [Environment]::NewLine
}

function Show-SpotOnMapAt([string]$lakeId, [int]$x, [int]$y, [string]$fish) {
    $l = $script:lakeById[$lakeId]
    if (-not $l -or -not $l.bounds) { return }
    $script:jumpLake = $lakeId
    $script:jumpFish = $fish
    $script:jumpXY = @($x, $y)
    $tabs.SelectedIndex = 0
    $window.Dispatcher.BeginInvoke([action]{
        $script:busy = $true
        Set-ComboKey $cmbMapLake $script:jumpLake
        $script:busy = $false
        Load-MapLake $script:jumpLake
        $chkCommunity.IsChecked = $true
        $script:busy = $true
        Set-ComboKey $cmbCommPeriod "all"
        Set-ComboKey $cmbCommFish $script:jumpFish
        $n = From-Game $script:curMapLake $script:jumpXY[0] $script:jumpXY[1]
        $script:searchMark = [pscustomobject]@{ NX = [double]$n.NX; NY = [double]$n.NY }
        $txtMapSearch.Text = "{0}:{1}" -f $script:jumpXY[0], $script:jumpXY[1]
        $script:busy = $false
        Draw-Markers
        $window.Dispatcher.BeginInvoke([action]{ Center-On ([double]$n.NX) ([double]$n.NY) }, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle) | Out-Null
        Set-Status ("{0}: {1}:{2}" -f (T "tgtSpots"), $script:jumpXY[0], $script:jumpXY[1])
    }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
}

function Start-WeeklyLoad {
    if (-not (Ensure-DataWebView)) { $txtWeekState.Text = T "webviewMissing"; return }
    if ($script:harvCur) { $txtWeekState.Text = T "harvestBusy"; return }
    $script:weekLoading = $true
    $btnWeekLoad.IsEnabled = $false
    $txtWeekState.Text = T "loading"
    Navigate-WebView $script:wvData ("https://rf4game.com/records/weekly/region/{0}/" -f $script:weekRegion)
    $script:weekTimer.Stop()
    $script:weekTimer.Start()
}

function Split-Baits([string]$bait) {
    @($bait -split ";" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$script:weeklyStale = $true
$script:weekFromCloud = $false

function Build-WeeklyFromArchive([string]$region) {
    if (-not $script:archCurId) { return $false }
    $cur = [datetime]::ParseExact($script:archCurId, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
    $ids = @($script:archCurId)
    $prev = $cur.AddDays(-7).ToString("yyyy-MM-dd")
    if ($script:archWeeks.ContainsKey($prev) -or (Test-Path -LiteralPath (Get-WeekFile $prev))) { $ids += $prev }
    $items = New-Object System.Collections.ArrayList
    foreach ($id in $ids) {
        foreach ($it in (Get-ArchWeek $id).Values) {
            if ($it["t"] -ne "n" -or $it["r"] -ne $region) { continue }
            $items.Add([pscustomobject]@{ fish = [string]$it["f"]; weight = [int]$it["w"]; lake = [string]$it["l"]; loc = ""; bait = [string]$it["b"]; player = [string]$it["p"]; date = [string]$it["d"] }) | Out-Null
        }
    }
    if ($items.Count -eq 0) { return $false }
    $script:weekly = @($items)
    $script:weekTime = $(if ($script:cloudLast) { $script:cloudLast } else { $script:lastHarvest })
    return $true
}

function Refresh-Weekly {
    if ($script:weeklyStale) {
        $script:weeklyStale = $false
        $script:weekFromCloud = Build-WeeklyFromArchive $script:weekRegion
        if (-not $script:weekFromCloud) { Load-WeeklyCache }
        $hasPlayers = @($script:weekly | Where-Object { $_.player } | Select-Object -First 1).Count -gt 0
        foreach ($c in $dgWeek.Columns) { if ($c.Binding -and $c.Binding.Path.Path -eq "Player") { $c.Visibility = $(if ($hasPlayers) { "Visible" } else { "Collapsed" }) } }
    }
    $lf = Get-ComboKey $cmbWeekLake
    $q = "$($txtWeekSearch.Text)".Trim().ToLower()
    $wd = Get-KeyDays (Get-ComboKey $cmbWeekDays)
    $minDate = $null
    if ($wd -gt 0) { $minDate = (Get-Date).Date.AddDays(1-$wd) }
    $rows = New-Object System.Collections.ArrayList
    $filtered = New-Object System.Collections.ArrayList
    foreach ($r in $script:weekly) {
        if ($lf -and $r.lake -ne $lf) { continue }
        if ($minDate) {
            $rd = Parse-RecordDate "$($r.date)"
            if ($rd -and $rd -lt $minDate) { continue }
        }
        $fishL = N $r.fish
        $baitL = (@(Split-Baits $r.bait | ForEach-Object { N $_ }) -join " + ")
        $lakeL = $r.loc
        if ($r.lake) { $lakeL = Get-LakeName $r.lake }
        if ($q) {
            $hay = ("{0} {1} {2} {3} {4} {5}" -f $fishL, $r.fish, $baitL, $r.bait, $lakeL, $r.player).ToLower()
            if (-not $hay.Contains($q)) { continue }
        }
        $filtered.Add($r) | Out-Null
        $rows.Add([pscustomobject]@{
            Fish = $fishL; Weight = (Format-Weight $r.weight); WeightSort = [int]$r.weight
            Mark = (Get-TrophyMark $r.fish $r.weight); Lake = $lakeL; Bait = $baitL; Player = $r.player; Date = $r.date
            LakeId = $r.lake; FishKey = $r.fish
        }) | Out-Null
    }
    $dgWeek.ItemsSource = @($rows | Sort-Object @{ Expression = "Fish" }, @{ Expression = "WeightSort"; Descending = $true })

    $bc = @{}
    $bf = @{}
    $bl = @{}
    foreach ($r in $filtered) {
        foreach ($b in (Split-Baits $r.bait)) {
            if (-not $bc.ContainsKey($b)) { $bc[$b] = 0; $bf[$b] = New-Object System.Collections.ArrayList; $bl[$b] = New-Object System.Collections.ArrayList }
            $bc[$b] = $bc[$b] + 1
            if (-not $bf[$b].Contains($r.fish)) { $bf[$b].Add($r.fish) | Out-Null }
            if ($r.lake) { $bl[$b].Add([string]$r.lake) | Out-Null }
        }
    }
    $brows = @()
    foreach ($k in $bc.Keys) {
        $topLake = @($bl[$k] | Group-Object | Sort-Object Count -Descending | Select-Object -First 1 | ForEach-Object { $_.Name })
        $brows += [pscustomobject]@{
            Bait = (N $k); Count = $bc[$k]; Fish = ((@($bf[$k]) | Select-Object -First 6 | ForEach-Object { N $_ }) -join ", ")
            FishKey = [string]@($bf[$k])[0]; LakeId = $(if ($lf) { $lf } elseif ($topLake.Count -gt 0) { $topLake[0] } else { "" })
        }
    }
    $dgWeekBaits.ItemsSource = @($brows | Sort-Object Count -Descending)
    if ($lf) { $txtWeekBaitsTitle.Text = "{0}: {1}" -f (T "topBaits"), (Get-LakeName $lf) }
    else { $txtWeekBaitsTitle.Text = T "topBaits" }
    if (-not $script:weekLoading) {
        if ($script:weekTime) {
            $txtWeekState.Text = "{0}: {1}   {2} {3}" -f (T "lastUpdate"), $script:weekTime.ToString("dd.MM.yyyy HH:mm"), $rows.Count, (T "records")
            if ($script:weekFromCloud) { $txtWeekState.Text = $txtWeekState.Text + "   " + (T "fromCloud") }
        } else {
            $txtWeekState.Text = T "noData"
        }
    }
}

function Ensure-WebBrowser {
    if ($script:wvWeb) { return $true }
    if (-not $script:hasWebView) { return $false }
    try {
        $script:wvWeb = New-WebView "webview_web" $null
        $webHost.Child = $script:wvWeb
        return $true
    } catch {
        return $false
    }
}

function Open-Web([string]$url) {
    if (-not (Ensure-WebBrowser)) { Set-Status (T "webviewMissing"); return }
    Navigate-WebView $script:wvWeb $url
}

function Get-Rf4itUrl([string]$lakeId) {
    $l = $script:lakeById[$lakeId]
    if (-not $l -or -not $l.mapKey) { return "https://rf4it.sandoramix.dev/" }
    $k = $l.mapKey.Replace("_", "")
    if ($l.mapKey -eq "norwegian_sea") { $k = "northsea" }
    if ($l.mapKey -eq "elk_lake") { $k = "american_pond" }
    "https://rf4it.sandoramix.dev/maps/$k"
}

function Get-WebSites {
    $statUrl = "https://rf4-stat.com/active-spots/"
    if ($script:lang -eq "de") { $statUrl = "https://de.rf4-stat.com/active-spots/" }
    @(
        [pscustomobject]@{ Key = "rf4it"; Label = "RF4 IT: " + (T "siteRf4it"); Url = "" }
        [pscustomobject]@{ Key = "trophyspot"; Label = "TrophySpot.de: " + (T "siteTrophyspot"); Url = "https://trophyspot.de/" }
        [pscustomobject]@{ Key = "rf4spots"; Label = "RF4-Spots.de: " + (T "siteRf4spots"); Url = "https://www.rf4-spots.de/index.php/rf4-spots/alle-aktiven-spots" }
        [pscustomobject]@{ Key = "rf4stat"; Label = "RF4-STAT: " + (T "siteRf4stat"); Url = $statUrl }
        [pscustomobject]@{ Key = "rf4intel"; Label = "RF4Intel: " + (T "siteRf4intel"); Url = "https://rf4intel.com/" }
        [pscustomobject]@{ Key = "rf4records"; Label = "RF4 Records: " + (T "siteRf4records"); Url = "https://rf4records.com/records" }
        [pscustomobject]@{ Key = "rf4db"; Label = "RF4DB: " + (T "siteRf4db"); Url = "https://rf4db.com/en" }
        [pscustomobject]@{ Key = "rf4hub"; Label = "RF4HUB: " + (T "siteRf4hub"); Url = "https://en.rf4h.ru/" }
        [pscustomobject]@{ Key = "official"; Label = "rf4game.com: " + (T "officialRecords"); Url = "" }
    )
}

function Open-SelectedSite {
    $it = $cmbWebSite.SelectedItem
    if (-not $it) { return }
    $url = $it.Url
    if ($it.Key -eq "rf4it") { $url = Get-Rf4itUrl (Get-ComboKey $cmbWebLake) }
    if ($it.Key -eq "official") { $url = "https://rf4game.com/records/weekly/region/{0}/" -f $script:weekRegion }
    $script:lastSite = $it.Key
    Open-Web $url
}

function Get-ChromePath {
    foreach ($k in @("HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")) {
        try {
            $v = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop)."(default)"
            if ($v -and (Test-Path -LiteralPath $v)) { return $v }
        } catch { }
    }
    foreach ($c in @((Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"), (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe"), (Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe"))) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

function Open-External([string]$url) {
    if (-not $url) { return }
    try { Set-Status $url } catch { }
    try {
        $chrome = Get-ChromePath
        if ($chrome) {
            $psi = New-Object System.Diagnostics.ProcessStartInfo $chrome, ("--new-window `"" + $url + "`"")
            $psi.UseShellExecute = $true
            $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
            [System.Diagnostics.Process]::Start($psi) | Out-Null
        } else {
            $psi = New-Object System.Diagnostics.ProcessStartInfo $url
            $psi.UseShellExecute = $true
            [System.Diagnostics.Process]::Start($psi) | Out-Null
        }
    } catch {
        Write-ErrorLog ("Open-External: " + $url + " " + $_.Exception.Message)
        [System.Windows.MessageBox]::Show($url, (T "appTitle")) | Out-Null
    }
}

function Refresh-WebFishChoices {
    $f = Get-ComboKey $cmbWebFish
    Set-Choices $cmbWebFish (Get-FishChoices (Get-ComboKey $cmbWebLake))
    Set-ComboKey $cmbWebFish $f
}

$script:commFile = Join-Path $userDir "community.json"
$script:commReports = New-Object System.Collections.ArrayList
$script:commIds = @{}
$script:commSynced = $null
$script:commClusterCache = @{}
$script:commSyncing = $false
$script:commSel = $null
$script:techByMethod = @{
    "Bottom Fishing" = "bottom"; "Float Fishing" = "float"; "Spinning" = "spin"; "Spin Fishing" = "spin"
    "Marine Fishing" = "marine"; "Sea Fishing" = "marine"; "Trolling" = "trolling"
}

function New-Serializer {
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = 268435456
    $ser
}

function Load-Community {
    $script:commReports.Clear()
    $script:commIds = @{}
    $script:commSynced = $null
    $script:commClusterCache = @{}
    if (-not (Test-Path $script:commFile)) { return }
    try {
        $d = (New-Serializer).DeserializeObject([System.IO.File]::ReadAllText($script:commFile, [System.Text.Encoding]::UTF8))
        if ($d["synced"]) { $script:commSynced = [datetime]::Parse([string]$d["synced"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind) }
        foreach ($r in $d["reports"]) {
            $idx = $script:commReports.Add($r)
            $script:commIds[[string]$r["id"]] = $idx
        }
        if ([int]$d["v"] -lt 2) { $script:commSynced = $null }
    } catch {
        $script:commReports.Clear()
    }
}

function Save-Community {
    $obj = @{ v = 2; synced = $script:commSynced.ToString("o"); source = "https://rf4intel.com"; reports = $script:commReports.ToArray() }
    [System.IO.File]::WriteAllText($script:commFile, (New-Serializer).Serialize($obj), (New-Object System.Text.UTF8Encoding $false))
}

function Convert-CommPost($p) {
    $c = [string]$p["coordinates"]
    if ($c -notmatch "^\s*(\d{1,4})\s*:\s*(\d{1,4})\s*$") { return $null }
    $x = [int]$Matches[1]
    $y = [int]$Matches[2]
    $lake = Get-LakeIdByLocation ([string]$p["waterbody"])
    if (-not $lake -and $p["waterbody_ru_name"]) { $lake = Get-LakeIdByLocation ([string]$p["waterbody_ru_name"]) }
    if (-not $lake) { return $null }
    $l = $script:lakeById[$lake]
    if (-not $l.bounds) { return $null }
    $fish = @()
    foreach ($f in @($p["fish"])) { if ($f) { $fish += [string]$f } }
    $bait = @()
    foreach ($b in @($p["bait"])) { if ($b) { $bait += [string]$b } }
    @{
        id = [string]$p["id"]; lake = $lake; x = $x; y = $y; fish = $fish; bait = $bait
        method = [string]$p["method"]; clip = $p["clip_m"]; depth = $p["depth_m"]
        posted = [string]$p["posted_at"]; url = [string]$p["source_url"]
        baitDetail = [string]$p["bait_detail"]; lure = [string]$p["lure_detail"]; rig = [string]$p["rig"]
        dip = [string]$p["dip"]; groundbait = [string]$p["groundbait"]; dryMix = [string]$p["dry_mix"]
        pva = [string]$p["pva"]; reelSpeed = [string]$p["reel_speed"]; weight = $p["weight_kg"]
    }
}

function Get-DaysLabel([int]$n) {
    if ($n -eq 1) { return (T "lastDay") }
    return ((T "lastDays") -f $n)
}

function Get-DaysChoices([string]$prefix) {
    @(1..7 | ForEach-Object { New-Choice ($prefix + $_) (Get-DaysLabel $_) })
}

function Get-KeyDays([string]$key) {
    $m = [regex]::Match("$key", "(\d+)$")
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

function Get-SinceUtc([int]$days) {
    if ($days -le 0) { return "" }
    (Get-Date).ToUniversalTime().AddDays(-$days).ToString("yyyy-MM-ddTHH:mm:ss")
}

function Get-CommPeriodStart {
    Get-SinceUtc (Get-KeyDays (Get-ComboKey $cmbCommPeriod))
}

function Parse-RecordDate([string]$t) {
    $d = [datetime]::MinValue
    foreach ($fmt in @("dd.MM.yy", "dd.MM.yyyy", "MM/dd/yy", "MM/dd/yyyy", "yyyy-MM-dd", "dd/MM/yy")) {
        if ([datetime]::TryParseExact($t.Trim(), $fmt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$d)) { return $d }
    }
    return $null
}

function Get-CommClusters([string]$lakeId, [string]$since, [string]$fish) {
    $key = "{0}|{1}|{2}" -f $lakeId, $since.Substring(0, [math]::Min(13, $since.Length)), $fish
    if ($script:commClusterCache.ContainsKey($key)) { return $script:commClusterCache[$key] }
    $byCoord = @{}
    foreach ($r in $script:commReports) {
        if ($lakeId -and $r["lake"] -ne $lakeId) { continue }
        if ($since -and [string]$r["posted"] -lt $since) { continue }
        if ($fish -and -not (@($r["fish"]) -contains $fish)) { continue }
        $ck = "{0}|{1}|{2}" -f $r["lake"], $r["x"], $r["y"]
        if (-not $byCoord.ContainsKey($ck)) { $byCoord[$ck] = New-Object System.Collections.ArrayList }
        $byCoord[$ck].Add($r) | Out-Null
    }
    $clusters = New-Object System.Collections.ArrayList
    $byLake = @{}
    foreach ($ck in ($byCoord.Keys | Sort-Object { $byCoord[$_].Count } -Descending)) {
        $list = $byCoord[$ck]
        $first = $list[0]
        $lk = [string]$first["lake"]
        $x = [int]$first["x"]
        $y = [int]$first["y"]
        if (-not $byLake.ContainsKey($lk)) { $byLake[$lk] = New-Object System.Collections.ArrayList }
        $target = $null
        foreach ($cl in $byLake[$lk]) {
            if ([math]::Abs($cl.X-$x) -le 2 -and [math]::Abs($cl.Y-$y) -le 2) { $target = $cl; break }
        }
        if (-not $target) {
            $target = [pscustomobject]@{ Lake = $lk; X = $x; Y = $y; Reports = (New-Object System.Collections.ArrayList) }
            $byLake[$lk].Add($target) | Out-Null
            $clusters.Add($target) | Out-Null
        }
        foreach ($r in $list) { $target.Reports.Add($r) | Out-Null }
    }
    $result = @($clusters)
    $script:commClusterCache[$key] = $result
    $result
}

function Get-TopCounts($values, [int]$max) {
    $h = @{}
    foreach ($v in $values) {
        if (-not $v) { continue }
        $k = [string]$v
        if ($h.ContainsKey($k)) { $h[$k] = $h[$k] + 1 } else { $h[$k] = 1 }
    }
    @($h.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $max)
}

function Resolve-ItemName([string]$text) {
    $t = $text.Trim()
    if (-not $t) { return "" }
    if ($script:names.ContainsKey($t)) { return $t }
    $row = (Get-ItemNames)[$t]
    if ($row) { return [string]$row[0] }
    return $t
}

function Format-Detail([string]$text) {
    if (-not $text) { return "" }
    (@($text -split "\s\+\s" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { N $_ }) -join " + ")
}

function Clean-CommDetail([string]$t) {
    if (-not $t) { return "" }
    if ($t -match "<==") { $t = ($t -split "<==")[-1] }
    $t = $t -replace "^\s*\(?\s*(Bait|Köder|Наживка)\s*\)?\s*:\s*", ""
    $t = $t -replace "^\s*(By catch date|By weight|By time|Nach Fangzeit)\s*", ""
    $t = $t -replace "[©]", ""
    $seen = @{}
    $parts = @()
    foreach ($x in ($t -split "\s\+\s")) {
        $x = $x.Trim()
        if (-not $x) { continue }
        $k = $x.ToLower()
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true
        $parts += $x
    }
    ($parts -join " + ")
}

function Test-PvaStick([string]$t) {
    return ($t -match "^\s*stick or PVA stringer\s*$")
}

function Open-ReportUrl([string]$url) {
    if (-not $url) { return }
    if ($url -match "^https://t\.me/([^/?]+)/(\d+)") { $url = "https://t.me/{0}/{1}?embed=1&mode=tme" -f $Matches[1], $Matches[2] }
    Open-External $url
}

function Get-ClusterSummary($cl) {
    $fish = @()
    $bait = @()
    $meth = @()
    $clips = @()
    $depths = @()
    $last = ""
    $det = @{}
    foreach ($f in @("baitDetail", "lure", "groundbait", "dryMix", "dip", "pva", "pvaMix", "rig", "reelSpeed")) { $det[$f] = @() }
    foreach ($r in $cl.Reports) {
        foreach ($f in @("baitDetail", "lure", "groundbait", "dryMix", "dip", "rig", "reelSpeed")) {
            $v = Clean-CommDetail ([string]$r[$f])
            if ($v) { $det[$f] += $v }
        }
        $pv = ([string]$r["pva"]).Trim()
        if ($pv) {
            if (Test-PvaStick $pv) { $det["pva"] += "PVA Stick/Stringer" }
            elseif ($pv -match ",") { $det["pvaMix"] += $pv }
            else { $det["pva"] += (Clean-CommDetail $pv) }
        }
        $fish += @($r["fish"])
        $bait += @($r["bait"])
        if ($r["method"]) { $meth += [string]$r["method"] }
        if ($null -ne $r["clip"]) { $clips += ("{0:0.#}" -f [double]$r["clip"]) }
        if ($null -ne $r["depth"]) { $depths += ("{0:0.#}" -f [double]$r["depth"]) }
        if ([string]$r["posted"] -gt $last) { $last = [string]$r["posted"] }
    }
    [pscustomobject]@{
        Fish = Get-TopCounts $fish 8; Bait = Get-TopCounts $bait 8; Methods = Get-TopCounts $meth 3
        Clips = Get-TopCounts $clips 4; Depths = Get-TopCounts $depths 4; Last = $last
        BaitDetail = Get-TopCounts $det["baitDetail"] 4; Lure = Get-TopCounts $det["lure"] 4
        Groundbait = Get-TopCounts $det["groundbait"] 3; DryMix = Get-TopCounts $det["dryMix"] 3
        Dip = Get-TopCounts $det["dip"] 3; Pva = Get-TopCounts $det["pva"] 3; PvaMix = Get-TopCounts $det["pvaMix"] 3
        Rig = Get-TopCounts $det["rig"] 3; ReelSpeed = Get-TopCounts $det["reelSpeed"] 3
    }
}

function Format-TopCounts($top, [int]$n) {
    (@($top | Select-Object -First $n | ForEach-Object { "{0} ({1})" -f (N $_.Key), $_.Value }) -join ", ")
}

function Format-MeterCounts($top) {
    (@($top | ForEach-Object { "{0} m ({1})" -f $_.Key, $_.Value }) -join ";  ")
}

function Format-IsoDate([string]$iso) {
    if (-not $iso) { return "" }
    try {
        $d = [datetime]::Parse($iso, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime()
        return Format-LocalDate $d
    } catch { return $iso }
}

function Format-Method([string]$m) {
    $t = $script:techByMethod[$m]
    if ($t) { return T "tech_$t" }
    return $m
}

function Update-CommState {
    if ($script:commSyncing) { return }
    if ($script:commReports.Count -gt 0) {
        $week = 0
        $since = (Get-Date).ToUniversalTime().AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ss")
        foreach ($r in $script:commReports) { if ([string]$r["posted"] -ge $since) { $week++ } }
        $txtCommState.Text = "{0} {1}, {2} {3}   {4}: {5}" -f $script:commReports.Count, (T "reports"), $week, (T "thisWeekShort"), (T "lastUpdate"), $(if ($script:commSynced) { Format-LocalDate $script:commSynced.ToLocalTime() } else { "?" })
    } else {
        $txtCommState.Text = T "noCommData"
    }
}

function Refresh-CommFishChoices {
    $k = Get-ComboKey $cmbCommFish
    $items = @(New-Choice "" (T "allFish"))
    $lakeId = $null
    if ($script:curMapLake) { $lakeId = $script:curMapLake.id }
    $items += Get-FishChoices $lakeId
    Set-Choices $cmbCommFish $items
    Set-ComboKey $cmbCommFish $k
}

$script:syncTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:syncTimer.Interval = [TimeSpan]::FromMilliseconds(1100)
$script:syncTimer.Add_Tick({
    $script:syncTimer.Stop()
    Request-SyncPage
})

function Start-CommunitySync([switch]$Full) {
    if ($script:commSyncing) { return }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $script:commSyncing = $true
    $btnCommSync.IsEnabled = $false
    $btnCommFull.IsEnabled = $false
    $script:syncOffset = 0
    $script:syncLakeIdx = 0
    $script:syncAdded = 0
    $script:syncMaxAge = 336
    $script:syncPerLake = $false
    if ($Full) {
        $script:syncMaxAge = 0
        $script:syncPerLake = $true
    } elseif ($script:commSynced -and $script:commReports.Count -gt 0) {
        $hours = [math]::Ceiling(((Get-Date).ToUniversalTime()-$script:commSynced.ToUniversalTime()).TotalHours) + 48
        if ($hours -lt 336) { $script:syncMaxAge = [int]$hours }
    }
    $script:syncStarted = (Get-Date).ToUniversalTime()
    $txtCommState.Text = T "syncing"
    Request-SyncPage
}

$script:syncWaterbodies = @(
    "Mosquito Lake", "Winding Rivulet", "Kuori Lake", "Old Burg Lake", "Volkhov River", "Ladoga Lake", "Bear Lake",
    "Sura River", "Akhtuba River", "Belaya River", "Ladoga archipelago", "Norwegian Sea", "Seversky Donets River",
    "The Amber Lake", "Lower Tunguska River", "Yama River", "Copper Lake", "Elk Lake"
)

$script:http = New-Object System.Net.Http.HttpClient
$script:http.Timeout = [TimeSpan]::FromSeconds(90)
$script:http.DefaultRequestHeaders.UserAgent.ParseAdd("RF4Companion/1.0")
$script:syncTask = $null
$script:pollTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:pollTimer.Interval = [TimeSpan]::FromMilliseconds(250)
$script:pollTimer.Add_Tick({
    $t = $script:syncTask
    if (-not $t -or -not $t.IsCompleted) { return }
    $script:pollTimer.Stop()
    $script:syncTask = $null
    if ($t.IsFaulted -or $t.IsCanceled) {
        if ($t.Exception) { Write-ErrorLog ("Sync: " + $t.Exception.GetBaseException().Message) } else { Write-ErrorLog "Sync: canceled or timed out" }
        Finish-CommunitySync $false
        return
    }
    Receive-SyncPage $t.Result
})

function Request-SyncPage {
    $url = "https://rf4intel.com/api/posts?limit=100&offset={0}" -f $script:syncOffset
    if ($script:syncPerLake) {
        $wb = $script:syncWaterbodies[$script:syncLakeIdx]
        $url = "{0}&waterbody={1}" -f $url, [System.Uri]::EscapeDataString($wb)
    }
    if ($script:syncMaxAge -gt 0) { $url = "{0}&max_age_hours={1}" -f $url, $script:syncMaxAge }
    $script:syncTask = $script:http.GetStringAsync($url)
    $script:pollTimer.Start()
}

function Finish-CommunitySync([bool]$ok) {
    $script:commSyncing = $false
    $btnCommSync.IsEnabled = $true
    $btnCommFull.IsEnabled = $true
    if ($ok) {
        if (-not $script:commSynced -or $script:syncStarted -gt $script:commSynced) { $script:commSynced = $script:syncStarted }
        Save-Community
        $script:commClusterCache = @{}
        Update-CommState
        Draw-Markers
        if ((Get-ComboKey $cmbSpotsSource) -ne "mine") { Refresh-SpotsGrid }
        Set-Status ("{0}: {1} {2}" -f (T "syncDone"), $script:syncAdded, (T "newReports"))
    } else {
        $txtCommState.Text = T "loadFailed"
    }
}

function Receive-SyncPage([string]$raw) {
    try {
        $d = (New-Serializer).DeserializeObject($raw)
    } catch {
        Write-ErrorLog ("Sync parse: " + $_.Exception.Message)
        Finish-CommunitySync $false
        return
    }
    foreach ($p in $d["posts"]) {
        $id = [string]$p["id"]
        $r = Convert-CommPost $p
        if ($script:commIds.ContainsKey($id)) {
            $idx = $script:commIds[$id]
            if ($r -and $idx -ge 0) { $script:commReports[$idx] = $r }
            continue
        }
        if ($r) {
            $script:commIds[$id] = $script:commReports.Add($r)
            $script:syncAdded++
        } else {
            $script:commIds[$id] = -1
        }
    }
    $script:syncOffset += 100
    $total = [int]$d["total"]
    if ($script:syncPerLake) {
        $wb = $script:syncWaterbodies[$script:syncLakeIdx]
        $lakeLabel = $wb
        $lid = Get-LakeIdByLocation $wb
        if ($lid) { $lakeLabel = Get-LakeName $lid }
        $txtCommState.Text = "{0}: {1} ({2}/{3})   {4} / {5}" -f (T "syncing"), $lakeLabel, ($script:syncLakeIdx + 1), $script:syncWaterbodies.Count, [math]::Min($script:syncOffset, $total), $total
    } else {
        $txtCommState.Text = "{0}: {1} / {2}" -f (T "syncing"), [math]::Min($script:syncOffset, $total), $total
    }
    if ($d["has_more"] -and $script:syncOffset -le 10000) {
        $script:syncTimer.Start()
    } elseif ($script:syncPerLake -and $script:syncLakeIdx + 1 -lt $script:syncWaterbodies.Count) {
        $script:syncLakeIdx++
        $script:syncOffset = 0
        $script:syncTimer.Start()
    } else {
        Finish-CommunitySync $true
    }
}

function Show-CommCluster($cl) {
    $script:commSel = $cl
    $sum = Get-ClusterSummary $cl
    $txtCommTitle.Text = "{0}   {1}:{2}   ({3} {4})" -f (Get-LakeName $cl.Lake), $cl.X, $cl.Y, $cl.Reports.Count, (T "reports")
    $lines = @()
    $lines += "{0}: {1}" -f (T "fish"), (Format-TopCounts $sum.Fish 8)
    if (@($sum.Bait).Count -gt 0) { $lines += "{0}: {1}" -f (T "bait"), (Format-TopCounts $sum.Bait 8) }
    if (@($sum.Methods).Count -gt 0) { $lines += "{0}: {1}" -f (T "technique"), ((@($sum.Methods) | ForEach-Object { "{0} ({1})" -f (Format-Method $_.Key), $_.Value }) -join ", ") }
    if (@($sum.Clips).Count -gt 0) { $lines += "{0}: {1}" -f (T "distance"), (Format-MeterCounts $sum.Clips) }
    if (@($sum.Depths).Count -gt 0) { $lines += "{0}: {1}" -f (T "depth"), (Format-MeterCounts $sum.Depths) }
    if (@($sum.ReelSpeed).Count -gt 0) { $lines += "{0}: {1}" -f (T "reelSpeed"), (Format-TopCounts $sum.ReelSpeed 3) }
    $lines += "{0}: {1}" -f (T "lastReport"), (Format-IsoDate $sum.Last)
    $txtCommInfo.Text = $lines -join [Environment]::NewLine
    $items = @()
    foreach ($r in @($cl.Reports | Sort-Object { [string]$_["posted"] } -Descending | Select-Object -First 40)) {
        $parts = @(Format-IsoDate ([string]$r["posted"]))
        $fl = (@($r["fish"]) | Select-Object -First 3 | ForEach-Object { N $_ }) -join ", "
        if ($fl) { $parts += $fl }
        if ($null -ne $r["weight"] -and "$($r["weight"])" -ne "") { $parts += Format-Weight ([double]$r["weight"] * 1000) }
        if ($null -ne $r["clip"] -and "$($r["clip"])" -ne "") { $parts += "{0} {1:0.#} m" -f (T "distance"), [double]$r["clip"] }
        $parts += Get-ReportHost ([string]$r["url"])
        $items += [pscustomobject]@{ Label = ($parts -join "  |  "); Url = [string]$r["url"]; Detail = (Get-CommReportText $r) }
    }
    $lstCommReports.ItemsSource = $items
    $panelSpotForm.Visibility = "Collapsed"
    $svSpotForm.Visibility = "Collapsed"
    $panelMapIdle.Visibility = "Collapsed"
    $lstLakeSpots.Visibility = "Collapsed"
    $panelComm.Visibility = "Visible"
    Draw-Markers
}

function Get-ReportHost([string]$url) {
    if ($url -match "t\.me/") { return "Telegram" }
    if ($url -match "vk\.com/") { return "VK" }
    if ($url -match "discord\.com/") { return "Discord" }
    return ""
}

function Format-CommItems([string]$text) {
    if (-not $text) { return "" }
    $lists = Get-MatchLists "en"
    $out = @()
    foreach ($part in ($text -split "\s\+\s")) {
        $x = $part.Trim()
        if (-not $x) { continue }
        $t = N $x
        if ($t -eq $x -and $x.Length -ge 4) {
            $hit = Match-Name $x $lists.ItemLoc $lists.ItemEn 0.85
            if ($hit) { $t = N $hit.En }
        }
        $out += $t
    }
    ($out -join " + ")
}

function Get-CommReportText($r) {
    $lines = @()
    $fl = (@($r["fish"]) | ForEach-Object { N $_ }) -join ", "
    if ($fl) { $lines += "{0}: {1}" -f (T "fish"), $fl }
    if ($null -ne $r["weight"] -and "$($r["weight"])" -ne "") { $lines += "{0}: {1}" -f (T "weight"), (Format-Weight ([double]$r["weight"] * 1000)) }
    if ([string]$r["method"]) { $lines += "{0}: {1}" -f (T "technique"), (Format-Method ([string]$r["method"])) }
    if ($null -ne $r["clip"] -and "$($r["clip"])" -ne "") { $lines += "{0}: {1:0.#} m" -f (T "distance"), [double]$r["clip"] }
    if ($null -ne $r["depth"] -and "$($r["depth"])" -ne "") { $lines += "{0}: {1:0.#} m" -f (T "depth"), [double]$r["depth"] }
    $bl = (@($r["bait"]) | Where-Object { $_ } | ForEach-Object { N $_ }) -join ", "
    if ($bl) { $lines += "{0}: {1}" -f (T "bait"), $bl }
    foreach ($pair in @(@("baitDetail", "baitExact"), @("lure", "lureDetail"), @("groundbait", "groundbaitMix"), @("dryMix", "dryMix"), @("dip", "dipL"), @("rig", "rigL"), @("reelSpeed", "reelSpeed"))) {
        $v = Clean-CommDetail ([string]$r[$pair[0]])
        if ($pair[0] -ne "reelSpeed") { $v = Format-CommItems $v }
        if ($v) { $lines += "{0}: {1}" -f (T $pair[1]), $v }
    }
    $pv = ([string]$r["pva"]).Trim()
    if ($pv) {
        if (Test-PvaStick $pv) { $lines += "{0}: PVA Stick/Stringer" -f (T "pvaL") }
        elseif ($pv -match ",") { $lines += "{0}: {1}" -f (T "pvaContent"), (($pv -split "\s*,\s*" | ForEach-Object { N $_ }) -join ", ") }
        else { $lines += "{0}: {1}" -f (T "pvaL"), (Format-CommItems (Clean-CommDetail $pv)) }
    }
    $lines -join [Environment]::NewLine
}

function Hide-CommCluster {
    $script:commSel = $null
    $panelComm.Visibility = "Collapsed"
    Update-MapPanel
}

function Draw-CommMarkers {
    if (-not $chkCommunity.IsChecked) { return }
    $lake = $script:curMapLake
    if (-not $lake -or $script:commReports.Count -eq 0) { return }
    $sc = $script:scale
    $clusters = Get-CommClusters $lake.id (Get-CommPeriodStart) (Get-ComboKey $cmbCommFish)
    $fill = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(200, 255, 112, 67))
    $selBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#FFFFD54F"))
    $script:commDrawn = @{}
    $i = 0
    foreach ($cl in $clusters) {
        $n = From-Game $lake $cl.X $cl.Y
        $size = (10 + [math]::Min(14, [math]::Sqrt($cl.Reports.Count) * 2.2)) / $sc
        $r = New-Object System.Windows.Shapes.Rectangle
        $r.Width = $size
        $r.Height = $size
        $r.RadiusX = 2 / $sc
        $r.RadiusY = 2 / $sc
        $r.Fill = $fill
        $isSel = ($script:commSel -and $script:commSel.Lake -eq $cl.Lake -and $script:commSel.X -eq $cl.X -and $script:commSel.Y -eq $cl.Y)
        if ($isSel) { $r.Stroke = $selBrush; $r.StrokeThickness = 4 / $sc }
        else { $r.Stroke = [System.Windows.Media.Brushes]::Black; $r.StrokeThickness = 1.5 / $sc }
        $r.RenderTransformOrigin = New-Object System.Windows.Point 0.5, 0.5
        $r.RenderTransform = New-Object System.Windows.Media.RotateTransform 45
        $r.Tag = "c:$i"
        $r.Cursor = [System.Windows.Input.Cursors]::Hand
        $top = @(Get-TopCounts (@($cl.Reports | ForEach-Object { $_["fish"] })) 3 | ForEach-Object { N $_.Key }) -join ", "
        $r.ToolTip = "{0}:{1}   {2} {3}   {4}" -f $cl.X, $cl.Y, $cl.Reports.Count, (T "reports"), $top
        [System.Windows.Controls.Canvas]::SetLeft($r, ([double]$n.NX * 2048)-($size / 2))
        [System.Windows.Controls.Canvas]::SetTop($r, ([double]$n.NY * 2048)-($size / 2))
        $canvasMarkers.Children.Add($r) | Out-Null
        $script:commDrawn["c:$i"] = $cl
        $i++
    }
}

function Get-SpotsGridCommunityRows([string]$lf, [string]$q) {
    $cq = Parse-CoordQuery $q
    $since = ""
    $sd = Get-KeyDays (Get-ComboKey $cmbSpotsSource)
    if ($sd -gt 0) { $since = Get-SinceUtc $sd }
    $rows = New-Object System.Collections.ArrayList
    foreach ($cl in (Get-CommClusters $lf $since "")) {
        $sum = Get-ClusterSummary $cl
        $meth = (@($sum.Methods) | ForEach-Object { Format-Method $_.Key }) -join ", "
        $row = [pscustomobject]@{
            Id = ("c|{0}|{1}|{2}" -f $cl.Lake, $cl.X, $cl.Y); Lake = (Get-LakeName $cl.Lake)
            Name = ("{0} {1}" -f $cl.Reports.Count, (T "reports")); Count = $cl.Reports.Count
            Fish = (Format-TopCounts $sum.Fish 3); Bait = (Format-TopCounts $sum.Bait 3); Tech = $meth
            Coords = ("{0}:{1}" -f $cl.X, $cl.Y); Depth = (@($sum.Clips | Select-Object -First 2 | ForEach-Object { "{0} m" -f $_.Key }) -join ";  ")
            Notes = (Format-IsoDate $sum.Last)
        }
        if ($cq) {
            if ([math]::Abs($cl.X-$cq.X) -gt 2 -or [math]::Abs($cl.Y-$cq.Y) -gt 2) { continue }
        } elseif ($q) {
            $allFish = (@($sum.Fish | ForEach-Object { "{0} {1}" -f $_.Key, (N $_.Key) }) + @($cl.Reports | ForEach-Object { @($_["fish"]) } | Select-Object -Unique | ForEach-Object { "{0} {1}" -f $_, (N $_) })) -join " "
            $hay = ("{0} {1} {2} {3} {4} {5}" -f $row.Lake, $row.Fish, $row.Bait, $row.Tech, $row.Coords, $allFish).ToLower()
            if (-not $hay.Contains($q)) { continue }
        }
        $rows.Add($row) | Out-Null
    }
    @($rows | Sort-Object Count -Descending)
}

function Show-CommOnMap([string]$id) {
    $parts = $id.Split("|")
    $lakeId = $parts[1]
    $x = [int]$parts[2]
    $y = [int]$parts[3]
    if (-not $script:curMapLake -or $script:curMapLake.id -ne $lakeId) {
        $script:busy = $true
        Set-ComboKey $cmbMapLake $lakeId
        $script:busy = $false
        Load-MapLake $lakeId
    }
    $chkCommunity.IsChecked = $true
    $sd = Get-KeyDays (Get-ComboKey $cmbSpotsSource)
    if ($sd -gt 0) { Set-ComboKey $cmbCommPeriod ("d" + $sd) } else { Set-ComboKey $cmbCommPeriod "all" }
    $script:busy = $true
    Set-ComboKey $cmbCommFish ""
    $script:busy = $false
    $cl = Get-CommClusters $lakeId (Get-CommPeriodStart) "" | Where-Object { $_.X -eq $x -and $_.Y -eq $y } | Select-Object -First 1
    if ($cl) {
        Show-CommCluster $cl
        $n = From-Game $script:curMapLake $x $y
        Center-On ([double]$n.NX) ([double]$n.NY)
    }
}

$script:searchMark = $null

function Parse-CoordQuery([string]$q) {
    if ($q -match "^\s*(\d{1,4})\s*[:;,\s/]\s*(\d{1,4})\s*$") {
        return [pscustomobject]@{ X = [int]$Matches[1]; Y = [int]$Matches[2] }
    }
    return $null
}

function Find-FishKeys([string]$q) {
    $t = $q.Trim().ToLower()
    if ($t.Length -lt 2) { return @() }
    $hits = @()
    foreach ($k in $script:names.Keys) {
        foreach ($p in $script:names[$k].PSObject.Properties) {
            if ("$($p.Value)".ToLower().Contains($t)) { $hits += $k; break }
        }
    }
    $fishSet = @{}
    foreach ($t2 in @($game.trophies)) { $fishSet[$t2.fish] = $true }
    @($hits | Where-Object { $fishSet.ContainsKey($_) } | Select-Object -Unique)
}

function Invoke-MapSearch {
    $q = "$($txtMapSearch.Text)".Trim()
    $lake = $script:curMapLake
    if (-not $q -or -not $lake) {
        $script:searchMark = $null
        Draw-Markers
        return
    }
    $c = Parse-CoordQuery $q
    if ($c) {
        $n = From-Game $lake $c.X $c.Y
        $script:searchMark = [pscustomobject]@{ NX = [double]$n.NX; NY = [double]$n.NY }
        $best = $null
        $bestD = 999
        foreach ($since in @((Get-CommPeriodStart), "")) {
            foreach ($cl in (Get-CommClusters $lake.id $since (Get-ComboKey $cmbCommFish))) {
                $d = [math]::Max([math]::Abs($cl.X-$c.X), [math]::Abs($cl.Y-$c.Y))
                if ($d -le 2 -and $d -lt $bestD) { $best = $cl; $bestD = $d }
            }
            if ($best) {
                if (-not $since -and (Get-ComboKey $cmbCommPeriod) -ne "all") {
                    $script:busy = $true
                    Set-ComboKey $cmbCommPeriod "all"
                    $script:busy = $false
                }
                break
            }
        }
        $own = $null
        foreach ($sp in $script:spots) {
            if ($sp.lake -ne $lake.id) { continue }
            $g = To-Game $lake ([double]$sp.nx) ([double]$sp.ny)
            if ([math]::Abs([math]::Round($g.X)-$c.X) -le 2 -and [math]::Abs([math]::Round($g.Y)-$c.Y) -le 2) { $own = $sp; break }
        }
        if ($own) { Select-Spot $own.id }
        if ($best) {
            $chkCommunity.IsChecked = $true
            Show-CommCluster $best
            Set-Status ("{0} {1}:{2}   {3} {4}" -f (T "searchFound"), $best.X, $best.Y, $best.Reports.Count, (T "reports"))
        } else {
            if ($script:commSel) { Hide-CommCluster }
            Draw-Markers
            if ($own) { Set-Status ("{0} {1}" -f (T "searchFound"), (Get-SpotLabel $own)) } else { Set-Status (T "searchNoMatch") }
        }
        Center-On ([double]$n.NX) ([double]$n.NY)
        return
    }
    $keys = @(Find-FishKeys $q)
    $lakeFish = @{}
    foreach ($f in @($lake.fish)) { $lakeFish[$f] = $true }
    $pick = @($keys | Where-Object { $lakeFish.ContainsKey($_) })
    if ($pick.Count -eq 0) { $pick = $keys }
    if ($pick.Count -eq 0) {
        Set-Status (T "searchNoMatch")
        return
    }
    $script:searchMark = $null
    $chkCommunity.IsChecked = $true
    if ($script:commSel) { Hide-CommCluster }
    $script:busy = $true
    Set-ComboKey $cmbCommFish $pick[0]
    $script:busy = $false
    Draw-Markers
    $cnt = @(Get-CommClusters $lake.id (Get-CommPeriodStart) $pick[0]).Count
    Set-Status ("{0}: {1}   {2} {3}" -f (T "searchFound"), (N $pick[0]), $cnt, (T "communitySpots"))
}

function Get-BestLakeForFish([string]$fish) {
    $score = @{}
    foreach ($r in $script:commReports) { if (@($r["fish"]) -contains $fish) { $k = [string]$r["lake"]; $score[$k] = 1 + [int]$score[$k] } }
    if ($script:archCurId) { foreach ($it in (Get-ArchWeek $script:archCurId).Values) { if ($it["f"] -eq $fish -and $it["l"]) { $k = [string]$it["l"]; $score[$k] = 1 + [int]$score[$k] } } }
    foreach ($k in ($score.Keys | Sort-Object { $score[$_] } -Descending)) { $l = $script:lakeById[$k]; if ($l -and $l.bounds) { return $k } }
    foreach ($l in @($game.lakes)) { if ($l.bounds -and (@($l.fish) -contains $fish)) { return $l.id } }
    return ""
}

function Show-FishAnywhere([string]$fish, [string]$lakeHint) {
    if (-not $fish) { Set-Status (T "pickFishFirst"); return }
    $lk = $lakeHint
    $l = $null
    if ($lk) { $l = $script:lakeById[$lk] }
    if (-not $l -or -not $l.bounds) { $lk = Get-BestLakeForFish $fish }
    if (-not $lk) { Set-Status ("{0}: {1}" -f (T "noMapForFish"), (N $fish)); return }
    Show-FishOnMap $lk $fish
}

function Show-FishOnMap([string]$lakeId, [string]$fish) {
    $l = $script:lakeById[$lakeId]
    if (-not $l -or -not $l.bounds) { return }
    $script:jumpLake = $lakeId
    $script:jumpFish = $fish
    $tabs.SelectedIndex = 0
    $window.Dispatcher.BeginInvoke([action]{
        $script:busy = $true
        Set-ComboKey $cmbMapLake $script:jumpLake
        $script:busy = $false
        Load-MapLake $script:jumpLake
        $chkCommunity.IsChecked = $true
        $script:busy = $true
        Set-ComboKey $cmbCommFish $script:jumpFish
        $cnt = @(Get-CommClusters $script:jumpLake (Get-CommPeriodStart) (Get-ComboKey $cmbCommFish)).Count
        $widened = $false
        if ($cnt -eq 0 -and (Get-ComboKey $cmbCommPeriod) -ne "all") {
            Set-ComboKey $cmbCommPeriod "all"
            $cnt = @(Get-CommClusters $script:jumpLake "" (Get-ComboKey $cmbCommFish)).Count
            $widened = $true
        }
        $script:busy = $false
        Draw-Markers
        $msg = "{0}: {1}, {2}   {3} {4}" -f (T "searchFound"), (N $script:jumpFish), (Get-LakeName $script:jumpLake), $cnt, (T "communitySpots")
        if ($widened) { $msg = $msg + "   " + (T "widenedAllTime") }
        $others = @{}
        foreach ($r in $script:commReports) { if ($r["lake"] -ne $script:jumpLake -and (@($r["fish"]) -contains $script:jumpFish)) { $k = [string]$r["lake"]; $others[$k] = 1 + [int]$others[$k] } }
        if ($others.Count -gt 0) { $msg = $msg + "   " + (T "alsoReportedAt") + " " + ((@($others.Keys | Sort-Object { $others[$_] } -Descending | Select-Object -First 3 | ForEach-Object { "{0} ({1})" -f (Get-LakeName $_), $others[$_] })) -join ", ") }
        Set-Status $msg
    }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
}

$script:trackerDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Russian Fishing 4\Screenshots"
$script:trackerSince = $null
$script:trackerSeen = @{}
$script:trackerSeenFile = Join-Path $userDir "tracker_seen.json"

function Load-TrackerSeen {
    $script:trackerSeen = @{}
    if (-not (Test-Path -LiteralPath $script:trackerSeenFile)) { return }
    try {
        $d = Read-Json $script:trackerSeenFile
        foreach ($n in @($d)) { if ($n) { $script:trackerSeen[[string]$n] = $true } }
    } catch { }
}

function Save-TrackerSeen {
    $today = (Get-Date).Date
    $keep = @($script:trackerSeen.Keys | Where-Object { (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTime -ge $today })
    for ($try = 0; $try -lt 5; $try++) {
        try {
            [System.IO.File]::WriteAllText($script:trackerSeenFile, (ConvertTo-Json -InputObject @($keep) -Compress), (New-Object System.Text.UTF8Encoding $false))
            break
        } catch { Start-Sleep -Milliseconds 150 }
    }
}
$script:trackerJob = $null
$script:trackerSetup = $null
$script:trackerTemp = ""

$script:catchFirst = $null
$script:hudTempFound = $false

function Find-PosInLines([string[]]$lines) {
    foreach ($l in $lines) {
        $t = ($l -replace "[Oo]", "0" -replace "[Il|]", "1" -replace "G", "5" -replace "S", "5")
        $m = [regex]::Match($t, "(\d{1,3})\s*[:;.]\s*(\d{1,3})")
        if ($m.Success) {
            $x = [int]$m.Groups[1].Value
            $y = [int]$m.Groups[2].Value
            if ($x -ge 1 -and $x -le 300 -and $y -ge 1 -and $y -le 300) { return [pscustomobject]@{ X = $x; Y = $y } }
        }
    }
    return $null
}

function Apply-TrackerPos([int]$x, [int]$y) {
    if ($script:trackerPos -and ([math]::Abs($script:trackerPos.X-$x) -gt 2 -or [math]::Abs($script:trackerPos.Y-$y) -gt 2)) { Start-TrackerSession }
    $script:trackerPos = [pscustomobject]@{ X = $x; Y = $y }
    if (-not $script:selCatchId -and -not $script:formPending) {
        $txtCatchX.Text = [string]$x
        $txtCatchY.Text = [string]$y
        Update-CatchSpotInfo
    }
    [System.Media.SystemSounds]::Exclamation.Play()
    Set-Status ("{0}: {1}:{2}" -f (T "trackerPos"), $x, $y)
}

function Match-Temp([string[]]$lines) {
    $loc = New-Object System.Collections.Generic.List[string]
    $en = New-Object System.Collections.Generic.List[string]
    foreach ($k in @("cold", "normal", "warm")) {
        $loc.Add($k); $en.Add($k)
        foreach ($lp in $langs.PSObject.Properties) {
            $v = [string]$lp.Value.("temp_" + $k)
            if ($v) { $loc.Add($v); $en.Add($k) }
        }
    }
    foreach ($l in $lines) {
        foreach ($wd in ($l -split "\s+")) {
            $t = ($wd -replace "rn", "m").Trim()
            if ($t.Length -lt 3) { continue }
            $hit = Match-Name $t $loc.ToArray() $en.ToArray() 0.7
            if ($hit) { return $hit.En }
        }
    }
    return ""
}
$script:trackerPos = $null
$script:trackerLake = $null
$script:trackerLakeManual = $false
$script:trackerSession = [guid]::NewGuid().ToString("N").Substring(0, 8)

function Start-TrackerSession {
    $script:trackerSession = [guid]::NewGuid().ToString("N").Substring(0, 8)
}

function Set-TrackerLakeAuto([string]$lakeId) {
    if ($script:trackerLakeManual -or -not $lakeId) { return }
    if ($script:trackerLake -and $script:trackerLake -ne $lakeId) { Start-TrackerSession }
    $script:trackerLake = $lakeId
    Set-TrackerLakeCombo $lakeId
}

function Set-TrackerLakeCombo([string]$lakeId) {
    $script:busy = $true
    Set-ComboKey $cmbCatchLake $lakeId
    Set-Choices $cmbCatchFish (Get-FishChoices $lakeId)
    $script:busy = $false
    Update-CatchSpotInfo
}

function Guess-TrackerLake($fishes) {
    if ($script:trackerLake) { return $script:trackerLake }
    $fl = @($fishes | Where-Object { $_ })
    $combo = Get-ComboKey $cmbCatchLake
    if ($fl.Count -eq 0) { return $combo }
    $best = @()
    $bestScore = 0
    foreach ($l in @($game.lakes)) {
        $sc = 0
        foreach ($f in $fl) { if (@($l.fish) -contains $f) { $sc++ } }
        if ($sc -gt $bestScore) { $bestScore = $sc; $best = @($l.id) } elseif ($sc -eq $bestScore -and $sc -gt 0) { $best += $l.id }
    }
    if ($best.Count -eq 0) { return $combo }
    if ($best -contains $combo) { return $combo }
    if ($best.Count -eq 1) { return $best[0] }
    $since = (Get-Date).ToUniversalTime().AddDays(-14).ToString("yyyy-MM-ddTHH:mm:ss")
    $pick = $best[0]
    $pickN = -1
    foreach ($id in $best) {
        $n = 0
        foreach ($r in $script:commReports) {
            if ($r["lake"] -ne $id -or [string]$r["posted"] -lt $since) { continue }
            foreach ($f in $fl) { if (@($r["fish"]) -contains $f) { $n++; break } }
        }
        if ($n -gt $pickN) { $pickN = $n; $pick = $id }
    }
    $pick
}
$script:trackerPending = New-Object System.Collections.ArrayList
$script:fuzzyReady = $false
$script:ocrScript = @'
param([string]$Path, [string]$Lang, [string]$Crop)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
$work = $Path
$img = [System.Drawing.Image]::FromFile($Path)
$w = $img.Width
$h = $img.Height
$max = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
$zoom = ($Crop -in @("hud", "mini", "catch")) -or $Crop.StartsWith("rect:")
if ($Crop -eq "grid" -or $zoom) {
    $cx = [int]($w * 0.24)
    $cy = [int]($h * 0.12)
    $cw = [int]($w * 0.73)
    $ch = [int]($h * 0.83)
    if ($Crop -eq "hud") {
        $cx = [int]($w * 0.9)
        $cy = [int]($h * 0.1)
        $cw = [int]($w * 0.1)
        $ch = [int]($h * 0.022)
    } elseif ($Crop -eq "mini") {
        $cx = [int]($w * 0.9335)
        $cy = [int]($h * 0.9244)
        $cw = [int]($w * 0.033)
        $ch = [int]($h * 0.022)
    } elseif ($Crop -eq "catch") {
        $cx = [int]($w * 0.38)
        $cy = [int]($h * 0.12)
        $cw = [int]($w * 0.24)
        $ch = [int]($h * 0.045)
    } elseif ($Crop.StartsWith("rect:")) {
        $rv = @($Crop.Substring(5).Split(",") | ForEach-Object { [int]$_ })
        $cx = [math]::Max(0, $rv[0])
        $cy = [math]::Max(0, $rv[1])
        $cw = [math]::Min($w-$cx, $rv[2])
        $ch = [math]::Min($h-$cy, $rv[3])
    }
    $f = [math]::Min($max / $cw, $max / $ch)
    if ($zoom) { $f = [math]::Min(4.0, $f) }
    $bmp = New-Object System.Drawing.Bitmap ([int]($cw * $f)), ([int]($ch * $f))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height), (New-Object System.Drawing.Rectangle $cx, $cy, $cw, $ch), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    if ($Crop -eq "hud" -or $Crop -eq "catch") {
        for ($py = 0; $py -lt $bmp.Height; $py++) {
            for ($px = 0; $px -lt $bmp.Width; $px++) {
                $c = $bmp.GetPixel($px, $py)
                $v = [math]::Min($c.R, [math]::Min($c.G, $c.B))
                $sat = [math]::Max($c.R, [math]::Max($c.G, $c.B))-$v
                if ($v -gt 95 -and $sat -lt 45) { $bmp.SetPixel($px, $py, [System.Drawing.Color]::Black) } else { $bmp.SetPixel($px, $py, [System.Drawing.Color]::White) }
            }
        }
    }
    $work = [IO.Path]::Combine([IO.Path]::GetTempPath(), "rf4companion_ocr_" + ($Crop -replace "[^a-z]", "") + ".png")
    $bmp.Save($work, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
} elseif ($w -gt $max -or $h -gt $max -or $w -lt 1800) {
    $f = [math]::Min($max / $w, $max / $h)
    if ($w -lt 1800) { $f = [math]::Min(2.0, $f) }
    $bmp = New-Object System.Drawing.Bitmap ([int]($w * $f)), ([int]($h * $f))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($img, 0, 0, $bmp.Width, $bmp.Height)
    $g.Dispose()
    $work = [IO.Path]::Combine([IO.Path]::GetTempPath(), "rf4companion_ocr.png")
    $bmp.Save($work, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$img.Dispose()
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($work)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$sb = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language $Lang))
if (-not $eng) { $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
$res = Await ($eng.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])
$stream.Dispose()
foreach ($ln in $res.Lines) {
    $x1 = 1e9; $y1 = 1e9; $x2 = 0; $y2 = 0
    foreach ($wd in $ln.Words) {
        $r = $wd.BoundingRect
        if ($r.X -lt $x1) { $x1 = $r.X }
        if ($r.Y -lt $y1) { $y1 = $r.Y }
        if (($r.X + $r.Width) -gt $x2) { $x2 = $r.X + $r.Width }
        if (($r.Y + $r.Height) -gt $y2) { $y2 = $r.Y + $r.Height }
    }
    "{0}`t{1}`t{2}`t{3}`t{4}" -f [int]$x1, [int]$y1, [int]($x2-$x1), [int]($y2-$y1), $ln.Text
}
'@

function Get-OcrLanguages {
    try {
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        return @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    } catch {
        return @()
    }
}

function Get-OcrLangCode([string]$tag) {
    if ($tag -match "^zh-(TW|HK|Hant)") { return "zh-TW" }
    return $tag.Split("-")[0]
}

function Ensure-Fuzzy {
    if ($script:fuzzyReady) { return }
    Add-Type -TypeDefinition @"
using System;
public static class RF4Fuzzy {
    static string Norm(string s) {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.ToLowerInvariant()) { if (char.IsLetterOrDigit(ch)) sb.Append(ch); }
        return sb.ToString();
    }
    static int Lev(string a, string b) {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++) {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++) {
                int cost = a[i-1] == b[j-1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j-1] + 1, prev[j] + 1), prev[j-1] + cost);
            }
            var t = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }
    public static string Best(string query, string[] candidates) {
        string q = Norm(query);
        if (q.Length < 2) return "-1|0";
        int best = -1; double score = 0;
        for (int i = 0; i < candidates.Length; i++) {
            string c = Norm(candidates[i]);
            if (c.Length == 0) continue;
            if (Math.Abs(c.Length-q.Length) > Math.Max(c.Length, q.Length) / 2) continue;
            double sc = 1.0-(double)Lev(q, c) / Math.Max(q.Length, c.Length);
            if (sc > score) { score = sc; best = i; }
        }
        return best + "|" + score.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
"@
    $script:fuzzyReady = $true
}

$script:matchCache = @{}

function Get-MatchLists([string]$code) {
    if ($script:matchCache.ContainsKey($code)) { return $script:matchCache[$code] }
    $fishLoc = New-Object System.Collections.Generic.List[string]
    $fishEn = New-Object System.Collections.Generic.List[string]
    foreach ($t in @($game.trophies)) {
        $e = $script:names[$t.fish]
        $v = $t.fish
        if ($e -and $e.$code) { $v = [string]$e.$code }
        $fishLoc.Add($v)
        $fishEn.Add([string]$t.fish)
    }
    $items = Get-ItemNames
    $idx = $script:itemLangIdx[$code]
    if ($null -eq $idx) { $idx = 0 }
    $itemLoc = New-Object System.Collections.Generic.List[string]
    $itemEn = New-Object System.Collections.Generic.List[string]
    foreach ($row in $items.Values) {
        $itemLoc.Add([string]$row[$idx])
        $itemEn.Add([string]$row[0])
    }
    $r = [pscustomobject]@{ FishLoc = $fishLoc.ToArray(); FishEn = $fishEn.ToArray(); ItemLoc = $itemLoc.ToArray(); ItemEn = $itemEn.ToArray() }
    $script:matchCache[$code] = $r
    $r
}

function Match-Name([string]$text, [string[]]$loc, [string[]]$en, [double]$min) {
    Ensure-Fuzzy
    $res = [RF4Fuzzy]::Best($text, $loc).Split("|")
    $i = [int]$res[0]
    $sc = [double]::Parse($res[1], [System.Globalization.CultureInfo]::InvariantCulture)
    if ($i -ge 0 -and $sc -ge $min) { return [pscustomobject]@{ En = $en[$i]; Score = $sc; Loc = $loc[$i] } }
    return $null
}

function Resolve-RecipeItem([string]$text, $lists) {
    $hit = Match-Name $text $lists.ItemLoc $lists.ItemEn 0.75
    if (-not $hit) { return $text }
    if ($hit.Score -ge 0.9) { return $hit.En }
    $q = ($text.ToLower() -replace "[^\p{L}\p{Nd}]", "")
    $c = ([string]$hit.Loc).ToLower() -replace "[^\p{L}\p{Nd}]", ""
    if ($q.Length -ge 8 -and $c.EndsWith($q)) { return $hit.En }
    return $text
}

function Get-RecipeGeoComposition($lists) {
    $g = @($script:lastGeo)
    if ($g.Count -eq 0) { return $null }
    $labRx = "^(Basis|Base|Основа|Zusätze|Zusatze|Zusatz|Additives?|Добавк\w*|Lebensmittel|Food|Продукт\w*|Aroma|Aromatizer|Flavor|Ароматизатор)$"
    $labels = @($g | Where-Object { $_.Text.Trim() -match $labRx } | Sort-Object Y)
    if ($labels.Count -lt 2) { return $null }
    $used = @{}
    $comp = New-Object System.Collections.ArrayList
    $aroma = ""
    foreach ($lb in $labels) {
        $best = $null
        $bestDx = 1e9
        foreach ($e in $g) {
            if ($e -eq $lb -or $used.ContainsKey($e)) { continue }
            $t = $e.Text.Trim()
            if ($t -match $labRx -or $t.Length -lt 3) { continue }
            if ([math]::Abs(($e.Y + $e.H / 2)-($lb.Y + $lb.H / 2)) -gt ($lb.H * 0.7)) { continue }
            $dx = $e.X-($lb.X + $lb.W)
            if ($dx -lt 0 -or $dx -gt ($lb.H * 30)) { continue }
            if ($dx -lt $bestDx) { $bestDx = $dx; $best = $e }
        }
        if (-not $best) { return [pscustomobject]@{ Incomplete = $true; Comp = @(); Aroma = "" } }
        $used[$best] = $true
        $v = Resolve-RecipeItem ($best.Text.Trim() -replace "^[^A-Za-zÄÖÜäöüА-Яа-я0-9]+", "") $lists
        if ($lb.Text.Trim() -match "^(Aroma|Aromatizer|Flavor|Ароматизатор)$") { $aroma = $v } else { $comp.Add($v) | Out-Null }
    }
    if ($comp.Count -eq 0) { return $null }
    [pscustomobject]@{ Incomplete = $false; Comp = @($comp); Aroma = $aroma }
}

function Parse-RecipeLines([string[]]$lines, $lists) {
    $name = ""
    foreach ($l in $lines) { if ($l -match "\*\*.+\*\*" -and $l -notmatch "Anzahl|Amount|Количество" -and $l.Trim().Length -gt $name.Length) { $name = $l.Trim() } }
    if (-not $name) {
        for ($i = 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^(Typ|Type|Тип)\s*:\s*(Trockenmischung|Futtermischung|PVA|Dry mix|Groundbait|Сухая|Прикормка|ПВА)") { $name = $lines[$i-1].Trim(); break }
        }
    }
    if (-not $name) {
        for ($i = 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^(Typ|Type|Тип|Anzahl|Amount|Количество)\s*:") { $name = $lines[$i-1].Trim(); break }
        }
    }
    if (-not $name) { return $null }
    $all = $lines -join " "
    $type = "pva"
    if ($all -match "Trockenmischung|Futtermischung|Dry mix|Groundbait|Прикормка|Сухая смесь") { $type = "groundbait" }
    $startIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -match "^\d+\s*(h|ч|Std)\.?$") { $startIdx = $i } }
    if ($startIdx -lt 0) {
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "ZUSAMMENSETZUNG|COMPOSITION|СОСТАВ") { $startIdx = $i } }
    }
    $labelRx = "^(ZUSTAND|CONDITION|СОСТОЯНИЕ|Kann verwendet|KATEGORIEN|CATEGORIES|Handwerk|Crafting|Favoriten)"
    $aromaIdx = -1
    for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -match "^(Aroma|Aromatizer|Flavor|Ароматизатор)$") { $aromaIdx = $i; break } }
    $endIdx = $lines.Count
    if ($aromaIdx -lt 0) {
        for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -match $labelRx) { $endIdx = $i; break } }
    }
    $comp = New-Object System.Collections.ArrayList
    $stop = $endIdx
    if ($aromaIdx -gt 0) { $stop = $aromaIdx }
    for ($i = $startIdx + 1; $i -lt $stop; $i++) {
        $l = $lines[$i].Trim()
        $l = $l -replace "^(Zusätze|Zusatze|Zusatz|Basis|Additives?|Base|Добавк\w*|Основа)\s+", ""
        if ($l.Length -lt 3 -or $l -match "^(Basis|Zusatz|Zusätze|Base|Additive|Основа|Добавка)$" -or $l -match $labelRx) { continue }
        $l = $l -replace "^[^A-Za-zÄÖÜäöüА-Яа-я0-9]+", ""
        $l = $l -replace "^[A-Z]{1,2}\W+(?=[a-zäöü])", ""
        $comp.Add((Resolve-RecipeItem $l $lists)) | Out-Null
    }
    $aroma = ""
    if ($aromaIdx -gt 0) {
        for ($i = $aromaIdx + 1; $i -lt $lines.Count; $i++) {
            $l = $lines[$i].Trim()
            if ($l.Length -lt 3 -or $l -match $labelRx) { continue }
            $aroma = Resolve-RecipeItem $l $lists
            break
        }
    }
    $gc = Get-RecipeGeoComposition $lists
    if ($gc -and $gc.Incomplete) { return $null }
    if ($gc) { $comp = New-Object System.Collections.ArrayList; foreach ($x in @($gc.Comp)) { $comp.Add($x) | Out-Null }; $aroma = $gc.Aroma }
    if ($comp.Count -eq 0 -and -not $aroma) { return $null }
    [pscustomobject]@{ Name = $name; Type = $type; Base = $(if ($comp.Count -gt 0) { $comp[0] } else { "" }); Additives = @($comp | Select-Object -Skip 1); Attractant = $aroma }
}

$script:lastGeo = @()

function Parse-CardItems($geo, $lists) {
    $g = @($geo)
    if ($g.Count -eq 0) { return $null }
    $width = ($g | ForEach-Object { $_.X + $_.W } | Measure-Object -Maximum).Maximum
    $minX = $width * 0.2
    $wrx = "^\W{0,2}([Zz0-9OoIl][\d\.,OoIl]*)\s*(kg|k|g|кг|г)\.?$"
    $weights = New-Object System.Collections.ArrayList
    foreach ($e in $g) {
        $t = $e.Text.Trim()
        $m = [regex]::Match($t, $wrx)
        if (-not $m.Success) { continue }
        $u = $m.Groups[2].Value
        if ($u -eq "k") { $u = "kg" }
        $w = $null
        if ($t -match "^[\dZz]") { $w = Convert-OcrWeight $m.Groups[1].Value $u }
        $weights.Add([pscustomobject]@{ E = $e; Weight = $w; Used = $false }) | Out-Null
    }
    $items = New-Object System.Collections.ArrayList
    foreach ($e in ($g | Sort-Object Y, X)) {
        if ($e.X -lt $minX) { continue }
        $l = $e.Text.Trim()
        if ($l.Length -lt 3 -or $l.Length -gt 40) { continue }
        if ($l -match "\d+\s*min|kg|%|/\s*\d|^S\s*\d|\d[\.,]\d") { continue }
        $f = Match-Fish $l $lists
        $above = @($g | Where-Object { $_ -ne $e -and [math]::Abs($_.X-$e.X) -lt ($e.H * 2) -and ($e.Y-$_.Y) -gt 0 -and ($e.Y-$_.Y) -lt ($e.H * 1.8) -and $_.Text.Trim() -notmatch "\d" } | Select-Object -First 1)
        if ($above.Count -gt 0) {
            $f2 = Match-Fish ($above[0].Text.Trim() + " " + $l) $lists
            if ($f2 -and $f2.Score -ge 0.8 -and (-not $f -or $f2.Score -ge $f.Score-0.05)) { $f = $f2 }
        }
        if (-not $f) { continue }
        $best = $null
        $bestD = 1e9
        foreach ($wt in $weights) {
            if ($wt.Used) { continue }
            $dx = [math]::Abs($wt.E.X-$e.X)
            $dy = [math]::Abs($wt.E.Y-$e.Y)
            if ($dx -gt ($e.H * 2.5) -or $dy -gt ($e.H * 2.6) -or $dy -lt 2) { continue }
            $dist = $dy + ($dx * 0.5)
            if ($dist -lt $bestD) { $bestD = $dist; $best = $wt }
        }
        $w = $null
        if ($best) { $best.Used = $true; $w = $best.Weight }
        $items.Add([pscustomobject]@{ Fish = $f.En; Weight = $w }) | Out-Null
    }
    @($items)
}

function Parse-KeepnetItems([string[]]$lines, $lists) {
    $seq = @(Parse-KeepnetSequential $lines $lists)
    if (@($script:lastGeo).Count -gt 0) {
        $ci = Parse-CardItems $script:lastGeo $lists
        if ($null -ne $ci) {
            $a = @($ci)
            $b = $seq
            if ($b.Count -gt $a.Count) { $a = $seq; $b = @($ci) }
            return @(Merge-KeepnetItems $a $b)
        }
    }
    return $seq
}

function Parse-KeepnetSequential([string[]]$lines, $lists) {
    $start = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "Nach Fangzeit|Nach Gewicht|Nach Preis|By catch time|By time|By weight|By price|По времени|По весу|По цене") { $start = $i + 1 }
    }
    $items = New-Object System.Collections.ArrayList
    for ($i = $start; $i -lt $lines.Count; $i++) {
        $l = $lines[$i].Trim()
        if ($l.Length -lt 3 -or $l.Length -gt 40) { continue }
        if ($l -match "\d+\s*min|kg|%|/\s*\d|^S\s*\d") { continue }
        $f = Match-Fish $l $lists
        $wi = $i-1
        if ($i -gt $start -and $lines[$i-1].Trim() -notmatch "\d" -and $lines[$i-1].Trim().Length -ge 3) {
            $f2 = Match-Fish ($lines[$i-1].Trim() + " " + $l) $lists
            if ($f2 -and $f2.Score -ge 0.8 -and (-not $f -or $f2.Score -ge $f.Score-0.05)) { $f = $f2; $wi = $i-2 }
        }
        if (-not $f) { continue }
        $w = $null
        if ($wi -ge 0) {
            $m = [regex]::Match($lines[$wi], "^\s*(\S*?)\s*(kg|g|кг|г|lb|lbs)\s*$")
            if ($m.Success) {
                $w = Convert-OcrWeight $m.Groups[1].Value $m.Groups[2].Value
                if ($null -ne $w -and $w -le 0) { $w = $null }
            }
        }
        $items.Add([pscustomobject]@{ Fish = $f.En; Weight = $w }) | Out-Null
    }
    @($items)
}

function Test-SimilarWeight($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    $x = [string][int]$a
    $y = [string][int]$b
    if ($x -eq $y) { return $true }
    if ($x.Length -ne $y.Length) { return $false }
    $diff = 0
    for ($i = 0; $i -lt $x.Length; $i++) { if ($x[$i] -ne $y[$i]) { $diff++ } }
    return ($diff -le 1)
}

function Merge-KeepnetItems($a, $b) {
    $res = New-Object System.Collections.ArrayList
    foreach ($it in @($a)) { $res.Add([pscustomobject]@{ Fish = $it.Fish; Weight = $it.Weight }) | Out-Null }
    foreach ($it in @($b)) {
        if ($null -ne $it.Weight) {
            $same = @($res | Where-Object { $_.Fish -eq $it.Fish -and (Test-SimilarWeight $_.Weight $it.Weight) })
            if ($same.Count -gt 0) { continue }
            $nul = @($res | Where-Object { $_.Fish -eq $it.Fish -and $null -eq $_.Weight } | Select-Object -First 1)
            if ($nul.Count -gt 0) { $nul[0].Weight = $it.Weight; continue }
        }
        $ca = @($res | Where-Object { $_.Fish -eq $it.Fish }).Count
        $cb = @(@($b) | Where-Object { $_.Fish -eq $it.Fish }).Count
        if ($ca -lt $cb) { $res.Add([pscustomobject]@{ Fish = $it.Fish; Weight = $it.Weight }) | Out-Null }
    }
    @($res)
}

function Apply-KeepnetItems($items, $total, [datetime]$time, [string]$path) {
    $st = $script:trackerSetup
    $today = $time.ToString("yyyy-MM-dd")
    $guessLake = Guess-TrackerLake @($items | ForEach-Object { $_.Fish })
    $added = 0
    foreach ($sp in (@($items) | Group-Object Fish)) {
        $fish = $sp.Name
        $known = New-Object System.Collections.ArrayList
        foreach ($p in $script:trackerPending) { if ($p.Fish -eq $fish -and $p.Lake -eq $guessLake) { $known.Add([pscustomobject]@{ Weight = $p.Weight; Ref = $p }) | Out-Null } }
        foreach ($c in $script:catches) { if ($c.fish -eq $fish -and $c.date -eq $today -and $c.lake -eq $guessLake) { $known.Add([pscustomobject]@{ Weight = $(if ("$($c.weight)" -ne "") { [int]$c.weight } else { $null }); Ref = $null }) | Out-Null } }
        $withW = @($sp.Group | Where-Object { $null -ne $_.Weight })
        $noW = @($sp.Group | Where-Object { $null -eq $_.Weight })
        $newOnes = New-Object System.Collections.ArrayList
        foreach ($it in $withW) {
            $k = @($known | Where-Object { $_.Weight -eq $it.Weight } | Select-Object -First 1)
            if ($k.Count -eq 0) { $k = @($known | Where-Object { Test-SimilarWeight $_.Weight $it.Weight } | Select-Object -First 1) }
            if ($k.Count -gt 0) { $known.Remove($k[0]); continue }
            $kn = @($known | Where-Object { $null -eq $_.Weight -and $_.Ref } | Select-Object -First 1)
            if ($kn.Count -gt 0) { $kn[0].Ref.Weight = $it.Weight; $known.Remove($kn[0]); continue }
            $newOnes.Add($it) | Out-Null
        }
        foreach ($it in $noW) {
            if ($known.Count -gt 0) { $known.RemoveAt(0); continue }
            $newOnes.Add($it) | Out-Null
        }
        foreach ($it in $newOnes) {
            $script:trackerPending.Add([pscustomobject]@{
                Time = $time; Fish = $it.Fish; Weight = $it.Weight; Lake = $guessLake; SpotId = ""; Session = $script:trackerSession; X = $(if ($script:trackerPos) { $script:trackerPos.X } else { $null }); Y = $(if ($script:trackerPos) { $script:trackerPos.Y } else { $null })
                Baits = $(if ($st) { @($st.Baits) } else { @() }); Dip = $(if ($st) { $st.Dip } else { "" }); Pva = $(if ($st) { $st.Pva } else { "" }); Rig = $(if ($st) { $st.Rig } else { "" })
                Temp = [string]$script:trackerTemp; Tech = $(if ($st) { [string]$st.Tech } else { "" }); Path = $path
            }) | Out-Null
            $added++
        }
    }
    $recognized = @($items).Count
    [System.Media.SystemSounds]::Asterisk.Play()
    if ($total) { Set-Status ((T "trackerKeepnetOf") -f $recognized, $total, $added) }
    else { Set-Status ("{0}: {1}" -f (T "trackerKeepnet"), $added) }
    Update-TrackerUi
}

function Convert-OcrWeight([string]$numText, [string]$unit) {
    $t = $numText.Trim()
    $t = $t -replace "^[Zz]", "2" -replace "[Oo]", "0" -replace "[lI|]", "1" -replace "\s", ""
    if ($t -notmatch "^\d[\d\.,]*$") { return $null }
    $num = Parse-Num $t
    if ($null -eq $num) { return $null }
    $u = $unit.ToLower()
    if ($u -in @("kg", "кг", "公斤")) {
        if ($t -notmatch "[\.,]" -and $num -ge 100) { $num = $num / 1000 }
        return [int][math]::Round($num * 1000)
    }
    if ($u -like "lb*") { return [int][math]::Round($num * 453.592) }
    return [int][math]::Round($num)
}

function Match-Fish([string]$text, $lists) {
    $best = $null
    foreach ($c in @($text, ($text -replace "^\S{1,2}\s+", ""))) {
        $hit = Match-Name $c $lists.FishLoc $lists.FishEn 0.7
        if ($hit -and (-not $best -or $hit.Score -gt $best.Score)) { $best = $hit }
    }
    $best
}

function Match-Lake([string]$text, [string]$code) {
    $loc = @($game.lakes | ForEach-Object { $v = $_.name.$code; if (-not $v) { $v = $_.name.en }; [string]$v })
    $ids = @($game.lakes | ForEach-Object { [string]$_.id })
    $hit = Match-Name $text $loc $ids 0.85
    if ($hit) { return $hit.En }
    return ""
}

function Parse-TrackerLines([string[]]$lines, [string]$code) {
    $lists = Get-MatchLists $code
    $out = [pscustomobject]@{ Kind = ""; Fish = ""; Weight = $null; Baits = @(); Dip = ""; Pva = ""; Rig = ""; Lake = ""; Items = @() }
    $allText = $lines -join " "
    $isKeepnetScreen = $allText -match "Setzkescher|Keepnet|Садок|Siatka"
    $isDetailScreen = $isKeepnetScreen -and ($allText -match "Fangzeit|Gewässer|Catch time|Waterbody|Время поимки|Водоём")
    for ($i = 1; $i -lt $lines.Count -and $isDetailScreen; $i++) {
        $m = [regex]::Match($lines[$i], "^\s*(Gewicht|Weight|Вес|Waga|Poids|Peso)\s*:\s*([\d\.,]+)\s*(kg|g|кг|г|lb|lbs)")
        if (-not $m.Success) { continue }
        $w = Convert-OcrWeight $m.Groups[2].Value $m.Groups[3].Value
        if ($null -eq $w) { continue }
        $f = Match-Fish $lines[$i-1] $lists
        if (-not $f) { continue }
        $out.Kind = "detail"
        $out.Weight = $w
        $out.Fish = $f.En
        foreach ($l in $lines) {
            if (-not $out.Lake -and $l.Length -ge 4 -and $l.Length -le 40) { $out.Lake = Match-Lake $l $code }
        }
        $bi = -1
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -match "^(Köder|Bait|Наживка|Przynęta|Appât|Cebo|Esca|Isca)$") { $bi = $j }
        }
        if ($bi -ge 0) {
            $baits = New-Object System.Collections.ArrayList
            for ($j = $bi + 1; $j -lt $lines.Count -and $baits.Count -lt 2; $j++) {
                $l = $lines[$j].Trim()
                if (-not $l -or $l -match "^\d[\d\.,]*\s*(kg|g|cm|кг|г|см)$" -or $l -match "^\d{1,2}:\d{2}$") { break }
                $hit = Match-Name $l $lists.ItemLoc $lists.ItemEn 0.8
                if ($hit) { $baits.Add($hit.En) | Out-Null } else { $baits.Add($l) | Out-Null }
            }
            $out.Baits = @($baits)
        }
        return $out
    }
    if ($allText -match "ZUSAMMENSETZUNG|Zusammensetzung|COMPOSITION|Composition|СОСТАВ|Состав") {
        $rec = Parse-RecipeLines $lines $lists
        if ($rec) {
            $out.Kind = "recipe"
            $out | Add-Member -NotePropertyName Recipe -NotePropertyValue $rec -Force
            return $out
        }
    }
    $isKeepnet = $isKeepnetScreen -and ($allText -match "Kapazität|Capacity|Вместимость|Pojemność")
    $isMarket = ($allText -match "Fischmarkt|Fish market|Fish Market|Рыбный рынок|Targ rybny") -and ($allText -match "Zum Verkauf|For sale|На продажу|Na sprzedaż")
    if ($isMarket) {
        $items = @(Parse-KeepnetItems $lines $lists)
        $total = $null
        $sm = [regex]::Match($allText, "(\d{1,3})\s*(stk|Stk|pcs|шт)")
        if ($sm.Success) { $total = [int]$sm.Groups[1].Value }
        if ($items.Count -gt 0) {
            $out.Kind = "keepnet"
            $out.Items = $items
            $out | Add-Member -NotePropertyName Total -NotePropertyValue $total -Force
            return $out
        }
    }
    if ($isKeepnet) {
        $items = @(Parse-KeepnetItems $lines $lists)
        $total = $null
        $cm = [regex]::Match($allText, "(Kapazität|Capacity|Вместимость|Pojemność)\D{0,20}?(\d{1,3})\s*/\s*(\d{2,3})")
        if ($cm.Success) { $total = [int]$cm.Groups[2].Value }
        if ($items.Count -gt 0) {
            $out.Kind = "keepnet"
            $out.Items = $items
            $out | Add-Member -NotePropertyName Total -NotePropertyValue $total -Force
            return $out
        }
    }
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], "(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs|公斤|克)\s*/\s*\d")
        if (-not $m.Success) { continue }
        $num = Parse-Num $m.Groups[1].Value
        if ($null -eq $num) { continue }
        $unit = $m.Groups[2].Value.ToLower()
        $grams = $num
        if ($unit -in @("kg", "кг", "公斤")) { $grams = $num * 1000 }
        if ($unit -like "lb*") { $grams = $num * 453.592 }
        $best = Match-Fish $lines[$i-1] $lists
        if (-not $best) { continue }
        $out.Kind = "catch"
        $out.Weight = [int][math]::Round($grams)
        $out.Fish = $best.En
        return $out
    }
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "Köder\s*Kombination|Bait\s*combination|Комбинация\s*наживок|Kombinacja\s*przyn") { $start = $i; break }
    }
    if ($start -lt 0) {
        $hud = ($lines -join " ") -match "Bedienfeld|Esc|Chat|Hilfe|Rute aufnehmen|Menu|Меню"
        foreach ($l in $lines) {
            if ($hud -or $lines.Count -gt 14) { break }
            $pm = [regex]::Match($l, "^\s*(\d{1,4})\s*[:;]\s*(\d{1,4})\s*$")
            if ($pm.Success) {
                $out.Kind = "position"
                $out | Add-Member -NotePropertyName X -NotePropertyValue ([int]$pm.Groups[1].Value) -Force
                $out | Add-Member -NotePropertyName Y -NotePropertyValue ([int]$pm.Groups[2].Value) -Force
                return $out
            }
        }
        return $out
    }
    for ($i = 0; $i -lt $start; $i++) {
        if ($lines[$i] -match "^(Angelmontage|Rig|Монтаж|Zestaw)\b" -and $i + 1 -lt $start -and $lines[$i+1] -notmatch "Ändern|Change|Изменить") { $out.Rig = $lines[$i+1].Trim(); break }
    }
    $baits = New-Object System.Collections.ArrayList
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -notmatch "\s[O0o©]\s*$") { continue }
        $name = ($l -replace "\s[O0o©]\s*$", "").Trim()
        $next = ""
        if ($i + 1 -lt $lines.Count) { $next = $lines[$i+1] }
        $next2 = ""
        if ($i + 2 -lt $lines.Count) { $next2 = $lines[$i+2] }
        $hit = Match-Name $name $lists.ItemLoc $lists.ItemEn 0.8
        $val = $name
        if ($hit) { $val = $hit.En }
        if ($next -match "^(Typ|Type|Тип)\s*:\s*D" -or $next -match "\bDip\b|Дип") { $out.Dip = $val }
        elseif (($next + " " + $next2) -match "Qualität|Quality|Качество") { $out.Pva = $name }
        else { $baits.Add($val) | Out-Null }
    }
    $out.Baits = @($baits | Select-Object -First 2)
    $headText = ($lines[0..$start] -join " ")
    $tech = ""
    if ($headText -match "Spinn|Spinning|Спиннинг|Baitcast|Jerk") { $tech = "spin" }
    elseif ($headText -match "Feeder|Karpfen|Carp|Grund|Bottom|Picker|Фидер|Карпов|Донн") { $tech = "bottom" }
    elseif ($headText -match "Match|Bolo|Pose|Float|Telesk|Поплав|Махов|Матч") { $tech = "float" }
    elseif ($headText -match "Meer|Marine|Sea|Морск") { $tech = "marine" }
    elseif ($out.Dip -or $out.Pva -or $out.Baits.Count -ge 2) { $tech = "bottom" }
    $out | Add-Member -NotePropertyName Tech -NotePropertyValue $tech -Force
    if ($out.Baits.Count -gt 0 -or $out.Dip -or $out.Pva) { $out.Kind = "setup" }
    $out
}

function Format-TrackerSetup {
    $st = $script:trackerSetup
    if (-not $st) { return (T "trackerNoSetup") }
    $parts = @()
    if (@($st.Baits).Count -gt 0) { $parts += (T "bait") + ": " + ((@($st.Baits) | ForEach-Object { N $_ }) -join " + ") }
    if ($st.Dip) { $parts += (T "dipL") + ": " + (N $st.Dip) }
    if ($st.Pva) { $parts += (T "pvaL") + ": " + $st.Pva }
    if ($st.Rig) { $parts += (T "rigL") + ": " + $st.Rig }
    if ($st.Tech) { $parts += (T "technique") + ": " + (T ("tech_" + $st.Tech)) }
    if ($script:trackerTemp) { $parts += (T "temperature") + ": " + (T ("temp_" + $script:trackerTemp)) }
    (T "trackerSetup") + "   " + ($parts -join "   |   ")
}

$script:trackerPendingFile = Join-Path $userDir "tracker_pending.json"

$script:pendingLoadOk = $true

function Save-TrackerPending {
    if (-not $script:pendingLoadOk) { return }
    $arr = @($script:trackerPending | ForEach-Object {
        [ordered]@{ time = $_.Time.ToString("o"); fish = $_.Fish; weight = $_.Weight; lake = $_.Lake; spotId = $_.SpotId; session = $_.Session; x = $_.X; y = $_.Y; baits = @($_.Baits); dip = $_.Dip; pva = $_.Pva; rig = $_.Rig; temp = $_.Temp; tech = $_.Tech; path = $_.Path }
    })
    for ($try = 0; $try -lt 5; $try++) {
        try {
            [System.IO.File]::WriteAllText($script:trackerPendingFile, (ConvertTo-Json -InputObject @($arr) -Depth 4), (New-Object System.Text.UTF8Encoding $false))
            break
        } catch { Start-Sleep -Milliseconds 150 }
    }
}

function Load-TrackerPending {
    $script:trackerPending.Clear()
    if (-not (Test-Path -LiteralPath $script:trackerPendingFile)) { return }
    try {
        $data = Read-Json $script:trackerPendingFile
        foreach ($x in $data) {
            if (-not $x) { continue }
            $script:trackerPending.Add([pscustomobject]@{
                Time = [datetime]::Parse($x.time, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
                Fish = [string]$x.fish; Weight = $(if ($null -ne $x.weight -and "$($x.weight)" -ne "") { [int]$x.weight } else { $null }); Lake = [string]$x.lake; SpotId = [string]$x.spotId; Session = [string]$x.session; X = $x.x; Y = $x.y
                Baits = @($x.baits | Where-Object { $_ }); Dip = [string]$x.dip; Pva = [string]$x.pva; Rig = [string]$x.rig; Temp = [string]$x.temp; Tech = [string]$x.tech; Path = [string]$x.path
            }) | Out-Null
        }
    } catch {
        Write-ErrorLog ("Tracker pending load: " + $_.Exception.Message)
        try { Copy-Item -LiteralPath $script:trackerPendingFile -Destination ($script:trackerPendingFile + ".bad") -Force } catch { }
        $script:pendingLoadOk = $false
    }
}

$script:trackerStateFile = Join-Path $userDir "tracker_state.json"

function Save-TrackerState {
    $st = $script:trackerSetup
    $o = [ordered]@{
        session = $script:trackerSession; lake = $script:trackerLake; temp = $script:trackerTemp
        pos = $(if ($script:trackerPos) { [ordered]@{ x = $script:trackerPos.X; y = $script:trackerPos.Y } } else { $null })
        setup = $(if ($st) { [ordered]@{ baits = @($st.Baits); dip = $st.Dip; pva = $st.Pva; rig = $st.Rig; tech = $st.Tech } } else { $null })
    }
    for ($try = 0; $try -lt 5; $try++) {
        try {
            [System.IO.File]::WriteAllText($script:trackerStateFile, (ConvertTo-Json -InputObject $o -Depth 5), (New-Object System.Text.UTF8Encoding $false))
            break
        } catch { Start-Sleep -Milliseconds 150 }
    }
}

function Load-TrackerState {
    if (-not (Test-Path -LiteralPath $script:trackerStateFile)) { return $false }
    try {
        $d = Read-Json $script:trackerStateFile
        if ($null -ne $d.session) { $script:trackerSession = [string]$d.session }
        if ($d.lake) { $script:trackerLake = [string]$d.lake }
        if ($d.temp) { $script:trackerTemp = [string]$d.temp }
        if ($d.pos) { $script:trackerPos = [pscustomobject]@{ X = [int]$d.pos.x; Y = [int]$d.pos.y } }
        if ($d.setup) { $script:trackerSetup = [pscustomobject]@{ Kind = "setup"; Baits = @($d.setup.baits | Where-Object { $_ }); Dip = [string]$d.setup.dip; Pva = [string]$d.setup.pva; Rig = [string]$d.setup.rig; Tech = [string]$d.setup.tech }
            if (-not $script:trackerSetup.Tech -and ($script:trackerSetup.Dip -or $script:trackerSetup.Pva -or @($script:trackerSetup.Baits).Count -ge 2)) { $script:trackerSetup.Tech = "bottom" }
        }
        return $true
    } catch { return $false }
}

function Update-TrackerUi {
    if ($script:trackerPending.Count -gt 0 -and $expCatchForm) { $expCatchForm.IsExpanded = $true }
    Save-TrackerPending
    Save-TrackerState
    if (-not (Test-Path -LiteralPath $script:trackerDir)) {
        $txtTrackerState.Text = (T "trackerNoFolder") + " " + $script:trackerDir
    } elseif ($chkTracker.IsChecked) {
        $txtTrackerState.Text = (T "trackerWatching") + " " + $script:trackerDir
    } else {
        $txtTrackerState.Text = T "trackerHint"
    }
    $txtTrackerSetup.Text = Format-TrackerSetup
    if ($script:trackerLake) { $txtTrackerSetup.Text = $txtTrackerSetup.Text + "   |   " + (T "lake") + ": " + (Get-LakeName $script:trackerLake) }
    if ($script:trackerPos) { $txtTrackerSetup.Text = $txtTrackerSetup.Text + "   |   " + (T "position") + ": " + $script:trackerPos.X + ":" + $script:trackerPos.Y }
    $items = @()
    $groups = [ordered]@{}
    foreach ($p in $script:trackerPending) {
        $key = "{0}|{1}|{2}|{3}|{4}|{5}|{6}" -f $p.Session, $p.Lake, $p.X, $p.Y, ((@($p.Baits)) -join "+"), $p.Dip, $p.Pva
        if (-not $groups.Contains($key)) { $groups[$key] = New-Object System.Collections.ArrayList }
        $groups[$key].Add($p) | Out-Null
    }
    foreach ($key in $groups.Keys) {
        $g = @($groups[$key])
        $p = $g[0]
        $times = @($g | ForEach-Object { $_.Time } | Sort-Object)
        $parts = @($times[-1].ToString("HH:mm"))
        if ($g.Count -eq 1) {
            $parts += (N $p.Fish)
            $wt = Format-Weight $p.Weight
            if (-not $wt) { $wt = "? kg" }
            $parts += $wt
            $mark = Get-TrophyMark $p.Fish $p.Weight
            if ($mark) { $parts += $mark }
        }
        $loc = ""
        if ($p.Lake) { $loc = Get-LakeName $p.Lake }
        if ($null -ne $p.X -and "$($p.X)" -ne "") { $loc = "{0} {1}:{2}" -f $loc, $p.X, $p.Y }
        if ($loc) { $parts += $loc.Trim() }
        if ($g.Count -gt 1) {
            $summary = (@($g | Group-Object Fish | Sort-Object Count -Descending | ForEach-Object { "{0}× {1}" -f $_.Count, (N $_.Name) }) -join ", ")
            $stars = @($g | Where-Object { Get-TrophyMark $_.Fish $_.Weight }).Count
            $noW = @($g | Where-Object { $null -eq $_.Weight -or "$($_.Weight)" -eq "" }).Count
            $txt = "{0} {1}: {2}" -f $g.Count, (T "fishCount"), $summary
            if ($stars -gt 0) { $txt = "{0}   ★ {1}" -f $txt, $stars }
            if ($noW -gt 0) { $txt = "{0}   ({1} {2})" -f $txt, $noW, (T "noWeight") }
            $parts += $txt
        }
        if (@($p.Baits).Count -gt 0) { $parts += ((@($p.Baits) | ForEach-Object { N $_ }) -join " + ") }
        $items += [pscustomobject]@{ Label = ($parts -join "  |  "); Ref = $g }
    }
    $lstTrackerPending.ItemsSource = $items
    if ($items.Count -gt 0) {
        $panelTrackerPending.Visibility = "Visible"
        if (-not $lstTrackerPending.SelectedItem) { $lstTrackerPending.SelectedIndex = 0 }
    } else {
        $panelTrackerPending.Visibility = "Collapsed"
    }
}

function Start-TrackerOcr([string]$path, [string]$langOverride = "", [string]$mode = "", [string]$crop = "") {
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    $lang = $script:trackerLang
    if (-not $lang) { $lang = "de-DE" }
    if ($langOverride) { $lang = $langOverride }
    [void]$ps.AddScript($script:ocrScript).AddArgument($path).AddArgument($lang).AddArgument($crop)
    $script:trackerJob = [pscustomobject]@{ PS = $ps; RS = $rs; Handle = $ps.BeginInvoke(); Path = $path; Started = Get-Date; Mode = $mode }
}

function Complete-TrackerOcr {
    $job = $script:trackerJob
    $script:trackerJob = $null
    $lines = @()
    $geo = New-Object System.Collections.ArrayList
    try {
        foreach ($raw in @($job.PS.EndInvoke($job.Handle) | ForEach-Object { [string]$_ })) {
            $pp = $raw.Split("`t", 5)
            if ($pp.Count -eq 5) {
                $geo.Add([pscustomobject]@{ X = [int]$pp[0]; Y = [int]$pp[1]; W = [int]$pp[2]; H = [int]$pp[3]; Text = $pp[4] }) | Out-Null
                $lines += $pp[4]
            } else {
                $lines += $raw
            }
        }
    } catch {
        Write-ErrorLog ("Tracker OCR: " + $_.Exception.Message)
    }
    $script:lastGeo = @($geo)
    $job.PS.Dispose()
    $job.RS.Dispose()
    if ($job.Mode -eq "lake") {
        foreach ($l in $lines) {
            if ($l.Length -lt 4 -or $l.Length -gt 40) { continue }
            $lid = Match-Lake $l "ru"
            if (-not $lid) { $lid = Match-Lake $l (Get-OcrLangCode $script:trackerLang) }
            if ($lid) {
                Set-TrackerLakeAuto $lid
                Set-Status ("{0}: {1}" -f (T "trackerLakeFound"), (Get-LakeName $lid))
                Update-TrackerUi
                break
            }
        }
        return
    }
    if ($job.Mode -eq "hud") {
        $tk = Match-Temp $lines
        if ($tk) {
            $script:trackerTemp = $tk
            foreach ($p in $script:trackerPending) { if ($p.Session -eq $script:trackerSession -and -not $p.Temp) { $p.Temp = $tk } }
            if (-not $script:selCatchId) { Set-ComboKey $cmbCatchTemp $tk }
            Set-Status ("{0}: {1}" -f (T "temperature"), (T ("temp_" + $tk)))
            Update-TrackerUi
        }
        $script:hudTempFound = [bool]$tk
        Start-TrackerOcr $job.Path "" "mini" "mini"
        return
    }
    if ($job.Mode -eq "mini" -or $job.Mode -eq "mapzoom") {
        $pos = Find-PosInLines $lines
        if ($pos) {
            Apply-TrackerPos $pos.X $pos.Y
            if ($job.Mode -eq "mapzoom" -and (Get-OcrLanguages) -contains "ru") {
                Update-TrackerUi
                Start-TrackerOcr $job.Path "ru" "lake"
                return
            }
        } elseif ($job.Mode -eq "mapzoom" -or -not $script:hudTempFound) {
            Set-Status (T "trackerUnknown")
        } else {
            [System.Media.SystemSounds]::Exclamation.Play()
        }
        Update-TrackerUi
        return
    }
    if ($job.Mode -eq "catch") {
        $first = $script:catchFirst
        $script:catchFirst = $null
        $w = $null
        foreach ($l in $lines) {
            $m = [regex]::Match($l, "(\d[\d\.,]*)\s*(kg|g|кг|г|lb|lbs)\b")
            if ($m.Success) { $w = Convert-OcrWeight $m.Groups[1].Value $m.Groups[2].Value; if ($null -ne $w) { break } }
        }
        if (-not $first -or $null -eq $w) { Set-Status (T "trackerUnknown"); return }
        $st = $script:trackerSetup
        $time = (Get-Item -LiteralPath $job.Path).LastWriteTime
        $p = [pscustomobject]@{
            Time = $time; Fish = $first; Weight = $w; Lake = $(if ($script:trackerLake) { $script:trackerLake } else { Guess-TrackerLake @($first) }); SpotId = ""; Session = $script:trackerSession; X = $(if ($script:trackerPos) { $script:trackerPos.X } else { $null }); Y = $(if ($script:trackerPos) { $script:trackerPos.Y } else { $null })
            Baits = $(if ($st) { @($st.Baits) } else { @() }); Dip = $(if ($st) { $st.Dip } else { "" }); Pva = $(if ($st) { $st.Pva } else { "" }); Rig = $(if ($st) { $st.Rig } else { "" })
            Temp = [string]$script:trackerTemp; Tech = $(if ($st) { [string]$st.Tech } else { "" }); Path = $job.Path
        }
        $script:trackerPending.Add($p) | Out-Null
        [System.Media.SystemSounds]::Asterisk.Play()
        Set-Status ("{0}: {1} {2}" -f (T "trackerCatch"), (N $p.Fish), (Format-Weight $p.Weight))
        Update-TrackerUi
        return
    }
    if ($job.Mode -eq "keepnet2") {
        $first = $script:keepnetFirst
        $script:keepnetFirst = $null
        if (-not $first) { return }
        $items2 = @(Parse-KeepnetItems $lines (Get-MatchLists (Get-OcrLangCode $script:trackerLang)))
        $merged = Merge-KeepnetItems $first.Items $items2
        Apply-KeepnetItems $merged $first.Total $first.Time $first.Path
        return
    }
    if ($lines.Count -eq 0) { [System.Media.SystemSounds]::Hand.Play(); return }
    $code = Get-OcrLangCode $script:trackerLang
    $r = Parse-TrackerLines $lines $code
    $st = $script:trackerSetup
    $time = (Get-Item -LiteralPath $job.Path).LastWriteTime
    if ($r.Kind -eq "detail") {
        $existing = $null
        foreach ($p in $script:trackerPending) { if ($p.Fish -eq $r.Fish -and $p.Weight -eq $r.Weight) { $existing = $p; break } }
        $lake = $r.Lake
        if ($script:trackerLakeManual) { $lake = $script:trackerLake }
        elseif ($lake) { Set-TrackerLakeAuto $lake }
        else { $lake = Guess-TrackerLake @($r.Fish) }
        if ($existing) {
            if (@($r.Baits).Count -gt 0) { $existing.Baits = @($r.Baits) }
            $existing.Lake = $lake
        } else {
            $dup = $false
            foreach ($c in $script:catches) {
                if ($c.fish -eq $r.Fish -and [int]$c.weight -eq $r.Weight -and $c.date -eq $time.ToString("yyyy-MM-dd")) {
                    $dup = $true
                    if (@($r.Baits).Count -gt 0 -and -not $c.bait) {
                        $c | Add-Member -NotePropertyName bait -NotePropertyValue $r.Baits[0] -Force
                        if (@($r.Baits).Count -gt 1) { $c | Add-Member -NotePropertyName bait2 -NotePropertyValue $r.Baits[1] -Force }
                        Save-User
                        Refresh-Catches
                    }
                }
            }
            if (-not $dup) {
                $script:trackerPending.Add([pscustomobject]@{
                    Time = $time; Fish = $r.Fish; Weight = $r.Weight; Lake = $lake; SpotId = ""; Session = $script:trackerSession; X = $(if ($script:trackerPos) { $script:trackerPos.X } else { $null }); Y = $(if ($script:trackerPos) { $script:trackerPos.Y } else { $null })
                    Baits = $(if (@($r.Baits).Count -gt 0) { @($r.Baits) } elseif ($st) { @($st.Baits) } else { @() }); Dip = $(if ($st) { $st.Dip } else { "" }); Pva = $(if ($st) { $st.Pva } else { "" }); Rig = $(if ($st) { $st.Rig } else { "" }); Temp = [string]$script:trackerTemp; Tech = $(if ($st) { [string]$st.Tech } else { "" }); Path = $job.Path
                }) | Out-Null
            }
        }
        [System.Media.SystemSounds]::Asterisk.Play()
        Set-Status ("{0}: {1} {2}" -f (T "trackerCatch"), (N $r.Fish), (Format-Weight $r.Weight))
        Update-TrackerUi
        return
    }
    if ($r.Kind -eq "keepnet") {
        $script:keepnetFirst = [pscustomobject]@{ Items = @($r.Items); Total = $r.Total; Time = $time; Path = $job.Path }
        Start-TrackerOcr $job.Path "" "keepnet2" "grid"
        return
    }
    if ($r.Kind -eq "keepnetOld") {
        $added = 0
        $today = $time.ToString("yyyy-MM-dd")
        $guessLake = Guess-TrackerLake @($r.Items | ForEach-Object { $_.Fish })
        foreach ($it in $r.Items) {
            $dup = $false
            foreach ($p in $script:trackerPending) { if ($p.Fish -eq $it.Fish -and $p.Weight -eq $it.Weight) { $dup = $true; break } }
            if (-not $dup) {
                foreach ($c in $script:catches) { if ($c.fish -eq $it.Fish -and [int]$c.weight -eq $it.Weight -and $c.date -eq $today) { $dup = $true; break } }
            }
            if ($dup) { continue }
            $script:trackerPending.Add([pscustomobject]@{
                Time = $time; Fish = $it.Fish; Weight = $it.Weight; Lake = $guessLake; SpotId = ""; Session = $script:trackerSession; X = $(if ($script:trackerPos) { $script:trackerPos.X } else { $null }); Y = $(if ($script:trackerPos) { $script:trackerPos.Y } else { $null })
                Baits = $(if ($st) { @($st.Baits) } else { @() }); Dip = $(if ($st) { $st.Dip } else { "" }); Pva = $(if ($st) { $st.Pva } else { "" }); Rig = $(if ($st) { $st.Rig } else { "" })
                Temp = [string]$script:trackerTemp; Tech = $(if ($st) { [string]$st.Tech } else { "" }); Path = $job.Path
            }) | Out-Null
            $added++
        }
        [System.Media.SystemSounds]::Asterisk.Play()
        Set-Status ("{0}: {1}" -f (T "trackerKeepnet"), $added)
        Update-TrackerUi
        return
    }
    if ($r.Kind -eq "catch") {
        $guessLake = Guess-TrackerLake @($r.Fish)
        $p = [pscustomobject]@{
            Time = $time; Fish = $r.Fish; Weight = $r.Weight; Lake = $guessLake; SpotId = ""; Session = $script:trackerSession; X = $(if ($script:trackerPos) { $script:trackerPos.X } else { $null }); Y = $(if ($script:trackerPos) { $script:trackerPos.Y } else { $null })
            Baits = $(if ($st) { @($st.Baits) } else { @() }); Dip = $(if ($st) { $st.Dip } else { "" }); Pva = $(if ($st) { $st.Pva } else { "" }); Rig = $(if ($st) { $st.Rig } else { "" })
            Temp = [string]$script:trackerTemp; Tech = $(if ($st) { [string]$st.Tech } else { "" }); Path = $job.Path
        }
        $script:trackerPending.Add($p) | Out-Null
        [System.Media.SystemSounds]::Asterisk.Play()
        Set-Status ("{0}: {1} {2}" -f (T "trackerCatch"), (N $p.Fish), (Format-Weight $p.Weight))
    } elseif ($r.Kind -eq "recipe") {
        $rc = $r.Recipe
        $sig = "{0}|{1}|{2}|{3}" -f $rc.Type, $rc.Base, ((@($rc.Additives)) -join "+"), $rc.Attractant
        $same = $null
        $nameTaken = $false
        foreach ($x in $script:recipes) {
            if ($x.name -eq $rc.Name -or $x.name -like ($rc.Name + " (*")) {
                if ($x.name -eq $rc.Name) { $nameTaken = $true }
                $xs = "{0}|{1}|{2}|{3}" -f $x.type, $x.base, ((@($x.additives)) -join "+"), $x.attractant
                if ($xs -eq $sig) { $same = $x; break }
            }
        }
        if ($same) {
            Set-Status ("{0}: {1}" -f (T "trackerRecipeExists"), $same.name)
            Update-TrackerUi
            return
        }
        $newName = $rc.Name
        if ($nameTaken) { $newName = "{0} ({1})" -f $rc.Name, $time.ToString("dd.MM. HH:mm") }
        $ex = [pscustomobject]@{ id = (New-Id); name = $newName; type = $rc.Type; fish = @(); lakes = @(); base = $rc.Base; additives = @($rc.Additives); attractant = $rc.Attractant; notes = "Tracker" }
        if ($script:trackerLake) { $ex.lakes = @($script:lakeById[$script:trackerLake].name.en) }
        $script:recipes.Add($ex) | Out-Null
        $rc.Name = $newName
        Save-User
        Refresh-Recipes
        Refresh-SpotRecipeChoices
        $pv = "$($cmbCatchPva.Text)"
        Set-Choices $cmbCatchPva @($script:recipes | ForEach-Object { New-Choice $_.id $_.name } | Sort-Object Label)
        $cmbCatchPva.Text = $pv
        [System.Media.SystemSounds]::Exclamation.Play()
        Set-Status ("{0}: {1}" -f (T "trackerRecipe"), $rc.Name)
    } elseif ($r.Kind -eq "position") {
        Apply-TrackerPos $r.X $r.Y
        if ((Get-OcrLanguages) -contains "ru") {
            Update-TrackerUi
            Start-TrackerOcr $job.Path "ru" "lake"
            return
        }
    } elseif ($r.Kind -eq "setup") {
        $script:trackerSetup = $r
        [System.Media.SystemSounds]::Exclamation.Play()
        Set-Status (T "trackerSetupRead")
    } elseif ($job.Mode -eq "") {
        $allText = $lines -join " "
        $isHud = $allText -match "Bedienfeld|Hilfe|Chat|Rute aufnehmen|Schnur straffen|Details|Menu|Меню"
        if (-not $isHud -and $allText -match "Freilassen|Release|Отпустить|Setzkescher|Keepnet|Садок|Leertaste|Space") {
            $f = $null
            $lists = Get-MatchLists $code
            foreach ($l in ($lines | Select-Object -First 3)) { if (-not $f) { $f = Match-Fish $l $lists } }
            if ($f) {
                $script:catchFirst = $f.En
                Start-TrackerOcr $job.Path "" "catch" "catch"
                return
            }
        }
        if (-not $isHud -and $lines.Count -lt 25) {
            $cand = @($script:lastGeo | Where-Object { $_.Text.Trim() -match "^\d{1,3}(\s*[:;.]\s*\d{1,3})?$" -and $_.H -lt 30 } | Select-Object -First 1)
            if ($cand.Count -gt 0) {
                $c = $cand[0]
                Start-TrackerOcr $job.Path "" "mapzoom" ("rect:{0},{1},{2},{3}" -f [int]($c.X-$c.H*3.3), [int]($c.Y-$c.H), [int]($c.W + $c.H*4.2), [int]($c.H*2.3))
                return
            }
        }
        Start-TrackerOcr $job.Path "" "hud" "hud"
        return
    } else {
        Set-Status (T "trackerUnknown")
    }
    Update-TrackerUi
}

$script:trackerTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:trackerTimer.Interval = [TimeSpan]::FromMilliseconds(1500)
$script:trackerTimer.Add_Tick({
    if ($script:trackerJob) {
        if ($script:trackerJob.Handle.IsCompleted) { Complete-TrackerOcr }
        elseif (((Get-Date)-$script:trackerJob.Started).TotalSeconds -gt 60) {
            try { $script:trackerJob.PS.Stop() } catch { }
            $script:trackerJob.PS.Dispose()
            $script:trackerJob.RS.Dispose()
            $script:trackerJob = $null
        }
        return
    }
    if (-not $chkTracker.IsChecked -or -not (Test-Path -LiteralPath $script:trackerDir)) { return }
    $cutoff = (Get-Date).AddSeconds(-1)
    $next = Get-ChildItem -LiteralPath $script:trackerDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match "^\.(png|jpg|jpeg|bmp)$" -and $_.LastWriteTime -gt $script:trackerSince -and $_.LastWriteTime -lt $cutoff -and -not $script:trackerSeen.ContainsKey($_.FullName) } |
        Sort-Object LastWriteTime | Select-Object -First 1
    if ($next) {
        $script:trackerSeen[$next.FullName] = $true
        Save-TrackerSeen
        Start-TrackerOcr $next.FullName
    }
})

$script:editSpotId = $null

function Hide-SpotEdit {
    $script:editSpotId = $null
    $svSpotEdit.Visibility = "Collapsed"
    $txtSeHint.Visibility = "Visible"
}

function Show-SpotEdit([string]$id) {
    $sp = Get-Spot $id
    if (-not $sp) { Hide-SpotEdit; return }
    $script:editSpotId = $id
    $lake = $script:lakeById[$sp.lake]
    $txtSeTitle.Text = "{0}   {1}" -f (Get-LakeName $sp.lake), (Format-Coords $lake $sp.nx $sp.ny)
    $txtSeName.Text = "$($sp.name)"
    if ($lake -and $lake.bounds) {
        $g = To-Game $lake ([double]$sp.nx) ([double]$sp.ny)
        $txtSeX.Text = [string][int][math]::Round($g.X)
        $txtSeY.Text = [string][int][math]::Round($g.Y)
    }
    Set-Choices $cmbSeTech (Get-TechChoices)
    Set-ComboKey $cmbSeTech "$($sp.tech)"
    Set-Choices $cmbSeFish (Get-FishChoices $sp.lake)
    Set-ComboKey $cmbSeFish "$($sp.fish)"
    $bc = Get-BaitChoices
    foreach ($pair in @(@($cmbSeBait, "$($sp.bait)"), @($cmbSeBait2, "$($sp.bait2)"), @($cmbSeDip, "$($sp.dip)"))) {
        Set-Choices $pair[0] $bc
        Set-ComboKey $pair[0] $pair[1]
    }
    Set-Choices $cmbSeGround (Get-RecipeChoices "groundbait")
    Set-RecipeField $cmbSeGround "$($sp.groundbait)"
    Set-Choices $cmbSePva (Get-RecipeChoices "pva")
    Set-RecipeField $cmbSePva "$($sp.pva)"
    $txtSeDist.Text = "$($sp.dist)"
    $txtSeDepth.Text = "$($sp.depth)"
    Set-Choices $cmbSeTemp (Get-TempChoices)
    Set-ComboKey $cmbSeTemp "$($sp.temp)"
    $txtSeDir.Text = "$($sp.dir)"
    $chkSeShare.IsChecked = [bool]$sp.share
    $txtSeNotes.Text = "$($sp.notes)"
    $txtSeHint.Visibility = "Collapsed"
    $svSpotEdit.Visibility = "Visible"
}

function Apply-Language {
    $script:busy = $true
    $window.Title = T "appTitle"
    $txtAppTitle.Text = T "appTitle"
    Localize-Tree $window
    foreach ($ck in $script:colKeys) { $ck.Col.Header = (T $ck.Key) }
    $txtPrefHint.Text = T "prefHintShort"
    $txtPrefHint.ToolTip = T "prefHint"

    $k = Get-ComboKey $cmbMapLake
    if (-not $k -and $script:curMapLake) { $k = $script:curMapLake.id }
    Set-Choices $cmbMapLake (Get-LakeChoices -MapsOnly)
    Set-ComboKey $cmbMapLake $k

    $f = Get-ComboKey $cmbSpotFish
    $lakeId = $null
    if ($script:curMapLake) { $lakeId = $script:curMapLake.id }
    Set-Choices $cmbSpotFish (Get-FishChoices $lakeId)
    Set-ComboKey $cmbSpotFish $f
    $b = Get-ComboKey $cmbSpotBait
    Set-Choices $cmbSpotBait (Get-BaitChoices)
    Set-ComboKey $cmbSpotBait $b
    foreach ($cb in @($cmbSpotBait2, $cmbSpotDip)) {
        $b = Get-ComboKey $cb
        Set-Choices $cb (Get-BaitChoices)
        Set-ComboKey $cb $b
    }
    Refresh-SpotRecipeChoices
    $t = Get-ComboKey $cmbSpotTech
    Set-Choices $cmbSpotTech (Get-TechChoices)
    Set-ComboKey $cmbSpotTech $t
    foreach ($cb in @($cmbSpotTemp, $cmbCatchTemp, $cmbSeTemp)) {
        $tk = Get-ComboKey $cb
        Set-Choices $cb (Get-TempChoices)
        Set-ComboKey $cb $tk
    }
    $tk = Get-ComboKey $cmbCatchTech
    Set-Choices $cmbCatchTech (Get-TechChoices)
    Set-ComboKey $cmbCatchTech $tk
    foreach ($pair in @(@($cmbCatchTech, $txtCatchNotes), @($cmbSpotTech, $txtSpotNotes), @($cmbSeTech, $txtSeNotes))) { Update-NotesHint $pair[0] $pair[1] }

    $k = Get-ComboKey $cmbSpotsSource
    if (-not $k) { $k = "mine" }
    Set-Choices $cmbSpotsSource (@((New-Choice "mine" (T "tabSpots")), (New-Choice "commAll" ((T "communitySpots") + ": " + (T "allTime")))) + @(1..7 | ForEach-Object { New-Choice ("commD" + $_) ((T "communitySpots") + ": " + (Get-DaysLabel $_)) }))
    Set-ComboKey $cmbSpotsSource $k

    $k = Get-ComboKey $cmbCommPeriod
    if (-not $k) { $k = "d7" }
    Set-Choices $cmbCommPeriod (@(New-Choice "all" (T "allTime")) + @(Get-DaysChoices "d"))
    Set-ComboKey $cmbCommPeriod $k
    $k = Get-ComboKey $cmbWeekDays
    if (-not $k) { $k = "d7" }
    Set-Choices $cmbWeekDays (Get-DaysChoices "d")
    Set-ComboKey $cmbWeekDays $k
    $k = Get-ComboKey $cmbPrefLake
    if (-not $k) { $k = "copper_lake" }
    Set-Choices $cmbPrefLake (Get-LakeChoices -WithAll)
    Set-ComboKey $cmbPrefLake $k
    $k = Get-ComboKey $cmbPrefTable
    Set-Choices $cmbPrefTable @((New-Choice "" (T "prefTableAll")), (New-Choice "n" (T "table_n")), (New-Choice "l" (T "table_l")), (New-Choice "b" (T "table_b")))
    Set-ComboKey $cmbPrefTable $k
    $k = Get-ComboKey $cmbPrefWindow
    if (-not $k) { $k = "h24" }
    Set-Choices $cmbPrefWindow (@(6, 12, 24, 48) | ForEach-Object { New-Choice ("h" + $_) ((T "prefWinHours") -f $_) })
    $cmbPrefWindow.ItemsSource = @(@($cmbPrefWindow.ItemsSource) + (New-Choice "all" (T "prefWinAll")))
    Set-ComboKey $cmbPrefWindow $k
    $k = Get-ComboKey $cmbTgtFish
    if (-not $k) { $k = "grp:koi" }
    Set-Choices $cmbTgtFish (@(New-Choice "grp:koi" (T "tgtKoiGroup")) + @(Get-FishChoices $null))
    Set-ComboKey $cmbTgtFish $k
    $k = Get-ComboKey $cmbTgtLake
    Set-Choices $cmbTgtLake (@(New-Choice "" (T "tgtAutoLake")) + @(Get-LakeChoices -MapsOnly))
    Set-ComboKey $cmbTgtLake $k
    $txtTgtHint.Text = T "tgtHint"
    $k = Get-ComboKey $cmbPrefWeek
    if (-not $k) { $k = "cur" }
    Set-Choices $cmbPrefWeek @((New-Choice "cur" (T "weekCurrent")), (New-Choice "prev" (T "weekPrev")), (New-Choice "all" (T "weekAll")))
    Set-ComboKey $cmbPrefWeek $k
    Refresh-CommFishChoices

    $k = Get-ComboKey $cmbSpotsLake
    Set-Choices $cmbSpotsLake (Get-LakeChoices -WithAll)
    Set-ComboKey $cmbSpotsLake $k

    $k = Get-ComboKey $cmbCatchLake
    Set-Choices $cmbCatchLake (Get-LakeChoices)
    if (-not $k) { $k = "mosquito_lake" }
    Set-ComboKey $cmbCatchLake $k
    $f = Get-ComboKey $cmbCatchFish
    Set-Choices $cmbCatchFish (Get-FishChoices $k)
    Set-ComboKey $cmbCatchFish $f
    foreach ($cb in @($cmbCatchBait, $cmbCatchBait2, $cmbCatchDip)) {
        $b = Get-ComboKey $cb
        Set-Choices $cb (Get-BaitChoices)
        Set-ComboKey $cb $b
    }
    $pv = "$($cmbCatchPva.Text)"
    Set-Choices $cmbCatchPva @($script:recipes | ForEach-Object { New-Choice $_.id $_.name } | Sort-Object Label)
    $cmbCatchPva.Text = $pv
    Refresh-CatchSpotChoices

    $k = Get-ComboKey $cmbWeekLake
    Set-Choices $cmbWeekLake (Get-LakeChoices -WithAll)
    Set-ComboKey $cmbWeekLake $k

    $k = Get-ComboKey $cmbWebSite
    if (-not $k) { $k = "rf4it" }
    Set-Choices $cmbWebSite (Get-WebSites)
    Set-ComboKey $cmbWebSite $k

    $k = Get-ComboKey $cmbWebLake
    if (-not $k) { $k = "mosquito_lake" }
    Set-Choices $cmbWebLake (Get-LakeChoices -MapsOnly)
    Set-ComboKey $cmbWebLake $k
    Refresh-WebFishChoices
    $b = Get-ComboKey $cmbWebBait
    Set-Choices $cmbWebBait (Get-BaitChoices)
    Set-ComboKey $cmbWebBait $b

    $k = Get-ComboKey $cmbRecType
    Set-Choices $cmbRecType @((New-Choice "groundbait" (T "groundbait")), (New-Choice "pva" (T "pva")))
    Set-ComboKey $cmbRecType $k
    $script:busy = $false

    Draw-Markers
    Refresh-LakeSpotList
    Refresh-SpotsGrid
    Refresh-Catches
    Refresh-Lakes
    Refresh-Trophies
    Refresh-Recipes
    Refresh-Weekly
    Update-BottomPanel
    Update-RecipeLabels
    Update-TrackerUi
    Update-CommState
    if ($script:commSel) { Show-CommCluster $script:commSel }
    Set-Status ""
}

function Refresh-AfterDataChange {
    Draw-Markers
    Refresh-LakeSpotList
    Refresh-SpotsGrid
    Refresh-CatchSpotChoices
    Refresh-Catches
    Show-Lake
    Refresh-Trophies
}

function Confirm-Delete {
    $r = [System.Windows.MessageBox]::Show((T "confirmDelete"), (T "appTitle"), [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Question)
    return ($r -eq [System.Windows.MessageBoxResult]::Yes)
}

$langItems = @()
foreach ($p in $langs.PSObject.Properties) { $langItems += New-Choice $p.Name $p.Value.langName }
Set-Choices $cmbLang $langItems
Set-ComboKey $cmbLang $script:lang

$cmbLang.Add_SelectionChanged({
    if ($script:busy -or -not $cmbLang.SelectedItem) { return }
    $script:lang = $cmbLang.SelectedItem.Key
    Apply-Language
    Save-User
})

$cmbMapLake.Add_SelectionChanged({
    if ($script:busy -or -not $cmbMapLake.SelectedItem) { return }
    Load-MapLake $cmbMapLake.SelectedItem.Key
})

$svMap.Add_PreviewMouseWheel({
    param($sender, $e)
    if (-not $script:curMapLake) { return }
    $old = $script:scale
    $new = $old * 1.2
    if ($e.Delta -lt 0) { $new = $old / 1.2 }
    if ($new -lt $script:minScale) { $new = $script:minScale }
    if ($new -gt 3) { $new = 3 }
    $p = $e.GetPosition($gridMap)
    $v = $e.GetPosition($svMap)
    Set-MapScale $new
    $svMap.UpdateLayout()
    $svMap.ScrollToHorizontalOffset(($p.X * $new)-$v.X)
    $svMap.ScrollToVerticalOffset(($p.Y * $new)-$v.Y)
    $e.Handled = $true
})

$svMap.Add_PreviewMouseRightButtonDown({
    param($sender, $e)
    $script:panning = $true
    $script:panStart = $e.GetPosition($svMap)
    $script:panH = $svMap.HorizontalOffset
    $script:panV = $svMap.VerticalOffset
    $svMap.CaptureMouse() | Out-Null
    $e.Handled = $true
})

$svMap.Add_PreviewMouseRightButtonUp({
    param($sender, $e)
    $script:panning = $false
    $svMap.ReleaseMouseCapture()
    $e.Handled = $true
})

$svMap.Add_PreviewMouseMove({
    param($sender, $e)
    if ($script:panning) {
        $cur = $e.GetPosition($svMap)
        $svMap.ScrollToHorizontalOffset($script:panH-($cur.X-$script:panStart.X))
        $svMap.ScrollToVerticalOffset($script:panV-($cur.Y-$script:panStart.Y))
        return
    }
    if (-not $script:curMapLake) { return }
    $p = $e.GetPosition($gridMap)
    if ($btnRuler.IsChecked -and $script:rulerA -and -not $script:rulerB) {
        $script:rulerLive = [pscustomobject]@{ NX = $p.X / 2048; NY = $p.Y / 2048 }
        Draw-Ruler
    }
    if ($p.X -ge 0 -and $p.Y -ge 0 -and $p.X -le 2048 -and $p.Y -le 2048) {
        $txtCursor.Text = "{0}  {1}" -f (T "cursor"), (Format-Coords $script:curMapLake ($p.X / 2048) ($p.Y / 2048))
    }
})

$canvasMarkers.Add_MouseLeftButtonDown({
    param($sender, $e)
    if (-not $script:curMapLake) { return }
    if ($btnRuler.IsChecked) {
        $p = $e.GetPosition($canvasMarkers)
        $pt = [pscustomobject]@{ NX = $p.X / 2048; NY = $p.Y / 2048 }
        if (-not $script:rulerA -or $script:rulerB) {
            $script:rulerA = $pt
            $script:rulerB = $null
            $script:rulerLive = $pt
        } else {
            $script:rulerB = $pt
        }
        Draw-Ruler
        $e.Handled = $true
        return
    }
    $src = $e.OriginalSource
    if ($src -is [System.Windows.Shapes.Rectangle] -and "$($src.Tag)".StartsWith("c:")) {
        $cl = $script:commDrawn["$($src.Tag)"]
        if ($cl) { Show-CommCluster $cl }
        $e.Handled = $true
        return
    }
    if ($script:commSel) { Hide-CommCluster }
    if ($src -is [System.Windows.Shapes.Ellipse] -and $src.Tag) {
        Select-Spot ([string]$src.Tag)
        $e.Handled = $true
        return
    }
    $p = $e.GetPosition($canvasMarkers)
    $keepFish = Get-ComboKey $cmbSpotFish
    $keepBait = Get-ComboKey $cmbSpotBait
    $keepTech = Get-ComboKey $cmbSpotTech
    $wasNew = -not $script:selSpotId
    if (-not $wasNew) { Clear-SpotForm }
    $script:selSpotId = $null
    $script:pending = [pscustomobject]@{ NX = $p.X / 2048; NY = $p.Y / 2048 }
    if ($wasNew) {
        Set-ComboKey $cmbSpotFish $keepFish
        Set-ComboKey $cmbSpotBait $keepBait
        Set-ComboKey $cmbSpotTech $keepTech
    }
    $g = To-Game $script:curMapLake $script:pending.NX $script:pending.NY
    $txtSpotX.Text = [string][int][math]::Round($g.X)
    $txtSpotY.Text = [string][int][math]::Round($g.Y)
    $lstLakeSpots.SelectedItem = $null
    $btnSpotDelete.IsEnabled = $false
    Update-MapPanel
    Draw-Markers
    $window.Dispatcher.BeginInvoke([action]{ $txtSpotName.Focus() | Out-Null; [System.Windows.Input.Keyboard]::Focus($txtSpotName) | Out-Null }, [System.Windows.Threading.DispatcherPriority]::Input) | Out-Null
    $e.Handled = $true
})

$btnSpotSave.Add_Click({
    $lake = $script:curMapLake
    if (-not $lake) { return }
    $x = Parse-Num $txtSpotX.Text
    $y = Parse-Num $txtSpotY.Text
    if ($null -eq $x -or $null -eq $y) {
        [System.Windows.MessageBox]::Show((T "invalidCoords"), (T "appTitle")) | Out-Null
        return
    }
    if (-not (Test-SpotFields $txtSpotName.Text.Trim() $x $y (Get-ComboKey $cmbSpotTech) (Get-ComboKey $cmbSpotFish) (Get-ComboKey $cmbSpotBait) $txtSpotDist.Text.Trim() $txtSpotDepth.Text.Trim() (Get-ComboKey $cmbSpotTemp) $txtSpotNotes.Text.Trim())) { return }
    $n = From-Game $lake $x $y
    if ($script:pending) {
        $g = To-Game $lake $script:pending.NX $script:pending.NY
        if ([int][math]::Round($g.X) -eq [int][math]::Round($x) -and [int][math]::Round($g.Y) -eq [int][math]::Round($y)) {
            $n = [pscustomobject]@{ NX = $script:pending.NX; NY = $script:pending.NY }
        }
    }
    $s = $null
    if ($script:selSpotId) { $s = Get-Spot $script:selSpotId }
    if ($s) {
        $old = To-Game $lake ([double]$s.nx) ([double]$s.ny)
        if ([int][math]::Round($old.X) -eq [int][math]::Round($x) -and [int][math]::Round($old.Y) -eq [int][math]::Round($y)) {
            $n = [pscustomobject]@{ NX = [double]$s.nx; NY = [double]$s.ny }
        }
    } else {
        $s = [pscustomobject]@{ id = (New-Id); lake = $lake.id; name = ""; fish = ""; bait = ""; tech = ""; depth = ""; dist = ""; notes = ""; nx = 0.0; ny = 0.0 }
        $script:spots.Add($s) | Out-Null
    }
    $s.name = $txtSpotName.Text.Trim()
    $s.fish = Get-ComboKey $cmbSpotFish
    $s.bait = Get-ComboKey $cmbSpotBait
    $s.tech = Get-ComboKey $cmbSpotTech
    $s | Add-Member -NotePropertyName temp -NotePropertyValue (Get-ComboKey $cmbSpotTemp) -Force
    $s | Add-Member -NotePropertyName dir -NotePropertyValue $txtSpotDir.Text.Trim() -Force
    $s | Add-Member -NotePropertyName share -NotePropertyValue ([bool]$chkSpotShare.IsChecked) -Force
    $s.depth = $txtSpotDepth.Text.Trim()
    $s.dist = $txtSpotDist.Text.Trim()
    $s.notes = $txtSpotNotes.Text.Trim()
    foreach ($pair in @(@("bait2", (Get-ComboKey $cmbSpotBait2)), @("dip", (Get-ComboKey $cmbSpotDip)), @("groundbait", (Get-RecipeFieldValue $cmbSpotGround)), @("pva", (Get-RecipeFieldValue $cmbSpotPva)))) {
        $v = [string]$pair[1]
        if ($s.tech -ne "bottom") { $v = "" }
        $s | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $v -Force
    }
    $s.nx = [double]$n.NX
    $s.ny = [double]$n.NY
    $script:selSpotId = $s.id
    $script:pending = $null
    Save-User
    Refresh-AfterDataChange
    $btnSpotDelete.IsEnabled = $true
    Set-Status ("{0}: {1}" -f (T "saved"), (Get-SpotLabel $s))
})

$script:spotFormOpen = $false
$btnMapNewSpot.Add_Click({ Clear-SpotForm; $script:spotFormOpen = $true; Update-MapPanel; $txtSpotName.Focus() | Out-Null })
$btnSpotClose.Add_Click({ Clear-SpotForm; Draw-Markers; Refresh-LakeSpotList })
$tabsPlan.Add_SelectionChanged({
    param($sender, $e)
    if ($e.OriginalSource -ne $tabsPlan) { return }
    if ($tabsPlan.SelectedItem -and $tabsPlan.SelectedItem.Tag -eq "t:tabTarget") { Refresh-Target }
})
$btnSpotNew.Add_Click({
    Clear-SpotForm
    $lstLakeSpots.SelectedItem = $null
    Draw-Markers
})

$btnSpotDelete.Add_Click({
    $s = Get-Spot $script:selSpotId
    if (-not $s) { return }
    if (-not (Confirm-Delete)) { return }
    $script:spots.Remove($s)
    Save-User
    Clear-SpotForm
    Refresh-AfterDataChange
})

$lstLakeSpots.Add_SelectionChanged({
    if ($script:busy -or -not $lstLakeSpots.SelectedItem) { return }
    Select-Spot $lstLakeSpots.SelectedItem.Key -Center
})

$txtSpotsSearch.Add_TextChanged({ Refresh-SpotsGrid })
$cmbSpotsLake.Add_SelectionChanged({ if (-not $script:busy) { Refresh-SpotsGrid } })

$showSpotOnMap = {
    $row = $dgSpots.SelectedItem
    if (-not $row) { return }
    $script:jumpId = $row.Id
    $tabs.SelectedIndex = 0
    if ("$($row.Id)".StartsWith("c|")) {
        $window.Dispatcher.BeginInvoke([action]{ Show-CommOnMap $script:jumpId }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
        return
    }
    $window.Dispatcher.BeginInvoke([action]{ Select-Spot $script:jumpId -Center }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
}
$btnSpotsShow.Add_Click($showSpotOnMap)
$dgSpots.Add_MouseDoubleClick($showSpotOnMap)

$dgSpots.Add_SelectionChanged({
    $row = $dgSpots.SelectedItem
    if (-not $row -or "$($row.Id)".StartsWith("c|")) { Hide-SpotEdit; return }
    Show-SpotEdit ([string]$row.Id)
})

$btnSeSave.Add_Click({
    $sp = Get-Spot $script:editSpotId
    if (-not $sp) { return }
    $lake = $script:lakeById[$sp.lake]
    $x = Parse-Num $txtSeX.Text
    $y = Parse-Num $txtSeY.Text
    if (-not (Test-SpotFields $txtSeName.Text.Trim() $x $y (Get-ComboKey $cmbSeTech) (Get-ComboKey $cmbSeFish) (Get-ComboKey $cmbSeBait) $txtSeDist.Text.Trim() $txtSeDepth.Text.Trim() (Get-ComboKey $cmbSeTemp) $txtSeNotes.Text.Trim())) { return }
    if ($lake -and $lake.bounds) {
        if ($null -eq $x -or $null -eq $y) { [System.Windows.MessageBox]::Show((T "invalidCoords"), (T "appTitle")) | Out-Null; return }
        $n = From-Game $lake $x $y
        $sp.nx = [double]$n.NX
        $sp.ny = [double]$n.NY
    }
    $vals = [ordered]@{
        name = $txtSeName.Text.Trim(); tech = (Get-ComboKey $cmbSeTech); fish = (Get-ComboKey $cmbSeFish)
        bait = (Get-ComboKey $cmbSeBait); bait2 = (Get-ComboKey $cmbSeBait2); dip = (Get-ComboKey $cmbSeDip)
        groundbait = (Get-RecipeFieldValue $cmbSeGround); pva = (Get-RecipeFieldValue $cmbSePva)
        dist = $txtSeDist.Text.Trim(); depth = $txtSeDepth.Text.Trim(); notes = $txtSeNotes.Text.Trim(); temp = (Get-ComboKey $cmbSeTemp); dir = $txtSeDir.Text.Trim(); share = [bool]$chkSeShare.IsChecked
    }
    foreach ($k in $vals.Keys) { $sp | Add-Member -NotePropertyName $k -NotePropertyValue $vals[$k] -Force }
    Save-User
    $keep = $sp.id
    Refresh-AfterDataChange
    $sel = @($dgSpots.ItemsSource) | Where-Object { $_.Id -eq $keep } | Select-Object -First 1
    if ($sel) { $dgSpots.SelectedItem = $sel }
    Show-SpotEdit $keep
    Set-Status ("{0}: {1}" -f (T "saved"), (Get-SpotLabel $sp))
})

$btnSeDelete.Add_Click({
    $sp = Get-Spot $script:editSpotId
    if (-not $sp -or -not (Confirm-Delete)) { return }
    $script:spots.Remove($sp)
    if ($script:selSpotId -eq $sp.id) { Clear-SpotForm }
    Save-User
    Hide-SpotEdit
    Refresh-AfterDataChange
})

$btnSpotsDelete.Add_Click({
    $row = $dgSpots.SelectedItem
    if (-not $row -or "$($row.Id)".StartsWith("c|")) { return }
    $s = Get-Spot $row.Id
    if (-not $s -or -not (Confirm-Delete)) { return }
    $script:spots.Remove($s)
    if ($script:selSpotId -eq $s.id) { Clear-SpotForm }
    Save-User
    Refresh-AfterDataChange
})

$cmbCatchLake.Add_SelectionChanged({
    if ($script:busy) { return }
    $k = Get-ComboKey $cmbCatchLake
    if ($k) {
        $script:trackerLake = $k
        $script:trackerLakeManual = $true
        $refs = @()
        if ($script:formGroup) { $refs = @($script:formGroup) } elseif ($script:formPending) { $refs = @($script:formPending) }
        if ($refs.Count -gt 0) {
            foreach ($pp in $refs) { $pp.Lake = $k }
            Save-TrackerPending
        }
    }
    $f = Get-ComboKey $cmbCatchFish
    Set-Choices $cmbCatchFish (Get-FishChoices $k)
    Set-ComboKey $cmbCatchFish $f
    Refresh-CatchSpotChoices
})

$script:selCatchId = $null

$script:formPending = $null

function Read-CatchForm {
    $fish = Get-ComboKey $cmbCatchFish
    if (-not $fish) { [System.Windows.MessageBox]::Show((T "invalidFish"), (T "appTitle")) | Out-Null; return $null }
    $w = $null
    if ("$($txtCatchWeight.Text)".Trim()) {
        $w = Parse-Weight $txtCatchWeight.Text
        if ($null -eq $w -or $w -le 0) { [System.Windows.MessageBox]::Show((T "invalidWeight"), (T "appTitle")) | Out-Null; return $null }
    }
    $d = $dpCatchDate.SelectedDate
    if (-not $d) { $d = Get-Date }
    $x = Parse-Num $txtCatchX.Text
    $y = Parse-Num $txtCatchY.Text
    $pva = ""
    if ($cmbCatchPva.SelectedItem) { $pva = [string]$cmbCatchPva.SelectedItem.Label } else { $pva = "$($cmbCatchPva.Text)".Trim() }
    $f = [pscustomobject]@{
        date = $d.ToString("yyyy-MM-dd"); lake = (Get-ComboKey $cmbCatchLake); fish = $fish; weight = $w
        bait = (Get-ComboKey $cmbCatchBait); bait2 = (Get-ComboKey $cmbCatchBait2); dip = (Get-ComboKey $cmbCatchDip); pva = $pva
        x = $(if ($null -ne $x) { [int][math]::Round($x) } else { $null }); y = $(if ($null -ne $y) { [int][math]::Round($y) } else { $null })
        spotName = "$($txtCatchSpotName.Text)".Trim(); clip = "$($txtCatchClip.Text)".Trim(); depth = "$($txtCatchDepth.Text)".Trim()
        spotId = ""; notes = $txtCatchNotes.Text.Trim(); temp = (Get-ComboKey $cmbCatchTemp); tech = (Get-ComboKey $cmbCatchTech); dir = $txtCatchDir.Text.Trim()
    }
    $f
}

function Fill-CatchForm($o) {
    $expCatchForm.IsExpanded = $true
    $script:busy = $true
    try { $dpCatchDate.SelectedDate = [datetime]$o.Date } catch { $dpCatchDate.SelectedDate = Get-Date }
    Set-ComboKey $cmbCatchLake "$($o.Lake)"
    Set-Choices $cmbCatchFish (Get-FishChoices "$($o.Lake)")
    $script:busy = $false
    Set-ComboKey $cmbCatchFish "$($o.Fish)"
    $txtCatchWeight.Text = [string]$o.Weight
    $b = @($o.Baits)
    Set-ComboKey $cmbCatchBait $(if ($b.Count -ge 1) { [string]$b[0] } else { "" })
    Set-ComboKey $cmbCatchBait2 $(if ($b.Count -ge 2) { [string]$b[1] } else { "" })
    Set-ComboKey $cmbCatchDip "$($o.Dip)"
    $cmbCatchPva.SelectedItem = $null
    $cmbCatchPva.Text = "$($o.Pva)"
    $txtCatchX.Text = $(if ($null -ne $o.X -and "$($o.X)" -ne "") { [string]$o.X } else { "" })
    $txtCatchY.Text = $(if ($null -ne $o.Y -and "$($o.Y)" -ne "") { [string]$o.Y } else { "" })
    $txtCatchSpotName.Text = "$($o.SpotName)"
    $txtCatchClip.Text = "$($o.Clip)"
    $txtCatchDepth.Text = "$($o.Depth)"
    $txtCatchDir.Text = "$($o.Dir)"
    $txtCatchNotes.Text = "$($o.Notes)"
    $tk = "$($o.Temp)"
    if (-not $tk -and -not $o.Keep) { $tk = $script:trackerTemp }
    Set-ComboKey $cmbCatchTemp "$tk"
    $tc = "$($o.Tech)"
    if (-not $tc -and -not $o.Keep -and $script:trackerSetup) { $tc = [string]$script:trackerSetup.Tech }
    Set-ComboKey $cmbCatchTech "$tc"
    Update-NotesHint $cmbCatchTech $txtCatchNotes
    Update-CatchSpotInfo
}

function Set-CatchEditMode([bool]$on) {
    if ($on) { $btnCatchSave.Visibility = "Visible"; $btnCatchNew.Visibility = "Visible" }
    else { $btnCatchSave.Visibility = "Collapsed"; $btnCatchNew.Visibility = "Collapsed"; $script:selCatchId = $null }
}

$dgCatches.Add_SelectionChanged({
    if ($script:busy) { return }
    $row = $dgCatches.SelectedItem
    if (-not $row -or "$($row.Id)".StartsWith("g|")) { return }
    $script:formGroup = $null
    Set-GroupFormMode $false 0
    $c = $null
    foreach ($x in $script:catches) { if ($x.id -eq $row.Id) { $c = $x } }
    if (-not $c) { return }
    $dt = Get-Date
    try { $dt = [datetime]::ParseExact($c.date, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture) } catch { }
    $sp = $null
    if ($c.spotId) { $sp = Get-Spot $c.spotId }
    $cx = $c.x
    $cy = $c.y
    if ($sp -and ($null -eq $cx -or "$cx" -eq "")) {
        $lk = $script:lakeById[$sp.lake]
        if ($lk -and $lk.bounds) { $g = To-Game $lk ([double]$sp.nx) ([double]$sp.ny); $cx = [int][math]::Round($g.X); $cy = [int][math]::Round($g.Y) }
    }
    Fill-CatchForm ([pscustomobject]@{
        Date = $dt; Lake = $c.lake; Fish = $c.fish; Weight = $c.weight; Baits = @($c.bait, $c.bait2); Dip = $c.dip; Pva = $c.pva
        X = $cx; Y = $cy; SpotName = $(if ($sp) { $sp.name } else { "" }); Clip = $c.clip; Depth = $c.depth; Notes = $c.notes; Temp = $c.temp; Tech = $(if ($c.tech) { $c.tech } elseif ($sp) { $sp.tech } else { "" }); Dir = "$($c.dir)"; Keep = $true
    })
    $script:formPending = $null
    $script:selCatchId = $c.id
    Set-CatchEditMode $true
})

$btnCatchSave.Add_Click({
    if (-not $script:selCatchId) { return }
    $f = Read-CatchForm
    if (-not $f) { return }
    $c = $null
    foreach ($x in $script:catches) { if ($x.id -eq $script:selCatchId) { $c = $x } }
    if (-not $c) { return }
    $f.spotId = Resolve-CatchSpot $f
    foreach ($p in $f.PSObject.Properties) { if ($p.Name -ne "spotName") { $c | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force } }
    Refresh-AfterDataChange
    Save-User
    $script:busy = $true
    Refresh-Catches
    $script:busy = $false
    Show-Lake
    Refresh-Trophies
    Set-Status ("{0}: {1} {2}" -f (T "saved"), (N $c.fish), (Format-Weight $c.weight))
})

$btnCatchNew.Add_Click({
    $script:busy = $true
    $dgCatches.SelectedItem = $null
    $script:busy = $false
    $txtCatchWeight.Text = ""
    $txtCatchNotes.Text = ""
    $dpCatchDate.SelectedDate = Get-Date
    $script:formPending = $null
    Set-CatchEditMode $false
    $txtCatchWeight.Focus() | Out-Null
})

$btnCatchAdd.Add_Click({
    $f = Read-CatchForm
    if (-not $f) { return }
    $miss = @(Get-MissingCatchFields $f (Get-SetupNeeds $script:formPending))
    if ($miss.Count -gt 0) { Show-MissingFields $miss; return }
    $f.spotId = Resolve-CatchSpot $f
    $c = [pscustomobject]@{
        id = (New-Id); date = $f.date; lake = $f.lake; fish = $f.fish; weight = $f.weight
        bait = $f.bait; bait2 = $f.bait2; dip = $f.dip; pva = $f.pva; x = $f.x; y = $f.y; clip = $f.clip; depth = $f.depth
        spotId = $f.spotId; notes = $f.notes; temp = $f.temp; tech = $f.tech; dir = $f.dir
    }
    $fish = $f.fish
    $w = $f.weight
    $script:catches.Add($c) | Out-Null
    if ($script:formPending) { $script:trackerPending.Remove($script:formPending); $script:formPending = $null; Update-TrackerUi }
    Set-CatchEditMode $false
    Refresh-AfterDataChange
    Save-User
    $txtCatchWeight.Text = ""
    $txtCatchNotes.Text = ""
    Refresh-Catches
    Show-Lake
    Refresh-Trophies
    $mark = Get-TrophyMark $fish $w
    $msg = "{0}: {1} {2}" -f (T "saved"), (N $fish), (Format-Weight $w)
    if ($mark) { $msg = "$msg   $mark" }
    Set-Status $msg
    $txtCatchWeight.Focus() | Out-Null
})

$txtCatchWeight.Add_KeyDown({
    param($sender, $e)
    if ($e.Key -eq [System.Windows.Input.Key]::Enter) { $btnCatchAdd.RaiseEvent((New-Object System.Windows.RoutedEventArgs ([System.Windows.Controls.Button]::ClickEvent))) }
})

$btnCatchDelete.Add_Click({
    $ids = @{}
    foreach ($row in @($dgCatches.SelectedItems)) {
        if ($row.Ids) { foreach ($i in @($row.Ids)) { $ids[[string]$i] = $true } } else { $ids[[string]$row.Id] = $true }
    }
    $del = @($script:catches | Where-Object { $ids.ContainsKey([string]$_.id) })
    if ($del.Count -eq 0) { return }
    $q = T "confirmDelete"
    if ($del.Count -gt 1) { $q = "{0} ({1} {2})" -f $q, $del.Count, (T "fishCount") }
    $r = [System.Windows.MessageBox]::Show($q, (T "appTitle"), [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Question)
    if ($r -ne [System.Windows.MessageBoxResult]::Yes) { return }
    foreach ($c in $del) { $script:catches.Remove($c) | Out-Null }
    Set-CatchEditMode $false
    Save-User
    Refresh-Catches
    Show-Lake
    Refresh-Trophies
})

$txtCatchSearch.Add_TextChanged({ Refresh-Catches })
$chkCatchGroup.Add_Click({ Refresh-Catches })
$txtCatchX.Add_TextChanged({ Update-CatchSpotInfo })
$txtCatchY.Add_TextChanged({ Update-CatchSpotInfo })
$txtCatchClip.Add_TextChanged({ Update-CatchSpotInfo })
$txtCatchDir.Add_TextChanged({ Update-CatchSpotInfo })

$lstLakes.Add_SelectionChanged({ Show-Lake })

$btnLakeMap.Add_Click({
    $it = $lstLakes.SelectedItem
    if (-not $it) { return }
    $script:jumpLake = $it.Key
    $tabs.SelectedIndex = 0
    $window.Dispatcher.BeginInvoke([action]{
        $script:busy = $true
        Set-ComboKey $cmbMapLake $script:jumpLake
        $script:busy = $false
        Load-MapLake $script:jumpLake
    }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
})

$txtTroSearch.Add_TextChanged({ Refresh-Trophies })
$chkTroOnlyMine.Add_Click({ Refresh-Trophies })

$txtRecSearch.Add_TextChanged({ Refresh-Recipes })

$lstRecipes.Add_SelectionChanged({
    if ($script:busy) { return }
    if ($lstRecipes.SelectedItem) { $script:selRecId = $lstRecipes.SelectedItem.Key } else { return }
    Show-Recipe
})

$btnRecNew.Add_Click({
    $script:busy = $true
    $lstRecipes.SelectedItem = $null
    $script:busy = $false
    Clear-RecipeForm
    $txtRecName.Focus() | Out-Null
})

$btnRecSave.Add_Click({
    $name = $txtRecName.Text.Trim()
    if (-not $name) { return }
    $r = Get-Recipe $script:selRecId
    if (-not $r) {
        $r = [pscustomobject]@{ id = (New-Id); name = ""; type = "groundbait"; fish = @(); lakes = @(); base = ""; additives = @(); attractant = ""; notes = "" }
        $script:recipes.Add($r) | Out-Null
    }
    $r.name = $name
    $r.type = Get-ComboKey $cmbRecType
    if (-not $r.type) { $r.type = "groundbait" }
    $r.fish = Split-Names $txtRecFish.Text
    $r.lakes = Split-Lakes $txtRecLakes.Text
    $r.base = Resolve-ItemAny $txtRecBase.Text
    $r.additives = @(@($txtRecAdd1, $txtRecAdd2, $txtRecAdd3, $txtRecAdd4) | ForEach-Object { "$($_.Text)".Trim() } | Where-Object { $_ } | ForEach-Object { Resolve-ItemAny $_ })
    $r.attractant = Resolve-ItemAny $txtRecAttr.Text
    $r.notes = $txtRecNotes.Text.Trim()
    $script:selRecId = $r.id
    Save-User
    Refresh-Recipes
    Refresh-SpotRecipeChoices
    Set-Status ("{0}: {1}" -f (T "saved"), $name)
})

$btnRecDelete.Add_Click({
    $r = Get-Recipe $script:selRecId
    if (-not $r -or -not (Confirm-Delete)) { return }
    $script:recipes.Remove($r)
    Save-User
    Clear-RecipeForm
    Refresh-Recipes
    Refresh-SpotRecipeChoices
})

$btnRuler.Add_Click({
    Clear-Ruler
    if ($btnRuler.IsChecked) { Set-Status (T "rulerHint") } else { Set-Status "" }
})

$window.Add_PreviewKeyDown({
    param($sender, $e)
    if ($e.Key -eq [System.Windows.Input.Key]::Escape -and $script:rulerA) {
        Clear-Ruler
        $e.Handled = $true
    }
})

Set-Choices $cmbWeekRegion @($script:weekRegions | ForEach-Object { New-Choice $_ $_ })
Set-ComboKey $cmbWeekRegion $script:weekRegion
Load-WeeklyCache

$cmbWeekRegion.Add_SelectionChanged({
    if ($script:busy -or -not $cmbWeekRegion.SelectedItem) { return }
    if ($script:weekLoading) { return }
    $script:weekRegion = $cmbWeekRegion.SelectedItem.Key
    Save-User
    $script:weeklyStale = $true
    Refresh-Weekly
})

$btnWeekLoad.Add_Click({
    if ($script:weekFromCloud) { $txtWeekState.Text = T "loading"; $script:cloudLast = $null; Start-CloudSync } else { Start-WeeklyLoad }
})
function Get-GridItemAt($grid, $src) {
    $c = $null
    try { $c = [System.Windows.Controls.ItemsControl]::ContainerFromElement($grid, $src) } catch { }
    if ($c -is [System.Windows.Controls.DataGridRow]) { return $c.Item }
    return $null
}

function Invoke-FishMapFor($grid, $item) {
    if (-not $item) { Set-Status (T "pickFishFirst"); return }
    switch ($grid.Name) {
        "dgWeek" { Show-FishAnywhere $item.FishKey $item.LakeId }
        "dgWeekBaits" { Show-FishAnywhere $item.FishKey $item.LakeId }
        "dgPrefs" { Show-FishAnywhere ([string]$script:prefFishByLabel[[string]$item.Row[0]]) (Get-ComboKey $cmbPrefLake) }
        "dgPrefBaits" { Show-FishAnywhere $script:prefSelFish (Get-ComboKey $cmbPrefLake) }
        "dgLakeFish" { $it = $lstLakes.SelectedItem; Show-FishAnywhere $item.FishKey $(if ($it) { $it.Key } else { "" }) }
        "dgTrophies" { Show-FishAnywhere $item.FishKey "" }
    }
}

foreach ($g in @($dgWeek, $dgWeekBaits, $dgPrefs, $dgPrefBaits, $dgLakeFish, $dgTrophies)) {
    $g.Add_PreviewMouseLeftButtonDown({
        param($sender, $e)
        if ($e.ClickCount -ne 2) { return }
        $item = Get-GridItemAt $sender $e.OriginalSource
        if (-not $item) { return }
        $e.Handled = $true
        Invoke-FishMapFor $sender $item
    })
    $cm = New-Object System.Windows.Controls.ContextMenu
    $mi = New-Object System.Windows.Controls.MenuItem
    $mi.Header = T "fishOnMap"
    $mi.Tag = $g
    $mi.Add_Click({ param($sender, $e) $gr = $sender.Tag; Invoke-FishMapFor $gr $gr.SelectedItem })
    [void]$cm.Items.Add($mi)
    $g.ContextMenu = $cm
    $g.Add_PreviewMouseRightButtonDown({
        param($sender, $e)
        $item = Get-GridItemAt $sender $e.OriginalSource
        if ($item) { $sender.SelectedItem = $item }
    })
}

$txtCommState.ToolTip = T "commStateTip"
$cmComm = New-Object System.Windows.Controls.ContextMenu
foreach ($pair in @(@("syncDb", $btnCommSync), @("syncFull", $btnCommFull))) {
    $mi = New-Object System.Windows.Controls.MenuItem
    $mi.Header = T $pair[0]
    $mi.Tag = $pair[1]
    $mi.Add_Click({ param($sender, $e) $sender.Tag.RaiseEvent((New-Object System.Windows.RoutedEventArgs ([System.Windows.Controls.Button]::ClickEvent))) })
    [void]$cmComm.Items.Add($mi)
}
$txtCommState.ContextMenu = $cmComm
$txtCommState.Add_MouseLeftButtonUp({ $txtCommState.ContextMenu.PlacementTarget = $txtCommState; $txtCommState.ContextMenu.IsOpen = $true })
$cmbWeekLake.Add_SelectionChanged({ if (-not $script:busy) { Refresh-Weekly } })
$cmbWeekDays.Add_SelectionChanged({ if (-not $script:busy) { Refresh-Weekly } })
foreach ($cb in @($cmbPrefLake, $cmbPrefTable, $cmbPrefWindow, $cmbPrefWeek)) { $cb.Add_SelectionChanged({ if (-not $script:busy) { Refresh-Prefs } }) }
$btnPrefHarvest.Add_Click({ Start-Harvest })
foreach ($cb in @($cmbTgtFish, $cmbTgtLake)) { $cb.Add_SelectionChanged({ if (-not $script:busy) { Refresh-Target } }) }

function Get-TgtSourceUrlAt([System.Windows.Point]$pt) {
    $n = $dgTgtSpots.InputHitTest($pt)
    while ($n -and -not ($n -is [System.Windows.Controls.DataGridCell])) {
        if ($n -is [System.Windows.Media.Visual]) { $n = [System.Windows.Media.VisualTreeHelper]::GetParent($n) } else { $n = [System.Windows.LogicalTreeHelper]::GetParent($n) }
    }
    if ($n -and $n.Column -eq $dgTgtSpots.Columns[$dgTgtSpots.Columns.Count-1] -and $n.DataContext -and $n.DataContext.Url) { return [string]$n.DataContext.Url }
    return ""
}

$dgTgtSpots.AddHandler([System.Windows.UIElement]::MouseLeftButtonUpEvent, [System.Windows.Input.MouseButtonEventHandler]{
    param($sender, $e)
    $u = Get-TgtSourceUrlAt ($e.GetPosition($dgTgtSpots))
    if ($u) { $e.Handled = $true; Open-ReportUrl $u }
}, $true)

$dgTgtSpots.Add_PreviewMouseLeftButtonDown({
    param($sender, $e)
    if ($e.ClickCount -ne 2) { return }
    $item = Get-GridItemAt $sender $e.OriginalSource
    if (-not $item -or -not $script:tgtLake) { return }
    $e.Handled = $true
    $f = ""
    if (@($script:tgtSet).Count -eq 1) { $f = $script:tgtSet[0] }
    Show-SpotOnMapAt $script:tgtLake $item.X $item.Y $f
})
$script:prefSelFish = ""

Load-CloudState
$dgPrefs.Add_SelectionChanged({
    $rv = $dgPrefs.SelectedItem
    if (-not $rv) { return }
    $lbl = [string]$rv.Row[0]
    $f = $script:prefFishByLabel[$lbl]
    if ($f) { Show-PrefFishBaits $f }
})
$dgPrefs.Add_AutoGeneratingColumn({
    param($sender, $e)
    $h = $script:prefHeaders[[string]$e.PropertyName]
    if ($h) { $e.Column.Header = $h }
    if ([string]$e.PropertyName -like "a_*") {
        $st = New-Object System.Windows.Style ([System.Windows.Controls.DataGridCell])
        try { $st.BasedOn = $dgPrefs.FindResource([System.Windows.Controls.DataGridCell]) } catch { }
        $bd = New-Object System.Windows.Data.Binding ([string]$e.PropertyName)
        $bd.Converter = $script:heatConv
        $st.Setters.Add((New-Object System.Windows.Setter ([System.Windows.Controls.Control]::BackgroundProperty), $bd))
        $st.Setters.Add((New-Object System.Windows.Setter ([System.Windows.Controls.Control]::HorizontalContentAlignmentProperty), ([System.Windows.HorizontalAlignment]::Center)))
        $e.Column.CellStyle = $st
    }
    if ($e.PropertyType -eq [int]) { $e.Column.Width = [System.Windows.Controls.DataGridLength]::Auto }
})
Load-ArchState
$script:weeklyStale = $true
$script:harvCheckTimer.Start()
$txtWeekSearch.Add_TextChanged({ Refresh-Weekly })

$tabs.Add_SelectionChanged({
    param($sender, $e)
    if ($e.OriginalSource -ne $tabs) { return }
    $it = $tabs.SelectedItem
    if ($it -and $it.Tag -ne "t:tabMap" -and -not $script:harvCur) { Set-Status "" }
    if ($it -and $it.Tag -eq "t:tabPlan" -and $tabsPlan.SelectedItem -and $tabsPlan.SelectedItem.Tag -eq "t:tabTarget") { Refresh-Target }
    if ($it -and $it.Tag -eq "t:tabCommunity" -and -not $script:wvWeb -and -not $script:jumpUrl) {
        Open-SelectedSite
    }
})

$cmbWebLake.Add_SelectionChanged({
    if ($script:busy) { return }
    Refresh-WebFishChoices
    if ($script:wvWeb -and (Get-ComboKey $cmbWebSite) -eq "rf4it") { Open-SelectedSite }
})
$cmbWebSite.Add_SelectionChanged({ if (-not $script:busy -and $script:wvWeb) { Open-SelectedSite } })
$btnWebOpen.Add_Click({ Open-SelectedSite })
$btnWebBack.Add_Click({
    if ($script:wvWeb -and $script:wvWeb.CoreWebView2 -and $script:wvWeb.CoreWebView2.CanGoBack) { $script:wvWeb.CoreWebView2.GoBack() }
})
$btnWebExternal.Add_Click({
    $url = Get-Rf4itUrl (Get-ComboKey $cmbWebLake)
    if ($script:wvWeb -and $script:wvWeb.Source) { $url = $script:wvWeb.Source.AbsoluteUri }
    Open-External $url
})

$btnWebSave.Add_Click({
    $lake = $script:lakeById[(Get-ComboKey $cmbWebLake)]
    $x = Parse-Num $txtWebX.Text
    $y = Parse-Num $txtWebY.Text
    if (-not $lake -or -not $lake.bounds -or $null -eq $x -or $null -eq $y) {
        [System.Windows.MessageBox]::Show((T "invalidCoords"), (T "appTitle")) | Out-Null
        return
    }
    $n = From-Game $lake $x $y
    $s = [pscustomobject]@{
        id = (New-Id); lake = $lake.id; name = ""; fish = (Get-ComboKey $cmbWebFish); bait = (Get-ComboKey $cmbWebBait)
        tech = ""; depth = ""; dist = ""; notes = (T "fromWeb"); nx = [double]$n.NX; ny = [double]$n.NY
    }
    $script:spots.Add($s) | Out-Null
    Save-User
    Refresh-AfterDataChange
    $txtWebX.Text = ""
    $txtWebY.Text = ""
    Set-Status ("{0}: {1}  {2}" -f (T "saved"), (Get-SpotLabel $s), (Format-Coords $lake $s.nx $s.ny))
})

Load-Community

$chkCommunity.Add_Click({ if (-not $chkCommunity.IsChecked -and $script:commSel) { Hide-CommCluster }; Draw-Markers })
$cmbCommPeriod.Add_SelectionChanged({ if (-not $script:busy) { if ($script:commSel) { Hide-CommCluster }; Draw-Markers } })
$cmbCommFish.Add_SelectionChanged({ if (-not $script:busy) { if ($script:commSel) { Hide-CommCluster }; Draw-Markers } })

$ocrLangs = @(Get-OcrLanguages)
if (-not $script:trackerLang -or $ocrLangs -notcontains $script:trackerLang) {
    $pref = @($ocrLangs | Where-Object { (Get-OcrLangCode $_) -eq $script:lang } | Select-Object -First 1)
    if ($pref.Count -gt 0) { $script:trackerLang = $pref[0] } elseif ($ocrLangs.Count -gt 0) { $script:trackerLang = $ocrLangs[0] }
}
$trackerLangItems = @()
foreach ($tag in $ocrLangs) {
    $code = Get-OcrLangCode $tag
    $label = $tag
    if ($langs.$code) { $label = $langs.$code.langName }
    $trackerLangItems += New-Choice $tag $label
}
Set-Choices $cmbTrackerLang $trackerLangItems
Set-ComboKey $cmbTrackerLang $script:trackerLang
$cmbTrackerLang.Add_SelectionChanged({
    if ($cmbTrackerLang.SelectedItem) { $script:trackerLang = $cmbTrackerLang.SelectedItem.Key; Save-User }
})

$chkTracker.IsChecked = (Test-Path -LiteralPath $script:trackerDir)
Load-TrackerSeen
Load-TrackerPending
$lastP = @($script:trackerPending | Sort-Object Time | Select-Object -Last 1)
if (Load-TrackerState) { $lastP = @() }
if (-not $script:trackerSetup) {
    $lc = @($script:catches | Where-Object { $_.bait } | Sort-Object { "$($_.date)" } | Select-Object -Last 1)
    if ($lc.Count -gt 0) { $script:trackerSetup = [pscustomobject]@{ Kind = "setup"; Baits = @(@($lc[0].bait, $lc[0].bait2) | Where-Object { $_ }); Dip = [string]$lc[0].dip; Pva = [string]$lc[0].pva; Rig = ""; Tech = $(if ($lc[0].dip -or $lc[0].pva -or $lc[0].bait2) { "bottom" } else { "" }) } }
}
if ($script:trackerSetup) {
    foreach ($p in $script:trackerPending) {
        if (@($p.Baits | Where-Object { $_ }).Count -eq 0 -and -not $p.Dip -and -not $p.Pva) {
            $p.Baits = @($script:trackerSetup.Baits)
            $p.Dip = $script:trackerSetup.Dip
            $p.Pva = $script:trackerSetup.Pva
        }
        if (-not $p.Tech) { $p.Tech = [string]$script:trackerSetup.Tech }
    }
}
if ($lastP.Count -gt 0) {
    $script:trackerSession = [string]$lastP[0].Session
    if ($null -ne $lastP[0].X -and "$($lastP[0].X)" -ne "") { $script:trackerPos = [pscustomobject]@{ X = [int]$lastP[0].X; Y = [int]$lastP[0].Y } }
    if ($lastP[0].Lake) { $script:trackerLake = $lastP[0].Lake }
}
$script:trackerSince = (Get-Date).Date
$chkTracker.Add_Click({
    if ($chkTracker.IsChecked) { $script:trackerSince = (Get-Date).Date }
    Update-TrackerUi
})
$script:trackerTimer.Start()

function Build-TrackerCatch($p, $shared, [bool]$override) {
    $b = @($p.Baits)
    $f = [pscustomobject]@{
        lake = $p.Lake; fish = $p.Fish; weight = $p.Weight; x = $p.X; y = $p.Y; spotName = ""; bait = $(if ($b.Count -ge 1) { $b[0] } else { "" })
        bait2 = $(if ($b.Count -ge 2) { $b[1] } else { "" }); dip = $p.Dip; pva = $p.Pva; clip = ""; depth = ""; notes = "Tracker"; temp = "$($p.Temp)"; tech = "$($p.Tech)"; dir = ""
    }
    if ($shared) {
        foreach ($prop in $shared.PSObject.Properties) {
            $cur = $f.($prop.Name)
            if ($override -or $null -eq $cur -or "$cur" -eq "") { $f | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value -Force }
        }
        $f.fish = $p.Fish
        $f.weight = $p.Weight
    }
    $f
}

function Accept-TrackerItems($refs, $shared = $null, [bool]$override = $true) {
    $miss = @()
    foreach ($p in @($refs)) { $miss += @(Get-MissingCatchFields (Build-TrackerCatch $p $shared $override) (Get-SetupNeeds $p)) }
    if ($miss.Count -gt 0) { Show-MissingFields $miss; return $false }
    foreach ($p in @($refs)) {
        $f = Build-TrackerCatch $p $shared $override
        $sid = Resolve-CatchSpot $f
        $c = [pscustomobject]@{
            id = (New-Id); date = $p.Time.ToString("yyyy-MM-dd"); lake = $f.lake; fish = $p.Fish; weight = $p.Weight
            bait = $f.bait; bait2 = $f.bait2
            dip = $f.dip; pva = $f.pva; rig = $p.Rig; x = $f.x; y = $f.y; clip = $f.clip; depth = $f.depth; spotId = $sid; notes = $f.notes; temp = $f.temp; tech = $f.tech; dir = $f.dir
        }
        $script:catches.Add($c) | Out-Null
        $script:trackerPending.Remove($p)
    }
    return $true
}

$btnTrackerNewSpot.Add_Click({
    Start-TrackerSession
    if (-not $script:selCatchId -and -not $script:formPending -and -not $script:formGroup) {
        $txtCatchSpotName.Text = ""
        $txtCatchClip.Text = ""
        $txtCatchDir.Text = ""
    }
    Update-TrackerUi
    Set-Status (T "newSpotStarted")
})

$btnTrackerRefresh.Add_Click({
    $today = (Get-Date).Date
    foreach ($k in @($script:trackerSeen.Keys)) {
        $keep = $false
        try { if ((Get-Item -LiteralPath $k -ErrorAction Stop).LastWriteTime -lt $today) { $keep = $true } } catch { }
        if (-not $keep) { $script:trackerSeen.Remove($k) }
    }
    Save-TrackerSeen
    $script:trackerSince = $today
    $chkTracker.IsChecked = $true
    Refresh-Catches
    Refresh-Recipes
    Refresh-SpotRecipeChoices
    Update-TrackerUi
    $cnt = @(Get-ChildItem -LiteralPath $script:trackerDir -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $today }).Count
    Set-Status ("{0}: {1}" -f (T "trackerRescan"), $cnt)
})

$btnTrackerAcceptAll.Add_Click({
    if ($script:trackerPending.Count -eq 0) { return }
    $n = $script:trackerPending.Count
    if (-not (Accept-TrackerItems @($script:trackerPending.ToArray()) (Read-SharedForm) $false)) { return }
    Save-User
    Refresh-Catches
    Show-Lake
    Refresh-Trophies
    Update-TrackerUi
    Set-Status ("{0}: {1}" -f (T "saved"), $n)
})

$script:formGroup = $null

function Set-GroupFormMode([bool]$on, [int]$count) {
    $cmbCatchFish.IsEnabled = -not $on
    $txtCatchWeight.IsEnabled = -not $on
    if ($on) {
        $cmbCatchFish.SelectedItem = $null
        $cmbCatchFish.Text = "{0} {1}" -f $count, (T "fishCount")
        $txtCatchWeight.Text = ""
    }
}

$lstTrackerPending.Add_SelectionChanged({
    $sel = @($lstTrackerPending.SelectedItems)
    if ($sel.Count -ne 1) { return }
    $g = @($sel[0].Ref)
    $p = $g[0]
    $script:busy = $true
    $dgCatches.SelectedItem = $null
    $script:busy = $false
    $script:selCatchId = $null
    Set-CatchEditMode $false
    Set-GroupFormMode $false 0
    Fill-CatchForm ([pscustomobject]@{
        Date = $p.Time; Lake = $p.Lake; Fish = $p.Fish; Weight = $p.Weight; Baits = @($p.Baits); Dip = $p.Dip; Pva = $p.Pva
        X = $p.X; Y = $p.Y; SpotName = ""; Clip = $txtCatchClip.Text; Depth = $txtCatchDepth.Text; Notes = "Tracker"; Temp = $p.Temp; Tech = $p.Tech; Dir = $txtCatchDir.Text
    })
    if ($g.Count -gt 1) {
        $script:formPending = $null
        $script:formGroup = $g
        Set-GroupFormMode $true $g.Count
    } else {
        $script:formGroup = $null
        $script:formPending = $p
    }
})

function Read-SharedForm {
    $x = Parse-Num $txtCatchX.Text
    $y = Parse-Num $txtCatchY.Text
    $pva = ""
    if ($cmbCatchPva.SelectedItem) { $pva = [string]$cmbCatchPva.SelectedItem.Label } else { $pva = "$($cmbCatchPva.Text)".Trim() }
    [pscustomobject]@{
        lake = (Get-ComboKey $cmbCatchLake); bait = (Get-ComboKey $cmbCatchBait); bait2 = (Get-ComboKey $cmbCatchBait2); dip = (Get-ComboKey $cmbCatchDip); pva = $pva
        x = $(if ($null -ne $x) { [int][math]::Round($x) } else { $null }); y = $(if ($null -ne $y) { [int][math]::Round($y) } else { $null })
        spotName = "$($txtCatchSpotName.Text)".Trim(); clip = "$($txtCatchClip.Text)".Trim(); depth = "$($txtCatchDepth.Text)".Trim(); notes = $txtCatchNotes.Text.Trim()
        temp = (Get-ComboKey $cmbCatchTemp); tech = (Get-ComboKey $cmbCatchTech); dir = $txtCatchDir.Text.Trim()
    }
}

$btnTrackerAccept.Add_Click({
    $sel = @($lstTrackerPending.SelectedItems | ForEach-Object { $_.Ref })
    if ($sel.Count -eq 0) { return }
    if ($sel.Count -eq 1 -and $script:formPending -eq $sel[0]) {
        $btnCatchAdd.RaiseEvent((New-Object System.Windows.RoutedEventArgs ([System.Windows.Controls.Button]::ClickEvent)))
        return
    }
    $override = [bool]($script:formGroup -and @($lstTrackerPending.SelectedItems).Count -eq 1)
    $n = $sel.Count
    if (-not (Accept-TrackerItems $sel (Read-SharedForm) $override)) { return }
    $script:formGroup = $null
    Set-GroupFormMode $false 0
    Refresh-AfterDataChange
    Save-User
    Refresh-Catches
    Show-Lake
    Refresh-Trophies
    Update-TrackerUi
    Set-Status ("{0}: {1} {2}" -f (T "saved"), $n, (T "fishCount"))
})

$btnTrackerDiscard.Add_Click({
    $sel = @($lstTrackerPending.SelectedItems | ForEach-Object { $_.Ref })
    foreach ($r in $sel) { $script:trackerPending.Remove($r) }
    Update-TrackerUi
})

$btnCommSync.Add_Click({ Start-CommunitySync })
$cmbSpotTech.Add_SelectionChanged({ if (-not $script:busy) { Update-BottomPanel }; Update-NotesHint $cmbSpotTech $txtSpotNotes })
$cmbCatchTech.Add_SelectionChanged({ Update-NotesHint $cmbCatchTech $txtCatchNotes })
$cmbSeTech.Add_SelectionChanged({ Update-NotesHint $cmbSeTech $txtSeNotes })
$btnSpotGroundRecipe.Add_Click({ Open-RecipeFromSpot $cmbSpotGround "groundbait" })
$btnSpotPvaRecipe.Add_Click({ Open-RecipeFromSpot $cmbSpotPva "pva" })
$txtMapSearch.Add_KeyDown({
    param($sender, $e)
    if ($e.Key -eq [System.Windows.Input.Key]::Enter) { Invoke-MapSearch; $e.Handled = $true }
})
$txtMapSearch.Add_TextChanged({
    if (-not "$($txtMapSearch.Text)".Trim() -and $script:searchMark) { $script:searchMark = $null; Draw-Markers }
})
$btnCommFull.Add_Click({ Start-CommunitySync -Full })
$btnCommClose.Add_Click({ Hide-CommCluster; Draw-Markers })
$lstCommReports.Add_PreviewMouseWheel({
    param($sender, $e)
    if ($e.Handled) { return }
    $e.Handled = $true
    $a = New-Object System.Windows.Input.MouseWheelEventArgs($e.MouseDevice, $e.Timestamp, $e.Delta)
    $a.RoutedEvent = [System.Windows.UIElement]::MouseWheelEvent
    $a.Source = $sender
    $sender.Parent.RaiseEvent($a)
})

$lstCommReports.Add_MouseDoubleClick({
    $it = $lstCommReports.SelectedItem
    if (-not $it -or -not $it.Url) { return }
    Open-ReportUrl $it.Url
})
$cmbSpotsSource.Add_SelectionChanged({ if (-not $script:busy) { Refresh-SpotsGrid } })

$btnCommSource.Add_Click({
    $cl = $script:commSel
    if (-not $cl) { return }
    $it = $lstCommReports.SelectedItem
    if ($it -and $it.Url) { Open-ReportUrl $it.Url; return }
    $best = $cl.Reports | Where-Object { $_["url"] } | Sort-Object { [string]$_["posted"] } -Descending | Select-Object -First 1
    if ($best) { Open-ReportUrl ([string]$best["url"]) }
})

$btnCommTake.Add_Click({
    $cl = $script:commSel
    if (-not $cl -or -not $script:curMapLake) { return }
    $sum = Get-ClusterSummary $cl
    Hide-CommCluster
    Clear-SpotForm
    $n = From-Game $script:curMapLake $cl.X $cl.Y
    $script:pending = [pscustomobject]@{ NX = [double]$n.NX; NY = [double]$n.NY }
    $txtSpotX.Text = [string]$cl.X
    $txtSpotY.Text = [string]$cl.Y
    if (@($sum.Fish).Count -gt 0) { Set-ComboKey $cmbSpotFish $sum.Fish[0].Key }
    if (@($sum.Bait).Count -gt 0) { Set-ComboKey $cmbSpotBait $sum.Bait[0].Key }
    if (@($sum.Methods).Count -gt 0) {
        $t = $script:techByMethod[$sum.Methods[0].Key]
        if ($t) { Set-ComboKey $cmbSpotTech $t }
    }
    if (@($sum.Clips).Count -gt 0) { $txtSpotDist.Text = ("{0} m" -f $sum.Clips[0].Key) }
    if (@($sum.BaitDetail).Count -gt 0) {
        $parts = @(([string]$sum.BaitDetail[0].Key) -split "\s\+\s" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($parts.Count -ge 1) { Set-ComboKey $cmbSpotBait (Resolve-ItemName $parts[0]) }
        if ($parts.Count -ge 2) { Set-ComboKey $cmbSpotBait2 (Resolve-ItemName $parts[1]) }
    }
    if (@($sum.Dip).Count -gt 0) { Set-ComboKey $cmbSpotDip (Resolve-ItemName ([string]$sum.Dip[0].Key)) }
    if (@($sum.Groundbait).Count -gt 0) { Set-RecipeField $cmbSpotGround (Format-Detail ([string]$sum.Groundbait[0].Key)) }
    $pvName = @($sum.Pva | Where-Object { $_.Key -ne "PVA Stick/Stringer" } | Select-Object -First 1)
    if ($pvName.Count -gt 0) { Set-RecipeField $cmbSpotPva (Format-Detail ([string]$pvName[0].Key)) }
    Update-BottomPanel
    if (@($sum.Depths).Count -gt 0) { $txtSpotDepth.Text = ("{0} m" -f $sum.Depths[0].Key) }
    $txtSpotNotes.Text = "rf4intel: {0} {1}, {2}" -f $cl.Reports.Count, (T "reports"), (Format-TopCounts $sum.Fish 4)
    if (@($sum.PvaMix).Count -gt 0) { $txtSpotNotes.Text = $txtSpotNotes.Text + [Environment]::NewLine + (T "pvaContent") + ": " + [string]$sum.PvaMix[0].Key }
    Draw-Markers
    $txtSpotName.Focus() | Out-Null
})

$dpCatchDate.SelectedDate = Get-Date
Clear-RecipeForm
Apply-Language
if (-not $recipesSeeded) { Save-User }

$window.Left = ([System.Windows.SystemParameters]::PrimaryScreenWidth-$window.Width) / 2
$window.Top = ([System.Windows.SystemParameters]::PrimaryScreenHeight-$window.Height) / 2
if ($window.Top -lt 0) { $window.Top = 0 }

$window.Add_ContentRendered({
    $k = Get-ComboKey $cmbMapLake
    if (-not $k) { $k = "mosquito_lake" }
    $script:busy = $true
    Set-ComboKey $cmbMapLake $k
    $script:busy = $false
    Load-MapLake $k
})

$window.Dispatcher.Add_UnhandledException({
    param($sender, $e)
    Write-ErrorLog ($e.Exception.GetBaseException().ToString())
    $e.Handled = $true
    try {
        $script:commSyncing = $false
        $btnCommSync.IsEnabled = $true
        $btnCommFull.IsEnabled = $true
        $btnWeekLoad.IsEnabled = $true
        Set-Status ((T "errorHint") + " " + $script:errorLog)
    } catch { }
})

if ($script:winPos) {
    try {
        $vl = [System.Windows.SystemParameters]::VirtualScreenLeft
        $vt = [System.Windows.SystemParameters]::VirtualScreenTop
        $vw = [System.Windows.SystemParameters]::VirtualScreenWidth
        $vh = [System.Windows.SystemParameters]::VirtualScreenHeight
        $wl = [double]$script:winPos.left
        $wt = [double]$script:winPos.top
        if ($wl -ge ($vl-50) -and ($wl + 200) -le ($vl + $vw) -and $wt -ge ($vt-50) -and ($wt + 100) -le ($vt + $vh)) {
            $window.Left = $wl
            $window.Top = $wt
            if ([double]$script:winPos.width -ge 1000) { $window.Width = [double]$script:winPos.width }
            if ([double]$script:winPos.height -ge 640) { $window.Height = [double]$script:winPos.height }
        }
    } catch { }
}
$window.Add_Closing({
    if ($window.WindowState -eq [System.Windows.WindowState]::Normal) {
        $script:winPos = [ordered]@{ left = $window.Left; top = $window.Top; width = $window.Width; height = $window.Height }
    } elseif ($window.WindowState -eq [System.Windows.WindowState]::Minimized) {
        $rb = $window.RestoreBounds
        if ($rb.Width -gt 0) { $script:winPos = [ordered]@{ left = $rb.Left; top = $rb.Top; width = $rb.Width; height = $rb.Height } }
    }
    try { Save-User } catch { }
})

$script:fittedOnce = -not $Minimized
$window.Add_StateChanged({
    if ($window.WindowState -ne [System.Windows.WindowState]::Minimized -and -not $script:fittedOnce) {
        $script:fittedOnce = $true
        $window.Dispatcher.BeginInvoke([action]{ Fit-Map }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
    }
})
if ($Minimized) {
    $window.ShowActivated = $false
    $window.WindowState = [System.Windows.WindowState]::Minimized
}

try {
    $window.ShowDialog() | Out-Null
} catch {
    Write-ErrorLog ($_.Exception.ToString())
}
