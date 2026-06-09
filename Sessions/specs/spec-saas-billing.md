# Spec — SaaS Subscription Billing (Platform Account) (US-005)

## Overview

Today Stripe charges **guests** (booking payments); there is **no subscription billing to charge
CasaZen's own customers**. This is a hard "sellable" gate. This spec adds **subscription billing**
(tiers/seats) for CasaZen's customers (the `Org`s) via **Stripe Billing on the platform account** —
distinct from the **connected-account** Stripe Connect flows in `spec-direct-checkout` (guest funds,
operator = MoR). Here CasaZen **is** the merchant: the `Org` is a Stripe **Customer** on CasaZen's
platform account and pays a recurring subscription mapped to its `PlanTier`.

Because billing and the Connect flows **share** `WebhooksController` + `StripeWebhookJob` +
`StripeWebhookHandler`, this spec owns the **platform-account** webhook routing (RF2): platform events
(`customer.subscription.*`, `invoice.*`) are verified with the **platform** signing secret and routed
separately from connected-account events.

Italian tax wiring (IVA/OSS + SDI *fattura elettronica*) is a regulatory gate and is marked
**[COUNSEL_REQUIRED]**; the **"P.IVA + SDI live" entry gate must be satisfied before the first charge**.

Phase: **1 (MVP Sellable — billing)** · User story: **US-005**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

As a CasaZen customer (an `Org` admin), I want to choose a plan (Starter/Pro/Scale) and pay a recurring
subscription by card, manage my billing in a self-serve portal, and receive a tax-correct invoice, so
that I can use CasaZen as a paid product.

As CasaZen, I want subscription state to drive plan entitlement, platform-account webhooks to be routed
and verified separately from guest-payment (Connect) webhooks, and Italian IVA/OSS + SDI e-invoicing to
be handled correctly before the first euro is charged.

---

## Acceptance Criteria

### Backend

- **AC1**: `GET /api/billing/plans` (authenticated) returns the available tiers `{ tier, displayName, priceMonthly, currency, unitAllowance, features[] }` mapped to Stripe **Price** ids (platform account). Read-only catalogue.

- **AC2**: `POST /api/billing/checkout-session` (Org admin) creates a **Stripe Billing Checkout Session** (mode `subscription`) on the **platform account** for the caller's `Org`, ensuring the `Org` has a Stripe **Customer** (`Org.StripeCustomerId`, created if missing), and returns `{ checkoutUrl }`. Selected `PlanTier` is carried in metadata `{ orgId, planTier }`.

- **AC3**: `POST /api/billing/portal-session` (Org admin) creates a **Stripe Billing Customer Portal** session for `Org.StripeCustomerId` and returns `{ portalUrl }` (plan change, payment method, cancel, invoice history are delegated to the Stripe-hosted portal).

- **AC4**: `GET /api/billing/subscription` (Org admin) returns the org's current `{ planTier, status (active|past_due|canceled|trialing), currentPeriodEnd, seats }`.

- **AC5 (RF2 — platform-account webhook routing)**: `POST /webhooks/stripe` handles **platform-account** events only, verified with `Stripe:WebhookSecret`. On `customer.subscription.created|updated|deleted` and `invoice.paid|payment_failed`, it updates `Org.PlanTier` + subscription status and **re-syncs plan entitlement** (`IEntitlementService`). Connected-account events (`Stripe:ConnectWebhookSecret`, `spec-direct-checkout`) are **not** handled on this route. Processing stays async via `StripeWebhookJob` with a `source = platform` discriminator; handling is idempotent by Stripe event id.

- **AC6**: Subscription status drives access — an `Org` whose subscription is `canceled`/`past_due` beyond grace is **downgraded** (entitlement reflects Starter/locked); reactivation on `invoice.paid` restores the tier. No data is deleted on downgrade.

- **AC7 [COUNSEL_REQUIRED — IVA/OSS matrix]**: An `IVatCalculationService` resolves the correct VAT treatment per customer:
  - **IT customer** → **IVA 22%**.
  - **EU B2B (valid VAT id)** → **reverse charge**, with the VAT id validated via **VIES** (`IViesService`); invoice notes reverse charge.
  - **EU B2C** → **OSS** scheme once the **€10,000** cross-border threshold is exceeded (destination-country VAT); below threshold, IT IVA.
  - Customer country + VAT id are captured at checkout and stored on the `Org`/billing profile. The exact wiring (rates table, threshold tracking, OSS reporting export) is **[COUNSEL_REQUIRED]**.

