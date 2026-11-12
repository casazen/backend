# Runbook: the assistant of the guests' messages (drafts)

E05/F05/US02/T01 and E05/F05/US03/T03 (NF-66, NF-67, PO-08, PO-09, OCT-11, ADR-032). Related: [`ai.md`](ai.md) (provider, budget, rate limits,
subprocessors), [`feature-flags.md`](feature-flags.md) (`MessageAssistant`), [`guest-messages.md`](guest-messages.md) (the hub of the messages),
[`octorate-webhooks.md`](octorate-webhooks.md) § 4c (the copy of the conversations), [`deploy-checklist.md`](deploy-checklist.md) § 2
(`Features__MessageAssistant`, `Assistant__*`). The last section, «Octorate's own assistant kept off», is about the assistant of the channel
manager, not CasaZen's, and runs whether or not CasaZen's is on.

## What it does

A message of a guest, in a conversation of a channel (Airbnb, Booking.com, Vrbo, Expedia, copied from Octorate) or in the guest area of a
direct booking, becomes a **draft that a person approves**. Nothing is sent to a guest by itself: until the auto-send gate of E05/F05/US03
is registered, the gate in place (`NeverAutoSendGate`) refuses every draft with `auto_send_not_enabled`, and every draft goes to the
approval queue (`GET api/approval-queue`, permission `approval.decide`).

The chain:

1. The event `GuestMessageReceived` (the sync of the channels records it; the guest area records it with the source `Direct` since this
   task) reaches `AssistantMessageHandler`, which queues `AssistantDraftJob` on Hangfire. **Nothing is queued** while
   `Features:MessageAssistant` is off, nor for an organization whose own switch is off (`OrgAssistantRules.Enabled`, off by default): the
   messages of those organizations are not looked at and no row of the decision log is written for them.
2. The job (one run per message, 2 retries of Hangfire) runs `AssistantDraftService.ProcessInboundAsync` under the tenant of the org of the
   event. Each step that fails ends in a decision `NotHandled` with a code, and no further call to the AI:

   | Step | Codes |
   |---|---|
   | (a) the conversation, a property, a channel that takes an answer, the switches, a provider | `thread_not_found`, `message_not_found`, `property_unassigned` (an unassigned conversation has no knowledge base), `thread_closed`, `octorate_not_connected`, `mapping_not_confirmed` (OCT-04), `assistant_disabled` (the org or the property), `ai_provider_not_configured` |
   | (b) the message (skipped when a person asks for a draft) | `message_too_old` (older than `Assistant:MaxMessageAgeHours`), `superseded` (the guest wrote again), `host_already_replied` |
   | the AI | `ai_budget_exceeded`, `ai_provider_error`, `ai_invalid_answer` (the answer of the draft could not be read) |

3. (c) The last eight messages, **minimized** (below), are classified on the Economy tier; the setting of the situation is resolved
   (`IAssistantSituationService.ResolveAsync`). A situation in mode `Escalate` (`mode_escalate`), a forbidden topic (`forbidden_topic`) or a
   topic outside a non-empty list of allowed ones (`topic_not_allowed`) ends in **`Escalated`**: a «Da fare» `MessageToReply` with the action
   `ReplyToMessage`, about the conversation (`OtaMessageThread` and its id, or `Booking` and the id of a direct booking), with the urgency
   (urgent and high: `High`; normal: `Medium`) and a deadline of `Assistant:Escalation`; no draft. There is **one «Da fare» per conversation
   while it is open**: a later message that is more urgent (or due sooner) raises it (`IInternalActivityService.RaiseUrgencyAsync`: urgency
   and reason of the more urgent cause, earlier deadline), and a less urgent one never lowers it. **An urgent message** raises the same «Da
   fare», alerts the people who reach the property (`UrgentGuestMessage`, through their preferences, with the route of the conversation and
   no text) and still gets its draft. The alert is done **before** the row of the log is written (its keys are the message and the person,
   so a run repeated before the row exists tells nobody twice); a person who cannot be alerted is handed to the job of the alerts, and an
   alert that cannot even be handed to it fails the run, which Hangfire repeats.
