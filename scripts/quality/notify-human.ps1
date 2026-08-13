<#
.SYNOPSIS
  POST a delivery-loop notification to NOTIFY_WEBHOOK_URL (Slack/Telegram bridge/etc).

.DESCRIPTION
  Payload: { event, work_id, pr_url, title, summary, evidence_path }.
  When -DryRun or NOTIFY_WEBHOOK_URL is unset: writes Sessions/loop/last-notify.json and exits 2 (warn).
  Success exit 0; HTTP failure exit 1.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$Event,

  [Parameter(Mandatory = $true)]
  [string]$WorkId,

  [string]$PrUrl = '',

  [Parameter(Mandatory = $true)]
  [string]$Title,

  [Parameter(Mandatory = $true)]
  [string]$Summary,

  [string]$EvidencePath = '',

  [string]$RepoRoot = '',

  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
  $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  $RepoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
}

$payload = [ordered]@{
  event           = $Event
  work_id         = $WorkId
  pr_url          = $PrUrl
  title           = $Title
  summary         = $Summary
  evidence_path   = $EvidencePath
  action_required = $false
  timestamp       = (Get-Date).ToUniversalTime().ToString('o')
}

$json = $payload | ConvertTo-Json -Compress
$loopDir = Join-Path $RepoRoot 'Sessions\loop'
if (-not (Test-Path $loopDir)) {
  New-Item -ItemType Directory -Path $loopDir -Force | Out-Null
}
$outFile = Join-Path $loopDir 'last-notify.json'
Set-Content -Path $outFile -Value ($payload | ConvertTo-Json -Depth 5) -Encoding utf8

$webhook = $env:NOTIFY_WEBHOOK_URL
if ($DryRun -or [string]::IsNullOrWhiteSpace($webhook)) {
  Write-Host "notify-human: DRY-RUN / no NOTIFY_WEBHOOK_URL - payload written to $outFile"
  Write-Host $json
  exit 2
}

try {
  $response = Invoke-RestMethod -Method Post -Uri $webhook -Body $json -ContentType 'application/json; charset=utf-8'
  Write-Host "notify-human: OK event=$Event work_id=$WorkId"
  if ($null -ne $response) {
    Write-Host ($response | ConvertTo-Json -Compress -ErrorAction SilentlyContinue)
  }
  exit 0
}
catch {
  $msg = $_.Exception.Message
  Write-Host "notify-human: FAIL - $msg"
  exit 1
}
