# Runbook: legal documents and subprocessors

Task PL-14 (audit defects A1-06, A9-40). Before it the hosts accepted "Termini di Servizio v2026-06-v1" without any
way to read them (no text, no page) and the published subprocessor list was a hand-written list that did not match the
code (SendGrid instead of Resend, Auth0 "EU" on a US tenant, Expo, Vercel and the AI provider missing).

> **Bozza redatta da un agente AI su incarico del PO: richiede revisione di un legale prima dell'uso in produzione.**

Decision D14 was updated on 2026-10-01: the product owner delegated the drafting of the Terms of Service, Privacy notice
and DPA to an agent (task LEGAL-TEXTS, version `2026-10-v1`, [section 5](#5-drafts-2026-10-v1-task-legal-texts)).
The code never writes a text: it serves the file of the **configured** version. Until a text exists for the configured
version, or while a value the text needs is not configured (fail-closed, [section 6](#6-fail-closed-health-check-and-startup-log-decision-d9)),
the public page says "in preparazione" and the consent still refers to the configured version.

## What exists

| Piece | Behaviour | Code |
|---|---|---|
| `GET /api/legal/{tos,privacy,dpa}?lang=it\|en` | Anonymous. `version`, `effectiveAt` (null while not configured), `available`, `contentHtml` (sanitized text of the current version, null while the file is missing or a value it needs is not configured), `contentLanguage` (the requested language, or `it` when no translation exists), optional `documentUrl` (external copy) | `LegalController`, `LegalDocumentService` |
| `GET /api/legal/subprocessors` | Anonymous. The providers the running configuration actually uses, see below | `LegalSubprocessorCatalog` |
| Web pages | `/legale` (index), `/legale/termini`, `/legale/privacy`, `/legale/dpa`, `/legale/sub-responsabili`: version, date in force, text or "in preparazione", error state with retry | frontend `src/features/legal/*` |
| Footer | Privacy and Terms on every public page; subprocessors and the index on the CasaZen pages. On a host booking site (`/book/*`) the links open in a new tab | frontend `Footer.tsx` |
| Onboarding consents step | Every checkbox links its page (new tab) and says "testo in preparazione" while the text is missing | frontend `consents-step.tsx` |
| Re-acceptance | A new Terms / Privacy / DPA version locks the host features until the host accepts it again (PL-02) | runbook [`onboarding-consents.md`](onboarding-consents.md) |

## 1. Text files: format and placeholders

Format: an **HTML fragment** (no `<html>`, `<head>` or `<body>`) using only `p`, `br`, `h2`, `h3`, `h4`, `ul`, `ol`,
`li`, `strong`, `em`, `a` (absolute `https`/`mailto` links only), `table`, `thead`, `tbody`, `tr`, `th`, `td`. Anything
else is removed by the sanitizer (`SeoHtmlSanitizer` on the API, DOMPurify again in the browser): styles, classes,
images and scripts never reach the page. The page title comes from the web app, so the file starts with the first
section (`<h2>`). A Word document can be converted with any "save as HTML" and then cleaned by hand; check the result on
the test environment. An HTML comment at the top of the file is metadata: the sanitizer drops it, readers never see it
(the drafts use it for the "bozza redatta da un agente AI" notice, version and rules).

**Placeholders.** A file may contain `{{Token}}` (replaced by a value of the running configuration, HTML-encoded) and
`{{#if Token}}…{{/if}}` / `{{#if Token}}…{{#else}}…{{/if}}` (a part shown only when the token has a value; not
nestable). The tokens are defined in `LegalVariables` (table in [section 5.2](#52-variables)). A token used outside a
condition that has no value, an unknown token or a malformed placeholder keeps the whole document **unpublished**
([section 6](#6-fail-closed-health-check-and-startup-log-decision-d9)): a placeholder is never shown to a reader.

File path, in the backend repository:

```
Casazen.Web/LegalDocuments/{tos|privacy|dpa}/{version}.{it|en}.html
```

- `{version}` is exactly the configured version (`Legal:Documents:{Tos|Privacy|Dpa}:Version`), letters, digits, `.`,
  `_`, `+`, `-` only (e.g. `2026-11-v1`). Another version's file is never served.
- The Italian file is the reference. The English one is optional: without it an English visitor reads the Italian text
  with the notice "This document is available in Italian only".
- The files are copied to the API output (`Casazen.Web.csproj`) and read once per process: a new text needs a deploy.
- **Never change or delete the file of a version that has been accepted by someone**: it is the proof of what the host
  accepted (GDPR art. 7). A change is a new version (section 2).
- The folder can be moved with `Legal__ContentPath` (absolute, or relative to the API binaries); the default is
  `LegalDocuments`.

Optional: `Legal__Documents__{Tos|Privacy|Dpa}__DocumentUrl` (absolute `https`) links an external official copy (for
example a signed PDF) from the page; with a URL and no file the page shows only the link.

## 2. Publish a new version

1. Agree with the product owner the new version name (e.g. `2026-11-v1`) and the date it enters into force.
2. Add the file(s) of section 1 with that name, in a pull request to `develop` (GitHub flow).
3. In the same pull request set in `Casazen.Web/appsettings.json`:
   - `Legal:Documents:{Kind}:Version` = the new version;
   - `Legal:Documents:{Kind}:EffectiveAt` = the date in force (ISO, e.g. `2026-11-15`, read as UTC midnight and shown
     as a calendar date in Europe/Rome).
   Railway variables (`Legal__Documents__Tos__Version`, `…__EffectiveAt`) override these values per environment: remove
   stale overrides, or set them on both environments.
4. Merge, then on the **test** environment check `GET /api/legal/{kind}` (`available: true`, right `version` and
   `effectiveAt`) and the page `/legale/...`.
5. Communicate the change to the hosts **before** promoting to production: from the deploy every host is sent to the
   consents step until they accept the new version (PL-02, `onboarding-consents.md`).
6. Release to production (release PR `develop` → `main`).

The first publication of the real texts follows the same steps: the current `2026-06-v1` versions have no text, so
publishing the texts **with a new version** makes every host accept them knowingly.

The drafts of task LEGAL-TEXTS (`2026-10-v1`) are shipped with the API but **not activated by `appsettings.json`**
(whose versions stay `2026-06-v1`): they are activated per environment with the Railway variables of
[section 5.3](#53-activation-and-what-the-hosts-see), because changing a version asks every host to accept it again.

## 3. Subprocessors (GDPR art. 28)

The list is computed from the configuration in use, never typed by hand:

| Provider | Listed when | Location (`region`) |
|---|---|---|
| Supabase | `ConnectionStrings:DefaultConnection` host on `supabase.co`/`supabase.com` (database) and/or `Storage:S3:ServiceUrl` on Supabase with a provider other than FileSystem (file storage) | the AWS region of the pooler host (`aws-0-eu-west-1.pooler.supabase.com` → `eu-west-1`), else `Storage:S3:Region`, else configuration |
| Auth0 | `Auth0:Domain` set | from the tenant domain (`Auth0:ManagementApiDomain`, else `Auth0:Domain`): `{tenant}.{us,eu,au,jp,uk,ca}.auth0.com` → that region, legacy `{tenant}.auth0.com` → `US`. A custom domain alone says nothing: configuration |
| Stripe | `Stripe:SecretKey` set | configuration |
| Resend | `Email:ApiKey` (or the legacy `Email:ResendApiKey`) set | configuration |
| Expo | always (every push notification goes through the Expo push service) | configuration |
| Railway | `…:Providers:Railway:Enabled` true, or a Railway variable present (`RAILWAY_PROJECT_ID`, `RAILWAY_ENVIRONMENT_NAME`, `RAILWAY_GIT_COMMIT_SHA`) | `RAILWAY_REPLICA_REGION` when Railway sets it, else configuration |
| Vercel | `…:Providers:Vercel:Enabled` true (default in `appsettings.json`: the web app is hosted on Vercel), or `App:PublicSiteBaseUrl` on `*.vercel.app` | configuration |
| AI provider (DeepSeek) | `Ai:Provider=DeepSeek` with `Ai:ApiKey` (the list version gets `+ai-deepseek`) | `Ai__Subprocessor__*`, runbook [`ai.md`](ai.md) |

A location read from the provider's own configuration wins over a configured one (it is what the code talks to).

Legal details that the code cannot deduce are configured, per provider (Railway variables or `appsettings.json`):

```
Legal__Documents__Subprocessors__Providers__{Supabase|Auth0|Stripe|Resend|Expo|Railway|Vercel}__Entity             legal entity and registered office
Legal__Documents__Subprocessors__Providers__{…}__Region                                                            processing location, when not deduced
Legal__Documents__Subprocessors__Providers__{…}__TransferMechanism                                                 legal basis of a transfer outside the EEA (GDPR chapter V), or the statement that there is none
Legal__Documents__Subprocessors__Providers__{…}__Source                                                            official URL(s) of the three details above and the consultation date (not shown to users)
```

While `Entity`, the location or `TransferMechanism` is missing the entry has `detailsPending: true`: the page shows
"in definizione" and the onboarding "sede e base giuridica del trasferimento in corso di definizione". Take these
facts only from the provider's official documents (DPA, subprocessor and data-location pages) or from the contract,
and cite them in `Source` (a test, `SubprocessorDetailsConfigurationTests`, fails for a committed detail without an
official URL and a consultation date).

### Public facts committed in `appsettings.json` (PL14-SUBP, consulted 2026-10-01)

The defaults of `Casazen.Web/appsettings.json` carry what each provider **publicly declares** about its contracting
entity, the location of the processing and its transfer mechanism. Nothing that depends on a choice of the product
owner is written there: the project region of Supabase, the tenant region of Auth0 and the service region of Railway
stay empty and are read from the running configuration (see the table above); until they can be read the entry stays
"in definizione". A Railway variable overrides any committed value (e.g. after a change of contract).

| Provider | Contracting entity and registered office (as declared) | Location (as declared) | Transfer mechanism (as declared) | Official sources |
|---|---|---|---|---|
| Supabase | Supabase Pte. Ltd., 65 Chulia Street #38-02/03, OCBC Centre, Singapore 049513 (Terms of Service; data importer of the DPA) | **Not written**: Postgres, Auth and Storage objects stay in the region chosen for the project | EU SCCs (Implementing Decision (EU) 2021/914, modules 2 and 3) incorporated in the DPA; a Transfer Impact Assessment is published | [DPA](https://supabase.com/legal/customer-resources/data-processing-addendum), [Terms](https://supabase.com/terms), [Regions](https://supabase.com/docs/guides/platform/regions), [TIA](https://supabase.com/downloads/docs/Supabase+TIA+250314.pdf) |
| Auth0 | Okta, Inc. (Delaware), 100 First Street, San Francisco, California 94105: Auth0 is sold under the Okta Master Subscription Agreement and the Okta DPA covers Auth0 | **Not written**: the tenant region, read from the tenant domain (`{tenant}.eu.auth0.com` → `EU`) | EU-U.S. Data Privacy Framework (Okta, Inc. and Auth0 LLC) plus EU SCCs incorporated in the DPA | [Okta DPA](https://www.okta.com/dpa), [Okta privacy policy](https://www.okta.com/legal/privacy-policy/), [Okta MSA](https://www.okta.com/sites/default/files/2025-02/MSA_Q1FY26_Online_Terms.pdf) |
| Stripe | Stripe Payments Europe, Limited, 1 Grand Canal Street Lower, Grand Canal Dock, Dublin, D02 H210, Ireland (contracts and processes for Europe) | Global: Stripe, LLC in the United States and affiliates/sub-processors in other jurisdictions | EU-U.S. Data Privacy Framework (Stripe, LLC) and EEA SCCs modules 1, 2 and 3 in the Data Transfers Addendum | [DPA](https://stripe.com/legal/dpa), [Data Transfers Addendum](https://stripe.com/legal/dta), [Legal entities](https://stripe.com/legal/stel), [Service providers](https://stripe.com/legal/service-providers) |
| Resend | Plus Five Five, Inc. (Resend), 2261 Market Street #5039, San Francisco, CA 94114 | United States: message content, delivery logs, webhook payloads and account records, whatever the sending region of the domain (it routes the mail, it does not move the stored data) | EU SCCs (modules 2 and 3) incorporated in the DPA, plus participation in the EU-U.S. Data Privacy Framework | [DPA](https://resend.com/legal/dpa), [GDPR page](https://resend.com/security/gdpr), [Terms](https://resend.com/legal/terms-of-service), [Regions](https://resend.com/docs/dashboard/domains/regions) |
| Expo | 650 Industries, Inc. (Expo), 624 University Ave, FL1, Palo Alto, CA 94301 | United States (Expo states it transfers EU data to the US) | EU-U.S. Data Privacy Framework (self-certified) and EU SCCs module 2 as stated in its GDPR page | [Privacy policy](https://expo.dev/privacy), [GDPR](https://docs.expo.dev/regulatory-compliance/gdpr/), [DPF](https://expo.dev/changelog/2024-12-17-dpf-replaces-privacy-shield) |
| Railway | Railway Corporation (Delaware), 548 Market St PMB 68956, San Francisco, California 94104 (data importer of the DPA) | **Not written**: the deploy region of the service (US West, US East, EU West Amsterdam, Southeast Asia); volumes follow the region of their service | EU SCCs (modules 2 and 3) incorporated in the DPA, disputes before the courts of Ireland (clause 18) | [DPA](https://railway.com/legal/dpa), [Terms](https://railway.com/legal/terms), [Regions](https://docs.railway.com/deployments/regions) |
| Vercel | Vercel Inc. (Delaware), 340 S Lemon Ave #4133, Walnut, CA 91789 | United States for the primary processing facilities, and anywhere Vercel or its sub-processors operate | EU-U.S. Data Privacy Framework (certified) and EU SCCs plus UK Addendum in the DPA | [DPA](https://vercel.com/legal/dpa), [Compliance](https://vercel.com/docs/security/compliance), [DPF](https://vercel.com/kb/guide/is-vercel-certified-under-dpf) |
| DeepSeek (only with `Ai__Provider=DeepSeek` and a key, [`ai.md`](ai.md)) | Hangzhou DeepSeek Artificial Intelligence Co., Ltd. and Beijing DeepSeek Artificial Intelligence Co., Ltd., registered in China (street address not stated in the text consulted) | People's Republic of China (personal data collected, processed and stored there) | **None declared**: only "appropriate safeguards … where required". Left empty on purpose | [Privacy policy](https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html), [Terms](https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html) |

**How these were verified, and what the product owner still has to do.** The agent session that prepared this table could
not open the providers' own domains (`supabase.com`, `stripe.com`, `okta.com`, `resend.com`, `expo.dev`, `railway.com`,
`vercel.com`: blocked by the network egress proxy, not worked around). The facts were taken from the excerpts of the same
official pages returned by a web search restricted to those domains; the page URLs are the ones listed. Before setting
`Legal__Documents__Subprocessors__EffectiveAt`:

1. Open each linked page and compare entity, address, location and transfer mechanism with the table (the providers
   revise their DPAs: each page carries its own "last updated" date, to be noted next to the consultation date).
2. Decide the choices that the table deliberately leaves open: the region of the Supabase project (and of its Storage
   buckets), of the Auth0 tenant, of the Railway service, and the Vercel Function region; the sending region of the
   Resend domain only changes where mail is routed from.
3. If the list changes with respect to what hosts already acknowledged, increase `Legal:Documents:Subprocessors:Version`
   (section 3, "Changing the list"). The Italian text of the DPA / privacy notice stays with the product owner (D14).
4. Check `GET /api/legal/subprocessors` on test: no entry is `detailsPending` except the ones whose region could not be
   read.

### Changing the list

1. A new integration that sends personal data to a third party is added to `LegalSubprocessorCatalog` in the same pull
   request as the integration (with its detection rule).
2. Increase `Legal:Documents:Subprocessors:Version` (and set `…:EffectiveAt`) when the list changes. The
   acknowledgement collected by the onboarding refers to that version; it does not lock the host features.
3. The DPA (text of the product owner) states how hosts are informed of a change and can object (GDPR art. 28(2)):
   the page `/legale/sub-responsabili` alone is not a notification.

## 4. Checks

```bash
curl -s "$API/api/legal/tos?lang=it" | jq '{version, effectiveAt, available, contentLanguage}'
curl -s "$API/api/legal/subprocessors" | jq '.version, (.items[] | {key, region, detailsPending})'
```

| Symptom | Cause | Action |
|---|---|---|
| Page says "in preparazione" with the file present and the version right | A value the text needs is not configured (fail-closed) | `GET /api/health/ready` with an admin token: the `legal` description names the Railway variables to set; the startup log has the same line ([section 6](#6-fail-closed-health-check-and-startup-log-decision-d9)) |
| Page says "in preparazione" after adding the file | File name differs from the configured version, a Railway variable overrides the version, or no deploy since the file was added | Compare `GET /api/legal/{kind}` `version` with the file name; redeploy |
| English page shows the Italian text | No `{version}.en.html` | Expected; add the translation as a new file of the same version only if it is a faithful translation approved by the product owner |
| A provider is missing from the list | Its configuration is not set on that environment (e.g. no `Email__ApiKey`) | The list reflects the environment: set the configuration or, for hosting, `…:Providers:{Name}:Enabled=true` |
| Auth0 region is empty | Custom domain without `Auth0__ManagementApiDomain` | Set `Auth0__ManagementApiDomain` to the canonical tenant domain (runbook [`auth0.md`](auth0.md)) |

## 5. Drafts 2026-10-v1 (task LEGAL-TEXTS)

> **Bozza redatta da un agente AI su incarico del PO: richiede revisione di un legale prima dell'uso in produzione.**

Written on 2026-10-01 after the product owner's delegation (D14 updated in `Sessions/risanamento/DECISIONI.md`).
Italian is the reference text, English a faithful translation; the two have the same sections, paragraph numbers and
links (checked by `LegalDocumentTextsTests`). Files: `Casazen.Web/LegalDocuments/{tos,privacy,dpa}/2026-10-v1.{it,en}.html`.

| Document | Sections | Product facts it states (from the code) |
|---|---|---|
| Terms of Service (`tos`) | 17 | SaaS for hosts and landlords in Italy and for marketplace suppliers; plans from `PlanCatalog` and `Billing:Display`, Stripe subscription with the past-due grace of `Billing:PastDueGraceDays` and return to the Starter limits; guest payments are direct charges on the host's Stripe Connect account, CasaZen never holds the funds and charges no fee of its own today; compliance tools are support, not advice (CIN format check only, Alloggiati summary and record file prepared for manual upload (CO-13), RLI registered by the landlord, no authorised intermediary); automatic suspension of a property that no longer meets the activation requirements; iCal sync is periodic, double bookings are the host's risk; marketplace: direct contract host-supplier, what the supplier sees before and after accepting, "paid" is a manual annotation; seasonal suggestions are arithmetic on the host's own rules (no AI); AI only drafts public informational text, reviewed, with no personal data; suspension, withdrawal, unilateral changes with notice, liability cap, Italian law |
| Privacy notice (`privacy`) | 13 | CasaZen as controller for host, collaborator, supplier and visitor data and as processor for guest data; data categories read from the entities (account, organization and fiscal data, billing identifiers, supplier profile, consent records with IP, sign-up attribution, technical data, push token); legal bases per purpose; recipients by category with the dynamic list `/legale/sub-responsabili` (not duplicated); transfers outside the EEA by reference to that list; retention (honest about what is not configured, with the table of the periods in force); rights and complaint to the Garante; automated decisions (none solely automated; rule-based status changes disclosed); real cookie and browser storage inventory (no cookies from the API, no analytics, the sign-up attribution storage described) |
| DPA (`dpa`) | 16 | Art. 28(3) contents: subject matter and duration, nature and purpose, data types and data subjects, documented instructions, confidentiality, security measures (only what the code does, [5.6](#56-the-security-measures-the-dpa-states-and-how-to-check-them)), subprocessors with general authorisation and prior notice, transfers, assistance with data subjects' rights (the real `/api/gdpr/*` and `/api/leases/{id}/erasure-request` tools, what remains after an erasure), assistance with arts. 32-36, breach notification, retention with the periods in force, return or deletion, audits, liability |

### 5.1 What the drafts do not contain

No company datum, price, court, retention period or provider detail is written in the text: they come from the
configuration ([5.2](#52-variables)) or from the live lists. The subprocessor list is not repeated in any document
(PL-14, task PL14-SUBP): they link `/legale/sub-responsabili`. The privacy information for **guests** (CO-16) and the
terms and notice of the **host's public booking site** (BK-14) are outside these drafts.

### 5.2 Variables

Set on Railway (per environment) or in `appsettings.json`. The company data and the court have **no default**: while one
is missing every document that uses it stays unpublished. The negotiable terms have a proposed default in
`appsettings.json`, **to be confirmed by a lawyer** (they are commercial and legal choices the code cannot deduce).
Values that are empty, a placeholder (`TODO`, `YOUR_…`, `[…]`, `…`), not an email where an email is expected, or out of
range are treated as missing.

| Railway variable (`:` is `__`) | Used by | Default | Meaning |
|---|---|---|---|
| `Legal__Controller__Name` | all | none | Legal name (ragione sociale) of the company that runs CasaZen |
| `Legal__Controller__Address` | all | none | Registered office |
| `Legal__Controller__VatId` | all | none; falls back to `Billing__VatNumber` | VAT number |
| `Legal__Controller__ReaNumber` | ToS 1.1 (optional) | none | REA number, shown only when set |
| `Legal__Controller__Pec` | all | none | PEC address: notices, withdrawal, reports of unlawful content |
| `Legal__Controller__PrivacyEmail` | all | none | Contact for privacy requests (arts. 15-22), sub-processor objections, general contact |
| `Legal__Controller__DpoEmail` | Privacy 1.3 (optional) | none | DPO contact, shown only when a DPO has been designated |
| `Legal__Terms__GoverningCourt` | ToS 16.2, DPA 16.3 | none | Court with exclusive jurisdiction (e.g. `Milano`), written after "Foro di" |
| `Legal__Terms__ChangeNoticeDays` | ToS 15.1, DPA 16.1 | 30 | Notice of changes (Reg. (EU) 2019/1150 art. 3(2): at least 15 days) |
| `Legal__Terms__TerminationNoticeDays` | ToS 13.3 | 30 | Notice of CasaZen's withdrawal (Reg. 2019/1150 art. 4: 30 days) |
| `Legal__Terms__LiabilityCapMonths` | ToS 14.3 | 12 | The liability cap is the fees paid in this many months |
| `Legal__Terms__DataReturnDays` | ToS 13.4, Privacy 8.1, DPA 13.2 | 30 | Days after termination to export or delete the data |
| `Legal__Dpa__BreachNotificationHours` | DPA 12.1 | 48 | Target to notify a breach to the controller ("without undue delay", art. 33(2), and where possible within this) |
| `Legal__Dpa__SubprocessorNoticeDays` | DPA 8.3 | 30 | Prior notice of a new or replaced subprocessor (art. 28(2)) |
| `Legal__Dpa__AuditNoticeDays` | DPA 14.2 | 30 | Notice of an audit |
| `Billing__PastDueGraceDays` | ToS 3.6 | 7 (same default as `EntitlementService`) | Grace after a failed renewal |
| `App__PublicSiteBaseUrl` | all (links to `/legale/*`) | required by the app | Public domain of the web app (D3) |
| `Billing__Display__{Starter,Pro,Scale}__PriceMonthly` and `…__Name` | ToS 3.1 | 29 / 79 / 199 and the catalogue names | Plan table; a price of 0 or missing shows "the price shown at the time of purchase". The limits come from `PlanCatalog` (3, 50, unlimited) |
| `Gdpr__Retention__{Category}__Years` / `__Months` / `__Days` and `__Source` | Privacy 4.3, DPA 13.1 | none | The retention table shows a period **only** when the retention job applies it (an amount **and** a source, [`gdpr.md`](gdpr.md) § 2); otherwise it says that nothing is deleted automatically. `{Category}` is `DocumentScans`, `AlloggiatiData`, `Marketing`, `FiscalData`, `LeaseParties` |

Changing one of these values does **not** change the version: no host is asked to accept again (the gate compares the
version only). A change that alters the meaning of a version (a different court, a longer notice, a new retention
period) should instead be published as a new version with the notice of ToS 15.1.

### 5.3 Activation and what the hosts see

The deploy never activates the drafts: `Casazen.Web/appsettings.json` keeps `2026-06-v1`, which has no text. A test
(`AppSettings_DefaultVersions_DoNotActivateTheDrafts`) guards this. To activate on an environment:

1. A lawyer reviews and approves the three texts (points in [5.5](#55-points-for-the-lawyer)) and the product owner
   decides the negotiable values of [5.2](#52-variables).
2. Railway, environment `test` first: set the company variables, the court and, if different from the defaults, the
   negotiable ones; set the retention periods the owner has decided (or leave them off: the text says so).
3. Check `GET /api/legal/subprocessors`: no entry with `detailsPending: true` (the notice and the DPA refer to the list;
   PL-14 / PL14-SUBP complete it from the providers' own documents) and the AI provider only if it is meant to be on.
4. Check the DPA's security list against the code ([5.6](#56-the-security-measures-the-dpa-states-and-how-to-check-them)).
5. Set `Legal__Documents__Tos__Version`, `Legal__Documents__Privacy__Version`, `Legal__Documents__Dpa__Version` =
   `2026-10-v1` and `Legal__Documents__{…}__EffectiveAt` = the date in force (ISO date). Redeploy.
6. Check `GET /api/health/ready` with an admin token: `legal` is `healthy`; `GET /api/legal/{tos,privacy,dpa}?lang=it`
   and `lang=en` answer `available: true` with the right `contentLanguage`; read the three pages `/legale/*` on the web.
7. Tell the hosts **at least `Legal__Terms__ChangeNoticeDays` days before** the date in force (ToS 15.1).
8. Repeat on `production` only after the same checks, with the release PR as usual; the production variables are
   separate.

**Effect on the hosts (PL-02, [`onboarding-consents.md`](onboarding-consents.md)).** The gate compares the accepted
version with the configured one at every evaluation (cache of at most `Authorization:UserCacheSeconds`). From the
moment a version variable changes, every host whose organization accepted `2026-06-v1` is sent to the consents step
("Documenti legali aggiornati") and must accept the three new versions; admin and supplier areas are not affected. New
hosts accept the new versions in the onboarding. A host who already accepted `2026-10-v1` is not asked again,
whatever else changes (company data, negotiable numbers, retention table), because the version is the same. The
subprocessor acknowledgement keeps its own version and never locks the host features.

**Rollback.** Setting the variables back to `2026-06-v1` restores the old gate for hosts who accepted it, but hosts
who accepted only `2026-10-v1` in the meantime would have to accept `2026-06-v1`, a version without text: do not roll
back after the hosts have accepted; fix forward with a new version instead (section 2).

**Supplier activation.** The supplier activation checkbox ("termini di servizio fornitore") stores only a date
(`SupplierProfile.TosAcceptedAt`), with no version: a new ToS version does not ask the suppliers to accept again. The
ToS has a section for suppliers (art. 8); see the open points.

### 5.4 Sources and how they were verified

Official sites (EUR-Lex, garanteprivacy.it, normattiva) are blocked from the agents' network and were **not**
fetched: each reference below was checked through web-search results quoting the provision, on 2026-10-01 (secondary
sources). A lawyer must still confirm them against the official text before production.

| Reference | Used for | Verified |
|---|---|---|
| Reg. (EU) 2016/679 art. 28(2), 28(3)(a)-(h), 28(4), 28(10) | DPA 5-8, 10-14 | Yes (contents of 28(3), general authorisation with notice and objection, same obligations for subprocessors, processor that decides purposes is a controller) |
| GDPR art. 33(1), 33(2), 34 | DPA 12 | Yes (processor notifies the controller without undue delay; controller 72 hours; communication to data subjects at high risk) |
| GDPR arts. 12(3), 7(3), 9, 15, 16, 17(3)(b), 18, 20, 21, 22, 77, 79, 82 | Privacy 9-10, DPA 10, 15 | Yes |
| GDPR arts. 13(1)(f), 13(2)(f), 35, 36, 45, 46(2)(c), 9 | Privacy, DPA | Yes |
| GDPR arts. 6(1)(b), 6(1)(c), 6(1)(f), 7(1), 14 (one month, first communication), 32 | Privacy 3, 5, DPA 7 | Yes |
| EDPB Guidelines 07/2020 on controller and processor (version 2.0, 7 July 2021) | Roles (controller for account data, processor for guest data) | Existence and version verified; the roles follow its functional test |
| Codice privacy (D.Lgs. 196/2003) art. 122 and Garante guidelines on cookies of 10 June 2021 (provv. n. 231, GU n. 163 of 9 July 2021) | Cookie section; the lawyer's decision on the sign-up attribution storage (open point) | Verified; **not cited in the user-facing text** |
| Codice civile art. 1229, 1341 co. 2 and 1342 | ToS 14.1, 17.6 | Yes |
| Codice del consumo (D.Lgs. 206/2005) art. 3 | ToS 1.6 | Yes |
| Reg. (EU) 2019/1150 art. 3(2) (15 days), art. 4 (30 days, reasons for suspension) | ToS 17.4 and the proposed notices | Yes; whether it applies to CasaZen is an open point |
| D.Lgs. 70/2003 art. 7 (name, office, email, REA, VAT) | Why ToS 1.1 shows REA and VAT | Yes; **not cited in the user-facing text** |
| TULPS art. 109 | `.claude/context/regulations/alloggiati.md` | Not cited in the text (the ToS says "the legal deadlines") |

A test (`Documents_CitedStatutes_AreOnlyTheVerifiedOnes`) fails when a text cites an act or a civil code article that is not in
this table: add the verification here first.

### 5.5 Points for the lawyer

These are the choices the code cannot make. Each has a conservative default in the draft.

- **Consumer or business.** The ToS treat the Customer as a business and carve out consumers (1.6). A host with one
  property may be a consumer (art. 3 Codice del consumo): decide whether to keep the carve-out, offer withdrawal
  rights or refuse consumers.
- **Specific approval (arts. 1341-1342 c.c.).** ToS 17.6 lists the onerous clauses and the app records one checkbox
  for the whole ToS: practice usually asks a separate, explicit acceptance of those clauses.
- **Liability cap** (14.3: fees of the last 12 months), **notice of changes and withdrawal** (30 days), **data return
  window** (30 days), **breach notification target** (48 hours), **audit** (once a year, 30 days' notice, auditor's
  cost), **subprocessor notice** (30 days with objection and withdrawal), **no refund of fees paid** (3.7), **indemnity**
  (6.6): proposals, not facts.
- **Governing court** (16.2): no default.
- **Reg. (EU) 2019/1150** (17.4) and the **Digital Services Act**: whether the booking site and the marketplace make
  CasaZen an "online intermediation service" or a hosting service with their own duties.
- **Starter plan.** Without a subscription the organization has the Starter limits (3.2); the plans page lists Starter
  at a price. Say whether Starter is free until a subscription is bought.
- **VAT** on the plan prices (3.1) and the electronic invoicing flow (the SDI integration is a stub, PL-13).
- **No fee on transactions** (4.2): true in the code today (no application fee on the direct charges); a future fee needs
  the notice of ToS 15.1.
- **Statements tied to flags** (2.2): the API integrations with portals and the AI supplier search are off (D10, D11).
- **Sign-up attribution storage** (Privacy 11.3): UTM, landing page and referrer host are written to the browser (tab,
  then up to 30 days) and sent after the onboarding. The notice describes it and bases it on legitimate interest;
  whether art. 122 Codice privacy requires consent for it is the lawyer's call. The cookie banner shown on the hosts'
  booking sites mentions analytics cookies that do not exist (task BK-14).
- **Retention.** Nothing is configured today (gdpr.md): the tables say so. Privacy 8.4 says no single period exists for
  technical logs.
- **Closing an organization.** There is no self-service closure or deletion: ToS 13.2 and 13.4 rely on a written
  request and a manual operation within `DataReturnDays`.
- **Records and people.** The register of processing of art. 30(2), the confidentiality commitments of the staff (DPA 6.1)
  and the breach procedure (DPA 12) are organisational duties of CasaZen that the product does not implement
  (`gdpr.md`: not implemented).
- **Subprocessor notice** (DPA 8.3): the notice is an email or in-app message to every host; the page alone is not a
  notification. No automatic sending exists.

### 5.6 The security measures the DPA states, and how to check them

DPA art. 7.1 lists only what the code does. Re-check this table before activating a new version and whenever a measure
changes (a new bullet needs a new version).

| Statement | Code and runbook | Quick check |
|---|---|---|
| Separation by organization, other organization's resource not found | `ITenantOwned`, `TenantQueryFilterArchitectureTests`, `HostResource` checks | `dotnet test --filter TenantQueryFilterArchitectureTests` |
| Verified token, role and resource authorisation, deactivation | `CasazenPolicies`, `InactiveAccountMiddleware` ([`auth0.md`](auth0.md)) | Anonymous call to `/api/properties` answers 401 |
| HTTPS, HSTS on the API, TLS to the database | `cors-security-headers.md` § 2; `ConnectionStrings__DefaultConnection` with `SSL Mode=Require` on Railway | `curl -sI $API/api/health/live \| grep -i strict-transport`; look for `SSL Mode=Require` in the Railway variable |
| Column encryption (document number and place, Alloggiati credentials, OTA keys, iCal URLs), AES-256-CBC + HMAC-SHA256, keys rotate every 90 days | [`encryption.md`](encryption.md) § 1-5 | The SQL checks of `encryption.md` § 4 return 0 |
| Private storage, signed links of a few minutes | [`storage.md`](storage.md) (`Storage__SignedUrlTtlMinutes`, default 5) | `storage.md` § 6 steps 4-5 |
| Masked document number, write-only credentials | `encryption.md` § 6-7 | `GET /api/guests/{id}` shows `documentNumberMasked` |
| Random, expiring, hashed check-in and confirmation links | `GuestCheckInSession.TokenHash`, `Bookings.CheckoutTokenHash` | `direct-booking.md` § 1 |
| Stripe: signature verified, one processing | [`stripe.md`](stripe.md) § Webhooks | `WebhookSignatureTests` |
| Logs without email in clear, names or tokens | `LogRedaction`, [`cors-security-headers.md`](cors-security-headers.md) | `LogRedaction` tests |
| Security headers, CORS allow-list without credentials, rate limits, anti-SSRF download | `cors-security-headers.md`, [`proxy-ip.md`](proxy-ip.md), [`external-fetch.md`](external-fetch.md) | the checks of those pages |
| Audit of exports, erasures and retention without personal data; append-only consent history | [`gdpr.md`](gdpr.md) § 1 | `GuestPrivacyAuditEntries` query of `gdpr.md` § 6 |
| Secrets in environment variables, no start without the encryption certificate | [`storage.md`](storage.md) § 4 | The startup log line of `encryption.md` § 3 |
| Retention job and erasure tools | [`gdpr.md`](gdpr.md) | `gdpr.md` § 6 |

## 6. Fail-closed, health check and startup log (decision D9)

A document is **published** when a file exists for its configured version **and** every value its placeholders need is
configured (or an external copy is configured with `DocumentUrl`). Otherwise:

| Where | What happens |
|---|---|
| `GET /api/legal/{kind}` | `available: false`, `contentHtml: null`: the pages `/legale/*` and the onboarding show "in preparazione"; never a text with a placeholder. The version and `effectiveAt` are still those configured (the consent refers to them) |
| `GET /api/health/ready`, check `legal` | `degraded` (HTTP 200: it never blocks a deploy). An admin token sees the description: `Tos 2026-10-v1: missing or invalid Legal__Controller__Pec` or `Dpa 2026-06-v1: no text file for this version`; the anonymous body has only the status |
| Startup log | One line per document: `Legal document {Kind} version {Version} is published` or a warning `Legal document not published, the public page stays 'in preparation': …` and a warning from the service naming the missing variables. Names only, never values |

While the versions in force are `2026-06-v1` (no text) the `legal` check is `degraded` on every environment: expected
until the activation of [5.3](#53-activation-and-what-the-hosts-see). The onboarding does not block a host from
accepting a document whose text is not available (the consent refers to the version): do not announce a new version
before the health check is `healthy`.

## 7. Tests

`LegalDocumentTextsTests` (unit) and `LegalDocumentsIntegrationTests`: the six drafts load; values are replaced and
HTML-encoded; the plan table follows `PlanCatalog` and the configured prices; every mandatory variable, a placeholder
value and an invalid value keep the documents unpublished and are named; optional values do not block; the retention
table shows only the periods the job applies; Italian and English have the same headings, paragraph numbers, lists,
tables, links and tokens; every file starts with the draft notice as a comment that readers never see; no provider is
named in the texts (the list is dynamic); only verified statutes are cited; the default versions do not activate the
drafts; the proposed notices respect Reg. 2019/1150; the version, not the data, decides the re-acceptance; the `legal`
health check is healthy/degraded as described; the endpoints serve the drafts and stay "in preparation" without data.
