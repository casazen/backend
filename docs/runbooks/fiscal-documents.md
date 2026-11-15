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
- **SDI sending only through a provider, behind a flag.** CasaZen has no channel of its own to the SDI. An issued invoice has the state `to_send` and its
  XML can be downloaded; it goes to the SDI only through the accredited channel of an intermediary (`Features:SdiSubmission` and `Sdi:Provider:*`, see *SDI
  submission, outcomes and correction*, E17/F01/US02), and nothing is ever marked sent without the provider's answer or a person's declaration. With no
  provider configured the issuer uploads the XML to the portal of the Agenzia delle Entrate and declares it sent.

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
| `italy` | «Italia» | `stampDutyReference`, `conservationAgreementConfirmed`, `virtualStampConfirmed` | Italy only: `conservation_agreement_unconfirmed`, `stamp_duty_unconfirmed` (neither the stamp reference nor the confirmation of the virtual stamp); `owner_issuer_missing` (reason `required`, with `count`, E17/F01/US01/T01): owners of Italian properties without an issuer profile that can issue, for any organization that has them; `sdi_issuer_registration_pending` (reason `pending`, with `count`, E17/F01/US02/T02): the issuers whose VAT number the SDI channel has not enabled yet, only while a provider is configured and `Features:SdiSubmission` is on (see *SDI submission, outcomes and correction*) |

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
  - **The guest's name stands in (FIX/DOC-RECIPIENT).** The portals (Booking.com, Airbnb, Expedia, Vrbo through the channel manager) and the site of the agency send no
    invoice data, and the document is due all the same (CasaZen issues for every booking, P1). When the document needs only a name and the booking has **no invoice data**
    (no row, or a row with no name that holds no «yes» to «Ti serve la fattura?»), the recipient is the guest of the booking: its first and last name, as a private person,
    **and nothing else**. Never copied: address, e-mail, phone, fiscal code, VAT number, nationality, country (the PDF prints the name alone; the e-mail of the document carries no recipient
    field, it greets the guest by first name as before). The snapshot stays in the same encrypted columns: no migration, no flag. The one place is the draft (`FiscalDocumentService.RebuildAsync`), so the
    check-out event, the daily run `fiscal-documents-due`, the draft of the host and a draft made again all do the same, and a draft made again never keeps a field of an
    invoice data it no longer has.
  - **Its limits.** The invoice data of the booking wins whenever it holds a name (typed by the host, given at the check-in or read from the channel manager), with all its
    fields, as before. An **Italian invoice** (an ordinary VAT or a flat-rate issuer) never falls back: no invoice data is still 422 `document_invoice_data_missing` with
    `fields: ["name"]`, and the stay is skipped by the daily run and read again at the next one. A guest who **asked for an invoice** (the «yes» at the check-in) without giving
    the data is waited for in the same way: the host asks for the data, and no document is made out to the guest's name behind their back. A guest who was **erased**
    (anonymized: the name is the placeholder `ANONYMIZED`), **deleted** or who **asked to be erased** never gets a document made out to a name that is no longer theirs: the
    same 422, and the stay waits for the host (who may type the invoice data of the booking, which then wins).
  - **The issue reads the recipient again.** For a document that needs only a name, the issue (the one document, the bulk issue, the automatic issue) reads the recipient as the draft does
    (`RecipientOfAsync`) and compares it with the draft: when they differ the answer is 409 `document_draft_outdated` (the same as for a changed rule: nothing is numbered, the draft stays
    a draft, and refreshing it, `POST api/fiscal/documents/{id}/refresh`, makes it again). It differs when the booking got invoice data with a name since the draft was made out to the guest
    (the data wins), when the invoice data the draft was made from changed or is gone, when the name of the guest changed, and when the guest was erased, deleted or asked to be erased
    since: the draft made again is then the 422 above, so a draft made before the erasure never goes out with the name of the guest. The draft of an **Italian invoice** is not read
    again at the issue: it never falls back and it is issued as it always was.
  - **The first run after the deploy.** The daily run reads the last `DueLookbackDays` (31) days, so the first one after this change makes the documents of every stay of that window
    that had been skipped for lack of invoice data (up to 500 stays a run per organization, the rest the next day) and, with `AutoIssueGuestDocuments` on, issues them at once. They are
    numbered from the most recent stay to the oldest (the run reads the most recent first): the most recent stay takes the lowest number, and every one gets the day of the run as its
    issue date. Where `EmailGuestDocuments` is on (it is off by default) the e-mail with the link goes to every guest of those stays. The PO decides before the automatic issue is switched
    on for real data.
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
- **Recipient**: the name, plus the address when there is one (never an Italian fiscal code or VAT number). A booking with no invoice data (a portal sends none) is made out to
  the name of its guest, with no address (see the rule «Recipient» above).
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

## Documents in the owner's name: portal and export (`IOwnerIssuedDocumentsExport`, E17/F02/US01/T01, NF-116)

The documents CasaZen issues **in the name of an owner** (the receipt of a private owner, the FatturaPA of a company, their credit notes: see *Document plan of a booking*)
are shown to the owner in its portal and handed to the owner's accountant. Acceptance steps C8 (a copy of the invoices issued in the name of company B for its accountant) and
D2 (the yearly summary of the documents issued in its name and of the recharges) of `pilota-flusso-completo-2026-11-14.md`. No migration, no flag and no setting of its own.

- **Which documents.** The issued documents of a stay (purpose `GuestStay`, source CasaZen, kind invoice, receipt or credit note, with a number and a day of the issue) whose
  `IssuerProfileId` is the issuer profile of the owner (`OrgFiscalIssuerProfile.PropertyOwnerId`). Never the organization's own documents (the agency's services to the guest),
  another owner's, a draft, a discarded one, a proforma, or an invoice of the agency to the owner. The document of several co-owners is issued on the profile of the one who signs
  (⚖ I6): it is that co-owner's, and the others, who are listed in it with their shares, do not find it in their own list.
- **Owner portal** (`OwnerPortal`, rate limits `OwnerPortalRead` and `OwnerPortalDownload`):
  - `GET api/owner-portal/issued-documents[?period=yyyy-MM|yyyy]`: `{ documentId, agencyId, agencyName, number, issueDate, kind, total, currency, sdiStatus, hasXml }`, the newest
    first, at most 500; 400 `accountant_package_invalid_period`.
  - `GET api/owner-portal/issued-documents/{id}/pdf?lang=` and `…/{id}/xml` (404 `fiscal_document_xml_unavailable` for a receipt, which has none).
  - `GET api/owner-portal/issued-documents/export?period=yyyy-MM|yyyy`: the ZIP of the next paragraphs.
  - Only the owner of the `owner_id` claim (every owner profile its ownerships are on, in every agency) and a delegated contact that holds `accounting.read` (403
    `owner_portal_permission_missing`); behind `Features:AgencyTier` **and** `Features:FiscalDocumentIssuance`: with either off the list is empty and a file is 404 (`owner_issued_document_not_found`,
    the export `owner_issued_documents_not_available`). One 404 for a document of another owner, of the agency, a draft or missing. **Nothing of a guest is in the list** (no name, no contact,
    no tax data): the PDF and the XML hold what the document itself says (the name and, for a company, the tax data of its recipient; never a contact), and the register of the ZIP the name of
    the recipient.
