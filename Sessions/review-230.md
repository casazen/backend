# Review: Issue #230 — SaaS Subscription Billing

## PR
- Backend: #272 `feature/230-saas-billing` → `develop`
- Frontend: N/A (frontend ACs deferred to separate issue)

## Iteration 1 findings → all resolved in commit `a35771d`

### 🔴 Critical (fixed)

| # | File | Line | Issue | Fix |
|---|---|---|---|---|
| C1 | `StripeWebhookHandler.cs` | 33–103 | Non-transactional idempotency check: concurrent Hangfire workers could both execute the same event before either inserted the dedup record. Connected events had zero replay protection. | Moved `ProcessedStripeEvent` insert **before** business logic; `DbUpdateException` on PK violation = already-processed; extended to `WebhookSource.Connected`; guard for null `EventId`. |
| C2 | `BillingDtos.cs:41`, `BillingController.cs:187` | — | `StripeCustomerId` exposed in `GET /api/billing/subscription` response — Stripe internal reference should not reach clients. | Removed `StripeCustomerId` from `SubscriptionDto` and the `Map()` helper. |

### 🟡 High (resolved or deferred)

| # | File | Issue | Resolution |
|---|---|---|---|
| H1 | `UsersController.cs:236–243` | `X-Forwarded-For` spoofable; consent IP evidence untrusted | Deferred — tracking issue #273 |
| H2 | `OrgsController.cs:65–104` | Self-serve plan upgrade bypasses Stripe billing when `SubscriptionId` is empty | Deferred — tracking issue #274 |
| H3 | `BillingEntryGate.cs:20–25` | `sk_test_*` key silently bypassed SDI/VAT gate in Production | Fixed: throws `BillingGateClosedException` when `IsProduction()` + test key |
| H4 | `appsettings.json:12` | `Auth0:ClientSecret` placeholder committed — normalises including secrets in VCS | Fixed: field removed from committed config |

### 🟢 Medium (documented, no block)

| # | File | Issue |
|---|---|---|
| M1 | `ConsentRecord.cs:24` | IP address stored without retention/erasure policy — tracked under #273 |
| M2 | `StripeWebhookHandler.cs:96` | `RecordProcessedEventAsync` hard-coded `Platform` source — fixed as part of C1 refactor (TryClaimEventAsync now passes `source`) |
| M3 | `BillingController.cs:34` | Stripe Price IDs in plans endpoint — acceptable for MVP; server-side only after Stripe key rotation is standard practice |
| M4 | `LegalController.cs:11` | Anonymous legal endpoints lack rate-limiting — add to backlog |

### ⚪ Low (noted)

| # | Issue | Status |
|---|---|---|
| L1 | `StripeWebhookHandler.cs.bak` untracked file | Deleted (never committed) |
| L2 | Inconsistent auth policy: `GetMyEntitlement` requires short-rent context, `UpdateMyPlan` bare `[Authorize]` | Tracked under #274 |
| L3 | `OrgBillingAdminAuthorizationHandler` resolves org independently — future IDOR risk if reused with org-ID params | Noted in code comment |

## Gate Status (post-fixes)

| Gate | Status |
|---|---|
| G1: PR mergeable | ✅ MERGEABLE |
| G2: 0 critical findings | ✅ (C1, C2 fixed) |
| G3: High findings addressed or deferred with issue | ✅ (H1→#273, H2→#274, H3 fixed, H4 fixed) |
| G4: Cross-repo consistency (FE N/A for this PR) | ✅ N/A |
| G5: No IDOR — billing endpoints use IOrgContextResolver | ✅ |
| G6: No raw SQL | ✅ (grep: 0 string-concatenated ExecuteSqlRaw) |
| G7: PII not in error responses | ✅ |
| G8: Stripe signature verified in WebhookHandler | ✅ (not bypassed) |
| G9: GDPR fields — ConsentRecord is platform data, not guest PII | ✅ N/A for guest GDPR fields |
| G10: Frontend auth routes (N/A — no FE in this PR) | ✅ N/A |

## Verdict

**APPROVED — ready for Stage 05**

All critical findings resolved. High findings H1/H2 deferred with tracking issues #273/#274.
Test suite: 537 pass, 16 pre-existing failures (unrelated to this feature), 25 skipped.
