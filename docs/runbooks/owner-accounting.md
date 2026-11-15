# Owner accounting of the agencies (epic E06, feature F05)

How CasaZen keeps the accounts of the owners an agency manages: the approved rule sets and the fiscal profiles of the owners (E06/F05/US02/T01), the
append-only owner ledger, the statement versions and the month-end close (T02), the external invoice references that replace the old agency invoices
(E06/F01/US02/T05) and the payouts with their tax evidence (T03). Requirements NF-25, NF-109, NF-110; ADR-012, ADR-017, ADR-018, ADR-019 (amended).

## Product boundary

- **A rendiconto is not an invoice.** The owner statement is a management report («Rendiconto gestionale - non è una fattura»): no fiscal series or
  number, never FatturaPA XML. Since 2026-11-14 (decision P1, E14/F01/US01/T01) **CasaZen issues the invoice of the property manager to the owner** from a
  version of the statement, as a fiscal document with lines (see «Owner invoices»); the reference to an invoice issued elsewhere stays for the months
  invoiced before (see «External invoice references»).
- **Legal and accounting review is a launch gate.** Every rate, applicability rule, tax code and retention period is data of a rule set approved by a
  qualified accountant; nothing is inferred from a country or written in the code.

## Rule sets and fiscal profiles (E06/F05/US02/T01)

- **Rule set** (`OwnerAccountingPolicy`, `api/owner-accounting/policies`): jurisdiction (`IT`, `CH`, `CH-TI`...), rule code, version, validity, the
  official source (an `https` address and the SHA-256 of the document) and the parameters (`OwnerAccountingRuleParameters`): `withholdingApplies`,
  `withholdingRatePercent`, `withholdingTaxCode`, `requiresAgencyCollection`, `requiresQualifyingShortLet`, `grossBaseComponents` (`stay_gross` since
  E18/F01/US01/T01), `rounding` (`half_up` or `half_even`), `retentionYears`, `portalsWithholding` (the portals that withhold themselves, ⚖ R5; omitted: the
  configured default). A rule set is used only once the approval of the qualified accountant is recorded (`POST …/{id}/approve` with `approvedByName`, and
  `sourceSha256` for the seeded draft whose source is not pinned); an approved rule set never changes: a change is a new version.
- **Fiscal profile** (`OwnerFiscalProfile`, `api/owners/{id}/fiscal-profiles`): effective-dated (a new profile ends the one in force the day before;
  profiles never overlap), with the kind of the owner, legal name, tax residence, legal address, fiscal code, VAT number, foreign tax id, settlement
  currency, bank account, mandate role and dates, revenue recognition, expense and commission model, working capital rule, tax jurisdiction and the
  rule set it follows. No SDI code or PEC: the profile is not an invoice recipient.
- **Privacy (ADR-012).** Legal name, legal address, identifiers and bank account are encrypted at rest (`Casazen.OwnerFiscalProfile`); the API answers
  them masked (`••••1234`) to `agency.owner.view` and in full only to `agency.owner.financial.manage` (owner and administrators), whose reads are logged
  with ids, never values.
- **Readiness for a day** (`IOwnerFiscalProfileService.ResolveAsync`, `GET …/fiscal-profiles/readiness?date=`): one complete profile in force and its
  approved rule set in force on that day, of the same jurisdiction, or a precise 422: `owner_fiscal_profile_missing`, `owner_fiscal_profile_incomplete`
  (with the `fields`), `owner_fiscal_policy_missing`, `owner_fiscal_policy_not_approved`, `owner_fiscal_policy_not_effective`,
  `owner_fiscal_policy_jurisdiction_mismatch`. A Swiss profile follows its Swiss rule set and can never be given an Italian one.

## Mandates, standard mandate, owner address and channel rules (E13/F01/US01/T01)

Decisions of 2026-11-14 (`fatturazione/analisi-tecnica.md` §5, ⚖ table §7): the defaults marked ⚖ are prudent and configurable, to be confirmed by an
accountant; every one of them is set through the «Fatturazione» wizard of the onboarding guide.

- **Mandate** (`OwnerMandate`, `api/owners/{ownerId}/mandates`): `GET` the versions, the newest first (`agency.owner.view`); `POST` a new version
  (`agency.owner.financial.manage`) with `validFrom` and `commissionPercent` (0-100, two decimals; **both required**: a request without the percent is 422
  `owner_mandate_invalid`, a mandate of 0% is a choice and never what an omitted or misspelt field turns into), `commissionBase` (`GrossExclTouristTax` = 1, the default,
  ⚖ R3: the gross of the stay without the tourist tax, cleaning included; `GrossExclTouristTaxAndCleaning` = 2), `commissionIncludesVat` (default
  `false`: the percent and the flat fees are «+ IVA», ⚖ R4; the VAT is added by the owner invoice, E14), `cleaningFlatRate` and `linenFlatRate` (per stay,
  in the settlement currency of the owner; `null` = none), `passthroughPaymentFees`, `withholdingTreatment` and the optional `documentReference` of the
  signed mandate (an https address or a key of the private storage: without it the owner readiness says `mandate_document_missing`). A version is in force
  from `validFrom` to `validTo` (Rome days, both included); a new version ends the one in force the day before it starts and is refused with 409
  `owner_mandate_overlap` when a version already starts on that day or later: one mandate a day, never two, never a gap written by the API. 422
  `owner_mandate_invalid` with the `field`. The versions written by the API cover every property of the owner; a mandate of one ownership (the shape of
  before E13, which nothing writes any more) still wins for its ownership. `OwnerMandateRules.InForce` is the one definition of «the mandate of a share on
  a day» for the readiness, the ledger and the old statements.
- **Standard mandate** (`POST api/owner-mandates/apply-template`, `agency.owner.financial.manage`, the step «Mandato standard» of the wizard):
  `{ template, ownerIds? }`. One version from `template.validFrom` for every owner of the agency in reach (or only the listed ones) that has an ownership not
  ended and no mandate still in force on that day or later (of the owner, or of one of its ownerships); the others are skipped. Answers
  `{ created, skipped, createdOwnerIds, skippedOwnerIds }`. An empty `ownerIds` is 422 `owner_mandate_owners_empty` (never «everyone»); a listed owner
  outside the reach is 404 `owner_not_found`; a template without `validFrom` or `commissionPercent` is 422 `owner_mandate_invalid` and nothing is written
  (the template cannot be applied again to the owners that got a version: a version is corrected by a later one). A second call creates nothing.
- **Owner address** (`GET`/`PUT api/owners/{id}/address`; `agency.owner.financial.read` / `agency.owner.financial.manage`): `street`, `buildingNumber`,
  `postalCode`, `town`, `province` (required in Italy, as the readiness), `country` (ISO 3166-1 alpha-2). It is the recipient of the owner invoice, the
  debtor of the QR-bill and the creditor of the pain.001.09 transfer. Stored **encrypted** (`Casazen.PropertyOwner.Financial`, the columns
  `PropertyOwners.Billing*`, no length; the values written in clear before are encrypted by the startup step), never logged (the read and the save are
  logged with ids). 422 `owner_address_invalid` with the `field`. The owner import fills the same columns from `indirizzo_via`, `indirizzo_civico`, `cap`,
  `localita`, `paese` (and the older names, `docs/TECHNICAL.md`).
- **Reach**: an owner is in the reach of a caller of the whole org; a caller restricted to some properties reaches an owner only when every ownership
  period the owner holds is on a property it reaches (at least one). Outside the reach: the same 404 as an owner that does not exist.
- **Channel commission rules** (`OrgChannelCommissionRule`, `api/owner-accounting/channel-commission-rules`): `GET` (`agency.statement.view`) answers
  `{ rules, channels }`: the rules of the agency and, for each portal (`BookingCom`, `Airbnb`, `Expedia`, `Vrbo`, `TripAdvisor`, `Agoda`, `OtherChannel`),
  what applies today: the rule in force, else the configured default (`isDefault: true`), else nothing (`percent: null`). `PUT`
  (`agency.owner.financial.manage`) replaces the rules with the whole list `[{ channel, percent, base, validFrom, validTo }]` (the `percent` of every rule is
  required: a rule of 0% is a choice that replaces the default, never what an omitted field turns into; an empty list leaves the defaults); the rules of a
  channel never overlap. A rule with the channel and the first day of a stored one is that rule and keeps its id, which the posted lines name
  (`channel-rule:{id}` in their rule version). **A rule that posted lines name is evidence of what they took: it is never removed and never given another
  percent or base (409 `channel_commission_rule_in_use` with the `ruleId`); its `validTo` can move**: a channel that changes its commission closes the old
  rule and adds the new one from the next day (a percent entered wrongly is not edited after lines named the rule: what was posted is corrected with a
  correction of the ledger). A rule nothing names is changed in place (same id) or removed freely. 422
  `channel_commission_rules_invalid` with the `index` and the `field`. Both actions need a caller of the whole org (403 otherwise). Defaults
  `OwnerAccounting:ChannelCommissionDefaults` (⚖ R2: BookingCom 15, Expedia 18, Airbnb 15.5 host-only fee, Vrbo 8, on the gross without the tourist tax;
  `deploy-checklist.md` § 2.11). The writes of the mandates and of the rules run under the lock of the owner accounting of the org, as a posting.