- **PDF.** Rendered like `GET api/fiscal/documents/{id}/pdf` (the issuer of the snapshot of the issue, the co-owners, the agency as intermediary, the stamp text). The payment state is the
  current one, read from the payments of the booking **naming the org of the document** (`FiscalDocumentService.CollectedByBookingAsync(…, ofOrg)`): the portal has no tenant, and a
  document that was paid is never printed «da incassare».
- **Export for the accountant.** `GET api/owners/{ownerId}/issued-documents/export?period=yyyy-MM|yyyy` for the agency (`OwnerIssuerProfilesController`: `agency.statement.view` on top of
  `agency.owner.view`, behind `FiscalDocumentIssuance`; **the whole organization only**, 403 `forbidden` for a caller who reaches only some properties, because the accounts of an owner span
  all of them; 404 `owner_not_found` for an owner of another org) and the same ZIP for the owner at `GET api/owner-portal/issued-documents/export`. One ZIP, streamed,
  `casazen-documenti-emessi-<period>.zip`, `Cache-Control: private, no-store`; 422 `accountant_package_too_large` before the first byte (the caps of `DocumentArchive`, as the accountant package).
  - `documenti-emessi/`: the PDF and the XML (when stored) of each document issued in the month or the year (by the day of the issue), and `registro-<issuer>.csv` and `.xlsx`, made with the
    tables of the accountant package: the register (kind, number, day, recipient, collected by, taxable, VAT, stamp duty, total, currency; credit notes negative), the VAT of each document
    for each rate, the totals for each rate and nature and **the totals of the period for each currency** (documents, taxable, VAT, stamp duty, total), which are the sums of the rows
    of the register.
  - A **year** adds `riepilogo-annuale/documenti-emessi.csv` and `.xlsx` (the documents by month, kind and currency, then the totals and the VAT for each rate of the year),
    `riepilogo-annuale/fatture-agenzia-e-riaddebiti.csv` and `.xlsx` (the invoices of the agency to the owner: commission, recharges with their VAT and expenses advanced for each currency and for each
    invoice, from the lines of the invoices of E14; credit notes negative) and `fatture-agenzia/` (the invoices and credit notes the agency issued to the owner in the year: the PDF the owner
    downloads in its portal, the XML when Italian, and their register).
  - `indice.csv`, written by the writer of the accountant package: every file with its description, its rows and its SHA-256. A person who reads the accounting of more than one owner
    profile (the owner of two agencies, a contact of two owners) gets one folder `proprietario-<n>-<issuer>/` for each.
- **Privacy.** Reads name the org and the owner in every query (the portal has no org of its own); the log carries ids and counts. An anonymized owner keeps its documents (legal retention,
  ADR-033-R7) and the agency can still export them; the portal reaches none (its ownerships no longer count).

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
  document it issues in its own name and for one it issues on behalf of an owner (`FiscalDocumentService.TransmitterOf`, the one place). The XML is
  immutable once issued, so the provider's channel sends the file **as it was issued**: the transmitter in the file is never rewritten at the submission. Whether the
  provider needs its own transmitter code in the header is a point to check on its sandbox (see *Verifying the channel*, open point 2): if it does, `TransmitterOf`
  is where it changes, for the files issued afterwards, never the stored XML. A Swiss agency has no Italian code: the invoice of an Italian owner company needs that
  provider.
- `CodiceDestinatario`: the recipient's code; `0000000` with the PEC or with nothing (the recipient's fiscal drawer); `XXXXXXX` for a recipient outside
  Italy. A recipient abroad without a VAT number (a private guest, B2C) has `IdFiscaleIVA` with its country and `99999999999`; every address abroad has the `CAP`
  `00000` and no `Provincia`.
- `CedentePrestatore` from the snapshot of the issuer: the organization (its VAT number and fiscal code from `Org`) or **the owner company** that issues the
  document of its stay (its own VAT number, fiscal code and regime code); `CessionarioCommittente` from the recipient snapshot of the document (a company
  with `Denominazione`, a person with `Nome` and `Cognome`).
- **Third-party issuer (TZ).** The invoice of an owner company issued through the agency carries `TerzoIntermediarioOSoggettoEmittente` with the agency
  (`IdFiscaleIVA` IT + its VAT number, its fiscal code, its `Denominazione`, from `FiscalIssuerSnapshot.Intermediary`, as at the issue) and `SoggettoEmittente`
  `TZ`; the credit note of such an invoice too. An invoice of the organization names no third party.
- **TD17 (E17/F01/US03/T01).** The builder writes a self-billed invoice issued by its recipient (`FatturaPaInput.IssuedBy` `Recipient`: `SoggettoEmittente`
  `CC`) with a foreign supplier as cedente (its country and VAT number, `RF18`, an address abroad) and the supplier's invoice as `DatiFattureCollegate` when the caller has it; the
  type code comes from the caller. `FiscalDocumentService.SelfBillingFatturaPaInputOf` makes it for a document with the purpose `SelfBilling` (see *Self-billed invoices*): the
  type `TD17`, the supplier from the recipient columns, the holder from the issuer snapshot, no related document.
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

## SDI submission, outcomes and correction (E17/F01/US02, NF-116, NF-86, D13, D15)

The Italian electronic invoices (an invoice or a credit note whose XML was issued, state `to_send`) go to the SDI through an accredited channel of an intermediary. The port
`ISdiEInvoiceProvider` serves every issuer (the organization and the owners who issue): `IsConfigured`, `Name`, `SubmitAsync(PlatformInvoice, Org)` (the SaaS invoices, unchanged),
`SubmitDocumentAsync`, `GetStatusAsync` and `ReadNotificationAsync`. With no provider configured `UnconfiguredSdiEInvoiceProvider` is registered and **nothing changes**.

### States and who may change them

`FiscalDocument.SdiStatus`: `to_send` → `submitted` → `delivered` or `rejected`; `manual_declared` for a file the issuer sent by hand. **No state says «sent» without the provider's
answer or a person's declaration**, and the provider is never called while it is not configured or `Features:SdiSubmission` is off.

| Change | Who | Rule |
|---|---|---|
| `to_send` → `submitted` | `IFiscalDocumentSdiService.SubmitAsync` (`POST api/fiscal/documents/{id}/sdi/submit`) | only with the provider's accepted answer **and** its id of the submission (`SdiTransmissionId`); a refusal keeps `to_send` with the code in `SdiErrorCode` (≤ 20 characters: `issuer_pending`, `provider_refused`, `provider_error`, `not_supported`) and a cleaned note in `SdiErrorDetail`; a second submission answers `AlreadySubmitted` |
| `submitted` → `delivered` / `rejected` | `RecordOutcomeAsync`, the **one entry** of the provider's webhook, of the job `fiscal-sdi-status` and of the declaration of a rejection | idempotent; the first answer wins; «submitted» changes nothing; **a delivered invoice is never rejected afterwards** (and a rejected one is never delivered): the contradiction is ignored and logged. A rejection is also accepted from `to_send` and `manual_declared` (the issuer saw it on the portal of the Agenzia delle Entrate) |
| `to_send` / `manual_declared` → `manual_declared` | `DeclareManuallyAsync` (`POST …/sdi/declare` `{ kind: Sent, date, reference }`) | the day (Europe/Rome, not in the future, not before the issue) and the reference are required; the reference is kept in `SdiTransmissionId`, the day in `SdiStatusUpdatedAt`. Works with no provider. A document the provider has (`submitted`) is decided by the provider (409 `sdi_not_to_send`); an answered one is final (409 `sdi_state_final`) |
| → `rejected` by hand | `DeclareManuallyAsync` `{ kind: Rejected, errorCode, errorText? }` | goes through `RecordOutcomeAsync`: same state, same event |

