# Reviews of the channels (E05/F06/US01, NF-69, OCT-12, OCT-13)

The guest of a booking that came through a channel leaves a review on that channel. CasaZen reads it through the channel manager, shows it to the host, and lets the host
**answer it once** (the public reply) and **write the host's own review of the guest** while the channel's window is open. Everything goes through the channel manager (Octorate
is never named to a host or a guest: the screens say «channel manager» and the channel). The reviews of the booking site (`site-reviews.md`) are another table and keep working
as they always did; the lists of the booking and of the message hub show both.

**Airbnb only.** The channel manager integrates the reviews of Airbnb and of no other channel (S6): Booking.com, Vrbo and Expedia get **no read, no reply, no host's review and no
simulated row**, and the answer says why with the reason `channel_not_integrated`. A manual fallback for those channels is a product decision that is not built. The table of what each
channel allows is one place, `OtaReviewCapabilities` (closed by default: a channel added to the enum is closed until somebody opens it).

## Switches and variables

| What | Value | Effect |
|---|---|---|
| `Features__OctorateConnector` | the connector flag, off by default | **The only switch.** Off: the job is not scheduled (and a run does nothing), the read and the two sends answer `octorate_not_connected`, nothing is stored or sent |
| `Octorate__Reviews__MaxBookingsPerRun` | `200` (1 to 2000) | The most bookings one run of the job reads, over all the orgs; the ones left wait for the next run. Nothing is required |

No flag of its own: nothing here is visible to a host who has no connected account, and the screens of the channels that have no integration say so instead of offering an action.

## The read: job `ota-review-sync`

The Octorate API has **no list of reviews per property**: a booking is one call (`GET /rest/v2/review/{accommodation}/{refer}`, the reference being the reservation of the channel
manager). The job `OtaReviewSyncJob` (every six hours at minute 40 UTC, `[DisableConcurrentExecution]` 300 s, no Hangfire retry) reads, org by org (each under `IOctorateOrgScope.Use`
and through the same bucket of calls as every other call to Octorate):

- the **Airbnb** bookings of the properties with a **confirmed listing**, of a stay that happens (the one definition of «active»: `AlloggiatiStatusRules.ActiveBookingStatuses`,
  no no-show), with a reservation on the channel manager, whose check-out was in the **last 60 days** and is not in the future;
- that are **due**: never read first (the stay that ended last first), then the ones whose `NextPollAt` has come (the most overdue first), within the cap of the run for the whole run;
- the **cadence** of a booking: every 6 hours in the first 14 days after the check-out, then once a day until day 60, and **no more** once the guest's review is out and the host's own
  review is sent or closed. A reply or a host's review whose outcome is not known keeps the booking in the schedule until a read settles it, **whatever the age of the stay**: the window
  of sixty days is lifted for it (also after a read that failed), and only a stay that happens, of a property with a confirmed listing and a reservation on the channel manager, is read.

An org is read only down to its last 30 calls of the minute (`RateReserve`): the rest is for the host who replies and for the chat. When Octorate says it is over its limits the run of
that org stops and the booking is read at the next run. A failure of one booking is stored on its row as a **code** (`LastSyncError`: `review_not_available` for a 403 without an error of
the API, `octorate_unavailable`, ...) with the time of the next try (a day for a refusal, six hours for a failure that may pass); a failure nobody foresaw is logged with its type and the
run goes on. **Nothing is invented**: no row is made for a channel with no integration, a read that fails stores no text, a review that is not `REVIEW_PUBLISHED` carries no text, no rating
and no reply, and a published review is never taken back by a later read that says less.

