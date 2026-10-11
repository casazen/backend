---
id: —
slug: spec-ltr-agreement-ingestion
title: Territorial rent agreement ingestion pipeline (canone concordato, on demand per comune)
phase: 1.5
type: enabler
priority: P1
status: specced
issue:
depends_on: [spec-ltr-canone-concordato-calculator]
blocks: []
exit_contributes_to: LTR GA — the first property in a comune with no verified agreement data triggers a reviewed, versioned, source-traced load of that agreement, instead of a manual research task per comune
last_reviewed: 2026-10-11
---

# Spec — Territorial Rent Agreement Ingestion Pipeline

> Extends the official-data agent of branch `feat/agente-dati-comune` (MEF `nuova_at` tourist tax, ISTAT, Alloggiati) to a second source type: the **accordo territoriale** of the canone concordato (L. 431/1998 art. 2 c. 3, D.M. 16/01/2017). Nothing in this spec is implemented yet.

## Overview

Today the canone concordato data of a comune (zones, €/mq bands, coefficient rules, signatories, validity) exists only for Seveso and Cesano Maderno, entered by hand after a research task (RS-8). The other 53 comuni of the Monza e Brianza agreement are `Missing`, and every other province has nothing. The agreements are public PDFs (often scans) published by comuni, provinces or associations; there is **no national dataset** and **no API**.

The pipeline loads them **on demand**: when a property of **any rental mode** (short or long) is created in a comune the system does not know yet, or a concordato lease is started in a comune with no usable agreement, an asynchronous job finds the source document, extracts the data deterministically first and with the AI provider second, validates it, versions it with its source hash, and puts it in a **review queue**. Data become binding only after a human approves them. One agreement document covers many comuni, so one run loads them all (the Monza e Brianza PDF covers 55).

What "done" means for a human: a landlord with a house in a comune of an already-ingested agreement gets the calculator range at once; a landlord in a new comune sees "data being prepared", and an admin gets one review task that, once approved, serves every comune of that agreement.

**Phase:** 1.5 · **Type:** enabler · **Status:** specced

Design: none yet. ADRs: none. Reused from the branch: `OfficialSourceFetch`, `OfficialHostAllowlist`, `OfficialSourceDownloader`, the versioned-prompt + cache-key pattern, `IAiProvider` with the platform budget, Hangfire jobs with advisory locks, `Comune` keyed by ISTAT code.

---

## Principles (inherited from the branch, made explicit)

1. **Deterministic first, AI second, human last.** Regular tables are parsed with code; the AI extracts only the narrative rules; a person approves before publication.
2. **Never invent a value.** Missing field → `null` and a rejection or `unreadable` status, never a guess or a completed table.
3. **Source and hash on every value.** URL, retrieval time, SHA-256, page, extractor version.
4. **Public documents only.** The AI prompt never contains tenant, property or user data. Customer documents are out of scope (the active provider processes data outside the EEA, see `docs/runbooks/ai.md`).
5. **If the comune is unknown, find out.** The discriminant is the knowledge of the comune, not the rental mode: any property with a chosen comune whose data are missing starts the work. No full Italy scrape; refresh is monthly and by hash. What differs by mode is the **review priority**, not whether the system learns.
6. **Wrong data costs the customer his tax benefits.** Publication is never automatic; `Complete` needs a written confirmation (existing rule in `docs/runbooks/canone-concordato.md`).

---

## User Story

As a landlord (short or long rent) adding a house in a comune the product does not know, I want the comune data to be gathered for me, so that the rules that apply (tourist tax for a short rent, canone concordato range for a long rent) are available without waiting for someone to research them by hand.

As an admin, I want a review screen with the source page next to the extracted values, so that I can approve or correct an agreement in minutes and be sure of what the customers will see.

---

## Acceptance Criteria

### Backend — trigger and dedupe

- **AC1**: Creating or updating a property of **any rental mode** with a chosen `ComuneIstatCode`, or creating a lease with `ContractType = Concordato`, enqueues `AgreementIngestionJob.EnsureAsync(istatCode, priority)` when no usable agreement and no run exist for that code. It is called at the same place as the existing `QueueComuneOfficialProfile` (property create and update), so one property event starts every comune dataset that is missing: the MEF tourist-tax profile (existing) and the territorial agreement (this spec). Priority: `High` (a concordato lease waits), `Normal` (a long-rent property waits), `Low` (only short-rent properties wait: the agreement is extracted and validated, the human review waits for capacity or for a property of that comune to switch to long rent).
- **AC2**: The job is idempotent and safe with several instances (advisory lock per agreement source, as in `ComuneOfficialProfileService`). Two properties in two comuni of the same agreement produce **one** run.
- **AC3**: If the comune is covered by an agreement already `Published`, or `InReview`, no new run starts and the caller gets the existing status. A later event with a higher priority (a short-rent property switches to long rent, a concordato lease is started) raises the priority of the existing run; it never starts a second one.

