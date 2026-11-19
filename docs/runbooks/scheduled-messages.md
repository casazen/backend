# Scheduled messages (E05/F07/US01, NF-71, MSG-11, ADR-022)

The messages of the routines (the action `MessageToGuest`) as a plan: for each stay the product writes down what its routines will send and when, the host reads that
on the timeline of the booking, edits, skips or sends a message at once, and the message goes out **once**, whichever of the routine, the sweep and «Send now» reaches it
first. It extends the routine engine ([`ADR-022`](../adr/ADR-022-event-source-routines.md)) and the message layer ([`guest-messages.md`](guest-messages.md)): there is
no second engine and no second send.

## 1. Switches and settings

| What | Where | Default | Effect |
|---|---|---|---|
| `Features__ScheduledMessages` | Railway variable | **off** | Off: nothing is planned or sent through the plan, the jobs `scheduled-message-plan` and `scheduled-message-sweep` are not scheduled (and are removed when an earlier deploy had them), the three endpoints answer 404 before authentication, and the action `MessageToGuest` does what it did before (it writes to the conversation of the booking with the template it names: no row of the plan, no check-in link, no access code) |
| `Features__Routines` | Railway variable | off | The module of the routines. The scheduled messages are the work of the routines: with it closed nothing is planned or sent, whatever the other flag says |
| `Messaging__ScheduledMessages__SendWindowFrom`, `…SendWindowTo` | Railway variables | `08:00`, `21:00` | The send window, on the clock of the property. A window that is empty or upside down is the default |
| `Messaging__ScheduledMessages__PlanHorizonDays` | Railway variable | `30` | How far ahead the hourly job plans (1-365) |
| `Messaging__ScheduledMessages__CatchUpMinutes` | Railway variable | `15` | How late a planned message is before the sweep sends it, and how late a moment may be when it is planned (1-1440) |
| `Messaging__ScheduledMessages__BlockedRetryMinutes` | Railway variable | `15` | How often the sweep tries again a message that is blocked or failed (1-1440) |

The times are **proposals of the plan that the product owner confirms** (nothing fails to start with a value that cannot work: it falls back to the default). The flag is
turned on only in the test environment until then; the coordinator does it, never a developer ([`deploy-checklist.md`](deploy-checklist.md) § 2.11).

## 2. The table `ScheduledMessages`

One row for each **booking, routine and action** (unique index `UIX_ScheduledMessages_Booking_Routine_Action`): planning twice writes one row. The rows go with their
booking and their property (cascade) and survive a routine that is deleted (`RoutineId` becomes null, the name stays). `PlannedAtUtc` is the moment it goes out; `TextOverride`
is the host's edit for this booking only, **encrypted at rest** (column `TextOverrideEncrypted`, purpose `Casazen.Message.Content`, no length); `xmin` is the concurrency token
the claim of a send relies on.

| `Status` | Meaning | Open |
|---|---|---|
| `Scheduled` (1) | Planned: goes out at `PlannedAtUtc` | yes |
| `Sent` (2) | The message layer says the channel took it (`SentThreadKey`, `SentAt`): only a message in the state `Sent`. If a skip of the host landed while the send was under way after its lease had lapsed, the row stays `Skipped` with `SentAt` beside it (the message did go out) | no |
| `Skipped` (3) | A person (`person`, `SkippedByUserId`) or the system decided it does not go out: `channel_rule`, `contact_unavailable`, `mapping_not_confirmed` (with an alert) and, as the normal course of a message and with no alert, `campaign_needs_consent`, `check_in_completed`, `check_in_with_operator`, `check_in_not_open` | no |
| `Blocked` (4) | Held back for a piece of data: `variable_missing:{name}`, `id_not_verified`, `access_code_held`, `template_missing`, `message_text_invalid`, `check_in_in_progress` (the guest is filling the check-in in); tried again by the sweep | yes |
| `Failed` (5) | The channel did not take it (its code in `BlockedReason`), or the message layer holds it without having taken it (`message_unconfirmed`: created or waiting for the authorization of the channel; `manual_verify`: nobody knows whether it left); tried again by the sweep, four tries in all (`Attempts` counts the tries that reached the channel, not the passes of the sweep over a blocked message nor a failure to read the way to the guest). A held message is asked again with the same key: the message layer finds it and writes no second | yes |
| `Cancelled` (6) | The booking does not happen (`booking_cancelled`), the routine is off or no longer holds it (`routine_inactive`), the stay does not meet its condition (`condition_not_met`) or the guest of a campaign has no marketing consent (`campaign_needs_consent`); it comes back if the cause goes away and its moment has not passed | no |
| `Expired` (7) | Its moment passed for good: `missed` (more than a day late, or it moved into the past; also a failed one that never reached the channel), `check_out_reached` (still blocked or failed the day after the check-out) | no |

