# Fiscal documents (epics E06, E11, E12 and E17)

What CasaZen issues, how the numbers, the amounts and the FatturaPA XML are made, and where the official schema comes from. Requirements NF-83,
NF-84, NF-87, NF-112, NF-113, NF-116; ADR-019 (amended twice), ADR-033-R7 (retention).

## Boundary

- **CasaZen is the only issuer (decision of the PO of 2026-11-14, E11/F01/US01/T01).** CasaZen issues the document of every active booking of
  every source: direct, manual, local and the portals imported through Octorate (`IDocumentIssuerService`, flag `Features:FiscalDocumentIssuance`, off
  by default, on in test). A booking that is not active (a request, a hold, cancelled, a no-show: the one definition of the code) gets no document.
  Nobody issues outside CasaZen; the accountant only receives the exports. **In whose name** (E17/F01/US01/T02): in Italy the document of a stay is issued
  in the name of the owner of the property, on the owner's issuer profile, and the agency's own services to the guest are a document of the agency (see
  *Document plan of a booking*); in Switzerland the agency issues, unless it chose «emette il proprietario» for an owner (⚖ C1).
- **Retired modes.** `DocumentIssuerMode` keeps its numbers for the stored rows: `CasaZen = 2` (formerly `CasaZenDirectOnly`) is the only mode;
  `Octorate = 1` and `Accountant = 3` are `[Obsolete]`, the migration `AddSoleIssuerModel` moved every profile to 2 and `PUT api/fiscal/documents/settings`
  answers 422 `issuer_mode_retired` for them (the old name `CasaZenDirectOnly` is still read as `CasaZen`).
- **Octorate documents never lock, and are never issued twice.** The import of the documents Octorate issued stores them read-only and links them to
  their booking; it never locks the issuer of a booking and never raises `two_issuers_detected`. Its job selects no organization unless
  `FiscalDocuments:ImportOctorateDocuments` is `true` (default `false`), so it never runs by accident. CasaZen issues for every active booking **except one
  that already has a document issued elsewhere**: a booking with a document imported from Octorate is refused (409 `document_already_issued`, a proforma is
  no document), and so is a booking a legacy row locked to Octorate before the decision (409 `document_issuer_conflict`, reason `issuer_locked_octorate`):
  never two documents for one stay. No such document or lock exists in the test and production data (the import was never on there). The write-back of the
  billing data to Octorate is unchanged and inert: it runs only for a booking with such a lock.
- **Owner statements are not fiscal documents.** A rendiconto (owner statement) is a management report: it has no fiscal series or number, never
  draws from the counter of the fiscal numbers and never produces FatturaPA XML. The invoice of the agency to an owner, with lines, is a fiscal
  document of CasaZen again (purpose `OwnerInvoice`, epic E14): the model already has its owner, statement version, period and the unique index of one
  live invoice per owner and period.
- **No real SDI sending.** No intermediary is chosen (PO-06): an issued invoice has the state `to_send` and its XML can be downloaded; nothing is
  transmitted by CasaZen.

## Issuer (`OrgFiscalIssuerProfile`, E11/F01/US01/T01)

One profile per organization, saved by the wizard (`PUT api/fiscal/documents/settings`, the whole org, `payment.read` + `property.write`):

- **Country and regime.** `CountryCode` `IT` (default) or `CH` decides the valid regimes: `PrivatePerson`, `VatOrdinary`, `VatFlatRate` in Italy,
  `SwissVatRegistered`, `SwissNotRegistered` in Switzerland; a regime of the other country is 422 `issuer_regime_country_mismatch`. `VatRegistered`
  follows the regime (a contradicting value is 422 on `vatRegistered`).
- **Italy** keeps its checks: province, the FatturaPA regime code and the VAT number of the organization (VAT regimes) or its fiscal code (private person).
  **Switzerland** needs no Italian tax id: a registered issuer gives its UID, `SwissVatNumber`, accepted with or without separators or the suffix
  `MWST`/`TVA`/`IVA` and stored as `CHE-123.456.789`; the format and the check digit are verified (eCH-0097 v5.2.0 § 2.4.2: weights 5, 4, 3, 2, 7, 6,
  5, 4, modulus 11, a result of 10 is never assigned; example `CHE-109.322.551`). The Italian codes of a Swiss profile and the UID of an Italian one are
  dropped.
- **Bank accounts** `Iban` (mod 97, not a QR-IBAN) and `QrIban` (an IBAN of CH or LI whose institution id is 30000 to 31999), **encrypted at rest**
  (`Casazen.OrgFiscalIssuerProfile`). The answer of `GET api/fiscal/documents/settings` carries them in full only for the caller who may change the setting
  (the whole organization and `property.write`; so does the answer of the save): any other caller who reads it, a collaborator «Solo alcuni» or a role that
  cannot write, gets `ibanMasked` and `qrIbanMasked` (`CH93…2957`) and no part of the full numbers.
- **Treatments** (⚖, prudent configurable defaults): `StayVatTreatment` `Standard` (default) or `ExemptArt10` (I1); `CleaningFeeTreatment` `FollowsStay`
  (default, C2) or `Service`.
- **Series** `GuestDocumentSeries` (default: the year) and `OwnerInvoiceSeries` (default `PM{year}`): at most 10 characters, letters, digits, `. _ / -`
  and the placeholder `{year}`.
- **Issue and delivery** `AutoIssueGuestDocuments` and `EmailGuestDocuments` (both off); `PlatformCollectedTouristTaxChannels` (the `BookingSource`
  names whose portal collects the tourist tax, jsonb; empty for Italy).
- **Italian reminders** `StampDutyReference` (the authorization of the virtual stamp or the number of the physical stamp, ⚖ I4),
  `ConservationAgreementConfirmed`, `VirtualStampConfirmed`.
- **Draft or confirmed.** Every save needs the flag on (422 `document_issuance_not_available`). A profile that cannot issue yet is saved as a draft and
  `missingFields` says what is left; `confirmed: true` with something missing is 422 `issuer_profile_incomplete`, and `ConfirmedAt` is set only when
  nothing is missing.

## Issuer profiles of the owners (E17/F01/US01/T01, NF-116, D12)

In Italy the owner of a property is the issuer of the document of the stay; CasaZen issues it **in the owner's name**, on the owner's issuer profile, with the
owner's own series (the *sezionale*) and numbering. The profiles live in the same table as the organization's, `OrgFiscalIssuerProfiles`:

- **One per organization, one per owner.** `PropertyOwnerId` is `null` for the profile of the organization (partial unique index `UIX_OrgFiscalIssuerProfiles_OrgId`
  on `OrgId` where `PropertyOwnerId IS NULL`) and the owner for an owner's profile (partial unique index `UIX_OrgFiscalIssuerProfiles_PropertyOwnerId`). The
  profile of an owner goes with the owner (cascade) unless a document was issued on it (the documents' foreign key is restrict). **Every reader of «the profile
  of the org» goes through `OrgFiscalIssuerProfileQueries.OfOrganization(orgId)`** (the setting, the documents, the owner invoices, the statements, the portal
  accounts, the wire files, the demo): an owner's profile is never taken for the organization's.
- **What an owner's profile holds.** The legal name (name of a person, denomination of a company), its own Italian fiscal code (`FiscalCode`, 16 characters or 11
  digits, `ItalianFiscalCode`) and VAT number (`VatNumber`, the 11 digits, checked; dropped for a private person), the FatturaPA regime code (`RFxx`, the rule's when
  empty), the structured address, the regime (`PrivatePerson`, `VatOrdinary`, `VatFlatRate`; a Swiss owner `SwissVatRegistered`/`SwissNotRegistered` with its UID),
  `StayVatTreatment` (⚖ I1), its own series `GuestDocumentSeries` (default the year), `StampDutyReference` (⚖ I4), its own SDI code and PEC, and for a Swiss owner
  `StayIssuedByOwner` («emette il proprietario», ⚖ C1, off by default). The same validation as the organization's (`DocumentIssuerService.Normalize`): the
  data of the other country is dropped. The organization's profile keeps `FiscalCode`/`VatNumber` empty (its codes are on `Org`).