### Backend — source discovery

- **AC4**: The job resolves the agreement source from, in order: (a) an admin-maintained `AgreementSourceHint` (scope: comune, ambito or province → URL); (b) a link found on the official page of the ente (comune, unione, province, città metropolitana, region) served by a **trusted host** (AC6b). It never takes a result of a free web search as a source; candidates found by search are only **suggested** to an admin (phase 2).
- **AC5**: No source found → status `SourceNotFound`, an `OfficialSourceFetch` row with the reason, an admin notification, and the user-facing state "data being prepared". Nothing is invented.
- **AC6b**: The trusted hosts come from a registry (`OfficialAuthorityHosts`: host, ente type, ISTAT or province scope, origin `Ipa` or `Admin`, verified-at), not from a host-name pattern. It is seeded from the official register of the public administrations (IPA open data; the field layout still has to be verified) and extended by an admin with a reason. Hosts of **associations** (signatories publish copies of the agreements) are never trusted by themselves: a document found only there is a `ThirdParty` candidate that an admin must confirm against the copy deposited with the comune. Every published agreement stores its `VerificationLevel` with the same legend as the tourist-tax rates (`Official`, `Deduced`, `ThirdParty`).
- **AC6**: The download goes through `OfficialSourceDownloader`: HTTPS only, allowlisted host, redirects re-checked against the allowlist, size cap, `%PDF-` signature check, SHA-256 stored. A document hosted on a third-party CMS (e.g. the Municipium storage that hosts the Seveso copy of the Monza e Brianza agreement) is accepted only when it is **linked from an allowed official page** (`LinkedFromUrl` stored); a bare third-party URL is refused with `host_not_allowed`.

### Backend — extraction and validation

- **AC7**: Text is extracted with the text layer (PdfPig, already referenced by the project). If the text layer is missing or too sparse (configurable ratio), the run ends `Unreadable` with reason `no_text_layer` until the OCR decision (Open Question 1) is made. It never sends page images to a provider that is not approved for it.
- **AC8**: Tables of €/mq per surface band and sub-fascia are parsed **by code** (layout "Allegato n/X — Comune di Y — Zona — Fino a 50 Mq / Da 51 a 74 / Da 75 a 99 / Oltre 100 — Sub-fascia 3/2/1 — Max/Min"). The AI provider extracts only the narrative rules: zones and their cadastral sheets, element lists A–D, sub-fascia thresholds, coefficients, duration uplifts, deposit and update rules, signatories, validity, attestation mode.
- **AC9**: The extract is JSON validated like `ComuneOfficialExtractValidator`: unknown fields ignored, missing required fields rejected, `legalValue` must be false, `sourceUrl` must equal the downloaded URL, `promptVersion` stored.
- **AC10**: Deterministic invariants (a violation → `Rejected` with the list, nothing published):
  1. every zone has the four surface bands, each with three sub-fasce, and `min < max` in every cell;
  2. sub-fasce of a band are contiguous and ascending (SF1 < SF2 < SF3) when the agreement states them so;
  3. every comune named in the agreement's scope has a table or is flagged `Missing`; the count of tables matches the count of annexes the agreement lists (Monza e Brianza: 55);
  4. percentages (furnished, air conditioning, durations, appurtenances) are within configurable sane limits;
  5. dates are consistent: `signed ≤ effective`, `deposit ≥ signed` when present, `expiresAt` derived only from the agreement's own text.
  Soft checks (warnings, not rejections): €/mq decreasing as surface grows; a value moving more than a configurable percentage against the previous version.
- **AC11**: Zone resolution is recorded per zone: `Single` (whole comune), `ByCadastralSheet` (sheet list), `ByMap` (planimetry annex). `ByMap` zones are published with `zone_requires_map` and the calculator asks the landlord to pick the zone; the product never guesses a zone from an address.

### Backend — review and publication

