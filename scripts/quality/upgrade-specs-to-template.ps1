<#
.SYNOPSIS
  Upgrade all Sessions/specs/spec-*.md to _TEMPLATE.md gate compliance.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [switch]$ForceRebuildVo
)

$ErrorActionPreference = 'Stop'
$specsDir = Join-Path $RepoRoot 'Sessions\specs'
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$updated = 0
$skipped = 0

function Get-AcIdList([string]$text) {
  $list = New-Object 'System.Collections.Generic.List[string]'
  $matches = [regex]::Matches($text, '(?m)^\s*[-*]\s*\*\*(AC\d+[a-z]?)\b')
  foreach ($m in $matches) {
    $id = [string]$m.Groups[1].Value
    if (-not $list.Contains($id)) { [void]$list.Add($id) }
  }
  $arr = New-Object string[] $list.Count
  for ($i = 0; $i -lt $list.Count; $i++) { $arr[$i] = $list[$i] }
  return $arr
}

function Remove-Section([string]$raw, [string]$headingRegex) {
  # Remove from heading through next ## heading (exclusive) or EOF
  $m = [regex]::Match($raw, ('(?ms)({0}.*?)(?=\n##\s+|\z)' -f $headingRegex))
  if (-not $m.Success) { return $raw }
  return $raw.Remove($m.Index, $m.Length)
}

function Insert-BeforeHeading([string]$raw, [string]$headingRegex, [string]$block) {
  $m = [regex]::Match($raw, $headingRegex)
  if ($m.Success) {
    return $raw.Substring(0, $m.Index) + $block.TrimEnd() + "`n`n---`n`n" + $raw.Substring($m.Index)
  }
  return $null
}

