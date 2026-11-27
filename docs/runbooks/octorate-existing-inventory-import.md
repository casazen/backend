# Runbook: import of the listings that already exist on OTAs

Task E02/F01/US03/T01 (D6, ADR-023, ADR-027, NF-28). How CasaZen brings into the channel manager the listings a host already has on OTAs or
in another channel manager (the `onOtas` scenario of the connect wizard, section 9.1 of the source requirements), keeping their identifiers,
what is verified and what is not, how to resume an import that stopped, and the one rule operators never break. The endpoints sit behind
`Features__OctorateConnector`; the settings are in [`deploy-checklist.md`](deploy-checklist.md) § 2.11.

## 1. The rule

**An import is never solved by creating something to replace what is there.** No state, error or retry of the import calls an operation that
creates a property, a listing or a replacement connection, or that publishes or pushes content, on Octorate or on an OTA, and none turns the
scenario into `newInventory`. A listing that cannot be imported stays where it is: the fix is to complete the step (the access, the mapping, a
retry), or to ask Octorate, never to publish a new copy or to make a new connection next to one that does not work. A new listing next to the
old one risks the reviews, the ranking and the history of the old one, and creates a duplicate on the OTA.

**The one creation allowed: «Collega da CasaZen»** (decision of the product owner, 14/11/2026). For a portal whose connection needs only the id
of the property on the portal (`connectByPropertyId` of the catalog: an OTA that Octorate lets be created through its API, that `requiresHotelId`
and needs neither `requiresPassword` nor `requiresTokenAuthentication`, such as Booking.com), the host types that id in the step «Portali» of the
wizard and presses «Collega»; `POST api/octorate/existing-inventory/batches/{id}/channels/{code}/connect` then creates the connection of the
channel on each accommodation named, with that id, the pulls off and no content pushed, and the host's only step left is to approve Octorate
on the extranet of the OTA. Exactly this, and nothing more:

- only on the host's explicit «Collega», never from a state, an error or a retry of the import;
- only on an accommodation of the batch (one of the org's that CasaZen knows, not rejected; for a batch of named properties or of a group, one
  confirmed to one of its properties);
- **never a second connection of the same channel on an accommodation**: the connections of the org are read under the lock of the channel
  (`OctorateInventoryConnect`), an accommodation that has one (whatever its state) is only recorded, and the accommodations already recorded
  on the channel of the batch are not connected again even when the list of Octorate lags;
- the portals that need more (Airbnb, token authentication; Vrbo and any portal with a username and a password) keep the hand-over to the
  panel.

- `newInventory` exists only when a person declares it (`PUT api/octorate/inventory-intents`), and the creation of a listing on Octorate
  (E02/F03/US01/T06) is refused for any property whose declared scenario is not explicitly `newInventory`
  (`IInventoryScenarioGuard`, 409 `octorate_existing_inventory_must_import`). A missing declaration, a missing mapping or an empty import
  never stand for «new».
- The access to a channel is given in the Octorate panel, or on the extranet of the OTA after «Collega». CasaZen accepts no username,
  password, cookie or OTA token, in any field, draft, query string or log. The id of the property on a portal is not a secret (the portal
  shows it to the host): «Collega» sends it to Octorate in the create of the connection and does not keep it.

## 2. The flow

