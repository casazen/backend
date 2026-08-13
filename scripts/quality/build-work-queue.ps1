<#
.SYNOPSIS
  Build Sessions/loop/work-queue.md (+ work-queue.json) for the delivery loop.

.DESCRIPTION
  Merges sticky running pipelines, open P0 gaps, and planned/in-dev specs.
  Optionally enriches with gh issue list. Supports -DryRunPick (print top only).
#>
param(
  [string]$RepoRoot = '',
  [switch]$SkipGh,
  [switch]$DryRunPick,
  [switch]$ApplyGoal,
  [switch]$GapsOnly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
  $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  $RepoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
}

$now = (Get-Date).ToUniversalTime().ToString('o')
$loopDir = Join-Path $RepoRoot 'Sessions\loop'
if (-not (Test-Path $loopDir)) {
  New-Item -ItemType Directory -Path $loopDir -Force | Out-Null
}

$items = New-Object System.Collections.Generic.List[object]

function Add-Item {
  param(
    [string]$WorkId,
    [string]$Kind,
    [string]$Source,
    [string]$Status,
    [string]$Notes = '',
    [string]$ReqId = ''
  )
  $script:items.Add([pscustomobject]@{
      work_id = $WorkId
      kind    = $Kind
      source  = $Source
      status  = $Status
      notes   = $Notes
      req_id  = $ReqId
    }) | Out-Null
}

# Prefer sticky from delivery-state if present
$stickyPrefer = $null
$deliveryState = Join-Path $loopDir 'delivery-state.md'
if (Test-Path $deliveryState) {
  $ds = Get-Content -Raw -Path $deliveryState
  if ($ds -match '(?m)^-\s*sticky_pipeline:\s*(\S+)') {
    $val = $Matches[1]
    if ($val -ne '(none)') { $stickyPrefer = $val }
  }
}

# A - Sticky pipelines
$pipelineDirs = Get-ChildItem -Path (Join-Path $RepoRoot 'Sessions') -Directory -Filter 'pipeline-*' -ErrorAction SilentlyContinue
$stickyCandidates = @()
foreach ($dir in $pipelineDirs) {
  $statePath = Join-Path $dir.FullName 'state.md'
  if (-not (Test-Path $statePath)) { continue }
  $raw = Get-Content -Raw -Path $statePath
  if ($raw -notmatch '(?m)^-\s*status:\s*running\b') { continue }
  $slug = $dir.Name -replace '^pipeline-', ''
  $stage = 'unknown'
  if ($raw -match '(?m)^-\s*current_stage:\s*(\S+)') { $stage = $Matches[1] }
  $updated = $dir.LastWriteTimeUtc
  if ($raw -match '(?m)^-\s*last_updated:\s*(\S+)') {
    try { $updated = [datetime]::Parse($Matches[1]).ToUniversalTime() } catch { }
  }
  $stickyCandidates += [pscustomobject]@{
    slug    = $slug
    stage   = $stage
    updated = $updated
  }
}

if ($stickyCandidates.Count -gt 0) {
  $chosen = $null
  if ($stickyPrefer) {
    $chosen = $stickyCandidates | Where-Object { $_.slug -eq $stickyPrefer } | Select-Object -First 1
  }
  if (-not $chosen) {
    $chosen = $stickyCandidates | Sort-Object updated -Descending | Select-Object -First 1
  }
  Add-Item -WorkId ("sticky:{0}" -f $chosen.slug) -Kind 'feature_stage' -Source 'pipeline' -Status 'running' -Notes ("Stage {0}" -f $chosen.stage)
}

# B - P0 gaps
$gapPath = Join-Path $RepoRoot 'Sessions\quality\gap-backlog.md'
if (Test-Path $gapPath) {
  $gapLines = Get-Content -Path $gapPath
  foreach ($line in $gapLines) {
    if ($line -notmatch '^\|\s*\d+\s*\|') { continue }
    $cols = $line.Trim('|').Split('|') | ForEach-Object { $_.Trim() }
    if ($cols.Count -lt 5) { continue }
    $gapId = $cols[1]
    $reqId = $cols[2]
    $source = $cols[3]
    $status = $cols[4]
    if ($status -ne 'open') { continue }
    if ($gapId -eq 'Gap ID') { continue }
    Add-Item -WorkId $gapId -Kind 'gap' -Source 'gap-backlog' -Status 'open' -Notes $source -ReqId $reqId
  }
}

