<#
.SYNOPSIS
  Extract machine-checkable requirements from ADRs into Sessions/quality/requirements.json
  and merge matrix-derived statuses from ac-matrix-mvp.md / gap-backlog.md.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [string]$OutJson = ''
)

$ErrorActionPreference = 'Stop'
if (-not $OutJson) {
  $OutJson = Join-Path $RepoRoot 'Sessions\quality\requirements.json'
}

$adrDir = Join-Path $RepoRoot 'docs\adr'
$matrixPath = Join-Path $RepoRoot 'Sessions\quality\ac-matrix-mvp.md'
$existing = @{}
if (Test-Path $OutJson) {
  try {
    $prev = Get-Content -Raw -Path $OutJson | ConvertFrom-Json
    foreach ($r in $prev.requirements) {
      $existing[$r.id] = $r
    }
  } catch {
    Write-Warning "Could not parse existing requirements.json; regenerating ADR rows only."
  }
}

$requirements = New-Object System.Collections.Generic.List[object]

function Add-Req {
  param($Id, $Source, $Priority, $Text, $MatrixStatus = 'unknown', $GapId = $null, $Active = $true)
  $prev = $existing[$Id]
  $status = if ($prev -and $prev.matrix_status) { [string]$prev.matrix_status } else { $MatrixStatus }
  $obj = [ordered]@{
    id             = $Id
    source         = $Source.Replace('\', '/')
    priority       = $Priority
    text           = $Text
    active         = [bool]$Active
    matrix_status  = $status
  }
  if ($GapId) { $obj.gap_id = $GapId }
  elseif ($prev -and $prev.gap_id) { $obj.gap_id = [string]$prev.gap_id }
  $requirements.Add([pscustomobject]$obj)
}

# --- ADR ## Requirements tables ---
Get-ChildItem -Path $adrDir -Filter 'ADR-*.md' | Where-Object { $_.Name -notmatch 'TEMPLATE' } | ForEach-Object {
  $rel = 'docs/adr/' + $_.Name
  $text = Get-Content -Raw -Path $_.FullName
  if ($text -notmatch '(?ms)^##\s+Requirements\s*\r?\n(.*?)(?=\r?\n##\s|\z)') {
    Write-Warning "No ## Requirements in $($_.Name)"
    return
  }
  $section = $Matches[1]
  foreach ($line in ($section -split "`n")) {
    if ($line -match '^\|\s*(ADR-\d+-R\d+)\s*\|\s*(P\d+)\s*\|\s*(.+?)\s*\|') {
      Add-Req -Id $Matches[1] -Source $rel -Priority $Matches[2] -Text ($Matches[3].Trim())
    }
  }
}

# --- Preserve / re-add SPEC:* rows from previous JSON ---
foreach ($key in $existing.Keys) {
  if ($key -like 'SPEC:*') {
    $r = $existing[$key]
    Add-Req -Id $r.id -Source $r.source -Priority $r.priority -Text $r.text `
      -MatrixStatus $r.matrix_status -GapId $r.gap_id -Active $r.active
  }
}

# --- Heuristic: sync fail/missing-test from matrix into known SPEC gaps ---
if (Test-Path $matrixPath) {
  $matrix = Get-Content -Raw -Path $matrixPath
  $failHints = @(
    @{ Pattern = 'AC4 Calendar'; Id = 'SPEC:native-host-app:AC4'; Status = 'fail' },
    @{ Pattern = 'AC15 Maestro 0 crash'; Id = 'SPEC:native-host-app:AC15'; Status = 'fail' },
    @{ Pattern = 'AC20 Maestro M1'; Id = 'SPEC:native-host-app:AC20'; Status = 'fail' },
    @{ Pattern = 'AC21 BE push'; Id = 'SPEC:native-host-app:AC21'; Status = 'missing-test' },
    @{ Pattern = 'Supplier take/complete'; Id = 'SPEC:micro-marketplace-v0:AC-supplier'; Status = 'fail' },
    @{ Pattern = 'L3 real API loop'; Id = 'SPEC:micro-marketplace-v0:AC-L3'; Status = 'missing-test' },
    @{ Pattern = 'L3 booking create'; Id = 'SPEC:direct-checkout:AC-L3'; Status = 'missing-test' }
  )
  foreach ($h in $failHints) {
    if ($matrix -match [regex]::Escape($h.Pattern)) {
      $found = $requirements | Where-Object { $_.id -eq $h.Id } | Select-Object -First 1
      if ($found) {
        # Do not clobber env/device/repo blocks (Automation must skip non-actionable P0s)
        if ([string]$found.matrix_status -ne 'blocked') {
          $found.matrix_status = $h.Status
        }
      }
    }
  }
}

# Dedupe by id (last write wins for SPEC merge quirks)
$dedup = [ordered]@{}
foreach ($r in $requirements) {
  $dedup[$r.id] = $r
}

$payload = [ordered]@{
  updated      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  requirements = @($dedup.Values)
}

$dir = Split-Path $OutJson -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
($payload | ConvertTo-Json -Depth 6) | Set-Content -Path $OutJson -Encoding UTF8

$openP0 = @($payload.requirements | Where-Object {
    $_.active -and $_.priority -eq 'P0' -and $_.matrix_status -in @('fail', 'missing-test', 'in-progress', 'unknown')
  }).Count

Write-Host ("extract-requirements: {0} rows, open_p0ish={1} -> {2}" -f $payload.requirements.Count, $openP0, $OutJson)
exit 0
