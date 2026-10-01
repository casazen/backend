# Runbook: AI provider, budget, rate limits, GDPR

Task FD-21 (defects A8-01, A8-07, A8-14, A8-15, A4-26, A8-26, A8-16 AI part; decision D11). Related:
[`feature-flags.md`](feature-flags.md) (flag `AiSupplierDiscovery`), [`hangfire.md`](hangfire.md) (SEO jobs).

## What calls an AI provider

| Caller | Data in the prompt | Guard |
|---|---|---|
| SEO page generation (`SeoPageGenerationJob`, `SeoContentRefreshJob`, admin "Genera" `POST /api/admin/seo/generate`, bootstrap) | Public regulatory data only: comune name and ISTAT code, page type, the verified CIN / Alloggiati facts with their official URLs, the tourist tax rates in force with their source URL (versioned prompt `SeoContentPrompt`) | Platform budget; the batch stops at the first page the budget cannot cover. `POST generate` is rate limited per user. The output is checked and stored as a **draft**: it is published only after a person approves it ([`seo-domain.md`](seo-domain.md) section 7, SE-01). |
| AI supplier match `POST /api/service-requests/match-supplier` (reason sentence, web search, LLM extraction) | Category code, urgency, open requests count; for the web search the property's city and a fixed category term | **Behind `Features:AiSupplierDiscovery`, off (D11).** When on: category allowlist, per-user and per-org rate limit, platform budget. |

Never put in a prompt sent to an external provider: the host's free-text notes, guest data (names, phone numbers,
documents, dates of stay), supplier or host names, e-mails, addresses of a person. `ISupplierMatchService.MatchAsync`
does not even take the notes, and `match-supplier` ignores a `notes` field in the body.

## Configuration (Railway `Ai__*`)

| Variable | Default | Meaning |
|---|---|---|
| `Ai__Provider` | `Stub` | `Stub`: template text, no external call, no cost; the SEO generation stores it as "contenuto non generato" (`AiProviderNotConfigured`), never as publishable text. `DeepSeek`: external provider. |
| `Ai__ApiKey` | — | Without a key DeepSeek answers empty and makes no call. **An external provider is "active" only with `Provider=DeepSeek` and a key.** |
| `Ai__Model`, `Ai__OpenAiBaseUrl`, `Ai__AnthropicBaseUrl` | `deepseek-v4-flash`, `https://api.deepseek.com`, `https://api.deepseek.com/anthropic` | Provider endpoints. |
| `Ai__MaxCompletionTokens` | `2048` | `max_tokens` of a completion, also the completion part of the budget reservation. |
| `Ai__WebSearchMaxTokens` | `4096` | `max_tokens` of a web search (discovery only). |
| `Ai__Subprocessor__Name` / `__Purpose` / `__Region` / `__TransferMechanism` / `__Website` | name = provider, purpose = "AI text generation", others empty | How the active provider appears in `GET /api/legal/subprocessors` (see GDPR below). |

The old key `Seo:AiProvider` (`Seo__AiProvider`) was never read and has been removed: the provider is `Ai:Provider`
only. `AddCasazenAiProvider` is registered once (Program.cs); the unused `AddCasazenExternalServices`, which registered
it a second time, has been removed.

## Platform AI budget (A8-01, A8-07)

- Row `PlatformAiBudgets` (one per database): `MonthlyTokenCap` (default 500,000), `TokensUsedThisMonth`,
  `LastResetAt`. Shown in the admin SEO dashboard ("Budget AI piattaforma", `GET /api/admin/seo/budget`).
- Every call to the external provider (completions and web searches) **reserves its worst case before the request**:
  prompt estimate + `max_tokens`. If `used + reservation > cap` the request is not sent: `AiBudgetExceededException`
  (API: 422, code `ai_budget_exhausted`, localized message `AiBudgetExhaustedDetail`). SEO batches stop there.