- **AC12**: A successful extraction creates `InReview` versions for **each comune** of the source; nothing reaches the calculator before approval. Approval publishes to the existing `TerritorialRentAgreement` / `ConcordatoRentBand` / signatory tables with `DataCompleteness = Partial` and fills `SourceUrl`, `LastVerifiedAt`, `VerificationSource`, `ExpiresAt`, `RemainsInForceUntilReplaced`.
- **AC13**: Moving to `Complete` stays a separate admin action that needs the written confirmation recorded outside the database (existing rule). Every approval, edit and rejection is written to `RegulatoryDataAuditEntry` with the admin id.
- **AC14**: Re-running the job with an unchanged SHA-256 writes `Unchanged` and creates no version. A changed hash creates a new `InReview` version and keeps the published one live until approved. Previous versions are never deleted.
- **AC15**: Publishing never overwrites a value an admin edited by hand: edits are kept as overrides and shown in the diff.

### Backend — refresh and watch

- **AC16**: A monthly job (like `ComuneOfficialProfileRefreshJob`) checks, **only for agreements that have at least one property or lease**, whether the source hash changed, whether `ExpiresAt` has passed without `RemainsInForceUntilReplaced`, and (quarterly) whether a newer agreement is linked from the same official page. Findings become admin tasks; no value changes by itself.
- **AC17**: When a published agreement is replaced or expires, leases already created on it keep their stored `concordatoAssessment` untouched; the lease page shows an advisory "agreement updated: check the attestation" (the attestation of the old agreement stays valid for the old contract, D.L. 73/2022).

### Admin UI

- **AC18**: The review screen shows, side by side, the source page (image or text of the page the value comes from) and the extracted table; per-comune approve / reject / edit; the diff against the previous version; the invariants that passed or failed; the prompt and extractor version.
- **AC19**: The screen lists the queue by priority, then by the number of long-rent properties and concordato leases waiting on each agreement, then by age, so the admin works the biggest first. `Low` runs are shown apart and can be filtered out.

### User-facing behavior

- **AC20**: For a comune with no published data the calculator and the concordato lease form show a state `agreement_data_pending` (copy: data being prepared, you will be notified) instead of the current dead end `concordato_range_unavailable`, and the landlord is notified when the data are published. The state never shows an estimate.
- **AC21**: A published `Partial` agreement keeps the existing "indicative range" warning with source and verification date.

### Cross-cutting

- **AC22**: The prompt, the response and the cache key contain no tenant, property, user or lease data. A test fails the build if the prompt builder takes anything but the agreement text, the comune identity (ISTAT code, name, province) and the source URLs.
- **AC23**: AI calls go through `IAiProvider` with the platform budget; a run that cannot be paid stops with `ai_budget_exhausted` and stays `Requested`.
- **AC24**: Every fetch and extraction writes an `OfficialSourceFetch` row (dataset `territorial_agreement`) with URL, authority, SHA-256, status; never a document body.

---

## Verifiable Outcomes

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + PG | A property of any rental mode in an unknown comune enqueues the job once, with the right priority (short-only → `Low`, long-rent → `Normal`, concordato lease → `High`); a comune already known enqueues nothing | Job skipped for short rent; queued twice on update; a second run started for a priority change |
| AC2 | L1 + PG | Two properties in Seveso and Meda → one run, one source row | Two runs; deadlock under two instances |
| AC4–AC6b | L1 | Hint URL on a trusted host downloads; a URL on a non-trusted host and an unlinked third-party storage URL → `host_not_allowed`; `comune.attacker.it` is **not** trusted; an association-only document → `ThirdParty` candidate, no automatic extraction run | A free web-search result accepted as source; a redirect to a non-trusted host followed; a host trusted because its name starts with `comune.` |
| AC7 | L1 | A PDF with no text layer → `Unreadable/no_text_layer`, no provider call | Image pages sent to the provider |
| AC8–AC10 | L1 golden | Monza e Brianza PDF fixture: the 55 comuni tables are extracted; **Seveso (24 values) and Cesano Maderno (48 values) equal the seed**; each invariant has a failing fixture | A comune missing from the scope accepted; `min ≥ max` published; an invented table for a comune with no annex |
| AC11 | L1 | Cesano Maderno zones carry sheet lists; Monza carries `ByMap` | Zone guessed from an address |
| AC12–AC15 | L1 + PG | Extract → `InReview`; calculator unchanged until approval; after approval the calculator returns the range with `Partial` warning; unchanged hash → no version; admin edit survives a re-run | Value live before approval; version deleted; override overwritten |
| AC16–AC17 | L1 + PG | Hash change → admin task, live data untouched; expired agreement → advisory only | Automatic overwrite; existing lease assessment changed |
| AC18–AC19 | L2 + L3 | Review screen shows source page, extracted table, diff, failed invariants; queue ordered by waiting properties | Table without a source page; approve button active with failed invariants |
| AC20 | L2 + L3 | Comune with no data → `agreement_data_pending` copy, no number; notification after publication | An estimate shown; the old 422 dead end |
| AC22 | L1 (architecture) | Reflection test on the prompt builder inputs | A property field added to the prompt |

