<#
.SYNOPSIS
  Fail if any active P0 requirement lacks pass/stub coverage (matrix_status).
  Also rebuilds a summary of open gaps for the reliability loop.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [switch]$UpdateBacklog
)

$ErrorActionPreference = 'Stop'

$reqPath = Join-Path $RepoRoot 'Sessions\quality\requirements.json'
$backlogPath = Join-Path $RepoRoot 'Sessions\quality\gap-backlog.md'
$matrixPath = Join-Path $RepoRoot 'Sessions\quality\ac-matrix-mvp.md'

if (-not (Test-Path $reqPath)) {
  Write-Error "Missing $reqPath — run extract-requirements.ps1 first"
}

$data = Get-Content -Raw -Path $reqPath | ConvertFrom-Json
$open = @($data.requirements | Where-Object {
    $_.active -eq $true -and
    $_.priority -eq 'P0' -and
    $_.matrix_status -notin @('pass', 'stub')
  })

Write-Host ("check-spec-coverage: {0} open P0 requirement(s)" -f $open.Count)
foreach ($o in $open) {
  Write-Host ("  - {0} [{1}] {2}" -f $o.id, $o.matrix_status, $o.text)
}

# Heuristic freeze: P0 `fail` rows in matrix
$freeze = $false
if (Test-Path $matrixPath) {
  $m = Get-Content -Raw -Path $matrixPath
  if ($m -match '\| `fail` \|') {
    $freeze = $true
    Write-Host 'check-spec-coverage: matrix contains `fail` rows — freeze-policy applies'
  }
}

if ($UpdateBacklog) {
  $lines = @(
    '# Gap backlog',
    ("**Updated:** {0}" -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')),
    ("**Open P0:** {0}" -f $open.Count),
    '',
    '| Priority | Gap ID | REQ-ID | Source | Status | Fail ticks | Suggested action |',
    '|---|---|---|---|---|---|---|'
  )
  $i = 1
  foreach ($o in $open) {
    $gapId = if ($o.gap_id) { $o.gap_id } else { "REQ:$($o.id)" }
    $src = if ($o.source -match 'adr') { 'adr' } elseif ($o.source -match 'spec') { 'spec' } else { 'matrix' }
    $lines += ("| {0} | {1} | {2} | {3} | open | 0 | Close via sdlc-loop-tick |" -f $i, $gapId, $o.id, $src)
    $i++
  }
  ($lines -join "`n") + "`n" | Set-Content -Path $backlogPath -Encoding UTF8
  Write-Host "Updated $backlogPath"
}

# Update loop state open_p0_gaps if present
$loopState = Join-Path $RepoRoot 'Sessions\loop\state.md'
if (Test-Path $loopState) {
  $ls = Get-Content -Raw -Path $loopState
  $ls2 = $ls -replace '(?m)^(-\s*open_p0_gaps:\s*)\d+', "`${1}$($open.Count)"
  if ($ls2 -ne $ls) {
    Set-Content -Path $loopState -Value $ls2 -Encoding UTF8
  }
}

if ($open.Count -gt 0) {
  Write-Host 'check-spec-coverage: FAIL'
  exit 1
}

Write-Host 'check-spec-coverage: PASS'
exit 0
