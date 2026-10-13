# Stage 04 Security Audit — PR #402

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/402 |
| Title | chore(sdlc): Stage 01 onboarding-plg (#271) — planning PASS |
| Base | `develop` |
| Head | `feature/271-onboarding-plg` |
| Work unit | Delivery tick 12 `SPEC:onboarding-plg` Stage 01 (planning PASS) + `requirements.json` refresh |
| Auditor | Stage 04 security-auditor (fresh context) |
| Date | 2026-08-13 |
| Evidence | `Sessions/loop/evidence/delivery-12/gates.json` overall=`pass` (G1–G5 + G2b planning gates only) |

## Scope reviewed

- Auditor brief: `.claude/sdlc/04-review/agents/security-auditor.md`
- `gh pr view 402` / `gh pr diff 402` (`casazen/backend`)
- Changed files (1 file, +25/−25):
  1. `Sessions/quality/requirements.json` — `updated` timestamp + P0 row reorder only

**No runtime controller, Infrastructure/EF, Stripe, appsettings, Auth0, or frontend code in the diff.**

## Attack surface table

| Surface | Result | Notes |
|---|---|---|
| Auth0 JWT / `[Authorize]` on `/api` | **N/A** | No controllers or endpoint changes |
| Owner-scoped IDOR (`OwnerId == auth-sub`) | **N/A** | No Property/Booking/Guest API changes |
| EF Core / raw SQL (`FromSqlRaw` concat) | **N/A** | No Infrastructure/SQL in diff |
| Stripe webhook signature | **N/A** | `StripeWebhookHandler` not touched |
| Guest PII (errors / logs) | **N/A** | No guest/error/log paths |
| Secrets / `appsettings*.json` | **PASS** | Diff is quality JSON only; secret-pattern scan: no matches |
| Frontend `<ProtectedRoute>` | **N/A** | Backend process PR; no FE routes |

**Runtime attack-surface audit: N/A (docs/quality JSON only).**  
Surfaces that are N/A for this PR are recorded as **N/A → not a product security PASS**.

## Explicit: onboarding product security — NOT PASS

Stage 01 evidence `overall=pass` covers **planning gates only** (issue #271 OPEN, AC depth, Technical Notes, `compliance` + `priority:*` labels). It does **not** certify product security for planned onboarding surfaces:

| Planned surface (spec / issue ACs) | This PR |
|---|---|
| `POST/PUT /api/users/onboarding` (Org + consents) | Not implemented / not in diff |
| `GET /api/onboarding/status` | Not in diff |
| `GET /api/legal/*` (`[AllowAnonymous]`) | Not in diff |
| ConsentRecord / GDPR consent persistence | Not in diff |
| FE onboarding wizard / ProtectedRoute ordering | Not in diff |

`requirements.json` contains **zero** `onboarding` / `plg` requirement rows and **no** matrix flip inventing endpoint security PASS. Existing onboarding-related `.cs` files in the tree are outside this PR scope and were **not** re-audited as PASS.

**Do not treat this PR as product security PASS for onboarding endpoints.**

## Diff-specific checks

| Check | Result |
|---|---|
| Secrets / tokens / connection strings in diff | **PASS** — none (`password|secret|apikey|token|connectionstring|Bearer|sk_live|sk_test` scan clean) |
| `appsettings` / Auth changes | **PASS** — not present |
| Raw SQL | **PASS** — not present |
| Invented matrix / product security PASS | **PASS** — vs `develop`: same 27 IDs; **zero** `matrix_status` changes (14 pass / 9 blocked / 4 unknown preserved). Timestamp + reorder only |
| Stage 01 honesty | **PASS** — planning `overall=pass` matches G1–G5+G2b logs; not claimed as Stage 03/runtime security |

## Secrets hygiene

| Check | Result |
|---|---|
| Credential / token / private key patterns in PR diff | None |
| Connection strings or Auth0/Stripe secret values | None |
| Committed secrets in `appsettings*.json` | N/A (file not in PR) |

**Secrets hygiene: PASS**

## Compliance gates

| Regulation / gate | Result | Notes |
|---|---|---|
| GDPR Art. 17 (`ErasureRequested` / `DataRetentionUntil`) | **N/A** | No guest creation flows in diff |
| CIN (`[CinCode]`) | **N/A** | No Property entity changes |
| Tourist tax (`TouristTaxRate`) | **N/A** | No tax logic |
| Alloggiati Web / Hangfire check-in | **N/A** | No check-in flows |
| G5 No IDOR | **N/A** | No controllers |
| G6 No raw SQL | **N/A** | No SQL |
| G7 PII not exposed | **N/A** | No guest/error/log paths |
| G8 Stripe signature | **N/A** | Handler not in diff |
| G9 GDPR guest fields | **N/A** | No Guest flows |
| G10 Frontend auth routes | **N/A** | Backend process PR only |

Compliance gates for this PR: **N/A** (no product surface). Future Stage 02–03 work on #271 must re-check Auth (`[Authorize]` vs intentional `[AllowAnonymous]` on legal docs), consent append-only integrity, Org scoping/IDOR, and GDPR consent/erasure — **not certified here**.

## Findings by severity

### 🔴 Critical

0 findings.

### 🟡 High

0 findings.

### 🟢 Medium / informational

1. **Informational (non-blocking):** `SPEC:onboarding-plg` Stage 01 planning PASS advances the sticky pipeline to `02-design`. Product Auth/GDPR/consent security for onboarding endpoints remains **unproven** until Stage 03 implementation + Stage 04 re-audit of real controller/FE diffs. Do not conflate planning PASS with endpoint security PASS.

## Merge recommendation

| Metric | Value |
|---|---|
| 🔴 Critical | 0 |
| 🟡 High | 0 |
| Secrets hygiene | PASS |
| Invented onboarding endpoint security PASS | **No** |
| **Merge OK** | **yes** |

No security blockers for merge to `develop`. Docs/quality JSON only; planning evidence only.

Merge OK: yes
