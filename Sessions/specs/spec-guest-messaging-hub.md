---
id:
slug: guest-messaging-hub
title: Guest messaging hub — templates, Jev router, rent-support agent
phase: 2
type: feature
priority: P1
status: specced
issue:
depends_on: [guest-check-in-portal, micro-marketplace-v0]
blocks: []
exit_contributes_to: Routine stay questions are answered from host-authored templates; a paid intervention never spends the host's money outside the approval rule below
last_reviewed: 2026-10-08
---

# Spec — Guest messaging hub

## Overview

One thread per short-stay booking is the guest's place to ask for practical help and to receive check-in and check-out messages. Most replies are filled from a template the host has already written, so the host is not pulled into Wi-Fi, arrival, or house-rule questions. A message analyzer (Jev, TypeSafe System One) reads each new guest message once and chooses the path: template, rent-support agent, or host. The agent is used only when the message is tied to a CasaZen function (a supplier intervention, a missing guide slot that needs a judgment, a stay fact the template catalog does not cover). It does not write the ordinary replies.

Anything that can cost the host money waits for the host, unless the host has already turned on auto-approve and put a card on file with a maximum amount. In that case the agent may open the supplier request and the card is authorized up to that maximum; the host is notified and can cancel for 60 minutes, while the supplier has not accepted. A safety emergency never uses that automatic path: the host approves it first.

Check-in and check-out stay on the schedules CasaZen already has. The thread carries a short guest-facing copy of those milestones. Alloggiati deadline chasers stay with the host.

The frozen specs `unified-inbox` (US-011) and `ai-copilot-messaging` (US-012) stay frozen. This hub is the CasaZen thread for a booking. It does not read or send OTA inbox messages.

**Phase:** 2 — Ecosystem · **Type:** feature · **Status:** specced · **Issue:** none yet

Design: none yet. ADRs: none.

