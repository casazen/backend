# Planning Session — Issue #182
# feat: long-term UI layer separation

**Stage**: 01 Planning  
**Date**: 2026-06-03  
**Issue**: https://github.com/casazen/backend/issues/182  
**Pipeline slug**: `long-term-ui-layer`  
**Epic**: #165 — Long-Term Lease End-to-End Contract Lifecycle (closed)  
**Prior FE work**: #177 — lease UI section (closed)  
**Branch target (Stage 03)**: `feature/182-long-term-ui-layer` (frontend repo)

---

## Council Composition

| Agent | Contribution |
|---|---|
| coordinator | Gate validation, synthesis, handoff |
| product-strategist | User story, acceptance criteria, priority |
| tech-architect | FE shell architecture, routing, complexity |
| regulatory-analyst | `compliance:none-required` — no new regulated data flows |

---

## Product Strategist — User Story & Acceptance Criteria

### User Story

As a CasaZen user, I want the app to present **separate navigation layers** for short-stay property management and long-term lease management, so that I only see the tools relevant to my role and can switch contexts clearly when I hold both roles.

### Persona matrix

| Persona | Roles | Expected experience |
|---|---|---|
| Short-stay owner | `PropertyOwner` only | Short-stay shell only; no Leases nav or layer switcher |
| Long-term landlord | `LongTermLandlord` only | Long-term shell only; no Bookings/OTA clutter |
| Dual operator | Both roles | Layer switcher; last-used layer persisted |

### Acceptance Criteria (6 — all testable)

| # | Criterion | Test type |
|---|---|---|
| AC1 | PropertyOwner-only → short-stay shell, no long-term nav | E2E + unit |
| AC2 | LongTermLandlord-only → long-term shell, no short-stay nav | E2E + unit |
| AC3 | Dual-role → persistent layer switcher toggles shells | E2E |
| AC4 | Long-term layer reuses `/leases/*` pages (no CRUD rewrite) | E2E + integration |
| AC5 | PropertyOwner-only blocked from `/leases` (redirect/403) | E2E + unit |
| AC6 | Dual-role lease nav stays in long-term shell with correct active state | E2E |

### Priority

`priority:medium` — #177 delivered functional lease pages; this issue improves UX architecture and role clarity. Not blocking lease CRUD MVP but important before broader rollout to mixed-role users.

---

## Tech Architect — Technical Impact

### Current state (post-#177)

- Single `AppShell` + `Sidebar` mixes short-stay nav with a role-gated "Leases" item
- Lease pages import `AppShell` directly per page
- Routes at `/leases/*` protected by `<ProtectedRoute role="LongTermLandlord">`

### Target architecture

```
┌─────────────────────────────────────────────────────────┐
│  Login → role-based default layer                       │
├──────────────────────┬──────────────────────────────────┤
│  Short-stay layer    │  Long-term layer                 │
│  AppShell            │  LongTermAppShell                │
│  Sidebar (no Leases) │  LongTermSidebar (Leases + …)    │
│  /, /properties, …   │  /leases/* (or /long-term/*)     │
├──────────────────────┴──────────────────────────────────┤
│  Dual-role: LayerSwitcher in header                     │
└─────────────────────────────────────────────────────────┘
```

### Affected layers (frontend only)

| Layer | Changes |
|---|---|
| `src/components/layout/` | Split shells, new long-term sidebar, layer switcher |
| `src/routes/index.tsx` | Nested layout routes per layer |
| `src/features/leases/*` | Remove direct `AppShell` import; inherit layout |
| `src/lib/auth-roles.ts` | Role-combination helpers |
| `src/hooks/use-app-layer.ts` | Layer state + `localStorage` persistence |

### Infrastructure impact

| Area | Impact |
|---|---|
| EF Core migrations | **None** |
| OTA platforms | **None** — OTA nav excluded from long-term layer |
| Background jobs | **None** |
| Backend / Auth0 | **None expected** — roles already in JWT |

### Complexity

**effort:M (1–2 days)** — routing refactor, two shell variants, layer switcher. Lease feature code unchanged.

### Open design decisions (Stage 02)

1. Route prefix: keep `/leases/*` with layout wrapper vs migrate to `/long-term/*`
2. Long-term-only default home: `/leases` vs dedicated dashboard stub
3. Whether short-stay Properties remain accessible from long-term layer (recommend: no cross-nav; shared property data via API only)

---

## Regulatory Analyst — Compliance Assessment

### Regulations in scope

| Regulation | Applies? | Rationale |
|---|---|---|
| CIN | No | Short-stay only; not surfaced in long-term layer |
| Alloggiati Web | No | Guest reporting is short-stay |
| Tourist tax | No | Short-stay only |
| GDPR (new flows) | No | No new PII collection; reuses #177 lease components |

### Label

`none-required` — navigation/shell separation only; existing #177 guardrails unchanged.

---

## Duplicate Check

```bash
gh issue list --state open   # no matching open issue
gh issue list --state all --search "long-term UI layer OR long term layer OR separate shell"
# → only #165 epic (closed); no duplicate
```

---

## Harness Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1: Issue exists | ✅ | #182 open |
| G2: Acceptance criteria | ✅ | 6 Given/When/Then criteria |
| G3: Technical scope | ✅ | Technical Notes with migration/OTA/jobs = None |
| G4: Regulatory label | ✅ | `none-required` applied |
| G5: Priority label | ✅ | `priority:medium` applied |

**Result**: All gates passed — ready for Stage 02 Design.

---

## Handoff to Stage 02

| Field | Value |
|---|---|
| Issue | `#182` |
| Design spec target | `Sessions/design-182.md` |
| Frontend repo | `casazen/frontend` (relative: `../frontend`) |
| Entry condition | Component tree for dual shells, route map, layer switcher UX spec |