4. (d) The facts of the knowledge base (`RetrieveAsync`) for the topics of the situation; the draft is the template of the situation filled
   by `MessageTemplateRenderer` when every variable resolves, else the model writes it on the Standard tier from the facts. The server
   fills `{{guest_name}}` and the other variables and appends the signature.
5. (e) The confidence: `round(situationMatch × factCoverage × ruleCompliance, 3)` (`AssistantConfidence`). **The product of the three
   components is a proposal for the product owner to confirm.**
6. (f, g) The gate; with the default one the draft goes to the queue (`MessageDraft`, the inbound message as its `ItemId`, the property, the
   action `SendGuestMessage`, the score, the situation, the conversation), which raises the «Bozza pronta» alert; then the row of
   `AssistantDecisionLogs` is written: situation, urgency, sensitive categories, score and components, model (`Ai:Model`) and version
   (provider and tier of the last call), version of the instructions, sources (`entry:{id}`, `listing:{field}`, `template:{situation}`), gate
   failures, the item of the queue, the «Da fare». **Ids and codes only, never a word of the guest or of the draft.** One row per message:
   processing it twice finds the first.

A person who answers the conversation (the hub, the guest area, or the approval of a draft) closes the «Da fare» of its escalated messages
(`AssistantEscalationCloser`, `ActivityClosedAt`). An automatic send of the assistant, which has no person behind it, closes nothing.

## What the provider reads (PO-09)

