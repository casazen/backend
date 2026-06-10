# Stage 04 — Review · Issue #224 Stripe Connect Onboarding

> Coordinator: Stage 04 Review · Specialists synthesized: `code-reviewer` + `security-auditor`
> PRs: backend [`casazen/backend#223`](https://github.com/casazen/backend/pull/223) · frontend [`casazen/frontend#117`](https://github.com/casazen/frontend/pull/117)
> Base: `develop` · Head: `feature/222-connect-onboarding` · Design contract: `Sessions/design-224.md`
> Date: 2026-06-10

---

## 1. Cross-repo summary

The change delivers Stripe Connect Express onboarding end-to-end as specified: backend `ConnectController` (`POST /api/connect/account`, `POST /api/connect/onboarding-link`, `GET /api/connect/status`), `ConnectOnboardingService` + `StripeConnectGateway`, capability columns on `Org` via migration `20260610055007_AddConnectStatusFields`, RF2 Connect webhook ingress at `POST /webhooks/stripe/connect` (separate `Stripe:ConnectWebhookSecret`), and frontend `ConnectPaymentsPage` at `/app/short-rent/settings/payments` with Italian UX states, return/refresh handling, and checkout-gate banner.

**Tenant isolation and auth are correct.** `OrgId` is resolved server-side from `ITenantContext`; management endpoints require `RequireContext:short-rent:property.write`; Connect status DTO exposes only account id, boolean capability flags, and requirement field names — no KYC/bank PII. Webhook authenticity uses `EventUtility.ConstructEvent` with the Connect-specific secret; `account.updated` updates org flags by `StripeConnectedAccountId` lookup (no cross-tenant client input).

**Frontend contract matches backend.** `connect.api.ts` calls the three Connect endpoints with camelCase payloads identical to `ConnectDtos.cs`. Route manifest registers `property.write` guard; page implements AC8–AC10 with Playwright e2e coverage.

**Scope note:** Backend PR #223 bundles a second commit (`fix(bookings): accept CreateBookingRequest DTO`) unrelated to Connect onboarding. It is a sound hardening change (server-side pricing, guest resolution) but increases review surface; not a blocker.

**Issue numbering:** PR titles/commits reference **#222**; design spec and pipeline state use **#224**. Functionally the same feature; align issue labels before Stage 05 release notes.

**Critical findings: 0** → security/compliance exit condition met. Two 🟢 medium coverage/hardening items deferred (see §3).

---

## 2. Specialist verdicts

### 2a. code-reviewer

- **AC1–AC3 — PASS.** `EnsureExpressAccountAsync` is idempotent (skips create when `StripeConnectedAccountId` set); Express account requests `card_payments` + `transfers`; Account Link uses `type = account_onboarding`; status refresh pulls Stripe snapshot when `refresh=true`. Integration tests `AC1_*`, `AC2_*`, `AC3_*` green (3/3).
- **AC4 — PASS (implementation) / PARTIAL (tests).** `WebhooksController.StripeConnectWebhook` verifies `Stripe:ConnectWebhookSecret`, enqueues `StripeWebhookJob`, `StripeWebhookHandler.HandleAccountUpdatedAsync` → `ApplyAccountUpdatedAsync` persists flags. No automated test for the webhook ingress → handler → DB path (see F-M1).
- **AC5 — DEFERRED (by design).** Charge gate is a downstream contract for `spec-direct-checkout` / `spec-ltr-recurring-rent`; not in #224 scope per design §C. AC10 FE banner provides operator-facing guard.
- **AC6 — PASS.** Single `Org` connected account; no separate landlord onboarding API.
- **AC7 — PASS.** Only `StripeConnectedAccountId` + capability flags + requirement key names stored.
- **Migration — PASS.** Four boolean/text columns with `defaultValue: false` / nullable JSON; symmetric `Down()`; ordering asserted as fourth-of-last in `MigrationSqlTests`.
- **Async/SOLID — PASS.** `async Task` with `CancellationToken` propagation; gateway/service/controller split; EF queries parameterized.
- **DI hygiene (Medium):** `IStripeConnectGateway`, `IConnectOnboardingService`, and `StripeWebhookHandler` registered in both `ServiceCollectionExtensions` and `Program.cs`; handler lifetime conflicts (`Singleton` vs `Scoped`) — last `Program.cs` `AddScoped` wins at runtime, but confusing (F-M2).

### 2b. security-auditor

- **G5 IDOR — PASS.** `ConnectController.RequireOrgId()` uses `ITenantContext.OrgId` only; no client-supplied org/account id; `ApplyAccountUpdatedAsync` resolves org by stored `StripeConnectedAccountId`.
- **G6 raw SQL — PASS.** `grep FromSqlRaw|ExecuteSqlRaw` in `Casazen.Infrastructure` → 0 matches. Migration uses EF `AddColumn` only.
- **G7 PII — PASS.** `ConnectStatusDto` whitelist: account id, booleans, requirement field names. No document content, bank details, or operator identity in API/logs. `connectedAccountId` shown in operator settings UI (non-secret Stripe reference; acceptable).
- **G8 Stripe signature — PASS.** `WebhooksController.StripeConnectWebhook` (lines 89–129): requires `Stripe-Signature`, `EventUtility.ConstructEvent` with `Stripe:ConnectWebhookSecret`; `400` on bad signature; `500` if secret missing; Hangfire async ack ≤3s. RF2 ingress separation from platform `/webhooks/stripe` (`Stripe:WebhookSecret`) confirmed.
- **G9 GDPR — N/A.** Connect flow does not create/modify `Guest` entities; no new guest PII persistence.
- **G10 FE auth — PASS.** `/app/short-rent/settings/payments` in `route-manifest.ts` with `requiredPermissions: ['property.write']`; rendered inside `<ProtectedRoute>` + `ContextRouteGuard` per `routes/index.tsx`.

---

## 3. Findings by severity

### 🔴 Critical — 0

### 🟡 High — 0

### 🟢 Medium — 3

**F-M1 — AC4 webhook path lacks integration test** _(backend)_
- `WebhooksController.StripeConnectWebhook` → `StripeWebhookJob` → `StripeWebhookHandler.HandleAccountUpdatedAsync` → `ConnectOnboardingService.ApplyAccountUpdatedAsync` is implemented but not covered by an integration test (signature construct + org flag persistence).
- **Risk:** regression on RF2 Connect ingress could go unnoticed.
- **Fix:** add test with constructed `Stripe-Signature` (or test helper) posting `account.updated` to `/webhooks/stripe/connect`, assert org flags updated.
- **Decision:** **Defer** — implementation mirrors existing platform webhook pattern; not a functional defect.

**F-M2 — Duplicate / conflicting DI registrations** _(backend)_
- `ServiceCollectionExtensions.AddCasazenExternalServices` registers `StripeWebhookHandler` as `Singleton` and Connect services as `Scoped`; `Program.cs` re-registers all three as `Scoped`.
- **Risk:** future maintainer removes `Program.cs` override → captive dependency (singleton handler holding scoped `IConnectOnboardingService`).
- **Fix:** single registration site with consistent `Scoped` lifetime for `StripeWebhookHandler`.
- **Decision:** **Defer** — runtime resolves `Scoped` (last registration wins); CI green.

**F-M3 — No server-side `returnUrl`/`refreshUrl` origin validation** _(backend)_
- `ConnectController.CreateOnboardingLink` validates non-empty only; design notes FE supplies same-origin URLs but does not mandate server allowlist.
- **Risk:** authenticated operator could pass arbitrary URLs into Stripe Account Link (limited — affects their own onboarding redirect).
- **Fix:** validate URLs against configured app origin(s) or relative-path policy.
- **Decision:** **Defer** — operator-only surface; low exploitability.

### ⚪ Low — 2

**F-L1 — Backend PR scope creep (booking DTO fix)** _(backend)_
- Commit `1d28433` refactors `BookingsController.Create` to `CreateBookingRequest` DTO with guest resolution and server-side pricing — valuable but orthogonal to Connect. Increases merge risk; unit tests added (`BookingsControllerTests`).

**F-L2 — Issue/branch numbering mismatch** _(process)_
- PRs/commits label **#222**; design/pipeline **#224**; branch `feature/222-connect-onboarding` vs design `feature/224-stripe-connect-onboarding`. Align before release changelog.

---

## 4. Gate status

| # | Gate | Status | Evidence |
|---|---|---|---|
| G1 | PR(s) `MERGEABLE` | ✅ PASS | `#223` MERGEABLE · `#117` MERGEABLE |
| G2 | Zero open 🔴 critical | ✅ PASS | 0 critical findings |
| G3 | All 🟡 high resolved/deferred | ✅ PASS | 0 high; 3 medium deferred with rationale (§3) |
| G4 | FE API matches BE contract | ✅ PASS | `connect.api.ts` ↔ `ConnectController`/`ConnectDtos`; types ↔ `ConnectStatusDto` |
| G5 | No IDOR | ✅ PASS | `ITenantContext.OrgId`; webhook org lookup by stored account id |
| G6 | No raw concat SQL | ✅ PASS | 0 `FromSqlRaw`/`ExecuteSqlRaw` in `Casazen.Infrastructure` |
| G7 | No PII exposure | ✅ PASS | Capability flags + requirement keys only; no KYC/bank data |
| G8 | Stripe signature verified | ✅ PASS | `StripeConnectWebhook` HMAC via `ConnectWebhookSecret`; not bypassed |
| G9 | GDPR fields (guest flows) | ➖ N/A | No guest creation in Connect endpoints |
| G10 | FE auth routes guarded | ✅ PASS | `ProtectedRoute` + `ContextRouteGuard` + `property.write` |

**CI:** Backend `#223` Build & Test ✅ · Frontend `#117` e2e ✅ (41 passed incl. new `connect-onboarding.spec.ts`).

---

## 5. AC coverage

| AC | Description | Status | Evidence |
|---|---|---|---|
| AC1 | Idempotent Express account create | ✅ PASS | `ConnectOnboardingService.EnsureExpressAccountAsync`; test `AC1_CreateAccount_IsIdempotent_AndPersistsConnectedAccountId` |
| AC2 | Account Link onboarding URL | ✅ PASS | `CreateAccountOnboardingLinkAsync`; test `AC2_OnboardingLink_ReturnsUrl`; FE e2e mints link |
| AC3 | Status with capability flags + refresh | ✅ PASS | `GetStatusAsync`; test `AC3_GetStatus_ReturnsCapabilityFlags`; FE `useConnectStatus` |
| AC4 | Connect webhook `account.updated` | ✅ PASS (impl) | `WebhooksController.StripeConnectWebhook` + `HandleAccountUpdatedAsync`; test gap F-M1 |
| AC5 | Downstream charge gate (`409`) | ➖ DEFERRED | By design — `spec-direct-checkout` / `spec-ltr-recurring-rent`; FE AC10 banner |
| AC6 | LTR landlord same Org account | ✅ PASS | No separate landlord API; shared `Org` fields |
| AC7 | No KYC/bank storage | ✅ PASS | Org stores id + flags + requirement JSON keys only |
| AC8 | Pagamenti settings page + CTA | ✅ PASS | `payments-page.tsx`; e2e disconnected/pending/active states |
| AC9 | Return/refresh + requirements prompt | ✅ PASS | `stripe_return`/`stripe_refresh` query handling; e2e requirements alert |
| AC10 | Checkout gate banner | ✅ PASS | `connect-checkout-gate-banner`; e2e asserts visibility when `!chargesEnabled` |

---

## 6. Exit decision

| Metric | Value |
|---|---|
| **Critical findings** | **0** |
| **High findings** | **0** |
| **Gate G1–G10** | **ALL PASS** (G9 N/A) |
| **Review status** | **✅ APPROVED for Stage 05** |

**Recommendation:** **Proceed to Stage 05** — merge backend #223 and frontend #117 to `develop`, configure `Stripe:ConnectWebhookSecret` in staging before enabling Connect webhook in Stripe Dashboard, apply migration `AddConnectStatusFields`.

**Pre-merge housekeeping (non-blocking):**
1. Track F-M1 (AC4 webhook integration test) as follow-up hardening.
2. Consolidate DI registrations (F-M2).
3. Align issue numbering (#222 vs #224) in PR titles/release notes.
4. Confirm `Stripe:ConnectWebhookSecret` env var on Railway staging/production per design Migration Plan step 2.

---

**Review file:** `Sessions/review-224.md`
