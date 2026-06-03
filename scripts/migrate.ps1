#!/usr/bin/env pwsh
# Apply EF Core migrations to Supabase (test or prod schema).

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('test', 'prod')]
    [string]$Target = 'test'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_supabase.ps1"

$repoRoot = Get-RepoRoot
$infra = Join-Path $repoRoot 'Casazen.Infrastructure'
$web = Join-Path $repoRoot 'Casazen.Web'

$connectionString = Get-SupabaseConnectionString -Target $Target
$env:ConnectionStrings__DefaultConnection = $connectionString
$env:CASAZEN_MIGRATION_TARGET = $Target

$schema = if ($Target -eq 'prod') { 'casazen_prod' } else { 'casazen_test' }
Write-Host "Applying migrations to Supabase schema: $schema"
Write-Host "Host: $($connectionString -replace 'Password=[^;]+', 'Password=***')"

dotnet ef database update `
    --project $infra `
    --startup-project $web

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Migrations applied successfully to $schema."
