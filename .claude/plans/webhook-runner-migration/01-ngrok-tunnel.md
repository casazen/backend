# Phase 1 — Ngrok Tunnel Setup

> Replaces the original Cloudflare Tunnel plan.
> Pattern borrowed from `agentic-kanban/scripts/ngrok-start.ts`.

## Goal

Expose `localhost:5050` (WebhookRunner) as a public HTTPS URL that GitHub can
reach. For development, ngrok works with zero firewall config and provides an
API at `localhost:4040` to query the live URL programmatically.

The startup script mirrors the agentic-kanban pattern:
start the app → wait for health → start ngrok → query API → register GitHub webhook.

---

## Step 1 — Install ngrok

```powershell
# Option A: winget
winget install ngrok.ngrok

# Option B: manual
# Download from https://ngrok.com/download (Windows AMD64)
# Extract ngrok.exe to C:\tools\ and add to PATH
```

Verify:
```powershell
ngrok version
# ngrok version 3.x.x
```

---

## Step 2 — Authenticate ngrok

Sign up at https://ngrok.com (free tier is sufficient).
Copy your auth token from https://dashboard.ngrok.com/get-started/your-authtoken.

```powershell
ngrok config add-authtoken <YOUR_AUTH_TOKEN>
# Writes to: C:\Users\<user>\AppData\Local\ngrok\ngrok.yml
```

---

## Step 3 — Start Ngrok (manual, for first test)

```powershell
# Start in a separate terminal — keep running while WebhookRunner is active
ngrok http 5050
```

Ngrok prints a forwarding URL like:
```
Forwarding   https://abc123.ngrok-free.app -> http://localhost:5050
```

Query the URL programmatically (used in the startup script):
```powershell
$tunnels = Invoke-RestMethod http://localhost:4040/api/tunnels
$publicUrl = ($tunnels.tunnels | Where-Object { $_.proto -eq "https" }).public_url
Write-Host "Webhook URL: $publicUrl/webhook"
```

---

## Step 4 — Startup Script (PowerShell)

Create `scripts/start-webhook-runner.ps1` in the repository root.
This mirrors the agentic-kanban `ngrok-start.ts` pattern:

```powershell
#!/usr/bin/env pwsh
# start-webhook-runner.ps1
# Starts WebhookRunner + ngrok, then registers the GitHub webhook automatically.

param(
    [int]    $Port           = 5050,
    [string] $GithubRepo     = "casazen/casazen-backend",
    [string] $GithubToken    = $env:GITHUB_TOKEN,
    [string] $WebhookSecret  = $env:WEBHOOK_SECRET,
    [switch] $SkipWebhookReg            # skip GitHub webhook registration
)

$ErrorActionPreference = "Stop"
$ROOT = Split-Path $PSScriptRoot -Parent

# ── 1. Build WebhookRunner ───────────────────────────────────────────────────
Write-Host "Building Casazen.WebhookRunner..."
dotnet build "$ROOT\Casazen.WebhookRunner" -c Release -v quiet

# ── 2. Start WebhookRunner in background ────────────────────────────────────
Write-Host "Starting WebhookRunner on port $Port..."
$runner = Start-Process dotnet `
    -ArgumentList "run --project $ROOT\Casazen.WebhookRunner -c Release" `
    -PassThru -NoNewWindow

# ── 3. Wait for /health ──────────────────────────────────────────────────────
Write-Host "Waiting for WebhookRunner to become healthy..."
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    try {
        $r = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -EA Stop
        if ($r.StatusCode -eq 200) { break }
    } catch { }
    if ((Get-Date) -gt $deadline) {
        Write-Error "WebhookRunner did not start within 30s"
        Stop-Process $runner -Force
        exit 1
    }
} while ($true)
Write-Host "WebhookRunner is healthy."

# ── 4. Start ngrok ───────────────────────────────────────────────────────────
Write-Host "Starting ngrok tunnel..."
$ngrok = Start-Process ngrok -ArgumentList "http $Port --log stdout --log-format json" `
    -PassThru -NoNewWindow -RedirectStandardOutput "$ROOT\.ngrok.log"

