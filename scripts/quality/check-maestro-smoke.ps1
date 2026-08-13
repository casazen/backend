<#
.SYNOPSIS
  Structural gate for ADR-003-R6: Maestro smoke launches to the login screen.
  Validates smoke.yaml exists, appId matches mobile app.json, and asserts
  visible strings that actually render on app/login.tsx (no device required).
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$mobileRoot = Join-Path (Split-Path $RepoRoot -Parent) 'mobile'
$smokePath = Join-Path $mobileRoot '.maestro\smoke.yaml'
$loginPath = Join-Path $mobileRoot 'app\login.tsx'
$appJsonPath = Join-Path $mobileRoot 'app.json'

$fail = {
  param([string]$Msg)
  Write-Host ("check-maestro-smoke: FAIL - {0}" -f $Msg)
  exit 1
}

if (-not (Test-Path $smokePath)) { & $fail "Missing $smokePath" }
if (-not (Test-Path $loginPath)) { & $fail "Missing $loginPath" }
if (-not (Test-Path $appJsonPath)) { & $fail "Missing $appJsonPath" }

$smoke = Get-Content -Raw -Path $smokePath
$login = Get-Content -Raw -Path $loginPath
$appJson = Get-Content -Raw -Path $appJsonPath | ConvertFrom-Json

if ($smoke -notmatch '(?m)^-\s*launchApp\s*$') {
  & $fail 'smoke.yaml must launchApp'
}

$androidId = [string]$appJson.expo.android.package
$iosId = [string]$appJson.expo.ios.bundleIdentifier
if (-not $androidId) { & $fail 'app.json missing expo.android.package' }

if ($smoke -notmatch ("(?m)^appId:\s*{0}\s*$" -f [regex]::Escape($androidId))) {
  & $fail ("smoke.yaml appId must be '{0}' (android package); ios bundle={1}" -f $androidId, $iosId)
}

# Collect assertVisible strings from smoke
$asserts = [regex]::Matches($smoke, 'assertVisible:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
if ($asserts.Count -lt 1) {
  & $fail 'smoke.yaml must assertVisible at least one login-screen string'
}

foreach ($a in $asserts) {
  # Prefer exact text present in login.tsx source (visible UI, not hidden header titles)
  if ($login -notmatch [regex]::Escape($a)) {
    & $fail ("assertVisible '{0}' not found in app/login.tsx visible UI source" -f $a)
  }
}

# Must not only assert Accedi while header is hidden
if ($asserts.Count -eq 1 -and $asserts[0] -eq 'Accedi' -and $login -match 'headerShown:\s*false') {
  & $fail 'assertVisible Accedi is insufficient when login header is hidden'
}

Write-Host ("check-maestro-smoke: PASS ({0} asserts, appId={1})" -f $asserts.Count, $androidId)
exit 0
