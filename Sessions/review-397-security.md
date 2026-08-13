# Stage 04 Security Audit — PR #397

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/397 |
| Title | fix(sdlc): close MATRIX:native-host:AC21 backend push tests |
| Base | `develop` |
| Head | `cursor/casazen-sdlc-delivery-34c4` |
| Work unit | Delivery tick 8 `MATRIX:native-host:AC21` / `SPEC:native-host-app:AC21` |
| Auditor | Stage 04 security-auditor (fresh context) |
| Date | 2026-08-13 |
| Evidence | `Sessions/loop/evidence/delivery-8/gates.json` overall=`pass` |

## Scope reviewed

- Auditor brief: `.claude/sdlc/04-review/agents/security-auditor.md`
- `gh pr view` / `gh pr diff 397` only (tests + quality matrix/process)
- Evidence: `Sessions/loop/evidence/delivery-8/gates.json` + gate logs / `assert-ac21-pass.ps1`
- Changed files:
  1. `Casazen.Tests/Unit/Services/PushNotificationServiceTests.cs` — checkout-reminder recipient + route tests
  2. `Sessions/quality/ac-matrix-mvp.md` — AC21 `missing-test` → `pass`
  3. `Sessions/quality/requirements.json` — `SPEC:native-host-app:AC21.matrix_status` → `pass` (+ reorder)
  4. `scripts/quality/extract-requirements.ps1` — honor matrix `pass`/`stub`/`blocked` cells over failHints

## Diff verification (CasaZen / OWASP attack surface)

Tests + quality/process only. Diff does **not** modify:

| Surface | Touched? |
|---|---|
| Controllers / `[Authorize]` / Auth0 JWT | No |
| Owner-scoped IDOR checks | No |
| EF Core / `FromSqlRaw` / SQL | No |
| Stripe webhook signature | No |
| Guest PII (models, errors, logs) | No |
| `appsettings*.json` / connection strings / tokens | No |
| Frontend / `ProtectedRoute` | No |

New unit tests assert host/privileged-only Expo recipients and deep-link `/bookings/{id}/checkout` — no production auth surface change.

## Diff-specific checks (requested)

| Check | Result |
|---|---|
| Secrets hygiene | **PASS** — no API keys, connection strings, Auth0/Stripe secrets, or private keys in diff. `ExponentPushToken[...]` strings are in-memory test fixtures only (credential-pattern scan: no real secret matches) |
| extract-requirements cannot force-open blocked gaps | **PASS** — when matrix cell is `pass`/`stub`/`blocked`, that cell wins; when cell is unresolved, existing `blocked` on the SPEC row is still not overwritten by failHints. Simulated sync: AC4/AC20/supplier/L3/gj AC6 remain `blocked`. vs `origin/develop`: **no blocked rows removed** |
| AC21 pass does not clear other fail rows | **PASS** — sole status delta vs `develop` is `SPEC:native-host-app:AC21` `missing-test` → `pass`. Matrix + JSON: **AC15 remains `fail`**. AC20 / Maestro device gaps remain `blocked` (not invented PASS) |

### extract-requirements sticky-pass note (non-blocking)

`rowPattern` can mis-capture the first `` `| \`...\`` `` after a greedy match when notes also use backtick tokens (e.g. AC20 notes `` `casazen/mobile` `` → capture `casazen/mobile` instead of status `blocked`). For already-`blocked` SPEC rows the `elseif` still preserves `blocked`, so this PR does **not** force-open blocked gaps. Residual process risk: failing to *apply* matrix-`blocked` onto a non-blocked SPEC row when notes contain competing `| \`...\`` cells — not observed in delivered `requirements.json` for this PR.

## Secrets hygiene

| Check | Result |
|---|---|
| Credential / token / private key patterns in PR diff | None (real) |
| Connection strings or Auth0/Stripe secret values | None |
| Invented Stage 03 / Maestro / device PASS | No — AC15 `fail`, AC20 `blocked`; evidence gates are BE unit/integration + AC21 sticky assert only |

**Secrets hygiene: PASS**

## Compliance gates (G5–G10)

| Gate | Result | Notes |
|---|---|---|
| G5 No IDOR | N/A → PASS | No controllers; tests only exercise existing host-scoped push recipient filter |
| G6 No raw SQL | N/A → PASS | No Infrastructure/SQL |
| G7 PII not exposed | N/A → PASS | No guest/error/log paths |
| G8 Stripe signature | N/A → PASS | Handler not in diff |
| G9 GDPR guest fields | N/A → PASS | No Guest creation flows |
| G10 Frontend auth routes | N/A → PASS | Backend process/tests PR only |

## Findings by severity

### 🔴 Critical

0 findings.

### 🟡 High

0 findings.

### 🟢 Medium / informational

1. `scripts/quality/extract-requirements.ps1` — status-cell regex can latch onto backtick-wrapped note tokens (e.g. `` `casazen/mobile` ``) on some matrix rows; blocked SPEC rows remain protected by the non-clobber branch. Follow-up hardening (match the **status** column explicitly) recommended; **not** a merge blocker for this AC21 close.

## Merge recommendation

| Metric | Count |
|---|---|
| 🔴 Critical | 0 |
| 🟡 High | 0 |
| 🟢 Medium | 1 (informational process note) |

**Security verdict: PASS** — 0 open 🔴; no secrets, no IDOR/SQL/auth regressions, AC15 remains `fail`, blocked gaps not force-opened, AC21 sticky-pass evidence-backed (BE suite only; no device/Maestro invent).

Do **not** merge from this auditor role; Stage 04 gate-runner + delivery tick own merge.
