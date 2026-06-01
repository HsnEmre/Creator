$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$stateFile = Join-Path $repoRoot "storage/dev/launched-processes.json"

if (-not (Test-Path $stateFile)) {
    Write-Host "No launcher state file found. Close windows manually if needed."
    exit 0
}

try {
    $state = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
} catch {
    Write-Host "Could not parse launcher state file. Close windows manually if needed."
    exit 0
}

$stopped = 0
foreach ($processId in $state.processIds) {
    if (-not $processId) { continue }
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -ne $proc) {
        Stop-Process -Id $processId -ErrorAction SilentlyContinue
        $stopped++
    }
}

Remove-Item -Path $stateFile -Force -ErrorAction SilentlyContinue
Write-Host "Stopped $stopped launcher process(es)."
Write-Host "If any child tools remain, close those terminals manually."
