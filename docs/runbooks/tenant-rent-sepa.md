# Tenant rent payments with SEPA

The tenant portal and reminder payment links collect an IBAN through Stripe Payment Element, without Financial Connections. The condominium account is never linked to Stripe.

## Funds and amounts

A rent installment remains the sum of its ledger components: rent, contractual condominium advance or confirmed budget allocation, and any recorded adjustments. The payment quote adds exactly EUR 1 outside the rent ledger. One destination charge on CasaZen's Stripe account, with `on_behalf_of` and `transfer_data.destination` set to the landlord, collects the full quote. The application fee is 100 cents. Stripe processing fees are charged to CasaZen; the landlord receives the installment amount. EUR 0.65 is the estimated margin only if actual processing fees are EUR 0.35; Connect, disputes and other charges can change the net result. Reconcile actual Stripe balance transaction fees rather than treating EUR 0.35 as an accounting constant.

Enable SEPA Direct Debit for the platform and `sepa_debit_payments` for participating connected accounts. New onboarding requests that capability. Existing accounts might need additional requirements completed. Existing direct charge checkout flows outside rent are unchanged. Before enabling the tenant-facing fee in production, confirm the applicable EU/Italian restrictions on payment-method surcharges and the contractual treatment of the service fee; the label does not itself determine permissibility.

## Consent and timing

A SetupIntent on the platform obtains a mandate on behalf of the landlord. The backend verifies its successful Stripe status, customer, lease, user, destination and SEPA payment-method type. Creating a mandate never enables automatic collection. The tenant chooses manual mode or separately enables monthly automatic payment, and may turn it off. Co-tenants cannot alter another tenant's saved mandate.

The existing `rent-collection` job handles automatic collection alongside existing manual requests and reconciliation. Only monthly, due, scheduled installments on or after activation are collected; historical arrears and failed attempts require manual action. The lease advisory lock and persistent Stripe intent identity are shared with manual payment, offline settlement, termination and webhook processing. A durable reservation and stable confirmation idempotency key allow recovery after an interrupted confirmation. Never mark an installment paid from the browser.

Stripe's SEPA debit notifications must remain enabled; the customer associated with the mandate has the tenant's email. The platform webhook must subscribe to `payment_intent.processing`, `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled` and `mandate.updated`. Signature verification and webhook event deduplication remain in the existing handler. Inactive mandates disable automatic collection and require a new authorization.

Authorization is limited by the current contract term. The existing renewal service extends 3+2 contracts by two years and 4+4 contracts by four years; notice or early termination prevents renewal. New debits are blocked from the first day of the month following the effective end. The collection job cancels outstanding unconfirmed intents after expiry; already-processing SEPA debits retain their actual Stripe outcome. Scheduled installments whose period starts after early termination are cancelled by the existing lease lifecycle.

## Budget updates

Only confirmed, tenant-recoverable budget lines feed advances. A fixed contractual advance takes precedence. Budget-based installments are updated after budget confirmation, schedule creation and lease renewal, scoped to the applicable fiscal year. Cents are conserved with the remainder on the last monthly portion. Paid, processing, settlement-only and already-quoted installments are preserved.

## Condominium payments

The landlord pays through their own home banking. `GET /api/long-rent/condominiums/manual-payments` lists scoped installments and residuals; the authorized `POST .../{id}/mark-paid` records the residual as a manual payment, under the same condominium accounting lock and with an idempotent source identity. It never initiates a transfer.

The rent collection job queues a reminder to the organization's configured contact email within five days before the due date, including overdue installments from the last 30 days not previously notified. Its link opens the payments page, where the landlord reviews and confirms “Segna come pagato”. Queue failure leaves the reminder retryable. No Stripe bank connection or automatic condominium payment remains in these screens.

## Release

Apply `20261110160000_AddTenantRentSepa` before deploying the portal. Existing schedules default to manual, with no saved mandate. Test in the connected Stripe sandbox: setup, manual success, asynchronous failure, automatic collection, duplicate requests, cancellation, renewal, termination, inactive mandate and webhook redelivery. No live debit is necessary for deployment validation.