| Step | What happens | Who | State of the channel |
|---|---|---|---|
| 1 | The host chooses the OTAs and the properties (all, some, or a group of the mixed portfolio) and the batch is created | host | `Draft` |
| 2 | `access/start`: the step, the hand-over and the resume point are saved, then the address of the Octorate panel is returned: the page of the portal's connection, `{Octorate__FrontendBaseUrl}/user/channelmanager/edit.xhtml?portal={code}` when the code is a number (verified by the product owner for Booking.com, `portal=142`, on 14/11/2026, and for no other portal yet), otherwise the panel itself; the host gives the access on the panel | host, in the panel | `AwaitingChannelAccess` |
| 2 bis | `connect` («Collega da CasaZen», only for a portal with `connectByPropertyId`): the host types the id of the property on the portal for each accommodation; CasaZen creates the missing connections of the channel (§ 1), records the accommodations connected or found in `connectedAccommodationIds` of the channel, then starts the step as `access/start` does. The host approves Octorate on the extranet of the OTA | host, then the OTA | `AwaitingChannelAccess` |
| 3 | CasaZen reads the connections of the org (at most once for each backoff window) and finds an authorized connection of the channel (`status` `ENABLED`: `userLogged` alone is no proof, see § 3) that is new to this hand-over (the host logged in on it after the hand-over began, or it belongs to an accommodation of the batch: confirmed to one of its properties, or connected by «Collega» for it; one the org already had working for another property proves nothing): the access is **verified** and the channel moves on by itself. Only if the checks run out (`AccessCheckMaxAttempts`) the host may **confirm** it (`access/confirm`), never before | CasaZen | `ImportingRooms` |
| 4 | `run`: `IMPORT_ROOMS` on each connection of the channel that Octorate shows as authorized, then the listings of the org are read again and the matcher proposes a property for each | CasaZen | `AwaitingMapping` |
| 5 | The host confirms each listing to a property, chooses another, imports it as a CasaZen property (the local record only, inactive and «to check»; nothing is created on the OTA), rejects it, or leaves it for later | host | `AwaitingMapping` |
| 6 | `continue`: for the confirmed listings, `IMPORT_RESERVATIONS` and the import of the reservations into CasaZen, then `IMPORT_CALENDAR`, then `MARK_CONNECTED`. A listing left for later stays write-blocked and is not marked connected; the others go on | CasaZen | `ImportingReservations`, `ImportingCalendar`, `MarkingConnected`, `Completed` |

A channel is `Completed` when every listing found is connected, left for later, rejected or out of the scope of the batch. `NeedsAttention`
means a person or time is needed (a stable code says what); `Failed` means Octorate refused the operation or the channel failed on its side.
Both are resumed by repeating the command: nothing already done is done again.

**One listing never holds the others back.** `run` asks the rooms only of the connections Octorate shows as authorized (a listing whose access
was not given is not part of the import), and the channel goes to `AwaitingMapping` as soon as the rooms of one connection are in. A connection
whose import failed keeps its code (it counts in `failed`), is not asked again by `continue`, is neither decided nor blocking, and `continue`
leaves the channel in `NeedsAttention` with its code, never `Completed`, while one waits; repeating `run` asks it again (a result nobody knows
only when it is named in `retryUnknown`). Rejecting the mapping of such a listing takes it out of the import.

The calendar of a connected listing is read into CasaZen by the 15-minute `CalendarSyncJob`, as for every confirmed listing; the import does
not read it itself.

**Which listings a batch covers, and who sees them.** A listing confirmed to a property belongs to the batch when that property is declared
`onOtas` and is one of the properties of the batch (every declared `onOtas` property for a batch of the whole inventory). A property **with no
declaration** says nothing about the batch: every property that existed before the declarations is like that, so it is covered by a batch of
the whole inventory only, or by a batch of named properties when its listing was confirmed to it **after the batch began** (the import itself
made the property); the import then declares it `onOtas`, in the name of someone who reaches it. Any other confirmed listing is left alone and
shown as `outOfScope`. A person who does not reach the whole organization (a collaborator «Solo alcuni») sees, decides and moves only the
listings of the properties it reaches, the same rule as the mapping list: the other listings are not in its counts or items, cannot be left for
later by it, and nothing is asked of Octorate or declared for them in its name.

## 3. What is verified, and the evidence matrix

