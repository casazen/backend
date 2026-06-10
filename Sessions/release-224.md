# Release — Issue #224 Stripe Connect Onboarding → v1.1.10

> **Date**: 2026-06-10 · **Tag**: v1.1.10 · **Pipeline**: `spec-connect-onboarding`

---

## Phase A — Merge to develop

| Gate | Status | Notes |
|---|---|---|
| G1 BE PR CI | ✅ | PR #223 — Build & Test pass |
| G2 FE PR CI | ✅ | PR #117 — e2e + Vercel pass |
| G3 BE merged | ✅ | `e250f47` on develop |
| G4 FE merged | ✅ | `7e93e0d` on develop |

---

## Phase B — Staging validation

| Gate | Status | Notes |
|---|---|---|
| G5 Railway test health | ✅ | `https://casazen-api-test.up.railway.app/api/health` → 200 |
| G6 Auth smoke | ✅ | `/api/properties` → 401 |
| G6d Migrations test | ✅ | `migrate.ps1 -Target test` — up to date |
| G7 dotnet test | ✅ | 476 passed (develop) |
| G8 E2E | ✅ | 60 passed local; develop CI green |
| G9 Feature AC | ✅ | Connect integration tests 3/3; `connect-onboarding.spec.ts` |
| G10 Staging SPA | ⚠️ | Develop preview URL not resolved; prod SPA + E2E green |

---

## Phase C — Promote to main

| Gate | Status | Notes |
|---|---|---|
| G11 Semver | ✅ | v1.1.10 (patch from v1.1.9) |
| G12 BE release PR | ✅ | [#225](https://github.com/casazen/backend/pull/225) → `b64490d` |
| G13 FE release PR | ✅ | [#118](https://github.com/casazen/frontend/pull/118) → `5d304a4` |
| G14 Tags pushed | ✅ | `v1.1.10` both repos |
| G15 GitHub Releases | ✅ | [BE](https://github.com/casazen/backend/releases/tag/v1.1.10) · [FE](https://github.com/casazen/frontend/releases/tag/v1.1.10) |

**Prod migration** (pre-Phase C): `AddConnectStatusFields` applied to `casazen_prod`.

---

## Phase D — Production validation

| Gate | Status | Notes |
|---|---|---|
| G16 BE prod health | ✅ | `https://casazen-api.up.railway.app/api/health` → 200 |
| G16b release-smoke.ps1 | ⚠️ | Script parse error (line 97); manual checks substituted |
| G17 FE prod SPA | ✅ | `https://casazen-app.vercel.app` → `id="root"` |
| G18 prod-deploy-smoke | ✅ | 2/2 Playwright tests pass |
| G20 Branch alignment | ⚠️ | `main` ⊂ `develop` (+2 sync merge commits); no code drift |
| G21 Build parity | ✅ | `dotnet build` + `npm run build` pass on both tips |

### Branch sync

| Repo | develop SHA | main SHA | Sync commit |
|---|---|---|---|
| backend | `a2125f0` | `b64490d` | `chore: sync develop with main after release v1.1.10 (#224)` |
| frontend | `9f15a92` | `5d304a4` | same |

---

## What to verify in prod

1. Log in as Org admin with `property.write`
2. Open `/app/short-rent/settings/payments`
3. Verify status badge (Non collegato / In verifica / Attivo)
4. Click **Collega Stripe** → redirects to Stripe hosted onboarding
5. Configure `Stripe:ConnectWebhookSecret` in Railway prod for `account.updated` events on `/webhooks/stripe/connect`

## Unblocks

- `spec-direct-checkout` (AC5 charge gate)
- `spec-branded-booking-site` publish gate (AC10)
- `spec-ltr-recurring-rent` landlord MoR

**DECISION: COMPLETE** — ready for Stage 06 operations audit.