### Official sources to pin (operator, with the accountant)

Record each in the rule set (`sourceUrl`, `sourceSha256`) before approving it: the FatturaPA documentation and schema (`docs/runbooks/fiscal-documents.md`),
the guidance of the Agenzia delle Entrate on the withholding of short lets (art. 4 D.L. 50/2017 and the provisions in force), the ESTV guidance on VAT and
accounting for Swiss owners, and the retention law that applies (Italy at least ten years; Swiss immovable-property evidence can require twenty).

## Owner ledger and month-end close (E06/F05/US02/T02)

- **Ledger** (`OwnerLedgerEntry`, append-only): every line has a stable idempotency key (source, type, owner and amounts), its source (payment, booking,
  charge, wire batch, manual) and document, the service, recognition and payment dates, the property, the effective ownership and share, the original
  and the statement currency with the rate, its source and date, the tax code and the rule version (`{ruleCode}@{version}`), and a signed amount
  (positive = owed to the owner). `AppDbContext` refuses to save a change or a removal of a line (409 `owner_ledger_immutable`); a correction is a
  reversal (`ReversesEntryId`, at most one per line) plus a replacement line.
- **Posting** (`POST api/owner-accounting/periods/{y}/{m}/post`, `agency.statement.manage`): idempotent. Sources: the collected payments (`Completed`,
  `PartiallyRefunded` and `Refunded`: a refund moves a payment to one of the last two, so the revenue is `Amount - RefundedAmount` less its part of the
  tourist tax; a partial refund lowers the line, a full one leaves nothing; the share in force on the payment day; recognized on the payment day, or on the
  check-out for a profile whose recognition rule is `stay`), the stays that check out in the month without a collected payment (E13/F01/US01/T02, see
  «Posting per booking flow»), the mandate commission on each share, the channel commission of an **active** booking (the one definition of the code:
  `AlloggiatiStatusRules.ActiveBookingStatuses` and no no-show) of a portal, or whose payments are gross (`OtaCommissionIncludedInPayments`, with its
  first payment that still holds money), the flat fees of the mandate and the card fees it passes through, the approved charges (`OwnerCharge`, one-time
  or monthly, to one owner or split by the shares), the wires marked paid **unless the owner settled the same statement month with a confirmed payout**
  (see «Payouts»). The shares are split in the source currency, rounded half to even, the
  last owner (by id) taking the remainder; then converted with the agency's rate (`OwnerFxRate`, `api/owner-accounting/fx-rates`, the rate of the day or of
  the 7 days before; CasaZen fetches none). Changed facts reverse the old line and post the new one. A line computed while the owner could not be resolved
  (no profile, no approved rule) has no rule version and its own key (`…:norule`): once the owner can be resolved it is reversed and replaced by a line that
  carries the rule version. A closed month of an owner is never posted again.
- **Versions** (`OwnerStatementVersion`): `POST …/periods/{y}/{m}/owners/{id}/versions` cuts the ledger now and stores the totals by type, the opening and
  closing balance and the SHA-256 of the lines; `Flagged` with a blocking readiness issue, else `Draft`; the version before becomes `Superseded`. A line
  posted after the cut-off marks the month dirty (`OwnerClosePeriod.IsDirty`), and so does a line posted in an **earlier** month after the cut-off (the
  balance carried in is part of the version: its opening balance is checked again at approval, publication and close): no approval, publication or close
  until a new version. Approval
  (`…/versions/{id}/approve`, `agency.statement.manage`) and publication (`…/publish`, `agency.statement.publish`) are distinct permissions; publication
  stores the JSON of the version and of its lines with its SHA-256 (`…/published`), kept after it is superseded. `GET …/versions/{id}` drills down to the
  lines and their sources.
- **Readiness** (`GET …/periods/{y}/{m}/readiness`): issues of a property (`ownership_missing`, `ownership_share_gap`, `source_amount_missing`,
  `channel_commission_missing`) have no owner of
  their own and are listed for the org, **and** hold back the statement of every owner of that property (a source left out of the ledger is a line missing
  from their accounts); owner issues from the fiscal profile (`owner_fiscal_*`), `mandate_missing_for_stay`, `fx_missing`, `unposted_source`, `duplicate_entry`, `currency_mixed`,
  `statement_missing`, `statement_not_approved`, `ledger_statement_imbalance` (the lines of the month or the balance carried in changed after the cut-off),
  `negative_balance`, `period_dirty`. For a closed month: `period_closed` and, not blocking, `late_source_after_close` for each line the sources say the
  month should hold and the ledger does not (it arrived or changed after the close: it is told, never posted; a correction in a later month fixes the account).
- **Close** (`POST …/owners/{id}/close`): only once the month is over (409 `owner_period_not_ended` until the day after its last day, Europe/Rome), on the last
  version, approved or published, and only if the readiness read **again** at that moment has no blocking issue (422 `owner_close_not_ready` with the
  `issues`: a payment that came after the cut-off and was never posted, a gap of the shares, a rate that is missing...). The approved closing balance is
  reconciled to the confirmed payouts of the month's versions (their gross) or, when the month has none, to the wires marked paid for the owner's statements of
  the month (same currency): one transfer is counted once. A difference or a negative balance blocks (422 `owner_close_unreconciled` with `due`, `paid`,
  `difference`) unless `waiverReason` is given by a caller with `agency.owner.financial.manage` (otherwise 403 `owner_close_waiver_forbidden`); the
  waiver, its author and time stay on the month. A closed month is never posted or versioned again; its corrections go into a later month.
- **Retention and legal hold**: at the close the month keeps its jurisdiction, years (the larger of the rule set's `retentionYears` and the floor: Italy
  10, Switzerland 20, others 10) and `RetainUntil` (31 December of the close year plus the years). `…/legal-hold` (`agency.owner.financial.manage`,
  with a reason) stops the expiry (`OwnerRetention.MayPurge`). No job deletes anything yet. `GET …/evidence` exports the month: the close, every version
  with its published file, the lines and their charges.
- **Corrections** (`POST api/owner-accounting/corrections`, `agency.owner.financial.manage`): a signed manual line with its reason; `replacesEntryId`
  reverses an earlier manual line first. A line of a source is corrected by fixing the source and posting again.
- **Scope**: the endpoints need an org-wide reach; a collaborator restricted to some properties gets 403 even with the permission.
- **Not yet**: the import of the outgoing bank movements (a wire marked paid, or a confirmed payout, is the evidence of the transfer). The wire batch that pays the net
  of the withholding exists since E16/F01/US01/T01: it is made from the confirmed payouts (see «Payouts» and [`wire-transfers.md`](wire-transfers.md)).

### Posting per booking flow (E13/F01/US01/T02)

The three flows of `fatturazione/analisi-tecnica.md` §5bis, plus the bookings entered by hand. **One path posts a stay**: a stay with a collected payment
(of the showcase, or one the agency recorded with `ManualPaymentService`) is posted by its payments; a stay without one by its booking. A stay whose
booking line is still live in another month (it was posted before it was paid) is not posted again by its payment; posting that month again reverses the
booking line, and the month of the payment then posts it (the readiness says `unposted_source` until then).

