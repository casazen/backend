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

**Notifications of a new message.** Every message of the guest also queues a push and an in-app notification to the hosts of the booking
(type `guest-message`, route `/bookings/{id}/messages`, text without guest name or message), and reading or answering the thread clears the
caller's notifications of it; the bell is updated in real time. See [`in-app-notifications.md`](in-app-notifications.md) §§ 9-10.

## Emails (the only writes to a third party)

Two, both in Italian (`EmailTemplates.DefaultCulture`), both the direct consequence of a message somebody just wrote, never of a timer or an event:

| Email | When | To | Content |
|---|---|---|---|
| `guest-message-to-host` | the guest sends a message | `Org.ContactEmail` | the guest's name, the property and the dates, a link to the booking on its Messaggi tab (`/app/short-rent/bookings/{id}?tab=messages`). **Never the text.** |
| `host-reply-to-guest` | the host sends a message | the guest's email | the host's name and the property, a link to "Le mie prenotazioni" with the code filled in. **Never the text.** |

At most **one per side every 10 minutes per thread** (`GuestMessageRules.NotifyIntervalMinutes`): the slot is claimed with one conditional
`UPDATE` of `HostNotifiedAt` / `GuestNotifiedAt`, so two messages at the same moment queue one email, and the slot is given back if the email
could not be queued (no address, provider off). The message is saved first: a failure of the email never loses a message. A replayed key sends
nothing. No email goes to an anonymized guest.

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

## Not here (deferred)

Automatic answers and the keyword router, the disclosure line, emergencies, `GuestMessageDecision`, `responseTimeMinutes`, the guest's unread badge, the guest's language (the emails are Italian), attachments, the "Scrivi" shortcut from the
guests page, quick replies.
