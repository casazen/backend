# Runbook: AI provider, budget, rate limits, GDPR

Task FD-21 (defects A8-01, A8-07, A8-14, A8-15, A4-26, A8-26, A8-16 AI part; decision D11). Related:
[`feature-flags.md`](feature-flags.md) (flag `AiSupplierDiscovery`), [`hangfire.md`](hangfire.md) (SEO jobs).

## What calls an AI provider

| Caller | Data in the prompt | Guard |
|---|---|---|
| Texts of the supplier showcase `POST /api/supplier/showcase/texts/suggestion` (sentence, introduction, «chi siamo», tips; editor step «Testi») | Category names, comune names, the names the supplier gave its services, the texts already in its draft and what it typed for the AI, all scrubbed of e-mails, phones and fiscal codes (versioned prompt `ShowcaseTextPrompt`) | No flag: shown only while a provider is configured (`GET …/texts/suggestion/status`). `[AiRateLimit]`, platform budget, answers kept per supplier in the cache. The output is checked (`ShowcaseTextRules`: no guarantee, certification, insurance, years, time, price, rating or unmeasured adjective the supplier did not write) and is a **draft** the supplier chooses and publishes; the guarantee, certifications, people, strengths and questions are never drafted. [`suppliers.md`](suppliers.md) section 33.14. |
| SEO page generation (`SeoPageGenerationJob`, `SeoContentRefreshJob`, admin "Genera" `POST /api/admin/seo/generate`, bootstrap) | Public regulatory data only: comune name and ISTAT code, page type, the verified CIN / Alloggiati facts with their official URLs, the tourist tax rates in force with their source URL (versioned prompt `SeoContentPrompt`) | Platform budget; the batch stops at the first page the budget cannot cover. `POST generate` is rate limited per user. The output is checked and stored as a **draft**: it is published only after a person approves it ([`seo-domain.md`](seo-domain.md) section 7, SE-01). |
| «Calcola con l'AI» of the step «Prezzi» of the property wizard: `POST /api/properties/{id}/price-suggestion` (`GET` on the same address says whether an AI is in effect), WIZ-AI-PRICE | The facts of the saved property (type, guests, bedrooms, beds, bathrooms, area, amenities, setting), the comune, province and region of the official list, and the nightly rates of the other homes of the comune that the public site shows, **summed up** (count, quartiles, median, ends: only with at least 3 homes, never one listing or one owner). Never the name, the address, the description or the house rules (versioned prompt `ListingPriceSuggestionPrompt`) | Per-user and per-org rate limit (`[AiRateLimit]`, shared with the other AI endpoints), platform budget. The answer is checked before the host sees it: a believable price, a range that contains it, the cleaning fee, surcharge and minimum stay within limits, and, with the figures of homes of a similar size, not far outside them. Nothing is written to the property. Without an AI it is still `200`, with `aiUsed` false and the `reason` (`ai_not_configured`, `ai_budget_exhausted`, `ai_unavailable`, `ai_invalid_answer`), and the price of the figures alone when there are enough homes. |
| AI supplier match `POST /api/service-requests/match-supplier` (reason sentence, web search, LLM extraction) | Category code, urgency, open requests count; for the web search the property's city and a fixed category term | **Behind `Features:AiSupplierDiscovery`, off (D11).** When on: category allowlist, per-user and per-org rate limit, platform budget. |
| Listing text of the wizard «Aggiungi immobile» (`POST /api/properties/{id}/listing-text/suggestion`, button «Suggerisci con l'AI», `PropertyListingTextService`, versioned prompt `ListingTextPrompt`) | The kind of place, guests, rooms, beds, bathrooms, area, amenities and house rules of the property (never its name, address, unit or anything about the host); the comune, its province and region; the public data of up to 5 listings published on CasaZen in the same comune (headline, kind, spaces, amenities); up to 6 photos of the gallery, reduced by the browser to 1,024 px. The photos go only to providers that read images (OpenAI, Anthropic, compatible services); DeepSeek gets the text alone, and a compatible service that refuses the images gets the call again without them. | `[AiRateLimit]` per user and per org, platform budget (each image reserved as 1,400 tokens), cache per org (`listing-text:<hash of prompt and photos>`; «Rigenera» skips it). The answer is a **suggestion** the host reads and edits before saving it with the step: nothing is published by the AI. Without an active provider, with the budget used up or when the provider fails, the answer is `aiUsed: false` with the `reason` and the wizard composes the text from the data as before. |

Never put in a prompt sent to an external provider: the host's free-text notes, guest data (names, phone numbers,
documents, dates of stay), supplier or host names, e-mails, addresses of a person. `ISupplierMatchService.MatchAsync`
does not even take the notes, and `match-supplier` ignores a `notes` field in the body. The photos of a listing are
sent only by the listing text above: they are the pictures the host publishes on the site, reduced, and the prompt asks
the model not to describe people, documents or number plates.