| Flow | Revenue (`BookingRevenue`) | Channel commission (`ChannelFee`) | Commission, flat fees, card fee |
|---|---|---|---|
| Portal (Booking.com, Airbnb, Expedia, Vrbo, the others of `FiscalCopy.IsOtaBookingSource`) collected by the portal or at the property | source `Booking`: the gross of the stay without the tourist tax, `OwnerLedgerRules.GrossOfStay` = `Booking.TotalPrice − Booking.TouristTax` (the room, the cleaning and the extras the portal charged; never the tourist tax, never the guest fee of the portal), never less than `Booking.BasePrice`, on the **check-out** (⚖ R1), the shares of that day | the commission of the reservation (`Booking.OtaCommission`, `RuleVersion` `payload`); without it the rule of the agency in force at the check-out (`channel-rule:{id}`) or the configured default (`channel-default`), its percent × its base; neither: `channel_commission_missing` (blocking, for every owner of the property), no fee. **Borne by the owner**, as before | commission of the mandate on its base; flat fees; no card fee |
| The agency's own site outside CasaZen, a booking entered by hand: no payment recorded | source `Booking`: `Booking.BasePrice` (priced by CasaZen: the gross without the tourist tax, the cleaning included) on the check-out; a price the host agreed by hand (`Booking.PriceOverridden`) is an amount even when it is 0 (a stay for free: no revenue, nothing to tell) | none (not a portal) | commission; flat fees |
| The same, the agency recorded the payment | source `Payment`: what it collected net of refunds and of its part of the tourist tax, on the **day of the recorded payment** (or the check-out for a profile `stay`) | none | commission; flat fees |
| CasaZen showcase (card through Stripe) | source `Payment`, as above, on the payment day | none | commission; flat fees; `OwnerExpense` of the card fee (`Payment.ProviderFeeAmount`, in its currency) when `PassthroughPaymentFees` |

- **Tourist tax out**: `Payment.Amount` includes `Booking.TouristTax`; a payment's part of it is `round(collected × TouristTax / TotalPrice)` half to even,
  never more than the tax or the payment (`OwnerLedgerRules.RevenueOfPayment`): a deposit, a balance and a partial refund each lose their own part, so the
  parts of several payments can differ by a cent from the tax. The tourist tax is never in the revenue nor in a commission base. A stay that owes no tourist
  tax (a booking that is not active, or a no-show: `AlloggiatiStatusRules`, as the box of the tax of the statement reads it) has no part of it: a payment it
  keeps (the deposit of a no-show, a penalty) is all revenue, and the reconciliation of the month reads it the same way (E19/F01/US01/T01).
- **Commission** (`AgencyCommission`): the percent of the mandate in force on the day of the revenue × its base (`GrossExclTouristTax`: the revenue;
  `GrossExclTouristTaxAndCleaning`: less its part of `Booking.CleaningFee`), the base split by the shares half to even with the remainder to the last owner.
  A share with **no mandate** in force is posted without commission and raises the blocking `mandate_missing_for_stay` (owner, stay): never a commission of
  zero by default; a mandate of 0% is a choice and raises nothing.
- **Flat fees**: for every active stay that checks out in the month, whatever path posts its revenue, an `OwnerCharge` line per fee kind
  (`SourceType` `MandateFlatFee`, `SourceReference` `cleaning` or `linen`, the booking as source) of the mandate in force on the check-out, in the settlement
  currency of the owner, split by the shares; approved by the mandate itself, a recharge whose VAT the owner invoice applies (E14); once per stay and kind
  (key `flat:{booking}:{kind}`). The owners are those of the **check-out day**, for a stay posted from its payments too (their revenue was shared on the day
  of the payment): a property with none on that day, or whose shares do not make 100, is told (`ownership_missing`, `ownership_share_gap`, blocking) and the
  fees are not posted.
- **Card fee**: `OwnerExpense` (source the payment, key `payfee:{payment}`) only when the mandate of the share passes the payment fees through.
- **Cancellations and no-shows**: only the active bookings are posted (a pending, cancelled or no-show booking is not); a booking cancelled after it was
  posted loses its lines at the next posting of its month (reversal), as any changed source.
- **Keys**: `stay:{booking}` (booking path), `pay:{payment}` (payment path), `fee:{booking}` (`:rule:{id}` or `:default` when the fee does not come from the
  reservation), `flat:{booking}:{kind}`, `payfee:{payment}`.
- **Gross of a stay posted from its booking** (the brief writes `BasePrice + CleaningFee + extras`): for a booking priced by CasaZen `Booking.BasePrice`
  already holds the cleaning (PC-07, the fiscal document and the Home revenue read it so) and the booking has no extras of its own, so the gross is
  `BasePrice`. For a booking of a portal, `BasePrice` of an imported reservation is its room only (`roomGross`, `CleaningFee` stays 0) and the cleaning and the
  extras the portal charged are only in its total, so the gross is `TotalPrice − TouristTax`, never less than `BasePrice`. **It is a prudent choice to be
  checked on the first real payload of the channel manager** (does `totalGross` include the tourist tax?): the channel commission and the commission of the
  mandate are a percentage of the same gross.

## Owner invoices (E14/F01/US01/T01)

The invoice of the property manager to an owner (NF-114, D9): a `FiscalDocument` with `Purpose = OwnerInvoice`, made from a version of the statement, behind
`Features:OwnerInvoicing` (off by default, on only in the test environment; [`feature-flags.md`](feature-flags.md)).

- **Endpoints** `api/owner-invoices`, the whole org only (403 for a caller restricted to some properties), 404 while the flag is off:
  `POST drafts { versionId }`, `POST {id}/issue { confirm }`, `POST {id}/credit-note { amount?, reason, confirm }` and `DELETE {id}` (the discard of a draft), all
  with `agency.statement.manage`, `GET ?period=yyyy-MM&ownerId=` (`agency.statement.view`: number, owner, total, currency, status and, once issued, `payment`:
  `offset`, `to_pay` with `amountDue`, `credited`). The PDF and the XML are those of every fiscal document (`GET api/fiscal/documents/{id}/pdf|xml`, read with
  `payment.read`, a document without a booking: the whole org). **The endpoints of the guest documents (`api/fiscal/documents`, `payment.write`) refuse to make an
  owner invoice again, to issue it, to credit it or to discard it (422 `document_owner_invoice`)**: the property manager of the short-rent area holds
  `payment.write` and no agency permission (DEB-01), and the credit note writes to the owner ledger (the VAT given back, below), so it needs the permission of the
  issue and the flag of the invoices.
- **The drafts of a period.** `POST api/agency-invoices/generate { year, month }` (`agency.invoice.manage`, behind the same flag) no longer answers 410: it makes,
  or makes again, the draft of every owner with a version of the month that is not superseded and answers the counts (`owners`, `created`, `updated`,
  `alreadyIssued`, `nothingToInvoice`, `failed` with the owner and the code). The rows of the retired generator (`AgencyInvoices`) stay read-only, `legacy_unverified`.
- **One per owner and period.** A live owner invoice (neither discarded, nor credited in full) holds its owner and month (`UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period`):
  a second draft of the month is the same document made again from the version asked for; once issued, 409 `document_already_issued`. A superseded version is
  refused (409 `owner_invoice_version_superseded`), a version with nothing to invoice too (422 `owner_invoice_nothing_to_invoice`).
- **Recipient.** The owner: the fiscal profile in force on the last day of the month (legal name, fiscal code, VAT number), else the registry; the structured
  billing address of E13. A Swiss invoice needs the name; an Italian electronic invoice also the identification and the address (422
  `document_invoice_data_missing` with the `fields`).
