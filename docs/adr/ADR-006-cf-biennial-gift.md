# ADR-006: ConcordatoFacile Biennial Subscription Gift

**Status:** Accepted  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-03 (ConcordatoFacile biennial subscription gift)

## Context

Organizations with a biennial CasaZen subscription receive one free ConcordatoFacile practice (gift). The gift can be consumed for any one lease. Concurrent consumption attempts from multiple tabs must be handled atomically.

**Requirements** (from `kb/intake/requirements.md` NF-03):
- One gift per organization with biennial subscription
- Gift consumption is atomic (only one lease can use it)
- Existing biennial subscriptions handled via manual admin review (Q-A04 intake answer)
- New biennial subscriptions receive gift automatically via webhook

**HITL answers:**
- Q-A03: Biennial plan does not exist today, must be created in Stripe dashboard
- Q-A04: Manual admin review for existing subscriptions

**Existing code** (from `kb/gap/gap-analysis.md`):
- `StripeWebhookHandler` (`work/base/backend/Casazen.Infrastructure/External/StripeWebhookHandler.cs`) processes subscription events
- PostgreSQL advisory locks in `UserRepository` (`work/base/backend/Casazen.Infrastructure/Repositories/UserRepository.cs:144-147`)
- `ProcessedStripeEvent` provides webhook idempotency

**Repo scope:** Backend (gift entity, webhook, concurrency), Frontend (gift button UI)

**Mobile:** NOT-APPLICABLE - mobile has no subscription management

## Decision

### Webhook-driven Grant for New Subscriptions

Extend `StripeWebhookHandler` to process `customer.subscription.created` events:

- Check if `subscription.items.data[0].price.id` is in configured `ConcordatoFacile:BiennialPriceIds` array
- If org does not already have a gift, insert `ConcordatoFacileGift` with `Status=Available`
- Idempotency via `ProcessedStripeEvent` prevents duplicate grants on event replay
- Unique constraint on `OrgId` prevents multiple gifts

### Manual Admin Endpoint for Existing Subscriptions

Per Q-A04 (intake Q-001), existing biennial subscriptions require manual admin review:

- `POST /api/admin/orgs/{orgId}/concordatofacile/grant-gift` with `[AdminOnly]` policy
- Accepts `{subscriptionId, reason}` body
- Records audit trail in activity log

### Atomic Gift Consumption with Advisory Lock

`ConcordatoFacileGiftService.ConsumeAsync(orgId, leaseId)`:

1. Acquire PostgreSQL advisory lock `pg_advisory_xact_lock(hashtext('concordato-gift-' || orgId))`
2. Query gift with `Status=Available AND ConsumedForLeaseId IS NULL`
3. Atomically update `Status=Consumed, ConsumedAt=UtcNow, ConsumedForLeaseId=leaseId`
4. Release lock on transaction commit
5. Return 409 `concordatofacile_gift_already_consumed` if gift unavailable

Re-entry allowed: if `ConsumedForLeaseId == leaseId` already, return 200 OK (same lease can re-enter CF portal).

### Frontend Gift Button

- Gift availability embedded in `/access` response (single query, not separate endpoint per adr-lead-fe recommendation)
- Show "Usa la pratica omaggio e continua" button when `giftAvailable=true && !giftConsumed`
- Handle 409 conflict with `role="alert"` message

### Rejected Alternatives

