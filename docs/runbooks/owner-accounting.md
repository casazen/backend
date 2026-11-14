# Owner accounting of the agencies (epic E06, feature F05)

How CasaZen keeps the accounts of the owners an agency manages: the approved rule sets and the fiscal profiles of the owners (E06/F05/US02/T01), the
append-only owner ledger, the statement versions and the month-end close (T02), the external invoice references that replace the old agency invoices
(E06/F01/US02/T05) and the payouts with their tax evidence (T03). Requirements NF-25, NF-109, NF-110; ADR-012, ADR-017, ADR-018, ADR-019 (amended).

## Product boundary

- **A rendiconto is not an invoice.** The owner statement is a management report («Rendiconto gestionale - non è una fattura»): no fiscal series or
  number, never FatturaPA XML. CasaZen issues **no agency-to-owner invoice**: when the agency invoices its fee, its own external fiscal system issues the
  invoice and CasaZen only keeps a reference to it (see «External invoice references»).
- **Legal and accounting review is a launch gate.** Every rate, applicability rule, tax code and retention period is data of a rule set approved by a
  qualified accountant; nothing is inferred from a country or written in the code.

## Rule sets and fiscal profiles (E06/F05/US02/T01)

- **Rule set** (`OwnerAccountingPolicy`, `api/owner-accounting/policies`): jurisdiction (`IT`, `CH`, `CH-TI`...), rule code, version, validity, the
  official source (an `https` address and the SHA-256 of the document) and the parameters (`OwnerAccountingRuleParameters`): `withholdingApplies`,
  `withholdingRatePercent`, `withholdingTaxCode`, `requiresAgencyCollection`, `requiresQualifyingShortLet`, `grossBaseComponents`, `rounding`
  (`half_up` or `half_even`), `retentionYears`. A rule set is used only once the approval of the qualified accountant is recorded
  (`POST …/{id}/approve` with `approvedByName`); an approved rule set never changes: a change is a new version.
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
  (`agency.owner.financial.manage`) with `validFrom`, `commissionPercent` (0-100, two decimals), `commissionBase` (`GrossExclTouristTax` = 1, the default,
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
  outside the reach is 404 `owner_not_found`. A second call creates nothing.
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
  (`agency.owner.financial.manage`) replaces the rules with the whole list `[{ channel, percent, base, validFrom, validTo }]` (an empty list leaves the
  defaults; a rule saved again as it was keeps its id, which the posted lines name); the rules of a channel never overlap. 422
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
  `PartiallyRefunded` and `Refunded`: a refund moves a payment to one of the last two, so the revenue is `Amount - RefundedAmount`; a partial refund lowers
  the line, a full one leaves nothing; the share in force on the payment day; recognized on the payment day, or on the check-out for a profile whose
  recognition rule is `stay`), the mandate commission on that share, the channel commission of an **active** booking (the one definition of the code:
  `AlloggiatiStatusRules.ActiveBookingStatuses` and no no-show) whose payments are gross (`OtaCommissionIncludedInPayments`, with its first payment that
  still holds money), the approved charges (`OwnerCharge`, one-time or monthly, to one owner or split by the shares), the wires marked paid **unless the
  owner settled the same statement month with a confirmed payout** (see «Payouts»). The shares are split in the source currency, rounded half to even, the
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
- **Readiness** (`GET …/periods/{y}/{m}/readiness`): issues of a property (`ownership_missing`, `ownership_share_gap`, `source_amount_missing`) have no owner of
  their own and are listed for the org, **and** hold back the statement of every owner of that property (a source left out of the ledger is a line missing
  from their accounts); owner issues from the fiscal profile (`owner_fiscal_*`), `fx_missing`, `unposted_source`, `duplicate_entry`, `currency_mixed`,
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
- **Not yet**: the import of the outgoing bank movements (a wire marked paid, or a confirmed payout, is the evidence of the transfer), and the wire batch that
  pays the net of the withholding (see «Payouts»).

## External invoice references (E06/F01/US02/T05)

- **No owner invoice in CasaZen.** `POST api/agency-invoices/generate` answers 410 `owner_invoice_issuance_removed`; the stored agency invoices stay
  readable (`…/xml`, `…/pdf`) and carry `ComplianceStatus = legacy_unverified` (ADR-019 amended). The courtesy copy is dated with the day the invoice was
  made (`CreatedAt`), never the day it is downloaded. The requirements v2.2 (NF-110: «agency-to-owner tax invoicing is outside CasaZen») and ADR-019 decide
  the retirement; the brief of E06/F01/US02/T05 asks for it in its coding objective, while its «Files» section and the criterion 3 of E06/F01/US02/T04 still
  name the old generator and a PDF overload (the briefs are not edited: the deviation is declared in the pull request). The frontend button «Genera
  fattura» must be replaced by the link of the external invoice, or hidden (manual follow-up of the pull request): until then it answers 410.
