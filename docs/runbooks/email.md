# Runbook: transactional email (Resend)

Task FD-13, audit defects A9-06, A9-16, A4-06, A4-07, A4-08, A5-33, A4-20.

## How it works

| Piece | Where | What it does |
|---|---|---|
| `EmailOptions` | `Casazen.Infrastructure/Email/EmailOptions.cs` | Typed configuration, section `Email` (`Provider`, `ApiKey`, `FromAddress`, `FromName`). No sender in code. |
| Startup validation | `Casazen.Web/Extensions/EmailServiceCollectionExtensions.cs` | Outside `Development` and `Testing` (so in `Production`, `Staging`, …) a missing or invalid value **stops the app at startup** with the list of problems (`OptionsValidationException`). |
| `ResendEmailService` | `Casazen.Infrastructure/External/ResendEmailService.cs` | The only `IEmailService`. Sends with the configured sender, unchanged: the old rewrite to `onboarding@resend.dev` is gone. |
| Templates | `Casazen.Infrastructure/Email/Templates/` | `EmailTexts.resx` (Italian, default) and `EmailTexts.en.resx`; `EmailTemplates` renders every email; every dynamic value (names, notes, property, reasons) is HTML-encoded. |
| Links | `PublicSiteLinks` | Every link comes from `App__PublicSiteBaseUrl`, the same public domain as the SEO canonical URLs and the sitemap ([`seo-domain.md`](seo-domain.md)). Missing or invalid value = configuration error, never a fallback domain. |
| Queue | `IEmailQueue` → Hangfire job `EmailDeliveryJob` | Request handlers only queue: a slow or failing provider never turns a saved operation into an error. Transient errors (timeouts, 429, 5xx) are retried 5 times, then the job is deleted. Recurring jobs that already run in Hangfire (check-in link) may send directly; the stay alerts (CO-10) queue theirs. |

