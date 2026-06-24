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

Write-Host "============================================================"
Write-Host "Starting CasaZen backend with InMemory database"
Write-Host "Port: http://localhost:$Port"
Write-Host "Swagger: http://localhost:$Port/swagger"
Write-Host "Health: http://localhost:$Port/api/health"
Write-Host "============================================================"

dotnet run --project Casazen.Web --urls "http://localhost:$Port"