- **AC7b (OSS threshold monitoring — engineering build item, distinct from the [COUNSEL_REQUIRED] legal sign-off; DA amendment)**: a running **cross-border EU-B2C revenue counter** tracks cumulative EU-B2C sales for the calendar year. When the cumulative total crosses **€10,000**, the system **auto-switches** subsequent EU-B2C invoices to destination-country VAT (OSS) and records the switchover on `PlatformInvoice`. This counter + switchover state machine is an explicit build item (a tally + state transition), **not** deferred to counsel; counsel still signs off rates/reporting. A test asserts the **first over-threshold EU-B2C sale is invoiced at destination VAT** (not IT 22%).

- **AC8 [COUNSEL_REQUIRED — SDI e-invoicing]**: Stripe invoices are **not** valid Italian *fattura elettronica*. For IT-resident customers, each paid subscription invoice is exported to a **`PlatformInvoice`** record and transmitted to **SDI** (Sistema di Interscambio) in FatturaPA XML via an e-invoicing provider; **imposta di bollo** applied where due. SDI transmission status is tracked. Wiring is **[COUNSEL_REQUIRED]**.

- **AC9 (Entry gate)**: A startup/config self-check refuses to create any live charge unless the **"P.IVA + SDI live"** gate is satisfied (P.IVA configured **and** SDI e-invoicing channel configured). In non-prod (test keys) the gate is bypassed with a logged warning. This ties to the §A.11 vehicle decision (SRLS/SRL → 22% IVA + SDI; **forfettario is individuals-only and does not apply to SRLS/SRL**).

### Frontend

- **AC10**: `src/features/billing/plans-page.tsx` (Org admin) shows the tier cards from `GET /api/billing/plans` with a "Scegli piano" CTA; clicking calls `POST /api/billing/checkout-session` and redirects to Stripe Checkout (`checkoutUrl`). **Until Stripe is live**, operators use the MVP `PUT /api/orgs/me/plan` / plan settings page from `spec-tenant-boundary` AC11b.

- **AC11**: `src/features/billing/billing-settings-page.tsx` shows the current subscription (`GET /api/billing/subscription`) with a "Gestisci abbonamento" button → `POST /api/billing/portal-session` redirect to the Stripe portal.

- **AC12**: At checkout the FE collects **country** and optional **VAT id (Partita IVA)** for the billing profile (feeds AC7); Italian end-user strings (e.g. "Partita IVA", "Fatturazione").

- **AC13**: Billing routes are Org-admin-only (behind `<ProtectedRoute>` + role check); a non-admin sees a "contatta l'amministratore" message. Subscription state badge (Attivo/Scaduto) is shown.

---

## Technical Notes

### Backend

