# Runbook: the assistant of the guests' messages (drafts)

E05/F05/US02/T01 (NF-66, NF-67, PO-08, PO-09, ADR-032). Related: [`ai.md`](ai.md) (provider, budget, rate limits, subprocessors),
[`feature-flags.md`](feature-flags.md) (`MessageAssistant`), [`guest-messages.md`](guest-messages.md) (the hub of the messages),
[`deploy-checklist.md`](deploy-checklist.md) § 2 (`Features__MessageAssistant`, `Assistant__*`).

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
   (urgent and high: `High`; normal: `Medium`) and a deadline of `Assistant:Escalation`; no draft. **An urgent message** raises the same «Da
   fare», alerts the people who reach the property (`UrgentGuestMessage`, through their preferences, with the route of the conversation and
   no text) and still gets its draft.
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
become `[guest]` and `[host]`; the code of the booking and its references on the channel `[booking]`; any run of six or more digits
`[code]`. The model never sees the name of the guest: it writes `{{guest_name}}`, the server fills it in. The provider scrubs every prompt
once more. **Not recognized**: the names of anyone the conversation does not know, a street address, an IBAN written with spaces. A
placeholder left in a draft lowers its compliance and is visible to the person who approves it.

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