- **Automatic backfill for existing subscriptions**: Risk of granting gifts to expired/cancelled subscriptions
- **Separate `/gift-status` endpoint**: Extra network call, prefer embedding in `/access`

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-006-R1 | P0 | `StripeWebhookHandler` MUST process `customer.subscription.created` events: if `subscription.items.data[0].price.id IN ConcordatoFacile:BiennialPriceIds` AND org does not have gift, insert `ConcordatoFacileGift` with `Status=Available, GrantedBySubscriptionId=subscription.id, GrantedAt=event.created` |
| ADR-006-R2 | P0 | Webhook handler MUST check `Subscription.Items[].Price.Id IN configured BiennialPriceIds` array before granting gift; non-biennial subscription MUST NOT grant gift |
| ADR-006-R3 | P0 | Gift assignment MUST be idempotent: if `subscription.created` event replayed (same EventId in `ProcessedStripeEvent`) OR org already has gift (unique OrgId constraint), skip grant and log "gift already assigned" |
| ADR-006-R4 | P0 | `ConcordatoFacileGiftService.ConsumeAsync(orgId, leaseId)` MUST acquire PostgreSQL advisory lock `pg_advisory_xact_lock(hashtext('concordato-gift-' || orgId::text))` before checking availability; lock MUST be transaction-scoped |
| ADR-006-R5 | P0 | Gift consumption MUST be atomic: `UPDATE ConcordatoFacileGifts SET ConsumedForLeaseId = @leaseId, ConsumedAt = @now, Status = 'Consumed' WHERE OrgId = @orgId AND ConsumedForLeaseId IS NULL RETURNING *`; zero rows affected MUST return 409 `concordatofacile_gift_already_consumed` |
| ADR-006-R6 | P0 | Re-entry before submission MUST allow same leaseId: if `ConsumedForLeaseId = leaseId` already, return 200 OK with `source=gift` (allow re-entry without consuming again) |
| ADR-006-R7 | P0 | Gift consumption MUST record `LeaseEvent` with `Type=ConcordatoFacileGiftConsumed`, UserId, OrgId, LeaseContractId, timestamp for audit trail |
| ADR-006-R8 | P0 | Configuration `ConcordatoFacile:BiennialPriceIds` MUST be validated at startup: if missing OR empty in Production, fail startup with "ConcordatoFacile:BiennialPriceIds required in Production" |
| ADR-006-R9 | P0 | `POST /api/admin/orgs/{orgId}/concordatofacile/grant-gift` with `[AdminOnly]` policy MUST accept `{subscriptionId, reason}` body, insert `ConcordatoFacileGift` with audit trail (CreatedBy=admin userId, reason in activity log), return 409 if org already has gift |
| ADR-006-R10 | P0 | Frontend MUST show "Usa la pratica omaggio e continua" button when `giftAvailable=true && !giftConsumed` in `/access` response, calling `POST /api/leases/{id}/concordatofacile/consume-gift` |
| ADR-006-R11 | P0 | Gift consumption 409 conflict MUST display `role="alert"` message "La pratica omaggio gia stata utilizzata" with `data-testid="gift-conflict"` |
| ADR-006-R12 | P1 | Migration `AddConcordatoFacileGift` MUST default all existing orgs to no gift (no rows, existing subscriptions handled via admin endpoint) |
| ADR-006-R13 | P1 | i18n keys: `concordatofacile.gift.{available, button, consumed, conflict, error}` (it/en) |

## Consequences

**Follow-on work:**
- Backend: `ConcordatoFacileGiftService`, webhook extension, admin endpoint
- Backend: Configuration validation with `IValidateOptions` pattern
- Backend: Runbook for creating biennial Stripe Price in test and prod dashboards
- Frontend: Conditional gift button UI, conflict handling
- Tests: PostgresFact CONCURRENCY TEST `ConsumeGiftAsync_ConcurrentRequests_OnlyOneSucceeds` runs 5 times in CI to catch race conditions
- Tests: PostgresFact test for duplicate webhook idempotency

**Risks:**
- Biennial Price ID misconfiguration: wrong Price ID does not grant gift (webhook silently skips)
- Subscription renewal does not re-grant gift (R3 idempotency check prevents)
- Gift button visibility race: user opens page, gift available; concurrent webhook assigns to another lease; user clicks -> 409 (acceptable per spec)

**CI cost:** +10s for concurrency test (5 runs x 2s)

**Cross-repo contract:**
- Backend: `/access` response includes `{giftAvailable: boolean, giftConsumed: boolean}`
- Backend: `POST /api/leases/{leaseId}/concordatofacile/consume-gift` returns access state or 409
- Frontend: Calls consume endpoint on gift button click