The four operations of the D6 sequence are **documented by Octorate and never run on a real property**: the sandbox has no OTA connected and
the partner licence is read-only (`kb/plan/octorate-integration-facts.md`, `kb/plan/capability-evidence.md`). The documented request is
`POST /rest/v1/connection/{action}/{accommodation}/{connection}` with `action` one of `IMPORT_ROOMS`, `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR`,
`MARK_CONNECTED`; the answer is a list of the mappings `{ portalConnection, productId, externalId, id }` (the example of the specification
wraps it as `{ additionalInfo: { actionPerformed, ... }, data: [...] }`); a failure of the channel is HTTP 550. The client reads exactly that
(a bare list, or a wrapper whose `data` is a list and whose `actionPerformed` is the action asked), treats an answer in any other shape
(`{}`, an error that came with HTTP 200, a wrapper without `data`) as an unknown result, and does not look for a status endpoint: **whether each action is synchronous is one of
the facts the pilot must record.**

So each operation has a flag for each channel, **off by default**: `Octorate__ExistingInventory__Channels__{Code}__ImportRooms`,
`__ImportReservations`, `__ImportCalendar`, `__MarkConnected`. With the flag off the operation is not called; the channel stops at that step
with `d6_operation_unverified` and the step stays guided (the host, or CasaZen support through «Fallo per me», completes it in the Octorate
panel). Never turn a flag on for a channel the pilot has not recorded here.

**The same flags gate the activation of a channel or a tool from the marketplace** (`POST /api/marketplace/activations/{id}/import` and `/activate`,
E09/F02/US01): `IMPORT_ROOMS`, `IMPORT_RESERVATIONS` and `IMPORT_CALENDAR` of the import step, and `MARK_CONNECTED` of the activation step, each with its
flag of the channel. With a flag off the step answers 409 `d6_operation_unverified` and the operation is not called (the import runs the verified ones in
order and stops at the first that is not). The switching on of `updateCalendar` and `pullReservations` that follows `MARK_CONNECTED` has no flag of its
own: it follows the one of `MARK_CONNECTED`, so turning that flag on for a channel also lets the activation push the calendar to it. Record in the
matrix below what the pilot saw for these two calls too: that `MARK_CONNECTED` leaves the pulls off (so the activation has to switch them on), or
already switches them on (the patch is then redundant).

Evidence matrix (update this table with the real-property pilot; one line for each channel, one cell for each operation: date, structure,
what was seen):

| Channel | Code | Access check | `IMPORT_ROOMS` | `IMPORT_RESERVATIONS` | `IMPORT_CALENDAR` | `MARK_CONNECTED` | Written confirmation of ids, reviews, ranking, history |
|---|---|---|---|---|---|---|---|
| Booking.com | `142` | not verified | not verified | not verified | not verified | not verified | not asked |
| Airbnb | `274` | not verified | not verified | not verified | not verified | not verified | not asked |
| Vrbo | `235` | not verified | not verified | not verified | not verified | not verified | not asked |
| Expedia | `4` | not verified | not verified | not verified | not verified | not verified | not asked |

The codes above are the ones the sandbox showed on reservations (`kb/plan/octorate-integration-facts.md`); the code CasaZen uses is the
`PortalCode` of the portal catalog (`MarketplacePortals`), which is also where a channel must be listed, enabled and of category OTA to be
chosen at all.

