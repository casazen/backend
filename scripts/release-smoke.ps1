#!/usr/bin/env pwsh
# Post-release production smoke — run after develop → main merge (Stage 05 Phase D).
# Fails fast on health, auth-gate regressions, and missing tenant endpoints.

param(
    [Parameter(Mandatory = $false)]
    [string]$ProdApiUrl = $env:RAILWAY_PROD_URL,

    [Parameter(Mandatory = $false)]
    [string]$ProdFeUrl = 'https://casazen-app.vercel.app',

    [Parameter(Mandatory = $false)]
    [switch]$SkipMigrations
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProdApiUrl)) {
    $ProdApiUrl = 'https://casazen-api.up.railway.app'
}

$ProdApiUrl = $ProdApiUrl.TrimEnd('/')
$apiBase = "$ProdApiUrl/api"

function Test-HttpStatus {
    param(
        [string]$Url,
        [int[]]$AllowedStatuses,
        [string]$Label
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -SkipHttpErrorCheck
        $status = [int]$response.StatusCode
    }
    catch {
        Write-Error "$Label failed: $_"
        return $false
    }

    if ($AllowedStatuses -notcontains $status) {
        Write-Error "$Label expected one of [$($AllowedStatuses -join ', ')] but got $status — $Url"
        return $false
    }

    if ($status -eq 500) {
        Write-Error "$Label returned 500 — likely pending EF migration on casazen_prod. Run: .\scripts\migrate.ps1 -Target prod"
        return $false
    }

    Write-Host "OK $Label ($status)"
    return $true
}

Write-Host "=== CasaZen production smoke ==="
Write-Host "API: $ProdApiUrl"
Write-Host "FE:  $ProdFeUrl"
Write-Host ""

if (-not $SkipMigrations) {
    Write-Host "Applying EF migrations to prod schema (casazen_prod)..."
    & "$PSScriptRoot\migrate.ps1" -Target prod
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host ""
}

$allOk = $true

$allOk = (Test-HttpStatus -Url "$apiBase/health" -AllowedStatuses @(200) -Label 'G16 health') -and $allOk

$authGatePaths = @(
    'properties',
    'bookings',
    'users/me',
    'me/contexts',
    'orgs/me/entitlement'
)

foreach ($path in $authGatePaths) {
    $ok = Test-HttpStatus -Url "$apiBase/$path" -AllowedStatuses @(401) -Label "auth gate /api/$path"
    $allOk = $ok -and $allOk
}

try {
    $feResponse = Invoke-WebRequest -Uri $ProdFeUrl -Method Get -SkipHttpErrorCheck
    if ($feResponse.StatusCode -ne 200) {
        Write-Error "G17 FE prod health expected 200, got $($feResponse.StatusCode) for $ProdFeUrl"
        $allOk = $false
    }
    elseif ($feResponse.Content -notmatch 'id="root"') {
        Write-Error "G17 FE prod SPA missing id=`"root`" — check Vercel Production deploy for $ProdFeUrl"
        $allOk = $false
    }
    else {
        Write-Host "OK G17 FE prod SPA (200, id=`"root`")"
    }
}
catch {
    Write-Error "G17 FE prod health failed: $_"
    $allOk = $false
}

if (-not $allOk) {
    Write-Host ""
    Write-Error "Production smoke FAILED — do not mark Stage 05 Phase D complete."
    exit 1
}

Write-Host ""
Write-Host "Production smoke passed."
Write-Host "Next: run authenticated prod E2E in frontend repo:"
Write-Host "  E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke"
