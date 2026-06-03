# Review Session — Issue #182 / PR #86
# feat: long-term UI layer separation

**Stage**: 04 Review  
**Date**: 2026-06-03  
**Issue**: https://github.com/casazen/backend/issues/182  
**PR**: https://github.com/casazen/frontend/pull/86  
**Design ref**: `Sessions/design-182.md`

---

## Council

| Agent | Scope |
|---|---|
| code-reviewer | Layer routing, AC coverage, tests, React patterns |
| security-auditor | ProtectedRoute, auth boundaries, localStorage scope |
| coordinator | Gate validation, finding triage |

---

## Findings

### 🔴 Critical — 0 open

None.

### 🟡 High — 0 open

None blocking merge.

### 🟢 Medium — deferred

| ID | Finding | Disposition |
|---|---|---|
| M1 | No E2E tests for layer switcher / dual-role flows | Follow-up recommended; unit tests cover guards and helpers |
| M2 | `useAppLayer` syncs state during render (pathLayer/forcedLayer) | Works; refactor to `useEffect` optional post-merge |
| M3 | `LayerAwareProfilePage` embeds shell manually vs layout route | Acceptable per design; profile is cross-layer |

### ⚪ Low — noted

| ID | Finding |
|---|---|
| L1 | Layer switcher labels in English; end-user UI policy is Italian — align in follow-up if product requires |
| L2 | `/search` remains public outside `AppLayerProvider` (unchanged behaviour) |

---

## Security Audit

| Check | Result |
|---|---|
| G5 IDOR (backend) | N/A — no backend changes; lease API still server-gated |
| G6 Raw SQL | N/A — FE only |
| G7 PII in errors | ✅ No new error surfaces; layer pref is non-PII |
| G8 Stripe webhook | N/A |
| G9 GDPR Guest fields | N/A — no Guest entity changes |
| G10 ProtectedRoute | ✅ Auth wrapper on root; `/leases/*` requires `LongTermLandlord` |

**Auth boundary note**: Frontend layer separation is UX-only. `ProtectedRoute role="LongTermLandlord"` on long-term layout remains the client gate; backend `LongTermLandlord` policy is authoritative.

**localStorage**: `casazen:active-layer` stores only `'short-stay' | 'long-term'` — no tokens or PII.

---

## Acceptance Criteria Verification

| AC | Status | Evidence |
|---|---|---|
| AC1 PropertyOwner-only → short-stay shell, no long-term nav | ✅ | `sidebar.tsx` (no Leases); `ShortStayLayerGuard` |
| AC2 LongTermLandlord-only → long-term shell | ✅ | `LongTermAppShell` + `LongTermSidebar`; login → `/leases` |
| AC3 Dual-role → layer switcher + persistence | ✅ | `LayerSwitcher`, `useAppLayer`, `localStorage` |
| AC4 Reuse `/leases/*` pages in long-term shell | ✅ | Layout route; lease pages drop per-page `AppShell` |
| AC5 PropertyOwner-only blocked from `/leases` | ✅ | `ProtectedRoute role="LongTermLandlord"` |
| AC6 Dual-role nav stays in long-term shell | ✅ | `getPathLayer` syncs layer on `/leases` deep links |

---

## Harness Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1: PR approval | ❌ | 0 approvals — **HITL required** |
| G2: No critical findings | ✅ | 0 🔴 |
| G3: High findings addressed | ✅ | 0 open 🟡 |
| G4: Test coverage adequate | ✅ | 20 new Vitest tests; guards + role helpers covered |
| G5: No IDOR | ✅ | N/A — FE only |
| G6: No raw SQL | ✅ | N/A |
| G7: PII not exposed | ✅ | |
| G8: Stripe signature | ✅ | N/A |
| G9: GDPR fields | ✅ | N/A |
| G10: ProtectedRoute on auth routes | ✅ | Root + long-term layout |

**CI**: Vercel preview ✅

---

## Verdict

**Council approves code quality** — no blocking findings. Merge blocked only by **G1 (human PR approval)**.

Handoff → Stage 05 after G1 satisfied and user confirms release at HITL Gate 2.