# ── 5. Query ngrok API for public URL ────────────────────────────────────────
Write-Host "Waiting for ngrok tunnel URL..."
$deadline = (Get-Date).AddSeconds(15)
$publicUrl = $null
do {
    Start-Sleep -Milliseconds 500
    try {
        $data = Invoke-RestMethod "http://localhost:4040/api/tunnels" -EA Stop
        $https = $data.tunnels | Where-Object { $_.proto -eq "https" } | Select-Object -First 1
        if ($https) { $publicUrl = $https.public_url; break }
    } catch { }
    if ((Get-Date) -gt $deadline) {
        Write-Error "Could not get ngrok URL within 15s"
        Stop-Process $runner, $ngrok -Force
        exit 1
    }
} while ($true)

$webhookUrl = "$publicUrl/webhook"
Write-Host ""
Write-Host "======================================================="
Write-Host "  Webhook URL: $webhookUrl"
Write-Host "  ngrok dashboard: http://localhost:4040"
Write-Host "======================================================="
Write-Host ""

# ── 6. Register GitHub webhook (optional) ────────────────────────────────────
if (-not $SkipWebhookReg -and $GithubToken -and $WebhookSecret) {
    Write-Host "Registering GitHub webhook..."

    # Check if webhook already exists with this URL
    $headers = @{ Authorization = "Bearer $GithubToken"; "Accept" = "application/vnd.github+json" }
    $existing = Invoke-RestMethod "https://api.github.com/repos/$GithubRepo/hooks" -Headers $headers

    $staleHook = $existing | Where-Object { $_.config.url -like "*.ngrok*" } | Select-Object -First 1
    if ($staleHook) {
        Write-Host "Removing stale ngrok webhook (id: $($staleHook.id))..."
        Invoke-RestMethod "https://api.github.com/repos/$GithubRepo/hooks/$($staleHook.id)" `
            -Method DELETE -Headers $headers | Out-Null
    }

    $body = @{
        name   = "web"
        active = $true
        events = @("issues", "issue_comment", "pull_request")
        config = @{
            url          = $webhookUrl
            content_type = "json"
            secret       = $WebhookSecret
            insecure_ssl = "0"
        }
    } | ConvertTo-Json -Depth 3

    Invoke-RestMethod "https://api.github.com/repos/$GithubRepo/hooks" `
        -Method POST -Headers $headers -Body $body -ContentType "application/json" | Out-Null

    Write-Host "GitHub webhook registered successfully."
} else {
    Write-Host "Skipping GitHub webhook registration (use -GithubToken and -WebhookSecret to enable)."
    Write-Host "Register manually: $webhookUrl"
}

# ── 7. Keep running — wait for Ctrl+C ────────────────────────────────────────
Write-Host "Press Ctrl+C to stop..."
try {
    Wait-Process $runner.Id
} finally {
    Write-Host "Shutting down..."
    Stop-Process $runner, $ngrok -Force -EA SilentlyContinue
}
```

Usage:
```powershell
# Simple start (manual webhook registration)
.\scripts\start-webhook-runner.ps1

# Full start with auto-registration
.\scripts\start-webhook-runner.ps1 `
    -GithubToken "ghp_xxx" `
    -WebhookSecret "my-secret"
```

---

## Step 5 — Validate

With the script running:

```powershell
# From another terminal
Invoke-WebRequest "https://abc123.ngrok-free.app/health"
# 200 {"status":"ok"}

# ngrok dashboard (inspect requests/responses)
Start-Process "http://localhost:4040"
```

---

## Ngrok vs Cloudflare: Why Ngrok for This Project

| | Ngrok | Cloudflare Tunnel |
|---|---|---|
| Setup time | < 5 min | 15-20 min (DNS config) |
| Stable URL | No (changes on restart) | Yes (custom domain) |
| API for URL | Yes (`localhost:4040`) | No |
| Auto-register webhook | Yes (startup script) | No (URL is fixed, not needed) |
| Free tier | Yes (1 tunnel, random URL) | Yes |
| Windows service | Manual | Built-in |

The startup script handles the unstable URL problem by automatically removing the
old GitHub webhook and registering the new URL on every start. This matches
exactly the pattern in `agentic-kanban/scripts/ngrok-start.ts`.

For production (always-on server), upgrade to ngrok paid plan for a fixed subdomain,
or switch to Cloudflare Tunnel at that point.

---

## Validation Checklist

- [ ] `ngrok version` returns version 3.x
- [ ] `ngrok config add-authtoken` succeeds
- [ ] Script starts WebhookRunner, ngrok, and prints the public URL
- [ ] `GET https://<url>.ngrok-free.app/health` returns 200
- [ ] ngrok dashboard at `http://localhost:4040` shows the tunnel
- [ ] GitHub webhook is registered (Settings → Webhooks shows the ngrok URL)
