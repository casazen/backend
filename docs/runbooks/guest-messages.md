# Messages between the guest and the host (DB-14a)

One conversation per direct booking: the guest writes from the guest area of the booking site, the host answers from the booking in the
app. No chat in real time (each side reads when it opens the screen), no attachments, no automatic answers, no flag: the endpoints are always
on, and nothing produces a message or an email without a person writing.

## Model

| Table | What |
|---|---|
| `GuestThreads` | One per booking (`UIX_GuestThreads_BookingId`), created by the first message of either side. Holds `OrgId`, `PropertyId` (the host's reach on properties applies to it, `InScope`), and the figures that answer the host's screens without reading the messages: `LastMessageAt`, `LastGuestMessageAt`, `LastHostMessageAt`, `HostReadAt` (the guest's messages up to this instant are read), `HostNotifiedAt` / `GuestNotifiedAt` (when the last email to each side was queued). Updated by one atomic `UPDATE` per message, never read and written back. |
| `GuestMessages` | `Sender` (`Guest` = 0, `Host` = 1), `Text` (≤ 2000, plain text), `CreatedAt`, `IdempotencyKey` (unique per thread), `AuthorUserId` (the host user, an opaque id). Never edited or deleted by a user. |

Both are `ITenantOwned`. A thread goes with its booking and its property (cascade); a guest that is erased or anonymized takes the threads of
its bookings with it (`GuestDataEraser.DeleteMessageThreadsAsync`, called by the erasure, the anonymization and the retention job), so the
messages follow the retention of the guest's data.

## Open and closed

A booking has a thread only if it is a **direct** booking (`Source = Direct`: the guest has the guest area) that is `Confirmed`, `CheckedIn`
or `CheckedOut`. It is **open** until the end of the seventh day after the check-out day (Europe/Rome): `GuestMessageRules.ClosesAtUtc` is the
start of the check-out day + 8 days. It follows the booking, so a booking that moves its dates moves the end of the thread. After that the
thread is read-only: `POST` answers **409 `thread_closed`**, `GET` still works.

| Booking | Guest `GET`/`POST` | Host `GET` | Host `POST` |
|---|---|---|---|
| Direct, confirmed, open | 200 | `state` `Open` | 200 |
| Direct, confirmed, past the seventh day | `GET` 200 (`Closed`); `POST` 409 | `state` `Closed` | 409 |
| Cancelled, never confirmed, or not a direct booking | **404 `guest_booking_not_found`** (the same as a booking that is not the guest's) | `state` `Unavailable`, no messages | 409 |

## API

**Guest** (policy `GuestSession`, token of `POST api/public/guest/session`; see [`direct-booking.md`](direct-booking.md) § 8.4). The booking comes
from `IGuestSessionService.FindBookingAsync`, so a booking of another guest or site is the usual uniform 404.

- `GET api/public/guest/bookings/{code}/messages` → `{ state: "Open"|"Closed", closesAt, messages: [{ id, sender: "Guest"|"Host", text, createdAt }] }`
  (the latest 300, oldest first). Limited per IP by `PublicBookingLookup`.
- `POST api/public/guest/bookings/{code}/messages` `{ text }` with the header **`Idempotency-Key`** (8–64 letters, digits, `-`, `_`: a UUID) →
  `{ message, redacted, replayed }`. The same key twice in a thread is the same message: the second call answers the first one (`replayed: true`)
  and writes nothing (it also sends no email). 409 `thread_closed`, 422 `guest_message_text_invalid` / `idempotency_key_invalid`, 429.

**Host** (JWT, `booking.read` / `booking.write` and the host's reach on the property of the booking, 404 otherwise):

- `GET api/bookings/{id}/messages` → `{ state, closesAt, unreadCount, needsReply, messages }`. `Unavailable` means the booking has no thread: the
  screen shows no Messaggi.
- `POST api/bookings/{id}/messages` `{ text }` with `Idempotency-Key` → `{ message, redacted: false, replayed }` (`booking.write`). The host may
  write first. Answering marks the guest's messages as read.
- `POST api/bookings/{id}/messages/read` (`booking.write`) → the thread after the guest's messages are marked read.
- `GET api/messages?needsReply=true&propertyId=&page=&pageSize=` → the threads of the properties the caller reaches (`HostScope`, in SQL), the
  latest message first: `{ bookingId, bookingCode, propertyId, propertyName, guestName, checkInDate, checkOutDate, state, lastMessageAt,
  lastMessageSender, lastGuestMessagePreview, unreadCount, needsReply }`. `needsReply=true` is the «Da rispondere» of the Home: **open** threads whose
  last message is the guest's. `false` is the others; omitted is all.
- `GET api/messages/unread` (same policy and scope) → `{ "items": [ { "bookingId", "unreadCount" } ] }`: open threads with something unread only,
  at most 200, never any text. For the badges of the calendar and the lists.

**Alerts to the hosts of a new message (NF-65).** Every message of the guest alerts the hosts **through the decision point of the preferences**
(`INotificationPreferenceService`, `NewGuestMessage`; the last section of this page), the same for
a direct thread and for a conversation of a channel (OTA, copied by the sync of the messages, `GuestMessageReceived` with source `Ota`, handled by
`MessageReceivedNotifier`). Both go through `HostMessageAlertDelivery`:

| Channel | What | Default (PO-10) |
|---|---|---|
| Counter | the in-app notification of the bell (direct: type `guest-message`, entity the booking, so reading or answering the thread clears it; channel: type `ota-message`, entity the thread), published to the open sessions of the person. Delivery key = the message (and the person for a channel), so a re-delivered event writes it once | on |
| Mobile push | to the devices of that one person (`PushAudience.User`): direct route `/bookings/{id}/messages`, channel route `/messages/{threadKey}` (`PushRoutes.MessageThread`, `ota:{guid}` or `direct:{guid}`, mirrored by the app). Text: the property (and the channel) only | on |
| Email | to the person's **own** address (`User.Email`, never only the org's), template `guest-message-to-host` (direct) or `host-message-received` (channel), at most one per conversation and person every 10 minutes | off |

Who is told: the audience of the property (`HostNotificationAudience`: the person in charge while they reach it, and the administrators; a collaborator
«Solo alcuni» only the properties it was given) narrowed to who holds `booking.read` (the permission that opens the thread), or the administrators
for a conversation not mapped to a property. The quiet hours of the person (22:00-08:00, Europe/Rome) defer the push and the email to the instant
they end (`HostMessageAlertDueJob`, which asks the service again before it sends), never the counter. **No text, no guest name (channel) and no
address of the guest is in a push, an email or a notification.** An urgent message (`UrgentGuestMessage`) is alerted by the assistant (E05/F05)
through the same delivery. The OTA handler throws when a person could not be told, and the bus delivers the event again (nobody is told twice); the
send of a direct message never fails for it: it has no event to be delivered again, so a failure is logged and the whole alert is handed to a
Hangfire job (`HostMessageAlertDueJob.RetryAsync`, 30 seconds later, with the retries of Hangfire), which delivers it again without telling anybody
twice. An error while the permissions are read is not taken for «no»: it is thrown (`HasPermissionOrThrowAsync`), so the bus or the job tries again.

## Emails (the only writes to a third party)

Three, all in Italian (`EmailTemplates.DefaultCulture`), all the direct consequence of a message somebody just wrote, never of a timer or an event:

| Email | When | To | Content |
|---|---|---|---|
| `guest-message-to-host` | the guest of a direct booking sends a message | each host the preferences select, at their own address | the guest's name, the property and the dates, a link to the booking on its Messaggi tab (`/app/short-rent/bookings/{id}?tab=messages`). **Never the text.** |
| `host-message-received` | a guest writes on a channel | the same | the channel and the property, a link to the conversation (`/app/short-rent/messages/{threadKey}`). **Never the text, never the guest.** |
| `host-reply-to-guest` | the host sends a message | the guest's email | the host's name and the property, a link to "Le mie prenotazioni" with the code filled in. **Never the text.** |

At most **one per conversation and person every 10 minutes** (`GuestMessageRules.NotifyIntervalMinutes`): to the hosts the slot is a row of
`NotificationEmailSlots` per person and conversation, claimed with one conditional statement (an email held back by the quiet hours holds it until it
is due, so the messages of the night are one email in the morning); to the guest it is `GuestNotifiedAt` of the thread. Two messages at the same
moment queue one email, and the slot is given back if the email could not be queued (no address, provider off). The message is saved first: a
failure of the email never loses a message. A replayed key sends nothing. No email goes to an anonymized guest. `GuestThread.HostNotifiedAt` is
retired (no longer written).

## Alerts and the preferences of notifications

`GET` and `PUT api/me/notification-preferences` ([TECHNICAL.md](../TECHNICAL.md)): each person chooses, for each kind of event, the channels (mobile
push, email, the counter; browser push is not available yet) and the quiet hours. A person who never chose gets the defaults of PO-10; a new
guest message is a mobile push and the counter. `HostNotifyingPathsArchitectureTests` fails when a path that notifies a host about one of the eight
kinds of event does not ask the service (the old paths are listed with the epic that moves them).

## Privacy

- **The text is never in a log**: the logs carry thread, booking and message ids only. Nor is it in an email (above).
- The guest's text is stored with **card numbers** (13–19 digits that pass the Luhn check, spaces or dashes allowed) and **IBANs** (ISO 13616
  check digits) replaced by `[rimosso]` (`GuestMessageRules.Redact`); the answer says `redacted: true` so the screen can warn. The host's text is
  stored as written. The check-in has its own form for identity documents.
- Plain text only: the screens show it as text, never as HTML or links.
- One 404 for the guest: another guest's booking, another site's, a cancelled one, one that is not a code. A host never reaches the thread of
  another org (tenant filter, the org in every query, `HostScope`).
- Limits: `GuestMessageSend` per client IP (30 / 10 min), `GuestMessagePerSession` per guest session and booking (20 / h), `HostMessageSend` per
  signed-in person (60 / 10 min). Configuration `RateLimiting__{Policy}__PermitLimit` / `__WindowSeconds` ([`proxy-ip.md`](proxy-ip.md)). A 429 is
  the usual `rate_limited` with `Retry-After` and `retryAfterSeconds`.

## The hub (E05/F02/US01/T01, ADR-032, NF-61, NF-64)

The hub is one list and one thread model over **two sources that stay where they are**: the direct threads above and the conversations of the
channels (Airbnb, Booking.com, Vrbo, Expedia) that the sync copies from the channel manager into `OtaMessageThreads` / `OtaMessages`
([`octorate-webhooks.md`](octorate-webhooks.md) § 4c). `IMessageHubService` reads them; the direct endpoints above answer exactly as before. Always on:
with the connector off the OTA part is empty.

**Thread key.** Every conversation is named by an opaque key, `ota:{thread id}` or `direct:{booking id}` (the guid with dashes, lower case;
`MessageThreadKey`). The client hands back what the server gave and never builds one. Anything else is **400 `thread_key_invalid`**. A direct
conversation is named by its booking because it has one thread per booking.

**Endpoints** (policy `booking.read`, `booking.write` to mark read; every answer private and not cached):

- `GET api/messages?includeOta=true&needsReply=&propertyId=&channel=&bookingId=&unread=&kind=&q=&page=&pageSize=` → `{ items, totalCount, page, pageSize }`,
  one item per conversation, the latest message first: `{ threadKey, source: "Direct"|"Ota", channel: "Direct"|"Airbnb"|"Booking"|"Vrbo"|"Expedia",
  kind: "Reservation"|"Request", state: "Open"|"Closed"|"Archived", propertyId, propertyName, unassigned, bookingId, bookingCode, guestName,
  checkInDate, checkOutDate, lastMessageAt, lastMessageOrigin: "Guest"|"Person"|"Assistant", preview, unreadCount, needsReply }`. The client's
  default view is `needsReply=true`; the server returns everything when it is omitted. `q` finds the name of the guest, the booking code (the
  `XXXXX-XXXXX` form, any case) and the name of the property, anywhere in them: the text of a message is encrypted at rest and is **not searched**.
  `channel`, `bookingId`, `unread`, `kind` and `q` are read only with `includeOta=true`; without it the endpoint is the list of the direct threads above.
  The list is one `UNION ALL` of the two sources paged in the database, with a count next to it; the preview (the start of the last message, any of its
  authors, `null` for a message that is only a file) is read for the page only.
- `GET api/messages/unread?includeOta=true` → `{ items: [ { threadKey, bookingId?, unreadCount } ] }`: the direct threads (at most 200) and then the
  conversations of the channels (at most 200) with something unread; counters only.
- `GET api/messages/threads/{threadKey}` → the conversation: `{ threadKey, source, channel, kind, state, propertyId, propertyName, unassigned,
  guestName, unreadCount, needsReply, anonymized, booking, customer, allowedActions, messages, hasEarlierMessages }`. `messages` are the latest 300,
  oldest first: `{ id, direction: "Inbound"|"Outbound", origin, ruleCode, senderName, text, sentAt, readAt, deliveryState, attachment: { contentType } }`
  (`deliveryState` for an outbound message of a channel: `Created`, `WaitingAuthorization`, `Sent`, `Error`, `ManualVerify`; `ruleCode` is the situation an
  assistant message answered, `null` for the others and for every direct message until the assistant writes there too). Reading does not mark anything.
- `POST api/messages/threads/{threadKey}/read` → `{ threadKey, unreadCount }`. Direct: `IGuestMessageService.MarkReadAsync` (it also clears the caller's
  notifications). A conversation of a channel: `ReadAt` on its inbound messages and `UnreadCount` recounted, under the advisory lock of the thread the
  sync takes, so a pass that stores a new message and a read of the host do not undo each other.
- `GET api/messages/{messageId}/attachment` → the file (below).

**The booking card** (`booking`, ADR-032-R8): `{ bookingId, bookingCode, guestName, checkInDate, checkOutDate, guests, adults, children, status, source,
channelLabel, currency, amounts }`, the dates as Europe/Rome days, `currency` the one of the property. It is shown only for a booking of the **same
property** as the conversation (a stray link never shows a stay under a house it does not belong to), and so the list, the filter by booking and the
badges carry the booking of a conversation only under the same rule (a conversation moved to another house by a new mapping shows no stay of the first
one). `amounts` is absent for a caller without `payment.read` (SR-D7, the same reach as the amounts of the booking itself). For a booking whose money is
the channel manager's (a booking of a channel, or a reservation of the booking engine of the agency that the import made:
`BookingAmountSourceRules.HoldsChannelManagerMoney`, NF-77) it holds the four figures the channel manager gave
(`commission`, `net`, `paid`, `balance`: a figure that is not available is **left out**, never zero and never derived; the commission often arrives late)
and `total` when set; for any other booking, one CasaZen sent to Octorate included (it has a reservation there, but its money is CasaZen's), `total` and
`paid` (from its payments). **Customer link** (`customer: { guestId }`): the guest of the booking, for
every conversation that has one; a request has no booking and so no link. The route of the profile is built by the client.

**Unassigned conversations (OCT-04).** A conversation whose listing has no confirmed mapping has no property (`PropertyId` null, `unassigned: true`).
It is listed and read only by a scope that reaches every property of the org (`HostScopeQueryExtensions.InScope` for `OtaMessageThread`); a collaborator
«Solo alcuni» and the account in no team never see it, and it is never shown under a property it was not mapped to. It can be read and not answered:
`allowedActions` is all false with `reason: "mapping_not_confirmed"`, the same code the reply answers with. The same holds for a conversation whose
property sits in a row of the mapping that nobody decided (`IChannelMappingService.IsWriteBlockedAsync`).

**What the host may do** (`allowedActions: { canReply, contactDetailsAllowed, attachmentsAllowed, autoReplyAllowed, reason }`): the one table
`OtaChannelCapabilities` for a conversation of a channel, by channel and kind. All four channels can be answered; `contactDetailsAllowed` is false for an
Airbnb request (no link, address or number before the booking, OCT-11); `attachmentsAllowed` and `autoReplyAllowed` are false for every channel until the
product owner confirms the policy (D3, D4: `OtaChannelCapabilities.AttachmentsPolicyConfirmed` / `AutoReplyPolicyConfirmed`, the one place to change).
A direct thread can be answered while it is open (`reason: "thread_closed"` after), with contact details, and `attachmentsAllowed` and `autoReplyAllowed` are
false there too, from the same two switches (`OtaChannelCapabilities.Direct`).

**The attachment proxy (ADR-032-R5).** The file of a message is not copied: the address the channel manager gave is stored with the message and used by the
server only. `GET api/messages/{messageId}/attachment` checks the message against the caller's org and scope (**404** `message_not_found` for a message of
another org, outside the reach of a collaborator, or missing; **403** for a caller without `booking.read`), then `OtaAttachmentFetcher` fetches it:

- the address is **untrusted** (a channel wrote it): only an `https` address of a host name on the default port, no user or password, not `localhost`
  or an internal name; the connection itself goes to **public addresses only** (the name is resolved where the socket is opened, so loopback, private,
  link-local (the metadata service of the cloud), shared and unique-local addresses are refused whatever a name points at); a redirect is followed at most
  three times and each target is checked like the first address; no cookie, no credential of the channel manager is sent;
- 30 seconds for everything, at most **20 MB** (the declared length is checked first), and the answer must be `image/jpeg`, `image/png` or
  `application/octet-stream` (served as a download, with the name `attachment`); an HTML page, an SVG, a PDF are refused;
- the answer is the file with `Cache-Control: private, no-store`, `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`;
- a file that is gone, expired (the usual 403/404/410 of a signed address), too big, of another type or unreachable is **404 `attachment_unavailable`**
  (the screen says "attachment no longer available"). **The address is in no answer, no header, no error and no log**; the thread detail says only whether a
  message has a file and its type (`attachment.contentType`).

**Privacy.** The words, the names and the addresses are the guests' personal data: logs carry ids and counters only (the fetcher logs a status or the kind of
error, never an address). The money of the card needs `payment.read`. An anonymized conversation (`anonymized: true`) has no words and no names, and a later
read of the channel manager brings none back (`OtaMessageThread.AnonymizedAt`).

