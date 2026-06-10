# Operations Report — 2026-06-09 (Post-release v1.1.6 / Issue #202)

> **Scope**: Production audit after tenant-boundary release · **Tag**: `v1.1.6` · **Issue**: #202

---

## Environment

| Target | URL | Status |
|---|---|---|
| Prod API | `https://casazen-api.up.railway.app` | ✅ healthy |
| Prod FE (canonical) | `https://casazen-app.vercel.app` | ✅ SPA shell (`id="root"`) |
| Prod FE (legacy) | `https://casazen.vercel.app` | ⚠️ 9-byte response — mislinked (#187) |

---

## Gate results (production)

| # | Gate | Status | Notes |
|---|---|---|---|
| G1 | Prod API health | ✅ | HTTP 200, JSON healthy |
| G2 | Prod FE health | ⚠️ | Pass on `casazen-app.vercel.app`; fail on `casazen.vercel.app` (#187) |
| G3 | CIN format (prod DB) | ➖ | Not executed — requires prod Supabase read-only access this session |
| G4 | GDPR retention (prod DB) | ➖ | Not executed |
| G5 | Alloggiati jobs (prod) | ➖ | Not executed — requires Hangfire dashboard / prod logs |
| G6 | Tourist tax rates (prod DB) | ➖ | Not executed |
| G7 | Error rate (prod logs) | ➖ | Not executed |
| G8 | OTA sync (prod DB) | ➖ | Not executed |
| G9 | Released feature AC | ✅ | `GET /api/orgs/me/entitlement` → 401 (auth-gated, endpoint live post-deploy) |

---

## Release feature spot-check (#202)

- **Org boundary API** deployed to production (entitlement endpoint responds, not 404).
- **Migrations**: auto-applied on startup (#201); no migration errors observed in health check window.
- **Cross-org isolation**: covered by 442 unit/integration tests pre-release; prod manual IDOR test not run.

---

## Open operational items

1. **#187** — Confirm canonical Vercel production URL (`casazen.vercel.app` vs `casazen-app.vercel.app`).
2. **frontend#105** — Fix pre-existing e2e CI failure (`pricing-adapter.spec.ts`).
3. **backend#204 / #205** — Deferred review follow-ups (TOCTOU, backfill SQL test).

---

## Decision

**PARTIAL PASS** — production smoke (G1, G9) green; DB/log gates (G3–G8) require scheduled prod audit with Supabase + Railway access. No P0 compliance alert from available evidence.

**Next pipeline**: `spec-public-booking-readmodel` (2/7 Phase 1).