- After the call the reservation is replaced by the tokens actually used: `usage.prompt_tokens + completion_tokens`
  (or `input_tokens + output_tokens` for the web search). **When the provider reports no usage the tokens are
  estimated** at one token every 3 characters of prompt and answer. Cache hits count 0. A failed call counts the
  prompt estimate.
- Reservation and settlement are single SQL `UPDATE`s with the cap in the `WHERE`: concurrent requests and several
  replicas cannot overspend. The counter resets on the first call of a new UTC month.
- The stub provider is not budgeted (it costs nothing).

Change the cap (test first, then production), in the SQL editor of Supabase, on the schema of the environment:

```sql
UPDATE "PlatformAiBudgets" SET "MonthlyTokenCap" = 1000000, "UpdatedAt" = now();
```

## Rate limits of the AI endpoints (A8-01)

`[AiRateLimit]` on `match-supplier` and `POST /api/admin/seo/generate`: a fixed window per user **and** one per
organization; over either one the API answers 429 `rate_limited` with `Retry-After`, and the provider is not reached.
Counters are in memory, per replica.

| Variable | Default |
|---|---|
| `RateLimiting__AiPerUser__PermitLimit` / `__WindowSeconds` | 20 / 3600 |
| `RateLimiting__AiPerOrg__PermitLimit` / `__WindowSeconds` | 60 / 3600 |

## GDPR: subprocessors (A8-15)

When an external provider is active, `GET /api/legal/subprocessors` (onboarding consents step) adds it to the list
and the list version becomes `<Legal:Documents:Subprocessors:Version>+ai-<provider>` (e.g. `2026-06-v1+ai-deepseek`):
every host has to acknowledge the new list again. If the provider is already listed in
`Legal:Documents:Subprocessors:Items`, nothing is added and the configured version is kept.

The code does **not** fill in the provider's legal details. Until `Ai__Subprocessor__Region` and
`Ai__Subprocessor__TransferMechanism` are set, the entry is marked `detailsPending: true` and the onboarding shows
"sede e base giuridica del trasferimento in corso di definizione".

**Product owner, before setting `Ai__Provider=DeepSeek` with a key on any environment:**

1. Verify from the provider's official documents where the data is processed and the provider's legal entity, and
   decide the legal basis of the transfer outside the EEA (GDPR chapter V: adequacy decision, standard contractual
   clauses or other). Or choose a provider with processing in the EU.
2. Set `Ai__Subprocessor__Region`, `Ai__Subprocessor__TransferMechanism` (and optionally `__Website`, `__Purpose`) on
   Railway, and update the privacy notice / DPA texts (provided by the product owner, D14).
3. Check `GET /api/legal/subprocessors` on test: the provider is listed, `detailsPending` is `false`.

## SEO bootstrap (A8-26)

The bootstrap creates **drafts only** (SE-01, A8-04): nothing is published until an admin reads and approves each text
([`seo-domain.md`](seo-domain.md) section 7). `Seo:AutoApproveAfterBootstrap` no longer exists.

With `Seo:BootstrapOnStartup=true` the first start with no SEO page queues the generation of every registry comune,
**once per environment**: under a Hangfire distributed lock (replicas starting together queue it once) and with a
marker (hash `casazen:seo-bootstrap`, fields `EnqueuedAt`, `JobId`) in the Hangfire schema of the environment. A
generation that fails or produces no page is not queued again at the next deploy. To run it again use "Genera" in the
admin SEO dashboard, or delete the marker (then restart):

```sql
DELETE FROM hangfire_casazen_test.hash WHERE key = 'casazen:seo-bootstrap';
```

(`hangfire_casazen_prod` on production; schema from `Hangfire__Schema`.)

## Checks after a deploy

- `GET /api/public/features` → `"aiSupplierDiscovery": false`.
- `POST /api/service-requests/match-supplier` → 404 `not_found`.
- Admin SEO dashboard: the budget card moves after a generation with DeepSeek (it stays 0 with the stub).