**Demo.** The scenario `message-hub` of the showcase (`DemoShowcaseCatalog.MessageThreads`) writes three invented conversations at reset (all saying TEST,
guests at `@example.com`): a direct stay of the lake villa waiting for an answer, an Airbnb conversation about the stay at Casa Borgo di Prova, and a
Booking.com request for the villa with no booking. They are rows of the hub only (marked `TestDataEntities.OtaMessageThread` / `GuestThread`); there is no
connection to a channel manager behind them.

## The hub, write side (E05/F02/US02, ADR-032, ADR-023, NF-63)

**Reply** (`POST api/messages/threads/{threadKey}/reply`, policy `booking.write`, header `Idempotency-Key` 8 to 64 characters, body `{ text }`, rate limited
per person like the direct reply). Answer `{ channel, threadKey, message, state, replayed, errorCode }`; `state` is the delivery state of the message:

| State | Meaning | HTTP |
|---|---|---|
| `Sent` | the channel manager accepted it (also a direct message, and an e-mail that was queued) | 200 |
| `Created`, `WaitingAuthorization` | the channel manager has it and sends it (it reports the next state; the copy of the conversation completes the row) | 200 |
| `Error` | a typed refusal (a token it does not take, the chat module not in the license, a request it rejects), with its `errorCode`; the guest got nothing | 200 |
| `ManualVerify` | no answer, a 5xx or an answer that cannot be read **to the post of the message**: the message **may** have gone (the same failure of the read of the thread that comes before the post is not this: nothing was sent, the row goes away, the answer is 502 `octorate_unavailable` and the same key sends the message again). **This call never sends it again**; the copy of the conversation matches it with the message that arrived (same text, within ten minutes), or a person checks on the channel | 202 |