`POST api/bookings/{id}/reviews/refresh` (`booking.read`, the caller's reach on the property) reads one booking now, at most **once every ten minutes**: 429 `ota_review_refresh_too_soon`
with `retryAfterSeconds`; 422 `channel_not_integrated`, and `ota_review_booking_not_reviewable` for a stay that did not happen (cancelled, no-show) or has no reference on the channel manager; 409
`octorate_not_connected` / `mapping_not_confirmed` (the listing is not confirmed) when nothing can be read. **A read that failed is not a 200**: the code of the channel manager is stored on the row
(`lastSyncError`, which also counts for the ten minutes) and raised as it is (409 `review_not_available`, 502 `octorate_unavailable`, ...).

The first time the channel manager shows the guest's review published, **one** domain event `ReviewReceived` is recorded (dedup key `ota-review:{id}`, ids only: the review, the
booking, the property) in the same save as the row, so the routines of the host that wait for a review start from it, and the hosts of the booking are told: a **bell entry, a pop-up
and the live refresh** of the open pages (`ota-review-received`, delivery key `ota-review:{id}:received`, the property name only, never the guest, the mark or a word of the review).

## Replying and the host's review: once, and never twice

`POST api/reviews/ota/{id}/reply` (`{ "text" }`, 1 to 800 characters) and `POST api/reviews/ota/{id}/host-review` (`{ "ratings": [{ "category", "value" }], "text", "privateText?" }`, at
least one whole rating from 1 to 5 and a public text) need `booking.write`, the header **`Idempotency-Key`** (8 to 64 letters, digits, dashes or underscores) and the reach of the
caller on the property of the review (a review of another org or out of the reach is a 404, like a missing one). The Octorate operations have no idempotency key of their own, so
CasaZen makes the send safe (OCT-13):

1. **Saved first.** Under the advisory lock of the booking (`PostgresAdvisoryLocks.Scope.OtaReview`) the text and the key are saved as `ManualVerify` (the outcome of a call that is
   running is not known either) and committed. A process that dies from here on leaves exactly the state it should.
2. **Sent with no lock held.** The call to the channel manager is one attempt, never repeated by CasaZen.
3. **Settled.** The answer is stored under the lock again: `Sent` (the time and the text), `Error` with a stable code (a typed refusal, or 409 `ota_review_action_not_allowed`), or it
   stays `ManualVerify` (no answer, a 5xx, an answer that cannot be read: the action may have left). A **429** means nothing happened: the attempt is taken back (no text, no key, no
   state) and the same key can be sent when the limit is over.

| State of the action | What the host sees | What happens next |
|---|---|---|
| `None` | the action is open | — |
| `ManualVerify` | «waiting for the check» | no second send is possible (409 `ota_review_action_unconfirmed`); the next read of the booking finds it on the channel and makes it `Sent`, or **after one hour** makes it `Error` (`reply_not_delivered` / `review_not_delivered`) and tells the hosts through the bell |
| `Sent` | done; the reply or the review is shown | nothing: 409 `ota_review_reply_already_sent` / `ota_review_host_review_already_sent` for another try |
| `Error` + code | the reason | the host may try again with a **new key** |

The same key returns what was stored (`replayed: true`) and sends nothing, whatever the text of the second request says. A reply on the channel is the host's reply, whoever wrote it (a
person on the channel itself too): the row is `Sent` and CasaZen sends none. 202 is the answer for a `ManualVerify` action; 200 for the others.

Refusals before anything leaves: 422 `ota_review_channel_not_supported` (not Airbnb), `ota_review_not_published`, `review_reply_invalid`, `ota_review_host_review_invalid`,
`idempotency_key_invalid`; 409 `mapping_not_confirmed` (nothing is written to a channel for a property whose listing nobody decided, OCT-04), `ota_review_window_closed`; the connector
off is `octorate_not_connected`. An empty list of ratings is refused, never sent as «no rating».

## What the wire looks like: documented, not proven

The sandbox has **no reviews and no OTA connected**: it answers an empty `data` for every booking, so no answer with a review in it was ever seen. The shapes are the plan's
(`REVIEW_PUBLISHED`, `privateReview`, `GUEST_TO_HOST` / `HOST_TO_GUEST`) and the habits of the other operations of the API; the reader is **tolerant** (member names without regard to
case, an object or a list, bare or in `data`) and lives in one file, `Octorate/OtaReviewMapper.cs`, the only one to change when the first real answer shows another name. An answer that
is none of the shapes is an `octorate_unavailable` error, **never** an empty review. The bodies of the two writes (`{ "text" }`; `categoryRatings`, `publicReview`, `privateReview`) are
documented too and are checked on the first real Airbnb property (pilot).

