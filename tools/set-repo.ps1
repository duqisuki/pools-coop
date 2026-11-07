# Run once after the GitHub repository exists, before the first push:  .\tools\set-repo.ps1 owner/name
# Writes the repository's raw-content address into install.ps1 (so the one-line PowerShell install can fetch PoolsCoop.dll) and the README.
param([Parameter(Mandatory)][string]$Repo, [string]$Branch = 'main')
$root = Split-Path $PSScriptRoot -Parent
$raw = "https://raw.githubusercontent.com/$Repo/$Branch"
$f = Join-Path $root 'install.ps1'
(Get-Content $f -Raw) -replace "(\$RawBase\s+=\s+)'[^']*'", "`${1}'$raw'" | Set-Content $f -Encoding UTF8 -NoNewline
$r = Join-Path $root 'README.md'
(Get-Content $r -Raw) -replace 'OWNER/REPO', $Repo | Set-Content $r -Encoding UTF8 -NoNewline
"install.ps1 and README.md now point at $raw"
