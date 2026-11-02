# Runbook: «Gestisci con AI» (the assistant of the adempimenti)

The button of the page of the adempimenti (`/app/short-rent/compliance`). It takes charge of what the cockpit lists
(`GET /api/compliance/summary`) and that CasaZen can really do, and says plainly what it cannot. Code:
`Casazen.Infrastructure/Services/ComplianceAutopilotService.cs` (plan and run), the rules in
`Casazen.Core/Services/AutopilotRules.cs` (pure, no database), the wording of the chat messages in
`AutopilotMessageWriter.cs`, the API in `ComplianceAutopilotController.cs`. No table, no migration: the history it needs (how often
and when a stay was asked) is read from `GuestCheckInSessions`.

## What it does, and only that

| Item of the cockpit | The assistant | Condition |
|---|---|---|
| Guest data to complete (`CompleteGuestCheckIn`) | issues a new check-in link and queues its e-mail (`IGuestCheckInLinkEmailQueue`, the real outcome stays on the session); where the guest has a phone number it also gives back a message with the link, in the guest's language, that the host sends by WhatsApp or SMS with one tap | the host may write on the property; no submitted form; asked fewer than 3 times; not asked in the last 24 hours; an e-mail address CasaZen can send to, or a number that can be written in international form |
| Departure to close (`CheckOut`) | `IStayLifecycleService.CheckOutAsync` with `new StayCheckOut(false)`: the same check-out as `POST /check-out`, which declares nothing (the property is not declared ready) | checked in, last day **before today**, the host may write on the property, and the guests are not being asked for their data (see below) |
| Alloggiati to send / failed | nothing: it says whether the data are ready for the portal (`ready_to_upload`), or what is missing (`data_missing`, `codes_to_complete`, `stay_days_invalid`) | always the host's |
| Properties to activate, turnovers | nothing (the CIN, the documents, "the property is ready" are the host's) | the web app shows a count |

It never writes a datum of a guest, never fills an official code, never declares a communication sent, never closes today's departure
nor one whose arrival was never registered.

**Why a departure waits for the data of its guests.** The check-in link works only while the stay is `Confirmed` or `CheckedIn`
(`GuestCheckInService`): closing the stay would cut the answer of a guest who was just asked. So a departure whose guests are `Ready` or
`Waiting` is `Waiting` (`awaiting_guest_data`); when the guests cannot be asked any more (no contact, asked three times, the form
submitted) it is closed.

## Limits (fixed in `AutopilotLimits`)

3 requests per stay (any link issued counts, also by the daily job or by the host), 24 hours between two, 20 guests and 30 departures per
run (the rest is counted in `beyondLimit`), one run at a time per org (per process; the cooldown does the rest between replicas).

## The AI

Only the wording of the chat messages. One call per run for all the messages (a text per language and per reminder), Economy tier, behind the
platform budget and the per-user and per-org AI rate limit (the controller takes **one permit right before the call**, so a run of departures
only never spends one). The prompt carries a language and the number of the reminder, **nothing of a guest, a stay, a property or the host**:
the names, the property, the arrival day and the link are put in by CasaZen afterwards. The answer must pass `AutopilotChatText.Check` text by
text (no digit, link, address, markup, no mention of authorities, fines or sanctions, only `{nome}`, `{struttura}`, `{data}`, `{link}`);
a text that does not pass is replaced by the plain text of CasaZen (`it`, `en`, `de`, `fr`, `es`). Cached per org. Without a provider, with the
budget gone, over the rate limit or on any failure the plain texts are used and `aiReason` says why: never an error for the host.

## What it cannot do, on purpose

- **Send to the Questura (Alloggiati Web) or to ISTAT.** CasaZen has no web service client (`alloggiati.md`, "Web service: not implemented"),
  and no Octorate integration exists in the code yet (only the requirements). The assistant prepares and checks; the host uploads on the portal.
- **Send WhatsApp or SMS by itself.** There is no sender (`NotificationChannelType` has the values, nothing implements them). The message is
  given to the host, who sends it from his own phone; a provider can be added behind the same item (channel `chat`) without changing the rules.

## Verify

`GET /api/compliance/autopilot` never changes anything: use it first. `POST .../run` with `{ "keys": ["Departure:<bookingId N>"] }` runs one
item. The logs hold counts and codes, never a name, an address, a number or a text.
