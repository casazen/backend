#!/usr/bin/env pwsh
# POST /api/admin/suppliers/invite on Railway test (or custom API base).
# Requires an Auth0 user with role Admin — M2M client_credentials tokens do NOT work.

param(
    [Parameter(Mandatory = $true)]
    [string]$SupplierEmail,

    [Parameter(Mandatory = $true)]
    [string]$ComuneCode,

    [string]$ApiBase = 'https://casazen-api-test.up.railway.app/api',
    [string]$Auth0Domain = 'dev-mp6wadq7j6bophl5.us.auth0.com',
    [string]$Auth0ClientId = 'xmZPesTR04r349c14n77MgJ2iSCeFaJb',
    [string]$Auth0Audience = 'https://casazen-api',
    [string]$AdminEmail = $env:E2E_AUTH0_ADMIN_EMAIL,
    [string]$AdminPassword = $env:E2E_AUTH0_ADMIN_PASSWORD,
    [string]$AccessToken = $env:CASAZEN_ADMIN_ACCESS_TOKEN,
    [string[]]$Categories = @('Pulizie'),
    [string]$Message = ''
)

$ErrorActionPreference = 'Stop'

function Get-AdminAccessToken {
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        return $AccessToken
    }

    if ([string]::IsNullOrWhiteSpace($AdminEmail) -or [string]::IsNullOrWhiteSpace($AdminPassword)) {
        throw @"
Admin JWT required. Either:
  - set CASAZEN_ADMIN_ACCESS_TOKEN (Bearer from browser DevTools after login as Admin), or
  - set E2E_AUTH0_ADMIN_EMAIL + E2E_AUTH0_ADMIN_PASSWORD for Auth0 password grant.

Note: M2M tokens from AUTH0_SETUP.md do not include the Admin role (expect 403).
"@
    }

    $tokenBody = @{
        grant_type = 'password'
        username   = $AdminEmail
        password   = $AdminPassword
        client_id  = $Auth0ClientId
        audience   = $Auth0Audience
        scope      = 'openid profile email'
    } | ConvertTo-Json

    $tokenRes = Invoke-RestMethod `
        -Uri "https://$Auth0Domain/oauth/token" `
        -Method POST `
        -ContentType 'application/json' `
        -Body $tokenBody

    return $tokenRes.access_token
}

$token = Get-AdminAccessToken

$payload = @{
    email      = $SupplierEmail
    comuneCode = $ComuneCode
    categories = $Categories
}
if (-not [string]::IsNullOrWhiteSpace($Message)) {
    $payload.message = $Message
}

$json = $payload | ConvertTo-Json -Compress
$uri = "$($ApiBase.TrimEnd('/'))/admin/suppliers/invite"

Write-Host "POST $uri"
$response = curl.exe -s -w "`nHTTP_STATUS:%{http_code}" `
    -X POST $uri `
    -H "Authorization: Bearer $token" `
    -H "Content-Type: application/json" `
    -d $json

$lines = $response -split "`n"
$statusLine = $lines | Where-Object { $_ -like 'HTTP_STATUS:*' } | Select-Object -Last 1
$body = ($lines | Where-Object { $_ -notlike 'HTTP_STATUS:*' }) -join "`n"
$status = if ($statusLine) { $statusLine.Split(':')[1] } else { 'unknown' }

Write-Host $body
Write-Host "Status: $status"

if ($status -eq '201') {
    exit 0
}

Write-Host @"

Common fixes:
  401 → missing/invalid Bearer token
  403 → user lacks Auth0 role Admin (https://casazen.app/roles)
  409 → pending invite already exists for this email
  502 → SendGrid/App__PublicSiteBaseUrl missing on Railway test
"@

exit 1
