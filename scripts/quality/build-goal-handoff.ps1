<#
.SYNOPSIS
  After a delivery goal completes, build a graphical demo (FE/BE features) or a text report (gaps/other), then optionally POST via notify-human.

.DESCRIPTION
  Reads goal, journal, pipeline, spec, and design. Writes:
    Sessions/loop/goal-handoff.md
    Sessions/loop/goal-handoff.html   (demo only)
    Sessions/loop/last-handoff.json
  Classification:
    demo   - journal has feature_stage AND (frontend routes or backend endpoints in design/spec)
    report - everything else (gaps, empty journal, no FE/BE surface)
  Does not run Playwright or hit staging. Honest about develop vs production.
#>
param(
  [ValidateSet('goal_done', 'queue_empty', 'max_work_units')]
  [string]$Event = 'goal_done',

  [string]$RepoRoot = '',

  [switch]$Notify,

  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
  $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  $RepoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
}

function Read-Utf8File {
  param([string]$Path)
  if (-not (Test-Path $Path)) { return '' }
  $utf8 = New-Object System.Text.UTF8Encoding $false
  return [System.IO.File]::ReadAllText($Path, $utf8)
}

function Write-Utf8File {
  param([string]$Path, [string]$Text)
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Get-MdField {
  param([string]$Text, [string]$Name)
  if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
  $pat = '(?m)^-\s*' + [regex]::Escape($Name) + ':\s*(.+?)\s*$'
  if ($Text -match $pat) {
    $v = $Matches[1].Trim()
    if ($v -eq '(none)' -or $v -eq '(pending)' -or $v -eq '') { return $null }
    return $v
  }
  return $null
}

function ConvertTo-HtmlEnc {
  param([string]$s)
  if ($null -eq $s) { return '' }
  return [System.Net.WebUtility]::HtmlEncode($s)
}

function Mermaid-Label {
  param([string]$s)
  if ([string]::IsNullOrWhiteSpace($s)) { return 'item' }
  $t = $s -replace '"', "'" -replace '`', '' -replace '[\[\]{}]', ' '
  if ($t.Length -gt 60) { $t = $t.Substring(0, 57) + '...' }
  return $t.Trim()
}

$loopDir = Join-Path $RepoRoot 'Sessions\loop'
if (-not (Test-Path $loopDir)) {
  New-Item -ItemType Directory -Path $loopDir -Force | Out-Null
}

$now = (Get-Date).ToUniversalTime().ToString('o')
$deliveryRaw = ''
$dsPath = Join-Path $loopDir 'delivery-state.md'
if (Test-Path $dsPath) { $deliveryRaw = Read-Utf8File $dsPath }

$goalRaw = ''
$goalPath = Join-Path $loopDir 'goal.md'
if (Test-Path $goalPath) { $goalRaw = Read-Utf8File $goalPath }

$workId = Get-MdField $deliveryRaw 'current_work_id'
if (-not $workId) { $workId = 'goal' }
$sticky = Get-MdField $deliveryRaw 'sticky_pipeline'
$prUrls = Get-MdField $deliveryRaw 'pr_urls'
$evidence = Get-MdField $deliveryRaw 'last_evidence'
$tick = Get-MdField $deliveryRaw 'tick'
if (-not $tick) { $tick = '0' }

# Journal rows
$journalKinds = @()
$journalRows = @()
$journalPath = Join-Path $loopDir 'journal.md'
if (Test-Path $journalPath) {
  $jlines = Get-Content -Path $journalPath -Encoding UTF8
  foreach ($line in $jlines) {
    if ($line -notmatch '^\|\s*\d+') { continue }
    $cols = @($line.Split('|') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($cols.Count -lt 5) { continue }
    $row = [pscustomobject]@{
      tick     = $cols[0]
      at       = $cols[1]
      work_id  = $cols[2]
      kind     = $cols[3]
      result   = $cols[4]
      pr_url   = if ($cols.Count -gt 5) { $cols[5] } else { '' }
      evidence = if ($cols.Count -gt 6) { $cols[6] } else { '' }
    }
    $journalRows += $row
    $journalKinds += $row.kind
  }
}

$hasFeature = $journalKinds -contains 'feature_stage' -or $journalKinds -contains 'feature'
$hasGap = $journalKinds -contains 'gap'

# Resolve pipeline
$pipelineRaw = ''
$pipelinePath = $null
$specPath = $null
$designPath = $null
$pipelineTitle = $null
$pipelineDesc = $null
$pipelineType = $null
$issueUrl = $null

$pipelineDirs = @()
if (Test-Path (Join-Path $RepoRoot 'Sessions')) {
  $pipelineDirs = @(Get-ChildItem -Path (Join-Path $RepoRoot 'Sessions') -Directory -Filter 'pipeline-*' -ErrorAction SilentlyContinue)
}

function Read-Pipeline {
  param([string]$Dir)
  $p = Join-Path $Dir 'state.md'
  if (Test-Path $p) { return Read-Utf8File $p }
  return $null
}

if ($sticky) {
  $cand = Join-Path $RepoRoot "Sessions\pipeline-$sticky"
  if (Test-Path $cand) {
    $pipelinePath = Join-Path $cand 'state.md'
    $pipelineRaw = Read-Pipeline $cand
  }
}

if (-not $pipelineRaw) {
  foreach ($dir in $pipelineDirs) {
    $raw = Read-Pipeline $dir.FullName
    if (-not $raw) { continue }
    $issue = Get-MdField $raw 'issue'
    if ($issue -and ($workId -eq $issue -or $workId -eq "#$issue" -or $workId -match [regex]::Escape($issue))) {
      $pipelinePath = Join-Path $dir.FullName 'state.md'
      $pipelineRaw = $raw
      if (-not $sticky) { $sticky = $dir.Name -replace '^pipeline-', '' }
      break
    }
  }
}

if ($pipelineRaw) {
  $pipelineTitle = $null
  if ($pipelineRaw -match '(?m)^#\s+Pipeline:\s*(.+)$') { $pipelineTitle = $Matches[1].Trim() }
  $pipelineDesc = Get-MdField $pipelineRaw 'description'
  $pipelineType = Get-MdField $pipelineRaw 'type'
  $issueUrl = Get-MdField $pipelineRaw 'issue'
  $specRel = Get-MdField $pipelineRaw 'spec'
  $designRel = Get-MdField $pipelineRaw 'design_spec'
  $prBe = Get-MdField $pipelineRaw 'pr_backend'
  $prFe = Get-MdField $pipelineRaw 'pr_frontend'
  if (-not $prUrls) {
    $prs = @($prBe, $prFe) | Where-Object { $_ }
    if ($prs.Count -gt 0) { $prUrls = $prs -join ', ' }
  }
  if ($specRel) {
    $specFull = Join-Path $RepoRoot ($specRel -replace '/', '\')
    if (Test-Path $specFull) { $specPath = $specFull }
  }
  if ($designRel) {
    $designFull = Join-Path $RepoRoot ($designRel -replace '/', '\')
    if (Test-Path $designFull) { $designPath = $designFull }
  }
}

$specRaw = ''
$designRaw = ''
if ($specPath) { $specRaw = Read-Utf8File $specPath }
if ($designPath) { $designRaw = Read-Utf8File $designPath }

$userStory = $null
if ($specRaw -match '(?ms)## User Story\s+(.+?)(?=\n## |\z)') {
  $userStory = ($Matches[1] -replace '\r', '' -replace '(?m)^---\s*$', '').Trim()
}

$actions = New-Object System.Collections.Generic.List[string]
if ($specRaw) {
  foreach ($m in [regex]::Matches($specRaw, '(?m)^-\s+\*\*(AC\d+)\*\*:\s*(.+)$')) {
    $actions.Add(($m.Groups[1].Value + ': ' + $m.Groups[2].Value.Trim()))
  }
}
if ($actions.Count -eq 0 -and $pipelineDesc) {
  $actions.Add($pipelineDesc)
}

$routes = New-Object System.Collections.Generic.List[object]
if ($designRaw) {
  foreach ($m in [regex]::Matches($designRaw, '(?m)^\|\s*(`)?(\/[^|`\s]+)(`)?\s*\|\s*([^|]+)\|')) {
    $path = $m.Groups[2].Value.Trim()
    $comp = $m.Groups[4].Value.Trim()
    if ($path -match '^/app|^/public|^/') {
      $routes.Add([pscustomobject]@{ path = $path; component = $comp })
    }
  }
}

$endpoints = New-Object System.Collections.Generic.List[string]
if ($designRaw) {
  foreach ($m in [regex]::Matches($designRaw, '(?m)^###\s+(GET|POST|PUT|PATCH|DELETE)\s+(\S+)')) {
    $endpoints.Add($m.Groups[1].Value + ' ' + $m.Groups[2].Value)
  }
}

$hasFe = $routes.Count -gt 0 -or ($specRaw -match '(?m)^### Frontend')
$hasBe = $endpoints.Count -gt 0 -or ($specRaw -match '(?m)^### Backend')
$kind = 'report'
if ($hasFeature -and ($hasFe -or $hasBe)) { $kind = 'demo' }

$titleStem = $pipelineTitle
if (-not $titleStem) { $titleStem = $workId }
if ($kind -eq 'demo') {
  $title = "Goal done - feature demo: $titleStem"
} else {
  $title = "Goal done - delivery report: $titleStem"
}

$canDoLines = @()
if ($actions.Count -gt 0) {
  $canDoLines = @($actions | Select-Object -First 12)
} elseif ($userStory) {
  $canDoLines = @($userStory)
} else {
  $canDoLines = @('See journal and evidence for what landed in this goal.')
}

$envNote = 'Landed on develop (test after Railway/Vercel develop deploy). Not production until Stage 05 promote to main.'

$summaryParts = New-Object System.Collections.Generic.List[string]
$summaryParts.Add("Delivery goal finished ($Event). Kind=$kind. Work=$workId.")
if ($pipelineDesc) { $summaryParts.Add($pipelineDesc) }
elseif ($userStory) { $summaryParts.Add(($userStory -replace '\s+', ' ')) }
$summaryParts.Add("You can: " + (($canDoLines | Select-Object -First 4) -join ' | '))
$summaryParts.Add($envNote)
$summary = ($summaryParts -join ' ')
if ($summary.Length -gt 1500) { $summary = $summary.Substring(0, 1497) + '...' }

# Mermaid
$mermaid = ''
if ($kind -eq 'demo') {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('flowchart TD')
  [void]$sb.AppendLine('  start(["Host logged in"])')
  if ($routes.Count -gt 0) {
    $i = 0
    $prev = 'start'
    foreach ($r in $routes) {
      $id = "r$i"
      $label = Mermaid-Label ($r.path + ' - ' + $r.component)
      [void]$sb.AppendLine("  $id[""$label""]")
      [void]$sb.AppendLine("  $prev --> $id")
      $prev = $id
      $i++
    }
    [void]$sb.AppendLine("  $prev --> done([""Done on develop""])")
  } elseif ($endpoints.Count -gt 0) {
    $i = 0
    $prev = 'start'
    foreach ($ep in ($endpoints | Select-Object -First 8)) {
      $id = "e$i"
      $label = Mermaid-Label $ep
      [void]$sb.AppendLine("  $id[""$label""]")
      [void]$sb.AppendLine("  $prev --> $id")
      $prev = $id
      $i++
    }
    [void]$sb.AppendLine("  $prev --> done([""API on develop""])")
  }
  $mermaid = $sb.ToString().Trim()
}

function Write-BulletList {
  param([string[]]$Items)
  if (-not $Items -or $Items.Count -eq 0) { return '_None captured._' }
  return (($Items | ForEach-Object { "- $_" }) -join "`n")
}

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Goal handoff")
[void]$md.AppendLine("")
[void]$md.AppendLine("- generated: $now")
[void]$md.AppendLine("- event: $Event")
[void]$md.AppendLine("- kind: $kind")
[void]$md.AppendLine("- work_id: $workId")
[void]$md.AppendLine("- sticky_pipeline: $(if ($sticky) { $sticky } else { '(none)' })")
[void]$md.AppendLine("- pr_urls: $(if ($prUrls) { $prUrls } else { '(none)' })")
[void]$md.AppendLine("- surfaces: FE=$hasFe BE=$hasBe")
[void]$md.AppendLine("")
[void]$md.AppendLine("## What you can do now")
[void]$md.AppendLine("")
[void]$md.AppendLine((Write-BulletList $canDoLines))
[void]$md.AppendLine("")
[void]$md.AppendLine("## Environment")
[void]$md.AppendLine("")
[void]$md.AppendLine($envNote)
[void]$md.AppendLine("")
if ($userStory) {
  [void]$md.AppendLine("## User story")
  [void]$md.AppendLine("")
  [void]$md.AppendLine($userStory)
  [void]$md.AppendLine("")
}
if ($kind -eq 'demo' -and $mermaid) {
  [void]$md.AppendLine("## Flow")
  [void]$md.AppendLine("")
  [void]$md.AppendLine('```mermaid')
  [void]$md.AppendLine($mermaid)
  [void]$md.AppendLine('```')
  [void]$md.AppendLine("")
}
if ($routes.Count -gt 0) {
  [void]$md.AppendLine("## Frontend screens")
  [void]$md.AppendLine("")
  foreach ($r in $routes) {
    [void]$md.AppendLine("- ``$($r.path)`` - $($r.component)")
  }
  [void]$md.AppendLine("")
}
if ($endpoints.Count -gt 0) {
  [void]$md.AppendLine("## Backend endpoints")
  [void]$md.AppendLine("")
  foreach ($ep in $endpoints) {
    [void]$md.AppendLine("- ``$ep``")
  }
  [void]$md.AppendLine("")
}
[void]$md.AppendLine("## Journal")
[void]$md.AppendLine("")
if ($journalRows.Count -eq 0) {
  [void]$md.AppendLine('_No journal rows._')
} else {
  [void]$md.AppendLine('| tick | work_id | kind | result | pr |')
  [void]$md.AppendLine('|---|---|---|---|---|')
  foreach ($row in $journalRows) {
    [void]$md.AppendLine("| $($row.tick) | $($row.work_id) | $($row.kind) | $($row.result) | $($row.pr_url) |")
  }
}
[void]$md.AppendLine("")
[void]$md.AppendLine("## Links")
[void]$md.AppendLine("")
if ($issueUrl) { [void]$md.AppendLine("- Issue: $issueUrl") }
if ($specPath) { [void]$md.AppendLine("- Spec: $($specPath.Substring($RepoRoot.Length).TrimStart('\','/'))") }
if ($designPath) { [void]$md.AppendLine("- Design: $($designPath.Substring($RepoRoot.Length).TrimStart('\','/'))") }
if ($evidence) { [void]$md.AppendLine("- Last evidence: $evidence") }

$mdPath = Join-Path $loopDir 'goal-handoff.md'
Write-Utf8File -Path $mdPath -Text $md.ToString()

$htmlPath = ''
if ($kind -eq 'demo') {
  $routeCards = ($routes | ForEach-Object {
      $p = ConvertTo-HtmlEnc $_.path
      $c = ConvertTo-HtmlEnc $_.component
      "<article class='card'><div class='kicker'>Screen</div><h3>$p</h3><p>$c</p></article>"
    }) -join "`n"
  $epChips = ($endpoints | ForEach-Object {
      "<li><code>$(ConvertTo-HtmlEnc $_)</code></li>"
    }) -join "`n"
  $actionLis = ($canDoLines | ForEach-Object { "<li>$(ConvertTo-HtmlEnc $_)</li>" }) -join "`n"
  $storyHtml = if ($userStory) { "<p class='story'>$(ConvertTo-HtmlEnc $userStory)</p>" } else { '' }
  $prHtml = if ($prUrls) { "<p>PRs: $(ConvertTo-HtmlEnc $prUrls)</p>" } else { '' }
  $surface = @()
  if ($hasFe) { $surface += 'Frontend' }
  if ($hasBe) { $surface += 'Backend' }
  $surfaceLabel = ConvertTo-HtmlEnc (($surface -join ' + '))
  $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>$(ConvertTo-HtmlEnc $title)</title>
  <script type="module">
    import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs';
    mermaid.initialize({ startOnLoad: true, theme: 'base', themeVariables: {
      primaryColor: '#d7efe4', primaryTextColor: '#14352c', primaryBorderColor: '#2f6f5e',
      lineColor: '#2f6f5e', fontFamily: 'Georgia, serif'
    }});
  </script>
  <style>
    :root { --ink:#14352c; --muted:#4d6b62; --bg:#f4f7f5; --card:#fff; --accent:#2f6f5e; }
    body { margin:0; font-family: Georgia, 'Times New Roman', serif; background:var(--bg); color:var(--ink); }
    header { background:linear-gradient(160deg,#1c4a3d,#2f6f5e); color:#f4f7f5; padding:2.5rem 1.5rem 2rem; }
    header p { opacity:.85; max-width:44rem; }
    main { max-width:960px; margin:-1.5rem auto 3rem; padding:0 1rem; }
    .banner { background:#fff3cd; color:#5c4a00; padding:.75rem 1rem; border-radius:8px; margin-bottom:1.25rem; }
    .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:1rem; }
    .card { background:var(--card); border:1px solid #d5e4de; border-radius:12px; padding:1rem 1.1rem; box-shadow:0 8px 24px rgba(20,53,44,.06); }
    .kicker { font-size:.7rem; letter-spacing:.08em; text-transform:uppercase; color:var(--accent); }
    h1 { margin:.2rem 0 .6rem; font-size:1.8rem; }
    h2 { margin:2rem 0 .8rem; font-size:1.2rem; }
    .story { font-size:1.05rem; line-height:1.45; }
    code { font-family: ui-monospace, Consolas, monospace; font-size:.85em; }
    ul.actions { line-height:1.55; }
    .flow { background:var(--card); border-radius:12px; padding:1rem; border:1px solid #d5e4de; overflow:auto; }
    footer { color:var(--muted); font-size:.85rem; padding:0 1rem 2rem; max-width:960px; margin:0 auto; }
  </style>
</head>
<body>
  <header>
    <p>CasaZen | goal handoff | $surfaceLabel demo</p>
    <h1>$(ConvertTo-HtmlEnc $titleStem)</h1>
    <p>$(ConvertTo-HtmlEnc $pipelineDesc)</p>
  </header>
  <main>
    <div class="banner">$(ConvertTo-HtmlEnc $envNote)</div>
    $storyHtml
    <h2>What you can do now</h2>
    <ul class="actions">$actionLis</ul>
    <h2>Product flow</h2>
    <div class="flow"><pre class="mermaid">@@MERMAID@@</pre></div>
    <h2>Screens</h2>
    <div class="grid">$routeCards</div>
    <h2>API</h2>
    <ul>$epChips</ul>
    $prHtml
  </main>
  <footer>Generated $now | event $Event | tick $tick | open this file in a browser (Mermaid loads from jsDelivr).</footer>
</body>
</html>
"@
  $html = $html.Replace('@@MERMAID@@', $mermaid)
  $htmlPath = Join-Path $loopDir 'goal-handoff.html'
  Write-Utf8File -Path $htmlPath -Text $html
}

$relMd = 'Sessions/loop/goal-handoff.md'
$relHtml = if ($htmlPath) { 'Sessions/loop/goal-handoff.html' } else { '' }

$handoff = [ordered]@{
  event           = $Event
  kind            = $kind
  work_id         = $workId
  title           = $title
  summary         = $summary
  artifact_path   = $relMd
  html_path       = $relHtml
  pr_url          = $(if ($prUrls) { $prUrls } else { '' })
  evidence_path   = $(if ($evidence) { $evidence } else { '' })
  what_you_can_do = @($canDoLines)
  routes          = @($routes | ForEach-Object { $_.path })
  endpoints       = @($endpoints)
  mermaid         = $mermaid
  timestamp       = $now
}

$jsonPath = Join-Path $loopDir 'last-handoff.json'
Write-Utf8File -Path $jsonPath -Text ($handoff | ConvertTo-Json -Depth 6)

Write-Host "goal-handoff: kind=$kind work_id=$workId md=$relMd html=$relHtml"

if ($Notify) {
  $notifyScript = Join-Path $PSScriptRoot 'notify-human.ps1'
  $notifyArgs = @{
    Event         = $Event
    WorkId        = $workId
    Title         = $title
    Summary       = $summary
    EvidencePath  = $(if ($evidence) { $evidence } else { '' })
    PrUrl         = $(if ($prUrls) { $prUrls } else { '' })
    ArtifactKind  = $kind
    ArtifactPath  = $relMd
    HtmlPath      = $relHtml
    RepoRoot      = $RepoRoot
    DryRun        = [bool]$DryRun
  }
  & $notifyScript @notifyArgs
  $code = $LASTEXITCODE
  if ($null -eq $code) { $code = 0 }
  exit $code
}

if ($DryRun) { exit 0 }
exit 0
