# Planning Session — Issue #177
# [FE] Long-Term Lease — UI section (draft form, signing flow, registration status, receipt)

**Stage**: 01 Planning  
**Date**: 2026-06-02  
**Issue**: https://github.com/casazen/backend/issues/177  
**Epic**: #165 — Long-Term Lease End-to-End Contract Lifecycle  
**Branch target (Stage 03)**: `feature/177-lease-ui` (frontend repo)

---

## Council Composition

| Agent | Contribution |
|---|---|
| coordinator | Gate validation, synthesis, handoff |
| product-strategist | User story, acceptance criteria, priority |
| tech-architect | FE impact, dependencies, complexity |
| regulatory-analyst | GDPR + cessione di fabbricato tagging |

---

## Product Strategist — User Story & Acceptance Criteria

### User Story

As a property owner with the **LongTermLandlord** role, I want a dedicated lease management section in CasaZen so that I can create contract drafts, track signing progress, and view registration status without leaving the app.

### Persona & scope

| Dimension | Value |
|---|---|
| Primary role | `LongTermLandlord` (Auth0 custom claim) |
| Secondary roles | May also hold `PropertyOwner` — lease routes require LongTermLandlord only |
| Out of scope | Guest/tenant self-service portal; automated Questura filing; PDF template editing |

### Acceptance Criteria (8 — all testable)

| # | Criterion | Test type |
|---|---|---|
| AC1 | GIVEN LongTermLandlord role, WHEN navigating to `/leases`, THEN route is protected and lease list renders | E2E + unit (ProtectedRoute) |
| AC2 | GIVEN empty lease list, WHEN page loads, THEN empty state with "Create lease" CTA | E2E |
| AC3 | GIVEN valid form data, WHEN submitted, THEN `POST /api/leases` called and draft appears with status `Draft` | Integration + E2E |
| AC4 | GIVEN lease in `Draft`, WHEN "Initiate signing" clicked, THEN `POST /api/leases/{id}/signing` called and per-signer URLs shown | Integration + E2E |
| AC5 | GIVEN lease in `Signed`, WHEN "Register" clicked, THEN `POST /api/leases/{id}/registration` called and status becomes `SentToProvider` | Integration + E2E |
| AC6 | GIVEN lease in `Registered`, WHEN detail viewed, THEN "Download receipt" triggers `GET /api/leases/{id}/registration/receipt` (PDF blob) | Integration |
| AC7 | GIVEN property without APE document, WHEN create form submitted, THEN client-side validation error shown **before** API call | Unit + E2E |
| AC8 | GIVEN tenant flagged `isExtraEU` by backend, WHEN detail viewed, THEN Questura 48h warning banner displayed | Unit + E2E |

### Priority

`priority:high` — Blocks MVP long-term rental workflow; epic #165 FE deliverable; regulatory UX (APE gate, Questura banner) required before go-live.

---

## Tech Architect — Technical Impact

### Affected layers (frontend only)

| Layer | Changes |
|---|---|
| `src/routes/index.tsx` | Add `/leases`, `/leases/new`, `/leases/:id` |
| `src/components/auth/protected-route.tsx` | Optional `role` prop — check Auth0 `https://casazen.app/roles` |
| `src/api/leases.api.ts` | New — CRUD + signing + registration + receipt blob |
| `src/api/properties.api.ts` | Extend — `GET /properties/{id}/documents` for APE pre-check |
| `src/queries/use-leases.ts` | New — `useLeases`, `useLease`, `useCreateLease`, `useInitiateSigning`, `useTriggerRegistration`, `useLeaseRegistration` |
| `src/types/lease.types.ts` | New — LeaseStatus, FiscalRegime, PartyRole, DTOs |
| `src/features/leases/` | Pages + form + panels + badges |
| `src/components/layout/sidebar.tsx` | Nav item "Leases" (visible only for LongTermLandlord) |

### Backend dependencies