## 3. The plan

* **When.** On `BookingConfirmed` and `BookingModified` the messages of the booking are planned or move with its dates; on `BookingCancelled` its open messages are cancelled
  with their «To do». The hourly job `scheduled-message-plan` (minute 20, UTC) plans again every stay whose check-in or check-out is within `PlanHorizonDays`, and the stays that
  still have a message to settle, for each organization that has an active routine with a message (or an open message), so a routine activated, edited or switched off is
  reflected within the hour and an event that was missed is recovered. The booking is read when the event is handled, never taken from the payload, so events that arrive out of
  order give the same result.
* **What.** Only an **active** routine that is **not in test mode** and holds a `MessageToGuest` with a template. The routines of other organizations are never read. A stay that
  does not happen (pending, cancelled, no-show) gets nothing. The **condition** of the routine (`ConditionJson`, ADR-014: channel, nights, guests, lead time) is read as the engine reads
  it when the routine fires: a stay that does not meet it gets no message, and a condition nobody can read plans nothing. A message marked as a **campaign** (`isCampaign`, read as the engine reads it)
  is planned only for a guest who gave the **marketing consent** (`CampaignConsent`, the same reading as the guardrail of the engine): a guest without it gets no row, and a row it had is cancelled
  (`campaign_needs_consent`) when the consent goes. **What the engine decides is not planned**: an action with an `aiCondition`, every action of a routine with an agent objective and an event that cannot be foreseen
  are written when the routine fires and the engine let them through; the plan never closes such an open row for being «no longer planned» (only with its stay, or when its routine is off).
* **At which moment.** The planner is pure (`ScheduledMessagePlanner`) and agrees with the time triggers of the engine (`TimeTrigger`): `BeforeCheckin`, `AfterCheckin`,
  `BeforeCheckout`, `AfterCheckout` are the check-in or check-out time of the property on its own clock (midnight when it has none) plus or minus the offset; `BookingNew` and
  `BookingConfirmed` are the moment of the booking; a `Wait` before the message adds its delay. The moment is brought **into the send window**. Events the plan cannot foresee
  (a payment, a message) plan nothing: the row is written when the routine fires, for now inside the window.
* **What is never written.** A message whose moment passed by more than `CatchUpMinutes` when it would be planned (switching a routine on never welcomes a guest welcomed long ago),
  and a row that was sent, skipped or expired is never overwritten by a later plan. A host's edit is never touched.

## 4. The send

One place sends: `ScheduledMessageDispatcher`. The routine that fires (`IScheduledMessageService.OnRoutineActionAsync`, called by the engine instead of its old path while the flag
is on), the sweep and «Send now» all go through it.

* **Once.** The row is **claimed** first (`LastCheckedAt` held for two minutes, saved with `xmin`): another actor at the same moment finds it held and does nothing. The message goes
  out with the key `sm-{id}` (`sm-{id}-2`, … after a message the channel refused), so even a claim that lapsed (a process that died) cannot write it twice: the message layer finds
  the one that exists, and a message already in the conversation is never written again: the row is brought up to date with its real state (below).