Emails covered: new service request (to the supplier), request taken / completed / rejected (to the host), supplier invite, guest check-in link (daily job and "resend link"), stay alerts to the host (guest data missing, Alloggiati Web deadline approaching / overdue / failed, check-out reminder: once per stage, see [hangfire.md §9](hangfire.md#9-stay-alerts-co-10)). Recipients have no language preference yet, so emails go out in Italian; the English texts are ready.

When the provider is not configured (only possible in Development or Testing) every skipped email is logged as a warning: `Email <template> skipped: the email provider is not configured`.

## 1. Verify the sender domain on Resend (one time, product owner)

Without a verified domain Resend only accepts `onboarding@resend.dev`, which delivers **only to the owner of the Resend account**: in production no guest, host or supplier would receive anything (A9-06). The app refuses that sender outside Development.

1. Resend dashboard → **Domains → Add domain**. Enter the domain (or a subdomain) that will send the emails, e.g. the CasaZen domain. If asked for a region, pick the EU one.
2. Resend shows the DNS records to create: an **SPF** record (TXT) and a return-path **MX** record on the sending subdomain, and a **DKIM** record (TXT, `…._domainkey`). Copy them **exactly as shown** into the DNS provider of the domain. Do not merge the SPF value into another existing SPF record of a different host.
3. Recommended: a **DMARC** TXT record on `_dmarc.<domain>`, starting with a monitoring policy (`p=none`), then tighten it once the reports are clean.
4. Back on Resend → **Verify DNS records**. Wait until the domain status is **Verified** (DNS propagation can take from minutes to hours).
5. Resend → **API Keys → Create API key** with **Sending access** only, restricted to that domain if possible. The key starts with `re_`. Create one key per environment (test, production).

## 2. Railway variables (per environment: `test` and `production`)

Railway → service `casazen/backend` → environment → **Variables**:

| Variable | Value | Notes |
|---|---|---|
| `Email__Provider` | `Resend` | Only provider implemented. |
| `Email__ApiKey` | `re_…` | Key of step 1.5 for this environment. Secret: never in the repo. |
| `Email__FromAddress` | e.g. `noreply@<verified domain>` | Only the address; must be on the domain verified in step 1. `…@resend.dev` is rejected outside Development. |
| `Email__FromName` | `CasaZen` | Display name. |
| `App__PublicSiteBaseUrl` | `https://<web app of this environment>` | Base of every link (invite `/register`, supplier inbox `/app/supplier/inbox`, guest check-in `/checkin/{token}`). Absolute URL, https outside Development/Testing. |

Notes:

- The old variable `Email__ResendApiKey` is still read when `Email__ApiKey` is empty. Rename it to `Email__ApiKey` and delete the old one. If `Email__ApiKey` already exists with a SendGrid key (`SG.…`), replace it with the Resend key: validation rejects keys that do not start with `re_`.
- `Email__SendGridApiKey`, `Email__Smtp*`, `SendGrid__ApiKey`, `App__SupplierLoginUrl` and `App__FrontendBaseUrl` are no longer read: delete them.
- **Set the variables before the release reaches `main`.** From this version the production service does not start without `Email__ApiKey`, `Email__FromAddress` and `App__PublicSiteBaseUrl`: the deploy log shows lines such as `Email__FromAddress is missing: set a sender of the domain verified on Resend.`
- Both Railway services run with `ASPNETCORE_ENVIRONMENT=Production` (`secrets/railway.test.variables.example.json`, [`docs/INFRA.md`](../INFRA.md#variables-required-in-production)): the `test` service needs these variables too. Only `Development` and `Testing` (local runs, CI) start without them, skipping each email with a warning; `/api/health/ready` then reports `email: degraded`.

## 3. Send test

On the **test** environment first, then on production after the release:

1. Log in as a platform admin and invite a supplier using a mailbox you control: admin area → supplier invite, or `POST /api/admin/suppliers/invite` with `{"email":"<your mailbox>","comuneCode":"H501"}`. The API answers `201` as soon as the email is queued.
2. Railway logs: `Email supplier-invite queued (job …)` followed by `Email supplier-invite delivered to the provider`. A warning `skipped: the email provider is not configured` means the variables of step 2 are missing.
3. Resend dashboard → **Emails**: the message is listed as delivered, with the configured sender (not `onboarding@resend.dev`).
4. In the mailbox: the link opens `App__PublicSiteBaseUrl/register?inviteToken=…`. In the message headers ("Show original" in Gmail) SPF and DKIM are `PASS` for the verified domain.
5. Optional: from a confirmed booking, **Resend check-in link** to your own address; the email links to `App__PublicSiteBaseUrl/checkin/<token>`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Service does not start, `OptionsValidationException` with `Email__…` or `App__PublicSiteBaseUrl` | Variables of step 2 missing or invalid in that environment. |
| Log `Email <template> rejected by the provider, not retried: InvalidFromAddress …` / `ValidationError` | Sender not on a verified domain, or domain verification lost (DNS records removed). Check step 1. |
| Log `… not delivered, will be retried: RateLimitExceeded` | Resend rate or daily quota reached; the job retries. If it persists, raise the Resend plan. |
| Log `… rejected …: InvalidApiKey` / `RestrictedApiKey` | Wrong key, revoked key, or key without sending access for that domain. |
| No log line at all after an action | The email is queued in Hangfire: check that the Hangfire server runs (connection string set) and, if enabled, the Hangfire dashboard. |

## Data kept in Hangfire

The queued job carries recipient, subject and HTML (the check-in email includes the check-in link). Hangfire removes succeeded jobs after 24 hours; failed deliveries are deleted after the last retry (each failure is logged without the recipient address).

## Adding a new email

1. Add the texts to `EmailTexts.resx` **and** `EmailTexts.en.resx` (`EmailTemplatesTests` fails if a key is missing or untranslated). Texts may contain markup; dynamic values only through `{0}`, `{1}` placeholders.
2. Add a method to `EmailTemplates` using `EmailHtmlBuilder` (never string interpolation of values into HTML).
3. Build links only with `PublicSiteLinks`.
4. In a request handler, queue with `IEmailQueue.Enqueue(...)` after the data is saved; inside a Hangfire job you may call `IEmailService` directly.
