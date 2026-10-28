# Prints the fields/methods of dumped game types, one line each, marking [PunRPC] methods.
# Usage: tools\members.ps1 GhostAI,PlayerSanity [-Pattern '@0x']   (run tools\dump-game.ps1 first)
param(
    [Parameter(Mandatory)][string[]]$Types,
    [string]$Pattern = '.'
)
$dir = Join-Path $PSScriptRoot ".cache\dump\DiffableCs\Assembly-CSharp"
if (-not (Test-Path $dir)) { throw "No dump found. Run tools\dump-game.ps1 first." }

foreach ($t in $Types) {
    "===== $t"
    $file = Get-ChildItem $dir -Recurse -Filter "$t.cs" | Select-Object -First 1
    if (-not $file) { "(missing)"; continue }
    $rpc = $false
    foreach ($line in (Get-Content $file.FullName -Encoding UTF8)) {
        if ($line -match '^\t\[PunRPC\]') { $rpc = $true; continue }
        if ($line -match '^\t(public|private|protected|internal)' -and $line -notmatch ' class |const ') {
            $x = $line.Trim() -replace '\s*\{\s*\}\s*$', '' -replace '//Field offset: ', '@'
            if ($x -match $Pattern) { if ($rpc) { "[RPC] $x" } else { $x } }
        }
        if ($line -match '^\t\S') { $rpc = $false }
    }
}