**Cost of a listing text.** With 6 photos a suggestion uses about 8,000-10,000 tokens (the photos are most of it):
with the default cap of 500,000 tokens a month that is about 50 suggestions for the whole platform. Raise
`MonthlyTokenCap` (section "Platform AI budget") before the wizard is used by many hosts.

**Reusing it.** A feature that asks the AI about photos, or for a text a person reviews, uses the same pieces:
`IAiProvider.GenerateAsync(AiRequest…)` (system prompt, temperature, completion limit and images, with the budget, the
PII scrubbing of the texts and the retry without images included), `AiPhotos.Decode(photos, maxPhotos)` with
`AiPhotoLimits` for the photos the browser sends (the web app reduces them with `reduce-photo.ts`),
`AiSuggestionFallbackReasons` for the `aiUsed: false` answers, `[AiRateLimit]` on the endpoint, and a cache key made of
the prompt version, the texts and the hashes of the photos in the org's scope. Add its row to the table above.

## Configuration (Railway `Ai__*`)

| Variable | Default | Meaning |
|---|---|---|
| `Ai__Provider` | `Stub` | `Stub`: template text, no external call, no cost; the SEO generation stores it as "contenuto non generato" (`AiProviderNotConfigured`), never as publishable text. `DeepSeek`: external provider. |
| `Ai__ApiKey` | — | Without a key DeepSeek answers empty and makes no call. **An external provider is "active" only with `Provider=DeepSeek` and a key.** |
| `Ai__Model`, `Ai__OpenAiBaseUrl`, `Ai__AnthropicBaseUrl` | `deepseek-v4-flash`, `https://api.deepseek.com`, `https://api.deepseek.com/anthropic` | Provider endpoints. |
| `Ai__MaxCompletionTokens` | `2048` | `max_tokens` of a completion, also the completion part of the budget reservation. |
| `Ai__WebSearchMaxTokens` | `4096` | `max_tokens` of a web search (discovery only). |
| `Ai__Subprocessor__Name` / `__Purpose` / `__Entity` / `__Region` / `__TransferMechanism` / `__Website` / `__Source` | name = provider, purpose = "AI text generation"; entity and region as declared by DeepSeek (see GDPR below), transfer mechanism empty, `Source` = official URL + consultation date | How the active provider appears in `GET /api/legal/subprocessors` (see GDPR below). |

The old key `Seo:AiProvider` (`Seo__AiProvider`) was never read and has been removed: the provider is `Ai:Provider`
only. `AddCasazenAiProvider` is registered once (Program.cs); the unused `AddCasazenExternalServices`, which registered
it a second time, has been removed.

## Cache of the AI answers (A8-25, SE-05)

The answers of a paid call are cached by `IAiResponseCache` (`AiResponseCache`, one singleton per process): memory
only, nothing is written to the database. There is **no cache inside the providers** (the old static dictionaries of
`DeepSeekAiProvider`, `StubAiProvider` and `AiSupplierDiscoveryService` grew without limit and never expired).

| Variable | Default | Meaning |
|---|---|---|
| `Ai__Cache__MaxEntries` | `500` | Entries in all. Past it the oldest goes first. |
| `Ai__Cache__MaxEntriesPerOrg` | `50` | Entries one org can hold. Past it the oldest entry of that org is replaced: an org never pushes out the entries of the others. |
| `Ai__Cache__TtlHours` | `24` | How long an answer stays. A value of 0 or less reads as the default. |

- **Keys.** The SEO content is platform-level: key `comune:page type:data version:prompt version`, no org. The supplier
  match reason (`supplier-match:category:urgency:load`) and the supplier discovery results
  (`supplier-discovery:city:category`) are cached **per org**: another org never reads them and fills its own entries.
  So is the nightly price of the wizard (`price-suggestion:price-v1:<hash>`: the hash covers the whole prompt, the language
  included), and only an answer that passed the checks is kept.
- **What is cached.** Only an answer of a configured provider with some text. The stub placeholder, an empty answer, a
  failure and the budget refusal are never cached.
- **"Regenerate" regenerates.** `forceRegenerate` (admin "Genera", `SeoContentRefreshJob`) does not read the cache: it
  asks the provider and replaces the entry. A batch retried after a stop (same data, same prompt version) reuses the
  cached answers and spends no tokens (`PromptTokens` 0 on the revision).
