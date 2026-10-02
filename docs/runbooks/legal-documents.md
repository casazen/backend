# Runbook: legal documents and subprocessors

Task PL-14 (audit defects A1-06, A9-40). Before it the hosts accepted "Termini di Servizio v2026-06-v1" without any
way to read them (no text, no page) and the published subprocessor list was a hand-written list that did not match the
code (SendGrid instead of Resend, Auth0 "EU" on a US tenant, Expo, Vercel and the AI provider missing).

The texts of the Terms of Service, Privacy notice and DPA are provided by the product owner (decision D14): the code
and the agents never write them. Until a text is provided the public page says "in preparazione" and the consent
still refers to the configured version.

## What exists

| Piece | Behaviour | Code |
|---|---|---|
| `GET /api/legal/{tos,privacy,dpa}?lang=it\|en` | Anonymous. `version`, `effectiveAt` (null while not configured), `available`, `contentHtml` (sanitized text of the current version, null while missing), `contentLanguage` (the requested language, or `it` when no translation exists), optional `documentUrl` (external copy) | `LegalController`, `LegalDocumentService` |
| `GET /api/legal/subprocessors` | Anonymous. The providers the running configuration actually uses, see below | `LegalSubprocessorCatalog` |
| Web pages | `/legale` (index), `/legale/termini`, `/legale/privacy`, `/legale/dpa`, `/legale/sub-responsabili`: version, date in force, text or "in preparazione", error state with retry | frontend `src/features/legal/*` |
| Footer | Privacy and Terms on every public page; subprocessors and the index on the CasaZen pages. On a host booking site (`/book/*`) the links open in a new tab | frontend `Footer.tsx` |
| Onboarding consents step | Every checkbox links its page (new tab) and says "testo in preparazione" while the text is missing | frontend `consents-step.tsx` |
| Re-acceptance | A new Terms / Privacy / DPA version locks the host features until the host accepts it again (PL-02) | runbook [`onboarding-consents.md`](onboarding-consents.md) |

## 1. Load a text provided by the product owner

Format: an **HTML fragment** (no `<html>`, `<head>` or `<body>`) using only `p`, `br`, `h2`, `h3`, `h4`, `ul`, `ol`,
`li`, `strong`, `em`, `a` (absolute `https`/`mailto` links only), `table`, `thead`, `tbody`, `tr`, `th`, `td`. Anything
else is removed by the sanitizer (`SeoHtmlSanitizer` on the API, DOMPurify again in the browser): styles, classes,
images and scripts never reach the page. The page title comes from the web app, so the file starts with the first
section (`<h2>`). A Word document can be converted with any "save as HTML" and then cleaned by hand; check the result on
the test environment.

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
| Page says "in preparazione" after adding the file | File name differs from the configured version, a Railway variable overrides the version, or no deploy since the file was added | Compare `GET /api/legal/{kind}` `version` with the file name; redeploy |
| English page shows the Italian text | No `{version}.en.html` | Expected; add the translation as a new file of the same version only if it is a faithful translation approved by the product owner |
| A provider is missing from the list | Its configuration is not set on that environment (e.g. no `Email__ApiKey`) | The list reflects the environment: set the configuration or, for hosting, `…:Providers:{Name}:Enabled=true` |
| Auth0 region is empty | Custom domain without `Auth0__ManagementApiDomain` | Set `Auth0__ManagementApiDomain` to the canonical tenant domain (runbook [`auth0.md`](auth0.md)) |
