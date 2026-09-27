$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$themesDir = Join-Path $projectRoot 'Resources\Themes'
$darkPath = Join-Path $projectRoot 'Resources\Themes\DarkTheme.xaml'
$lightPath = Join-Path $projectRoot 'Resources\Themes\LightTheme.xaml'
$ashPath = Join-Path $projectRoot 'Resources\Themes\AshTheme.xaml'
$stylesPath = Join-Path $projectRoot 'Resources\Themes\ThemeStyles.xaml'
[xml]$dark = Get-Content -Raw $darkPath
[xml]$light = Get-Content -Raw $lightPath
[xml]$ash = Get-Content -Raw $ashPath
[xml]$styles = Get-Content -Raw $stylesPath

function Get-ResourceKeys([xml]$dictionary) {
    $keys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $dictionary.ResourceDictionary.ChildNodes) {
        if ($node -is [System.Xml.XmlElement] -and $node.HasAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')) {
            [void]$keys.Add($node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml'))
        }
    }
    return ,$keys
}

$darkKeys = Get-ResourceKeys $dark
$lightKeys = Get-ResourceKeys $light
$ashKeys = Get-ResourceKeys $ash
$styleKeys = Get-ResourceKeys $styles
# Shared token dictionaries (Shape/Motion/Elevation) also contribute theme resources.
$tokenKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($tokenFile in Get-ChildItem $themesDir -Filter '*.Tokens.xaml' -File) {
    [xml]$tokenDictionary = Get-Content -Raw $tokenFile.FullName
    $tokenFileKeys = Get-ResourceKeys $tokenDictionary
    foreach ($key in $tokenFileKeys) { [void]$tokenKeys.Add($key) }
}
$missingInLight = @($darkKeys | Where-Object { -not $lightKeys.Contains($_) })
$missingInDark = @($lightKeys | Where-Object { -not $darkKeys.Contains($_) })
$missingInAsh = @($darkKeys | Where-Object { -not $ashKeys.Contains($_) })
$missingAshInDark = @($ashKeys | Where-Object { -not $darkKeys.Contains($_) })
if ($missingInLight.Count -or $missingInDark.Count -or $missingInAsh.Count -or $missingAshInDark.Count) {
    throw "Theme resource keys differ. Missing in Light: $($missingInLight -join ', '); missing in Dark: $($missingInDark -join ', '); missing in Ash: $($missingInAsh -join ', '); missing Ash keys in Dark: $($missingAshInDark -join ', ')"
}
$availableKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in $darkKeys) { [void]$availableKeys.Add($key) }
foreach ($key in $styleKeys) { [void]$availableKeys.Add($key) }
foreach ($key in $tokenKeys) { [void]$availableKeys.Add($key) }

function Get-ThemeColorMap([xml]$dictionary) {
    $colors = @{}
    foreach ($node in $dictionary.ResourceDictionary.Color) {
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($key -and $node.InnerText -match '^#([0-9A-Fa-f]{6})$') {
            $colors[$key] = $Matches[1]
        }
    }
    return $colors
}

function Get-RelativeLuminance([string]$hex) {
    $channels = @(
        [Convert]::ToInt32($hex.Substring(0, 2), 16),
        [Convert]::ToInt32($hex.Substring(2, 2), 16),
        [Convert]::ToInt32($hex.Substring(4, 2), 16)
    )
    $linear = foreach ($channel in $channels) {
        $value = $channel / 255.0
        if ($value -le 0.04045) { $value / 12.92 } else { [Math]::Pow(($value + 0.055) / 1.055, 2.4) }
    }
    return 0.2126 * $linear[0] + 0.7152 * $linear[1] + 0.0722 * $linear[2]
}