The checks, in order: the conversation within the reach of the caller (**404** `thread_not_found`, also for another org's and for the unassigned one when the
caller does not reach every property), the guest not erased (**409** `thread_closed`; also a direct thread past the seventh day after the check-out), a
property whose mapping is decided and the connector on (**409** `mapping_not_confirmed`, OCT-04: nothing is sent; with the connector off **409**
`octorate_not_connected`), the key (**422** `idempotency_key_invalid`), the rules of the channel (**422** `message_text_invalid`; **422** `channel_rule_violation`
for a link, an e-mail address or a phone number on an Airbnb request, OCT-11, `MessageChannelRules`), and then the message with the same key, which is
answered with `replayed: true` and sends nothing (OCT-10). A message that went out marks the inbound messages as read and the conversation as answered.

**What is stored and when.** The outbound row (`OtaMessages`: `Direction` Outbound, `Origin` Person or Assistant, `RuleCode`, `SentByUserId`, the key) is written
**before** the call and committed under the advisory lock of the thread; the call is made with no lock held; the outcome (the Octorate id, the state) is stored
under the lock again. If the copy of the conversation read the thread in between and stored the message as one of the host side, that twin is removed (same
Octorate id): the row that knows the key and the person stays. A row left in `Created` with no id for more than two minutes (the process stopped) is turned into
`ManualVerify` by the next send with its key. A 429 (Octorate's, or the empty bucket of the org) removes the row and answers 429: the same key can be sent later.

**First message** (`GET api/bookings/{id}/contact-options`, `POST api/bookings/{id}/messages/new`): the channel is the conversation the booking has on a channel
(never one whose mapping is undecided), else the direct conversation while it is open, else, for a booking of a channel with no chat (Agoda, TripAdvisor,
another portal), an **e-mail** when the stay is an active one (confirmed, checked in or checked out and not a no-show: the statuses of `AlloggiatiStatusRules`;
a pending or cancelled booking and a no-show get `no_conversation`, and the contact of their guest is not even read) and the channel manager says it holds the
guest's address (`emailAvailable`: a flag; the address is not asked for to answer the options), else `None` with the reason. The e-mail reads the contact of **that one booking** (`POST /rest/v2/reservation/{acc}/{refer}/contactInfo`, a special
permission that Octorate gives only to the registered static IP addresses of CasaZen, S7/D24, and logs on every read): until they are registered the answer is
**409** `contact_info_unavailable` and nothing is queued. A read that Octorate answered is recorded as `ContactInfoRead` in the activity log (booking, person,
purpose `message_to_guest`); the address and the phone number are in no answer, no log line and no row, and go only to the queue of the e-mails
(`EmailTemplates.HostGuestMessage`, Italian or English by the guest's country, the text of the host HTML-encoded, reply-to the organization's contact address).

