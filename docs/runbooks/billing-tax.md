# Runbook: VAT on the CasaZen subscriptions (Stripe Tax) and Italian e-invoices (SDI)

Task PL-13 (audit defect A1-08, spec-saas-billing SB-AC7, SB-AC7b, SB-AC8). Sources and rules:
`.claude/context/regulations/fiscale.md` § "PL-13 — IVA sul billing SaaS CasaZen" (RS-5). This runbook covers only the
SaaS invoices CasaZen issues to hosts for its plans, not the payments of the guests (Stripe Connect).

## What was wrong (A1-08)

| Before | Now |
|---|---|
| `VatCalculationService` applied a hand-written 22% to OSS sales (it should be the rate of the customer's country), 0% to EU consumers under the 10,000 € threshold (it should be Italian VAT) and labelled every non-EU customer `EU_BELOW_THRESHOLD` | No rate in the code. **Stripe Tax** computes the VAT on the checkout and on every renewal; `PlatformInvoice` copies what Stripe charged |
| The Stripe Checkout charged no tax (no `automatic_tax`, no `tax_rates`) while `PlatformInvoice.VatAmount` recorded VAT: CasaZen collected the net amount and owed the VAT | Checkout with `automatic_tax[enabled]=true`: the customer pays the VAT Stripe Tax computes |
| `OssRevenueTracker` counted the 10,000 € OSS threshold itself (and reset it every year, ignoring the previous year) | Removed (table `PlatformBillingMetrics` dropped). Stripe Tax monitors the EU threshold and applies the "small seller" rule (fiscale.md S3) |
| `ViesService` was a stub: `Vies:StubMode=true` (the default in `appsettings.json`) accepted any VAT id of 5+ characters as verified, so any made-up id gave a reverse charge; without it every EU VAT id was refused | Removed. The VAT id that counts is the one entered in Stripe Checkout, saved on the Stripe customer and **verified by Stripe** (VIES, asynchronous). A reverse charge without a verified id is flagged for review |
| `SdiEInvoiceService` was a stub that logged "queued" and did nothing; `Sdi:ProviderConfigured=true` opened the live checkout anyway | `ISdiEInvoiceProvider` with an honest default: no provider → every paid invoice is `manual_required` ("fattura elettronica da emettere manualmente"). The old flag no longer opens anything |

## How it works

### Checkout (`StripeBillingService.BuildCheckoutSessionOptions`)

Parameters sent to Stripe (checked on the Stripe.net 50.1.0 API reference, API version `2025-12-15.clover`):

| Parameter | Why |
|---|---|
| `automatic_tax[enabled]=true` | Stripe Tax computes the tax "for this session and resulting payments, invoices, and subscriptions": the renewals of the subscription are taxed too |
| `billing_address_collection=required` | The full billing address is always asked (location of the customer) |
| `tax_id_collection[enabled]=true` | The customer can enter a VAT id; Checkout saves it on the Stripe customer, Stripe verifies EU ids on VIES |
| `customer_update[address]=auto`, `customer_update[name]=auto` | The customer already exists (one per org): without these Checkout does not save the address and the business name on it, and the renewals would be computed without them |

No `tax_rates` are sent: there is no rate in the code.

The `vatId` sent to `POST /api/billing/checkout-session` and `PUT /api/billing/profile` is stored as **declared** by the
user (letters and digits, 4-20 characters); it is no longer "verified" by the API and `viesValidated` is always
`null`. The user enters the VAT id again in Stripe Checkout ("I'm purchasing as a business"): that is the one Stripe
Tax uses.

### Paid invoice (`invoice.paid` → `PlatformInvoiceService.RecordPaidInvoiceAsync`)

Inside the webhook transaction (PL-10), the invoice is recorded from the Stripe invoice:

| Column | Source |
|---|---|
| `AmountExVat` | `total_excluding_tax` (after discounts) |
| `VatAmount` | sum of `total_taxes[].amount` |
| `TotalAmount` | `total` (what the customer paid) |
| `TaxCountry`, `VatRatePercent` | the tax rate of `total_taxes[].tax_rate_details.tax_rate`, read from Stripe (`country`, `effective_percentage`); only when there is one rate |
| `CustomerCountry` | `customer_address.country` |
| `TaxabilityReasons` | `total_taxes[].taxability_reason` (e.g. `standard_rated`, `reverse_charge`, `not_collecting`) |
| `CustomerVatIdVerification` | for a reverse charge only: VIES status of the customer's `eu_vat` ids on Stripe (`verified`, `pending`, `unverified`, `unavailable`, `none`) |
| `OssApplied` | VAT charged in an EU country other than Italy |
| `VatTreatment` | `taxed`, `reverse_charge`, `not_collecting`, `zero_tax`, `automatic_tax_disabled`, `tax_calculation_incomplete` |
| `TaxReviewReason` | set when the invoice needs a check (below) |

A Stripe error while reading the tax rates or the tax ids rolls the event back; Hangfire retries it (no guessed tax is
ever stored). Rows written before PL-13 keep their old values (`IT_22`, `EU_OSS`, `EU_REVERSE_CHARGE`,
`EU_BELOW_THRESHOLD`): those VAT amounts were **not** charged to the customer.

### Tax review flags (`TaxReviewReason`)

| Value | Meaning | What to do |
|---|---|---|
| `automatic_tax_disabled` | The invoice was computed without Stripe Tax (subscription created before PL-13) | See "Subscriptions created before PL-13" |
| `tax_calculation_incomplete` | Stripe Tax could not compute (e.g. `requires_location_inputs`) | Fix the customer's address on Stripe; check the invoice with the accountant |
| `reverse_charge_vat_id_not_verified` | Reverse charge but the customer's VAT id is not `verified` on VIES. Stripe applies the reverse charge to any id with a valid **format** (fiscale.md S4); art. 18 Reg. UE 282/2011 wants the validity confirmed (P10) | Open question for the accountant (policy for `pending`/`unverified`, fiscale.md "Punti che richiedono il commercialista" 1) |
| `not_collecting` | No tax registration of CasaZen in the customer's jurisdiction (e.g. a customer outside the EU) | Check whether the customer's country requires a registration (fiscale.md P11) |
| `zero_tax` | No VAT for another reason (exempt customer, not subject to tax) | Check with the accountant |
| `multiple_tax_rates` | More than one rate on the invoice | Read the rates on the Stripe invoice |

Admin list of the flagged invoices: `GET /api/admin/platform-invoices?taxReview=true`.

### Italian e-invoice (SDI)

Stripe does not issue FatturaPA e-invoices (fiscale.md S6). `ISdiEInvoiceProvider` is the extension point; this build
contains **no** provider adapter (`UnconfiguredSdiEInvoiceProvider`), on purpose: no commercial provider is integrated
without a contract and verified documentation (candidates with public API docs: fiscale.md, table "Provider API SDI").

| `SdiStatus` | Meaning |
|---|---|
| `manual_required` | No provider: **the e-invoice must be issued by hand** (default for every paid invoice in this build) |
| `pending` | A provider is configured, submission not attempted yet (legacy rows before PL-13 also read `pending`: they were never sent, treat them as `manual_required`) |
| `submitted` | The provider accepted the invoice; `SdiTransmissionId` is its id. It is not yet the SDI delivery receipt |
| `failed` | The provider refused it or could not be reached; `SdiError` says why |
| `manual_issued` | A platform admin declared it issued by hand; `SdiTransmissionId` holds the reference given |

Every paid invoice is put in the queue, whatever the customer's country: e-invoices for Italian customers, data
transmission for foreign ones (fiscale.md P13). Whether an invoice is needed for an Italian consumer who did not ask
for it (P12) is an open question for the accountant; until then the queue lists them too.

Data for the manual e-invoice:

- amounts, rate, VAT, treatment: `GET /api/admin/platform-invoices` (one row per invoice) and the Stripe invoice
  (`stripeInvoiceId`, `stripeInvoiceNumber`);
- name, address and VAT id of the customer: the Stripe customer (collected by Checkout);
- SDI recipient code, PEC and codice fiscale: `sdiRecipientCode`, `pecEmail`, `fiscalCode` of the row, entered by the
  host with `PUT /api/billing/profile` (optional fields `sdiRecipientCode`, `pecEmail`, `fiscalCode`; omitted = unchanged, empty string = cleared).
  The API checks only the shape (letters and digits, at most 7 / 16 characters; a single e-mail address); the official
  formats are not encoded (no verified source reachable, D14).

## Settings (product owner)

### 1. Stripe Tax (Stripe Dashboard, test mode first, then live mode)

1. **Tax → Settings**: set the head office address (Italy) and the default tax behaviour of prices (**exclusive**:
   the plan prices are net of VAT and the VAT is added; or **inclusive** if the prices shown to hosts include VAT —
   a product decision, see "Open questions"). Stripe Tax computes nothing while the settings are `pending` (missing
   head office).
2. **Product tax code**: on each plan product (Starter, Pro, Scale) set the tax code for SaaS. RS-5 names
   `txcd_10103001` (SaaS, business use); confirm it on Stripe's tax code list before choosing (fiscale.md S5).
3. **Registrations** (Tax → Registrations): add **Italy** (domestic registration, CasaZen's VAT number). Add the
   **EU OSS** registration only after enrolling in the OSS with the Agenzia delle Entrate (fiscale.md P6). Stripe
   computes tax **only where a registration exists**; without the Italian one every invoice is 0 (S2).
4. **Restricted key** (only if `Stripe__SecretKey` is an `rk_…` key): add read access to **Tax rates** and to
   **Customers** (tax ids). Without them `invoice.paid` fails and is retried by Hangfire: the log shows the Stripe
   permission error.
5. Nothing to change on the webhook endpoints: `invoice.paid` is already in the platform event list.

### 2. Railway variables (`production`)

| Variable | Value | Effect |
|---|---|---|
| `Billing__VatNumber` | CasaZen's VAT number | required by the billing entry gate with live keys |
| `Sdi__ManualIssuanceAccepted` | `true` once the product owner accepts to issue the e-invoices by hand from the admin queue | with `Billing__VatNumber` opens the live plan checkout (no SDI provider in this build) |
| `Sdi__ProviderConfigured` | **delete it** | no longer read: it opened the checkout while nothing was sent to SDI |

With test keys outside Production the gate is open anyway (PL-11). The ready check reports `einvoicing: degraded`
while no provider is configured (decision D9: the app shows the missing configuration); the admin description says
whether the manual issuance was accepted and whether `Billing__VatNumber` is missing (names only, never values).

### 3. Subscriptions created before PL-13

They were created without automatic tax: their renewals are still untaxed and are recorded as
`automatic_tax_disabled` (flagged). For each of them, in the Stripe Dashboard (subscription → update → enable
automatic tax), or with the API `POST /v1/subscriptions/{id}` with `automatic_tax[enabled]=true` (the customer needs
an address). Decide with the accountant how to treat the invoices already paid without VAT.

## Operations: issuing the e-invoices by hand

1. Weekly (and before the monthly VAT settlement): `GET /api/admin/platform-invoices?sdiStatus=manual_required`
   (Admin token). Also check `?sdiStatus=failed` and `?taxReview=true`.
2. Issue the e-invoice in the accounting / e-invoicing tool of CasaZen with the data above, within the legal deadline
   (ask the accountant).
3. Record it: `POST /api/admin/platform-invoices/{id}/sdi-manual-issued` with `{ "reference": "<SDI file name or
   invoice number>" }` → `manual_issued`. A second declaration answers 409 `platform_invoice_sdi_already_issued`.

## Adding an SDI provider later

1. Choose the provider (contract, sandbox) among those with public API documentation (fiscale.md).
2. Implement `ISdiEInvoiceProvider` in `Casazen.Infrastructure/External/` (build the FatturaPA document from
   `PlatformInvoice`, the org's e-invoice data and the Stripe customer; `IsConfigured` true only when its Railway
   variables are present) and register it in `ServiceCollectionExtensions` instead of
   `UnconfiguredSdiEInvoiceProvider`.
3. The webhook marks new invoices `pending` and calls `SubmitToSdiAsync` after the commit; a provider error leaves
   `failed` with the error, never `submitted`. The SDI receipts (delivery, rejection) are a further step: the provider's
   webhook must update the status.
4. Document the variables here and in `docs/INFRA.md`, and add the provider to the `einvoicing` health check.

## Verification (test environment, test keys)

1. Stripe test mode: Tax settings with head office, Italian registration, tax code on the products.
2. As a host owner choose a plan, country Italia: Stripe Checkout shows the VAT line and asks the billing address. Pay
   with `4242 4242 4242 4242`.
3. `GET /api/admin/platform-invoices?pageSize=5` (Admin): the new row has `vatTreatment=taxed`, `vatAmount` equal to
   the VAT on the Stripe invoice, `taxCountry=IT`, the rate of the Stripe tax rate, `sdiStatus=manual_required`.
4. Repeat with country Germania and a test VAT id (Stripe test ids, e.g. the ones documented by Stripe for
   `eu_vat`): `vatTreatment=reverse_charge`; if the id is not `verified`, `taxReviewReason=reverse_charge_vat_id_not_verified`.
5. `POST …/sdi-manual-issued` on the first row → `manual_issued`.
6. `GET /api/health/ready` as Admin: `einvoicing` degraded with the manual-issuance description.

## Open questions (product owner / accountant)

- Policy for a reverse charge with a VAT id `pending` / `unverified` / `unavailable` (charge VAT as B2C until verified?).
- Plan prices net or gross of VAT (`tax_behavior` of the Stripe prices): changes what hosts pay.
- Opt-in to destination taxation under the 10,000 € threshold and OSS enrolment timing (fiscale.md P5, P6).
- E-invoice for Italian consumers who did not request it (P12); which SDI provider to contract.
- Official formats of SDI recipient code and codice fiscale to validate in the API (sources not reachable from the
  build environment).
