# Dumps Phasmophobia's IL2CPP type signatures to tools/.cache/dump so hooks can be re-verified
# after a game update. Read-only with respect to the game folder.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Phasmophobia",
    [string]$Cpp2IlVersion = "2022.1.0-pre-release.21"
)
$ErrorActionPreference = "Stop"
$cache = Join-Path $PSScriptRoot ".cache"
$exe = Join-Path $cache "Cpp2IL-$Cpp2IlVersion.exe"
$out = Join-Path $cache "dump"

New-Item -ItemType Directory -Force $cache | Out-Null
if (-not (Test-Path $exe)) {
    Invoke-WebRequest "https://github.com/SamboyCoding/Cpp2IL/releases/download/$Cpp2IlVersion/Cpp2IL-$Cpp2IlVersion-Windows.exe" -OutFile $exe
}
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Push-Location $cache
try { & $exe --game-path $GameDir --output-as diffable-cs --output-to $out }
finally { Pop-Location }

Write-Host "`nDump written to $out\DiffableCs\Assembly-CSharp"
Write-Host "Inspect a type with: tools\members.ps1 GhostAI,PlayerSanity"