- **Lines, from the ledger lines of the version** (the owner's lines of the month up to its cut-off; a reversal and the line it reverses cancel out):
  - `AgencyCommission` → one `Commission` line per property («Provvigione di gestione - casa - mese»);
  - `OwnerCharge` → one `Recharge` line per flat fee of a stay (cleaning, linen) and per approved charge, with its own description and day;
  - `OwnerExpense` of an owner charge of kind expense → one `AdvancedExpense` line each, outside the VAT (an expense advanced in the name and on behalf of the
    owner, art. 15 in Italy); the card fees the mandate passes on (`OwnerExpense` of a payment) are a cost the agency bears in its own name → one `Recharge`
    line per property;
  - `ChannelFee` → one `Recharge` line per portal «Riaddebito commissioni Booking.com», **only** for the listings whose portal account the agency holds (V2)
    and only when the organization recharges them (⚖ I10, I11, below). A listing whose account the owner holds (V1, the default) has none.
- **VAT (⚖ R4).** The mandate of the owner in force on the last day of the month says it: `CommissionIncludesVat = false` (the default) makes the ledger amounts
  the taxable amounts and adds the VAT (`FiscalDocumentTotals.FromNet`: every line keeps its net to the cent, the VAT of a rate on the sum of its lines);
  `true` makes them the gross and takes the VAT out (`FromGross`). The rates are those of the catalog `FiscalDocuments:TaxRules` in force on the last day of
  the month by line type: Switzerland 8.1 (`ch-vat-2024.1`), Italy 22, a flat-rate issuer `N2.2` with the stamp duty (borne by the issuer, as the catalog says).
- **Issue.** The issue of the fiscal documents: the gap-free number in the series `OrgFiscalIssuerProfile.OwnerInvoiceSeries` (default `PM{year}`, e.g.
  `PM2026/1`), the snapshot of the issuer, the rule version, the Italian XML of an invoice (422 `fatturapa_schema_unavailable` until the official schema is in
  the build, E17). The draft is made again from its version first: when the version was superseded or the mandate, the holders, the rule or the recipient give
  other amounts now, 409 `document_draft_outdated` (make the draft again). **A VAT added on top is posted to the owner ledger in the transaction of the
  number**: one line `AgencyVat` (debit, source `OwnerInvoice`, key `invoicevat:{documentId}`), once, in the month of the invoice (the month of today when that
  month of the owner is closed), so the net paid to the owner is right. The month is marked dirty; a **draft or flagged** version of it is generated again at
  once; an **approved or published** one is left as it is and the readiness says `owner_invoice_after_approval` (blocking, with the invoice), so the agency
  generates it again. The `Commission` of a version counts the `AgencyVat` lines with the commission.
- **Credit note** (`POST api/owner-invoices/{id}/credit-note`, `agency.statement.manage`). A credit note of the owner invoice gives the VAT back in its own
  transaction: the reversal of the line when it credits the whole invoice at once, else the VAT of the note (the rest of it with the note that completes the
  credit, so the cents of the partial notes are not lost); in the month of the invoice, or in the month of today when that month of the owner is closed; nothing for
  a mandate «IVA inclusa» (it posted none). The draft or flagged version of the month is generated again. An invoice credited in full frees its owner and month.
  The draft of an owner invoice is discarded by `DELETE api/owner-invoices/{id}` (the month is free for a new draft).
- **Payment.** An issued invoice is `offset` when the statement covers it, else `to_pay` with how much the debt of the owner grew in the month (the ledger before
  the month and at its end, the VAT included), never more than what is left of the invoice after its credit notes (`OwnerInvoiceSettlements`).
- **The statement names the invoice.** The PDF and the CSV of the statement (`IStatementExportService`) print «Fattura n. PM2026/1» next to the commission of
  the ledger once the owner invoice of the month is issued. The statement PDF of the version (E13/F02) reads the same number, and has a line for the VAT the invoice
  added on top (`AgencyVat`, and the line that gives it back with a credit note) in the part «Provvigione del PM»: «IVA sulla provvigione e sui riaddebiti (fattura n.
  PM2026/1)»; the part counts it because the commission of the version does, so the parts of the file give the totals the version stores.

### QR-bill of a Swiss owner invoice (E14/F01/US01/T02, NF-113)

- **When.** The PDF of an issued Swiss owner invoice (the snapshot of its issuer is Swiss) ends with the QR-bill payment part (receipt and payment part, on a page
  of its own) **only when the invoice leaves an amount for the owner to pay** (`payment.state = to_pay`): the amount of the QR-bill is that amount. Offset in the
  statement: no QR-bill and «Compensata nel rendiconto del MM/yyyy». An agency without an IBAN or a QR-IBAN of Switzerland or Liechtenstein: no QR-bill and
  «Pagamento su indicazione del gestore». A draft never has one. An Italian owner invoice says «Pagamento su indicazione del gestore» when there is something to pay.
- **The account** is kept in the snapshot of the issuer at the issue (`FiscalIssuerSnapshot.QrBillAccount`, encrypted at rest like the whole snapshot): the
  QR-IBAN when the profile has one (with a QR reference `QRR` of 27 digits made from the document id, recursive modulo-10 check digit), else the IBAN (with a
  creditor reference `SCOR`, ISO 11649, made from the number of the invoice). A later change of the bank accounts does not change an issued invoice.
- **The payload** (`SwissQrBill.Build`, Core): `SPC` / `0200` / `1`, the account, the creditor (the agency, structured address `S`), seven empty lines of the
  ultimate creditor, the amount and the currency (CHF or EUR), the debtor (the owner, structured address `S`, from the recipient of the document; seven empty
  lines and a box to fill in when its postal code, town or country is missing), the reference type and the reference, the message «Fattura PM2026/7» (always in
  Italian: the payload of an invoice does not change with the language of the request, only the headings of the payment part do), `EPD`.
  Only structured addresses; the characters outside the Latin set of the guidelines become dots; a payload that is not valid gives no QR-bill.
- **Guidelines followed:** Swiss Implementation Guidelines for the QR-bill, **version 2.3**, published by SIX:
  <https://www.six-group.com/en/products-services/banking-services/payment-standardization/standards/qr-bill.html> (the version and the address are
  `SwissQrBill.GuidelinesVersion` / `GuidelinesUrl`; they were taken from the knowledge of the payment standard, not read again on the page when the code was
  written: check them against the page and validate one generated bill on the validation portal of SIX before the first real payment).
- **Rendering** (`SwissQrBillRenderer`, called by `MigraDocPdfDocumentRenderer` under its lock): the Swiss QR Code with error correction M, 46 × 46 mm, the Swiss
  cross of 7 × 7 mm in the middle, drawn as vector squares from the module matrix of **QRCoder 1.6.0** (MIT, fully managed, no System.Drawing); the receipt of
  62 mm and the payment part of 148 mm on the bottom 105 mm of an A4 page, the dashed lines between them and «Da staccare prima del versamento». **Known gap:**
  the text is in the embedded DejaVu Sans of the documents, while the guidelines name Arial, Frutiger, Helvetica or Liberation Sans; commit Liberation Sans
  (SIL OFL) next to DejaVu and switch the font of the payment part to close it.

### Who holds the portal account (⚖ I10, I11)

The holder of the account of a listing on a portal decides the recharge of its commissions (acceptance step C2 of `pilota-flusso-completo-2026-11-14.md`):

- **V1, the owner** (default, `PortalAccountHolder.Owner`): the portal invoices the owner; the agency invoices only its fee and the commission is the owner's
  cost («trattenuta del portale»).
- **V2, the agency** (`PortalAccountHolder.Agency`): the portal invoices the agency; the commission reaches the owner as a recharge of the owner invoice, at the
  VAT of a recharge (the scheme of a mandate without representation, art. 3 c. 3 DPR 633/72), when `RechargePortalCommissions` is on (default on).

The default of the organization and the recharge are on the profile of the issuer (`OrgFiscalIssuerProfile.PortalAccountHolder`, `RechargePortalCommissions`,
migration `AddPortalAccountHolder`: the profiles that exist get the owner and the recharge on): they are the `values` of the section `channels` of the billing
setup (`GET api/billing-setup`, wizard «Fatturazione») and are saved by the endpoint of that section, `PUT api/billing-setup/channels`
(`IPortalAccountSettingsService`). The override of a listing and portal is a row of `PropertyChannelAccountHolders` (unique per property and portal, gone with
the property), read and written in bulk by `GET/PUT api/billing-setup/channel-account-holders` (`[{ propertyId, channel, holder }]`, `holder: null` back to the
default; an empty list is 422, never «all»). They are part of the billing setup: `payment.read` (`property.write` to write), the whole org, behind
`Features:FiscalDocumentIssuance` like the rest of the setup and behind `Features:OwnerInvoicing` like the invoices that use them (404 while either is off;
while `OwnerInvoicing` is off the section `channels` shows no values). `IPortalAccountHolderResolver` is the one reader: the owner invoice, the statement labels of
E13 (its PDF and the owner portal) and the VAT summary of E15; the TD17 of E17 is meant to read it too.
## Statement files and the owner portal accounting (E13/F01/US02)

- **PDF and CSV of a version** (`GET api/owner-accounting/versions/{id}/pdf` and `…/csv`, `agency.statement.view`, the whole org): made from the ledger lines the
  version was cut from, in the order «Soggiorni», «Commissioni dei canali», «Ritenuta» (only when present), «Provvigione del PM», «Riaddebiti e spese»,
  «Rettifiche» (only when present), «Netto» (opening balance, total of the period, payments, closing balance). The net is the one the version stores: if the lines
  no longer give it the answer is 409 `owner_ledger_immutable` (see «Evidence»), never a file with another net. The box «Tassa di soggiorno» is for information,
  collected by the agency and to be remitted to the municipality or the cantonal tourist office, and is never added to or taken from the net.
- **Who holds the portal account.** The label of the commission of the channels follows it: «Trattenuta dal portale (fattura del portale a tuo nome)» when the owner
  holds the account (V1), «Commissioni del portale riaddebitate dal gestore (fattura n. …)» when the invoice of the property manager recharges it (V2 with
  `RechargePortalCommissions` on). The test is the one of the owner invoice (`PortalAccountHolders.RechargesCommissionOf` of `IPortalAccountHolderResolver`, E14), so
  the statement and the invoice never tell the owner two different things (E19/F01/US01/T01): an account of the agency whose commissions it does not recharge is never
  labelled as recharged and never names the invoice. **Known gap:** that line is labelled «Trattenuta dal portale (fattura del portale a tuo nome)» although the portal
  bills the agency; a label of its own needs new texts. The owner portal reads the same flag (`channelFeeRechargedByAgency`): the resolver reads the holders whatever
  the tenant of the caller, since the request of an owner has no org of its own.
