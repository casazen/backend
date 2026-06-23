#!/usr/bin/env pwsh
# GET or PUT /api/supplier/profile on Railway test (or custom API base).
# Requires Auth0 user with role Supplier — PropertyOwner/Admin tokens return 403.

param(
    [ValidateSet('GET', 'PUT')]
    [string]$Method = 'GET',

    [string]$ApiBase = 'https://casazen-api-test.up.railway.app/api',
    [string]$Auth0Domain = 'dev-mp6wadq7j6bophl5.us.auth0.com',
    [string]$Auth0ClientId = 'xmZPesTR04r349c14n77MgJ2iSCeFaJb',
    [string]$Auth0Audience = 'https://casazen-api',
    [string]$SupplierEmail = $env:E2E_AUTH0_SUPPLIER_EMAIL,
    [string]$SupplierPassword = $env:E2E_AUTH0_SUPPLIER_PASSWORD,
    [string]$AccessToken = $env:CASAZEN_SUPPLIER_ACCESS_TOKEN,

    # PUT body (all optional)
    [string]$LegalName,
    [string]$VatNumber,
    [string]$Phone,
    [string[]]$Categories,
    [string[]]$Comuni,
    [string]$Bio,
    [string[]]$PhotoUrls
)

$ErrorActionPreference = 'Stop'

function Get-SupplierAccessToken {
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        return $AccessToken
    }

    if ([string]::IsNullOrWhiteSpace($SupplierEmail) -or [string]::IsNullOrWhiteSpace($SupplierPassword)) {
        throw @"
Supplier JWT required. Either:
  - set CASAZEN_SUPPLIER_ACCESS_TOKEN (Bearer from browser after login as Supplier), or
  - set E2E_AUTH0_SUPPLIER_EMAIL + E2E_AUTH0_SUPPLIER_PASSWORD for Auth0 password grant.

The user must have Auth0 role Supplier (https://casazen.app/roles).
After invite signup, assign Supplier in Auth0 before calling /api/supplier/*.
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

    $tokenRes = Invoke-RestMethod `
        -Uri "https://$Auth0Domain/oauth/token" `
        -Method POST `
        -ContentType 'application/json' `
        -Body $tokenBody

    return $tokenRes.access_token
}

$token = Get-SupplierAccessToken
$uri = "$($ApiBase.TrimEnd('/'))/supplier/profile"

if ($Method -eq 'GET') {
    Write-Host "GET $uri"
    $response = curl.exe -s -w "`nHTTP_STATUS:%{http_code}" `
        -X GET $uri `
        -H "Authorization: Bearer $token"
}
else {
    $payload = @{}
    if ($PSBoundParameters.ContainsKey('LegalName')) { $payload.legalName = $LegalName }
    if ($PSBoundParameters.ContainsKey('VatNumber')) { $payload.vatNumber = $VatNumber }
    if ($PSBoundParameters.ContainsKey('Phone')) { $payload.phone = $Phone }
    if ($PSBoundParameters.ContainsKey('Categories')) { $payload.categories = $Categories }
    if ($PSBoundParameters.ContainsKey('Comuni')) { $payload.comuni = $Comuni }
    if ($PSBoundParameters.ContainsKey('Bio')) { $payload.bio = $Bio }
    if ($PSBoundParameters.ContainsKey('PhotoUrls')) { $payload.photoUrls = $PhotoUrls }

    if ($payload.Count -eq 0) {
        throw 'PUT requires at least one field (-Bio, -Categories, -LegalName, ...)'
    }

    $json = $payload | ConvertTo-Json -Compress
    Write-Host "PUT $uri"
    $response = curl.exe -s -w "`nHTTP_STATUS:%{http_code}" `
        -X PUT $uri `
        -H "Authorization: Bearer $token" `
        -H "Content-Type: application/json" `
        -d $json
}

$lines = $response -split "`n"
$statusLine = $lines | Where-Object { $_ -like 'HTTP_STATUS:*' } | Select-Object -Last 1
$body = ($lines | Where-Object { $_ -notlike 'HTTP_STATUS:*' }) -join "`n"
$status = if ($statusLine) { $statusLine.Split(':')[1] } else { 'unknown' }

Write-Host $body
Write-Host "Status: $status"

if ($status -eq '200') {
    exit 0
}

Write-Host @"

Common fixes:
  401 → missing/invalid Bearer token
  403 → user lacks Auth0 role Supplier
  404 → supplier org/profile not found (rare: auto-provision usually creates one on first call)
  405 → wrong HTTP method (use GET or PUT, not POST)
"@

exit 1