---

## UX / UI Quality

| Criterion | Required | How to verify |
|---|---|---|
| Landlord never blocked silently | A pending comune shows status, what happens next and who notifies | L3: create a property in a missing comune, see the pending state |
| Admin decides fast | Approve an agreement with the source next to the values, no second tab | L3: approve a fixture agreement in ≤ 3 screens |
| Language | End-user strings Italian via `t()`, no `defaultValue` (`.claude/rules/code-style.md`) | L2: label assertions |
| No false precision | Pending and `Partial` states never show an exact "valid" claim | L2 |

---

## Export / Report Criteria

N/A for end users. Admin: CSV of the review queue (agreement, comuni count, properties waiting, status, age) — optional, phase 2.

---

## Technical Notes

### Data model (proposal)

Phase 1 keeps the existing one-row-per-comune `TerritorialRentAgreement` and adds the provenance; a full refactor to "agreement ↔ many comuni" is phase 2.

| Table | Purpose |
|---|---|
| `AgreementSourceDocuments` (new) | URL, `LinkedFromUrl`, authority, SHA-256, retrieved-at, page count, text-layer ratio, private storage key of the PDF (for the review screen) |
| `AgreementIngestionRuns` (new) | One run per source: status machine, extractor and prompt version, counts, reason, started/finished |
| `AgreementSourceHints` (new) | Admin-maintained scope (comune / ambito / province, by ISTAT code) → URL |
| `TerritorialRentAgreement` (change) | + `IstatCode` (6 digits, FK-like to `Comune`), + `SourceDocumentId`, + `ReviewStatus`, + `AttestationMode` (`Joint`, `Single`, `Unknown`), + `LocalContractReference` |
| `ConcordatoRentBand` (change) | + `ZoneResolution` (`Single`, `ByCadastralSheet`, `ByMap`) |

Match comuni by **ISTAT code**, never by the free-text city (home town names collide). `OfficialSourceDatasets` gets `territorial_agreement`.

### Source trust (finding on the current allowlist)

`OfficialHostAllowlist` (branch `feat/agente-dati-comune`) trusts any host that starts with `comune.` or contains `.comune.` and ends in `.it`. Anyone can register `attacker.it` and serve from `comune.attacker.it`, which passes. For the MEF and ISTAT sources the risk is small (fixed URLs), but this pipeline follows links and fetches documents whose **content decides what a customer is told about his taxes**, so it must not inherit the pattern. The same allowlist also does not cover provinces, città metropolitane or regions (`provincia.mb.it`, `regione.lombardia.it` are not `*.gov.it`), where many agreements are published. Fix for the new source type: the registry of AC6b. Fixing the pattern for the existing sources is a separate small change, to be agreed.

### Run status machine

`Requested → SourceFound | SourceNotFound → Extracted | Unreadable | Rejected → InReview → Published | Declined`. `DataCompleteness` (`Missing`, `Partial`, `Complete`) stays a property of the **published** data; the ingestion status is separate.

### Pipeline

1. **Trigger** (AC1–AC3): property create/update (any mode) or concordato lease → one orchestration call per ISTAT code → dataset jobs for what is missing (tourist tax: existing; agreement: this spec) → dedupe by source, priority by what waits.
2. **Discover** (AC4–AC6): hint → allowed official page link → download, hash, store the PDF in the private bucket.
3. **Extract** (AC7–AC9): text layer → code parser for tables → AI for narrative rules, prompt `agreement-2026-10-v1`, cache key `agreement:{sourceSha}:{promptVersion}`, `MaxActChars` raised for 70+ page documents or split by annex.
4. **Validate** (AC10–AC11): invariants, soft checks, diff vs previous.
5. **Review** (AC12, AC18–AC19): admin approves per comune.
6. **Publish** (AC12–AC15): write to the existing tables with `Partial` and the audit trail.
7. **Watch** (AC16–AC17): monthly delta.

### Configuration (`OfficialReferenceData` section)

`AgreementIngestion:Enabled` (default false until reviewed), allowed storage hosts with link-provenance rule, `MaxPdfBytes`, `MinTextLayerRatio`, invariant limits, `MaxPromptChars`, refresh cron, `ReviewSlaDays`.

### Calculator inputs this spec relies on, and one defect found while preparing it

