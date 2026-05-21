# Phase 4 — GitHub Configuration (Shadow Mode)

## Goal

Register the GitHub webhook pointing to the ngrok public URL.
At this stage both systems receive the same events — GitHub Actions still run.
Shadow mode is temporary: once validated in Phase 5, Actions jobs are disabled.

---

## Step 1 — Generate a Webhook Secret

```powershell
$secret = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Max 256) })
Write-Host $secret
```

Store this value in two places:

1. `Casazen.WebhookRunner/appsettings.Development.json` → `WebhookRunner.GitHubWebhookSecret`
2. Used by the startup script (`start-webhook-runner.ps1 -WebhookSecret <value>`) OR entered manually in GitHub (Step 3)

---

## Step 2 — Prepare Local Config

`Casazen.WebhookRunner/appsettings.Development.json` (never committed):

```json
{
  "WebhookRunner": {
    "GitHubWebhookSecret": "<generated-above>",
    "GitHubToken":         "<github-pat-with-repo-scope>",
    "AnthropicApiKey":     "<sovereign-api-key>",
    "AnthropicBaseUrl":    "https://adesso-ai-hub.3asabc.de/v1",
    "WorkingDirectory":    "C:\\Users\\luca.la-malfa\\private-project\\casazen\\backend"
  }
}
```

The `GitHubToken` must have scopes: `repo` (read + write issues, PRs, labels).
Create at: https://github.com/settings/tokens/new

---

## Step 3 — Option A: Auto-Register via Startup Script (Recommended)

```powershell
.\scripts\start-webhook-runner.ps1 `
    -GithubToken  $env:GITHUB_TOKEN `
    -WebhookSecret $env:WEBHOOK_SECRET
```

The script (from Phase 1):
1. Builds and starts WebhookRunner
2. Starts ngrok
3. Queries `localhost:4040/api/tunnels` for the public URL
4. Removes any stale ngrok webhook from GitHub
5. Registers a new webhook with the live URL

Check GitHub → Settings → Webhooks to confirm the new entry.

---

## Step 3 — Option B: Manual Registration

If not using the startup script:

1. Start WebhookRunner: `dotnet run --project Casazen.WebhookRunner`
2. Start ngrok: `ngrok http 5050`
3. Get URL: check ngrok terminal output or `http://localhost:4040`
4. Navigate to: **GitHub repo → Settings → Webhooks → Add webhook**

| Field | Value |
|---|---|
| Payload URL | `https://<your-id>.ngrok-free.app/webhook` |
| Content type | `application/json` |
| Secret | value from Step 1 |
| SSL verification | Enable |
| Events | Issues, Issue comments, Pull requests |

Click **Add webhook**.

---

## Step 4 — Verify Initial Delivery

GitHub sends a `ping` event immediately after creating the webhook.

Check both:
1. GitHub → Settings → Webhooks → Recent Deliveries → should show HTTP 202
2. WebhookRunner logs:
   ```
   dbug: Delivery <id> (ping) skipped — no matching rule
   ```

---

## Step 5 — Verify Webhook Registration

After running the startup script, confirm in GitHub:

1. **Settings → Webhooks** shows the ngrok URL entry
2. **Recent Deliveries** shows the `ping` event with HTTP 202
3. WebhookRunner log shows:
   ```
   dbug: Delivery <id> (ping) skipped — no matching rule
   ```

Both Actions and the local runner now receive every event (shadow mode).
Functional validation and final Actions disabling happen in Phase 5 (`05-cutover.md`).

---

## Note on Ngrok URL Stability

The ngrok free tier generates a new random URL on every restart.
The startup script handles this automatically: it removes the old ngrok webhook
and registers the new URL on each run. It never touches non-ngrok webhooks.
GitHub stores up to 72h of webhook deliveries — no events are lost on restart.

---

## Validation Checklist — Phase 4

- [ ] Startup script runs end-to-end without error
- [ ] Ping delivery shows HTTP 202 in GitHub Recent Deliveries
- [ ] No existing webhooks (non-ngrok) were modified or deleted
- [ ] `step-transitions.yml` is unchanged (verify: `git diff -- .github/`)
- [ ] No secrets appear in WebhookRunner log output