- A restart empties the cache; the revisions are in the database, so nothing is lost.

## Transparency notice (EU AI Act, A8-27)

Where AI-written text reaches a person, the web app shows `AiContentNotice` (`aiContentNotice.*`), driven by a flag of
the API, never by a constant:

| Where | Flag | Notice |
|---|---|---|
| Public SEO pages (`/p/...`) | `SeoPagePublic.aiGenerated` (the published revision is `Generated`) | In the footer of the page (`seo`). The other disclaimers ("not legal advice", date) are texts of the web app in the language of the visitor, no longer sent by the API. `contentLanguage` is `it`: the guides are Italian, the page says so to a visitor reading in another language. |
| Admin SEO review dialog | The revision is `Generated` | Next to the text, worded as a draft (pending) or as the approved page (published). Nothing for a "not generated" state. |
| `POST /api/service-requests/match-supplier` (flag `AiSupplierDiscovery`, off) | `reasonGeneratedByAi` on the candidate; `source = ai_web_search` on an external suggestion | The API says it; **no screen of the web app or of the app uses this endpoint today**, so no notice is mounted. Whoever builds that screen shows `AiContentNotice kind="supplierReason"` when the flag is true. With the flag off the endpoint answers 404 and the reason is static. |

The property page of the public booking site no longer mounts the component with a constant `false`. A property
description may start as a suggestion of the AI («Suggerisci con l'AI» in the wizard, which says so to the host: «Testo
scritto dall'AI in base a …, rileggilo»), but it is the host's text: they review it, change it and save it, so the
page shows no notice.

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

`[AiRateLimit]` on `match-supplier`, `POST /api/admin/seo/generate` and `POST /api/supplier/showcase/texts/suggestion`: a fixed window per user **and** one per
organization; over either one the API answers 429 `rate_limited` with `Retry-After`, and the provider is not reached.
Counters are in memory, per replica.

| Variable | Default |
|---|---|
| `RateLimiting__AiPerUser__PermitLimit` / `__WindowSeconds` | 20 / 3600 |
| `RateLimiting__AiPerOrg__PermitLimit` / `__WindowSeconds` | 60 / 3600 |

## GDPR: subprocessors (A8-15)

When an external provider is active, `GET /api/legal/subprocessors` (onboarding consents step) adds it to the list
and the list version becomes `<Legal:Documents:Subprocessors:Version>+ai-<provider>` (e.g. `2026-06-v1+ai-deepseek`):
the list acknowledged in the onboarding changes with it. The other subprocessors are detected from the configuration
too (PL-14, runbook [`legal-documents.md`](legal-documents.md)).

The code does **not** fill in the provider's legal details. Until `Ai__Subprocessor__Entity`, `Ai__Subprocessor__Region`
and `Ai__Subprocessor__TransferMechanism` are set, the entry is marked `detailsPending: true`: the onboarding shows
"sede e base giuridica del trasferimento in corso di definizione" and the public page `/legale/sub-responsabili` shows
"in definizione".

PL14-SUBP: `appsettings.json` carries what DeepSeek publicly declares in its privacy policy (consulted 2026-10-01,
`Ai:Subprocessor:Source`): the entities are **Hangzhou DeepSeek Artificial Intelligence Co., Ltd. and Beijing DeepSeek
Artificial Intelligence Co., Ltd.** (registered in China) and the personal data is **collected, processed and stored in
the People's Republic of China**. The policy names no GDPR transfer mechanism ("appropriate safeguards … where
required"), so `Ai:Subprocessor:TransferMechanism` is empty and the entry stays `detailsPending: true`: the transfer
basis is a decision of the product owner, not something the code or an agent can fill in. Context to weigh before the
decision: on 30 January 2025 the Italian Data Protection Authority ordered the immediate limitation of the processing of
Italian users' data by these two companies for the DeepSeek chatbot service
([press release](https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10097450)); the order concerns the
consumer service, not necessarily the paid API, which is exactly what needs a legal opinion.

**Product owner, before setting `Ai__Provider=DeepSeek` with a key on any environment:**

1. Check the entity and location above on the provider's current documents and decide the legal basis of the transfer
   outside the EEA (GDPR chapter V: adequacy decision, standard contractual clauses or other), with a legal opinion
   given the point above. Or choose a provider with processing in the EU. The default (`Ai__Provider=Stub`) sends
   nothing anywhere and keeps the entry out of the list.
2. Set `Ai__Subprocessor__TransferMechanism` (and, if the checks change them, `__Entity`, `__Region`, `__Source`,
   optionally `__Website`, `__Purpose`) on Railway, and update the privacy notice / DPA texts (provided by the product
   owner, D14).
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