function Assert-Contrast([hashtable]$colors, [string]$foregroundKey, [string]$backgroundKey, [double]$minimum, [string]$themeName) {
    $foreground = Get-RelativeLuminance $colors[$foregroundKey]
    $background = Get-RelativeLuminance $colors[$backgroundKey]
    $ratio = ([Math]::Max($foreground, $background) + 0.05) / ([Math]::Min($foreground, $background) + 0.05)
    if ($ratio -lt $minimum) {
        throw "$themeName contrast $foregroundKey/$backgroundKey is $([Math]::Round($ratio, 2)):1; requires $minimum`:1"
    }
}

foreach ($theme in @(@{ Name = 'Dark'; Colors = (Get-ThemeColorMap $dark) }, @{ Name = 'Light'; Colors = (Get-ThemeColorMap $light) }, @{ Name = 'Ash'; Colors = (Get-ThemeColorMap $ash) })) {
    $colors = $theme.Colors
    Assert-Contrast $colors 'TextPrimaryColor' 'SurfaceWindowColor' 4.5 $theme.Name
    Assert-Contrast $colors 'TextSecondaryColor' 'SurfaceWindowColor' 4.5 $theme.Name
    Assert-Contrast $colors 'TextPrimaryColor' 'SurfaceControlColor' 4.5 $theme.Name
    Assert-Contrast $colors 'AccentButtonForegroundColor' 'ApplyButtonColor' 4.5 $theme.Name
    Assert-Contrast $colors 'WaveformColor' 'WaveformBackgroundColor' 3.0 $theme.Name
    Assert-Contrast $colors 'MinimapWaveformColor' 'WaveformBackgroundColor' 3.0 $theme.Name
    Assert-Contrast $colors 'PlaybackCursorColor' 'WaveformBackgroundColor' 3.0 $theme.Name
}

$sourceFiles = Get-ChildItem $projectRoot -Recurse -File | Where-Object {
    ($_.Extension -eq '.xaml' -or $_.Extension -eq '.cs') -and
    $_.FullName -notmatch '[\\/]Resources[\\/]Themes[\\/]'
}
foreach ($file in $sourceFiles | Where-Object { $_.Extension -eq '.xaml' }) {
    $content = Get-Content -Raw $file.FullName
    foreach ($match in [regex]::Matches($content, 'x:Key="([^"]+)"')) {
        [void]$availableKeys.Add($match.Groups[1].Value)
    }
}
$violations = [System.Collections.Generic.List[string]]::new()
$missingReferences = [System.Collections.Generic.List[string]]::new()
foreach ($file in $sourceFiles) {
    $lineNumber = 0
    foreach ($line in Get-Content $file.FullName) {
        $lineNumber++
        if ($line -match '#[0-9A-Fa-f]{3,8}\b|Color\.From(?:Rgb|Argb)|Brushes\.(?!Transparent\b)[A-Za-z]+\b|(?:Foreground|Background|BorderBrush|Fill|Stroke)="(?:White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange|Purple|Pink|Brown|Cyan|Magenta)"') {
            $relative = $file.FullName.Substring($projectRoot.Length + 1)
            $violations.Add("${relative}:${lineNumber}: move the UI color literal into a theme dictionary")
        }
        $resourceReferences = [regex]::Matches($line, '\{(?:DynamicResource|StaticResource)\s+([^}\s]+)\}|(?:FindResource|TryFindResource)\("([^"]+)"\)')
        foreach ($match in $resourceReferences) {
            $resourceKey = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            if (-not $availableKeys.Contains($resourceKey)) {
                $relative = $file.FullName.Substring($projectRoot.Length + 1)
                $missingReferences.Add("${relative}:${lineNumber}: missing theme/style resource '$resourceKey'")
            }
        }
    }
}
if ($violations.Count) {
    throw "Hard-coded UI colors found:`n$($violations -join "`n")"
}
if ($missingReferences.Count) {
    throw "Undefined theme or style resources found:`n$($missingReferences -join "`n")"
}

Write-Host 'Theme resource keys match, references resolve, and no UI color literals were found.'
