---
round: 2
role: product-architect
vote: APPROVE
---

## Product Architect — Round 2 Response

**Vote**: APPROVE

**Reasoning**:
draft-v2 resolves all six Round 1 OBJECT conditions accurately and against the real codebase. Phase 0 is rebuilt on the verified baseline (22 entities / 16 controllers / 10 jobs / Supabase Postgres) and enumerates the LTR lease+RLI+e-sign subsystem and the context-RBAC with the correct `RequireContext:{ctx}:{perm}` convention. LTR is rescoped to complete+verify with the recurring-rent ledger named as the true gap and moved to a genuinely parallel Phase 1.5; `spec-direct-booking-engine` is split into read-model + checkout + branded-site with the raw-`Property` leak as the seed-not-shortcut rationale. Multi-tenancy split correctly (tenant-boundary P1 + org-seats P2), billing stays a P1 gate, inbox gains real entities + async job + per-adapter caveat, `IChannelAdapter` fixed throughout.

**Details**:
- **Condition check**: (1) Resolved — accurate Phase 0 baseline + LTR + context-RBAC. (2) Resolved — Phase 1.5 parallel, recurring-rent gap. (3) Resolved — 3-way split, seed-not-shortcut. (4) Resolved — tenant-boundary P1 (+entitlement, OrgId FKs) / org-seats P2, billing P1. (5) Resolved — Conversation/Message/Thread + InboundMessageIngestionJob + per-adapter variance. (6) Resolved — IChannelAdapter consistent, doc-drift noted.
- **OrgId FK merge-order answer**: one-directional migration-sequencing dependency only (not a dev-track blocker). Land `spec-tenant-boundary` migration first (nullable → backfill one default Org per OwnerId → NOT NULL + FK). Phase 1.5 migrations rebase onto it (single `AppDbContextModelSnapshot.cs` + linear `__EFMigrationsHistory` will collide at snapshot — regenerate, never hand-merge). New Phase 1.5 tables (RentLedgerEntry/RentSchedule) carry `OrgId` from creation. Code/design stay parallel; only migration order serialized.
- **Residual non-blocking notes**: (a) add cross-cutting invariant to `spec-tenant-boundary` — every new tenant-scoped table inherits OrgId + entitlement (so the parallel rent ledger can't ship un-scoped); (b) `spec-saas-billing` (Stripe Billing, platform account) and `spec-direct-checkout` (Stripe Connect, operator MoR) share `WebhooksController`/`StripeWebhookHandler` — separate platform-account vs connected-account event routing; (c) Phase 1 internal ordering: readmodel→checkout, tenant-boundary→branded-site & →saas-billing; (d) naming nit: Phase 0 cites `ContextAuthorizationService`; verified services are the `RequireContext` policy builder + `PropertyAuthorizationService` — claim holds.
