# Stage 04 — Review · Issue #202 Multi-Tenant `Org` Boundary (US-004)

> Coordinator: Stage 04 Review · Specialists synthesized: `code-reviewer` + `security-auditor`
> PRs: backend [`casazen/backend#203`](https://github.com/casazen/backend/pull/203) · frontend [`casazen/frontend#104`](https://github.com/casazen/frontend/pull/104)
> Base: `develop` · Head: `feature/202-tenant-boundary` · Design contract: `Sessions/design-202.md`
> Date: 2026-06-09

---

## 1. Cross-repo summary

The change introduces the foundational tenant boundary exactly as specified: an `Org` entity (unique `Slug`, `PlanTier`), an `OrgId` FK on the four tenant tables (`Property`, `Booking`, `LeaseContract`, `Payment`) + nullable `User.OrgId`, an **EF Core global query filter** that scopes the four tables to the caller's `OrgId`, an `IEntitlementService` enforcing per-tier `maxProperties`, and the read-only FE surface (`OrgBadge`/`PlanBadge`, `useEntitlement`, plan-limit handling). The 3-step zero-downtime migration sequence (nullable → backfill → NOT NULL+FK) is present with a pre-flight NULL guard and tested `Down()` methods.

**The tenant-isolation core is correct and fail-closed.** `OrgId` is resolved server-side from the authenticated principal (`ITenantContext` → `User.OrgId`), never accepted from the client; cross-org reads return `404`/empty (verified by integration tests); writes set `OrgId` from the principal. No IDOR, no raw SQL, no PII leakage, no secrets.

**One genuine functional regression** was found: the new global filter silently scopes the **Admin** cross-org dashboard/CIN-compliance reads to the admin's own org (admins normally have `OrgId == null` post-backfill → empty results). The design's Security Notes explicitly required `IgnoreQueryFilters()` + audit for `AdminOnly` paths; that bypass was not implemented, and existing admin tests mask the regression because they build `AppDbContext` without a tenant context (filter disabled). This is **High, not Critical** (it is fail-closed — admins see *less*, never another org's data — so it is neither a security nor a data-corruption issue).

**Critical findings: 0** → the security/compliance/data-corruption exit condition is met. Three 🟡 High items require resolution or tracked-issue deferral before Stage 05 promotion (see §3 / §4).

---

## 2. Specialist verdicts

### 2a. code-reviewer
- **Migration correctness — PASS.** Step 1 adds nullable `OrgId` + `Orgs` table + indexes with a symmetric `Down()`. Step 2 is an idempotent relationship-walk backfill (`WHERE OrgId IS NULL`, deterministic slug `org-<sanitized>-<md5 prefix>`, `ON CONFLICT (Slug) DO NOTHING`, inactive `casazen-unassigned` fallback Org, `RAISE NOTICE` row counts) with a documented no-op `Down()` (per AC10b). Step 3 runs the **pre-flight NULL guard (`RAISE EXCEPTION`) before** the `SET NOT NULL` flip + `Restrict` FKs, with a tested `Down()` reverting FKs and nullability. Ordering asserted last-three (AC5).
- **Entitlement logic — PASS.** Tier→limit map with config override (`Entitlement:Tiers:{tier}:MaxProperties`), Starter fallback, correct boundary (`count < max`). Good unit coverage.
- **Async/EF — PASS.** No `.Result`/`.Wait()`/`async void`; I/O is `async Task` with `CancellationToken` propagation in services. `TenantContext.OrgId` uses a synchronous `FirstOrDefault` inside the property getter (required for EF filter evaluation; documented, not a deadlock pattern) and a **separate DI scope** to avoid query-filter reentrancy (sound — `Users` carries no filter).
- **DI/SOLID — PASS.** `ITenantContext`/`IEntitlementService`/`IOrgService` registered `Scoped`; constructor injection; small focused classes; `AppDbContext` receives `ITenantContext` via DI with a `NullTenantContext` fallback for design-time/jobs/tests.
- **Regression (High):** Admin cross-org reads (see F-H1). **Test fidelity (Medium):** backfill is simulated in LINQ, not executed as real Postgres SQL (see F-M2).

### 2b. security-auditor
- **G5 IDOR — PASS.** `OrgId` resolved from `sub` (with `NameIdentifier` fallbacks) server-side; global filter `!FilterEnabled || OrgId == _tenant.OrgId`; cross-org id → 404, list → empty (integration tests `AC7_*`); writes set `property.OrgId = tenantContext.OrgId`, ignore body; fail-closed when caller org is null. No client-supplied/route/body tenant key, no `IgnoreQueryFilters()` read-path escape.
- **G6 raw SQL — PASS.** Zero `FromSqlRaw`/`ExecuteSqlRaw`/interpolated SQL in the tree. Migrations use `migrationBuilder.Sql(...)` with **static** SQL (owner values are handled inside SQL via column references, not C# string concatenation).
- **G7 PII — PASS.** No `DocumentNumber`/`DateOfBirth`/`Nationality`/`FullName` in new error responses or logs; create-path logs only `UserId`/`OrgId`/`PlanTier`/`limit`. `OrgSummaryDto` is a whitelist (id, name, slug, planTier) — excludes `Stripe*Id` and `ContactEmail`.
- **G8 Stripe — N/A.** `StripeWebhookHandler` untouched. `Org.StripeCustomerId`/`StripeConnectedAccountId` are non-secret references and are not exposed in any DTO.
- **G9 GDPR — N/A.** `Guest` not modified (no `OrgId` on Guest per AC2); no new guest-creation flow; consent/retention fields unchanged. Background/Hangfire jobs are unaffected by the filter (no `HttpContext` → `FilterEnabled == false`), so the cross-org retention path is preserved.
- **G10 FE auth routes — PASS.** No new top-level authenticated route; `OrgBadge` mounts in the existing `<ProtectedRoute>`-wrapped shell; create page is an existing guarded route; the upgrade CTA only links to `/app/billing/upgrade` (owned by `spec-saas-billing`).

---

## 3. Findings by severity

### 🔴 Critical — 0

### 🟡 High — 3

**F-H1 — Admin cross-org dashboard & CIN-compliance reads regressed by the global filter** _(backend)_
- `Casazen.Infrastructure/Services/AdminService.cs:26` (`dbContext.Properties.ToListAsync()`), `:37`–`:45` (`Bookings`/`Payments` aggregates), `:81` (CIN report `Properties.ToListAsync()`).
- Reached via `Casazen.Web/Controllers/AdminController.cs:18` (`GetStats`) and `:49` (`GetCinCompliance`), both `[Authorize(Policy="AdminOnly")]`.
- Root cause: `AppDbContext` `OnModelCreating` global `HasQueryFilter` on the four tables + `TenantContext.FilterEnabled => User.Identity.IsAuthenticated`, with **no `IgnoreQueryFilters()`** anywhere. An authenticated admin has `FilterEnabled == true`; admins normally own no properties so `User.OrgId == null` → filter matches nothing → **stats return all zeros and the CIN-compliance report returns empty**; an admin who happens to own properties sees only their own org. This contradicts design §Security Notes ("Admin endpoints … use `IgnoreQueryFilters()` … logged via `IAdminAccessAuditService`").
- **Masked by tests:** `Casazen.Tests/Unit/Services/AdminServiceTests.cs:18` builds `new AppDbContext(options)` → `NullTenantContext` → filter disabled, so the 440-test suite stays green.
- **Severity rationale:** fail-closed (no cross-org data exposure, no corruption) → High, not Critical. But it deterministically breaks a shipped admin feature and a regulatory (CIN, D.L. 145/2023) *monitoring* surface.
- **Exact fix:** add `.IgnoreQueryFilters()` to the three admin cross-org queries in `AdminService` (with the existing `IAdminAccessAuditService` audit), and add a regression test that constructs `AppDbContext` with a `FilterEnabled` tenant context and asserts admin reads remain cross-org. → Recommend **Stage 03 fix loop**.

**F-H2 — Step 3 NOT-NULL/FK flip is not lock-light; deviates from the AC10b zero-downtime claim** _(backend)_
- `Casazen.Infrastructure/Migrations/20260609101050_MakeOrgIdRequired.cs:5624`–`:5698`: `AlterColumn(... nullable:false)` emits `SET NOT NULL` (full-table scan under ACCESS EXCLUSIVE) and `AddForeignKey(...)` validates immediately (SHARE ROW EXCLUSIVE). Design Migration Plan promised Deploy 3 as "fast metadata ops" and explicitly recommended `NOT VALID` → `VALIDATE CONSTRAINT` for large tables.
- **Severity rationale:** correct migration, but on a populated production DB (Supabase) this can lock; negligible at current data volume.
- **Exact fix / decision:** adopt the `... ADD CONSTRAINT ... NOT VALID` + later `VALIDATE CONSTRAINT` pattern (and a validated `CHECK (OrgId IS NOT NULL) NOT VALID` → validate → `SET NOT NULL`) for the four tables. → **Deferrable** with a tracked issue (ops/scale hardening) and a conscious go/no-go before running Deploy 3 on prod.

**F-H3 — Frontend `e2e` CI check is RED; PR body overstates "0 failed"** _(frontend / CI)_
- `casazen/frontend` run [27203870849](https://github.com/casazen/frontend/actions/runs/27203870849/job/80314467476): `e2e/pricing-adapter.spec.ts:113` (AC18) fails with `net::ERR_CONNECTION_REFUSED` / "No response from server" → `1 failed, 7 skipped, 37 passed`; this drives `mergeStateStatus = UNSTABLE`.
- The failing spec is **pre-existing and unrelated to #202** (pricing-adapter; backend unreachable in demo mode → environmental/flaky). The **two new #202 specs pass** (`tenant-boundary.spec.ts` AC11/AC12).
- The PR body's gate table claims `npm run test:e2e → 38 passed, 7 skipped, 0 failed`, which does not match CI.
- **Exact fix / decision:** re-run e2e (likely transient) or quarantine + track the flaky pricing-adapter spec; correct the PR body. → **Deferrable** with a tracked issue (not a #202 defect), but must be green or explicitly waived before Stage 05.

### 🟢 Medium — 2

**F-M1 — Entitlement check is TOCTOU; concurrent creates can exceed the cap by one** _(backend)_
- `Casazen.Web/Controllers/PropertiesController.cs` `Create`: `CanAddPropertyAsync` then insert, with no transaction/serialization or DB constraint. Design offered a `409` race alternative (the FE already handles 403/409); not implemented server-side. Low practical impact (one owner per org in Phase 1). Consider a transactional re-check or a partial unique/count guard.

**F-M2 — Backfill test fidelity: real Postgres SQL is never executed in CI** _(backend)_
- `Casazen.Tests/Integration/OrgBackfillSimulationTests.cs:258`–`:302` re-implements the backfill in LINQ on the EF in-memory provider and uses a **different** slug (`org-{owner}`) than the shipped migration (`org-<sanitized>-<md5 prefix>`); `Casazen.Tests/Unit/Infrastructure/MigrationSqlTests.cs` only asserts SQL *substrings*. The actual `BackfillDefaultOrgs` SQL is therefore never run against Postgres (no Docker/Testcontainers in the harness). Recommend a Testcontainers-Postgres migration test to exercise the real SQL (slug derivation, relationship walk, idempotency).

### ⚪ Low — 3 (document only)

- **F-L1** `Casazen.Infrastructure/Migrations/20260609100413_BackfillDefaultOrgs.cs:3789` — comment claims distinct owners "can never collide" onto one Org; an 8-hex `md5` prefix (~2^32) makes a collision astronomically unlikely but not impossible, and `ON CONFLICT DO NOTHING` would merge colliders. Negligible; consider a longer suffix or a uniqueness assertion.
- **F-L2** `casazen/frontend` `property-create-page.tsx` switches the title to Italian ("Crea proprietà") while the submit button stays English ("Create Property", `property-form.tsx`); the e2e asserts the English label. Minor i18n inconsistency.
- **F-L3** `PropertiesController.Create` calls `CanAddPropertyAsync`/`GetEntitlementAsync` without a `CancellationToken` (the action exposes none). Trivial.

### Context (not a new finding)

- **Pre-existing G7 ESLint baseline (Stage 03):** `npm run lint` (`eslint .`) reports ~47 errors / 8 warnings repo-wide, all pre-existing and unrelated to #202 (byte-identical-to-`develop` files fail lint). All #202-authored files lint clean; the only two errors in touched files are on pre-existing lines (`property-form.tsx:53` `as any`, `properties-page.tsx:36` unused catch). Tracked for a separate repo-wide lint-cleanup chore. **Carried as known baseline, not a #202 regression.**

---

## 4. Per-gate status

| # | Gate | Status | Evidence |
|---|---|---|---|
| G1 | Both PRs `MERGEABLE` | ✅ PASS (note) | `#203` MERGEABLE/CLEAN; `#104` MERGEABLE/**UNSTABLE** (failing non-required `e2e` check → F-H3) |
| G2 | Zero open 🔴 critical | ✅ PASS | 0 critical |
| G3 | All 🟡 high resolved/deferred | ⚠️ OPEN | 3 highs: F-H1 → **Stage 03 fix** (recommended); F-H2, F-H3 → defer with tracked issues. Must be closed/waived before Stage 05 |
| G4 | Cross-repo consistency | ✅ PASS | `orgs.api.ts` `GET /orgs/me/entitlement` ↔ `OrgsController` `EntitlementDto`; `useCurrentUser` org ↔ `UserDetailDto.Org`/`OrgSummaryDto`; `isPlanLimitError` 403/409 `plan_limit_reached` ↔ BE 403 payload; `PlanTier` union ↔ enum |
| G5 | No IDOR | ✅ PASS | Server-side `OrgId` from principal; global filter; cross-org 404/empty; fail-closed; writes server-set |
| G6 | No raw concat SQL | ✅ PASS | No `FromSqlRaw`/`ExecuteSqlRaw`; migrations use static `migrationBuilder.Sql` |
| G7 | No PII exposure | ✅ PASS | No Guest PII in errors/logs; whitelisted `OrgSummaryDto` (pre-existing lint baseline noted as context) |
| G8 | Stripe signature | ➖ N/A | `StripeWebhookHandler` not touched; Stripe ids non-secret, not exposed |
| G9 | GDPR fields | ➖ N/A | `Guest` untouched (no `OrgId` per AC2); jobs unaffected (no `HttpContext` → filter off) |
| G10 | FE auth routes guarded | ✅ PASS | No new authenticated route; org UI inside existing `<ProtectedRoute>` shell |

---

## 5. Exit decision

- **Critical findings: 0** → the exit gate's hard condition (security/compliance/data-corruption = 0) is **CLEARED**.
- **Not merged, not approved-for-merge** (review comments only).
- **Required before Stage 05 promotion (G3):**
  1. **F-H1 (admin cross-org regression)** — recommend a **Stage 03 fix loop** (add audited `IgnoreQueryFilters()` + regression test). This is the consequential item.
  2. **F-H2 (FK/NOT-NULL lock)** and **F-H3 (flaky e2e red + PR-body mismatch)** — defer with tracked issues, or fix.

**Critical findings: 0**

---

## 6. Iteration 2 — Re-review (2026-06-09)

**Fixes applied on `feature/202-tenant-boundary` (backend commit `46665ee`):**

| Finding | Resolution |
|---|---|
| **F-H1** Admin cross-org reads regressed | ✅ Fixed — `AdminService` uses `.IgnoreQueryFilters()` + audit log; regression tests with real authenticated `ITenantContext`. |
| **F-H2** Validating NOT NULL + FK (table locks) | ✅ Fixed — `MakeOrgIdRequired` rewritten to AC10b zero-downtime pattern. |
| **F-H3** Pre-existing e2e CI failure | ⏭️ Deferred → casazen/frontend#105 |
| **F-M1** Entitlement TOCTOU | ⏭️ Deferred → casazen/backend#204 |
| **F-M2** Backfill test fidelity | ⏭️ Deferred → casazen/backend#205 |

**Backend gates re-run:** 442 passed · format clean · build /warnaserror 0 warnings · ef migrations script OK.

**Stage 04: PASS** — cleared for Stage 05 promotion.

**Critical findings: 0**
