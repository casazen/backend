# Runbook: Golden Journey L3 from the UI on an ephemeral stack

Task FN-03 (audit defects A6-04, A6-23, A6-24, A3-23). Before it, the "L3" Golden Journey created the booking as the host
through the API and the L2 suite mocked every API call (`page.route`): login, wizard, checkout, inbox and "Segna pagato"
could be broken and the check stayed green. Now the journey is driven **from the UI by four distinct actors** against a
real backend, a throw-away PostgreSQL and the real frontend; the API is only an oracle for assertions.

## 1. What runs

Suite `frontend/e2e/gj-l3/golden-journey.spec.ts` (Playwright project `gj-l3`), one journey, two payment variants:

| Step | Actor (own browser context) | UI action | Oracle (API, read only) |
|---|---|---|---|
| 1 | Platform admin | invites the supplier by email for Roma, chosen from the official ISTAT list (`/app/admin/suppliers/invite`, SU-04) | invite mail in the mail catcher |
| 2 | Supplier (phone viewport 375x812) | opens the mail link, signs in, registers, completes the activation wizard | `GET /supplier/profile` = `Active` |
| 3 | Host | onboarding (type, consents, plan), creates the property (comune from the ISTAT list: city, code and region), compliance activation wizard (documents, D.L. 145/2023 safety checklist, terms) | public site lists the property |
| 4 | Host | Stripe Connect linked (see 4) | |
| 5 | Guest (anonymous) | public site `/book/{org}/property/{slug}`: relative dates (today, +2 nights), summary, checkout, pays (card, or "Paga in struttura") | booking dates, `Confirmed`, total = nights x rate |
| 5b | Guest + host | pay at the property only: the guest confirms the email, the host accepts the request in the console (BK-06, D5) | |
| 6 | Host / guest | host sends the check-in link, guest fills the check-in portal (2 guests, document) | check-in session `Completed` |
| 7 | Host | "Registra arrivo" | booking `CheckedIn` |
| 8 | Host | "Richiedi fornitore" for the stay | request `Richiesto` |
| 9 (F1) | Supplier (phone) | inbox, "Presa in carico" | `PresoInCarico` |
| 10 (F2) | Supplier, then host | "Completa"; the host console must show "Completato" within 30 s | |
| 11 | Host | "Segna pagato" | `Pagato` |
| 12 | Host | check-out wizard (CO-17) and compliance cockpit | `CheckedOut`, stay not in `checkoutsDue` / `turnoversPending`; no API 5xx in the whole journey |

Variants: **pay at the property** (D5) always runs; **Stripe test card** runs only with the Stripe secrets (section 3) and is
otherwise reported as SKIPPED (annotation + job summary), never as a pass. `e2e/.auth/gj-seed.json` (ids, emails) is written
for the app suite of FN-04 and uploaded as a CI artifact; it is not committed.

Deterministic: stay dates are `today` and `today + 2` in Europe/Rome (the clock of `RomeCalendar`), accounts, slugs and
emails are unique per run (fresh organization, no cleanup, no test-only endpoint), the DB is created and dropped per run,
no network but Stripe test mode. The backend uses the real `TimeProvider` (the journey needs the real current day).

## 2. The ephemeral stack (`frontend/e2e/stack/`)

`up.sh` starts, `down.sh` stops and drops the database, `ci-run.sh` is the CI entry (up, suite, down, summary).

| Piece | How | Production code touched |
|---|---|---|
| PostgreSQL | `CREATE DATABASE gj_l3_<run>` on the server given by `PGHOST/PGPORT/PGUSER/PGPASSWORD`; the backend applies every EF migration at startup. The health check reports the real database (no InMemory) | none |
| Backend | the real `Casazen.Web` (environment `Development`), config only through `Auth0__*`, `ConnectionStrings__*`, `Email__*`, `App__*`, `Stripe__*` variables | `Email__ApiUrl` (below) |
| Auth0 | **mock IdP** `mock-services.mjs`: HTTPS with a throw-away certificate, OIDC code + PKCE + refresh token, JWKS (RS256), and a minimal Management API (roles). The backend validates the tokens with its **unchanged** JwtBearer setup (issuer `https://localhost:<port>/`, audience, signature); it trusts the certificate through `SSL_CERT_FILE` (system roots + the throw-away certificate). No auth bypass exists in the backend, nothing is weakened in production | none |
| Roles | granted through the mock Management API exactly as the backend does at onboarding and supplier registration (additive), then present in the next token. `gj-admin-*@example.test` is the platform admin | none |
| Email | **mock Resend** (`POST /emails`, `GET /__outbox`) on the same mock server. The real pipeline runs (Hangfire job, `ResendEmailService`); the test reads the invite, confirmation and check-in links from the outbox | `Email__ApiUrl` |
| Frontend | Vite dev server, real Auth0 SDK pointed at the mock IdP (`VITE_AUTH0_DOMAIN=localhost:<port>`, no demo mode) | none |
| Stripe | only with the secrets: backend with the test keys, Stripe CLI `stripe listen --forward-to .../webhooks/stripe --forward-connect-to .../webhooks/stripe/connect` | none |

**`Email__ApiUrl`** (new, `EmailOptions.ApiUrl`): base URL of a Resend-compatible API, read only to point the Resend client
at the mail catcher. `EmailOptionsValidator` **rejects it outside Development and Testing** (startup error naming
`Email__ApiUrl`), so it can never redirect production or Railway `Staging` email. Tests: `EmailConfigurationTests`.