- **One document at a time.** A submission, an outcome, a declaration and the start of a correction run under the transaction-scoped advisory lock `FiscalDocumentSdi` (1_620, key
  `{documentId:N}`), the state read again under it; the submission holds it across the call to the provider.
- **Behind the flags.** `submit`, `declare` and `correct`, the webhook and the job need `Features:SdiSubmission` **and** `Features:FiscalDocumentIssuance` (404 while either is off; the job is
  removed). `GET api/fiscal/documents/{id}/sdi` (the state, with the reason in plain words) is a read and stays. The writes need `payment.write`, the document is authorized through its
  booking (a document without one, for the whole org), another org's document is a 404. `GET api/fiscal/sdi/issuers` (behind the issuance flag) lists the issuers that send electronic
  invoices with their state with the channel; a caller who reaches only some properties («Solo alcuni», an owner) sees only the issuers that have a document on a booking of those
  properties, a caller with no scope is 401 (the same rule as the list of the documents).
- **Events** (ADR-022, recorded in the unit of work of the outcome, once for a document): `FiscalDocumentSdiDelivered` (64), `FiscalDocumentSdiRejected` (65), `FiscalDocumentCorrected` (66). Ids and codes only.
- **The recurring job `fiscal-sdi-status`** (`SdiStatusPollJob`, every 30 minutes, never two runs at once): the fallback of the webhook. Only with both flags on and a provider configured; for every
  `submitted` document, in a scope under its org's tenant, it asks the provider and records an outcome. A document it could not read is logged (id and type of the error) and read again at the next
  run, and the run then **fails** (`SdiStatusPollException`): nothing is reported as done that was not.

### Rejection reasons in plain words (`SdiRejectionReasons`)

The map from the code of the notification of rejection (the 5-digit codes of the SDI) to the key of its text in `SharedResources` (Italian default, English). It covers the **63 codes** of the
official list, family by family (several codes share a text where the cause and the cure are the same), plus the four codes of the channel's own refusals; any other code is the generic
text with the raw code in it, a rejection without a code says so, and the words never name the provider (D14). What a notification says is kept only after `SdiRejectionReasons.Clean` (no e-mail
address, fiscal code of a person or number of nine digits or more, at most 500 characters).

| Source (read on 2026-11-15) | |
|---|---|
| Document | «Elenco dei controlli effettuati sul file fattura del Sistema di Interscambio», **versione 2.0, 31/01/2025** (valid from 15/05/2026, with the specifications of the SdI v1.8.4) |
| Page | Agenzia delle Entrate, `https://www.fatturapa.gov.it/it/norme-e-regole/DocumentazioneSDI/` |
| File | `https://www.fatturapa.gov.it/export/documenti/fatturapa/v1.4/Elenco-Controlli-versione-2.0.pdf` |
| SHA-256 | `a3ec6b4b5f74c43bca8c5849f49b33d2c9b0784c5d227f32a25a81b1d7caf9f2` (1,054,977 bytes) |
| Name of the file | «Specifiche tecniche relative al Sistema di Interscambio» versione 1.8.4: `{country}{transmitter id}_{progressive}.xml`, the progressive at most five letters and digits (`SdiFileNames`) |

A new official version: read the list again, compare the codes (`SdiRejectionReasonsTests` pins the 63), add the code to the map and the text in **both** `SharedResources` files, and record the new
file here. Two rules of the list the correction rests on: an invoice already sent and **not rejected** is a duplicate (00404), and a rejected one is not counted for it, so the same number may be sent again.

### Correction of a rejected invoice (`IFiscalDocumentCorrectionService`, `POST …/sdi/correct`)

- A rejection raises the «Da fare» **«Correggi la fattura»** (`FiscalDocumentRejectionHandler`, the consumer of `FiscalDocumentSdiRejected`): type `FiscalDocument`, action `CorrectInvoice` (31),
  reason `sdi_rejected`, urgency high, entity the document, no personal data; the reason in plain words is read from `GET …/sdi`. It closes by itself when the correction starts
  (`FiscalDocumentCorrected`) or the document is credited (`FiscalDocumentCreditNoteIssued`, for an owner invoice). The frontend adds the action to its `ACTION_ROUTE_MAP`.
- **Told live, not only listed.** For an invoice of a booking the same consumer also queues a push to the hosts of the booking (`FiscalDocumentLiveRefresh.TellOfRejection`, type
  `fiscal-document-sdi-rejected`, delivery key `fiscal-document:{documentId:N}:sdi-rejected`, one per document whatever the redeliveries of the event): the bell gets its entry, the pop-up appears
  and the open pages read the documents again without a reload (the frontend maps the type in `use-notifications-realtime.ts`, like `fiscal-document-issued`). Fixed text (`FiscalDocPushSdiRejected*`
  in both `SharedResources` files), no reason, no code, no guest, no amount; best effort after the «Da fare» is saved. An owner invoice has no booking: only its «Da fare» tells.
- **A rejected invoice counts as not issued.** `StartCorrectionAsync` (only for `rejected`, 422 `document_not_rejected` otherwise; a document with credit notes is 422 `document_not_correctable`,
  an imported one 422 `document_read_only`, an owner invoice 422 `document_owner_invoice`): the rejected document **keeps its display number for the history, gives up its sequence number and
  becomes `Discarded`**, and a new **draft** (`ReplacesDocumentId`) is made with the same lines, amounts, recipient, issuer and series and **holding the number the rejected document gave up**. The host
  fixes the data (the invoice data of the booking; the refresh or the daily run makes the draft from it again) and issues the replacement, which uses that number (and keeps the day of the
  invoice it replaces) instead of reserving one: the next invoice of the series is the next number, so the sequence has neither a duplicate nor a hole. A second start answers the same draft.
- **Only the replacement of that document, only because it never was delivered.** `rejected` is reached only by a document that was not delivered (a delivered one is never rejected afterwards,
  see above). The issue checks it again under the numbering path (`document_replacement_invalid`, 422): the replaced document must be a discarded rejected one of the same issuer, series and number,
  and no other replacement of it may be issued. A document that **was delivered** is corrected with a credit note, never replaced.
- **A rejected invoice is never credited** (422 `document_rejected_use_correction`, also for the credit note a booking change would prepare: the host gets the «To do» that the booking changed).
- ⚖ To be confirmed by the accountant: the reuse of the number of a rejected invoice, and that the replacement keeps its day (the Agenzia delle Entrate accepts the same number and date for a file sent again
  shortly after a rejection; after that, or for a delivered invoice, the credit note and a new invoice are the way). A replacement that is discarded leaves its number unused: the rejected document keeps it
  in the history.

### The channel: adapter, configuration and webhook (E17/F01/US02/T02)

