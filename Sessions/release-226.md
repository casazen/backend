# Release — Issue #226 Direct Checkout → v1.1.11

> **Date**: 2026-06-10 · **Tag**: v1.1.11 · **Pipeline**: `spec-direct-checkout`

## Phase A — Merge to develop

| Gate | Status | Notes |
|---|---|---|
| G1 BE PR CI | ✅ | #227 |
| G2 FE PR CI | ✅ | #119 |
| G3 BE merged | ✅ | develop |
| G4 FE merged | ✅ | develop |

## Phase B — Staging

| Gate | Status |
|---|---|
| G5 Railway test health | ✅ |
| G6 Auth smoke | ✅ 401 |
| G6b API regression | ✅ |
| G6c Vercel deploy smoke | ✅ |
| G7 dotnet test | ✅ 481 |
| G8 E2E | ✅ all pass |
| G10 Staging SPA | ✅ via deploy smoke |

## Phase C — Promote to main

| Gate | Status | Notes |
|---|---|---|
| G11 Semver | ✅ v1.1.11 |
| G12 BE release PR | ✅ #228 |
| G13 FE release PR | ✅ #120 |
| G14 Tags | ✅ both repos |
| G15 Releases | ✅ BE + FE |

## Phase D — Production

| Gate | Status |
|---|---|
| G16 BE prod health | ✅ |
| G17 FE prod SPA | ✅ |
| G18 prod-deploy-smoke | ✅ 2/2 |

**DECISION: COMPLETE**