| Issue | Status | Notes |
|---|---|---|
| #167 — LongTermLandlord Auth0 policy | Open (code present) | Policy name is `LongTermLandlord` (not `LongTermLandlordPolicy`). FE must read role from JWT custom claim. |
| #174 — LeasesController | Open (code present) | `LeasesController` with 6 endpoints exists in `Casazen.Web`. Safe to integrate; verify in Stage 03 against Swagger. |
| #165 — Epic design spec | Complete | `Sessions/design-165.md` is the API contract reference for Stage 02. |

**EF Core migration**: No — FE only  
**OTA platforms**: None  
**Background jobs**: None (FE polls registration status every 30s when `SentToProvider`)  
**External services**: Auth0 role claim; e-sign URLs rendered as external links  

### Complexity

**effort:M (1–2 days)** — Standard feature slice: 3 routes, 1 API module, 6 query hooks, 7 components. No Zustand. Main complexity: role-guard extension + APE pre-validation + PDF blob download.

### Technical risks

| Risk | Mitigation |
|---|---|
| Auth0 role not assigned in dev tenant | Demo mode: grant `LongTermLandlord` in `demo.config.ts`; document Auth0 dashboard steps in #167 |
| Signing URLs not persisted on lease entity | Store mutation result in component state; re-initiate signing if user refreshes during `AwaitingSignature` |
| Receipt endpoint returns 404 before `Registered` | Disable download button unless `lease.status === 'Registered'` |
| Property list endpoint lacks document metadata | Fetch `/properties/{id}/documents` on property select; check `documentType === 'Ape'` client-side (AC7) |

### Gate commands (Stage 03 exit)

```bash
cd frontend
npm test
npx tsc -b --noEmit
npm run lint
npm run build
```

---

## Regulatory Analyst — Compliance Assessment

### Regulations in scope

| Regulation | Applies? | Rationale |
|---|---|---|
| GDPR (EU 2016/679) | Yes | Lease form collects party PII (name, fiscal code, email, citizenship). Display only in authenticated owner context. |
| Cessione di fabbricato (D.L. 286/1998) | Yes | Extra-EU tenant triggers 48h Questura notification obligation — UI warning banner required (not automated in MVP). |
| APE (D.Lgs. 192/2005) | Yes | Lease creation blocked without APE document on property — enforced client-side (AC7) + server-side. |
| CIN / Alloggiati / Tourist tax | No | Long-term lease flow is separate from short-stay guest reporting. |

### Labels to apply

- `compliance` — GDPR PII handling + regulatory UX requirements
- `regulatory-compliance` — Italian lease registration + Questura + APE

### Implementation guardrails (for Stage 03–04)

- Never log party PII in browser console beyond form validation errors
- Error toasts must show generic messages; do not surface raw API error bodies containing fiscal codes
- Extra-EU banner is informational only — no PII in banner text

---

## Harness Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1: Issue exists | ✅ | #177 open with structured body |
| G2: Acceptance criteria | ✅ | 8 Given/When/Then criteria |
| G3: Technical scope | ✅ | Technical Notes + this planning doc |
| G4: Regulatory label | ✅ | `compliance` + `regulatory-compliance` applied |
| G5: Priority label | ✅ | `priority:high` |

**Result**: All gates passed — ready for Stage 02 Design.

---

## Handoff to Stage 02

| Field | Value |
|---|---|
| Issue | `#177` |
| Design spec target | `Sessions/design-177.md` |
| API reference | `Sessions/design-165.md` (parent epic contract) |
| Frontend repo | `casazen/frontend` |
| Entry condition | Design spec with component tree, query key map, and route guard spec |

### Open questions (resolved)

1. **Policy name**: Use `LongTermLandlord` (matches `Program.cs`, not `LongTermLandlordPolicy` from #167 draft).
2. **Hook location**: `src/queries/use-leases.ts` (matches existing `use-bookings.ts` convention, not `src/hooks/`).
3. **Feature folder**: `src/features/leases/` (matches feature-slice convention in `FRONTEND-PROJECT.md`).
