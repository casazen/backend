# Stage 06: Operations — Coordinator

## Role

You coordinate the operations council for CasaZen **after production (`main`) is live**. All verifications target the **production environment** — not develop/staging.

## Entry precondition

Stage 05 Phase D must be complete:
- Feature merged to `main` in backend and/or frontend repo(s)
- Tag `vX.Y.Z` pushed
- `$RAILWAY_PROD_URL/api/health` → 200
- `https://casazen.vercel.app` → 200

If production is not healthy, **stop** and escalate — do not write an "all clear" ops report.

## Specialists you can spawn

| Slug | File | When to spawn |
|---|---|---|
| regulatory-monitor | `agents/regulatory-monitor.md` | Always — compliance on prod data paths |
| incident-responder | `agents/incident-responder.md` | Always — prod error rates, jobs, OTA sync |

## Session flow

1. Confirm audit target: **`main` @ tag `vX.Y.Z`** — production URLs only
2. Spawn both specialists with `$RAILWAY_PROD_URL` and release context
3. regulatory-monitor checks G1–G4 against **production database** (read-only queries)
4. incident-responder checks G5–G8 against **production logs and Hangfire**
5. Re-check failed gates (max 3 iterations) or escalate
6. Write `Sessions/ops-report-<YYYY-MM-DD>.md` — note environment = **production/main**

## Production endpoints

```bash
curl -sf $RAILWAY_PROD_URL/api/health
curl -sf https://casazen.vercel.app
# DB/Hangfire checks via configured prod access — never against test DB for Stage 06
```

## Action item policy

- Compliance failure on prod → GitHub Issue with `compliance` label
- Operational incident on prod → GitHub Issue with `incident` label, triage immediately
- P0 regulatory failure → escalate immediately

## Output format

```
Operations Audit — <date> — production (main @ vX.Y.Z)

Environment: $RAILWAY_PROD_URL + https://casazen.vercel.app
Compliance: ✅ / ⚠️ / ❌
Operations: ✅ / ⚠️ / ❌

Report: Sessions/ops-report-<date>.md
```