Jev contract used here, read 2026-10-08: `POST https://api.typesafe.ai/v1/systemone` with `model`, `state`, and a map of typed `questions` (Choice and Noul). One request answers every question on the same state. Input tokens are billed; output tokens are free. Docs: [API](https://docs.typesafe.ai/api), [Choice](https://docs.typesafe.ai/primitives/choice), [State](https://docs.typesafe.ai/concepts/state). Model id captured from the vendor model page on 2026-10-04: `jev-1.13.0` (aliases `jev-latest`, `jev-preview`).

---

## User Story

As a guest, I want one thread for the stay — arrival details, house questions, and a way to report something broken — so I do not have to guess which email to answer.

As a host, I want ordinary questions answered from the instructions I already wrote, and I want every cost to wait for me unless I have explicitly allowed a capped automatic charge, so a guest message does not create a bill or a supplier visit I did not intend.

---

## Acceptance Criteria

### Thread and intake

- **AC1**: A `Confirmed` short-rent booking gets one `GuestThread` `{ Id, OrgId, BookingId, PropertyId, TokenHash, OpensAt, ClosesAt, DisclosureShownAt }`. `OpensAt` is the confirmation instant. `ClosesAt` is 7 days after `CheckOutDate` in the property time zone (Europe/Rome when the property zone is missing or unknown). A second confirmation of the same booking does not create a second thread.

- **AC2**: The guest reads and writes the thread with `GET` and `POST /api/public/stay/{token}/messages`. The token is its own secret, stored only as a hash, and is not the check-in submit token from `spec-guest-check-in-portal`. Submitting check-in does not invalidate the thread token. Unknown, mismatched, or expired tokens return 404. After `ClosesAt` the GET still returns the transcript and the POST returns 409 `thread_closed`. No model call runs on a closed thread.

- **AC3**: `POST` stores the guest message first, then routes it. A retried POST with the same `Idempotency-Key` returns the original decision and does not call Jev or the agent again. The request that stores the message does not wait on a model: routing runs in a Hangfire job chained from the save.

### Analyzer (Jev) and the template path

- **AC4**: Before any external call, a message whose normalized text is only an acknowledgement (`grazie`, `thanks`, `ok`, `okay`, `perfetto`, `👍`, and the same tokens with trailing punctuation) is stored as `route=acknowledge`. No Jev call, no agent call, no guest reply, no host notification.

- **AC5**: Every other inbound message produces exactly one analyzer request. With `Jev:ApiKey` set, that request is `POST /v1/systemone` with `model` = `Jev:Model` (default `jev-1.13.0`) and these questions on one state:
  - `route` (choice): `standard`, `agent`, `acknowledge`, `host`
  - `template` (choice): the catalog keys in AC6
  - `cost_bearing` (noul): the message asks for work, a visit, a replacement, or anything else the host would pay for
  - `emergency` (noul): someone is unsafe or the place is unusable right now (gas, flood, fire, a person locked inside, no power, a medical emergency). A slow boiler, late checkout, or extra towels is not an emergency.
  The `state` is a JSON object with two fields: `message` (the redacted guest text, truncated so the serialized state is at most 1500 characters) and `stay` (`phase` of `pre_arrival` | `in_stay` | `checkout_day` | `after`, `checkInComplete` bool). The state has no guest name, email, phone, document number, fiscal code, Wi-Fi password, door code, full address, or prior transcript. Question instructions and criteria are English, fixed in source, and covered by a snapshot test.

- **AC6**: Template catalog keys, each filled only from the property `GuestStayGuide` slot of the same name (host-authored text, max 500 characters per slot): `wifi`, `check_in_time`, `check_out_time`, `address_arrival`, `parking`, `house_rules`, `trash`, `towels`, `quiet_hours`, `check_in_link`. `check_in_link` is filled with the existing guest check-in URL, not with a document form in the chat. The guide is edited by the host at `/app/short-rent/properties/{id}/stay-guide`. Empty slot means the product does not invent the fact.

- **AC7**: A template reply is sent only when `route=standard`, `route` confidence ≥ 0.80, the chosen template's probability ≥ 0.80, `cost_bearing` < 0.50, and `emergency` < 0.70, and the slot is non-empty. The reply body is the slot text plus, for `check_in_link`, the URL. Generative completion tokens for that decision are 0. The host receives no email and no push.

- **AC8**: The same standard decision with an empty slot stores `route=host` and `reason=guide_slot_missing`, sends the guest exactly one fixed line (`Ti rispondo appena ho il dettaglio.`), and notifies the host once for that slot and that thread. A later message that hits the same empty slot does not send a second guest line and does not send a second host notification.

- **AC9**: `cost_bearing` ≥ 0.50 or `emergency` ≥ 0.70 overrides `route=standard`. The template is not sent. `emergency` ≥ 0.70 follows AC14 even when `route` confidence is below 0.80. Otherwise a `route` confidence below 0.80 stores `route=host` and `reason=low_confidence`, sends no guest reply, and notifies the host. The agent is not called on `low_confidence`.

- **AC10**: Without `Jev:ApiKey` the same four decisions are produced by an in-repo keyword map over the same labels. The decision record has `analyzer=keyword`. No HTTP call is made. Enabling the hub does not require a Jev key.

### Rent-support agent

- **AC11**: The agent runs only when the stored route is `agent`, or when AC9 overrides a standard route into the cost gate and `route` confidence ≥ 0.80. It is one `IAiProvider` completion at `AiModelTier.Economy`, `max_tokens` 400, prompt version `GuestSupportPrompt` v1 snapshotted in tests. The prompt instructs a short-rent support procedure: answer from tool facts, prefer a catalog template key, propose a supplier request only when an intervention is actually needed. Tool allowlist, each call server-side and tenant-scoped to the thread's booking:
  - `get_stay_card` — dates, phase, check-in session status, guest count, property name. No contact, no documents.
  - `get_stay_guide` — the slots in AC6.
  - `get_checkin_link` — the existing portal URL.
  - `list_suppliers` — Active CasaZen suppliers for the property comune and one `ServiceCategories` code (`GetActiveByComune`). No `AiSupplierDiscovery` call.
  - `propose_service_request` — returns a proposal `{ category, urgency, needed, quoteEur? }`. It does not insert a `ServiceRequest`.
  The completion must parse as `{ templateKey|null, replyText|null, needed: bool, category|null, urgency: Normal|High|Emergency, quoteEur|null }`. A parse failure stores `route=host`, `reason=agent_unparsed`, notifies the host, and creates no request and no charge.

- **AC12**: With the AI provider unset (`Ai:Provider=Stub` or no key), an agent-routed message stores `route=host`, `reason=agent_unavailable`, notifies the host, and does not synthesize a reply. Template decisions in AC7 still send.

- **AC13**: When `needed=false`, the guest receives the catalog template when `templateKey` is set and the slot is filled; otherwise `replyText` trimmed to 500 characters. No `ServiceRequest`, no card authorization, no host notification. `replyText` is rejected (treated as AC11 parse failure) when it contains a euro amount the tools did not return, a promise that a supplier is already coming, or a claim that Alloggiati was sent.

### Cost gate

- **AC14**: `emergency` ≥ 0.70, or an agent result with `urgency=Emergency`, always opens a host approval (`GuestCostAuthorization.Status=PendingHost`, `Kind=Emergency`). No `ServiceRequest` is created, no card is authorized, and auto-approve is ignored. The guest receives the fixed line `Ho avvisato l'host: serve una sua conferma prima di un intervento.` The host is notified immediately (email + push), including inside quiet hours.

- **AC15**: Any other `needed=true` result, and any non-emergency message with `cost_bearing` ≥ 0.50, opens `GuestCostAuthorization` in `PendingHost` when auto-approve is off. No `ServiceRequest`, no card charge. The guest receives `Ho passato la richiesta all'host.` The host is notified (email + push) with category, property, stay dates, and the supplier shortlist from `list_suppliers` (empty shortlist included). Approve creates the `ServiceRequest` (`OpenedBy=GuestHub`) and, when the host confirms an amount, authorizes that amount on the card. Decline stores `Declined`, sends the guest `L'host non ha confermato l'intervento.`, and creates no request.

- **AC16**: Auto-approve can be saved only when all of the following are true: the org toggle is on, `MaxAutoApproveEur` is a positive amount the host typed (no default), and the org platform customer (`Org.StripeCustomerId`) has a card that can be charged off-session. The save calls Stripe to confirm the payment method; a failed setup leaves the toggle off and shows the Italian problem message. Guest booking cards on the connected account do not satisfy this check.

- **AC17**: With auto-approve on, a non-emergency `needed=true` result and at least one Active supplier opens `GuestCostAuthorization.Status=WindowOpen` for 60 minutes from `WindowOpenedAt`:
  - A `ServiceRequest` is created immediately (`OpenedBy=GuestHub`, status `Richiesto`) and the supplier is notified as today.
  - The host card is authorized with `capture_method=manual` for `min(quoteEur, MaxAutoApproveEur)` when `quoteEur` is present and ≤ the cap, otherwise for `MaxAutoApproveEur`.
  - The host is notified (email + push) with the amount authorized, the supplier, and a cancel link. The guest receives `Ho organizzato la richiesta. L'host può ancora annullarla per un'ora.`
  - No Active supplier, or `quoteEur` above the cap before dispatch: the authorization stays `PendingHost` (AC15) and nothing is sent to a supplier.

- **AC18**: `POST /api/guest-cost-authorizations/{id}/cancel` by the host, while `now < WindowOpenedAt + 60 minutes` and the linked `ServiceRequest` is still `Richiesto`, sets the authorization to `Voided`, voids the PaymentIntent, sets the request to `Rifiutato` with reason `annullata dall'host`, and sends the guest `La richiesta è stata annullata.` When the supplier has already moved to `PresoInCarico` or later, the cancel returns 409 `cost_window_supplier_accepted` and the authorization stays open. After 60 minutes the cancel returns 409 `cost_window_closed`.

- **AC19**: At 60 minutes a job captures `min(accepted quote, authorized amount)` when a quote is known and ≤ the cap, and sets `Captured`. When no quote exists yet, the authorization stays open and uncaptured until a quote arrives or 7 days pass; at 7 days with no `PresoInCarico` the job voids it and rejects the request. A quote above the cap sets `AboveCap`, captures nothing, blocks `PresoInCarico` for that `OpenedBy=GuestHub` request with 409 `cost_above_cap`, and notifies the host. Host requests that are not `OpenedBy=GuestHub` keep today's state machine.

### Check-in, check-out, and how often the guest is written to

- **AC20**: Proactive guest messages are templates, not model calls. Each row is sent at most once per thread. Times use the property time zone, falling back to Europe/Rome. A slot that falls between 22:00 and 08:00 local is deferred to 08:00 the same morning (or the next morning if the slot is after 22:00). Inbound replies are still routed immediately.

  | Milestone | When | Guest text | Host |
  |---|---|---|---|
  | Check-in link | The existing `GuestCheckInSendJob` send (default 3 days before check-in) | One thread copy of that same email, including the portal link and one line that guest data are collected on the form because the stay has to be registered. No second guest email. | Unchanged |
  | Still incomplete | `StayAlertSchedule` guest-data reminder instant, only when check-in is not complete | One line and the same link | The existing host `GuestDataMissing` alert stays. The guest is not written to again about the missing data. |
  | Arrival day | 08:00 local on `CheckInDate`, only when check-in is complete and `address_arrival` or `check_in_time` is filled | Those filled slots | No notification |
  | Check-out | The existing `StayAlertSchedule.DueCheckoutReminder` instant | `check_out_time` and `trash` when filled; otherwise one fixed line with the check-out date. A tourist-tax sentence is included only when the booking already has a calculated amount, prefixed with `Importo calcolato, non è una consulenza.` | The existing host checkout reminder stays |

- **AC21**: Alloggiati stages `DeadlineApproaching`, `Overdue`, and overdue reminders are not copied into the guest thread. The thread never states that the police report was sent unless `GuestCheckInSession` is `AlloggiatiInviato` and a receipt id is stored. A guest message that asks for the registration status receives the fixed status label already used by the check-in portal (including "da inviare manualmente" while that is the honest state). Document numbers, document photos, and fiscal codes are not accepted in the thread: a stored guest message has those spans replaced with `[rimosso]`, and the reply is the check-in link template.

### Limits, tenancy, disclosure

- **AC22**: `Features:GuestMessagingHub` defaults off. Off: no thread is created, public stay routes return 404, and check-in email behaves as today. Jev input tokens and agent completion tokens are reserved against `PlatformAiBudget` before the call, the same way `docs/runbooks/ai.md` reserves a completion. A budget refusal stores `route=host`, `reason=budget`, and still sends nothing that would cost a further call. Decisions are cached for 24 hours only when the stored route is `standard` or `acknowledge`, keyed by org, property, guide version, stay phase, `checkInComplete`, and the redacted message. A cache hit records 0 tokens and is not readable by another org.

- **AC23**: Host routes require the booking read/write policies already used for that booking (`HostScope`, tenant filter). A token or a host of another org receives 404. Public POSTs are rate-limited per token and per IP; over the limit the API returns 429 `rate_limited` with `Retry-After` and does not call Jev or the agent.

- **AC24**: The first automated guest message of a thread ends with `Messaggi automatici preparati da CasaZen per conto dell'host.` and sets `DisclosureShownAt`. An agent `replyText` also carries `Messaggio preparato con assistenza automatica.` A host-typed message carries neither line. Every decision is stored in `GuestMessageDecision` `{ ThreadId, MessageId, Analyzer, Route, Reason, RouteConfidence, TemplateKey, CostBearing, Emergency, PromptTokens, CompletionTokens, Model, CreatedAt }` with no message body in the row.

### Frontend

- **AC25**: Guest page `/stay/:token` is mobile-first. It shows the transcript, a composer, and the Italian empty state `Scrivi qui se ti serve qualcosa per il soggiorno.` Closed thread: composer hidden, copy `Questa conversazione è chiusa.` Loading and 404 use Italian sentences. The page has no Auth0 step.

- **AC26**: Host inbox `/app/short-rent/messages` lists threads for the host's bookings. A thread that was answered by AC7 or AC4 shows no badge. Badges, in Italian: `Da approvare`, `Urgenza`, `Manca un dettaglio`. Opening a thread shows the transcript and, when an authorization is pending or in the window, the amount, the supplier name, and the action `Approva`, `Rifiuta`, or `Annulla`. A 409 from AC18 shows the Italian problem message and leaves the authorization in place.

- **AC27**: Stay-guide editor and auto-approve settings live on the property. Auto-approve controls: toggle, amount field, card setup. Saving with an empty amount or a failed card setup leaves the toggle off and shows an Italian error. The primary path is: open the inbox, open a `Da approvare` thread, press `Approva`. That path is four screens or fewer from `/app/short-rent/messages`.

- **AC28**: A standard reply (AC7) creates no host notification row and no push. An emergency (AC14) creates both. Copy on primary controls is Italian.

---

## Verifiable Outcomes

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | A second confirm leaves one `GuestThread` for the booking. `ClosesAt` is 7 days after checkout in the property zone. | Two threads; close date computed in UTC with no zone |
| AC2 | L1 | Check-in submit leaves the stay token valid. POST after `ClosesAt` is 409 `thread_closed` and the analyzer mock has zero calls. | Stay token deleted on check-in; model called after close |
| AC3 | L1 | Two POSTs with one idempotency key call the analyzer once. The HTTP handler returns before the analyzer runs; a job row exists. | Second call billed; analyzer invoked inside the controller |
| AC4 | L1 | `Grazie!` stores `acknowledge`, analyzer mock and notification mock have zero calls, guest transcript has no new outbound message. | Jev called for `ok` |
| AC5 | L1 | One system-one request contains the four question ids. Serialized state ≤ 1500 characters and the fixture password, document number, and email are absent from the body. | Four HTTP calls; password present in `state` |
| AC6 | L1 + L2 | Guide PUT persists the `wifi` slot. A standard wifi decision replies with that exact text. An empty `parking` slot does not appear as a guessed sentence. | Model text used as the Wi-Fi reply; invented parking instructions |
| AC7 | L1 | Fixture decision `standard` / confidence 0.91 / template 0.90 / cost 0.1 / emergency 0.1 sends the slot and writes `CompletionTokens=0`. Notification mock receives no host send. | Host push on a Wi-Fi answer; completion tokens > 0 |
| AC8 | L1 | First empty-slot hit sends one guest line and one host notice. The second hit sends neither. | Second guest apology; missing host notice |
| AC9 | L1 | Cost 0.62 with route `standard` does not send the template. Emergency 0.80 with route confidence 0.40 creates an emergency approval and does not call the agent. Route confidence 0.50 and cost 0.1 stores `low_confidence` and does not call the agent. | Template sent on a paid request; agent called below 0.80 |
| AC10 | L1 | With no Jev key, a fixture `qual è la password del wifi` stores `analyzer=keyword`, `template=wifi`, and the HTTP client has zero calls. | Live HTTP without a key |
| AC11 | L1 | Agent path issues one Economy completion with max tokens 400. `propose_service_request` does not insert a row. A non-JSON completion stores `agent_unparsed` and inserts no `ServiceRequest`. | Supplier row created inside the tool; second model tier |
| AC12 | L1 | Stub provider on an agent route stores `agent_unavailable` and sends no guest prose. A standard wifi decision still sends the slot. | Stub text presented as the guest reply |
| AC13 | L1 | `needed=false` plus a filled template sends the slot and creates no authorization. `replyText` that says a technician is on the way stores `agent_unparsed`. | Charge opened when the agent said no intervention |
| AC14 | L1 | Emergency approval has no `ServiceRequest` and no PaymentIntent. Guest transcript contains the fixed emergency line. Host notification is recorded at 23:00 local. | Supplier notified before the host answers; quiet hours delay the emergency |
| AC15 | L1 | Auto-approve off: `needed=true` stores `PendingHost`, no `ServiceRequest`. Decline sends the fixed decline line. Approve inserts one `OpenedBy=GuestHub` request. | Request inserted while still pending |
| AC16 | L1 + L2 | PUT with no amount or a failing card setup returns 422 and the stored toggle is false. A connected-account guest card does not flip the toggle. | Toggle true without a platform payment method |
| AC17 | L1 | Quote 80 and cap 100: one `ServiceRequest` in `Richiesto`, one manual-capture authorization for 80, host notification contains 80. No supplier: status stays `PendingHost` and the supplier notifier has zero calls. | Full cap captured immediately; supplier pinged when the shortlist is empty |
| AC18 | L1 | Cancel at minute 30 while `Richiesto` voids the intent, sets `Rifiutato`, and sends the fixed cancel line. Cancel after `PresoInCarico` is 409 `cost_window_supplier_accepted` and the intent is still open. Cancel at minute 61 is 409 `cost_window_closed`. | Void after the supplier accepted; cancel allowed after an hour |
| AC19 | L1 | At 60 minutes a quote of 70 against an authorization of 100 captures 70. A quote of 140 sets `AboveCap`, captures 0, and `PresoInCarico` on that hub request returns 409 `cost_above_cap`. A host-opened request still moves to `PresoInCarico`. | Cap ignored; marketplace request blocked |
| AC20 | L1 | Incomplete check-in produces one guest reminder at the guest-data instant and no further guest message when the overdue stage arrives. The check-in job sends one email, and the thread contains that body once. A milestone at 23:00 local is stored with a send time of 08:00. | Guest receives the overdue Alloggiati chaser; two check-in emails |
| AC21 | L1 | A message containing a document number is stored with `[rimosso]` and the reply is the check-in link. A status question while the session is not `AlloggiatiInviato` does not contain `inviato alla questura`. | Raw document number in the transcript; false sent claim |
| AC22 | L1 | Flag off: public GET is 404 and no thread row. Budget refusal stores `budget` and the Jev client has zero calls. A cached standard decision on a second stay in another org misses the cache. | Cross-org cache hit; Jev called after the cap |
| AC23 | L1 | A host outside the booking org gets 404 on the thread. The 429 response does not increment the analyzer call count. | Thread listed across orgs |
| AC24 | L1 | The first outbound sets `DisclosureShownAt` and contains the fixed disclosure sentence. The second template outbound does not repeat it. `GuestMessageDecision` has null body columns. | Disclosure on every bubble; message text in the decision table |
| AC25 | L2 + L3 | `/stay/:token` empty state shows `Scrivi qui se ti serve qualcosa per il soggiorno.` After `ClosesAt` the composer is absent and `Questa conversazione è chiusa.` is visible. | Blank page; composer still submits |
| AC26 | L2 + L3 | Inbox row for a wifi auto-reply has no badge. Pending row shows `Da approvare`. Forced 409 shows the Italian problem title and the `Annulla` control remains. | Badge on a template reply; raw JSON error |
| AC27 | L2 + L3 | Amount left empty: toggle stays off and an Italian error is visible. Happy path from the inbox list to a completed approve takes ≤ 4 screens. | Toggle on with an empty amount |
| AC28 | L2 + L3 | After a wifi reply the host notification API returns no new row. After an emergency fixture the host list contains the emergency notice. Primary buttons read `Approva` and `Rifiuta`. | English CTA; host toast on Wi-Fi |

---

## UX / UI Quality

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Host approves a pending cost from the inbox without opening settings | L3: inbox → thread → `Approva`, ≤ 4 screens |
| Language | Italian on guest empty/closed copy and on host badges and actions | L2 asserts the strings in AC25–AC28 |
| Empty state | Guest with no messages sees the AC25 sentence | L2 empty fixture |
| Error state | 409 and 422 show the problem title in Italian | L2 forced 409 / empty amount |
| Destructive / legal copy | Cancel, decline, emergency, and the disclosure use the fixed sentences in the ACs | L2 asserts those sentences |

**Happy-path script:**

1. Start at `/app/short-rent/messages`.
2. Open the thread badged `Da approvare`.
3. Read the amount and the supplier, press `Approva`.
4. Done when the badge is gone and the transcript contains the guest holding line from AC15.

**Guest script:**

1. Open `/stay/:token` for a confirmed stay.
2. Send `Qual è il Wi-Fi?` with the wifi slot filled.
3. Done when the reply is the slot text and no document field is on the page.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/GuestThread.cs`, `GuestStayGuide.cs`, `GuestCostAuthorization.cs`, `GuestMessageDecision.cs` | Create — thread, guide slots, cost window, decision log |
| `Casazen.Core/Features/FeatureFlags.cs` | Modify — add `GuestMessagingHub`, default off |
| `Casazen.Core/Services/GuestMessageRouter.cs` | Create — ack lexicon, thresholds, AC9 overrides, keyword map |
| `Casazen.Infrastructure/External/JevDecisionClient.cs` | Create — one `POST /v1/systemone`, no call without a key |
| `Casazen.Core/Services/GuestSupportAgent.cs` | Create — Economy completion, tool allowlist, JSON parse |
| `Casazen.Core/Services/GuestCostGate.cs` | Create — AC14–AC19, uses the org platform customer |
| `Casazen.Web/Controllers/PublicStayMessagesController.cs` | Create — anonymous token routes, rate limit, idempotency |
| Hangfire jobs next to the existing check-in and stay-alert jobs | Create — route inbound; send AC20 milestones; expire the 60-minute window |
| `docs/runbooks/ai.md` | Modify — Jev caller, state allowlist, budget reservation, subprocessor fields |
| Frontend property stay-guide, `/stay/:token`, `/app/short-rent/messages` | Create — AC25–AC28 |
| Host app push | Modify — deep link only for AC8, AC14, AC15, AC17 |

**Complexity:** L  
**Migration:** yes — the four entities above, plus `OpenedBy` on `ServiceRequests` (default empty, so existing rows are not hub requests).  
**Dependencies:** `guest-check-in-portal`, `micro-marketplace-v0`. Does not depend on `unified-inbox` or `ai-copilot-messaging`.  
**Repos:** BE, FE, mobile (push deep link only)

Thresholds are constants: route 0.80, template 0.80, cost override 0.50, emergency 0.70, cancel window 60 minutes, thread life 7 days after checkout, state cap 1500 characters, agent max tokens 400. Jev questions stay in one HTTP request so extra judgments share the state tokens.

`OpenedBy=GuestHub` is the only request the above-cap block applies to. Supplier matching stays on Active suppliers for the property comune. `AiSupplierDiscovery` stays off.

The hub flag ships off. No Jev key is added to the repo or to a deployed environment by this spec. Turning the key on in any environment waits for the counsel item below; until then the keyword map is the analyzer and the agent path falls through to the host (AC10, AC12).

---

## Test expectations

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit on the router, gate, redaction, schedule, and idempotency, with Jev and Stripe faked | A spec review with no tests once implementation starts |
| L2 | Playwright on `/stay/:token` and the host inbox, titled `test('ACn: …')`, API routed | One smoke for the whole hub |
| L3 | Real API locally for AC25–AC28, titled per AC | A screenshot of the inbox |

Analyzer tests assert the outbound JSON (question ids, state allowlist, single request). They do not call TypeSafe.

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] TypeSafe (Jev) as a subprocessor: legal entity, region of processing, and GDPR transfer mechanism before `Jev:ApiKey` is set in any environment. Until those fields exist, the subprocessor entry stays `detailsPending` and production stays on the keyword map. Same bar as `docs/runbooks/ai.md` for the generative provider.
- [COUNSEL_REQUIRED] Off-session charge of the host's platform card, the 60-minute cancel window, and the guest-facing sentence that an intervention was organized. Product behavior is specified above; the legal wording of the host consent at toggle-on is not invented here.
- Guest identity documents stay on the check-in portal. The thread redacts them and does not file Alloggiati.
- Tourist tax in the checkout message is the amount already stored on the booking, labeled as a calculation, not as tax advice.
- Emergency dispatch waits for the host, including when auto-approve is on. The spec does not authorize CasaZen to send a supplier before that approval.

---

## Out of Scope

- OTA inbox sync and auto-send across Airbnb or Booking.com (`unified-inbox`, frozen).
- The generative copilot in `ai-copilot-messaging` (frozen): no frontier-model draft, no auto-send confidence loop, no per-org ARPU meter.
- AI supplier discovery and any web search.
- Booking changes: refunds, date changes, cancellations, price edits.
- Collecting identity documents, fiscal codes, or Alloggiati payloads in the chat.
- Machine translation of templates.
- Long-term tenancy threads.
- Charging the guest for an extra service.
- A review request after checkout.

---

## Open Questions

- Counsel on the TypeSafe subprocessor and on the host auto-approve consent text. Owner: product owner, before any Jev key or any live card charge. Until then AC10 and AC12 are the shipped behavior.
- No default euro cap: the host types `MaxAutoApproveEur`. Left as a product rule, not an open number.
