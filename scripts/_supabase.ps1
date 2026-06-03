# Shared helpers for Supabase connection strings (PowerShell).
# Dot-source from other scripts: . "$PSScriptRoot\_supabase.ps1"

$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $root = Split-Path $PSScriptRoot -Parent
    if (-not (Test-Path (Join-Path $root 'Casazen.sln'))) {
        throw "Cannot find repo root from $PSScriptRoot"
    }
    return $root
}

function Get-SupabaseEnvFilePath {
    Join-Path (Get-RepoRoot) 'secrets\supabase.local.env'
}

function Read-SupabaseEnvFile {
    $path = Get-SupabaseEnvFilePath
    if (-not (Test-Path $path)) {
        return $null
    }

    $vars = @{}
    Get-Content $path | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { return }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { return }
        $key = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if ($value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $vars[$key] = $value
    }
    return $vars
}

function Get-SupabaseConnectionString {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('test', 'prod')]
        [string]$Target
    )

    $schema = if ($Target -eq 'prod') { 'casazen_prod' } else { 'casazen_test' }

    $vars = Read-SupabaseEnvFile
    if ($null -eq $vars) {
        throw @"
secrets/supabase.local.env not found.

  1. Copy secrets/supabase.local.env.example → secrets/supabase.local.env
  2. Fill SUPABASE_HOST and SUPABASE_PASSWORD from Supabase dashboard
  3. Run: .\scripts\setup-supabase.ps1
"@
    }

    $dbHost = $vars['SUPABASE_HOST'].Trim()
    $password = $vars['SUPABASE_PASSWORD']
    if ([string]::IsNullOrWhiteSpace($dbHost) -or [string]::IsNullOrWhiteSpace($password)) {
        throw 'SUPABASE_HOST and SUPABASE_PASSWORD must be set in secrets/supabase.local.env'
    }

    # Accept: pooler host, db.ref.supabase.co, https://ref.supabase.co, or project ref only
    $dbHost = $dbHost -replace '^https?://', ''
    if ($dbHost -match '\.pooler\.supabase\.com$' -or $dbHost -match '^db\.[a-z0-9-]+\.supabase\.co$') {
        # use as-is
    }
    elseif ($dbHost -match '^([a-z0-9]+)\.supabase\.co$') {
        $dbHost = "db.$($Matches[1]).supabase.co"
    }
    elseif ($dbHost -notmatch '\.') {
        $dbHost = "db.$dbHost.supabase.co"
    }

    $port = if ($vars['SUPABASE_PORT']) { $vars['SUPABASE_PORT'] } else { '5432' }
    $database = if ($vars['SUPABASE_DATABASE']) { $vars['SUPABASE_DATABASE'] } else { 'postgres' }
    $username = if ($vars['SUPABASE_USERNAME']) { $vars['SUPABASE_USERNAME'] } else { 'postgres' }

    # Supavisor pooler uses postgres.[project-ref] as username
    if ($dbHost -like '*.pooler.supabase.com' -and $username -eq 'postgres') {
        Write-Warning 'Pooler host detected but SUPABASE_USERNAME=postgres. Use postgres.YOUR_PROJECT_REF from Supabase Connect page.'
    }

    return "Host=$dbHost;Port=$port;Database=$database;Username=$username;Password=$password;SearchPath=$schema;SSL Mode=Require;Trust Server Certificate=true"
}
