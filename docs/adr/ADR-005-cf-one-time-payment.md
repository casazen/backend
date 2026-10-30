# ADR-005: ConcordatoFacile One-time Payment Model

**Status:** Superseded come riferimento del processo ConcordatoFacile (25 ottobre 2026; era Accepted)  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-02 (ConcordatoFacile one-time payment)

> **SUPERATO come riferimento del processo ConcordatoFacile (25 ottobre 2026).** Superato dal documento `docs/integrations/concordatofacile/README.md`: non usare come riferimento. Il file resta nel repository perché i commenti del codice e dei test citano gli identificativi delle sue regole (`ADR-005-Rn`): sono etichette, non requisiti validi. I fatti sul processo sono in quel documento, i requisiti in `Sessions/business-analysis-canone-concordato.md`.
>
> Punti che non valgono più o non sono attuati:
>
> - R1: i metadati sono `orgId`, `leaseId` e `userId`, scritti sulla sessione **e** sul PaymentIntent (`payment_intent_data`), mai con una chiave `kind` (CF-FIX, backend #602).
> - R7: si gestisce solo `charge.refunded` con rimborso totale (acquisto `Refunded`); `payment_intent.payment_failed` per CF e `RefundedAmountCents` non sono attuati (debito CF-FIX-FU2).
> - R10: la validazione di avvio vale in Production e in ogni ambiente che non sia Development o Testing.
> - Dopo l'ADR: 404 `lease_not_found` e 409 `concordatofacile_already_purchased` prima di chiamare Stripe, sessione `complete` e `paid` recuperata subito, interruttore `Features:ConcordatoFacile` (CF-FIX): vedi il §4 del documento di processo.

## Context

CasaZen sells access to ConcordatoFacile (CF) service for EUR 119,00 total VAT included per lease. This is a one-time payment, not a subscription. The payment grants the landlord access to generate a signed link to the CF portal for that specific lease.

**Requirements** (from `kb/intake/requirements.md` NF-02):
- EUR 119,00 total VAT included for all organizations (no VAT exemption branching per Q-A02)
- Stripe Checkout with mode=payment (not subscription)
- Amounts and currency re-verified server-side in webhook
- Pre-configured Stripe Price ID (Q-A01), not dynamic price_data

**Existing code** (from `kb/gap/gap-analysis.md`):
- `BillingCheckoutService` (`work/base/backend/Casazen.Infrastructure/Services/BillingCheckoutService.cs:17`) handles subscriptions
- `StripeWebhookHandler` (`work/base/backend/Casazen.Infrastructure/External/StripeWebhookHandler.cs:17-80`) processes webhook events
- `ProcessedStripeEvent` (`work/base/backend/Casazen.Core/Entities/ProcessedStripeEvent.cs:8-19`) provides idempotency
- Webhook signature verification in `WebhooksController.cs:22-47`

**Repo scope:** Backend (payment service, webhook handler), Frontend (offer page, checkout redirect)

**Mobile:** NOT-APPLICABLE - mobile has no billing or payment capabilities

## Decision

### Dedicated ConcordatoFacileCheckoutService

Create a separate `ConcordatoFacileCheckoutService` rather than extending `BillingCheckoutService`:

- Prevents subscription flow interference
- Creates Stripe Checkout session with `mode=payment` (not `mode=subscription`)
- Uses configured Price ID from `ConcordatoFacile:StripePriceId`
- Reuses existing session if pending purchase exists for (orgId, leaseId)

### Webhook Handling

Extend `StripeWebhookHandler` to process `payment_intent.succeeded` (not `checkout.session.completed`) to handle async payment methods (SEPA, bank transfer):

- Verify `PaymentIntent.Amount == 11900` AND `Currency == "eur"`
- Update `ConcordatoFacilePurchase.Status = Completed` and `PaidAt`
- Idempotency via `ProcessedStripeEvent` insert with `Source=ConcordatoFacile`
- Handle refund events (`charge.refunded`, `payment_intent.payment_failed`)

### Access Verification Service

Separate `IConcordatoFacileAccessService.CheckAccessAsync(leaseId)` reused by:
- Offer page (NF-02): determines payment vs gift button display
- Link generation (NF-05): verifies access before generating link
- Practice lookup (NF-06): authorizes CF API call

Access granted if ANY of:
1. `ConcordatoFacilePurchase.Status == Completed` for the lease
2. `ConcordatoFacileGift.ConsumedForLeaseId == leaseId`
3. User has Admin role

### Frontend Offer Page

- Route: `/app/long-rent/leases/:id/concordatofacile`
- Display: "119,00 EUR totali IVA compresa" (hardcoded display, backend controls Stripe Price)
- Checkout: POST to backend, redirect via `redirectToStripe(sessionUrl)`
- Gift availability embedded in `/access` response (single query, not separate endpoint)

### Rejected Alternatives

- **Extend BillingCheckoutService (Option B)**: Couples one-time purchase to subscription logic
- **Dynamic price_data (Option C)**: Creates live Stripe products during testing, violates spec constraint

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-005-R1 | P0 | `ConcordatoFacileCheckoutService.CreateCheckoutSessionAsync(orgId, leaseId)` MUST create Stripe Checkout session with `mode=payment`, `line_items` using configured `ConcordatoFacile:StripePriceId`, `success_url` and `cancel_url` preserving `leaseId`, `metadata={orgId, leaseId}` |
| ADR-005-R2 | P0 | Session reuse MUST query existing `ConcordatoFacilePurchase WHERE OrgId = orgId AND LeaseContractId = leaseId AND Status = Pending` before creating new session |
| ADR-005-R3 | P0 | Webhook handler MUST verify webhook signature using `StripeConfiguration.SignatureKey` before processing `payment_intent.succeeded` events (WebhooksController.cs:22-47 pattern) |
| ADR-005-R4 | P0 | Webhook handler MUST verify `PaymentIntent.Amount == 11900 AND Currency == "eur"` before granting access; amount mismatch MUST log error with orgId/leaseId and NOT grant access |
| ADR-005-R5 | P0 | Webhook handler MUST process `payment_intent.succeeded` events: verify metadata contains leaseId, update `ConcordatoFacilePurchase.Status=Completed + PaidAt=event.Created`, claim event via `ProcessedStripeEvent` insert with `Source=ConcordatoFacile` |
| ADR-005-R6 | P0 | Duplicate `ProcessedStripeEvent.EventId` insert conflict MUST be caught and treated as idempotent success (no double-credit) |
| ADR-005-R7 | P0 | Webhook MUST handle refund events (`charge.refunded`, `payment_intent.payment_failed`) updating `ConcordatoFacilePurchase.Status` and `RefundedAmountCents` without deleting CF practice or documents |
| ADR-005-R8 | P0 | `GET /api/leases/{leaseId}/concordatofacile/access` MUST return `{hasAccess: boolean, source: 'payment'\|'gift'\|'admin'\|null, giftAvailable: boolean, giftConsumed: boolean, purchasedAt?: ISO8601, giftAssignedAt?: ISO8601}` checking server-side purchase/gift/admin state only; MUST NOT trust `checkout=success` query param or client state |
| ADR-005-R9 | P0 | `POST /api/leases/{leaseId}/concordatofacile/checkout` MUST authorize User with LeaseCreate OR LeaseRead policy and verify leaseId ownership before creating session |
| ADR-005-R10 | P0 | Checkout configuration MUST validate `ConcordatoFacile:StripePriceId` exists at startup and throw `InvalidOperationException` if missing (startup fails in Production) |
| ADR-005-R11 | P0 | Frontend offer page MUST display "119,00 EUR totali IVA compresa" as fixed text, NOT derive from API |
| ADR-005-R12 | P0 | Frontend route `/app/long-rent/leases/:id/concordatofacile` MUST be added to route-manifest.ts with `requiredPermissions: ['lease.read', 'lease.create']` |
| ADR-005-R13 | P1 | i18n keys MUST exist in BOTH it.json and en.json: `concordatofacile.offer.{title, price, steps, payButton, giftButton, processing, error, success}` |
| ADR-005-R14 | P1 | data-testid values for Playwright: `cf-offer-price`, `cf-pay-button`, `cf-gift-button` |

## Consequences

**Follow-on work:**
- Backend: `ConcordatoFacileCheckoutService`, `IConcordatoFacileAccessService`, webhook extension, configuration validation
- Backend: Runbook for creating Stripe Price in test and prod dashboards
- Frontend: Offer page component, route registration, i18n keys
- Tests: PostgresFact PROOF TEST `PaymentIntentSucceeded_WebhookWithAmount11900_GrantsAccess` for money rule
- Tests: PostgresFact test for duplicate webhook idempotency

**Risks:**
- Stripe Price ID misconfiguration: wrong Price ID charges wrong amount (not caught by unit tests that mock Stripe SDK)
- Async payment methods (SEPA, bank transfer) settle later; `payment_intent.succeeded` is truth, not `checkout.session.completed`

**CI cost:** Backend PR ~20 files (service + controller + DTO + webhook extension + tests)

**Cross-repo contract:**
- Backend: `POST /api/leases/{leaseId}/concordatofacile/checkout` returns `{sessionId, sessionUrl}`
- Backend: `GET /api/leases/{leaseId}/concordatofacile/access` returns access state
- Frontend: Calls checkout endpoint, redirects via `redirectToStripe(sessionUrl)`