| File | Action |
|---|---|
| `Casazen.Web/Controllers/BillingController.cs` | Create — plans, checkout-session, portal-session, subscription (AC1–AC4) |
| `Casazen.Infrastructure/External/StripeBillingService.cs` | Create — **platform-account** Billing: ensure Customer, Checkout Session (subscription), Portal Session (kept separate from `StripeService` Connect flows) |
| `Casazen.Core/Entities/Org.cs` | Modify — add `SubscriptionId?`, `SubscriptionStatus`, `BillingCountry`, `VatId?` (builds on `spec-tenant-boundary`) |
| `Casazen.Core/Entities/PlatformInvoice.cs` | Create — SDI/IVA export tracking (AC8) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet<PlatformInvoice>` + indexes |
| `Casazen.Infrastructure/Migrations/<ts>_AddBillingFields.cs` | Create — Org billing fields + `PlatformInvoice` (rebased on snapshot per RF3) |
| `Casazen.Web/Controllers/WebhooksController.cs` | Modify (RF2) — `POST /webhooks/stripe` = platform events, `Stripe:WebhookSecret`; route with `source = platform` (Connect route owned by `spec-direct-checkout`) |
| `Casazen.Web/BackgroundJobs/StripeWebhookJob.cs` | Modify (RF2) — accept webhook `source`; dispatch to platform vs connected handling |
| `Casazen.Infrastructure/External/StripeWebhookHandler.cs` | Modify — handle `customer.subscription.*` + `invoice.*`; update `Org` tier/status; re-sync entitlement (AC5–AC6) |
| `Casazen.Core/Services/IEntitlementService.cs` | Modify — entitlement reads subscription status (AC6) |
| `Casazen.Infrastructure/External/VatCalculationService.cs` | Create — IVA/OSS matrix **[COUNSEL_REQUIRED]** (AC7) |
| `Casazen.Infrastructure/External/ViesService.cs` | Create — EU VAT id validation (VIES) (AC7) |
| `Casazen.Infrastructure/External/SdiEInvoiceService.cs` | Create — FatturaPA XML export + SDI transmission **[COUNSEL_REQUIRED]** (AC8) |
| `Casazen.Web/Infrastructure/BillingEntryGate.cs` | Create — "P.IVA + SDI live" pre-charge guard (AC9) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register billing/VAT/VIES/SDI services + `Stripe:WebhookSecret` config |

### Frontend

| File | Action |
|---|---|
| `src/features/billing/plans-page.tsx` | Create — tier selection → Checkout redirect |
| `src/features/billing/billing-settings-page.tsx` | Create — current plan + portal redirect |
| `src/features/billing/components/plan-card.tsx` | Create — tier card |
| `src/api/billing.api.ts` | Create — plans, checkout-session, portal-session, subscription |
| `src/queries/use-billing.ts` | Create — `usePlans`, `useSubscription`, `useStartCheckout`, `useOpenPortal` |
| `src/types/billing.types.ts` | Create — `PlanDto`, `SubscriptionDto` |
| `src/routes/index.tsx` | Modify — add `/settings/billing` behind `<ProtectedRoute>` + admin check |

---

## Compliance

- **Platform vs connected separation (RF2)**: platform-account Billing webhooks (`Stripe:WebhookSecret`) are verified and routed **separately** from connected-account Connect webhooks (`Stripe:ConnectWebhookSecret`, `spec-direct-checkout`). Each event source is verified with its **own** signing secret; the wrong secret rejects the event.
- **IVA/OSS matrix [COUNSEL_REQUIRED]**: IT 22% / EU-B2B reverse charge (+ **VIES** validation) / EU-B2C **OSS** above €10k cross-border. Country + VAT id captured at checkout (AC7, AC12).
- **SDI e-invoicing [COUNSEL_REQUIRED]**: Stripe invoices ≠ Italian *fattura elettronica*; IT invoices exported to **SDI** in FatturaPA XML, *imposta di bollo* where due (AC8).
- **Entry gate**: **no live charge before "P.IVA + SDI live"** (AC9). Vehicle note: SRLS/SRL ⇒ 22% IVA + SDI mandatory; **regime forfettario applies to individuals/ditta only — never to SRLS/SRL** (draft-v3 §A.11). Final tax trade-off is the Financial Strategist's call.
- **PCI/security**: card entry and invoice PDFs are Stripe-hosted (Checkout + Customer Portal); CasaZen stores only Stripe ids and tax metadata, never card data.

---

## Dependencies

- **Requires**: `spec-tenant-boundary` (`Org` + `PlanTier` + `StripeCustomerId` + entitlement); existing Stripe integration + `WebhooksController`/`StripeWebhookJob`/`StripeWebhookHandler`.
- **Requires (hard gate)**: P.IVA active + SDI e-invoicing channel live **before first charge** (Legal C2); resolve §A.11 vehicle (SRLS/SRL).
- **Blocks**: monetization / "sellable" exit criterion of Phase 1 (an external PM pays CasaZen a subscription with a correct IVA/OSS + SDI invoice).
- **Related (RF2)**: `spec-direct-checkout` (and Phase 1.5 `spec-ltr-recurring-rent`) — connected-account flows sharing the same webhook controller; coordinate signing-secret routing.
- **[COUNSEL_REQUIRED]**: external tax/legal counsel must confirm the IVA/OSS rate logic, OSS threshold tracking/reporting, and SDI transmission wiring before go-live.
