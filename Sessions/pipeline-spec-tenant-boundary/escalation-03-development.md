# Escalation — Stage 03 Development — Issue #202 (multi-tenant Org boundary)

- **Date**: 2026-06-09
- **Branch**: `feature/202-tenant-boundary` (both repos), based on `develop`
- **Status**: BLOCKED on **one** gate — **G7 frontend lint** — which is a **pre-existing, repo-wide baseline failure unrelated to #202**.
- **Action taken**: PRs **NOT** opened, nothing committed/pushed (per "open PRs only when all applicable gates pass" + "do not open PRs with failing gates"). Resolving G7 in-scope is impossible without editing ~25 files untouched by #202, which violates the CRITICAL staging-hygiene mandate. Escalating for a human decision.

The feature itself is **complete and correct**: every other gate passes, and every Issue/design acceptance criterion has at least one automated test.

---

## Gate status (actual command outcomes)

### Backend — `casazen/backend`
| Gate | Command | Result |
|---|---|---|
| G1 tests | `dotnet test` | ✅ 440 passed, 0 failed, 25 skipped |
| G2 format | `dotnet format --verify-no-changes` | ✅ exit 0 |
| G3 warnings | `dotnet build /warnaserror` | ✅ 0 warnings, 0 errors |
| G4 migration | `dotnet ef migrations script --project Casazen.Infrastructure` | ✅ exit 0 |
| G10 CIN | `dotnet test --filter CinCode` | ✅ 3 passed (Property touched) |
| G11 secrets | git status + grep | ✅ no secrets; only pre-existing test fixtures match |
| G12 GDPR | Guest entity check | N/A — `Guest` not modified (AC2: no `OrgId` on Guest) |
| G13 tourist tax | grep `Casazen.Core` | N/A — no tax surface touched; no hardcoded amounts |