- **Complete or not.** A profile is saved as a draft and says what it misses (`missingFields`, `OwnerIssuerProfileRules.MissingFields`: the organization's rule with the
  owner's own fiscal code and VAT number); nothing is issued on it until it is complete (`canIssue`).
- **API.** `GET api/owners/{ownerId}/issuer-profile` (`agency.owner.view`, an owner in the caller's reach: the whole org, or an owner whose every ownership is on a
  property the caller reaches; anything else is 404 `owner_not_found`): the profile, or the empty one (`exists: false`), with `missingFields` and the rule in force.
  The personal data are in clear only for the whole organization with `agency.owner.financial.manage` (the read is logged with ids), masked (`••••501U`,
  `masked: true`) for anyone else. `PUT` the same route (`agency.owner.financial.manage` **and the whole organization**, 403 `fiscal_settings_need_whole_org`
  otherwise): the whole profile, made at the first save; 422 `issuer_field_invalid` (with `field` and `errors`), `issuer_regime_country_mismatch`,
  `document_issuance_not_available`; 404 `owner_not_found` (another org, an anonymized owner). Both are 404 while `Features:FiscalDocumentIssuance` is off.
- **Privacy.** The personal data of every profile (organization's and owners') are **encrypted at rest** with the purpose `Casazen.OrgFiscalIssuerProfile`: legal
  name, fiscal code, VAT number, street, house number, postal code, city, PEC (plus the IBAN and QR-IBAN as before). The columns are `text`; the values stored in
  clear before (the legal name and address of the organizations' profiles) are rewritten encrypted by `EncryptedColumns.EncryptLegacyPlaintextAsync` at the first
  start. The province, the country and the SDI code are no personal data and stay readable.
- **Billing setup.** The section `italy` gets the item `owner_issuer_missing` (reason `required`, with `count`): the owners with an ownership of an **Italian property**
  (`Property.Country` `IT`) not ended on the Rome day of today and **no profile that can issue** (none, one that misses a field, or a Swiss one), anonymized owners
  excluded (`OwnerIssuerSetupContributor`). It reads for the organization whatever its own country (a Swiss agency with Italian properties needs its owners'
  profiles too); without such owners it has nothing to look at (`IBillingSetupContributor.AppliesAsync`), so the section `italy` of a Swiss agency without Italian
  properties does not apply.

The migration of the slice is `AddOwnerIssuerProfiles` (see *Numbers* for what it moves).

## Billing setup (the wizard «Fatturazione», E11/F01/US01/T04)

Every invoicing setting is set in one place, the wizard «Fatturazione» of the onboarding guide, through one endpoint: `GET api/billing-setup` returns the
setup as **sections**, each with its `values` and its `missing` items; `PUT api/billing-setup/{section}` saves one section (`payment.read` to read, `property.write`
and a scope over the whole organization to save, 403 `fiscal_settings_need_whole_org` otherwise; both are 404 while `Features:FiscalDocumentIssuance` is off).
There is no migration: the sections are views over `OrgFiscalIssuerProfile` (`IDocumentIssuerService` is still the only store and the only validation: the field
codes of a refusal are the ones of the issuer setting, `issuer_field_invalid` with the `field` and the list of `errors`), and over the contributors of the other
epics.

| Section | Step | Fields saved by the PUT | Items (`key`, reason) |
|---|---|---|---|
| `issuer` | «Chi emette» | `countryCode`, `legalName`, `addressStreet`, `addressCivic`, `postalCode`, `city`, `province`, `swissVatNumber` | `country_missing` (nothing saved yet), `legal_name_missing`, `address_missing` (street, postal code, city and, in Italy, the province), `vat_number_missing` (Italy, a VAT regime), `fiscal_code_missing` (Italy, private person), `swiss_uid_missing` (a registered Swiss issuer): reason `required` |
| `vat` | «IVA» | `regime`, `vatRegistered`, `swissVatNumber`, `fatturaPaRegimeCode`, `stayVatTreatment`, `cleaningFeeTreatment` | `regime_missing`, `regime_code_missing` (Italy, a VAT regime without a code): `required`; `tax_rule_missing`: `unavailable` (no rule of `FiscalDocuments:TaxRules` is in force today for the regime: nothing is issued with a rate nobody approved) |
| `numbering` | «Numerazione» | `guestDocumentSeries`, `ownerInvoiceSeries` | `guest_series_unconfirmed`, `owner_series_unconfirmed`: `unconfirmed` (the default is shown in `defaultGuestDocumentSeries` and `defaultOwnerInvoiceSeries`; the step saves the series explicitly) |
| `touristTax` | «Tassa di soggiorno» | `platformCollectedTouristTaxChannels` | Switzerland only: `tourist_tax_channels_unconfirmed` |
| `channels`, `mandates` | «Commissioni dei canali», «Mandato standard» | none: saved by the endpoints of the owner ledger | added by E13/F01/US02/T03, for agencies only (plan Agency; any other organization gets no item): `channel_commissions_unconfirmed` (reason `unconfirmed`) until the organization saved its channel commission rules once, the defaults of the configuration alone do not count; `owners_without_mandate` (reason `required`, with `count`) while some owner with an ownership not ended has no mandate in force today (`OwnerMandateRules.InForce`) |
| `automation` | «Emissione» | `autoIssueGuestDocuments`, `emailGuestDocuments` | `auto_issue_unconfirmed`: `unconfirmed` |
| `italy` | «Italia» | `stampDutyReference`, `conservationAgreementConfirmed`, `virtualStampConfirmed` | Italy only: `conservation_agreement_unconfirmed`, `stamp_duty_unconfirmed` (neither the stamp reference nor the confirmation of the virtual stamp); `owner_issuer_missing` (reason `required`, with `count`, E17/F01/US01/T01): owners of Italian properties without an issuer profile that can issue, for any organization that has them |

- **Draft and idempotent.** A partial body is a draft: a field that is absent or `null` keeps its value, a text sent empty clears it, a list (even an empty one)
  is sent; the same answers saved twice change nothing and a body with no field saves nothing. A field of another section is 422
  `billing_setup_field_not_in_section` (the one exception is the Swiss UID, a tax id and the VAT registration at once: `issuer` and `vat` both take it); an unknown section is 404
  `billing_setup_section_unknown`, `channels` and `mandates` are 422 `billing_setup_section_not_writable`. A refused save changes nothing. The save is the
  setting as it is now with the fields of the body laid over it, saved whole by `SaveSettingsAsync`: the bank accounts and the other sections go back as they were.
- **A regime follows the country.** Changing the country drops what only made sense in the other one: its regime (the step «IVA» asks again), the postal code and
  the province (asked again, unless the same save carries them; the street and the city stay). The VAT registration tells the
  regime where only one fits: `vatRegistered` alone gives `SwissVatRegistered`/`SwissNotRegistered` to a Swiss issuer and `PrivatePerson` to an Italian one that is
  not registered; an Italian issuer that is registered chooses between `VatOrdinary` and `VatFlatRate`, and a `vatRegistered` that contradicts the regime is 422 on
  its field.
- **The Italian VAT number and fiscal code live on the organization.** They are read (`vatNumber`, `fiscalCode` in the values of `issuer`) and saved in its tax
  profile (`PUT api/fiscal/tax-profile`), never copied into the profile of the issuer, so the items `vat_number_missing` and `fiscal_code_missing` are cleared
  there.
- **«Confirmed» without a new column.** A choice that has a default cannot be told from «never made» by its value, so: the **series** count as confirmed when
  they are saved (a `null` series is the default, not yet confirmed); the **tourist tax** channels are confirmed when the column
  `PlatformCollectedTouristTaxChannels` is not `null`, and an empty list is stored as `[]` («no portal collects it», `PlatformCollectedTouristTaxChannelsConfirmed`),
  so no other save turns an unconfirmed choice into a confirmed one and none loses a confirmed `[]`; the **emission** is confirmed by saving the step while the
  profile can issue (`ConfirmedAt`, which the issuer service clears whenever the profile stops being complete: the item comes back and the step is saved again).
  A save of `automation` while the profile cannot issue yet is kept as a draft.
- **A section that does not apply** (`applies: false`: the Swiss tourist tax of an Italian issuer, the Italian reminders of a Swiss one, `channels` and
  `mandates` until E13 registers its contributors) has no item and is not counted.

### The guide and the flow

- **Part «Fatturazione»** (`OnboardingPartKey.FiscalDocuments`, key unchanged; label IT «Fatturazione», EN «Invoicing», `OnboardingPartFiscalDocuments`) is
  `FiscalDocumentsPartEvaluator`: `PartFacts.Counted(done, total, reason)` over the sections that apply (`total`) and have nothing missing (`done`); it is done
  only when no item is missing and its `reason` is the key of the first missing item, in the order of the wizard, so the guide reopens the wizard at that step.
  With `Features:FiscalDocumentIssuance` off the part is to do (`issuer_missing`): nobody issues.
- **Flow «Documents»** (`DocumentsFlowProvider`): the prerequisite `issuer` holds when the sections `issuer` and `vat` have no missing item and the issuance is on.
  The other sections never stop the flow. `accountant_handles` is never produced again (the constant stays for the old rows).

### Adding items (other epics)

An epic that owns a piece of the setup implements `IBillingSetupContributor` (`Section`, `EvaluateAsync(orgId, ct)`: **only the missing items**, a read for that
organization, codes and counts only, no personal data) and registers it in `AddCasazenFiscalDocuments`, next to the six of the setup; it never touches
`BillingSetupService`. A section may have several contributors (the items follow the order of registration); an item is attributed to the section of its
contributor; a section with at least one contributor that applies is counted by the part. `BillingSetupItem.Count` carries a number when the item counts
something. Keys are `snake_case` and never renamed: the client maps every key to its wizard step and fails a test on an unknown one.

## The issuer as it was (`FiscalDocument.IssuerSnapshot`)

At the issue the document keeps the issuer as JSON (`FiscalIssuerSnapshot`: legal name, structured address, Italian VAT number and fiscal code of the
organization, Swiss VAT number, regime, country, FatturaPA regime code, IBAN masked), **encrypted at rest** like the recipient, and the version of its
rule (`RuleVersion`). The PDF and the XML of an issued document read the issuer from the snapshot; only a draft, and a document issued before the
snapshot existed, show the profile of today. The document also keeps its `Purpose` (`GuestStay`, `OwnerInvoice`, `SelfBilling`), its property, the
period of the stay (`ServicePeriodFrom`/`To`, from the check-in and the check-out) and its `PaymentChannel` (`Unknown` until E12 fills it). Its lines
carry a `LineType` (`Accommodation`, `Cleaning`, `ExtraService`, `TouristTax`, `Commission`, `Recharge`, `AdvancedExpense`, `Other`); the stored lines
were migrated (the tourist tax line, recognized by its fixed description, became `TouristTax`; the others `Accommodation`).

**A draft is issued only with the rule it was made with.** The draft keeps the `RuleVersion` and the VAT of each line it was made with; if the regime, the
country or a treatment of the issuer changed since (or a new version of the catalog started), the amounts the host confirmed are the old rule's while the XML,
the snapshot and the layout would be the new one's: the issue answers 409 `document_draft_outdated` and nothing is numbered. Refreshing the draft
(`POST api/fiscal/documents/{id}/refresh`) makes it again with the rule of today. A draft without a version (made before the versions existed) is not checked.

## Official schema (precondition of the XML)

The XML is validated against the official FatturaPA schema before it is stored (ADR-019-R6). The schema files are **not** written or rebuilt by
hand: they are the files their publishers give, committed as they are under `Casazen.Infrastructure/Fiscal/Schemas/` and embedded
in the assembly (`Casazen.Infrastructure.csproj`, logical names `Casazen.Infrastructure.Fiscal.Schemas.<file>`; `FatturaPaSchemaProvider.OfficialFiles`).

| File | Version | Source (taken on 2026-11-14) | SHA-256 |
|---|---|---|---|
| `Schema_VFPR12_v1.2.3.xsd`: the FatturaPA schema of the ordinary invoice, format FPR12 (namespace `http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2`) | 1.2.3, the documentation valid from 2025-04-01 (Specifiche tecniche del formato FatturaPA 1.4) | Agenzia delle Entrate, fatturapa.gov.it, page «Formato FatturaPA» (`https://www.fatturapa.gov.it/it/norme-e-regole/documentazione-fattura-elettronica/formato-fatturapa/`), file `https://www.fatturapa.gov.it/export/documenti/fatturapa/v1.4/Schema_VFPR12_v1.2.3.xsd` | `152944f6eef9f5d69ef6e955ee173b32142b00a8c1c5222fc97dfab5910e8a8c` (68,619 bytes) |
| `xmldsig-core-schema.xsd`: the XML signature schema the FatturaPA schema imports (its `schemaLocation`) | W3C Recommendation of 2002-02-12 | W3C, `https://www.w3.org/TR/2002/REC-xmldsig-core-20020212/xmldsig-core-schema.xsd` | `35cf8197da812c85e40d57891b35c94187569ed474a2dac813ce5090dafcd35c` (10,293 bytes) |

**State of this build: the files are in the repository** (E17/F01/US01/T03). `.gitattributes` marks `Casazen.Infrastructure/Fiscal/Schemas/*.xsd` as `-text`, so
the bytes, and the checksums, are the same on every machine; `FatturaPaSchemaProvider_EmbeddedSchemas_AreTheOfficialFiles_Test` fails when an embedded file is
not the one recorded here. The Italian invoices and their credit notes are now issued with their XML; 422 `fatturapa_schema_unavailable` remains only for a build
without the files. Every test of the builder validates what it writes against the real schema (`FatturaPaProofTests`).

**Updating the schema (operator):**

1. Download the new files from the publication of the Agenzia delle Entrate (the page above) and the signature schema from the address its `schemaLocation`
   names, never from a mirror.
2. Put them in `Casazen.Infrastructure/Fiscal/Schemas/` unchanged (no reformatting, no change of line endings), removing the old version.
3. Record in the table above and in `FatturaPaSchemaProvider.OfficialFiles` the exact URL of each file, the version and the SHA-256 (`sha256sum <file>`).
4. Run `FatturaPaProofTests`: every document of the builder is validated against the new files; if one does not validate, the builder is what changes.

The schemas are compiled without a resolver (the import of the signature schema is satisfied by the embedded file, never fetched; the internal DTD of the W3C
file is parsed, its external one never read); the documents are read with the DTD prohibited and no resolver (`XsdValidator`): no external entity, no network.

## Tax rules (`FiscalDocuments:TaxRules`, E11/F01/US01/T02)

The rules of the documents are one effective-dated catalog in `appsettings.json` (`FiscalTaxRulesOptions`, D2), which replaced the rules per regime
(`FiscalDocuments:Rules`) and the stamp duty of the section. A rule set has a `Jurisdiction` (`IT` or `CH`), a `Regime` of that country, `ValidFrom` and
an optional `ValidTo` (both days included), a `Version` (stored on every document, `FiscalDocument.RuleVersion`), a `SourceUrl`, a `DocumentKind`
(`Receipt` or `Invoice`), a VAT rate for **every** line type (`Rates`: `Percent` and, for an Italian 0%, its `Nature`), and in Italy the codes of
FatturaPA (`FatturaPaRegimeCode`, `TD01`/`TD04`), the `StampDuty` (`Threshold`, `Amount`, `ChargedToRecipient`) and the nature of an exempt stay
(`StayExemptNature`).

- **Which rule.** `IFiscalTaxRuleResolver` picks the set of the country and the regime of the issuer whose range holds the date: the **check-out** of
  the stay for a document of a guest (a stay that ended before a new version keeps the old rate), the last day of the period for an invoice to an
  owner (E14). Then the choices of the issuer: `ExemptArt10` gives the stay the exempt nature (`N4`, Italian ordinary only), and the cleaning that
  `FollowsStay` takes the VAT of the stay (the rate of `Cleaning` is the one of the cleaning as a service). No rule = 422 `document_rule_not_configured`.
- **Checked at startup** (`ValidateOnStart`): overlapping ranges of one country and regime, a line type without a rate, an Italian 0% without a
  nature, a nature with a rate above 0, a Swiss rule with a nature, a stamp duty or an exempt nature, a receipt with VAT, a repeated version, a kind
  other than receipt or invoice: the application does not start and the message names the rule set (`FiscalDocuments:TaxRules:{index} ({version} ...)`).
- **Adding a version** (never edit a published one: the documents keep its name): close the old set with `ValidTo` the day before, add a new set from
  the first day with a new `Version` and its `SourceUrl`, ship it with the deploy. An empty catalog starts and nothing is issued.
- **Shipped defaults (⚖, prudent and configurable, to be confirmed by an accountant):**

  | Version | Country, regime | Document | Rates |
  |---|---|---|---|
  | `ch-vat-2024.1` | CH `SwissVatRegistered`, from 2024-01-01 (F1) | Invoice | stay 3.8; cleaning 3.8 with the stay (C2) or 8.1 as a service; extras, commission, recharges, other 8.1; tourist tax and advanced expenses 0, outside the VAT |
  | `ch-novat-2024.1` | CH `SwissNotRegistered`, from 2024-01-01 | Receipt | every rate 0, no VAT shown |
  | `it-private-2026.1` | IT `PrivatePerson`, from 2026-01-01 | Receipt | 0 `N2.2` (internal only), tourist tax and advanced expenses 0 `N1`; stamp duty 77.47 / 2.00 (I4) |
  | `it-ordinary-2026.1` | IT `VatOrdinary`, from 2026-01-01, `RF01` | Invoice | stay 10, or 0 `N4` when exempt (I1); cleaning with the stay or 22; extras 22 (I2); commission, recharges, other 22; tourist tax 0 `N1` (I3); advanced expenses 0 `N1` (art. 15); stamp duty 77.47 / 2.00 |
  | `it-flatrate-2026.1` | IT `VatFlatRate`, from 2026-01-01, `RF19` | Invoice | 0 `N2.2`; tourist tax and advanced expenses 0 `N1`; stamp duty 77.47 / 2.00 (I5) |

  Choices of this delivery beyond the brief, to confirm with the rest of the table: the line type `Other` takes the normal rate (8.1 in CH, 22 in IT
  ordinary); the advanced expenses are `N1` (art. 15) in every Italian regime; the Italian ordinary rule keeps the stamp duty the section had for every
  regime (due only above 77.47 of amounts without VAT: an exempt stay, a large tourist tax). The Swiss source URL is the page of the Federal Tax
  Administration on the VAT rates (F1: to confirm on estv.admin.ch).
- **Recipient.** A Swiss document (any kind) and an Italian receipt need only the name of the recipient (the address is printed when there is one, an
  Italian fiscal code is never asked); an Italian invoice keeps the checks of FatturaPA (the fiscal code of a person or the country abroad; the VAT
  number of a company with its SDI code or PEC).
- **Swiss documents** have no nature code, no stamp duty, no XML (`FatturaPaXml` null) and the SDI state `not_applicable`; their tourist tax line reads
  «Tassa di soggiorno (non soggetta all'IVA)».
- **The preview** `GET api/fiscal/documents/issuer-preview` (without `bookingId`: the organization) and `GET api/fiscal/documents/settings` return the rule
  in force today for the profile (`rule`: version, dates, source, kind, the rate of every line type, the stamp duty): the summary of the wizard.

## Swiss document (`SwissFiscalDocumentPdf`, MWSTG art. 26, E11/F01/US01/T03)

`FiscalDocumentPdfs` lays out the document of an issuer whose snapshot is in Switzerland (`CountryCode` `CH`) with the Swiss layout (MigraDoc through
`IPdfDocumentRenderer`, embedded DejaVu fonts), in the language asked for (the guest's; Italian by default, the endpoint passes the language of the
request). It carries the data of art. 26:

- **Issuer**: legal name, address and, for a registered issuer, «N. IVA CHE-123.456.789 IVA» (IT) / «VAT no. CHE-123.456.789 VAT» (EN), from the
  snapshot (the issuer as it was at the issue).
- **Recipient**: the name, plus the address when there is one (never an Italian fiscal code or VAT number).
- **Title** «Fattura» / «Ricevuta» (IT), «Invoice» / «Receipt» (EN) with the number; the date of the document; the period of the stay
  (`ServicePeriodFrom`/`To`) and the property.
- **Lines**: description, quantity, unit price, amount and VAT rate (`3.8%`, `8.1%`; `—` for a line outside the VAT); the tourist tax is always labelled
  «Tassa di soggiorno (non soggetta all'IVA)» / «Tourist tax (not subject to VAT)».
- **VAT per rate**: base, rate and VAT, the stored VAT of the lines of each rate; then the taxable total, the VAT and the total in CHF, written with the
  it-CH culture and its apostrophe pinned (`1'234.50`).
- **Payment**: «Pagata tramite il portale» when the portal collected (`PaymentChannel` `Portal`), «Pagata» when the money is collected, «Da incassare»
  otherwise; never the name of a portal or of a supplier.
- **Not registered**: a receipt without the VAT column, without the summary and with the note «Non soggetto all'IVA» (⚖ wording, the resource
  `FiscalDocPdfChNotVatRegistered`).

A Swiss document never shows the Italian courtesy copy, nature codes, stamp duty or SDI wording. The labels are the resources `FiscalDocPdf*` (the
Italian layout's) and `FiscalDocPdfCh*`, in Italian and English. The Italian layout shows no nature code on a receipt (they stay internal).

## Amounts (`FiscalDocumentTotals`, computation `fiscal-totals-2026.11-1`)

- The lines come from what the guest paid for the booking, **VAT included**, each with its type and the VAT the rule gives it: lodging
  (`BasePrice - CleaningFee`, `Accommodation`), cleaning (`CleaningFee`, `Cleaning`) and the tourist tax (`TouristTax`, outside the VAT).
- Per VAT rate (and nature code): taxable `T = round(G × 100 / (100 + rate), 2)` half away from zero, VAT `G − T`: the total of the document is what
  the guest paid. The net of each line is computed the same way; a cent of difference goes to the largest line of the group. The VAT of each line
  (`FiscalDocumentLine.VatAmount`, its gross less its net) is stored: the summaries of the PDF and of the XML add up the stored VAT of the lines of
  each rate (`FiscalDocumentTotals.SummaryOf`), never `taxable × rate` again, so a document with several rates adds up to its total in the XML too.
- Stamp duty: only under a rule that has one (Italy, never a CHF document): due when the amounts without VAT exceed its `Threshold` (77.47), of its
  `Amount` (2.00); added to the total only with `ChargedToRecipient` (off: the issuer bears it).
- Credit notes: the amount (the whole document by default) is spread over the lines of the credited document in proportion; never more than the
  document minus what was already credited. The cap (and «one document per booking» for an invoice or a receipt) is checked once before the transaction
  (a quick refusal) and **again inside it, right after the number is reserved**, under the lock of the series (a credit note takes the series of its
  original, so the same lock): two requests at the same moment never both pass (`FiscalDocumentCreditPostgresTests`).
- A document is issued only for an **active** booking (the one definition of the code, `AlloggiatiStatusRules`: a status that owes the police communication
  and no no-show): a request, a hold, a cancelled booking or a no-show gets no draft, no number and no lock of the issuer (422 `booking_not_active`). A
  credit note of an issued document is not refused for the state of its booking (it is what is done after a cancellation).
- The payment state (`Paid` / `ToCollect`) of a document CasaZen issued is the **current** one, computed when it is read (the completed and partially
  refunded payments of the booking, net of the refunds, against the total; for a stay of a portal the host collects from, what the channel manager reports paid) by the
  document API, the export and the payment ledger; the value stored with the draft is only a starting point. A document Octorate issued keeps the state Octorate gave it.
- The series: the pattern of the profile (`GuestDocumentSeries`, `{year}` is the year of the issue), the year when it has none.

## Righe del documento e tassa di soggiorno (`GuestDocumentLineBuilder`, E12/F01/US01/T01)

The one place that says what a guest is billed for, whatever the source of the booking (`IGuestDocumentLineBuilder`, used by the draft and by the refresh of a
draft). The amounts are gross (VAT included), in the currency of the property; the VAT of each line is the one of the tax rule (see *Tax rules*).

| Line | Amount | Notes |
|---|---|---|
| `Accommodation` | `BasePrice - CleaningFee` | `BasePrice` is «nightly rate × nights + cleaning» for a booking made in CasaZen (direct, manual). For a booking of a **portal** it is the room price Octorate reports (`roomGross`, the price of the host); the import never writes `CleaningFee`, so the whole price is the stay. The service fee the portal charges the guest is never in it. The cleaning is clamped to the price and the two add up to `BasePrice`: it is never billed twice |
| `Cleaning` | `CleaningFee` when > 0 | follows the stay or is a service, as the profile says (`CleaningFeeTreatment`) |
| `ExtraService` | — | the model has no extras of a booking (no add-on table), so no such line is made yet |
| `TouristTax` | see below | out of VAT: `N1` in Italy, no nature in Switzerland |

**Tourist tax, Italy.** The amount of the stay's confirmed `StayTouristTax` (per guest, with the exemptions the host confirmed), else `Booking.TouristTax`, **only when the host
or the agency collects it** (`TouristTaxCollectorRules`: a booking that is not of a channel, the active `TouristTaxChannelRule` of the comune and the channel, then the
host's declaration). If the portal collects it there is no line (never a double charge to the guest); a channel stay whose collector nobody has declared yet has none either.

**Tourist tax, Switzerland.** No line for a channel in the channels the profile says collect it (`PlatformCollectedTouristTaxChannels` once confirmed, else the shipped default, below); else `Booking.TouristTax` when it is > 0; else the canton rule of
`SwissTouristTax:Rules` (the canton comes from `SwissMunicipalities` through `Property.SwissMunicipalityCode`): `guests × nights × rate`, each night at the rate in force on
that night. Shipped: Ticino, CHF 2.00 per person and night from 2025-01-01, no age exemption (⚖ C4: all the guests of `Booking.NumberOfGuests`), source
`https://www.ticino.ch/en/hospitality/opportunita/tasse/alloggio.html`. A canton without a rule gives no line. `SwissTouristTax:PlatformCollected` (⚖ C5, `Airbnb`) is the default
list of a Swiss issuer **that has not confirmed its own yet**: `IGuestDocumentLineBuilder.PlatformCollectedChannelsOf` reads it when the document is made and never copies it into
the profile (the step «Tassa di soggiorno» of the billing setup stays open until the issuer confirms its list). The issuer's confirmed list, an empty one included («no portal collects it»), is the one the documents follow from then on; an Italian issuer never has the default.
Example: 2 guests × 3 nights × CHF 2.00 = CHF 12.00, out of VAT.

**Who collected the money** (`FiscalDocument.PaymentChannel`, written with the draft):

| Channel | When | Payment state |
|---|---|---|
| `Portal` | a booking of a portal that collects: `Booking.OtaCompanyCollect` (Octorate `companyCollect`), else `OtaCommissionIncludedInPayments` (the portal catalog), else the portal is in `FiscalDocuments:PortalCollectedChannels` (⚖: Airbnb, Booking.com «Payments by Booking», Expedia Collect, Vrbo) | **Paid** (stored: CasaZen sees none of its payments) |
| `CasaZen` | any other booking with a payment that went through CasaZen (Stripe) | the current one, from the payments (`Paid` / `ToCollect`) |
| `Outside` | the agency's own site, pay at the property, manual bookings, a portal the host collects from | «to collect» until the host records a manual payment (`ManualPaymentService`). **A stay of a portal** has no payment of CasaZen (`ManualPaymentService` refuses it, no payment link is made for it): its collected amount is what the channel manager reports the guest paid (`Booking.OtaPaidAmount`, the figure of the payment ledger), so the document is `Paid` once that reaches the total and `ToCollect` before (and while the channel manager says nothing) |

**When the booking changes.** `IFiscalDocumentService.ApplyBookingChangeAsync` follows a change of the booking: a **draft** is made again from it (or discarded when the booking is no
longer active and nothing was collected for it; kept with a «To do» when money was collected or when it cannot be made again); an **issued** document is **never** rewritten: it is corrected
by a credit note (see *Corrections when a booking changes*), and when the correction cannot be prepared (the issuer's profile is incomplete, no tax rule applies, a portal collected the money)
the host gets a «To do» (`ReviewFiscalDocument`, reason `fiscal_document_booking_changed`, about the document and its property, no personal data) that a credit note may be needed.

## Document plan of a booking (`IDocumentPlanService`, E17/F01/US01/T02, NF-116, D12)

`IDocumentPlanService.PlanAsync(bookingId)` says which documents a booking needs and who issues each; every draft CasaZen makes goes through it (the manual draft
`POST api/fiscal/documents/drafts`, the check-out event, the daily run `fiscal-documents-due`, the corrections of a change). The day of the stay is its **check-out**
(the day of the tax rule and of the owner ledger).

| Booking | Documents |
|---|---|
| **Italian property** (`Property.Country` `IT`) with ownerships in force on the day | **The owner issues the stay**, on its issuer profile: a private owner's rule gives a **receipt** (the stamp duty of the rule, 2.00 above 77.47 of amounts without VAT, borne by the issuer, ⚖ I4), a company's a **FatturaPA invoice** in its own sezionale, with the VAT of its `StayVatTreatment` (⚖ I1) and the agency as third-party issuer (TZ, T03). Several co-owners: **one** document, issued on the profile of the co-owner whose mandate is in force on the day (several, or none: the largest share, then the oldest ownership, then the smallest id), listing every co-owner with its share (⚖ I6). |
| the same, with the agency's services | **A second document of the organization** (`ForAgencyServices`): the cleaning when the organization's `CleaningFeeTreatment` is `Service`, and the extras (the model has no extras of a booking yet), with the agency's VAT. The owner's document then leaves them out. A cleaning that follows the stay stays in the owner's document. |
| an owner **without an issuer profile that can issue** (none, incomplete, or a Swiss one: the stay of an Italian property follows the Italian rules) | **No document at all**: 422 `owner_issuer_missing`, and the «Da fare» of the owner (`ReviewFiscalDocument`, entity `PropertyOwner`, reason `owner_issuer_missing`, about the property; one open item per owner). The daily run skips the stay until the profile is complete. |
| **Swiss property** (`CH`) | The organization issues everything, as E11/E12 do, **unless** the owner has a Swiss issuer profile on which the agency chose «emette il proprietario» (`StayIssuedByOwner`, ⚖ C1): then the owner issues. |
| a property without owners in force, a property of another country | The organization issues one document with everything, as before. |

- **Never two documents that bill the same part of a stay.** A booking may now have several documents (the owner's stay, the agency's services), but never two live
  documents that bill the same part: an owner's document bills the **stay**, the agency's document of its services the **services**, the organization's own
  document (and one imported from Octorate, or without an issuer) **both**. The draft and the issue ask again with the part of the document (409
  `document_already_issued`), whoever issued the live one: a stay the agency billed in full before its owner was recorded gets no document of the owner until the
  agency's is credited in full. `BookingDocumentIssuers` has **one row per document issued** (unique on `LockedByDocumentId`, the booking only indexed); a legacy
  row locked to Octorate still keeps CasaZen off the booking (409 `document_issuer_conflict`).
- **Drafts follow the plan.** The draft endpoint makes (or makes again) every document of the plan and answers with the document of the stay; a draft the plan has no
  place for any more (the agency's services once the cleaning follows the stay again) is discarded; a document that is issued is left alone. The check-out event
  and the daily run make the missing drafts of a booking that has none, and with the automatic issue they issue them in the order of the plan (the stay first).
- **Issuer of each step.** A draft is issued (one document, the bulk issue, the daily run) only while it is the document the plan wants: an owner whose profile
  cannot issue gets its «Da fare» (422 `owner_issuer_missing`), a draft of another issuer or with a line its part does not bill (made before the owner, the
  ownership or the mandate of today, or before this change and moved to the organization by the migration) is 409 `document_draft_outdated`; refreshed, it
  follows the plan. The credit notes (and the reversal receipts) and the corrections use the profile the document was made on: an owner's credit note is the
  owner's, in the owner's series. The issuer snapshot of an owner's document keeps the owner's own fiscal code and VAT number, the agency as `intermediary`, the
  co-owners and the stamp reference.
- **PDF.** An owner's document shows the owner as issuer, the co-owners with their shares (`Comproprietari: … (50%)`) and «Emessa tramite <agenzia> per conto del
  proprietario» (EN «Issued through … on behalf of the owner»); a document with a stamp duty says how it is paid (⚖ I4): the stamp reference of its issuer
  («Imposta di bollo assolta: …»), «Imposta di bollo assolta in modo virtuale» when the issuer confirmed the virtual stamp without a reference
  (`virtualStampConfirmed` in the snapshot), else «Imposta di bollo da assolvere»; a snapshot written before this change prints nothing, so an old document stays
  as it was issued. Resources `FiscalDocPdfIssuedOnBehalf`, `FiscalDocPdfCoOwners`, `FiscalDocPdfStampDutyReference`, `FiscalDocPdfStampDutyVirtual`,
  `FiscalDocPdfStampDutyToPay`, Italian and English.

## Drafts and bulk issue (E12/F01/US01/T02)

**One draft for every stay that ended, never two.** A booking has a *live document* when it has one that is not discarded, not a credit note, not a proforma and **not credited in
full** (whoever issued it: a document imported from Octorate counts). `IFiscalDocumentService.EnsureDraftAsync` makes the draft only for a booking without a live document, with the check
and the draft under the advisory lock of the booking (`FiscalDocumentBooking`, 1_604), so the check-out event, the daily run and the host never make two. A document credited in full is
over: the booking may have its next one (the new draft of a date change).

- **The check-out event.** `FiscalDocumentCheckOutHandler` consumes `CheckOutCompleted` (`DomainEventType` 7, run by the outbox dispatcher, so it can never fail the check-out) and makes the
  draft. A refusal of the rules (invoice data of the guest missing, no tax rule, the profile incomplete) is an answer: logged with its code and ids, the event is not delivered again and
  the daily run makes the draft once the data is there. Any other failure is logged with the type of the error and thrown again, so the outbox delivers the event again. Nothing while
  `Features:FiscalDocumentIssuance` is off.
- **The daily run `fiscal-documents-due`** (`FiscalDocumentsDueJob`, 05:10 Europe/Rome, registered only while the flag is on, `[DisableConcurrentExecution]`). It exists because a booking of a
  portal rarely has a check-out recorded in CasaZen. For each organization with an issuer profile, in a DI scope of its own under its tenant (the tenant scope is once per scope), it looks at
  the **active** bookings (`AlloggiatiStatusRules`: no request, cancelled or no-show) whose check-out day is **before today in Rome** and not older than `FiscalDocuments:DueLookbackDays`
  (31: a channel import brings years of past stays, and the first run after the issuance is switched on must never draft or issue the history; an older stay is drafted by hand or with the
  bulk issue), the most recent first, **at most 500 a run per organization**, the rest the next day. Nothing runs with the flag off or while the profile misses an item the documents need
  (`MissingFields`) or the billing setup misses an item in the sections `issuer` and `vat` (`IBillingSetupService.GetProgressAsync`, the same prerequisite as the flow «Documents»; the
  other sections, a series or the automatic issue not confirmed yet, never hold back a draft); one session advisory lock per organization (`FiscalDocumentsDueRun`, 1_602) keeps a retry or a manual trigger from running over it. With `AutoIssueGuestDocuments` on
  the draft (the one the event made too) is issued through the same path as the API (the whole point of the profile setting); with it off the drafts wait for the host. A stay whose
  data is missing is skipped and counted (`skipped`), read again at the next run: never a failure, never a stop of the others. An organization that fails does not stop the others. Idempotent:
  the second run finds nothing to do. The log carries ids and counts. **A draft made earlier is made again right before the automatic issue** (under the lock of the booking): the tourist
  tax confirmed, the collector declared or the data of the invoice changed since the check-out are no change of the booking that the corrections follow, and nobody checks a document
  issued by the run. **The changes of a portal stay after its end:** the import records no event for a stay that is over, so the run also compares the issued document (invoice or receipt, not
  credited in full, no correction waiting) of each portal stay that ended inside the look-back with the lines the booking bills now (`Matches`, the comparison of the corrections); when it
  bills something else, or the stay is no longer on, the host gets the «To do» `fiscal_document_booking_changed`, **once for each document** (an item the host closed is not raised again).
  Nothing is credited or rewritten by the run: the host decides. A stay of CasaZen is not compared (its changes are events).

**Bulk issue.** `POST api/fiscal/documents/issue-batch` (`payment.write` + the flag): `{ documentIds?, from?, to?, propertyIds? }`; at most 200 documents; **an empty selection is 422
`document_batch_selection_required`, never «all»** (nothing, `[]`, an empty `propertyIds` alone); more than 200 ids is 422 `document_batch_too_large`; a period that ends before it starts is 422
`document_batch_period_invalid`. `from`/`to` select the drafts of the stays whose check-out day falls in the period (both ends included), `propertyIds` the drafts of those properties; a
credit note draft is issued only when named by id. Each draft goes through the issue path of the API in **its own transaction** (the number is reserved and rolled back with it: no gap); the
answer has **one row per document** `{ id, issued, number?, errorCode? }` with the counts and `remaining`, and never says «all issued» while one failed. **Reach:** the documents are narrowed
in SQL to the scope of the caller (the list's own `Matching`), so a caller restricted to some properties issues only the documents of the properties it reaches; any other id is the row
`fiscal_document_not_found`, the same answer as an id that does not exist. A row's code is the one of the issue (`booking_not_active`, `document_not_draft`, `document_read_only`,
`document_draft_outdated`, `invoice data missing`...) or `document_issue_failed` for a failure that is not a rule (the draft stays a draft and can be issued again).

**The documents of a booking.** `GET api/bookings/{id}/fiscal-documents` (`payment.read`, the booking authorized like its payments): number, kind, status, total, currency and the current payment
state of each document (a discarded draft left out). The booking card reads it.

**Live refresh.** The issue of **one** document asked by a person, and a credit note, queue the push `fiscal-document-issued` (`PushDeliveryKeys.FiscalDocumentIssued`, once per document, to the
hosts of the booking): through the decorator it is the entry of the bell and the live `notificationsChanged` that makes the open pages read the list of the documents and the booking again
(`docs/runbooks/in-app-notifications.md`). The bulk issue and the daily run do **not** notify: a message for each of hundreds of documents helps nobody, and their answer and the list tell.
No guest and no amount is in the text; a push that cannot be queued is logged and never fails the issue.

## Corrections when a booking changes (E12/F01/US01/T03, ⚖ I12)

An issued document is never rewritten. When its booking changes CasaZen prepares the **correcting document**; a booking with only a draft just refreshes or discards it (see above).
`FiscalDocumentBookingChangeHandler` consumes the events the booking lifecycle records and calls `IFiscalDocumentService.ApplyBookingChangeAsync`:

| Event (`DomainEventType`) | When | Payload |
|---|---|---|
| `BookingModified` (3) | the dates, the guests, the amounts or the property changed (the host's edit, the Octorate import) | `BookingEventPayload` |
| `BookingCancelled` (4) | the booking was cancelled by the host, the guest, the system or the channel | `BookingEventPayload` |
| `BookingNoShowMarked` (62, **new**) | the host marked a confirmed stay as a no-show (`StayLifecycleService.MarkNoShowAsync`, dedup key `booking-no-show:{id}`) | `BookingEventPayload` |
| `PaymentRefunded` (63, **new**) | a refund of a payment of the booking succeeded (`PaymentRefundService.RecomputePaymentAsync`, once for each amount refunded in all, dedup key `payment-refunded:{paymentId}:{amount}`) | `PaymentRefundedPayload { paymentId, bookingId, refundedAmount }`: ids and an amount, nothing personal |

The two new events did not exist: the cancellation records a refund only as a change of the payment, and the no-show recorded nothing. Neither starts a routine. The service decides from the
**state** of the booking and of its documents when the event is handled (what is credited already, what the guest is charged), never from the event, so an event delivered again, the two
events of one change (the refund and the cancellation) and events delivered out of order give one correction.

- **Date change, or any change of what the booking bills** (it is still on): the lines it bills now (`GuestDocumentLineBuilder`) are compared with the document (same total, same net for each type of
  line); when they differ, a **full credit note** of what no credit note covers yet (the document as stored) and a **new draft** with the new lines. The draft is made beside the credit note
  but cannot be issued before it (409 `document_already_issued`): the issue of a document asks again, with only the credit notes **issued**. A change that bills nothing different (notes) corrects nothing.
- **Cancellation, no-show, refund** (the booking is not on): **what the guest is charged is the consideration**: the document is kept when it equals what the guest paid, net of the refunds
  (`collected = completed and partially refunded payments, amount minus refunded`); otherwise a credit note for the difference, and a full credit note when nothing was collected. A refund is
  the credit note of the amount refunded: its **VAT split in proportion to the original lines** (`FiscalDocumentTotals.ScaleForCredit`, the cent of difference on the largest line). A refund
  of a booking that is still on is its own price and corrects nothing. A cancelled booking whose refund is still pending is waited for: the event is delivered again
  (`FiscalDocumentChangePendingException`, never consumed with no effect) and the refund that succeeds records `PaymentRefunded`, which prepares the credit note.
- **A portal collected the money** (`PaymentChannel` `Portal`): CasaZen does not know what the portal kept, so it makes no credit note by guessing: the host gets the «To do»
  `fiscal_document_booking_changed` and decides. The same for a stay of a portal the host collects from while the channel manager has not said what the guest paid
  (`Booking.OtaPaidAmount` is `null`): no payment of CasaZen can exist on such a stay, and «nothing known» is never «nothing paid». When it says, that amount is what the guest paid.
- **Receipts** (the Swiss receipt of an issuer that is not registered, the Italian receipt of a private owner): the correction is a **reversal receipt**: a credit note of the same kind of
  document, in the same series and numbering (a credit note takes the series of its original), with no XML and nothing to send to the SDI.
- **Each correction is a draft plus a «To do»** (`ReviewFiscalDocument`, reason `fiscal_document_credit_note_to_issue`, about the credit note: «Controlla ed emetti la nota di credito», no personal
  data). With `AutoIssueGuestDocuments` on the credit note and then the new draft are issued at once, through the same path as the API (the chain stops at the first refusal, and what is
  still a draft is the host's «To do»); the push `fiscal-document-issued` tells the hosts. A draft credit note is not made again from the booking, can be discarded, and is issued through
  `POST api/fiscal/documents/{id}/issue` (or by id in the bulk issue).
- **Never more than the original.** A credit note respects the cap: it is checked when it is prepared and **again under the lock of the series when it is issued** (a credit note issued by
  hand meanwhile leaves the draft refused with 422 `credit_amount_exceeds`). The decision and the draft are taken under the advisory lock of the booking (`FiscalDocumentBooking`, 1_604).
- **What cannot be decided** (the issuer's profile is incomplete, no tax rule applies) is not a failure of the event: the host gets the «To do» `fiscal_document_booking_changed`, the event is
  not delivered again, and the daily run makes the draft of a booking that has none. Any other failure (the database) is thrown again and the outbox delivers the event again.

## E-mail to the guest and the public page of the document (E12/F01/US02/T01, NF-83)

The guest gets the document of the stay as an **e-mail with a signed link**, never as an attachment (the e-mail queue has none). `IFiscalDocumentDeliveryService` (`FiscalDocumentDeliveryService`):

- **Who sends.** The host, from the booking, with `POST api/fiscal/documents/{id}/send-email` (`payment.write`, the booking authorized like its payments, behind `Features:FiscalDocumentIssuance`; every call sends again, so it is also
  the «send again»). And CasaZen by itself when the profile of the issuer has **«e-mail to the guest»** on (`OrgFiscalIssuerProfile.EmailGuestDocuments`, the step «Emissione» of the billing setup): `FiscalDocumentEmailHandler`
  consumes `FiscalDocumentIssued` (60) and `FiscalDocumentCreditNoteIssued` (61), the events of every way of issuing (the host's confirmation, the bulk issue, the daily run, the corrections an event makes), so the credit note of a
  correction goes to the guest who got the invoice.
- **What is sent.** Only an issued guest document of CasaZen: not a draft, not an owner invoice (422 `document_owner_invoice`), not a document imported from Octorate (422 `document_read_only`). The Swiss documents go like the Italian ones.
- **The address** is the one of the guest of the booking. A relay address of a portal (`…@guest.booking.com`, which forwards to the guest) is a normal address. Refused (422 `document_recipient_email_invalid`; for the automatic send:
  nothing is sent and the document stays in CasaZen and in the package of the accountant): empty, malformed, no dot in the domain, an erased guest (`Guest.DataAnonymizedDate`) and the domains that are never delivered
  (`.invalid`, `.localhost`, `.local`, where the placeholder of an erased guest, `ANON-…@deleted.local`, is). `example.com` and `.test` stay addresses: the data of a demo is e-mailed like any other.
- **The link** is `{App:PublicSiteBaseUrl}/documents/view/{id}?token=`. The token is an ASP.NET Core Data Protection time-limited payload (`FiscalDocumentLinkTokens`, purpose `Casazen.FiscalDocument.Link`, **30 days** from the
  moment the e-mail is queued) that carries **only the document id**: no name, no address, no amount. Nothing is stored for it (no column, no migration); a new e-mail makes a new token and the older ones open until they expire;
  the key ring is in the database, so a link outlives a redeploy.
- **The e-mail** (`EmailTemplates.FiscalDocumentDelivery`; Italian, or English for a guest of another country, as the answers of the host by e-mail): the issuer, the kind, the number and the day, the link and its last day (Rome time);
  **no amount, no address and no name of a portal or of a supplier**, every value HTML-encoded. Queued on Hangfire (`IEmailQueue`, retried by the job), never sent inside a request.
- **A queue that says no is not a success.** Without the e-mail provider or `App:PublicSiteBaseUrl`, or with the queue down, the host's send answers 422 `document_email_not_sent`. The automatic send tells the two cases apart: an
  environment that cannot send at all (no provider, no public address: a development machine, since the startup refuses it elsewhere) is an answer, `FiscalDocumentSendOutcome.NotConfigured`, logged with the ids and not delivered again;
  a queue that refuses while it could send is a failure, thrown again by `FiscalDocumentEmailHandler`, so the outbox delivers the event again (nothing was queued, so nothing is sent twice). A guest with no usable address and a
  document that is not a guest document are answers too: the document stays in CasaZen and the host sends it from the booking.
- **The event is delivered at least once, the e-mail once.** When another consumer of the event fails (or the dispatcher stops before it saves) the outbox delivers it again to every consumer, this one included. The service
  remembers **in memory, for an hour** (`FiscalDocumentDeliveryService.AutomaticSendMemory`, like `MessageReplyService.EmailKeyMemory`) the documents whose e-mail it queued by itself, and answers
  `FiscalDocumentSendOutcome.AlreadyQueued` to a later delivery: nothing is queued again. Only a queued e-mail is remembered, so a queue that refused, a guest with no address and an issuer that did not ask are asked again
  at the next delivery. **The limit:** nothing is stored (no «sent» column, no migration), so another replica, or a restart within the hour, does not know it and can queue a second e-mail, with a link of its own. The host's
  own send (`send-email`) is never held back: every call sends again.

**The public page** (`/documents/view/:id?token=`, a frontend task) reads two anonymous endpoints: `POST api/public/fiscal-documents/{id}` (the summary: kind, number, day, total, currency) and `…/pdf`, with `{ token }` in the **body**
and the rate limit `PublicFiscalDocumentDownload` per client IP (20 a minute). A wrong, expired or foreign token (made for another document or another purpose), a missing one and a document that is not an issued guest document of
CasaZen are **the same 404 `document_link_invalid`**, with no word of why. The token is never written to a log (the proof test `PublicDocumentLink_TokenNeverLogged_Test`, which also reads the source of the files that touch it).
The document is found by the signed id with the tenant filter ignored, and the PDF is drawn by `IFiscalDocumentService.GetPdfAsync` in a scope entered under the org of the document, so a signed-in person of another org who opens
the link sees what the guest sees. The token is in the query of the page, which is a frontend route: the access log of the web host holds it, the one of the API never does.

## XML (`FatturaPaXmlBuilder`)

- Written with `XDocument`/`XmlWriter`, never by concatenation: every text is escaped; decimals in invariant culture (two places, unit prices and
  quantities up to eight); days as `yyyy-MM-dd` (Europe/Rome calendar days).
- `IdTrasmittente` (E17/F01/US01/T03): the subject whose channel sends the file to the SDI, by its fiscal code (else its VAT number): the agency, both for a
  document it issues in its own name and for one it issues on behalf of an owner. No provider of the SDI exists yet: E17/F01/US02 replaces it with the
  provider's when one is configured (`FiscalDocumentService.TransmitterOf`, the one place). A Swiss agency has no Italian code: the invoice of an Italian owner
  company needs that provider.
- `CodiceDestinatario`: the recipient's code; `0000000` with the PEC or with nothing (the recipient's fiscal drawer); `XXXXXXX` for a recipient outside
  Italy. A recipient abroad without a VAT number (a private guest, B2C) has `IdFiscaleIVA` with its country and `99999999999`; every address abroad has the `CAP`
  `00000` and no `Provincia`.
- `CedentePrestatore` from the snapshot of the issuer: the organization (its VAT number and fiscal code from `Org`) or **the owner company** that issues the
  document of its stay (its own VAT number, fiscal code and regime code); `CessionarioCommittente` from the recipient snapshot of the document (a company
  with `Denominazione`, a person with `Nome` and `Cognome`).
- **Third-party issuer (TZ).** The invoice of an owner company issued through the agency carries `TerzoIntermediarioOSoggettoEmittente` with the agency
  (`IdFiscaleIVA` IT + its VAT number, its fiscal code, its `Denominazione`, from `FiscalIssuerSnapshot.Intermediary`, as at the issue) and `SoggettoEmittente`
  `TZ`; the credit note of such an invoice too. An invoice of the organization names no third party.
- **TD17 (for E17/F01/US03).** The builder writes a self-billed invoice issued by its recipient (`FatturaPaInput.IssuedBy` `Recipient`: `SoggettoEmittente`
  `CC`) with a foreign supplier as cedente (its country and VAT number, `RF18`, an address abroad) and the supplier's invoice as `DatiFattureCollegate`; the
  type code comes from the caller. Nothing in this slice makes one yet.
- A document that does not validate is 422 `fatturapa_xml_invalid`: the message names the number of errors and the line:position of the first ones,
  never a value of the document; the log carries ids and counts only.

## Numbers (`FiscalNumberingService`)

- Per **org, issuer and series** (E17/F01/US01/T01), consecutive from 1, no gap, no duplicate: the issuer is the profile the document is issued on
  (`FiscalDocument.IssuerProfileId`: the organization's, or an owner's with its sezionale). The counter (`FiscalNumberSequences`, unique
  `(OrgId, IssuerProfileId, Series)`) is incremented under the advisory lock `FiscalNumbering` (1_601) with the key `{orgId:N}:{issuerProfileId:N}:{series}`, in
  the READ COMMITTED transaction that saves the document; a rollback gives the number back. The database refuses a number twice for one issuer and series
  (`UIX_FiscalDocuments_OrgId_IssuerProfileId_Series_SequenceNumber`); two issuers of one org both have their `2026/1`. A credit note takes the issuer and the series
  of its original.
- At the first use of a series the counter starts above the highest number the issuer already used in it; for the **profile of the organization** the documents
  numbered before the issuers existed and the legacy `AgencyInvoices` count too, an owner's series starts from its own documents only.
- A series has at most 10 characters (the display `{series}/{number}` fits the 20 characters of `Numero`): longer is 422 `series_too_long`.
- **Migration `AddOwnerIssuerProfiles`.** Adds the owner and the owner's codes to the profiles (and their partial unique indexes), encrypts the personal columns
  (`text`), adds `FiscalDocuments.IssuerProfileId` (restrict) and `FiscalNumberSequences.IssuerProfileId`; **every existing document and counter is moved to the
  profile of its organization** (a counter of an organization without a profile, which cannot have issued, is deleted: the next issue seeds it again above the
  highest number used); the issuer rows of the bookings become one per document (see *Drafts and bulk issue*). **Rollback:** the down migration restores the
  old unique indexes, so it fails once an owner has issued in a series the organization also used, or a booking has two issuer rows: roll back before any owner
  document is issued (the flag `FiscalDocumentIssuance` off stops new ones).

## Export of the list (`FiscalDocumentExports`, `XlsxWriter`)

- `GET api/fiscal/documents/export?format=csv|xlsx`: the issued documents (CasaZen's and the imported ones) that the caller reaches, the same rows as the list, in a
  file named `documenti-yyyy-MM`; the same fourteen columns in both formats: kind, number, issue date, booking code, property, recipient, taxable base, VAT, stamp
  duty, total, currency, payment state, SDI state, source.
- The amounts are the stored ones, to two places (a credit note keeps its positive amounts: its kind says what it is); the payment state is the current one, as in the
  list. The recipient is the name only: no fiscal code, VAT number or address leaves in a file. Drafts, discarded drafts, the lines and the XML are not in it, and the
  channel manager is never named (its documents say «Emesso dal tuo channel manager»).
- The Excel file is written by `XlsxWriter` (no package, only `System.IO.Compression`): amounts are numbers, days are dates, every text is an inline string, so a text
  that starts with `= + - @` is never run (the CSV puts an apostrophe before it). More than 9,999 documents is 422 `csv_too_many_rows` or `xlsx_too_many_rows`: narrow the period.

## Retention and privacy

- A fiscal document is kept ten years (ADR-033-R7): the booking, the property, the owner, the statement version, the credited and the replaced document
  are restrict foreign keys; the erasure of a guest leaves the recipient snapshot of an issued document in place.
- The recipient snapshot, the issuer snapshot and the XML are encrypted at rest (`Casazen.FiscalDocument`, `docs/runbooks/encryption.md`); so are the
  IBAN and the QR-IBAN of the issuer and the personal data of every issuer profile, the owners' included (`Casazen.OrgFiscalIssuerProfile`: legal name, fiscal
  code, VAT number, street, house number, postal code, city, PEC). The logs carry ids, codes and field names, never a value.