- **Published = stored.** Publishing a version builds its PDF once, stores it with its SHA-256 as an owner document of type statement (the id of the document is the
  id of the version) and tells the people of the owner portal who read the statements of the owner by e-mail: the agency and the link to the page «Contabilità», never an
  amount, a period or a line. The owner downloads exactly the stored file. A version published before this release has no stored PDF: the agency gets a PDF built
  from its lines, the owner portal answers 404 `owner_statement_pdf_not_available`; a new version (after a correction) is published with its file. **When the owner cannot be
  told** (the queue refuses an e-mail, the public address of the web app is not configured, or reading who to tell fails) the publication stands, the failure is logged with the
  ids of the version, the owner and the org, and the agency gets a «To do» (type `OwnerStatementNoticeFailed`, action `ReviewOwnerStatement`, about the version, reason
  `owner_statement_notice_failed`, one for each version) to tell the owner by hand; nothing is raised for a portal that is off or an owner nobody reaches in it. If even the
  «To do» cannot be raised, that is an error in the log with the same ids.
- **Language and numbers.** The files are Italian unless `lang=en` is asked (no language is kept for an owner yet); the stored PDF is Italian. Amounts in the PDF follow the
  currency of the version: `1'234.50` for CHF, `1.234,50` for any other. The CSV uses the one CSV writer (decimal comma, no thousands separator).
- **Owner portal** (`GET api/owner-portal/statements`, `…/statements/{id}`, `…/statements/{id}/pdf`, `…/invoices`, `…/invoices/{id}/pdf`): the owner of the `owner_id`
  claim and the delegated contacts that hold `accounting.read` (403 otherwise); another owner's statement or invoice is the same 404 as a missing one; never a guest;
  behind `Features:AgencyTier` (ON in test): with the flag off the lists stay empty and a statement or an invoice is not there. The invoices are the issued documents
  with the purpose owner invoice made out to the owner profile (E14 makes them from a statement).

## Month-end in bulk (E13/F01/US03/T01)

Quokka manages about 250 flats: every month-end step runs for all the owners of a month in one call (`fatturazione/analisi-tecnica.md` §5 and ⚖ §7: no flag, no
migration, no new table).

- **Endpoint** `POST api/owner-accounting/periods/{y}/{m}/bulk` with `{ action, ownerIds? }`, `action` one of `post`, `generate`, `approve`, `publish`, `close`
  (member names, any case). The permission is the one of the single operation: `agency.statement.publish` for `publish`, `agency.statement.manage` for every other
  step (and `agency.statement.view` of the controller); a caller restricted to some properties gets 403 before anything is read. 422 `owner_bulk_action_invalid`,
  `owner_period_invalid`; 409 `owner_period_not_ended` for a `close` of a month that is not over (once, for every owner: never one refusal each).
