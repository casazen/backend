#!/usr/bin/env pwsh
# PUT /api/supplier/availability — upsert availability by date.
# Requires Auth0 user with role Supplier. No GET endpoint (PUT only).

param(
    [Parameter(Mandatory = $true)]
    [string[]]$Dates,

    [bool[]]$Available,
    [string]$ApiBase = 'https://casazen-api-test.up.railway.app/api',
    [string]$Auth0Domain = 'dev-mp6wadq7j6bophl5.us.auth0.com',
    [string]$Auth0ClientId = 'xmZPesTR04r349c14n77MgJ2iSCeFaJb',
    [string]$Auth0Audience = 'https://casazen-api',
    [string]$SupplierEmail = $env:E2E_AUTH0_SUPPLIER_EMAIL,
    [string]$SupplierPassword = $env:E2E_AUTH0_SUPPLIER_PASSWORD,
    [string]$AccessToken = $env:CASAZEN_SUPPLIER_ACCESS_TOKEN
)

$ErrorActionPreference = 'Stop'

function Get-SupplierAccessToken {
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        return $AccessToken
    }

    if ([string]::IsNullOrWhiteSpace($SupplierEmail) -or [string]::IsNullOrWhiteSpace($SupplierPassword)) {
        throw @"
Supplier JWT required. Set CASAZEN_SUPPLIER_ACCESS_TOKEN or E2E_AUTH0_SUPPLIER_EMAIL + E2E_AUTH0_SUPPLIER_PASSWORD.
User must have Auth0 role Supplier.
"@
    }

    $tokenBody = @{
        grant_type = 'password'
        username   = $SupplierEmail
        password   = $SupplierPassword
        client_id  = $Auth0ClientId
        audience   = $Auth0Audience
        scope      = 'openid profile email'
    } | ConvertTo-Json

    return (Invoke-RestMethod -Uri "https://$Auth0Domain/oauth/token" -Method POST `
        -ContentType 'application/json' -Body $tokenBody).access_token
}

if ($Dates.Count -eq 0) {
    throw 'At least one date required (yyyy-MM-dd). Example: -Dates 2026-06-23,2026-06-24'
}

$entries = for ($i = 0; $i -lt $Dates.Count; $i++) {
    $isAvailable = if ($Available -and $i -lt $Available.Count) { $Available[$i] } else { $true }
    @{ date = $Dates[$i]; available = $isAvailable }
}

$payload = @{ dates = $entries } | ConvertTo-Json -Compress
$token = Get-SupplierAccessToken
$uri = "$($ApiBase.TrimEnd('/'))/supplier/availability"

Write-Host "PUT $uri"
$response = curl.exe -s -w "`nHTTP_STATUS:%{http_code}" `
    -X PUT $uri `
    -H "Authorization: Bearer $token" `
    -H "Content-Type: application/json" `
    -d $payload

$lines = $response -split "`n"
$statusLine = $lines | Where-Object { $_ -like 'HTTP_STATUS:*' } | Select-Object -Last 1
$body = ($lines | Where-Object { $_ -notlike 'HTTP_STATUS:*' }) -join "`n"
$status = if ($statusLine) { $statusLine.Split(':')[1] } else { 'unknown' }

Write-Host $body
Write-Host "Status: $status"

if ($status -eq '200') { exit 0 }

Write-Host @"

Common fixes:
  401 → missing/invalid Bearer token
  403 → user lacks Auth0 role Supplier
  405 → used GET/POST — this endpoint is PUT only
  404 → supplier org not found
"@

exit 1