Not driven from the UI, on purpose: Stripe Connect onboarding is hosted by Stripe (KYC pages). Without Stripe keys the
organization is marked as having a charges-enabled connected account directly in the throw-away database (the "pay at the
property" checkout needs a ready Connect account, BK-06); with the keys the test creates a real Stripe **test-mode** Custom
connected account through the Stripe API and links it the same way. The guest's Stripe Payment Element, the PaymentIntent
on the connected account and the `payment_intent.succeeded` Connect webhook are real in that variant.

## 3. CI (GitHub Actions)

| Repo | Workflow / job (check name) | Trigger |
|---|---|---|
| frontend | `e2e-golden-journey.yml` / **`GJ L3 (UI, ephemeral stack)`** | PR and push to `develop` / `main`, nightly, manual |
| backend | `e2e-golden-journey.yml` / **`Golden Journey L3`** | same |

Each job checks out both repositories (same line: the PR base branch or the pushed branch), starts a `postgres:16` service,
builds the backend, installs Chromium, runs `e2e/stack/ci-run.sh` and uploads `gj-l3-logs` (stack logs, traces, report,
seed). No `continue-on-error`.

One-time setup (repo admin, product owner):

| Kind | Where | Name | Value |
|---|---|---|---|
| Secret | `casazen/frontend` | `BACKEND_REPO_TOKEN` | fine-grained PAT (or deploy token), **read** on `casazen/backend` contents |
| Secret | `casazen/backend` | `FRONTEND_REPO_TOKEN` | fine-grained PAT, **read** on `casazen/frontend` contents |
| Secret | both | `STRIPE_TEST_SECRET_KEY` | Stripe **test-mode** secret key (`sk_test_...` or restricted `rk_test_...` with Connect Accounts write), of a dedicated test account |
| Secret | both | `STRIPE_TEST_PUBLISHABLE_KEY` | `pk_test_...` of the same account |

The webhook secret needs no setting: `stripe listen` prints the secret of the run. A key that is not test-mode makes the
job fail (`ci-run.sh` and the test refuse it). Never put a key in the repository.

**Fail-visible rules** (a check that cannot fail is not a gate):

- Missing `BACKEND_REPO_TOKEN` / `FRONTEND_REPO_TOKEN` (also on PRs from forks and Dependabot): the job **fails** with an
  `::error::` annotation. Required status checks therefore need the secrets to exist.
- Missing Stripe secrets: the job runs the pay-at-the-property variant, the Stripe test is **SKIPPED** with its reason in
  the log, a `::warning title=Golden Journey L3: Stripe variant SKIPPED::` annotation on the run and a "SKIPPED" row in the
  job summary (`e2e/stack/summary.mjs`). Add the two Stripe secrets to close that gap.
- Required checks (after a few green runs): add `GJ L3 (UI, ephemeral stack)` to `casazen/frontend` and `Golden Journey L3`
  to `casazen/backend`, on `develop` and `main`, as in `ci-frontend.md` / `ci-backend.md` section 1.

## 4. Run it locally

```bash
# PostgreSQL reachable (here: password dev), .NET 10, Node 22, openssl, psql
export PGHOST=localhost PGUSER=postgres PGPASSWORD=dev
cd frontend
E2E_BACKEND_DIR=../backend E2E_API_PORT=5150 E2E_FE_PORT=5163 bash e2e/stack/up.sh     # ~1 min
E2E_GJ_L3=1 E2E_BASE_URL=http://localhost:5163 npx playwright test --project=gj-l3       # ~1 min
bash e2e/stack/down.sh                                                                  # stops, drops the database
```

With Stripe test keys: export `STRIPE_TEST_SECRET_KEY` / `STRIPE_TEST_PUBLISHABLE_KEY` and run `bash e2e/stack/ci-run.sh`
(needs the Stripe CLI). Logs of the stack: `frontend/e2e/.stack/*.log`. Trace of a failure: `npx playwright show-trace`.

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Ephemeral stack not running` | start `up.sh` first (`e2e/.stack/env.json` is missing) |
| The comune picker offers nothing | the ISTAT list is seeded at backend startup (`Comuni__SeedOnStartup`, [comuni-istat.md](comuni-istat.md)); check the log of the seed |
| `TIMEOUT waiting for backend` | `e2e/.stack/backend.log`: migration or configuration error (the stack is `Development`; `Storage` needs `App__ApiBaseUrl`, set by `up.sh`) |
| The app shows "Autenticazione..." forever / 401 on every call | the backend does not trust the mock certificate: `SSL_CERT_FILE` must be `e2e/.stack/trust-bundle.pem`; the browser context needs `ignoreHTTPSErrors` |
| An email never arrives | Hangfire server not running, or `Email__ApiUrl` not set; `curl http://localhost:9444/__outbox` |
| Stripe step fails to find the card fields | Stripe changed the Payment Element labels: adjust the selectors in `e2e/gj-l3/guest-steps.ts` |

## 6. Known limits (see the task report)

- The Stripe card variant and the Stripe Connect test account creation run only in CI with the secrets; they are not exercised
  by the author's local run (no access to Stripe from the development container).
- The tourist tax is not asserted: the rate comes from `TaxRate` data that must come from the official source (never hardcoded).
- The host console does not refresh itself when the supplier completes a request: the test reloads the page within the 30 s window.
