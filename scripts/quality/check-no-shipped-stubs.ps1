<#
.SYNOPSIS
  Fails if shipped-path stubs / TODO Implement appear outside the allowlist.
#>
param(
  [string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
  [string]$FrontendRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '../frontend'),
  [string]$MobileRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path '../mobile')
)

$ErrorActionPreference = 'Stop'

$AllowList = @(
  'Casazen.Infrastructure\OTA\BookingComAdapter.cs',
  'Casazen.Infrastructure\OTA\ChannelFactory.cs',
  'Casazen.Infrastructure\External\LeaseESignHttpAdapter.cs',
  'Casazen.Infrastructure\Services\NullRentBillingService.cs',
  'Casazen.Infrastructure\Services\PaymentService.cs',
  'Sessions\quality\',
  'scripts\quality\',
  'mobile\app.json',
  '\app.json'
)

$patterns = @(
  'TODO:\s*Implement',
  'stub-session-',
  'NotImplementedException'
)

$failures = New-Object System.Collections.Generic.List[string]

function Test-Allowed([string]$path) {
  $norm = $path -replace '/', '\'
  foreach ($a in $AllowList) {
    if ($norm -like "*$a*") { return $true }
  }
  return $false
}

function Scan-Files([string]$root, [string[]]$extensions) {
  if (-not (Test-Path $root)) { return }
  Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
      $extensions -contains $_.Extension -and
      $_.FullName -notmatch '[\\/](node_modules|bin|obj|\.git|dist|coverage)[\\/]'
    } |
    ForEach-Object {
      if (Test-Allowed $_.FullName) { return }
      try {
        $content = [System.IO.File]::ReadAllText($_.FullName)
      } catch {
        return
      }
      foreach ($p in $patterns) {
        if ($content -match $p) {
          $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
          $failures.Add("$rel :: matched /$p/")
        }
      }
    }
}

Scan-Files $BackendRoot @('.cs')
if (Test-Path $FrontendRoot) {
  Scan-Files (Resolve-Path $FrontendRoot).Path @('.ts', '.tsx')
}
if (Test-Path $MobileRoot) {
  Scan-Files (Resolve-Path $MobileRoot).Path @('.ts', '.tsx')
}

if ($failures.Count -gt 0) {
  Write-Host 'check-no-shipped-stubs: FAIL'
  $failures | Select-Object -Unique | ForEach-Object { Write-Host " - $_" }
  Write-Host ''
  Write-Host 'Allowlisted stubs must be labeled status:stub and excluded from production claims.'
  Write-Host 'See Sessions/quality/ac-matrix-mvp.md'
  exit 1
}

Write-Host 'check-no-shipped-stubs: PASS (allowlisted stubs only)'
exit 0
