<#
.SYNOPSIS
  Installs BepInEx 5 and the OGMultiplayer plugin into a Windows copy of Ostfriesland Games.
  Nothing from the game is copied anywhere; the plugin is compiled against your own install.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Ostfriesland Games"

.NOTES
  Needs the .NET SDK 8 (winget install Microsoft.DotNet.SDK.8).
  BEPINEX_ZIP (environment) may point to an already downloaded BepInEx_win_x64 zip.
#>
param(
    [Parameter(Position = 0)]
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'

$BepInExVersion = '5.4.23.6'
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256 = '6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755'

$RepoDir = Split-Path -Parent $PSScriptRoot

function Test-GameDir([string]$dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'OG_Data/Managed/Assembly-CSharp.dll'))
}

if (-not $GameDir) {
    # common Steam locations
    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Ostfriesland Games",
        "$env:ProgramFiles\Steam\steamapps\common\Ostfriesland Games"
    )
    $GameDir = $candidates | Where-Object { Test-GameDir $_ } | Select-Object -First 1
    if (-not $GameDir) {
        $GameDir = Read-Host 'Pfad zum Spielordner (mit OG.exe)'
    }
}
$GameDir = $GameDir.Trim('"')

if (-not (Test-GameDir $GameDir)) {
    throw "'$GameDir' sieht nicht wie der Spielordner aus (OG_Data/Managed/Assembly-CSharp.dll fehlt)."
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet nicht gefunden. Bitte das .NET SDK 8 installieren: winget install Microsoft.DotNet.SDK.8'
}

# 1) BepInEx
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx/core/BepInEx.dll'))) {
    $zip = $env:BEPINEX_ZIP
    $tmp = $null
    if (-not $zip) {
        Write-Host "==> Lade BepInEx $BepInExVersion ..."
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("bepinex-" + [Guid]::NewGuid().ToString('N') + '.zip')
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $tmp -UseBasicParsing
        $zip = $tmp
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $BepInExSha256) {
        throw "Prüfsumme von BepInEx stimmt nicht ($hash)."
    }
    Write-Host "==> Entpacke BepInEx nach $GameDir"
    Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    if ($tmp) { Remove-Item -LiteralPath $tmp -Force }
}
else {
    Write-Host '==> BepInEx ist bereits installiert'
}

# 2) Plugin
Write-Host '==> Baue Plugin'
$out = Join-Path $RepoDir 'build/plugin'
& dotnet build (Join-Path $RepoDir 'src/OGMultiplayer.Plugin/OGMultiplayer.Plugin.csproj') -c Release -nologo -v quiet "-p:GameDir=$GameDir" -o $out
if ($LASTEXITCODE -ne 0) {
    throw 'Build fehlgeschlagen.'
}

$pluginDir = Join-Path $GameDir 'BepInEx/plugins/OGMultiplayer'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $out 'OGMultiplayer.dll') -Destination $pluginDir -Force

Write-Host ''
Write-Host 'Fertig. Das Spiel einfach normal starten (Steam oder OG.exe) - BepInEx lädt den Mod automatisch.'
Write-Host 'Im Spiel: Hauptmenü > Mehrspieler > Online (oder F8).'