Every text of a prompt (the messages and the facts) goes through `AssistantPromptBuilder.Minimize`: the e-mail addresses, phone numbers and
fiscal codes (`PiiScrubber`) become `[email]`, `[tel]`, `[cf]`; the names of the guest (booking and channel) and of the host (`Org.HostName`)
become `[guest]` and `[host]` (whole words, in any case, with the accents and the typographic apostrophes folded: «Jose» is found for «José», «D’Angelo» for «D'Angelo»); the code of the booking and its references on the channel `[booking]`; any run of six or more digits
`[code]`. The model never sees the name of the guest: it writes `{{guest_name}}`, the server fills it in. The provider scrubs every prompt
once more. **Not recognized**: the names of anyone the conversation does not know, a street address, an IBAN written with spaces. A
placeholder left in a draft lowers its compliance and is visible to the person who approves it. The facts are the host's own texts: what
the host wrote in an entry of the knowledge base (a Wi-Fi password included; the facts read from the listing never carry it) goes to the
provider, minimized like the rest, when its topic is the one of the situation. The simulator knows no name of a guest (the example is the
host's own text): it takes out the contacts, the codes, the long numbers and the host's name.

## Endpoints (behind the flag, private answers)

| Route | Policy | What |
|---|---|---|
| `POST api/assistant/threads/{threadKey}/draft` | `BookingWrite` + the conversation in the caller's reach | A draft now of the last message of the guest: age and «already answered» not looked at, nothing sent, the waiting draft of the conversation superseded (rejected with the reason `regenerated`, which the lessons ignore). 409 `assistant_no_guest_message`. `[AiRateLimit]` |
| `POST api/assistant/simulate` | `PropertyWrite` + the property in the caller's reach | `{ propertyId, text, language? }`: steps (c) to (e) on the example with the current settings and sample values; answers situation, urgency, categories, confidence, draft, facts used, `wouldAutoSend`, `gateFailures`. **Writes no log, no queue item, no «Da fare», sends nothing**, works with the switch of the org still off, spends the AI budget. 422 `assistant_simulation_invalid`. Limits: `AssistantSimulate` (20 / h per person) and `[AiRateLimit]` |

## Configuration (`Assistant__*`)

| Variable | Default | Meaning |
|---|---|---|
| `Assistant__MaxMessageAgeHours` | `6` | The oldest message (hours, 1 to 168) the assistant looks at by itself. |
| `Assistant__Escalation__UrgentHours` / `__HighHours` / `__NormalHours` | `1` / `4` / `24` | Deadline of the «Da fare» of an escalated message (hours, 1 to 168). |
| `Assistant__Trust__MinDays` / `__MinDrafts` / `__MinApprovedWithoutEditPercent` | `14` / `10` / `90` | PO-08: when a situation may be proposed for the automatic modes. Read by E05/F05/US03. |

A value of the first two rows out of its range is brought within it; nothing fails the startup.

## Activation checklist (PO-09), in this order

1. **Privacy notice of the guests**: it says that the messages may be read by an automatic assistant of the host, which prepares answers a
   person approves (product owner and legal, D14).
2. **Processing agreement** with the hosts (the AI provider as a subprocessor of CasaZen).
3. **`Ai:Subprocessor` details** of the provider in effect: `Ai__Subprocessor__Entity`, `__Region`, `__TransferMechanism`, `__Source`;
   check `GET /api/legal/subprocessors` on test (`detailsPending: false`). See [`ai.md`](ai.md) § GDPR.
4. **`Ai__Provider` not `Stub`**, with `Ai__ApiKey`: without a provider in effect every message is `ai_provider_not_configured`.
5. **Then the flag**: `Features__MessageAssistant=true` (test first). Then each organization switches its own assistant on in its rules.

## Checks after a deploy

- With the flag off: `POST api/assistant/simulate` answers 404 before authentication; no `AssistantDraftJob` in the Hangfire dashboard.
- With the flag on (test): a simulation answers a situation and a draft, and `AssistantDecisionLogs`, `ApprovalQueueItems` and
  `InternalActivities` do not grow; a message of a guest of an org with the switch on gets a row in `AssistantDecisionLogs` and, when it is
  `Drafted`, an item in `GET api/approval-queue`.

## Rollback

No migration and no new table: the decision log and the queue already exist (E05/F05/US01, E05/F04). Turning the flag off stops the handler
and the job (a job queued before does nothing). The rows written stay (ids and codes); the drafts that wait in the queue expire in 7 days
or are rejected by a person.

## Known limits

- The urgent alert uses the texts of the alert of a new message (the event type and the preferences are its own; its push and notification
  have a key of their own, so a person who had the alert of the new message gets the urgent one too). Its push waits for the quiet hours
  like the alert of a new message (only a legal deadline overrides them, PO-10).
- The routines (NF-26) do not start on the messages of the guest area of a direct booking: the event of a direct message is the assistant's.
- `ruleCompliance` of a draft written from a template does not check its language (a template has none).

## Octorate's own assistant kept off (E05/F05/US03/T03, OCT-11, D4, NF-67)

Octorate has an assistant of its own for each chat thread (`aiAssistant` in the API, the flag `aiDriven`, the message attribute `AI_SKIP_DISABLE`).
Left on, a guest would get an answer from it and another from CasaZen, which is the inbox of these threads. So **CasaZen keeps that assistant off on every
thread it manages, whether or not CasaZen's own assistant is on** (AC-NF67-6: verified at every sync). It needs no flag and no configuration of its own.

1. **What the pass reads.** Every pass of the copy of the conversations (the webhook, `ota-message-reconcile` every 10 minutes, the nightly deep pass)
   reads the thread, stores `aiDriven` in `OtaMessageThreads.AiAssistantOn` (`null`: the thread does not say) and records `OtaThreadSynced` with it.
2. **What the guard does.** `OctorateAiAssistantGuard`, a consumer of that event (registered in `AddMessageSync`, nothing while
   `Features:OctorateConnector` is off), calls `PATCH /rest/v1/chat/{accommodation}/threads/{thread}/aiAssistant?enabled=false`
   (`IOctorateApiClient.SetThreadAiAssistantAsync`) when the pass read the flag on **or did not read it** (a thread seen for the first time), and,
   once Octorate accepted the call, stores `AiAssistantOn = false`. The call says the value it sets, it does not toggle: a repeat is safe. **One call
   for each thread and pass, none for a thread that is off**: an event whose flag reads off, and a thread the copy already holds as off (an earlier
   delivery of the same event, or a later pass), cost nothing.
3. **Verified at every pass, not once.** While `aiDriven` reads on, the key of the event has the ten minutes of the pass in it
   (`ota-thread:{thread}:{last message}:on:{yyyyMMddHHmm}`, `MessageSyncService.SyncedEventKey`), so each pass that finds it on tells the guard: a person
   who switches the assistant on again in Octorate, or a call that failed, is found within the 10 minutes of the reconciliation and the call is made
   again. The webhook and the pass of the same ten minutes are one event. A flag that reads off or is not said keeps the key it had (once for each
   newest message), so an assistant that is off costs nothing.
4. **Only what CasaZen manages.** A thread with no property (its listing has no confirmed mapping) and a thread whose property sits in a mapping nobody
   decided (`IChannelMappingService.IsWriteBlockedAsync`, the one definition every write to Octorate asks, OCT-04) are left as they are: CasaZen writes
   nothing to a channel for them, and the host's own setting on the channel manager is not ours to change. The thread gets its property at the first
   pass after the confirmation, and the next pass that reads the flag on switches it off.
5. **When Octorate refuses.** One `Warning` line with the thread, the organization and the code, never a name or a word of the guest:
   `octorate_unavailable` (5xx, no answer), `octorate_rate_limited`, `chat_not_licensed` (no `chat` module in the license), `octorate_not_connected`,
   `octorate_not_found` (the thread is gone), `octorate_request_rejected`, `octorate_auth_failed`. **The event is not failed** (the sync and the other
   consumers are not affected), the copy keeps the flag Octorate reported, and the next pass tries again. The pass leaves 5 calls of the organization's
   bucket (100 a minute) to the people who use CasaZen: down to that reserve the guard sends nothing and says `octorate_rate_limited`. Anything that is
   not a refusal of Octorate (the database after a call that was made) rises and the dispatcher delivers the event again, which the call, being
   idempotent, survives.
6. **The account-level message agent has no API.** Octorate also has an agent for the messages that is set for the whole account. The API has no
   operation to read or change it, so CasaZen cannot switch it off or verify it, and **does not pretend to**: **the host leaves it off** in Octorate,
   and the text of the connect wizard of the connector (E02/F01/US01/T04) asks for that. A thread answered by that agent is not seen by the guard.

**Documented, not proven.** The sandbox has no `chat` module in its license (`octorate-integration-facts.md`: every chat operation answers 403), so the
call has not been seen answered by Octorate: the way the value travels (the query parameter `enabled`) follows the operations of the same API that set a
state (`?status=` of the check-in), and `OctorateApiClient.AiAssistantPath` is the one place to change if Octorate wants another. The first real
verification is the first licensed property (the test property needs the `chat` module):

- After the first pass the thread must read `aiDriven: false` in Octorate and `AiAssistantOn = false` in `OtaMessageThreads`; the log has «the
  assistant of Octorate is switched off» with the thread id.
- `octorate_request_rejected` (400, 405, 415) at every pass: the shape of the call is not the one Octorate wants.
- The guard says «switched off» for the same thread at every pass: Octorate accepts the call and ignores it (the flag reads on again at the next
  pass). Octorate's assistant is then not kept off, and the shape of the call has to be fixed.

**Rollback.** No migration, no table, no configuration. `Features:OctorateConnector` off stops the guard; the threads already switched off stay off in
Octorate (nothing switches them back on); a revert of the change leaves the copy as it is.

**Known limits.** A guest message may be answered by Octorate's assistant before CasaZen has read the thread for the first time (the webhook, then the
event, then the dispatcher: seconds to a minute); and a person who switches it on again is found at the next pass, at most about 10 minutes later. The
guard never switches Octorate's assistant ON: the call it makes always sets off.