`RestSdiEInvoiceProvider` is the adapter of **one** provider, chosen by `Sdi:Provider:Name` (⚖ T1: the choice is the product owner's); everything provider-specific stays in it, and another
provider is another adapter and another name. It was written against the **public documentation of Invoicetronic, API v1**, read on 2026-11-15 on the official pages only:

| What | Where |
|---|---|
| Documentation | `https://invoicetronic.com/en/docs/` (API keys, sandbox, webhooks, idempotency) |
| API reference and OpenAPI | `https://api.invoicetronic.com/v1/docs`, `https://api.invoicetronic.com/v1/docs/swagger.json` (API **v1**, base URL `https://api.invoicetronic.com/v1/`) |
| Webhooks | `https://invoicetronic.com/en/docs/webhooks/` (events `update.add`…, the `Event` body in PascalCase, `Invoicetronic-Signature`, 15 seconds, eight attempts) |
| Sandbox | `https://invoicetronic.com/en/docs/sandbox/` (keys `ik_test_…` / `ik_live_…`, simulated updates every half hour, nothing reaches the SDI) |

- **Authentication.** HTTP Basic: the key of the account is the user name, the password is empty. Never logged.
- **Issuer registration.** The provider sends on behalf of many VAT numbers: each issuer is a *company* of the account. Before the first submission of an issuer the adapter looks for its VAT number (or
  fiscal code) among the companies (`GET company?q=`) and creates it when it is not there (`POST company`: name, `vat` or `fiscal_code`). A number registered on **another** account cannot be enabled here
  (`company_already_registered`): the submission is refused with the code `issuer_pending`, the document stays `to_send`, and the issuer is **`pending`** (`GET api/fiscal/sdi/issuers`, the item
  `sdi_issuer_registration_pending` of the section «Italia» of the billing setup, with its count) until a later submission of it is accepted. The state is derived from the documents: the provider is never
  asked on a read of the setup.
- **Submission.** One call, `POST send?validate=true` with the XML in base 64 and the id of the document as `Idempotency-Key` (a second submission of one document is never sent twice by the provider,
  for 24 hours), the provider names and signs the file itself (`SdiFileNames` is the name the specifications give, for adapters that need it). A refusal of the provider (400, 422) is `provider_refused`; the
  credentials, the quota, a missing route and a failure of the provider are `provider_error`; a failure of the transport is `provider_error` too (never an exception of a submission). The platform's SaaS invoices
  are **not** submitted through it (`not_supported`): with a provider configured they go `pending` and then `failed` instead of `manual_required`, and stay in the admin queue to be
  declared issued by hand as before (`docs/runbooks/billing-tax.md`); the plan checkout gate (`BillingEntryGate`) counts a configured provider as «e-invoicing decided». Configure the
  provider only where that is wanted (the test environment).
- **What the SDI said.** Read from the updates of a submission (`GET update?send_id=`, the latest one): `Consegnato`, `NonConsegnato`, `ImpossibilitaDiRecapito`, `AttestazioneTrasmissioneFattura`,
  `AccettatoDalDestinatario` and `DecorrenzaTermini` are **delivered** (the invoice is issued and available to its recipient: ⚖ to be confirmed by the accountant, in particular for the non-delivery),
  `Scartato` is **rejected** with the codes and the words of the SDI, `Inviato` and any other state leave the document `submitted`.
- **The webhook** `POST api/webhooks/sdi/{secret}`: the secret of the address is `Sdi:Provider:WebhookSecret` (32 characters or more, different on test and production), compared in constant time
  (`SdiWebhookVerifier`). A wrong secret is **401** and the log line `Security: SDI webhook refused` (event `SdiWebhookSecretRefused`, with the remote address, never the secret or the body); no usable secret, or no provider, is
  **500** `sdi_webhook_not_configured`. The body only says what changed: the adapter reads the update **again from the API** and never trusts the body; an event that is not about an update is acknowledged
  (200) and recorded nowhere. A notification about a submission CasaZen does not know yet (the call may overtake the save) is **404** `sdi_transmission_unknown`, and a failure of the provider's API or of
  the database is 500: the provider delivers the call again (any non-2xx is retried), the outcomes being idempotent, and the job reads the state anyway. **The secret is part of the path: the request logs must not
  print it** (the API logs no path by itself while `Logging__LogLevel__Microsoft` is `Warning`; the error middleware logs the route template; Railway's own HTTP logs show the path to the members of the project:
  treat access to the project as access to the secret and rotate it when a member leaves).
- **Privacy.** Neither the file, nor a fiscal id, a name or the key of the account reaches a log (the proof test `SdiAdapter_NeverLogsInvoicePayload_Test`); a call is logged by its operation and the status of the
  answer. The XML and the snapshot of the issuer stay encrypted at rest.

| Setting | Meaning |
|---|---|
| `Sdi__Provider__Name` | the adapter: `invoicetronic`; empty (or an unknown name) = no provider, nothing is ever submitted |
| `Sdi__Provider__BaseUrl` | the API address (`https`, or `http` on the machine itself); empty = `https://api.invoicetronic.com/v1/` |
| `Sdi__Provider__ApiKey` | the key of the account at the provider: a sandbox key (`ik_test_…`) in the test environment, a live key in production. **Secret**; without it no provider |
| `Sdi__Provider__WebhookSecret` | the secret of the address of the webhook, 32 characters or more. **Secret** |
| `Sdi__Provider__TimeoutSeconds` | the longest a call may last (5 to 120, default 30) |
| `Features__SdiSubmission` | the module (default off) |

### Verifying the channel (test environment, sandbox key) and the open points

1. Set `Sdi__Provider__Name=invoicetronic`, `Sdi__Provider__ApiKey` (a sandbox key), `Sdi__Provider__WebhookSecret` and `Features__SdiSubmission=true` on the test environment; register at the provider a webhook for
   the event `update.add` with the address `https://<api host>/api/webhooks/sdi/<secret>`.
2. Issue an invoice to an Italian company whose recipient code is one of the reserved values of the sandbox: `SBXRC00` (delivered), `SBXNS00` (rejected), `SBXMC00` (non-delivery); submit it
   (`POST …/sdi/submit`): `submitted`; the sandbox answers at the next half hour (up to about two hours), the webhook or the job records the outcome.
3. A rejected one: the «Da fare» appears, `GET …/sdi` gives the words, `POST …/sdi/correct` makes the draft with the same number, and after the fix it is issued and submitted again.

Open points to settle on the sandbox before anything is turned on in production (none is verified by this change, which never called the real service): (1) the format of `vat` of a company (the adapter sends the 11 digits
without the country prefix and reads both forms); (2) whether the transmitter in the header of the file (`IdTrasmittente`, the agency's) is accepted as it is or the provider needs its own code; (3) the
enabling flow of a VAT number (delega) for the owners who issue through the agency; (4) the mapping of the non-delivery states to «delivered»; (5) the behaviour of the idempotency key after 24 hours.

### Adding another provider

1. Choose the provider (contract, sandbox, public API documentation: the table of candidates is in `.claude/context/regulations/fiscale.md`); record the official documentation URLs, the version and the date here.
2. Write an adapter of `ISdiEInvoiceProvider` in `Casazen.Infrastructure/External/Sdi/` (all of the provider's wire format in it, no provider name outside it and its settings), give it a name in
   `SdiProviderOptions` and choose it in the registration of `AddCasazenFiscalDocuments`; its tests use a fake HTTP handler and never the real service.
3. Add its settings to `docs/runbooks/deploy-checklist.md` § 2 and its notifications to `ReadNotificationAsync`, so that every one goes to `RecordOutcomeAsync`.

## Self-billed invoices (TD17, `ISelfBillingService`, E17/F01/US03/T01, NF-116, D4)

An Italian VAT holder that pays a commission to a **foreign portal** integrates the portal's invoice with a TD17 (reverse charge, art. 17 c. 2 DPR 633/72), by the 15th of
the following month (⚖, `SelfBilling:IssueByDay`). CasaZen prepares it from the owner ledger and issues it through the normal path: a `FiscalDocument` with the purpose
`SelfBilling`, kind invoice, type of document `TD17`, **one for each holder, portal entity and month**.

**Who needs one** (`IPortalAccountHolderResolver`, ⚖ I10: the one reader of who holds the account of a listing on a portal):

| Holder of the account | Who | TD17 |
|---|---|---|
| V1, the owner | an owner company or a professional (Italian issuer profile with a VAT number, complete) | yes, the owner's, issued by CasaZen on the owner's profile through the agency |
| V1, the owner | a private person (the portal charged the Italian VAT 22%) | no (`holder_private_person`) |
| V1, the owner | no issuer profile: a company or an owner with a VAT number | not yet: reported in `failed` (`owner_issuer_missing`, with the fields missing) |
| V2, the agency | an Italian agency with a VAT number and a complete profile | yes, the agency's (and E14 recharges the commission to the owners) |
| V2, the agency | a Swiss agency | no (`holder_not_italian`; the VAT summary of the package tells the purchase tax, E15, ⚖ not verified) |

With several co-owners the owner of a V1 account is the one who **signs for the property on the check-out day**, by the rule of the document plan
(`DocumentPlanRules.SignatoryOf`: the mandate in force, then the largest share, then the oldest ownership, then the smallest id), so the TD17 and the receipt or the invoice of the
stay are always the same owner's.

**The amounts.** The taxable amount is the sum of the `ChannelFee` lines of the owner ledger of the month (a reversal and the line it reverses cancel out; the drafts only read the ledger,
so a month the owner accounting has not posted yet has no commissions to invoice, and the answer says so: `ledgerPosted` false), in the currency of the stay
and **in euro only**: a commission in another currency is not converted by a guess, it is reported (`currency_not_supported`). Only the stays that are **active now** are integrated (the one
definition, `AlloggiatiStatusRules.ActiveBookingStatuses` and no no-show): the owner ledger is posted by hand, so the line of a stay cancelled or marked no-show after the posting is alive until the
next posting, and the drafts and the issue leave it out as `stay_not_active`. One line for each property
(`Commissioni di intermediazione {portal} - {property} - {MM/yyyy}`); a property whose commissions net to nothing has none. The VAT is **integrated at `SelfBilling:VatRatePercent`
(⚖, 22)** on top, through `FiscalDocumentTotals.FromNet` (cent-accurate, the cent of difference on the largest line), whatever the regime of the holder (a flat-rate regime integrates
the VAT too): it never comes from the tax rules of the sales documents. The part of the amount that comes from a rule of the agency or from the default of the channel, not from the
figure of the portal (`OwnerReconciliationRules.SourceOf`), is the **estimated amount** (`estimatedAmount`, in the answer of the drafts and in the detail of the draft, recomputed from the ledger as the issue does): check it against the invoice of the
portal before issuing. The issue refuses a draft with an estimate (422 `self_billing_estimate_unconfirmed`, the figure in `estimatedAmount`, nothing numbered) until the host accepts it with
`acceptEstimate: true`, because an issued TD17 is not corrected from here.

**Portals.** The cedente is the legal entity of `SelfBilling:Portals` (channel, name, ISO country, VAT number, street, city; ⚖ verify them against a real invoice of the portal before the
first issue). Committed: Booking.com B.V. and Airbnb Ireland UC. A channel without an entry (Expedia, Vrbo, TripAdvisor, Agoda...) is **never** invoiced by a guess: its commissions are
in `notDue` with the reason `portal_not_configured` until the accountant gives the legal entity and an entry is added. An entry that is not usable (not a portal, in Italy, without a name,
VAT number or address) is ignored. Two channels of one legal entity make one TD17 (the key is the country and the VAT number).

**One live document for a holder, a portal and a month.** `POST api/fiscal/self-billing/drafts` makes the drafts of a month that is over (Europe/Rome, as the close of an owner's month: 422 `self_billing_period_not_ended` while it runs, for the issue too, because an issued TD17 is not
completed afterwards), or makes them again from the ledger while they are drafts; an
issued TD17 is left alone and the row says `changedSinceIssue` when the ledger of the month now gives another taxable amount (an issued document never changes: correct it with the
accountant); a draft the ledger no longer supports (its commissions are gone, its holder or portal changed) is discarded (no number). The generation of a month is taken one at a time
per organization (advisory lock `SelfBilling`, 1_640), so a double click never makes two drafts. The issue (`POST …/{id}/issue`, `{ confirm: true }`) refuses a draft that is not what the
ledger, the holder, the portal and the settings give today (409 `document_draft_outdated`), then goes through `IFiscalDocumentService.IssueSelfBillingAsync`: the gap-free number in
the series `SelfBilling:SeriesPattern` (⚖, `TD17-{year}`: apart from the sales documents) **of the profile of the holder**, the snapshot of the holder (with the agency as intermediary
for an owner company), the Italian XML validated against the official schema and the state `to_send`: the SDI port sends it like every Italian electronic invoice (nothing is sent here).

**Where the parties are stored.** The document keeps the foreign supplier (the cedente) in the **recipient columns** (name, VAT number with its country prefix, address, country: encrypted
like every recipient; the SDI code and the PEC are the holder's: where the file is delivered) and the holder (the cessionario, who issues) as the **issuer** (`IssuerProfileId` numbers it,
`IssuerSnapshot` keeps it as it was). The XML turns them round: `CedentePrestatore` is the supplier (`RF18`, address abroad with `CAP` `00000`), `CessionarioCommittente` the holder,
`SoggettoEmittente` `CC`, `IdTrasmittente` the agency, no related document (CasaZen does not hold the invoice of the portal). The PDF says «Autofattura (TD17)», names the two blocks
«Cessionario/committente (emittente)» and «Cedente/prestatore (fornitore estero)», prints the month and no payment state (nobody pays: the VAT is integrated). Because the supplier is in the recipient fields, the generic list and detail of the fiscal documents
(`api/fiscal/documents`) carry the `purpose` of a document (`GuestStay`, `OwnerInvoice`, `SelfBilling`) and the export of the list names the kind of a TD17 «Autofattura TD17»: it is no invoice to the
portal.

| Endpoint (`agency.invoice.manage`, agency plan, the whole organization only, `Features:FiscalDocumentIssuance`) | What |
|---|---|
| `GET api/fiscal/self-billing?period=yyyy-MM` | the TD17 of the org (drafts and issued ones), the newest month first: holder, portal, month, deadline, amounts, status, state towards the SDI |
| `GET api/fiscal/self-billing/{id}` | one TD17 with its lines and, for a draft, `estimatedAmount` |
| `POST api/fiscal/self-billing/drafts` `{ period }` | the drafts of the month: counts, `ledgerPosted` (the owner ledger has lines for the month), one row for each TD17 (`created`, `updated`, `already_issued`, `estimatedAmount`, `changedSinceIssue`), `failed` (a holder that needs one and cannot get one: `owner_issuer_missing`, `issuer_profile_incomplete`, with the fields), `notDue` (the commissions left out and why: `portal_not_configured`, `holder_private_person`, `holder_not_italian`, `holder_not_vat_registered`, `currency_not_supported`, `not_a_portal`, `stay_not_found`, `stay_not_active`) |
| `POST api/fiscal/self-billing/{id}/issue` `{ confirm: true, acceptEstimate? }` | the issue, after the confirmation (and the acceptance of the estimate when part of the amount is one) |
| `DELETE api/fiscal/self-billing/{id}` | the discard of a draft |

The PDF and the XML are the ones of every document (`GET api/fiscal/documents/{id}/pdf` and `/xml`, a document without a booking: the whole org only). The endpoints of the guest documents
(`api/fiscal/documents`) neither make again, issue, credit nor discard a TD17: 422 `document_self_billing`. A caller who reaches only some properties is refused on every action (403); a
document of another org, or one that is not a TD17, is 404.

**Not in this slice** (so it is not silently missing): a credit note or a correction of an issued TD17 (it is not credited from here: the accountant decides how to correct it); the posting
of the integrated VAT to the owner ledger (the TD17 is neutral for the holder: the VAT is due and deductible); the invoice number and date of the portal as the related document; the
submission to the SDI (not made by the issue: the document is `to_send` and the channel of *SDI submission, outcomes and correction* above sends it like every Italian electronic invoice); the accountant package and the VAT register (E15, E19). No migration: nothing is stored beyond the existing columns of
`FiscalDocuments`. **Rollback:** switch `Features:FiscalDocumentIssuance` off (the endpoints answer 404) or empty `SelfBilling:Portals` (nothing is made: 422 `self_billing_not_configured`); an
issued TD17 stays what it is (ten years).

## Export of the list (`FiscalDocumentExports`, `XlsxWriter`)

- `GET api/fiscal/documents/export?format=csv|xlsx`: the issued documents (CasaZen's and the imported ones) that the caller reaches, the same rows as the list, in a
  file named `documenti-yyyy-MM`; the same fourteen columns in both formats: kind, number, issue date, booking code, property, recipient, taxable base, VAT, stamp
  duty, total, currency, payment state, SDI state, source.
- The amounts are the stored ones, to two places (a credit note keeps its positive amounts: its kind says what it is); the payment state is the current one, as in the
  list. The recipient is the name only: no fiscal code, VAT number or address leaves in a file. Drafts, discarded drafts, the lines and the XML are not in it, and the
  channel manager is never named (its documents say «Emesso dal tuo channel manager»). A self-billed invoice (TD17) says «Autofattura TD17» in the kind column (`Purpose = SelfBilling`):
  its recipient column is the foreign supplier it integrates, not a customer.
- The Excel file is written by `XlsxWriter` (no package, only `System.IO.Compression`): amounts are numbers, days are dates, every text is an inline string, so a text
  that starts with `= + - @` is never run (the CSV puts an apostrophe before it). More than 9,999 documents is 422 `csv_too_many_rows` or `xlsx_too_many_rows`: narrow the period.

## Acceptance scenario of Switzerland (E19/F01/US01/T01)

`Casazen.Tests/Integration/Acceptance/SwissAgencyScenarioTests.cs` drives the whole chain of an agency of Lugano through the real code, from the booking to the wire
file, and asserts every amount to the cent. The setup is `AcceptanceScenarioBuilder.cs`: each step opens a scope of its own (a context and the services over it, as a
request or a job does) on one database, in memory for every test and on PostgreSQL in CI for the main path (`[PostgresFact]` twin). Only the external services are
stand-ins: the client of Octorate, Stripe, the e-mail queue (it records what it is given), the police (Alloggiati) and the push notifications; the Italian XML builder is
a strict stand-in, never called, since a Swiss document has no XML.

**The agency** (plan Agency, flags `FiscalDocumentIssuance`, `OwnerInvoicing`, `AccountantPackage`, `AgencyTier` on, the configuration shipped in `appsettings.json`):
an issuer of Switzerland registered for the VAT (UID, IBAN, QR-IBAN) saved with `SaveSettingsAsync` as the wizard «Fatturazione» saves it, with the automatic issue and
the e-mail to the guest on and Airbnb collecting the tourist tax of Ticino; one flat in Lugano (CHF, BFS 5113) owned by a private owner with a mandate of 20% «+ IVA» and a
cleaning flat fee of CHF 80; the rule of the agency for Booking.com, 15%; the listing mapped on the channel manager; the account of Booking.com held by the owner (V1)
or, in its variant, by the agency (V2).

**The four origins** (one test each, `Origin_*`, the whole chain in one test; the main path has the four in one month):

| Origin | How it comes in | Document of the guest (CHF) | Statement of October |
|---|---|---|---|
| Booking.com through Octorate | the import of the channel manager: 450.00, commission 67.50 in the reservation, collected by the portal | stay 450.00 at 3.8% (VAT 16.47), tourist tax of Ticino 2 × 3 × 2.00 = 12.00 outside the VAT: 462.00, «Pagata tramite il portale» | gross 450.00, channel fee −67.50 (payload), commission −90.00, VAT of the invoice −13.77, cleaning −80.00: 198.73 |
| Agency's own site | the «direct» channel of Octorate: 390.00 + 12.00 of tourist tax, paid by bank transfer, recorded by hand | 390.00 at 3.8% (VAT 14.28) + 12.00: 402.00, paid | gross 390.00 (the payment less its tax), commission −78.00, VAT −12.80, cleaning −80.00: 219.20 |
| CasaZen site | a booking paid with Stripe (420.00, fee 12.48 captured from the balance transaction, borne by the agency) | stay 360.00 and cleaning 60.00 at 3.8% (VAT 15.38) + 12.00 of tourist tax: 432.00, **to collect** | gross 420.00 (on the day of the payment), commission −84.00, VAT −13.28, cleaning −80.00: 242.72 |
| By hand | a booking entered by the agency, 300.00 + 12.00, paid in cash | 300.00 at 3.8% (VAT 10.98) + 12.00: 312.00, paid | gross 300.00, commission −60.00, VAT −11.34, cleaning −80.00: 148.66 |

Each document is drafted and issued by the daily run of the day after the check-out (numbers `2026/1`… in the order of the stays), e-mailed to the guest with its link,
and its PDF is the Swiss layout. The month is then posted, its version generated, the invoice of the property manager made from it and issued (`PM2026/1`, commission
and recharges at 8.1%, its VAT posted to the ledger and the version generated again with it), the version approved and published (its PDF in the owner portal), the
package of October built (the index lists every file with its SHA-256; the VAT of the quarter for each rate) and the owner paid with a `pain.001.001.09.ch.03` file.
The invoice is offset in the statement, so it has no QR-bill; `QrBill_OnlyWhenTheOwnerOwesTheAgency` shows the QR-bill of a month in which the owner owes the agency.
The variant V2 recharges the commission of Booking.com on the invoice of the property manager and lists it in the VAT file of the agency for information.
The variant of the portal that collects the tax (an Airbnb reservation imported from Octorate, Airbnb in `PlatformCollectedTouristTaxChannels`) bills the stay only:
no line of the tax of Ticino on the document, the gross without it, nothing in the box of the tax of the statement nor in the tax of the agency summary.

**The changes (B12)**: a date change (the guest stays two nights more: a credit note of the whole document and a new document, issued at once), a cancellation with a
full Stripe refund (a credit note of the whole document; nothing posted for the stay) and a no-show with a deposit kept (a credit note of the difference, the VAT split in
proportion to the lines; the deposit is the revenue of the owner, without a cleaning fee).

**The wire file** is checked against the stand-in schema of the tests; `Wire_ThePayoutFile_ValidatesAgainstTheOfficialSwissSchema` validates it against the official
`pain.001.001.09.ch.03.xsd` and is reported as skipped, with its reason, until the file is added (`docs/runbooks/wire-transfers.md`).

**Fixed with the scenario:** the statement read every listing as V1 (the resolver of E14 was not wired into `OwnerStatementDocuments`), so the statement of a V2
listing told the owner that the portal billed it while the invoice of the property manager recharged the commission. The channel fee that the invoice recharges (the
agency holds the account and recharges, the test of the invoice) is now «Commissioni del portale riaddebitate dal gestore (fattura n. …)», in the published PDF and on
the owner portal (`Variant_V2_*`, read as the owner reads it: the request of an owner has no org, and the resolver reads the holders whatever the tenant of the
caller); an account of the agency whose commissions it does not recharge never names the invoice (`Variant_V2_WithoutTheRecharge_*`).

**What the scenario shows** (facts of the code as it is, for the decisions of the PO and of the accountant):

- A Swiss document needs the name of its recipient: the invoice data of the booking when it has a name, else the name of its guest (FIX/DOC-RECIPIENT, the rule «Recipient»
  of the tax rules). A stay of a portal comes with none (no answer at the check-in, nothing typed by the agency, no pre-fill from the channel manager), so the Booking.com
  origin of the scenario gets its document from the daily run made out to the guest of the booking, by name only (asserted, with no other field of the guest on it); the
  other origins have the agency give the name on the booking card. Before this fix such a stay got no document from the daily run (`document_invoice_data_missing`, skipped
  and read again at the next run).
- The checkout of the CasaZen site in Switzerland does not collect the tourist tax yet: the document carries the line of Ticino and stays «Da incassare» although the
  stay is paid (analisi-tecnica §5bis.3, second wave).
- A credit note for the difference (a no-show, a partial refund) is spread over the lines of the document in proportion, the tourist tax included: the part that stays
  (the deposit kept) keeps a share of the tourist tax line (⚖ I12). The ledger of the owner does not follow it: a stay that owes no tourist tax (a booking that is
  not active, or a no-show, `AlloggiatiStatusRules`) keeps none of a payment as tax, so the deposit kept is all revenue of the owner, as the box of the tax of the
  statement leaves the stay out (fixed with the scenario, `OwnerAccountingService` and the reconciliation of the month).
- The document of a booking of a portal is always «Pagata tramite il portale», also for the line of the tourist tax of Ticino that Booking.com does not collect and
  that nobody recorded (`Origin_BookingCom_*`, the assertion is marked): left to a fix of its own (the part of the tax to collect at the property).

## Acceptance scenario of Italy (E19/F01/US02/T01)

`Casazen.Tests/Integration/Acceptance/ItalianAgencyScenarioTests.cs` is the twin of the Swiss scenario: the same builder (`AcceptanceScenarioBuilder.CreateItalianAsync`), each step in a
scope of its own (a context and the services over it, as a request or a job does) on one database, in memory for every test and on PostgreSQL in CI for the main path
(`[PostgresFact]` twin). It drives an agency of Como through the real code with two flats, from the booking to the documents of the month and of the year, and asserts every
amount to the cent. Only the external services are stand-ins: the client of Octorate, Stripe, the e-mail queue, the police (Alloggiati), the push notifications and the SDI (no
provider is configured: `UnconfiguredSdiEInvoiceProvider`). The XML builder is the real one, with the official schema, and every XML the scenario makes is validated against it again.

**The agency** (plan Agency; the flags `FiscalDocumentIssuance`, `OwnerInvoicing`, `AccountantPackage` and `AgencyTier` as the configuration shipped in `appsettings.json` has
them, and `SdiSubmission` and `TaxReportingExports` turned on by the scenario): an issuer of Italy with the ordinary VAT regime (RF01) saved with `SaveSettingsAsync` as the wizard «Fatturazione» saves it (its own series
`{year}`, the invoices of the property manager `PM{year}`, the cleaning billed as a service, the automatic issue and the e-mail to the guest on, no portal collecting the
tourist tax); two flats in Como under a mandate with representation (commission of 20% «+ IVA»):

| | Flat A | Flat B |
|---|---|---|
| Owner | a private person (fiscal code), the issuer profile `PrivatePerson` with its sezionale `RIC{year}` and the reference of the stamp duty | a company (a Srl: VAT number and fiscal code), the issuer profile `VatOrdinary` with its sezionale `SRL{year}`; the stay at 10%, or exempt N4 in the variant |
| Documents of a stay | the **receipt** in the owner's name (the agency issues for it), the stamp duty of 2.00 over 77.47 borne by the owner, the tourist tax on its own line (N1) | the **FatturaPA** of the company (cedente), the agency its third-party issuer (`TZ`) and transmitter, the VAT of 10% on the stay, the tourist tax N1 outside the VAT |
| Cleaning | recharged at the cost of the cleaning request (⚖ I13: a recharge for each request, with its reference) | a flat fee of 70.00 for each stay |
| Withholding | 21%, code 1919, when the agency collected and the portal did not withhold | none, with its reason (`not_qualifying_short_let`: a company is no short let of a private person) |
| Booking.com account (V1) | the owner's: no TD17 (the portal charged the Italian VAT) | the company's: the TD17 of the month |

The cleaning the agency bills the guest is its own invoice (22%, its own series) beside the document of the owner; the tourist tax of Como is host-collected for every channel
(a rule of the comune), except Airbnb in its variant. Flat B's stay ends a day after flat A's in every origin and the daily run of each day makes the documents of its own
flat, so the numbers of the invoices of the agency, which both flats share, are in a fixed order.

**The four origins** (one test each, `Origin_*`, both flats in one test; the main path has the four in one month, on both flats):

| Origin | Flat A (receipt) | Flat B (invoice of the company) | Statement of October, flat A: gross, channel fee, commission, VAT of the invoice, cleaning → net | Withholding of flat A, F24 |
|---|---|---|---|---|
| Booking.com through Octorate (450.00, commission 67.50, tax 12.00, collected by the portal) | 450.00 + 12.00 = 462.00, stamp 2.00, «Pagamento: pagato» | 409.09 + VAT 40.91 + 12.00 = 462.00 | 450.00, −67.50, −90.00, −30.80, −50.00 → 211.70 (flat B: −35.20, −70.00 → 187.30) | none (the portal pays the owner): the contract is in the communication of 30 June |
| Agency's own site (the «direct» channel, 402.00 by bank transfer recorded by hand) | 390.00 + 12.00 = 402.00 | 354.55 + VAT 35.45 + 12.00 | 390.00, 0, −78.00, −28.16, −50.00 → 233.84 (B: 209.44) | 81.90 → paid 151.94 |
| CasaZen site (Stripe 432.00, fee 12.48 borne by the agency) | 360.00 + 12.00 = 372.00, and the invoice of the agency for the cleaning 49.18 + VAT 10.82 = 60.00 | 327.27 + VAT 32.73 + 12.00 = 372.00, and the cleaning of the agency | 420.00, 0, −84.00, −29.48, −50.00 → 256.52 (B: 232.12) | 88.20 → paid 168.32 |
| By hand (312.00 in cash) | 300.00 + 12.00 = 312.00 | 272.73 + VAT 27.27 + 12.00 | 300.00, 0, −60.00, −24.20, −50.00 → 165.80 (B: 141.40) | 63.00 → paid 102.80 |
| The four in one month | `RIC2026/1`…`4`, `2026/1` and `2026/2` for the cleaning | `SRL2026/1`…`4` | 1'560.00, −67.50, −312.00, −112.64, −200.00 → 867.86 (B: −130.24, −280.00 → 770.26) | 233.10 on a base of 1'110.00 (the three stays the agency collected) → 634.76; F24 of November 233.10, due 16 December |

Each document is made by the daily run of the day after the check-out and e-mailed to the guest with its signed link; its PDF is the Italian layout (the owner as issuer and the agency
«emessa tramite … per conto del proprietario», the reference that pays the stamp duty, the courtesy copy of the e-invoice). The invoices are `to_send` to the SDI: a submission with
no provider answers «not configured» and changes nothing, and the issuer declares what it sent by hand (`manual_declared`). The month is then posted for both owners, each version
generated, the invoice of the property manager made from it (22%, a commission line for the flat and a recharge for each cleaning, a FatturaPA to the owner: the fiscal code of the
private owner, the VAT number of the company) and issued, the version approved and published (its PDF in the owner portal). The TD17 of the month (`TD17-2026/1`, 67.50 + VAT 14.85 =
82.35 for the company that holds the Booking.com account) is made on the third of November, when the month is over. The owners are paid with a `pain.001.001.09` file (euro).

**The year end** (read in January 2027): the CU of the private owner (its base and its withholding, who it is made out to, the stays), the 770 (the F24 of the month with its
figures), the communication of the contracts of 30 June (the stay nobody withheld on: Booking.com in V1, Airbnb with its reason in the variant), the declaration of the tourist tax
per comune and the account of the agent (the stays, the tax owed, the payment of 8 January that settles it). Each is read twice: as the data (the records of the CU, the lines of the
770, the rows of the communication, the comuni of the declaration) and as the CSV a spreadsheet set to Italian opens (`TaxReportingExports.CuCsv`, `Tax770Csv`, `ContractsCsv`,
`TouristTaxYearEndExports.DeclarationCsv`: `;` between the cells, a decimal comma, a day as `dd/MM/yyyy`). When the agency withheld nothing there is no CU, 770 or F24 (422
`tax_no_obligations`), never a file of zeros.

**The variants**:
- **V2** (the agency holds the Booking.com account of both flats): the commission comes from the rule of the agency (15%), the invoice of the property manager recharges it
  («Riaddebito commissioni Booking.com») and so does the statement; the TD17 is the agency's (one for the two stays, 135.00 + VAT 29.70, the estimate accepted at the issue), none for the owners;
  the portal now pays the agency, so the agency withholds 21% of the stay of the private owner (94.50).
- **Airbnb** (the portal collects the tourist tax of Como and withholds for a private Italian host, ⚖ R5): no line of the tax on the receipt, nothing in the box of the statement,
  no withholding of the agency («already withheld by the portal»: never twice), and the contract in the communication of 30 June.
- **Flat B exempt N4** (⚖ I1): the stay N4 and the tourist tax N1 are outside the VAT and over 77.47, so the invoice of the company carries the stamp duty of 2.00 (`DatiBollo`).
- **Co-owners 60/40** (⚖ I6): one receipt on the profile of the co-owner with the largest share, listing both with their shares; each owner has its own statement, invoice, payout and
  withholding on its share (37.80 and 25.20, one F24 of 63.00); the mandate says the CU is per share, so there is a record for each co-owner.
- **Company tenant** (⚖ I7): the warning is raised once; while nobody decides, the regime does not change by itself (the preview still withholds and says the decision is pending); the
  decision of the agency (the regime and its reason, by whom) is recorded once and the payout then withholds nothing on the stay.
- **A third unit** (⚖ I8): two apartments declared outside the agency make three, one over the limit: the statement carries the flag `short_let_business_presumed` and the payout
  withholds nothing, the facts saying it is the owner.

**The changes (B12)**: a cancellation with a full Stripe refund after the documents were issued at once: the receipt is reversed by a credit note of its own kind and series (no XML),
the invoices of the company and of the agency by a TD04 each (linked to the credited invoice, valid against the schema), and nothing is posted for the stays in October.

**The wire file** is checked against the stand-in schema of the tests; `Wire_ThePayoutFile_ValidatesAgainstTheOfficialIsoSchema` validates it against the official `pain.001.001.09.xsd`
and is reported as skipped, with its reason, until the file is added (`docs/runbooks/wire-transfers.md`).

**What the scenario shows** (facts of the code as it is, for the decisions of the PO and of the accountant):

- The document of a booking of a portal is always «Pagamento: pagato», also for the line of the tourist tax of Como that Booking.com does not collect and nobody recorded (the same
  residual defect as in Switzerland; the assertion is marked).
- The base of the withholding of a stay is the gross of the ledger without the tourist tax, so the stay of the CasaZen site is 420.00 with the cleaning of 60.00 that the agency bills the
  guest as its own service (a document of the agency, not of the owner): the 21% is taken on the 420.00 (⚖ R3, the base of the commission is the same).
- The accountant package has no sales-register row for the commission of a portal the agency holds the account of in Italy (the Swiss file has one, for the purchase tax): the TD17 is
  its document. The TD17 itself is not in the package of the month (its file is the sales documents and the invoices of the property manager).
- The numbers of the documents of the agency that two flats share depend on the order the daily run reads two stays that ended on the same day (the id of the booking): the scenario ends
  the stays of the two flats on different days so that the numbers are fixed.


## Retention and privacy

- A fiscal document is kept ten years (ADR-033-R7): the booking, the property, the owner, the statement version, the credited and the replaced document
  are restrict foreign keys; the erasure of a guest leaves the recipient snapshot of an issued document in place.
- The recipient snapshot, the issuer snapshot and the XML are encrypted at rest (`Casazen.FiscalDocument`, `docs/runbooks/encryption.md`); so are the
  IBAN and the QR-IBAN of the issuer and the personal data of every issuer profile, the owners' included (`Casazen.OrgFiscalIssuerProfile`: legal name, fiscal
  code, VAT number, street, house number, postal code, city, PEC). The logs carry ids, codes and field names, never a value.