## The reviews in the hub of messages (E05/F06/US01/T03)

The hub lists the reviews next to the conversations, **under their own filter**: `GET api/messages?includeOta=true&kind=review` answers the reviews of the booking site (key `review:{id}`) and the
published reviews of the channels (key `otareview:{id}`) in one statement, paged in the database, newest first (the instant the review was published), narrowed to the properties the caller
reaches (a collaborator «Solo alcuni» lists only its own; a review of another org does not exist for it). Each row is a hub item: `threadKey`, `source` (`Review` or `OtaReview`), `channel`, `kind`
`Review`, the property, the booking id and code, the name of the guest, the first 120 characters of the review (for the host only), `rating` and `needsReply` (published and not answered; a reply whose
outcome is not known counts as sent). **The default view has no review**: «Da rispondere» is the conversations. The words of a review are read for the rows of the page only, after the statement
(the text of a review of a channel is encrypted at rest and is never searched).

`GET api/messages/reviews/{threadKey}` (`booking.read`, the caller's reach in SQL) opens one review: the stars and the categories, the words of the guest, the host's reply and its state, the host's own
review and its window (a review of a channel), the booking card (the amounts only for a caller who may read payments) and `capabilities` (`canReply`, `canHostReview`, `reason`): for a channel the table of
`OtaReviewCapabilities` narrowed by the connector and the listing, for the booking site a reply once and no review of the host. A review is **not a conversation**: its key is a 404 `thread_not_found`
for every endpoint of a conversation (the detail, the mark as read, the reply, the templates, the drafts of the messages), and the key of a conversation is a 404 `review_not_found` for the detail of a
review; a review that is not there, is of another org, is out of the reach or was never published answers 404 `review_not_found`.

## The drafts of the replies (E05/F06/US01/T03, NF-69, AI-06, PO-09)

**Never automatic: a reply to a review is public.** The assistant writes a draft for the host to approve, and nothing else; the reply leaves, once, from the approve action of the queue.

1. A guest's review appears (`DomainEventType.ReviewReceived`: the booking site or the first time a channel review is seen published). `ReviewReceivedHandler` queues `ReviewReplyDraftJob` when
   `Features__MessageAssistant` is on and the organization switched its assistant on (the exception of the property and the budget are asked by the drafter).
2. `ReviewReplyDrafter` (Standard tier of the AI provider, through the monthly budget as the draft of a message) writes the reply from the stars and the words of the review, **without the names** (the guest,
   the host, the codes of the booking and the reference on the channel manager, the contact data and the long numbers are taken out by `AssistantPromptBuilder.Minimize`, and the instructions forbid greeting
   by a name), in the language of the review, with the tone and the signature of the rules of the property, at most 800 characters signature included. The server checks the answer for what a reply
   never says and queues nothing that breaks a rule: a promise or an offer of money or of a gift (refund, compensation, discount, voucher, free night, in the languages of the reviews), a mention of being an
   AI, a link, an address or a number to call, a placeholder left in the text (`ReviewReplyPromptBuilder.Violation`).
3. The draft is an item of the approval queue (`ReviewReply`, origin action `SendReviewReply`, the property, no confidence score, `ThreadKey` the key of the review, `ItemId` the id of the review).
   A review that has a draft waiting, or one approved in the last seven days, gets no second one from the automatic path; a person who asks (`POST api/reviews/{reviewKey}/draft`, `booking.write`, the reach
   of the caller, limited like the other AI endpoints) gets a new one and the one that waited is rejected with the reason `regenerated`. Without the assistant, a provider, budget, or for a review that is not
   published, is answered or cannot be answered (the connector is off, the listing is not confirmed, the channel has no integration) **there is no draft** and the reason is the code of the answer and of the log:
   `assistant_disabled`, `ai_provider_not_configured`, `ai_budget_exceeded`, `ai_provider_error`, `ai_invalid_answer`, `draft_rule_violation`, `draft_too_long`, `already_answered`, `already_drafted`,
   `ota_review_not_published`, `ota_review_channel_not_supported`, `reply_not_possible`.
4. Approving the item runs `ReviewReplyApprovalAction`: a review of Airbnb is answered through `IOtaReviewService.ReplyAsync` (the safe send above) with the key `approval-{item}-{hash of the text}` (a colon
   would be refused as `idempotency_key_invalid`, so the form of the messages is the one used), a review of the booking site through `IHostReviewService.ReplyAsync` (once: a second reply is
   `review_reply_already_sent`). A refusal comes back as a **failed action**: the item stays pending with the code (409 `approval_origin_action_failed`, `originActionError`), the host edits and tries again; the
   retry of a text the channel refused sends with the next key. An outcome that is not known counts as done and is checked on the channel, never sent again. A rejection does nothing: nothing was sent.
   A key that does not name the review of the item (a conversation, another review) sends nothing.

Privacy: the review is the guest's personal data. The log carries ids, codes and counters, never a word of the review, the reply or a name; the erasure of a guest empties the words of the reviews of its stays and the
drafts of the replies to them (`GuestDataEraser`).

**Demo** (`demo-environment.md`): the scenario `reviews` writes two invented reviews of past stays of the house `appartamento-brera`, one on the booking site and one of Airbnb, each with its draft waiting in
the queue (all saying TEST), marked as showcase data (`Review`, `OtaReview`, `ApprovalQueueItem`); the reset deletes them with their drafts before the houses go. The review of the channel has no
channel manager behind it, so its reply cannot leave from the demo.

## Where things are

| Piece | File |
|---|---|
| Table of what each channel allows | `Casazen.Core/Services/OtaReviewCapabilities.cs` |
| When a booking is read again | `Casazen.Core/Services/OtaReviewPollSchedule.cs` |
| The reader of the answer and the laying over the row | `Casazen.Infrastructure/Octorate/OtaReviewMapper.cs` |
| The three client operations | `Casazen.Infrastructure/Octorate/OctorateApiClient.Reviews.cs` |
| The service (read, lists, the two safe sends) | `Casazen.Infrastructure/Services/OtaReviewService.cs` |
| The job | `Casazen.Infrastructure/Jobs/OtaReviewSyncJob.cs` |
| Endpoints | `BookingReviewsController`, `OtaReviewsController` |
| Registration | `AddOtaReviews` (`Casazen.Web/Extensions/OtaReviewsServiceCollectionExtensions.cs`) |
| The reviews in the hub (list, detail, the key of a review) | `Casazen.Infrastructure/Services/MessageHubService.Reviews.cs`, `MessageThreadsController.GetReview` |
| The drafter, the handler, the job | `ReviewReplyDrafter`, `ReviewReplyPromptBuilder` (the instructions and the rules a reply never breaks), `ReviewReceivedHandler`, `ReviewReplyDraftJob` |
| What approving does | `Casazen.Infrastructure/Services/ApprovalActions/ReviewReplyApprovalAction.cs`; the endpoint `ReviewDraftsController` |

## Privacy

- The words of the guest, the reply and the host's review are **encrypted at rest** (`OtaReviews.GuestReviewTextEncrypted`, `HostReplyTextEncrypted`, `HostReviewJsonEncrypted`, purpose
  `Casazen.Review.Content`, see `encryption.md`); there is no plaintext column.
- **Erasure of a guest** (`GuestDataEraser`, `gdpr.md`): what the guest wrote is emptied for the reviews of the guest's bookings (the mark, the categories and the states stay: the
  reputation of the house is not an identity), the host's own words stay, and the next read does not store the text again (the sync stores none for a booking whose guest was erased).
- **Logs** carry ids, states and codes: never a word of a guest or of a host, never the reference of the booking on the channel manager.
- The text of the hosts' notices names the property only.

## To check on the first real Airbnb property

The reading of the guest's review and of the reply, the window and the status words of the host's review, the body of the two writes, the answers to a reply that was already sent, and
whether the channel manager reads back what it was sent (which decides how soon an unconfirmed action is found). Until then the screens work on what the tolerant reader finds, and a
reply or a review whose outcome is not known waits for the check instead of being sent again.