An attestation issued by two signatory organizations for a real contract (Cesano Maderno, zone 1, 70 mq, sub-fascia 3, 4-year term, furnished, air conditioning) states the **maximum** as 110 €/mq × 70 mq × (1 + 15% + 3% + 5%) ÷ 12 = **789,25 €/month**, i.e. the coefficients are **summed**, which is the product's current reading. It states the **minimum** as 20 €/mq × 70 mq ÷ 12 = **116,67 €/month**, i.e. **without** the duration uplift, while `CanoneConcordatoEligibilityService` applies the duration uplift to the minimum too (120,17 €/month). Track as a separate calculator fix after confirmation with a signatory organization; use this case (anonymized) as a golden test of the calculator, not of this pipeline.

### Cost control

Extraction runs once per source hash and is cached; refresh is by hash; a 70-page agreement costs one extraction for all its comuni. The platform AI budget cap applies; a batch stops at the first call the budget cannot cover.

---

## Compliance

- Only official public publications; the product stores numeric values, structured rules and the **link** to the document, not a republished copy for end users. The PDF kept in the private bucket serves the reviewer only.
- No personal data in prompts, logs or `OfficialSourceFetch.Detail`.
- The provider currently configured processes data outside the EEA: acceptable for public documents only (see `docs/runbooks/ai.md`). Any future step that reads customer documents (attestations, visure) needs a different provider and its own spec.
- The product keeps saying "informativa, non consulenza": a published range is not an attestation.

---

## Dependencies

- Branch `feat/agente-dati-comune` / `feat/dati-ufficiali-comuni` (PR #495): `OfficialSourceFetch`, `OfficialHostAllowlist`, `OfficialSourceDownloader`, `ComuneOfficialProfile*`, jobs registration, ISTAT list. **This spec assumes they are merged first.**
- `spec-ltr-canone-concordato-calculator`: entities, admin page (`/app/admin/compliance/ltr-reference-data`), audit table.
- Private storage (FD-07) for the source PDFs.

---

## Test expectations (process contract)

- Unit: parser golden tests on the Monza e Brianza fixture (all 55 comuni), one failing fixture per invariant, validator, allowlist and redirect cases, prompt builder inputs.
- PostgreSQL: dedupe under two parallel runs, idempotent re-run, version history, publication writes the audit entry, an override survives a re-run.
- No test calls the network or a paid provider: recorded fixtures with their SHA-256.
- Frontend: review screen and pending state (L2) and one L3 path.

---

## Rollout

| Phase | Content | Exit |
|---|---|---|
| 0 | Prerequisites merged; Open Questions 1–3 answered; fixture of the Monza e Brianza agreement | — |
| 1 | Hints (manual) + code parser + AI narrative extraction + review queue, **Monza e Brianza only**, run on test | the 53 missing comuni published `Partial` after review; Seveso and Cesano equal the seed |
| 2 | Search-assisted candidate suggestions for new provinces; ISTAT-province coverage dashboard | a second province loaded in under a working day of review |
| 3 | Agreement ↔ many comuni refactor; zone resolution by map where an official GIS is published | — |

---

## Out of Scope

- Reading customer documents (attestations, visure, APE) with an AI provider.
- The list of high-tension-housing comuni (cedolare 10%, registro 70%): a separate national dataset (CIPE 13/11/2003 and D.L. 551/1988), loaded once, not per comune.
- Issuing or validating the attestation; the contract templates; registration and signature flows.
- Automatic publication without review; estimates for comuni without an agreement.

---

## Open Questions

1. **Scanned agreements.** Some agreements have no text layer. OCR in the container (an open-source engine) or an approved vision provider? Until decided, those runs end `Unreadable`.
2. **Who reviews, and the SLA** promised to a landlord in a new comune (suggested: ≤ 3 working days for a published `Partial`).
3. **Allowlist for third-party storage** (Municipium and similar): is the "linked from an official page" rule acceptable?
4. **Several agreements in one comune** (different associations): which one the calculator uses, or whether the landlord chooses.
5. **Order of provinces after Monza e Brianza**, by where the customers are.
6. **Minimum rent** in the calculator (duration uplift or not) and **coefficient combination** (sum, as the attestation above suggests): confirm in writing with a signatory organization before changing the code.
7. **Review capacity for `Low` runs.** Extract and validate agreements for short-rent-only comuni (the unknown-comune rule), or only discover and extract, leaving the review until a long-rent need appears? The spec assumes the first, with the review deprioritised.
8. **Official domain list.** IPA open data as the seed of the trusted-host registry: confirm the dataset and its field for the institutional website before building on it.
9. **Renewal of the Monza e Brianza agreement** (formal expiry 01/11/2025, in force until replaced): the pipeline watches it but nobody has verified whether a new one exists.