**Templates** (`api/message-templates`, `booking.read` to list and render, `booking.write` to change): name, language (one of the 14 of the listing), a body of up
to 2000 characters with `{{guest_name}}`, `{{property_name}}`, `{{booking_code}}`, `{{check_in_date}}`, `{{check_out_date}}`, `{{check_in_time}}`,
`{{check_out_time}}` and `{{host_name}}`; unique per organization, name and language (**409** `message_template_exists`). A variable that is none of the eight is
refused when the template is saved (**422** `template_variable_unknown`). `POST …/{id}/render` with `{ threadKey }` fills the variables with the booking and the
property of the conversation (read within the caller's reach; dates as Rome dates in the language of the template, e.g. `6 novembre 2026`; times as `HH:mm`
on the property's clock) and answers **422** `template_variable_missing` naming the variable when the conversation has no value for it (a request with no booking
has no dates): the text never holds a literal `{{…}}`. `GET api/messages/threads/{threadKey}/channel-templates` lists the templates of the channel manager for a
conversation of a channel, read only, to copy.

**Translation** (`POST api/messages/translate`, `{ threadKey, messageId | text, targetLanguage }`): for the screen only, **never stored** (the message keeps its
original), always marked `machineTranslated`. **CasaZen's AI only with `Features:MessageTranslationAi` on, which is off by default** (FD-21, AI-06: what a guest
wrote goes to an external sub-processor, [ai.md](ai.md); the product owner turns it on after deciding the transfer): `Economy` tier, behind the platform budget, under
the AI limits of the user and of the org, with the text scrubbed (`PiiScrubber` takes out e-mail addresses, phone numbers and fiscal codes; the names of the guest
and of the host become `[[N1]]…` and are put back in the translation, a name whose words are all short, «Li Wu», is replaced as a whole name and the particles
de, da, di, van, von, al, el are left alone; **a document number, an IBAN, a street address and the names of other people are not recognized**). With the flag off the
provider is never asked: the channel manager's translation of that message is the only one, and it is also the one used when the AI is not configured, the budget
is used up, or the provider fails; **422** `translation_unavailable` with neither (a text the host is still writing has no message on the channel manager, so with
the flag off every draft is 422). **422** `translation_language_invalid` (not one of the 14), `translation_input_invalid` (a message and a text together, or neither).

**Demo.** The scenario `message-templates` of the showcase (`DemoShowcaseCatalog.HostMessageTemplates`) writes three invented templates at reset (a welcome in Italian
and in English and a reminder of the departure, all saying TEST, together using the eight variables), marked `TestDataEntities.MessageTemplate` and deleted by the
next reset; a template the host wrote is never touched. The conversations they can be tried on are the ones of `message-hub`; the direct one is answered as always,
while the two of a channel go through the channel manager, which a demo without a real connection does not have: the answer is a refusal of the connector (the
state `Error` with its code, or **409** `octorate_not_connected` when the connector is off), never a message that looks sent.

## Not here (deferred)

Automatic answers and the keyword router, the disclosure line, emergencies, `GuestMessageDecision`, `responseTimeMinutes`, the guest's unread badge, the guest's language (the emails are Italian), attachments, the "Scrivi" shortcut from the
guests page, quick replies.