# C - Features from specs README (planned / in-dev)
$specsReadme = Join-Path $RepoRoot 'Sessions\specs\README.md'
$featureSlugs = @{}
if (Test-Path $specsReadme) {
  $inRegistry = $false
  foreach ($line in Get-Content -Path $specsReadme) {
    if ($line -match '^## Registry') { $inRegistry = $true; continue }
    if ($inRegistry -and $line -match '^## ') { break }
    if (-not $inRegistry) { continue }
    if ($line -notmatch '^\|') { continue }
    if ($line -match '^\|\s*[-:| ]+\|') { continue }
    if ($line -match '^\|\s*ID\s*\|') { continue }
    $cols = $line.Trim('|').Split('|') | ForEach-Object { $_.Trim() }
    # Registry tables vary: find slug + status + issue columns by content
    $slug = $null
    $status = $null
    $issue = $null
    foreach ($c in $cols) {
      if ($c -match '^`([a-z0-9-]+)`$') { $slug = $Matches[1]; continue }
      if ($c -match '^(planned|in-dev)$') { $status = $Matches[1]; continue }
      if ($c -match '\[#(\d+)\]') { $issue = $Matches[1]; continue }
      if ($c -match '^#(\d+)$') { $issue = $Matches[1]; continue }
    }
    if (-not $slug -or -not $status) { continue }
    if ($featureSlugs.ContainsKey($slug)) { continue }
    # Skip if sticky already covers this slug
    $stickyId = "sticky:$slug"
    if ($items | Where-Object { $_.work_id -eq $stickyId }) { continue }
    $notes = if ($issue) { "#$issue" } else { '' }
    $workId = if ($issue) { "SPEC:$slug" } else { "SPEC:$slug" }
    Add-Item -WorkId $workId -Kind 'feature' -Source 'specs+gh' -Status $status -Notes $notes
    $featureSlugs[$slug] = $true
  }
}

# Optional gh enrichment (open issues not already represented)
if (-not $SkipGh) {
  try {
    $ghJson = & gh issue list --state open --limit 50 --json number,title,labels 2>$null
    if ($LASTEXITCODE -eq 0 -and $ghJson) {
      $issues = $ghJson | ConvertFrom-Json
      foreach ($iss in $issues) {
        $num = [string]$iss.number
        $already = $false
        foreach ($it in $items) {
          if ($it.notes -eq "#$num" -or $it.work_id -eq "#$num") { $already = $true; break }
        }
        if ($already) { continue }
        # Only add issues labeled enhancement/feat that look delivery-related; keep queue focused
        $labels = @($iss.labels | ForEach-Object { $_.name })
        $isFeat = ($labels -contains 'enhancement') -or ($labels -contains 'feat') -or ($iss.title -match '^(feat|feature)[:\[]')
        if (-not $isFeat) { continue }
        Add-Item -WorkId "#$num" -Kind 'feature' -Source 'gh-issue' -Status 'open' -Notes $iss.title
      }
    }
  }
  catch {
    Write-Host "build-work-queue: gh issue list skipped ($($_.Exception.Message))"
  }
}

# Optional GapsOnly / ApplyGoal filters (before priority assignment)
$filtered = [System.Collections.Generic.List[object]]::new()
foreach ($it in $items) { $filtered.Add($it) | Out-Null }
if ($GapsOnly) {
  $tmp = [System.Collections.Generic.List[object]]::new()
  foreach ($it in $filtered) {
    if ($it.kind -eq 'gap') { $tmp.Add($it) | Out-Null }
  }
  $filtered = $tmp
}