- **Which owners.** Without `ownerIds`: every owner the readiness of the month knows (a line, a source, a version or a close in it), by id. With a list: those named,
  each once, in the order given. **An empty list is 422 `owner_bulk_owners_empty`, never «everyone»** (the list was sent and says nobody); **no list on a month in which the
  readiness knows no owner is an empty run**: 200 with no row (`total` 0), not that error. More than 5,000 is 422 `owner_bulk_too_many_owners`; an id that is
  not an owner of the org (another org's included: the same row for both) is a row `owner_not_found` and nothing is run for it.
- **One transaction and one result for each owner.** `generate`, `approve`, `publish` and `close` run owner by owner through the single operations (their rules, their
  refusals, their lock of the org): an owner that fails leaves nothing behind (the tracked changes are dropped) and never stops the others. The answer has one row for
  each owner `{ ownerId, ok, code?, unchanged, issues?, details? }` and the counts (`total`, `processed`, `succeeded`, `unchanged`, `failed`, `allDone`):
  **`allDone` is true only when the run is over, every owner was answered and none failed.** `ok` is the truth (the owner is in the state the step leads to);
  `code` is the stable code of the reason (why a step was not done, or a note on an `ok` row: `owner_period_closed`, `owner_statement_unchanged`, a version generated
  `owner_statement_flagged`); `issues` are the blocking issue codes behind it; `details` the facts to act on (`due`, `paid`, `difference` of a close that does not
  reconcile). A failure that is not a rule of the domain (the database, a file) is logged with the ids and its exception (its cause and stack stay in the log) and is
  `owner_bulk_failed`, never a success.
- **Steps.** `post` posts the ledger of the month for the owners asked for **in one transaction** (a ledger is posted from all the sources of the month under one lock;
  the lines of the other owners are left as they are): a row fails with the first blocking issue that kept a source out of its ledger (`mandate_missing_for_stay`,
  `ownership_share_gap`, `fx_missing`...), is done when lines were posted or reversed, unchanged when there was nothing to post. `generate` makes the version
  (`owner_statement_flagged` with the blocking `issues` on an `ok` row when it came out flagged). `approve` approves the version in force: **a flagged version is never
  approved in bulk, it is a failed row `owner_statement_flagged` with its blocking `issues`**; one already approved or published is unchanged. `publish` publishes the
  approved version (its file, its PDF and the notice to the owner, as the single publication does them). `close` closes the month on the approved version reconciled to
  the confirmed payouts or the paid wires and **never waives a difference** (that needs a reason and `agency.owner.financial.manage`, owner by owner): a difference or a
  negative balance is a failed row `owner_close_unreconciled` with `details`.
- **Idempotent.** An owner already where the step leads (a version generated and not changed since, with the same lines, opening balance and issues; approved;
  published; a closed month, for every step but the publication) is an `unchanged` row and nothing is made, posted, published or closed twice: a run is repeated after a failure
  or a deploy. **A closed month is not «published» by its close**: the close accepts an approved version too, so `publish` on a closed month publishes a version that is only
  approved (the owner portal and the accountant read only published statements) and is `unchanged` (`owner_period_closed`) only when the version is already published.
- **Up to 20 owners the answer is the request's** (200 with the rows); **for more it is a background job**: 202 with `jobId`, `status` `Queued`, the `Location` of the
  progress and nothing run in the request. `GET api/owner-accounting/periods/{y}/{m}/bulk/{jobId}` (`agency.statement.view`) answers `status` (`Queued`, `Running`,
  `Completed`, `Failed`), `total`, the counts and the rows done so far. The job (`OwnerBulkCloseJob`, Hangfire) runs under the tenant of the org and as the user who
  asked, with the owners resolved when it was queued; **its progress is a parameter of the Hangfire job (`owner-bulk-progress`), so nothing is stored in the database**,
  and it lasts as long as Hangfire keeps the job (a day after it succeeded by default): an expired run, one of another org or month, or an id that is not a job is the
  same 404 `owner_bulk_job_not_found` (the org and the month are read from the arguments the job was queued with, never from the caller). No automatic retry: a run that
  fails as a whole is `Failed` with `errorCode` (`owner_bulk_failed`, or the code of the rule that stopped it) and the rows done before it, and it is run again, which
  finds the owners done as they are.
- **Cost.** Each owner reads the plan of the month again inside its own step (so a source that arrives during a long run is seen by the next owner, and the close reads
  the readiness at its own moment): 250 owners are a few minutes of background work, not seconds. The screen of the bulk close shows the reconciliation of the month
  (below) before it asks the agency to close.
- **Not here.** No notification when a run ends (the screen polls the progress); no waiver; no bulk correction.

## Month reconciliation (E13/F01/US03/T02)

Acceptance step C1 of `pilota-flusso-completo-2026-11-14.md`: before closing a month the agency checks that the portals' payments, the bookings and the statements
agree. **Read only, no entity, and a difference blocks nothing: the agency decides** (the close of an owner does not read it).

- **Endpoint** `GET api/owner-accounting/periods/{y}/{m}/reconciliation` (`agency.statement.view`, the whole org: 403 otherwise; `Cache-Control: no-store`). 422
  `owner_period_invalid`. Amounts are in the currency they were posted or recorded in and **are never added to those of another currency**: a channel has a row for
  each currency, the statements one for each statement currency.
- **`channels`**: one row for each channel and currency with a stay posted in the month (by channel, then currency). `stays` and `ledgerGross` are the live lines of the owner
  ledger of the month that come from a booking (neither reversals nor reversed lines; the owners' shares add up to the whole stay): the stays with a revenue line and their
  gross without the tourist tax, in the currency of the property. `commissions` is what the portals kept, split by where the line says it comes from (its rule version):
  `fromPortal` (`payload`, the reservation's own figure; also the lines of before the rules existed, which could only come from it), `fromRule` (`channel-rule:{id}`, a rule of
  the agency) and `fromDefault` (`channel-default`, the configured default); `total` and `onRule` (`fromRule + fromDefault`) tell how much of it rests on a rule and not on the
  portal (⚖ R2). For a **portal** (`FiscalCopy.IsOtaBookingSource`): `expectedNetPayout` is the gross less the commission of the stays **the portal collected** (a stay with
  `OtaCompanyCollect = false` is collected by the host: no payout is expected, it is counted in `hostCollectedStays`; unknown counts as collected, as the payment ledger reads
  it), `recordedPayouts` what CasaZen records as paid for the stays that have a payout figure, `difference` the recorded less the expected of those stays (negative: less was
  recorded), and `staysWithoutRecordedPayout` the stays with an expected payout and no payout figure. These four are `null` for a channel that is not a portal (the agency's own
  site, a booking entered by hand). **Not available is never zero** (ADR-033-R2): when the row expects a payout and not one of its stays has a figure, `recordedPayouts` and
  `difference` are `null`, not 0 and not minus the expected (a row with some figures compares only the stays that have one, and counts the others in
  `staysWithoutRecordedPayout`).
- **What is «recorded».** Read from the database like the payment ledger (`IBookingPaymentLedgerService`), without a call to the channel manager: the payments collected in
  CasaZen for the booking (recorded by hand: each payment net of its refunds and **without its part of the tourist tax**, as the owner ledger posts it,
  `OwnerLedgerRules.RevenueOfPayment`; a payment refunded in full counts 0) and, when there is none, the net the channel manager reported
  (`Booking.OtaNetAmount`; **never derived** from the amount paid and the commission, ADR-033). **Today that is no figure at all for a stay of a portal:** the Octorate import
  gives no net (`Booking.OtaNetAmount` stays `null`, `BookingStateMapper.ToOtaAmounts`) and a portal stay takes no payment by hand (`payment_booking_from_portal`), so every stay
  the portal collected is told as `payout_source_unavailable` until a source of the payouts exists (the amount the guest paid, `Booking.OtaPaidAmount`, is not the payout and is
  never used for it). The comparison is on the gross **without the tourist tax**: a portal that collects it (Airbnb in Ticino, ⚖ C5) is read for the rest.
- **`statements`**: one row for each statement currency of the month: the owners with a line in it (`ledgerOwners`), the published statements (`publishedStatements`: the
  newest published version of each owner for the month, even superseded by one not published yet, as the owner portal reads it), `ledgerTotal` (every line of the month,
  a reversal cancelling the line it reverses), `publishedTotal` (closing less opening balance of those versions) and `difference` (published less ledger).
- **`differences`**: every difference, by what it is about (`kind`: `Stay` with the booking, `Entry` with the ledger line, `Statement` with the version), with `reason`,
  `currency`, `expected`, `recorded` and `difference` (**always recorded less expected**, `null` when there is no recorded figure to subtract), the `channel` of a stay and the
  `ownerId` of an owner's ledger or statement. The reasons: `payout_source_unavailable` (a stay the portal collected has an expected payout and CasaZen has no figure for it: `recorded`
  and `difference` are `null`, not available and never zero), `payout_differs`, `stay_not_posted` (an
  active stay of the month with no collected payment, that the ledger posts from its booking, with a gross to post and no live revenue line in any month: expected its
  gross, recorded 0; a stay paid in CasaZen is posted by its payments on their day and is never told here), `statement_not_published` (the owner has no published statement:
  its lines are in none; the lines of a booking of an owner are one difference, any other line is one by itself) and `posted_after_publication` (lines posted after the
  cut-off of the published statement, listed the same way). What is left once those are taken out (a published total that is not the total of the lines it was cut from) is
  told by the version, `statement_total_differs`, so **the listed differences with a figure add up to the difference of the totals** (the payouts of a channel row, the statements
  of a statement row); `payout_source_unavailable` has no figure and `stay_not_posted` is in no total.
- **Scope.** Every read names the org; the lines, the bookings (also of a property deleted since) and the payments of another org are never read; nothing is written; the
  logs carry ids and counts.

## External invoice references (E06/F01/US02/T05)

- **The months invoiced outside CasaZen.** Before E14 the agency invoiced its fee with its own fiscal system; the stored agency invoices of the retired
  generator stay readable (`api/agency-invoices/{id}/xml`, `…/pdf`) and carry `ComplianceStatus = legacy_unverified` (ADR-019 amended). The courtesy copy is
  dated with the day the invoice was made (`CreatedAt`), never the day it is downloaded. `POST api/agency-invoices/generate` makes the owner invoices of a
  period since E14/F01/US01/T01 (see «Owner invoices»).
- **Link** (`OwnerExternalDocumentReference`): `POST api/owner-accounting/versions/{id}/external-documents` (`agency.owner.financial.manage`) on an
  approved or published version, with `provider`, `externalId`, `externalNumber`, `externalDate`, optional `documentUrl` (https) and `documentSha256`.
  `…/external-documents/{id}/verify` records the check against the external system, `…/remove` (with `reason`) removes a mistaken link: the row stays.
  The link's facts never change (409 `owner_ledger_immutable`) and it never changes the statement.
- **Owners** read the links of their published versions in `GET api/owner-portal/external-documents`; never a removed one, never before publication.

## Payouts and tax evidence (E06/F05/US02/T03)

- **Only a payout decides a withholding.** No statement operation (posting, version, approval, publication, export) creates withholding any more: the
  statement-based calculation and the 21% and `1919` literals are gone (ADR-018 amended). `POST api/owner-accounting/payouts`
  (`agency.owner.financial.manage`) confirms a payout of an approved or published version: `idempotencyKey`, `ownerId`, `statementVersionId`, `paidOn`,
  `reference` and, optionally, `grossAmount` and `currency` (the version's). **Since E18/F01/US01/T01 the request carries no fact**: the gross, the
  collection by the agency and the qualifying short lets are read from the ledger (see «Facts of the payout»); `agencyCollected`, `qualifyingShortLet`,
  `components` and `stayIds` sent by an older client are ignored. `GET api/owner-accounting/payouts/preview?statementVersionId=&paidOn=` (`agency.statement.view`,
  read only, `no-store`) answers what the confirmation would be: the facts, the `balance` still unpaid, the decision with the withholding in euro and in the currency
  of the payout and the net, or the `blockingCode` (and `blockingField`) the confirmation would answer.
- **Decision** (`PayoutFactRules.Decide` → `OwnerWithholdingRules.Decide`, on the approved rule in force on `paidOn`; a rule set that is not approved, the seeded draft
  included, decides nothing: `owner_fiscal_policy_not_approved`): a rule without withholding (a Swiss rule) is `not_applicable` / `rule_without_withholding`; no stay
  left to decide is `no_stays_to_decide`; every stay withheld by its portal `already_withheld_by_portal`; otherwise the stays in the base make the component
  `stay_gross` and the decision is `applicable` (the amount `round(base × rate / 100)` with the rule's rounding, the tax code the rule's), or, with none in the base,
  `no_collection_or_intervention` or `not_qualifying_short_let`. A fact the rule needs that cannot be read is 422 `tax_applicability_unresolved` with the `field`
  (`stays`, `fxRate`, `components.{code}` for a rule that names another component, `withholdingRatePercent`, `withholdingTaxCode`, `rounding`,
  `grossBaseComponents`).
- **Evidence** (`TaxObligationEvidence`, append-only): decision, reason, jurisdiction, rule code and version, base, rate, rounding, amount and tax code
  (both `null` when not applicable: never a zero obligation), payment day and month, stays, facts, payout and statement version. A retry with the same
  key answers the same payout (no second decision); the same key with other facts is 409 `owner_payout_key_reused`. `…/payouts/{id}/reverse` (with
  `reason`) adds the opposite evidence row (in the month of the reversal) and reverses the ledger lines.
- **Ledger**: a payout posts, in the month of `paidOn`, the net paid (`OwnerPayment`) and the withholding kept (`Withholding`), together the gross that
  settles what was owed. **A transfer is recorded once**: a month an owner settled with a confirmed payout is settled by it, so the wires of the same
  statements are neither posted nor counted at the close (`PlanAsync`, `CloseAsync`); if the payout is reversed the wires count again. **The wire pays the
  net**: the wire batch is made from the confirmed payouts and each transfer is the payout's `netAmount`, the gross less the withholding (E16/F01/US01/T01,
  checked again by E18/F01/US01/T02 with a payout decided on the facts of the ledger: [`wire-transfers.md`](wire-transfers.md)), never the `NetPayable` of an old
  statement. A payout in another currency than euro keeps the withholding decided in euro at the agency's rate of the payout day (⚖ R6, see «Facts of the
  payout»); without a rate it is 422 `tax_applicability_unresolved` (`fxRate`): nothing is converted by guess. `…/payouts/{id}/reverse` posts the reversals in
  their month while it is open, else in the month of today, which must be open too (409 `owner_period_closed`: a closed month is never posted).
