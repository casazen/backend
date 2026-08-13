<#
.SYNOPSIS
  Fail when AC Test Map / specs claim coverage that tests do not exercise.

  Stage 02: -SpecPath  → every ACn has Verifiable Outcome; export ACs have format criteria.
  Stage 03/04: -DesignPath -RequireTests → each UI AC has a titled test in L2 and L3;
               one L3 smoke cannot cover many ACs; export ACs need content/download asserts.

.EXAMPLE
  .\scripts\quality\check-ac-depth.ps1 -SpecPath Sessions\specs\spec-regime-fiscale-2026.md
  .\scripts\quality\check-ac-depth.ps1 -DesignPath Sessions\design-3.md -RequireTests
#>
param(
  [string]$DesignPath,
  [string]$SpecPath,
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [switch]$RequireTests,
  [switch]$RequireSpecOutcomes
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]

function Add-Fail([string]$msg) {
  $failures.Add($msg)
  Write-Host "FAIL: $msg"
}

function Get-RepoParent {
  # PowerShell Split-Path on single-segment roots like /workspace can return "".
  $parent = Split-Path $RepoRoot -Parent
  if ([string]::IsNullOrWhiteSpace($parent)) {
    try {
      $parent = [System.IO.Directory]::GetParent($RepoRoot)?.FullName
    } catch {
      $parent = $null
    }
  }
  if ([string]::IsNullOrWhiteSpace($parent)) { $parent = [System.IO.Path]::DirectorySeparatorChar.ToString() }
  return $parent
}

function Get-FrontendRoot {
  $parent = Get-RepoParent
  $candidates = @(
    (Join-Path $parent 'frontend'),
    (Join-Path $RepoRoot 'frontend'),
    '/frontend',
    '/tmp/casazen-frontend'
  )
  foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
  }
  return $null
}

