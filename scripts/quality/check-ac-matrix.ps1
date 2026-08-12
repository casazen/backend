<#
.SYNOPSIS
  Verifies design spec (and optional PR body) contain a complete ## AC Test Map,
  and that referenced L1/L2/L3 path tokens exist when they look like file paths.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$DesignPath,
  [string]$PrBodyPath,
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [switch]$SkipPathCheck
)

$ErrorActionPreference = 'Stop'

function Test-LooksLikePath {
  param([string]$Cell)
  if ([string]::IsNullOrWhiteSpace($Cell)) { return $false }
  if ($Cell -match '(?i)^(N/?A|—|-|none|non UI|scaffold)') { return $false }
  if ($Cell -match '`([^`]+)`') { return $true }
  if ($Cell -match '(?i)\.(ts|tsx|cs|yaml|yml|ps1)\b') { return $true }
  if ($Cell -match '(?i)(e2e/|Casazen\.|mobile/|\.maestro/)') { return $true }
  return $false
}

function Get-PathTokens {
  param([string]$Cell)
  $tokens = New-Object System.Collections.Generic.List[string]
  [regex]::Matches($Cell, '`([^`]+)`') | ForEach-Object {
    $tokens.Add($_.Groups[1].Value.Trim())
  }
  if ($tokens.Count -eq 0 -and (Test-LooksLikePath $Cell)) {
    # bare path-ish cell
    $bare = ($Cell -split '\s+|;|,')[0].Trim()
    if ($bare) { $tokens.Add($bare) }
  }
  return $tokens
}

function Resolve-CandidatePaths {
  param([string]$Token)
  $candidates = New-Object System.Collections.Generic.List[string]
  $token = $Token -replace '^\./', ''
  $candidates.Add((Join-Path $RepoRoot $token))
  $candidates.Add((Join-Path (Split-Path $RepoRoot -Parent) $token)) # sibling monorepo root
  $candidates.Add((Join-Path (Split-Path $RepoRoot -Parent) ('frontend\' + ($token -replace '^e2e/', 'e2e/'))))
  if ($token -match '^e2e/') {
    $candidates.Add((Join-Path (Split-Path $RepoRoot -Parent) ('frontend\' + $token.Replace('/', '\'))))
  }
  if ($token -match '^mobile/') {
    $candidates.Add((Join-Path (Split-Path $RepoRoot -Parent) ($token.Replace('/', '\'))))
  }
  if ($token -match '^\.\./frontend/') {
    $candidates.Add((Join-Path $RepoRoot ($token.Replace('/', '\'))))
  }
  if ($token -match '^\.\./mobile/') {
    $candidates.Add((Join-Path $RepoRoot ($token.Replace('/', '\'))))
  }
  # Globs: if token contains * treat as pattern under frontend/mobile/backend
  return $candidates
}

function Assert-AcTestMap {
  param([string]$Path, [string]$Label)

  if (-not (Test-Path $Path)) {
    Write-Error "$Label not found: $Path"
  }

  $text = Get-Content -Raw -Path $Path
  if ($text -notmatch '(?m)^##\s+AC Test Map\s*$') {
    Write-Error "$Label missing '## AC Test Map' section"
  }

  $section = ($text -split '(?m)^##\s+AC Test Map\s*$')[1]
  if (-not $section) {
    Write-Error "$Label AC Test Map section empty"
  }

  $next = $section -split '(?m)^##\s+'
  $body = $next[0]
  $rows = @($body -split "`n" | Where-Object { $_ -match '^\|\s*AC\d+' -or $_ -match '^\|.\s*AC\d+' })
  if ($rows.Count -lt 1) {
    $rows = @($body -split "`n" | Where-Object { $_ -match '^\|\s*AC[0-9]' })
  }
  if ($rows.Count -lt 1) {
    Write-Error "$Label AC Test Map has no AC rows (expected | AC1 | ...)"
  }

  $missing = New-Object System.Collections.Generic.List[string]

  foreach ($row in $rows) {
    $cells = @($row.Split('|') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($cells.Count -lt 4) {
      Write-Error "$Label incomplete AC row (need AC, L1, L2, L3 at minimum): $row"
    }
    foreach ($c in $cells) {
      if ([string]::IsNullOrWhiteSpace($c)) {
        Write-Error "$Label empty cell in: $row"
      }
    }

    if (-not $SkipPathCheck) {
      # cells 1..3 are L1 L2 L3 (index 0 = AC id)
      for ($i = 1; $i -le [Math]::Min(3, $cells.Count - 1); $i++) {
        $cell = $cells[$i]
        if (-not (Test-LooksLikePath $cell)) { continue }
        foreach ($token in (Get-PathTokens $cell)) {
          if ($token -match '\*') {
            # glob: ok if any match under repo or siblings
            $parent = Split-Path $RepoRoot -Parent
            $hits = @()
            $hits += @(Get-ChildItem -Path $RepoRoot -Recurse -Filter ($token.Split('/')[-1]) -ErrorAction SilentlyContinue | Select-Object -First 1)
            $fe = Join-Path $parent 'frontend'
            if (Test-Path $fe) {
              $hits += @(Get-ChildItem -Path $fe -Recurse -Filter ($token.Split('/')[-1]) -ErrorAction SilentlyContinue | Select-Object -First 1)
            }
            $mob = Join-Path $parent 'mobile'
            if (Test-Path $mob) {
              $hits += @(Get-ChildItem -Path $mob -Recurse -Filter ($token.Split('/')[-1]) -ErrorAction SilentlyContinue | Select-Object -First 1)
            }
            if ($hits.Count -lt 1) {
              $missing.Add("$Label AC path glob not found: $token (row: $($cells[0]))")
            }
            continue
          }
          $found = $false
          foreach ($cand in (Resolve-CandidatePaths $token)) {
            if (Test-Path -LiteralPath $cand) { $found = $true; break }
          }
          if (-not $found) {
            $missing.Add("$Label AC path not found: $token (row: $($cells[0]))")
          }
        }
      }
    }
  }

  if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Host $_ }
    Write-Error "$Label AC Test Map path check failed ($($missing.Count) missing)"
  }

  Write-Host ("OK {0} - {1} AC row(s)" -f $Label, $rows.Count)
}

Assert-AcTestMap -Path $DesignPath -Label 'Design'

if ($PrBodyPath) {
  Assert-AcTestMap -Path $PrBodyPath -Label 'PR body'
}

Write-Host 'check-ac-matrix: PASS'
exit 0