- **F24** (`api/withholding/f24`, `…/f24/generate`): built from the applicable evidence of the month (and its reversals) of an **Italian** rule only (`IT` or
  `IT-…`: the evidence of a Swiss or any other jurisdiction is never an F24 or a CU, whatever its parameters say), with the tax code and the rule versions of
  that evidence; 422 `tax_no_obligations` without any or when the obligations net to nothing (a payout and its reversal in the same month: a record only
  generated is dropped, never a zero or negative F24), `f24_tax_code_mixed` with more codes. It runs under the lock of the owner accounting of the org, so
  two requests for one month make one record; due on the 16th of the next month (16 January for December). **Annual data** (`api/withholding/annual/{year}`):
  the Certificazione Unica data per owner. Both start `generated`, are regenerated only while generated (else 409 `tax_file_already_filed`) and move
  `reviewed` → `submitted` (only with the `handoffReference` of a controlled manual handoff) → `accepted` (with the `receiptReference`) or `rejected`
  (with the `reason`) through `…/filing-state` (`agency.owner.financial.manage`). Nothing is ever sent by CasaZen. `api/withholding/reconciliation/{year}`
  compares the obligations of each month with its F24 and the year with the annual data. The F24 PDF prints the filing state and the receipt.
- **F24 export** (E18/F01/US01/T02, NF-117): `GET api/withholding/f24/{year}/{month}/export?format=csv|xlsx` (`agency.withholding.manage`, `no-store`), next to the
  PDF of the stored record, read only (it neither generates nor changes the record): a first table with **one line per tax code and period** (tax code, reference
  month and year, amount in euro, due date on the 16th of the next month, the decisions that make it: `1919` while the approved rule says so; two codes in a month are
  two lines, never merged), then the **breakdown per owner and payout** (owner, payout reference and day, decision or reversal, tax code, rule version, base, rate and
  withholding in euro, then the currency, gross, withholding kept and net of the payout; a reversal has the opposite amounts), so the breakdown adds up to the lines.
  Built from the same evidence as the F24 (`IWithholdingService.GetF24DataAsync`, an Italian rule only), in the language of the request; the Excel file has a sheet
  `F24` and a sheet for the breakdown. 400 `fiscal_export_invalid_format`; 422 `tax_no_obligations` (no decision in the month), `owner_period_invalid`. The data to pay
  and file with: **payment and filing stay with the agency or its accountant**, no telematic file is produced.
- **Reach**: every action of `api/withholding` needs a caller of the whole org since E18/F01/US01/T02 (403 for a caller restricted to some properties, before anything is
  read), like the rest of the owner accounting: the F24 data and the breakdown name every owner of the org.
- **Existing rows**: the F24 records created before get the state `generated`; the old `WithholdingLog` rows stay as history, nothing writes them.

### Facts of the payout (E18/F01/US01/T01)

Decisions of 2026-11-14 (`fatturazione/analisi-tecnica.md` §5, ⚖ I9, R5, R6 of the table §7): prudent defaults, configurable, to be confirmed by an accountant. No flag
(the owner accounting is a module of the plan), no migration.

- **The gross** is the balance the version still owes (`OwnerPayoutFacts.UnpaidBalanceAsync`): its closing balance less the gross of the confirmed payouts of the owner
  in its currency that it does not count (paid in a later month or confirmed after its cut-off). Without `grossAmount` the payout pays it all; with one (a part, the
  working capital the agency keeps) never more: 422 `owner_payout_exceeds_balance` with `balance` and `currency`, also when nothing is left. A retry with the same key
  answers the same payout and never reads the facts again.
- **The stays** are the owner's revenue lines of the ledger (`BookingRevenue` from a payment or from the booking) of the month of the version and of the months before,
  posted up to its cut-off, added up per stay: the owner's share of the gross **without the tourist tax** (the ledger never posts it as revenue). A stay with nothing left
  (cancelled, refunded) is none. A stay a confirmed payout of the owner already covered (its `stayIds`) is never decided twice: the second payout of a version decides
  `no_stays_to_decide`; a reversed payout frees its stays. A payout confirmed before this release lists no stays and covers every stay of its version's month and of
  the months before. A stay whose booking cannot be read, or whose lines are in two currencies, is 422 `tax_applicability_unresolved` (`stays`).
- **Each stay** (`PayoutFactRules.EvaluateStay`, codes of `PayoutStayReasons`):
  - *qualifying short let*: active (confirmed, checked in or out, no no-show: `AlloggiatiStatusRules`), in an Italian property, 1 to `ShortStayFiscal:MaxStayNights`
    nights (30), of an owner who is a private natural person (the registry, `OwnerTaxRules.IsNaturalPrivate`, and the fiscal profile in force is not a legal entity),
    not presumed a business and with no decision `NotShortLet` of a company tenant (`IOwnerTaxChecks.TreatmentOfStaysAsync`, E18/F01/US03, on the payout day);
    otherwise `stay_not_active`, `property_not_italian`, `stay_length_out_of_range`, `owner_not_private`, `owner_presumed_business`, `company_tenant_not_short_let`;
  - *collected or handled by the agency*: posted from its payments (`casazen_payment` with a Stripe payment of CasaZen, `recorded_payment` for a payment the agency
    recorded), or a stay of a portal that collects and pays the agency because it holds the account (`portal_pays_agency`, V2, `IPortalAccountHolderResolver`); not
    the agency's: `portal_pays_owner` (V1), `collected_at_property` (`OtaCompanyCollect = false`), `payment_not_recorded` (the agency's own site or a booking entered
    by hand whose payment nobody recorded: record it before the payout);
  - *already withheld by the portal* (never withheld again, AC-NF117-1): a collected payment of the stay carries the withholding of its portal
    (`Payment.OtaWithholdingTax`, `payment`), or the stay qualifies and its portal is one that withholds for an Italian private host itself (`portal_rule`, ⚖ R5:
    `portalsWithholding` of the rule set, else `OwnerAccounting:ItalianShortLetRule:PortalsWithholding`, committed `Airbnb`);
  - *in the base*: what the rule requires (collection, qualifying contract) and not withheld; its `baseAmount` is in euro.
- **Currency (⚖ R6).** The base, the withholding and the evidence are in euro (the F24). A stay in another currency is converted at the agency's rate of the payout day
  (or of the 7 days before, `api/owner-accounting/fx-rates`); the withholding of a payout in francs is kept at the rate euro → francs of that day (half to even, as the
  ledger converts), and the net is in francs: `Withholding` line in francs with the euro as its original amount and the rate. Without a rate: 422
  `tax_applicability_unresolved` (`fxRate`).
- **Evidence.** `factsJson` of the evidence (and `facts` of `TaxObligationEvidenceDto`) holds the facts with `source: "ledger"`: the gross, the balance, the currency and
  the one of the withholding, every stay with its facts and its reason, the components, the portals of ⚖ R5, the rate and the parameters of the rule. The evidence of
  before this release has the facts that were typed (no `source`; `facts` is `null`). The co-owners (⚖ I6): each owner is paid its share, so the facts are its share;
  the attribution of the Certificazione Unica to the signatory is E18/F01/US02's.
- **The Italian rule as a draft (⚖ I9).** At every start (`OwnerWithholdingDraftSeedHostedService`) each agency with owners gets, once per version, the rule of
  `OwnerAccounting:ItalianShortLetRule` (`deploy-checklist.md` § 2.11) as a rule set that is **not approved**: `IT-STR-WITHHOLDING` `2026.1` from 2024-01-01, 21%,
  tax code 1919, base `stay_gross` (the gross without the tourist tax), `half_up` to the cent, 10 years, `portalsWithholding: ["Airbnb"]`, source
  `https://www.agenziaentrate.gov.it/portale/le-regole-per-gli-intermediari`, with **no hash of the source** (`sourceSha256` empty). It decides nothing: an owner whose
  profile points at it gets `owner_fiscal_policy_not_approved` (the payout is refused and nothing is written; the preview says it). The accountant approves it with
  `POST api/owner-accounting/policies/{id}/approve { approvedByName, sourceSha256 }`: the SHA-256 of the document checked is **required** for a rule set whose source is
  not pinned (422 `owner_policy_invalid`, `sourceSha256`) and pins it; for a pinned one a different hash is refused the same way. Then the profiles of the Italian owners
  point at it. A rule set approved before this release keeps working only if its `grossBaseComponents` is `stay_gross` (else 422 `components.{code}`: approve the new
  version).