function Resolve-TestFile([string]$Token) {
  $token = ($Token -replace '^`|`$', '').Trim()
  if ([string]::IsNullOrWhiteSpace($token)) { return $null }
  if ($token -match '(?i)^(N/?A|—|-|none)') { return $null }
  $parent = Get-RepoParent
  $cands = @(
    (Join-Path $RepoRoot $token),
    (Join-Path $RepoRoot ($token.Replace('/', '\'))),
    (Join-Path $parent $token)
  )
  $fe = Get-FrontendRoot
  if ($fe) {
    if ($token -match '^e2e/') {
      $cands += (Join-Path $fe ($token.Replace('/', '\')))
    }
    $cands += (Join-Path $fe $token)
    $cands += (Join-Path $fe ($token.Replace('/', '\')))
  }
  foreach ($c in $cands) {
    if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
  }
  return $null
}

function Get-AcIdsFromTestTitles([string]$FilePath) {
  if (-not $FilePath -or -not (Test-Path $FilePath)) { return @() }
  $text = Get-Content -Raw -Path $FilePath
  $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  # test('AC1: ...') / test("AC2 ...") / test(`AC3`) / it('AC4')
  [regex]::Matches($text, "(?im)(?:test|it)\s*\(\s*['""`]([^'""`]+)['""`]") | ForEach-Object {
    $title = $_.Groups[1].Value
    [regex]::Matches($title, '(?i)\bAC(\d+)\b') | ForEach-Object {
      [void]$ids.Add(('AC{0}' -f $_.Groups[1].Value))
    }
  }
  return @($ids)
}

function Test-ExportAsserts([string]$FilePath) {
  if (-not $FilePath -or -not (Test-Path $FilePath)) { return $false }
  $text = Get-Content -Raw -Path $FilePath
  return [bool]($text -match '(?i)(format=csv|format=pdf|text/csv|application/pdf|download|saveAs|content-disposition|ReadAsByteArray|ReadAsStringAsync.*csv|pdf|%PDF|packLabel|ContentType)')
}

function Test-VacuousOnlyVisibility([string]$FilePath, [string]$AcId) {
  if (-not $FilePath -or -not (Test-Path $FilePath)) { return $false }
  $text = Get-Content -Raw -Path $FilePath
  # Find test blocks whose title mentions this AC (rough slice)
  $pattern = "(?is)(?:test|it)\s*\(\s*['""`][^'""`]*\b$([regex]::Escape($AcId))\b[^'""`]*['""`]\s*,\s*async\s*\([^)]*\)\s*=>\s*\{(.*?)\n\s*\}\)"
  $m = [regex]::Match($text, $pattern)
  if (-not $m.Success) { return $false }
  $body = $m.Groups[1].Value
  $hasVisible = $body -match 'toBeVisible|toBeInTheDocument'
  $hasStrong = $body -match '(?i)(toHaveText|toContainText|toHaveValue|toHaveURL|download|expect\(.*status|toBe\(|toEqual|toHaveCount|fill\(|click\()'
  # Vacuous if only visibility and no stronger assert / interaction
  return ($hasVisible -and -not $hasStrong)
}

# ── Spec outcomes (Stage 02 / template gate) ─────────────────────────────
if ($SpecPath -or $RequireSpecOutcomes) {
  if (-not $SpecPath) { Write-Error '-SpecPath required with -RequireSpecOutcomes' }
  if (-not (Test-Path $SpecPath)) { Write-Error "Spec not found: $SpecPath" }
  $spec = Get-Content -Raw -Path $SpecPath

  if ($spec -notmatch '(?m)^##\s+Verifiable Outcomes\s*$') {
    Add-Fail "Spec missing '## Verifiable Outcomes' (required for impeccable process)"
  }
  else {
    $vo = ($spec -split '(?m)^##\s+Verifiable Outcomes\s*$')[1]
    $voBody = ($vo -split '(?m)^##\s+')[0]
    $acMentions = [regex]::Matches($spec, '(?m)^\s*[-*]\s*\*\*(AC\d+[a-z]?)\b') | ForEach-Object { $_.Groups[1].Value }
    $acMentions = @($acMentions | Select-Object -Unique)
    foreach ($ac in $acMentions) {
      if ($voBody -notmatch ('(?i)\b{0}\b' -f [regex]::Escape($ac))) {
        Add-Fail "Verifiable Outcomes missing row/bullet for $ac"
      }
    }
  }

  # Export AC quality: AC text mentions csv/pdf/export/report → need ## Export / Report Criteria or VO detail
  $exportAc = [regex]::Matches($spec, '(?im)^\s*[-*]\s*\*\*(AC\d+[a-z]?)\b[^\n]{0,200}\b(csv|pdf|excel|xlsx|export|report|commercialista)\b') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
  if ($exportAc.Count -gt 0) {
    if ($spec -notmatch '(?m)^##\s+Export\s*/\s*Report Criteria\s*$' -and $spec -notmatch '(?m)^##\s+Export / Report Criteria\s*$') {
      Add-Fail "Spec has export/report ACs ($($exportAc -join ', ')) but missing '## Export / Report Criteria'"
    }
  }

  # UX ACs: Frontend section (any ### Frontend* heading) requires ## UX / UI Quality
  if ($spec -match '(?m)^###\s+Frontend\b') {
    if ($spec -notmatch '(?m)^##\s+UX\s*/\s*UI Quality\s*$') {
      Add-Fail "Frontend ACs present but missing '## UX / UI Quality' (testable UX bar)"
    }
  }

  Write-Host "check-ac-depth (spec): scanned $SpecPath"
}

# ── Design test depth (Stage 03/04) ──────────────────────────────────────
if ($RequireTests) {
  if (-not $DesignPath) { Write-Error '-DesignPath required with -RequireTests' }
  if (-not (Test-Path $DesignPath)) { Write-Error "Design not found: $DesignPath" }

  $design = Get-Content -Raw -Path $DesignPath
  if ($design -notmatch '(?m)^##\s+AC Test Map\s*$') {
    Add-Fail "Design missing ## AC Test Map"
  }
  else {
    $section = ($design -split '(?m)^##\s+AC Test Map\s*$')[1]
    $body = ($section -split '(?m)^##\s+')[0]
    $rows = @($body -split "`n" | Where-Object { $_ -match '^\|\s*AC\d+' })

    # file -> list of ACs claiming it for L3
    $l3Claims = @{}
    $l2Claims = @{}
    $uiAcCount = 0

    foreach ($row in $rows) {
      $cells = @($row.Split('|') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
      if ($cells.Count -lt 5) { continue }
      $ac = $cells[0] -replace '\s+', ''
      if ($ac -notmatch '^AC\d+$') { continue }

      # Stage 02 map: AC | REQ | L1 | L2 | L3 | Seed
      $l1 = $cells[2]
      $l2 = $cells[3]
      $l3 = $cells[4]
      if ($cells.Count -ge 2 -and $cells[1] -match '(?i)SPEC:|ADR-') {
        # already aligned
      }
      elseif ($cells.Count -ge 4 -and $cells[1] -notmatch '(?i)SPEC:|ADR-') {
        # PR map: AC | L1 | L2 | L3 | Status
        $l1 = $cells[1]; $l2 = $cells[2]; $l3 = $cells[3]
      }

      $isNonUi = ($l2 -match '(?i)N/?A') -and ($l3 -match '(?i)N/?A')
      if ($isNonUi) {
        # L1 must still look like a path for non-UI
        if ($l1 -match '(?i)N/?A') {
          Add-Fail "$ac non-UI row has N/A L1 - backend AC needs L1 path"
        }
        continue
      }

      $uiAcCount++
      foreach ($pair in @(@{Layer='L2'; Cell=$l2; Bag=$l2Claims}, @{Layer='L3'; Cell=$l3; Bag=$l3Claims})) {
        if ($pair.Cell -match '(?i)N/?A') {
          Add-Fail "$ac UI AC missing $($pair.Layer) path (N/A not allowed for UI)"
          continue
        }
        $path = Resolve-TestFile $pair.Cell
        if (-not $path) {
          Add-Fail "$ac $($pair.Layer) file not found: $($pair.Cell)"
          continue
        }
        if (-not $pair.Bag.ContainsKey($path)) { $pair.Bag[$path] = New-Object System.Collections.Generic.List[string] }
        $pair.Bag[$path].Add($ac)

        $titled = Get-AcIdsFromTestTitles $path
        if ($titled -notcontains $ac) {
          Add-Fail ('{0}: no titled test/it containing {0} in {1} file {2}' -f $ac, $pair.Layer, (Split-Path $path -Leaf))
        }
      }

      # Export depth: if AC seed/row or design mentions csv/pdf for this AC
      $rowLower = $row.ToLowerInvariant()
      $isExport = $rowLower -match 'csv|pdf|export|report|withholding|annual'
      if ($isExport -and $ac -match 'AC(8|9|10)\b') {
        $l1Path = Resolve-TestFile $l1
        $l3Path = Resolve-TestFile $l3
        $ok = (Test-ExportAsserts $l1Path) -or (Test-ExportAsserts $l3Path)
        if (-not $ok) {
          Add-Fail "$ac export/report: L1 or L3 must assert CSV/PDF content, download, or Content-Type (visibility-only FAIL)"
        }
      }
    }

    foreach ($path in $l3Claims.Keys) {
      $claimed = @($l3Claims[$path] | Select-Object -Unique)
      $titled = @(Get-AcIdsFromTestTitles $path)
      $covered = @($claimed | Where-Object { $titled -contains $_ })
      if ($claimed.Count -gt 1 -and $titled.Count -lt $claimed.Count) {
        Add-Fail ("L3 file {0} claimed by {1} ACs ({2}) but only {3} titled AC test(s): {4}" -f `
          (Split-Path $path -Leaf), $claimed.Count, ($claimed -join ','), $titled.Count, ($titled -join ','))
      }
      if ($claimed.Count -ge 3 -and $titled.Count -eq 1) {
        Add-Fail ("Vacuous L3: {0} maps {1} ACs onto a single smoke test" -f (Split-Path $path -Leaf), $claimed.Count)
      }
    }

    Write-Host ("check-ac-depth (tests): {0} UI AC row(s), {1} L3 file(s)" -f $uiAcCount, $l3Claims.Count)
  }
}

if (-not $SpecPath -and -not $RequireTests -and -not $RequireSpecOutcomes) {
  Write-Error 'Specify -SpecPath and/or -DesignPath -RequireTests'
}

if ($failures.Count -gt 0) {
  Write-Host ("check-ac-depth: FAIL ({0})" -f $failures.Count)
  exit 1
}

Write-Host 'check-ac-depth: PASS'
exit 0