### Frontend — `casazen/frontend`
| Gate | Command | Result |
|---|---|---|
| G5 unit | `npm test` | ✅ 107 passed (17 files) |
| G6 types | `tsc -b --noEmit` | ✅ exit 0 |
| **G7 lint** | `npm run lint` (`eslint .`) | ❌ **exit 1 — 47 errors, 8 warnings (ALL pre-existing; 0 from #202)** |
| G8 build | `npm run build` | ✅ exit 0 |
| G9 E2E | `npm run test:e2e` | ✅ 38 passed, 7 skipped, 0 failed (incl. 2 new #202 specs) |

---

## Failing gate G7 — root cause: pre-existing baseline, NOT introduced by #202

`npm run lint` runs `eslint .` over the **entire** repo and reports **47 errors** across ~25 files. Evidence that this is pre-existing debt, not a regression from #202:

1. **~45 errors are in files this branch never modified** (confirmed against `git diff --name-only`): e.g. `src/api/bookings.api.ts`, `src/api/client.ts`, `src/api/ota.api.ts`, `src/api/payments.api.ts`, `src/components/ui/{input,label,textarea}.tsx`, `src/features/{bookings,leases,ota,payments,pricing}/...`, `src/types/{guest,ota,tourist-tax}.types.ts`, `e2e/pricing-adapter.spec.ts`. Rules: `@typescript-eslint/no-explicit-any`, `no-empty-object-type`, `no-unused-vars`, `react-hooks/*`.
   - **Proof**: `git diff --quiet -- src/api/bookings.api.ts` → unchanged, yet `npx eslint src/api/bookings.api.ts` → 1 error. A file identical to `develop` fails lint ⇒ `develop` is already red.

2. **The only 2 errors in files #202 touched are on PRE-EXISTING lines** (the #202 diff does not touch them):
   - `src/features/properties/components/property-form.tsx:53` `as any` — the RHF `defaultValues` cast (pre-existing). #202 only added the `disabled?` prop + `disabled={isLoading || disabled}`.
   - `src/features/properties/properties-page.tsx:36` unused `catch (error)` — in the pre-existing `toggleActive`. #202 only changed the *other* catch (`handleCreateProperty`), which **uses** `error`.

3. **All files CREATED/authored for #202 are lint-clean**: `npx eslint` over `e2e/tenant-boundary.spec.ts`, `e2e/helpers/org-api-mock.ts`, `src/components/org/`, `src/lib/entitlement-error.ts` (+ test), `src/api/orgs.api.ts`, `src/types/org.types.ts` → **exit 0, 0 problems**.

**Conclusion**: #202 introduces **zero** new lint errors. The gate is red because of pre-existing repo-wide lint debt.

### Why it was not fixed (staging hygiene conflict)
Making `eslint .` exit 0 requires editing ~25 files that are **unrelated to #202**. The task's CRITICAL constraint is to **commit ONLY the feature code** (explicit project/dir allow-list; never `git add -A`). Fixing unrelated lint debt would either (a) pollute this PR with ~25 off-topic files, or (b) require `eslint-disable` edits to untouched files — both violate the mandate and are dishonest gate-gaming. Therefore the only in-scope outcome is to leave the pre-existing debt and escalate.

---

## Fix-loop iteration history

- **Iteration 1** — Ran all backend + frontend + compliance gates with real commands. 8/9 FE-applicable + all BE/compliance gates green. G7 red (47 errors). Root-caused: 45/47 in unmodified files; 2/47 on pre-existing lines in touched files; all #202-authored files lint-clean; reproduced a lint error on a file byte-identical to `develop`. **Determination: failure is pre-existing and cannot be fixed without out-of-scope edits.**
- **Iterations 2–3** — Not performed: the only available "fix" is editing ~25 unrelated files, which is forbidden by the staging-hygiene mandate. The loop is therefore terminal; further mechanical iterations cannot change the outcome in-scope.

---

## Acceptance-criteria coverage (feature is complete)

| AC | Test(s) |
|---|---|
| AC1 Org + unique Slug | `MultiTenancyModelTests.Org_HasUniqueIndexOnSlug`, `Org_IsRegisteredAsDbSet` |
| AC2 OrgId FK + index + Restrict (4 tables) | `MultiTenancyModelTests.TenantEntity_HasRequiredRestrictedOrgIdFkAndIndex` |
| AC3 AddOrgIdNullable | `MigrationSqlTests.Step1_AddOrgIdNullable_*` |
| AC4 BackfillDefaultOrgs | `MigrationSqlTests.Step2_*`, `OrgBackfillSimulationTests.*` |
| AC5 MakeOrgIdRequired + ordering | `MigrationSqlTests.Migrations_LandInOrder_*`, `Step3_*` |
| AC6 snapshot discipline | `MultiTenancyModelTests` (model = snapshot source) |
| AC7 tenant filter / cross-org 404 | `TenantBoundaryIntegrationTests.AC7_CrossOrg_*` |
| AC8 entitlement 403 | `EntitlementServiceTests.*`, `TenantBoundaryIntegrationTests.AC8_*` |
| AC9 User.OrgId + /me org | `TenantBoundaryIntegrationTests.AC9_GetMe_*`, `MultiTenancyModelTests.User_HasNullableRestrictedOrgIdFk` |
| AC10 regression | `TenantBoundaryIntegrationTests.AC10_*`, `OrgBackfillSimulationTests.Backfill_LeavesNoOrphans/IsIdempotent` |
| AC10b down-migration + pre-flight guard | `MigrationSqlTests.Step3_*_GuardsThenFlips...`, `Step3_*_DownReverts_*` |
| AC11 header org + plan badge | `org-badge.test.tsx` (Vitest) + `e2e/tenant-boundary.spec.ts` "AC11 …" |
| AC12 plan-limit Italian message | `entitlement-error.test.ts` (Vitest) + `e2e/tenant-boundary.spec.ts` "AC12 …" |

Gap filled during this run: there was **no E2E spec** for the tenant boundary. Added `e2e/tenant-boundary.spec.ts` (+ `e2e/helpers/org-api-mock.ts`) covering AC11 + AC12 — both pass.

---

## Deviations from `design-202.md`
1. `ITenantContext` lives in `Casazen.Core/Multitenancy/` (impl `TenantContext` in `Casazen.Web/Infrastructure/`) rather than the spec's `Casazen.Web/Infrastructure/ITenantContext.cs`. Required: `AppDbContext` (in `Casazen.Infrastructure`) consumes the interface for the global query filter, and Infrastructure cannot reference Web. Sensible and necessary.
2. `org` attached to `GET /api/users/me` (`UsersController`/`UserDetailDto`) — this **follows** the design's resolved Open Question #1 (reconciled away from a separate `MeController` surface). Not a true deviation.
3. FE create route is `/app/short-rent/properties/create` (existing route-manifest naming); the design prose said `/new`. Pre-existing route naming; no behavior change.

---

## Recommendation (human decision required)
- **Option A (recommended if CI does not block on a fully-clean `eslint .`)**: Treat G7 as accepted pre-existing debt. On approval I will immediately commit ONLY the feature dirs, push both feature branches, and open the two PRs (bodies will state G7 = pre-existing baseline, 0 new errors). Everything else is green and ready.
- **Option B**: Fix the repo-wide lint debt in a **separate** chore PR first (out of scope for #202), then re-run G7 and open the #202 PRs clean.
- **Not recommended**: bundling the lint cleanup into the #202 PRs (violates staging hygiene; obscures the feature diff).

No files were committed, pushed, or staged. Working trees are intact on `feature/202-tenant-boundary`.