## Limit of units, company tenant and co-owners (E18/F01/US03/T01)

Decisions of 2026-11-14 (`fatturazione/analisi-tecnica.md` §5, ⚖ I6, I7 and I8 of the table §7): prudent defaults, configurable, to be confirmed by an accountant; nothing is
filed by CasaZen. The warnings and the endpoints are behind `Features:TaxReportingExports` (off by default, on in the test environment: `feature-flags.md`); the **facts the
withholding reads are not behind it** (an owner past the limit is a business whatever the screens show, a decision already recorded is followed).

- **Limit of units** (⚖ I8, `IOwnerTaxChecks.CheckUnitsAsync`). The units of an owner are the apartments the agency manages for it (the distinct **Italian** properties in
  **short-rent** mode, not deleted, with an ownership of the owner in force on the day: a co-owner counts the property once, whatever its share, because the limit is counted
  for each co-owner) plus `PropertyOwner.ExternalShortLetUnits`, the apartments the owner declares it lets short outside the agency (0 to 1000, 0 until declared). A natural
  person who is not a VAT holder (`entityKind` not a company, `taxRegime` `Private`; a legal entity or a VAT holder is a business already, `applies: false`) with **more
  than `ShortStayFiscal:MaxApartmentsPerTaxpayer` units** (2: the third is the first past the limit) is **presumed a business** from the tax year
  `ShortStayFiscal:ThresholdFromTaxYear` (2026); a day of an earlier year presumes nothing, as the older limit is not in the configuration. The limit is the configuration of
  the fiscal rules (`deploy-checklist.md` § 2), never a number of the code. The count is the owner's whole portfolio in the org, whatever the reach of the caller.
  - **Treated as a business**: `TreatmentOfStaysAsync` answers `qualifiesAsShortLet: false`, reason `owner_presumed_business`, for every stay of the owner: no short-let
    regime, so no 21% withholding. The facts of E18/F01/US01 read it; this task decides nothing else about the withholding.
  - **Readiness warning**: `GET api/onboarding/owners/readiness` gives the owner `warnings: [{ code: "short_let_business_presumed", managedUnits, externalUnits, maxUnits,
    suggestion: "issuer_profile" }]`. A warning never changes `ready` or `accountingReady`. The suggestion is to make the issuer profile of the owner that of a business (a VAT
    regime and its VAT number, `PUT api/owners/{id}/issuer-profile`): nothing is changed by itself. A caller with a partial reach gets the warning only for an owner whose every
    ownership it reaches (the line of `GET api/owners/{id}/tax-checks`, of the import and of the invitations): the counts are the whole portfolio, so for an owner with a property
    outside the reach there is no warning, as the counts would tell about properties the caller does not reach.
  - **Statement flag**: a statement version generated for the owner records the **non-blocking** issue `short_let_business_presumed` (in `issuesJson`, the units of the last day of
    the month): the version is never `Flagged` because of it and can be approved as usual. A version generated before the owner crossed the limit does not carry it.
  - **API**: `GET api/owners/{ownerId}/tax-checks` (`agency.owner.view`) answers `{ ownerId, applies, managedUnits, externalUnits, totalUnits, maxUnits, presumedBusiness }`;
    `PUT api/owners/{ownerId}/external-short-let-units` (`agency.owner.financial.manage`) with `{ units }` (**required**: a request without it is 422
    `owner_external_units_invalid`, never a 0 by omission) saves the declared apartments and answers the new check. Both need the owner in the reach of the caller (an owner
    with a property outside it is the 404 `owner_not_found`).
- **Company tenant** (⚖ I7). The tax agency (circular 24/E/2017) excludes the short-let regime when the tenant is a company, the Court of Cassation (12395/2024) says only the
  landlord counts when the use is residential: CasaZen **changes nothing by itself**, a person decides and the decision is recorded.
  - **The case**: an **active** stay (confirmed, checked in or checked out, no no-show: `AlloggiatiStatusRules`), in an **Italian** property, of at most
    `ShortStayFiscal:MaxStayNights` nights (30), whose invoice data names a **company** (only the kind is read, never a name or a fiscal code), and at least one owner of the
    property on the check-out day is a private natural person (`OwnerTaxRules.IsCompanyTenantCase`, the one definition).
  - **«Da fare»** (`ActivityType.CompanyTenantDecision`, about the booking, leading to it): raised once for each booking by the consumer of `InvoiceDataSaved` and `BookingConfirmed` (either order: the state is read when the event is handled)
    (`CompanyTenantDecisionHandler`, only while the flag is on) with a fixed sentence in the language of the org that tells the two readings in plain words (key
    `TodoReason_CompanyTenantDecision` of `EmailTexts`, no rate in it: the rate is configuration); no personal data. It closes when a decision is recorded, when the booking is cancelled or marked a
    no-show, and (by the system) when the consumer reads the stay and it is no case any more (the invoice data became a private person's, the dates or the owners changed: a decision
    would be 422 `owner_tax_decision_not_applicable`); it is asked again if the stay becomes a case again, never after a person closed it or a decision was recorded. **Limit**: the
    state is read only when one of the two events is handled, so a stay that becomes a case in another way (an owner is added to the property after its bookings: the owner import is
    the only writer of the ownerships; the dates change) raises no «Da fare»; `GET api/owner-tax-decisions` lists it all the same.
  - **Decision** (`ShortLetTaxDecision`: booking, regime, reason, who, when; **append only**, the latest in force, the earlier ones are the history): `POST api/owner-tax-decisions`
    (`agency.owner.financial.manage`) `{ bookingId, regime, reason }` with `regime` `ShortLet` (the stay stays a short let: the intermediary withholds) or `NotShortLet` and the
    reason a note of the agency (1 to 500 characters, never logged, never in the «Da fare»). The regime is **required** (no default by omission). The same regime and reason again
    is the same decision. A stay that is no case is 422 `owner_tax_decision_not_applicable`: a regime is never recorded for a stay nobody questioned. 404 `booking_not_found`
    (also a booking outside the reach); 422 `owner_tax_decision_invalid` with the `field`. `GET api/owner-tax-decisions?state=Pending|Decided` (`agency.owner.financial.read`)
    lists the cases in reach with the decision in force (the code of the booking, the property and the dates: never the tenant), the most recent check-out first, at most 200.
  - **Withholding facts**: `TreatmentOfStaysAsync(orgId, ownerId, bookingIds, asOf)` gives each stay `qualifiesAsShortLet`, a `reason` (`short_let`, `owner_presumed_business`,
    `company_tenant_short_let`, `company_tenant_not_short_let`), `companyTenant` and `decisionPending`. A company tenant **without a decision stays a short let** (⚖ I7: no automatic
    change of regime) and says `decisionPending`; `NotShortLet` takes the stay out of the withholding; a decision is followed only while the stay still is a company-tenant case
    (one recorded for a stay whose recipient changed since is not). A payout already confirmed keeps its own evidence: a later decision changes the stays not yet paid.
- **Co-owners** (⚖ I6, acceptance step A4). `OwnerMandate.CoOwnerTaxReporting` (`coOwnerTaxReporting` on `POST api/owners/{id}/mandates`, in the standard mandate and in
  the answer): `Signatory` (0, the default: everything on the co-owner who signed the mandate, the one whose mandate is in force, as `DocumentPlanService` picks it) or
  `PerShare` (1: the withholding evidence and the CU split by `PropertyOwnership.SharePercent`). An undefined value is 422 `owner_mandate_invalid` with the `field`
  `coOwnerTaxReporting`. Every mandate written before the field is `Signatory`. E18/F01/US01 and US02 read it; nothing is split by this task.
- **Data**: migration `AddOwnerTaxChecks`: `PropertyOwners.ExternalShortLetUnits` (int, default 0, check 0 to 1000), the table `ShortLetTaxDecisions` (tenant-owned, gone with the
  org, the booking a plain id with no foreign key: the decision is tax evidence) and `OwnerMandates.CoOwnerTaxReporting` (int, default 0). Rolling it back drops the decisions: they
  are evidence, so a rollback after decisions were recorded is a decision of the operator, not a routine step.