foreach ($file in (Get-ChildItem -Path $specsDir -Filter 'spec-*.md')) {
  $path = $file.FullName
  $name = $file.Name
  $raw = [System.IO.File]::ReadAllText($path)
  $raw = $raw.Replace("`r`n", "`n").Replace("`r", "`n")

  $acs = Get-AcIdList $raw
  if ($null -eq $acs) { $acs = @() }
  $acCount = @($acs).Length

  $hasFe = [bool]($raw -match '(?m)^###\s+Frontend\b')
  $hasExport = [bool]($raw -match '(?im)^\s*[-*]\s*\*\*AC\d+[a-z]?\b[^\n]{0,220}\b(csv|pdf|excel|xlsx|export|report|commercialista|ical|text/calendar)\b')
  $slug = $name -replace '^spec-', '' -replace '\.md$', ''
  if ($raw -match '(?m)^slug:\s*(\S+)') { $slug = [string]$Matches[1] }

  $changed = $false

  if ($raw -match '(?m)^last_reviewed:\s*') {
    $nr = [regex]::Replace($raw, '(?m)^last_reviewed:\s*.*$', ('last_reviewed: ' + $today))
    if ($nr -ne $raw) { $raw = $nr; $changed = $true }
  }

  if ($raw -notmatch 'Sessions/specs/_TEMPLATE\.md') {
    $nr = [regex]::Replace(
      $raw,
      '(?m)^(#[^\n]+)\n',
      ('$1' + "`n`n> Template contract: ``Sessions/specs/_TEMPLATE.md``. Validated by Stage 02 G9b (``check-ac-depth.ps1 -SpecPath``).`n"),
      1
    )
    if ($nr -ne $raw) { $raw = $nr; $changed = $true }
  }

  # Rebuild VO if missing or incomplete (previous botched run wrote 1 row)
  $voRowCount = 0
  if ($raw -match '(?m)^##\s+Verifiable Outcomes\s*$') {
    $voBody = ([regex]::Split($raw, '(?m)^##\s+Verifiable Outcomes\s*$')[1])
    $voBody = ([regex]::Split($voBody, '(?m)^##\s+')[0])
    $voRowCount = @([regex]::Matches($voBody, '(?m)^\|\s*AC\d+')).Count
  }
  $needVo = ($acCount -gt 0) -and (($voRowCount -lt $acCount) -or $ForceRebuildVo -or ($raw -notmatch '(?m)^##\s+Verifiable Outcomes\s*$'))
  $needStubVo = ($acCount -eq 0) -and ($raw -notmatch '(?m)^##\s+Verifiable Outcomes\s*$')

  if ($needStubVo) {
    $stub = @(
      '## Verifiable Outcomes',
      '',
      '| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |',
      '|---|---|---|---|',
      '| (none numbered) | L1 | Spec has no numbered ACn bullets - add them before Stage 02 or keep deferred | Missing ACs |',
      ''
    ) -join "`n"
    $raw = Remove-Section $raw '(?m)^##\s+Verifiable Outcomes\s*$'
    $ins = Insert-BeforeHeading $raw '(?m)^##\s+Technical Notes\s*$' $stub
    if ($null -ne $ins) { $raw = $ins } else { $raw = $raw.TrimEnd() + "`n`n" + $stub }
    $changed = $true
  }
  elseif ($needVo) {
    $raw = Remove-Section $raw '(?m)^##\s+Verifiable Outcomes\s*$'

    $feSection = ''
    $fm = [regex]::Match($raw, '(?s)(###\s+Frontend\b.*?)(?=\n###\s|\n##\s|$)')
    if ($fm.Success) { $feSection = [string]$fm.Groups[1].Value }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('## Verifiable Outcomes')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |')
    [void]$sb.AppendLine('|---|---|---|---|')

    foreach ($acObj in @($acs)) {
      $ac = [string]$acObj
      $pat = '(?m)^\s*[-*]\s*\*\*' + [regex]::Escape($ac) + '\b\*\*\s*:?\s*(.+)$'
      $sm = [regex]::Match($raw, $pat)
      $snip = 'See Acceptance Criteria.'
      if ($sm.Success) { $snip = [string]$sm.Groups[1].Value.Trim() }
      if ($snip.Length -gt 140) { $snip = $snip.Substring(0, 137) + '...' }
      $snip = $snip.Replace('|', '/')

      $isUi = $false
      $needle = '**' + $ac + '**'
      if ($feSection.Length -gt 0 -and $feSection.Contains($needle)) { $isUi = $true }
      if ($snip -match '(?i)(e2e|Playwright|Maestro|golden.journey|UI|page|wizard|CTA|Italian|inbox|screen|app host|mobile)') {
        $isUi = $true
      }

      $layer = 'L1'
      if ($isUi) { $layer = 'L2 + L3' }
      if ($snip -match '(?i)(GET |POST |PUT |PATCH |DELETE |entity|OrgId|Hangfire|endpoint|migration)') {
        if ($isUi) { $layer = 'L1 + L2 + L3' }
      }

      $fail = 'Outcome not met; wrong status; silent no-op'
      if ($isUi) { $fail = 'Missing Italian CTA; blank empty state; flow dead-end; visibility-only' }

      [void]$sb.AppendLine('| ' + $ac + ' | ' + $layer + ' | ' + $snip + ' | ' + $fail + ' |')
    }

    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('Rules:')
    [void]$sb.AppendLine('- UI ACs need L2 **and** L3 outcomes (titled tests per AC).')
    [void]$sb.AppendLine('- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).')
    [void]$sb.AppendLine('- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.')
    [void]$sb.AppendLine('')
    $vo = $sb.ToString()

    $ins = Insert-BeforeHeading $raw '(?m)^##\s+Technical Notes\s*$' $vo
    if ($null -eq $ins) { $ins = Insert-BeforeHeading $raw '(?m)^##\s+Out of Scope\s*$' $vo }
    if ($null -ne $ins) { $raw = $ins } else { $raw = $raw.TrimEnd() + "`n`n" + $vo }
    $changed = $true
  }

  if ($hasFe -and $raw -notmatch '(?m)^##\s+UX\s*/\s*UI Quality\s*$') {
    $ux = @"
## UX / UI Quality

**Required** (Frontend ACs present). Testable bar for Stage 03.

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | User completes happy path without guessing | L3 scripted flow below |
| Language | End-user strings Italian | L2/L3 assert Italian primary labels |
| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |
| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |
| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |

**Happy-path script:**

1. Enter the primary route for ``$slug``
2. Complete the main user action defined in Acceptance Criteria
3. Done when the Verifiable Outcome for the primary AC holds

"@
    $ins = Insert-BeforeHeading $raw '(?m)^##\s+Technical Notes\s*$' $ux
    if ($null -ne $ins) { $raw = $ins } else { $raw = $raw.TrimEnd() + "`n`n" + $ux }
    $changed = $true
  }

  if ($hasExport -and $raw -notmatch '(?m)^##\s+Export\s*/\s*Report Criteria\s*$') {
    $ex = @"
## Export / Report Criteria

**Required** (export / feed / report ACs present).

### Feed / file

| Requirement | Required |
|---|---|
| Declared Content-Type matches payload (e.g. text/calendar, text/csv, application/pdf) | yes |
| Non-empty body when seed data exists | yes |
| No CF / P.IVA / secrets in filename or URL | yes |
| Documented columns/fields or VEVENT shape in AC / design | yes |

### PDF (when applicable)

| Requirement | Required |
|---|---|
| Real PDF bytes (%PDF) - not empty stub | yes |
| Readable labeled content for the intended audience | yes |

"@
    $ins = Insert-BeforeHeading $raw '(?m)^##\s+Technical Notes\s*$' $ex
    if ($null -ne $ins) { $raw = $ins } else { $raw = $raw.TrimEnd() + "`n`n" + $ex }
    $changed = $true
  }

  if ($raw -notmatch '(?m)^##\s+Test expectations') {
    $te = @"
## Test expectations (process contract)

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |
| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |
| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |

Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

"@
    $ins = Insert-BeforeHeading $raw '(?m)^##\s+Regulatory' $te
    if ($null -eq $ins) { $ins = Insert-BeforeHeading $raw '(?m)^##\s+Out of Scope\s*$' $te }
    if ($null -ne $ins) { $raw = $ins } else { $raw = $raw.TrimEnd() + "`n`n" + $te }
    $changed = $true
  }

  if ($raw -notmatch '(?m)^##\s+Regulatory') {
    $raw = $raw.TrimEnd() + "`n`n## Regulatory / Legal Gates`n`n- None`n"
    $changed = $true
  }
  if ($raw -notmatch '(?m)^##\s+Out of Scope') {
    $raw = $raw.TrimEnd() + "`n`n## Out of Scope`n`n- See Acceptance Criteria non-goals / PLANNING freeze list`n"
    $changed = $true
  }
  if ($raw -notmatch '(?m)^##\s+Open Questions') {
    $raw = $raw.TrimEnd() + "`n`n## Open Questions`n`n- None (or list with owner/date before Stage 03)`n"
    $changed = $true
  }

  if (-not $changed) {
    Write-Host ("OK   {0} (ACs={1})" -f $name, $acCount)
    $skipped++
    continue
  }

  [System.IO.File]::WriteAllText($path, $raw.Replace("`n", "`r`n"))
  Write-Host ("UPD  {0} (ACs={1} FE={2} Export={3} voRowsWas={4})" -f $name, $acCount, $hasFe, $hasExport, $voRowCount)
  $updated++
}

Write-Host ("Done. updated={0} skipped={1}" -f $updated, $skipped)
