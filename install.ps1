# POOLS Co-op installer / uninstaller.
#   install.bat                       -> install into every POOLS build found in your Steam library (flat and VR)
#   install.bat -Build flat|vr        -> only one build
#   install.bat -GameDir "D:\Games\Pools"
#   uninstall.bat                     -> remove exactly what this installer added
#
# What it does: (1) finds the game, (2) downloads the pinned BepInEx 6 IL2CPP build and checks its SHA-256, (3) copies it next to POOLS.exe,
# (4) copies PoolsCoop.dll (from dist\ next to this script, or from the repository) into BepInEx\plugins\PoolsCoop and checks its SHA-256.
# It never touches your saves, and it writes a manifest (BepInEx\poolscoop-manifest.txt) so uninstall removes only its own files.
# Needs Windows PowerShell 5.1 or newer and, for the BepInEx download, an internet connection (or pass -BepInExZip).

[CmdletBinding()]
param(
  [string]$GameDir = '',
  [ValidateSet('auto', 'flat', 'vr', 'both')][string]$Build = 'auto',
  [switch]$Uninstall,
  [switch]$Force,
  [string]$BepInExZip = '',     # use an already downloaded BepInEx zip (still hash-checked)
  [string]$PluginDll = ''       # use a specific PoolsCoop.dll instead of dist\PoolsCoop.dll
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- pinned downloads --------------------------------------------------------------------------------------------------------
$BepInExUrl    = 'https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip'
$BepInExSha256 = '5b63d7d0e92a2448ccd13b738ff95ff1cc1026c6d718555d4df8e6c824953ac4'
# Used only when this script runs without the repository next to it (for example piped from the web).
$RawBase       = 'https://raw.githubusercontent.com/duqisuki/pools-coop/main'    # filled in by tools\set-repo.ps1, e.g. https://raw.githubusercontent.com/owner/repo/main
$AppId         = '2663530'
$ManifestName  = 'poolscoop-manifest.txt'

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail([string]$text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---- find the game -----------------------------------------------------------------------------------------------------------
function Find-GameDir {
  $libs = New-Object System.Collections.Generic.List[string]
  $steam = $null
  foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
    try { $v = (Get-ItemProperty $k -ErrorAction Stop); foreach ($n in 'SteamPath', 'InstallPath') { if ($v.$n) { $steam = $v.$n; break } } } catch { }
    if ($steam) { break }
  }
  if ($steam) {
    $steam = $steam -replace '/', '\'
    $libs.Add($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
      foreach ($line in Get-Content $vdf) { if ($line -match '"path"\s+"([^"]+)"') { $libs.Add(($Matches[1] -replace '\\\\', '\')) } }
    }
  }
  foreach ($lib in ($libs | Select-Object -Unique)) {
    # A library on a drive that is not connected right now (an unplugged disk) must not stop the search.
    try {
      $manifest = [IO.Path]::Combine($lib, "steamapps\appmanifest_$AppId.acf")
      $dir = $null
      if (Test-Path -LiteralPath $manifest) {
        $m = Select-String -LiteralPath $manifest -Pattern '"installdir"\s+"([^"]+)"' | Select-Object -First 1
        if ($m) { $dir = [IO.Path]::Combine($lib, 'steamapps\common', $m.Matches[0].Groups[1].Value) }
      }
      if (-not $dir) { $dir = [IO.Path]::Combine($lib, 'steamapps\common\Pools') }
      if (Test-Path -LiteralPath ([IO.Path]::Combine($dir, 'POOLS.exe'))) { return $dir }
    } catch { continue }
  }
  return $null
}

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir) {
  Say 'POOLS was not found in your Steam library.' Yellow
  $GameDir = Read-Host 'Type the game folder (the one that contains POOLS.exe)'
}
$GameDir = $GameDir.Trim('"').TrimEnd('\')
if (-not (Test-Path (Join-Path $GameDir 'POOLS.exe'))) { Fail "POOLS.exe is not in '$GameDir'." }
Say "Game folder: $GameDir" Cyan

# ---- which builds ------------------------------------------------------------------------------------------------------------
$targets = New-Object System.Collections.Generic.List[string]
$flatOk = Test-Path (Join-Path $GameDir 'POOLS.exe')
$vrDir = Join-Path $GameDir 'POOLSVR'
$vrOk = Test-Path (Join-Path $vrDir 'POOLS.exe')
switch ($Build) {
  'flat' { if ($flatOk) { $targets.Add($GameDir) } }
  'vr'   { if ($vrOk) { $targets.Add($vrDir) } else { Fail "No VR build found in '$vrDir'." } }
  default {
    if ($flatOk) { $targets.Add($GameDir) }
    if ($vrOk) { $targets.Add($vrDir) }
  }
}
if ($targets.Count -eq 0) { Fail 'Nothing to do: no matching game build found.' }

if (Get-Process -Name 'POOLS' -ErrorAction SilentlyContinue) { Fail 'POOLS is running. Close the game and run this again.' }

# ---- uninstall ---------------------------------------------------------------------------------------------------------------
if ($Uninstall) {
  foreach ($t in $targets) {
    $manifest = Join-Path $t "BepInEx\$ManifestName"
    if (-not (Test-Path $manifest)) { Say "[$t] not installed by this installer, skipping." Yellow; continue }
    $lines = Get-Content $manifest
    $ownBepInEx = $lines -contains '#bepinex=installed-by-poolscoop'
    foreach ($rel in ($lines | Where-Object { $_ -and -not $_.StartsWith('#') })) {
      $p = Join-Path $t $rel
      if (Test-Path $p) { Remove-Item $p -Force }
    }
    $plugDir = Join-Path $t 'BepInEx\plugins\PoolsCoop'
    if (Test-Path $plugDir) { Remove-Item $plugDir -Recurse -Force }
    if ($ownBepInEx) {
      foreach ($d in 'BepInEx', 'dotnet') { $p = Join-Path $t $d; if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
      foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') { $p = Join-Path $t $f; if (Test-Path $p) { Remove-Item $p -Force } }
    }
    Say "[$t] removed." Green
  }
  Say 'Done. Your saves were not touched.' Green
  exit 0
}

# ---- get the files -----------------------------------------------------------------------------------------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('poolscoop-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  # plugin
  $here = $PSScriptRoot
  if (-not $PluginDll) {
    $local = if ($here) { Join-Path $here 'dist\PoolsCoop.dll' } else { '' }
    if ($local -and (Test-Path $local)) { $PluginDll = $local }
    elseif ($RawBase -like 'http*') {
      Say 'Downloading PoolsCoop.dll ...'
      $PluginDll = Join-Path $tmp 'PoolsCoop.dll'
      $bust = [Guid]::NewGuid().ToString('N')   # a fresh copy, not one the GitHub cache kept from before an update
      Invoke-WebRequest -Uri "$RawBase/dist/PoolsCoop.dll?nocache=$bust" -OutFile $PluginDll -UseBasicParsing -Headers @{ 'Cache-Control' = 'no-cache' }
      $expected = (Invoke-WebRequest -Uri "$RawBase/dist/PoolsCoop.dll.sha256?nocache=$bust" -UseBasicParsing -Headers @{ 'Cache-Control' = 'no-cache' }).Content.Trim().ToLowerInvariant()
      if ((Get-Sha256 $PluginDll) -ne $expected) { Fail 'PoolsCoop.dll does not match its published SHA-256 (GitHub may still be updating its copy: wait 5 minutes and run the command again).' }
    } else { Fail 'PoolsCoop.dll not found. Download the whole repository (Code > Download ZIP) and run install.bat from it.' }
  }
  if (-not (Test-Path $PluginDll)) { Fail "Plugin file not found: $PluginDll" }
  $pluginHash = Get-Sha256 $PluginDll
  $hashFile = if ($here) { Join-Path $here 'dist\PoolsCoop.dll.sha256' } else { '' }
  if ($hashFile -and (Test-Path $hashFile) -and ($PluginDll -eq (Join-Path $here 'dist\PoolsCoop.dll'))) {
    $expected = (Get-Content $hashFile -Raw).Trim().ToLowerInvariant()
    if ($pluginHash -ne $expected) { Fail 'dist\PoolsCoop.dll does not match dist\PoolsCoop.dll.sha256 (damaged download?).' }
  }
  Say "PoolsCoop.dll sha256 $pluginHash" DarkGray

  # BepInEx (only fetched when some target does not have it yet)
  $bepStage = Join-Path $tmp 'bepinex'
  $needBep = @($targets | Where-Object { -not (Test-Path (Join-Path $_ 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')) }).Count -gt 0
  if ($needBep) {
    if (-not $BepInExZip) {
      $BepInExZip = Join-Path $tmp 'bepinex.zip'
      Say 'Downloading BepInEx 6 (IL2CPP build 793) ...'
      Invoke-WebRequest -Uri $BepInExUrl -OutFile $BepInExZip -UseBasicParsing
    }
    if ((Get-Sha256 $BepInExZip) -ne $BepInExSha256) { Fail 'The BepInEx zip does not match the pinned SHA-256. Nothing was installed.' }
    Say 'BepInEx zip verified.' DarkGray
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($BepInExZip, $bepStage)
  }

  # ---- install into each build ----
  foreach ($t in $targets) {
    Say "[$t] installing ..." Cyan
    $manifestPath = Join-Path $t "BepInEx\$ManifestName"
    $core = Join-Path $t 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
    $loader = Join-Path $t 'winhttp.dll'
    $added = New-Object System.Collections.Generic.List[string]
    $ownBepInEx = $false
    # A re-install must keep what the first install recorded, otherwise uninstall would forget that it owns BepInEx.
    if (Test-Path $manifestPath) {
      $prev = Get-Content $manifestPath
      $ownBepInEx = $prev -contains '#bepinex=installed-by-poolscoop'
      foreach ($l in $prev) { if ($l -and -not $l.StartsWith('#')) { $added.Add($l) } }
    }

    if (Test-Path $core) {
      Say "  BepInEx is already installed here, keeping it and only adding the plugin." Yellow
    } elseif ((Test-Path $loader) -and -not $Force) {
      Fail "'$loader' already exists (another mod loader?). Use -Force to overwrite, or install manually."
    } else {
      foreach ($f in Get-ChildItem $bepStage -Recurse -File) {
        $rel = $f.FullName.Substring($bepStage.Length + 1)
        $dest = Join-Path $t $rel
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.FullName $dest -Force
        $added.Add($rel)
      }
      New-Item -ItemType Directory -Force (Join-Path $t 'BepInEx\plugins') | Out-Null
      $ownBepInEx = $true
      Say "  BepInEx copied ($($added.Count) files)." DarkGray
    }

    $plugDir = Join-Path $t 'BepInEx\plugins\PoolsCoop'
    New-Item -ItemType Directory -Force $plugDir | Out-Null
    $dest = Join-Path $plugDir 'PoolsCoop.dll'
    $oldVer = if (Test-Path $dest) { (Get-Item $dest).VersionInfo.FileVersion } else { '' }
    $oldHash = if (Test-Path $dest) { Get-Sha256 $dest } else { '' }
    Copy-Item $PluginDll $dest -Force
    $newVer = (Get-Item $dest).VersionInfo.FileVersion
    if (-not $oldHash) { Say "  PoolsCoop $newVer installed." DarkGray }
    elseif ($oldHash -eq $pluginHash) { Say "  PoolsCoop $newVer was already the latest version." DarkGray }
    else { Say "  PoolsCoop updated: $oldVer -> $newVer (your settings and saves are kept)." Green }
    if ((Get-Sha256 $dest) -ne $pluginHash) { Fail "Copy of PoolsCoop.dll into '$plugDir' is damaged." }
    $added.Add('BepInEx\plugins\PoolsCoop\PoolsCoop.dll')

    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('# written by the POOLS Co-op installer; uninstall removes exactly these files')
    if ($ownBepInEx) { $out.Add('#bepinex=installed-by-poolscoop') }
    foreach ($a in ($added | Select-Object -Unique)) { $out.Add($a) }
    Set-Content -Path $manifestPath -Value $out -Encoding ascii
    Say "[$t] installed." Green
  }

  Write-Host ''
  Say 'Done. Start POOLS from Steam as usual.' Green
  Say '  - The FIRST start takes about a minute longer: the mod prepares its files.' Gray
  Say '  - In the main menu click CO-OP (F7) at the top right (VR: the CO-OP button under the side buttons) for Host a game / Join a friend.' Gray
  Say '  - Both players need the mod, be Steam friends, and start the same chapter.' Gray
  Say '  - To remove everything: run uninstall.bat. Your saves are never touched.' Gray
}
finally {
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