$include = @()
$exclude = @()
if ($ApplyGoal) {
  $goalPath = Join-Path $loopDir 'goal.md'
  if (Test-Path $goalPath) {
    $goalRaw = Get-Content -Raw -Path $goalPath
    if ($goalRaw -match '(?m)^-\s*include:\s*\[([^\]]*)\]') {
      $include = @($Matches[1].Split(',') | ForEach-Object { $_.Trim().Trim("'").Trim('"') } | Where-Object { $_ })
    }
    if ($goalRaw -match '(?m)^-\s*exclude:\s*\[([^\]]*)\]') {
      $exclude = @($Matches[1].Split(',') | ForEach-Object { $_.Trim().Trim("'").Trim('"') } | Where-Object { $_ })
    }
  }

  # Force-add GH issues listed in goal include (e.g. #3) even without enhancement/feat labels
  if (-not $SkipGh -and $include.Count -gt 0) {
    foreach ($inc in $include) {
      if ($inc -notmatch '^#?(\d+)$') { continue }
      $num = $Matches[1]
      $wid = "#$num"
      $exists = $false
      foreach ($it in $filtered) {
        if ($it.work_id -eq $wid -or $it.notes -eq $wid) { $exists = $true; break }
      }
      if ($exists) { continue }
      try {
        $issJson = & gh issue view $num --json number,title,state 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $issJson) { continue }
        $iss = $issJson | ConvertFrom-Json
        if ($iss.state -ne 'OPEN') { continue }
        Add-Item -WorkId $wid -Kind 'feature' -Source 'gh-issue-goal' -Status 'open' -Notes ([string]$iss.title)
        $filtered.Add($items[-1]) | Out-Null
      }
      catch {
        Write-Host ("build-work-queue: could not load issue #{0}: {1}" -f $num, $_.Exception.Message)
      }
    }
  }
  if ($exclude.Count -gt 0) {
    $tmp = [System.Collections.Generic.List[object]]::new()
    foreach ($it in $filtered) {
      $id = [string]$it.work_id
      $blocked = $false
      foreach ($ex in $exclude) {
        if ($id -eq $ex -or $id.StartsWith($ex)) { $blocked = $true; break }
      }
      if (-not $blocked) { $tmp.Add($it) | Out-Null }
    }
    $filtered = $tmp
  }
  if ($include.Count -gt 0) {
    function Test-GoalTokenMatch {
      param([string]$Token, [string]$WorkId, [string]$Notes)
      if ([string]::IsNullOrWhiteSpace($Token)) { return $false }
      # Issue numbers: exact #N only (avoid #3 matching #301)
      if ($Token -match '^#?(\d+)$') {
        $n = $Matches[1]
        $pat = "(^|[^\d])#$n($|[^\d])"
        if ($WorkId -eq "#$n") { return $true }
        if ($Notes -match $pat) { return $true }
        return $false
      }
      if ($WorkId -eq $Token) { return $true }
      if ($WorkId.StartsWith($Token)) { return $true }
      if ($Notes -eq $Token) { return $true }
      return $false
    }
    $tmp = [System.Collections.Generic.List[object]]::new()
    foreach ($it in $filtered) {
      $id = [string]$it.work_id
      $notes = [string]$it.notes
      foreach ($inc in $include) {
        if (Test-GoalTokenMatch -Token $inc -WorkId $id -Notes $notes) {
          $tmp.Add($it) | Out-Null
          break
        }
      }
    }
    $filtered = $tmp
  }
}

# Assign priorities
$priority = 1
$ordered = foreach ($it in $filtered) {
  [pscustomobject]@{
    priority = $priority
    work_id  = $it.work_id
    kind     = $it.kind
    source   = $it.source
    status   = $it.status
    notes    = $it.notes
    req_id   = $it.req_id
  }
  $priority++
}

$mdPath = Join-Path $loopDir 'work-queue.md'
$jsonPath = Join-Path $loopDir 'work-queue.json'

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# Delivery work queue')
[void]$sb.AppendLine("**Updated:** $now")
[void]$sb.AppendLine('**Source:** sdlc-work-queue / build-work-queue.ps1')
[void]$sb.AppendLine("**Open items:** $($ordered.Count)")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Priority | Work ID | Kind | Source | Status | Notes |')
[void]$sb.AppendLine('|---|---|---|---|---|---|')
foreach ($row in $ordered) {
  $notes = $row.notes
  if ($row.req_id) { $notes = if ($notes) { "$($row.req_id); $notes" } else { $row.req_id } }
  [void]$sb.AppendLine("| $($row.priority) | $($row.work_id) | $($row.kind) | $($row.source) | $($row.status) | $notes |")
}
Set-Content -Path $mdPath -Value $sb.ToString() -Encoding utf8

$jsonObj = @{
  updated = $now
  items   = @($ordered | ForEach-Object {
      @{
        priority = $_.priority
        work_id  = $_.work_id
        kind     = $_.kind
        source   = $_.source
        status   = $_.status
        notes    = $_.notes
        req_id   = $_.req_id
      }
    })
}
$jsonObj | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding utf8

Write-Host "build-work-queue: wrote $mdPath ($($ordered.Count) items)"

$top = $ordered | Select-Object -First 1
if ($top) {
  Write-Host ("TOP PICK: work_id={0} kind={1} status={2} notes={3}" -f $top.work_id, $top.kind, $top.status, $top.notes)
}
else {
  Write-Host 'TOP PICK: (empty queue)'
}

if ($DryRunPick) {
  Write-Host 'build-work-queue: DryRunPick - no further work'
}

exit 0