* **Order of the checks.** The booking (a stay that does not happen: cancelled); the routine and its action (off or gone: cancelled; condition not met: cancelled; not for «Send now»,
  which is the host's own decision); the marketing consent of a campaign (skipped, `campaign_needs_consent`, for every actor: the engine reads it when the routine fires, but the sweep sends what the
  routine did not, so a campaign the engine blocked is not sent later); the text (the host's edit, else the template in the language of the guest, Italian when there is none); the variables the booking
  has no value for (blocked); the way to the guest (`IMessageReplyService.ChooseChannelAsync`: none, a request of a portal or a mapping nobody decided is skipped by the system); a message the message layer
  already holds for the row (only the row is brought up to date); the rules of the check-in link; the access code **taken** (the gate of the verification, nothing logged); the text tried with the rules of
  the message layer and a stand-in for the link. Only then, **last, right before the send**, the things that have a side effect: the check-in link is issued, and once the message layer has the message the
  release of the code is logged. Nothing is issued, replaced or logged for a message that is not going out.
* **What the message layer says of the message is the state of the row.** Only a message in the state `Sent` is `Sent` (as for the routine that writes straight to the message layer). `ManualVerify`
  (nobody knows if it left) is `Failed` with `manual_verify`; `Created` or `WaitingAuthorization` (held by the channel manager, not taken) is `Failed` with `message_unconfirmed`. The sweep asks again
  with the key of the row: the message layer finds the message and writes no second, the row follows it when it becomes `Sent`, and after four tries the host is told (the «To do» and the alert).
  A failure to read how the guest is reached (a limit, an outage of the channel manager) reached nothing: it counts no try, the message is tried again at every pass, and it is not sent more than a day late.
* **The host while a send is under way.** A row a send holds (claimed less than two minutes ago) refuses «Edit» and «Skip» with 409 `scheduled_message_in_progress`, as «Send now» does: the text was read when
  the send began and the answer «skipped» would not be true. The claim of a process that died has lapsed after the lease and does not hold the host off; a skip that lands then is not undone by the send that
  finishes later (the row stays skipped, with `SentAt` beside it).
* **Time.** The routine and the sweep never send before `PlannedAtUtc` (a blocked message whose stay moved to later waits for its new time); «Send now» ignores it and the window.
  A message that goes out outside the window (late, or by hand) is flagged (`OutsideHours`) and the other hosts are told.
* **Other answers.** A refusal of the rules of the channel (a link on a request of a portal) or a guest who cannot be reached is `Skipped` by the system, with an alert; a message the channel did not take
  is `Failed` with its code and the sweep tries again (four tries in all, and after the fourth the hosts are told); an unexpected failure (the database) is recorded on the row and thrown, so whoever
  can try again does.
* **The sweep** (`scheduled-message-sweep`, every 15 minutes): sends the planned messages overdue by more than `CatchUpMinutes`, tries again the blocked and failed ones every
  `BlockedRetryMinutes` once their time has come, and ends what has passed for good (a planned message more than a day late; a blocked or failed one the day after the check-out), closing its
  «To do». One row's failure never stops the others.

### The variables

The thirteen variables of a template: the eight of the hub (guest, house, booking code, dates, times, host) and, for the scheduled messages, `{{check_in_method}}` (the check-in method of
the property in the language of the guest), `{{check_in_link}}`, `{{access_code}}`, `{{property_address}}` and `{{review_link}}`. A template that names a variable the booking has no value
for **blocks** the message (never a literal `{{…}}` in what a guest reads).

* **`{{check_in_link}}`** issues a **new** link of the online check-in **at the moment of the send, last, right before the send** (the previous one stops working) and ends the delivery of the link that the
  portal chat was carrying (`CheckInLinkEndReasons.LinkReplaced`). `IGuestCheckInService.IssueLinkAsync` refuses nothing (it expires every open session of the booking, the one the guest is filling in included),
  so the message applies the rules the other automatic senders apply (`GuestCheckInSendJob`, the e-mail that confirms the booking) **before** it issues anything: a guest who completed the check-in (the data or
  the Alloggiati communication were sent) or a stay that takes no link (not confirmed or checked in, or over) gets the message **skipped** (`check_in_completed`, `check_in_not_open`); a stay whose identity
  verification is with an operator (rejected by one, or a hand-off still open) gets it **skipped** (`check_in_with_operator`: a new session would start the verification again against the decision of a person);
  a guest who is filling the check-in in right now keeps the session: the message is **blocked** (`check_in_in_progress`) until the session ends or expires («Send now» is the host's own decision and replaces it).
  These skips tell nobody (they are the normal course for a guest who checked in early). The daily job `guest-checkin-send` ([`alloggiati.md`](alloggiati.md)) and the delivery of the link
  ([`octorate-webhooks.md`](octorate-webhooks.md) § 4d) issue links on their own: **use one of the two for a stay**, or the guest receives two messages with two links, the newest of which works.
* **`{{access_code}}`** is given **only after the identity of the guest was verified** (ADR-022-R11): `IAccessCodeService.GetReleasableCodeAsync`, with the channel the message will use
  (`portal-chat` for a portal, `guest-area` for a direct booking, `email`), answers `null` for a guest not verified or a stay held back for the tourist tax, and the message is blocked
  (`id_not_verified`, `access_code_held`). A property with no code set blocks it as a missing variable. The code is **taken** (the gate is asked, nothing is logged) after the link and the text are verified;
  the line of the release log (`AccessCodeReleaseLog`: channel and time, never the code) is written by `RecordReleaseAsync` only once the message layer has the message, and once: a message that is blocked,
  refused or fails leaves no line, and a retry writes none. The code is never in a preview, a log or a notice.
* **`{{review_link}}`** is the signed link of the review invitation of a **direct** booking (valid until the end of the review window); a booking of a portal has none.
* A **preview** (the timeline) shows `[check_in_link]`, `[access_code]` and `[review_link]` and lists what the booking lacks: it issues no link and releases no code.
* The composer of the hub fills only the first eight variables: it refuses a template that names one of the other five (`template_variable_missing`).

## 5. What the host sees

* **Timeline and actions** (`ScheduledMessagesController`, `booking.read` / `booking.write` and the reach on the property; another organization's message, or one of a property a collaborator
  «Solo alcuni» was not given, is a 404 and the service applies the same scope in SQL): `GET api/bookings/{id}/scheduled-messages`, `PUT api/scheduled-messages/{id}` (edit), `POST …/skip`,
  `POST …/send-now` ([`TECHNICAL.md`](../TECHNICAL.md)). Only an open message can be edited or skipped; «Send now» of a blocked message is 422 with what it waits for.
* **One «To do» for each booking** (`ScheduledMessageIssue`, action `FixScheduledMessage`), shared by its blocked messages and by a message that failed for good, raised once for each time a
  message becomes blocked (or blocked for another reason: not at every retry of the sweep; one that could not be raised, the failure is logged, is raised at the next pass), and closed when none
  is left blocked or failed (sent, skipped, cancelled, expired, booking cancelled).
* **Alerts** (`ScheduledMessageAlertDelivery`, notification type `ScheduledMessageIssue` of NF-65): a message blocked, failed for good, skipped by the system or by a person, or sent outside the
  window tells the people who reach the property **through the preferences** (`INotificationPreferenceService`: the bell with its live update, the push, the e-mail, the quiet hours), once for each
  message, kind and person; the person who skipped or asked for «Send now» is not told about their own action. A skip that is the normal course of a message (a campaign for a guest without the consent,
  a check-in link for a check-in that is done or with an operator) tells nobody: the timeline shows its reason. A push carries the name of the property and the route of the booking, never a
  word of the message, a name or an address.

## 6. Privacy and guardrails

* The words of a message are never logged; logs carry the row, the organization, the state and a code. No external supplier is ever named in a text for a host or a guest.
* The edit of the host is encrypted at rest. The preview is `private, no-store`.
* Nothing is sent with the flag off, with the routines closed, with a routine in test mode (the engine simulates the action) or for a stay that does not happen.
* **Campaigns only with the consent** (ADR-022-R11): a message of a routine marked `isCampaign` goes only to a guest with the marketing consent (`Guest.MarketingConsent`, not erased or anonymized), read by the one
  helper (`CampaignConsent`) the guardrail of the engine reads: where it is planned and where it is sent, for the routine, the sweep and «Send now». A guest who withdrew the consent gets nothing.
* **What the AI judges stays with the engine.** An action with an `aiCondition` and every action of a routine with an agent objective are not planned and not evaluated by the plan or the sweep: their row is
  written when the routine fires and the engine let them through. The selection of the operations an agent makes for a stay («omit what is inappropriate») is not replayed by the plan: a planned message is
  what the routine says, so a host who wants a message to depend on the stay uses a **condition** of the routine (read by both) rather than an objective for the agent.

## 7. Turning it on and off

* **Test environment** (done by the coordinator, not by a developer): set `Features__ScheduledMessages=true` (and `Features__Routines=true`) on Railway **test**, restart, then check in the
  Hangfire dashboard that `scheduled-message-plan` and `scheduled-message-sweep` exist. The demo showcase writes the routine «Messaggio di arrivo (TEST)» and three messages
  ([`demo-environment.md`](demo-environment.md)): the timeline of the demo stays shows a planned, a held-back and a skipped message.
* **Rollback:** remove the variable (or set it to `false`) and restart: the jobs are removed at startup, nothing more is planned or sent, the action `MessageToGuest` returns to its old
  path. The rows stay (`ScheduledMessages` is not dropped) and are read again when it is turned on.
* **Production:** only after the product owner has confirmed the send window and the retry times and the frontend shows the timeline.

## 8. Known limits

* A message to a **direct** booking goes through the conversation of the stay with the origin `Automation` stored on the message (`IGuestMessageService.ReplyAsAutomationAsync`): it is the latest message of the
  thread and answers nobody, as on the channels of a portal (the question of the guest stays unread and in «Da rispondere»). The routine's `MessageToGuest` of the flag-off path goes through the same door.
* The e-mail path of the message layer remembers its key in memory only: a crash between the queueing of an e-mail and the save of the row can issue the link and queue the e-mail again.
* The claim of a row is proved on `xmin`, which the in-memory tests do not model: the unit tests prove the lease and the key; the race between two replicas is the database's.
* The send window and the retry times are proposals (see § 1).
