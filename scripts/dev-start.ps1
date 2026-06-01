$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiDir = Join-Path $repoRoot "backend/VideoStudio.Api"
$webDir = Join-Path $repoRoot "frontend/VideoStudio.Web"
$workerDir = Join-Path $repoRoot "workers/video-worker"
$stateDir = Join-Path $repoRoot "storage/dev"
$stateFile = Join-Path $stateDir "launched-processes.json"

New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

function Start-DevWindow {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$Command
    )

    $safeTitle = $Title.Replace("'", "''")
    $fullCommand = "`$Host.UI.RawUI.WindowTitle = '$safeTitle'; $Command"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fullCommand))
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit", "-EncodedCommand", $encodedCommand -PassThru
}

$apiCmd = @'
Set-Location "__API_DIR__"
Write-Host "[API] Starting ASP.NET Core backend on http://localhost:5281 ..."
dotnet run --urls http://localhost:5281
'@.Replace("__API_DIR__", $apiDir)

$webCmd = @'
Set-Location "__WEB_DIR__"
if (-not (Test-Path "node_modules")) {
  Write-Host "[WEB] Installing npm dependencies ..."
  npm.cmd install
}
Write-Host "[WEB] Starting Vite dev server on http://localhost:5173 ..."
npm.cmd run dev -- --host localhost --port 5173
'@.Replace("__WEB_DIR__", $webDir)

$workerCmd = @'
Set-Location "__WORKER_DIR__"
if (-not (Test-Path ".venv")) {
  Write-Host "[WORKER] Creating Python 3.9 virtual environment ..."
  py -3.9 -m venv .venv
}

$stampPath = ".venv/.requirements_installed"
$installDeps = -not (Test-Path $stampPath)

if ($installDeps) {
  Write-Host "[WORKER] Installing worker requirements ..."
  .\.venv\Scripts\python.exe -m pip install -r requirements.txt
  New-Item -ItemType File -Path $stampPath -Force | Out-Null
}

$env:VIDEO_API_BASE_URL = "http://localhost:5281"
Write-Host "[WORKER] VIDEO_API_BASE_URL=$env:VIDEO_API_BASE_URL"
Write-Host "[WORKER] Starting Python worker ..."
.\.venv\Scripts\python.exe main.py
'@.Replace("__WORKER_DIR__", $workerDir)

$apiProcess = Start-DevWindow -Title "VideoStudio API" -Command $apiCmd
$webProcess = Start-DevWindow -Title "VideoStudio Web" -Command $webCmd
$workerProcess = Start-DevWindow -Title "VideoStudio Worker" -Command $workerCmd

$payload = @{
    createdAtUtc = [DateTime]::UtcNow.ToString("o")
    processIds = @($apiProcess.Id, $webProcess.Id, $workerProcess.Id)
} | ConvertTo-Json
Set-Content -Path $stateFile -Value $payload -Encoding UTF8

Write-Host ""
Write-Host "VideoStudio local development services started:"
Write-Host "  Backend:  http://localhost:5281/swagger"
Write-Host "  Frontend: http://localhost:5173"
Write-Host "  Worker:   separate PowerShell window"
Write-Host ""
Write-Host "Use .\scripts\dev-stop.ps1 to close windows started by this launcher."