- **Link** (`OwnerExternalDocumentReference`): `POST api/owner-accounting/versions/{id}/external-documents` (`agency.owner.financial.manage`) on an
  approved or published version, with `provider`, `externalId`, `externalNumber`, `externalDate`, optional `documentUrl` (https) and `documentSha256`.
  `…/external-documents/{id}/verify` records the check against the external system, `…/remove` (with `reason`) removes a mistaken link: the row stays.
  The link's facts never change (409 `owner_ledger_immutable`) and it never changes the statement.
- **Owners** read the links of their published versions in `GET api/owner-portal/external-documents`; never a removed one, never before publication.

## Payouts and tax evidence (E06/F05/US02/T03)

- **Only a payout decides a withholding.** No statement operation (posting, version, approval, publication, export) creates withholding any more: the
  statement-based calculation and the 21% and `1919` literals are gone (ADR-018 amended). `POST api/owner-accounting/payouts`
  (`agency.owner.financial.manage`) confirms a payout of an approved or published version with its facts: `idempotencyKey`, `grossAmount` (what is
  settled, before the withholding), `currency` (the version's), `paidOn`, `agencyCollected`, `qualifyingShortLet`, `components` (gross by code) and
  `stayIds`.
- **Decision** (`OwnerWithholdingRules.Decide`, on the approved rule in force on `paidOn`): a rule without withholding (a Swiss rule) is
  `not_applicable` / `rule_without_withholding`; otherwise a fact the rule needs that is unknown is 422 `tax_applicability_unresolved` with the `field`
  (`agencyCollected`, `qualifyingShortLet`, `components.{code}`, `withholdingRatePercent`, `withholdingTaxCode`, `rounding`, `grossBaseComponents`);
  no collection or intervention is `no_collection_or_intervention`, no qualifying short let `not_qualifying_short_let`; else `applicable`: the base is
  the sum of the components the rule names, the amount `round(base × rate / 100)` with the rule's rounding, the tax code the rule's.
- **Evidence** (`TaxObligationEvidence`, append-only): decision, reason, jurisdiction, rule code and version, base, rate, rounding, amount and tax code
  (both `null` when not applicable: never a zero obligation), payment day and month, stays, facts, payout and statement version. A retry with the same
  key answers the same payout (no second decision); the same key with other facts is 409 `owner_payout_key_reused`. `…/payouts/{id}/reverse` (with
  `reason`) adds the opposite evidence row (in the month of the reversal) and reverses the ledger lines.
- **Ledger**: a payout posts, in the month of `paidOn`, the net paid (`OwnerPayment`) and the withholding kept (`Withholding`), together the gross that
  settles what was owed. **A transfer is recorded once**: a month an owner settled with a confirmed payout is settled by it, so the wires of the same
  statements are neither posted nor counted at the close (`PlanAsync`, `CloseAsync`); if the payout is reversed the wires count again. Known limit: the wire
  batch generator still pays the `NetPayable` of the statement, which since E06/F05/US02/T03 no longer carries a withholding; until the wire amounts take the
  net of the payout (E06/F05/US02/T04) the agency pays the net of the withholding by hand and records the payout, instead of marking the wire paid for the
  same money. A payout in another currency than euro that the rule makes applicable is 422 `tax_applicability_unresolved` (`currency`): an F24 is in euro and
  nothing is converted by guess. `…/payouts/{id}/reverse` posts the reversals in their month while it is open, else in the month of today, which must be
  open too (409 `owner_period_closed`: a closed month is never posted).
- **F24** (`api/withholding/f24`, `…/f24/generate`): built from the applicable evidence of the month (and its reversals) of an **Italian** rule only (`IT` or
  `IT-…`: the evidence of a Swiss or any other jurisdiction is never an F24 or a CU, whatever its parameters say), with the tax code and the rule versions of
  that evidence; 422 `tax_no_obligations` without any or when the obligations net to nothing (a payout and its reversal in the same month: a record only
  generated is dropped, never a zero or negative F24), `f24_tax_code_mixed` with more codes. It runs under the lock of the owner accounting of the org, so
  two requests for one month make one record; due on the 16th of the next month (16 January for December). **Annual data** (`api/withholding/annual/{year}`):
  the Certificazione Unica data per owner. Both start `generated`, are regenerated only while generated (else 409 `tax_file_already_filed`) and move
  `reviewed` → `submitted` (only with the `handoffReference` of a controlled manual handoff) → `accepted` (with the `receiptReference`) or `rejected`
  (with the `reason`) through `…/filing-state` (`agency.owner.financial.manage`). Nothing is ever sent by CasaZen. `api/withholding/reconciliation/{year}`
  compares the obligations of each month with its F24 and the year with the annual data. The F24 PDF prints the filing state and the receipt.
- **Existing rows**: the F24 records created before get the state `generated`; the old `WithholdingLog` rows stay as history, nothing writes them.