**Sandbox evidence of 13/11/2026** (the user's Octorate test account, structure `520897`), which decided «Collega da CasaZen» and the rule of
the access:

- `POST /rest/v1/connection/520897` with exactly the body of `OctorateApiClient.ConnectionCreateBody` (portal `142`, pulls off, no content)
  plus a `hotelId` that does **not** exist on Booking.com answered 200 with a connection `status: "WAITING"`, `userLogged: true` and
  `lastPortalAcceptLog: {}`, although Booking.com never approved anything. So `userLogged` is true right after a create through the API and is
  **no proof that the OTA accepted** (the specification says it «means if user has logged in and this portal connection is authorized»):
  CasaZen takes only `status` `ENABLED` (the enum is `DISABLED` | `WAITING` | `ENABLED`) as the access given
  (`OctorateConnectionSummary.IsAuthorized`).
- `DELETE /rest/v1/connection/520897/{id}` answered 200 (the test connection was removed).
- The same create for Airbnb (`274`) without a `hotelId` answered 500 (a `NullPointerException` of Octorate on `getHotelId`): a portal that
  needs the access on the panel cannot be created this way, which is why it keeps the hand-over to the panel.
- What is not proven yet: the moment a real `hotelId` turns the connection to `ENABLED` once the host approves Octorate on the extranet of
  Booking.com, and how long that takes. The pilot records it in the column «Access check».

What the pilot must record, for each channel, before its flags are turned on:

1. The access: after the host gives the access in the panel (or approves Octorate on the extranet after «Collega»), does
   `GET /rest/v1/connection` show the connection with `status` `ENABLED`, and does it carry `loginAt` (the time of the host's login)? CasaZen
   takes as «verified» an `ENABLED` connection whose `loginAt` is after the hand-over, or that belongs to an accommodation of the batch
   (confirmed to one of its properties, or connected by «Collega» for it); `userLogged` alone never counts (see the sandbox evidence above).
   Where `loginAt` is not there (and in a batch of the whole inventory that did not use «Collega») a first access is confirmed by the host after
   the checks run out. Does a connection exist on an accommodation the org's token sees? If Octorate
   needs a technical structure to hold the channel (D6 note), who creates it and how: **CasaZen does not create it in this task**, and until
   someone does the import stops at `channel_connection_not_found`.
2. Each operation: the exact answer, whether it is complete when the call returns, how long it takes, whether repeating it is harmless, and
   what it does to rooms already mapped.
3. Whether a connection already working in Octorate (a listing declared `onOctorate`) shows up in the connections of the channel and so would
   be imported again. The import skips a listing already confirmed to a property declared in another scenario, outside the scope of the
   batch or with no declaration that the batch did not make (see § 2), but it cannot tell a working connection that has no mapping in CasaZen.
4. In writing from each OTA: that the listing id, the reviews, the ranking and the history stay after the move from the current system (D6).
   Until then the wizard says «identifiers preserved» and promises nothing else.

The first channel enabled in production needs this table filled in for that channel and the four flags set for it only.

## 4. Turning a capability on, and back off

1. Fill in the matrix line with the pilot evidence (date, structure, what was seen).
2. Set `Octorate__ExistingInventory__Channels__<code>__<Operation>=true` on the environment (test first), one operation at a time, in the
   order of the sequence.
3. Run the import on the pilot structure; the batch shows the capability as verified for the channel (`capabilities` of the channel).
4. To take it back: unset the variable. The operation is not called any more; the stamps already written stay, and nothing is undone on
   Octorate.

Other settings: `Octorate__ExistingInventory__AccessCheckBackoffSeconds` (30) and `__AccessCheckMaxAttempts` (6), the pace of the check of the
access (doubling up to 600 seconds); see the checklist.

## 5. Error codes

| Code | Meaning | What to do |
|---|---|---|
| `d6_operation_unverified` | The operation is not verified for this channel (flag off). Nothing was called | Complete the step in the panel with the host, or verify the operation in the pilot and turn the flag on (§ 4) |
| `d6_result_unknown` | No answer in time, a failure of Octorate, an answer that cannot be read, or a request closed after the call left. The item is marked BEFORE each call and the answer replaces the mark, so it is never asked again by itself, not even after a crash | Check on the panel whether the step was done. If it was not, repeat the command naming the listing (its accommodation id) in `retryUnknown` of `run` or `continue`: that asks the operation a second time, so only after checking |
| `d6_channel_failed` | Octorate answered 550: the channel (the OTA) failed, not CasaZen | Check the channel in the panel with the host, then repeat |
| `channel_connection_not_found` | The access is given but Octorate shows no authorized connection of the channel (or, in a batch of named properties, none of the ones the batch covers) | See § 3 point 1; never create a listing to make one |
| `channel_access_not_valid` | The check found the access refused or not valid | The host gives the access again (`access/start`) |
| `mapping_refresh_conflict` | Another read of the listings of the org was running | Repeat `run` |
| `reservation_import_incomplete` | The reservations were not all read into CasaZen (the run stopped before the end) | Repeat `continue`: the stamps keep what is done |
| `octorate_room_not_chosen` | The listing is confirmed but its room is not chosen | Choose the room (`PUT api/octorate/mapping/{id}/room`), then `continue` |
| `octorate_mapping_not_confirmed` | The listing is no longer confirmed to a property | Confirm or import it again, then `continue` |
| `octorate_not_connected`, `octorate_auth_failed`, `octorate_wrong_environment` | The org's connection to Octorate is missing, refused or of the other environment | Reconnect the account (`api/octorate/connect/*`), then repeat. These stop the whole channel for that command |
| `octorate_rate_limited` | The org used its calls of the minute | Wait a minute and repeat: the stamps keep what is done |
| `octorate_request_rejected`, `octorate_not_found`, `octorate_conflict` | Octorate refused the operation for that item | Read the item in the batch, check it in the panel, repeat; ask Octorate if it stays |
| `existing_inventory_connect_not_supported` (422) | «Collega» on a portal that needs more than the id of the property (the access on the panel, a username and a password) or that Octorate does not let be created through its API | Use `access/start` and the panel for that portal |
| `existing_inventory_connect_empty` (422) | «Collega» with no accommodation, or more than 50 | Name 1 to 50 accommodations |
| `existing_inventory_hotel_id_invalid` (422) | An id of the property on the portal that is empty, longer than 64, or with a character other than letters, digits, `.`, `_`, `-`; or an accommodation named twice. `accommodations` lists the ones concerned | Correct the id the host typed |
| `existing_inventory_accommodation_unknown` (422) | An accommodation that is not of the batch: not one of the org's CasaZen knows, one the person rejected, or, for a batch of named properties or of a group, not confirmed to one of its properties. `accommodations` lists them | Reload the mapping (`run` or the mapping endpoints) and name only the accommodations of the batch |

After a «Collega» that stopped on an error of Octorate (`octorate_rate_limited`, `octorate_unavailable`...), the connections made before it
are already recorded in `connectedAccommodationIds`: repeat the same «Collega» and only the missing ones are created. Never create a
connection by hand in the panel for an accommodation that is in that list.

## 6. Resuming an import

Every command is safe to repeat: the batch holds what is done (`GET api/octorate/existing-inventory/batches/{id}`), each external item has its
stamps, and a channel is resumed from them, not from memory. After a restart of the worker, a closed tab or another device, call the same
command. In practice:

- The channel is `AwaitingChannelAccess` or `ImportingRooms`: `GET` the batch (it reads the access), then `run`.
- The channel is `AwaitingMapping`: decide the listings through the mapping endpoints, then `continue` (naming in `leaveForLater` the ones
  to leave, also a listing the mapping does not know yet: it does not hold the others back).
- The channel is `NeedsAttention` or `Failed`: read its `errorCode` (and the `errorCode` of the items), remove the cause (§ 5), then repeat
  `run` (if its rooms are not all in) or `continue`. A listing with `d6_result_unknown` waits for a person: after checking the panel, name it in
  `retryUnknown`; the other listings go on without it.
- Two commands for the same channel at the same time: the second one leaves the channel alone and returns its state.

An external item (one Octorate connection) is imported and marked at most once, also across batches: a later batch inherits the stamps of the
earlier one before it asks Octorate anything. A **result nobody knows** (`d6_result_unknown`) is remembered by the item of that batch only: a
second batch for the same connection does not know it and would ask the operation again, so check the panel before opening another batch
while a listing waits with that code.
