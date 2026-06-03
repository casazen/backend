#!/usr/bin/env pwsh
# One-time setup: read secrets/supabase.local.env → dotnet user-secrets (local machine only).

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_supabase.ps1"

$repoRoot = Get-RepoRoot
$webProject = Join-Path $repoRoot 'Casazen.Web'
$envExample = Join-Path $repoRoot 'secrets\supabase.local.env.example'
$envFile = Get-SupabaseEnvFilePath

if (-not (Test-Path $envFile)) {
    Copy-Item $envExample $envFile
    Write-Host "Created $envFile - edit SUPABASE_HOST and SUPABASE_PASSWORD, then run this script again."
    exit 1
}

$testConn = Get-SupabaseConnectionString -Target test
$prodConn = Get-SupabaseConnectionString -Target prod

Write-Host "Setting dotnet user-secrets on Casazen.Web (stored outside repo)..."

dotnet user-secrets set "ConnectionStrings:DefaultConnection" $testConn --project $webProject | Out-Null
dotnet user-secrets set "ConnectionStrings:SupabaseTest" $testConn --project $webProject | Out-Null
dotnet user-secrets set "ConnectionStrings:SupabaseProd" $prodConn --project $webProject | Out-Null

Write-Host "Done. Connection strings saved to user-secrets (casazen-backend-local)."
Write-Host ""
Write-Host "Next:"
Write-Host "  .\scripts\migrate.ps1 -Target test"
Write-Host "  .\scripts\migrate.ps1 -Target prod   # when promoting"
