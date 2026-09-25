#!/usr/bin/env pwsh
# Start the .NET backend locally with InMemory database (no Supabase needed).
# Use this for local E2E testing or frontend development without a real database.
#
# Usage: .\scripts\start-backend-local.ps1
#        .\scripts\start-backend-local.ps1 -Port 5000

param(
    [Parameter(Mandatory = $false)]
    [int]$Port = 5000
)

$ErrorActionPreference = 'Stop'

# Clear connection string to trigger EF Core InMemory fallback
$env:ConnectionStrings__DefaultConnection = ""
$env:ASPNETCORE_ENVIRONMENT = "Development"

# JWT validation must match the Auth0 tenant of the SPA you log in with (development/test tenant, never production:
# docs/runbooks/auth0.md section 1). No default tenant: set Auth0__Domain before running this script.
if (-not $env:Auth0__Domain) {
  throw 'Set Auth0__Domain to the login domain of your development/test Auth0 tenant (same as VITE_AUTH0_DOMAIN of the web app).'
}
if (-not $env:Auth0__Audience) {
  $env:Auth0__Audience = 'https://casazen-api'
}

Write-Host "============================================================"
Write-Host "Starting CasaZen backend with InMemory database"
Write-Host "Port: http://localhost:$Port"
Write-Host "Swagger: http://localhost:$Port/swagger"
Write-Host "Health: http://localhost:$Port/api/health"
Write-Host "Auth0 Domain: $($env:Auth0__Domain)"
Write-Host "============================================================"

dotnet run --project Casazen.Web --urls "http://localhost:$Port"
