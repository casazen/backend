<#
.SYNOPSIS
  Starts local InMemory backend (if needed) and runs Playwright L3 suite.
#>
param(
  [string]$SpecFilter = 'l3',
  [string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
  [string]$FrontendRoot = (Resolve-Path (Join-Path $BackendRoot '../frontend')).Path,
  [int]$ApiPort = 5000
)

$ErrorActionPreference = 'Stop'

function Test-ApiHealthy {
  try {
    $r = Invoke-WebRequest -Uri "http://localhost:$ApiPort/api/health" -UseBasicParsing -TimeoutSec 3
    return $r.StatusCode -eq 200
  } catch {
    return $false
  }
}

$startedBackend = $false
if (-not (Test-ApiHealthy)) {
  Write-Host "Starting local backend on :$ApiPort ..."
  $startScript = Join-Path $BackendRoot 'scripts/start-backend-local.ps1'
  if (-not (Test-Path $startScript)) {
    Write-Error "Missing $startScript"
  }
  Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-File', $startScript) -WorkingDirectory $BackendRoot -WindowStyle Hidden
  $startedBackend = $true
  $deadline = (Get-Date).AddMinutes(2)
  while (-not (Test-ApiHealthy)) {
    if ((Get-Date) -gt $deadline) {
      Write-Error 'Local backend did not become healthy within 2 minutes'
    }
    Start-Sleep -Seconds 2
  }
  Write-Host 'Backend healthy.'
}

Push-Location $FrontendRoot
try {
  $env:E2E_LOCAL = '1'
  $env:E2E_LOCAL_API_URL = "http://localhost:$ApiPort/api"
  Write-Host "Running L3 Playwright (filter=$SpecFilter) ..."
  npm run test:e2e:local -- --grep "$SpecFilter"
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
  Write-Host 'run-l3-local: PASS'
} finally {
  Pop-Location
  if ($startedBackend) {
    Write-Host 'Note: local backend process may still be running; stop it manually if needed.'
  }
}

exit 0
