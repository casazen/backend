# Runbook: service requests, events on the bus and the cleaning at check-out

Tasks E24/F01/US01/T01, T02 and T03 (requirement NF-120, decision O11 of `operazioni/analisi-tecnica.md`, operations plan of 2026-11-14).
This page covers what is new around the service requests: the two events they record, the default cleaner of a property, the routine action
that creates the cleaning of a stay at check-out, how that cleaning follows the stay, and the one read of the completed cleanings with their cost
that the invoicing of the owners takes. The rest of the lifecycle of a request (quote, backups, timeouts, payments) is in [`suppliers.md`](suppliers.md).

## Events on the bus

`ServiceRequestService` records two events in the unit of work of the change, so there is never an event without its change (ADR-022-R1):

| Event | When | Key (once per request) |
|---|---|---|
| `ServiceRequestCreated` | a host's request is created (`POST /api/service-requests`, the automatic cleaning) | `service-request-created:{id}` |
| `ServiceRequestCompleted` | the supplier completes the work (any context, showcase requests included), or an admin decides a no-show for the supplier and the job is completed by the decision | `service-request-completed:{id}` |

The payload is `ServiceRequestEventPayload`: the request id, the booking and property ids, the supplier org, the category code, the rental context and the status.
Never a note, an address, an amount or a person. The org of the event is the org of the request (the supplier's own for a showcase request: filter on
`rentalContext`). `ServiceRequestCompleted` maps to the routine event `ActivityCompleted` (`RoutineEventMap`), so a routine of the org on «an activity completed» now
runs; `ServiceRequestCreated` starts no routine.

## The default cleaner of a property (T01)

`PropertyCleaningSetup`: one row for each property (unique index `UIX_PropertyCleaningSetups_PropertyId`), written by

| Endpoint | Policy | Notes |
|---|---|---|
| `GET api/properties/{id}/cleaning-setup` | `property.write` (short-rent) | the setup, or `configured: false` with the defaults |
| `PUT api/properties/{id}/cleaning-setup` | `property.write` | the whole setup; 422 with a code of `PropertyCleaningSetupRules` |
| `PUT api/properties/cleaning-setup/bulk` | `property.write` | `{ propertyIds, ...setup }`: one result for each property (200 even when some failed), at most 200, none is 422 (never «all») |

- **Cleaner.** `cleanerKind` `Supplier` (an org with an active supplier profile that covers the comune of the property, preferably one of the org's trusted suppliers:
  the answer says `supplierIsTrusted`) or `Staff` (an active member of the org with an active account who reaches the property: the rule of `OrgPropertyAccessService.SetResponsibleAsync`).
- **Price.** `priceCents` is the price agreed for one cleaning, in cents of the **currency of the property**, gross as the cleaner asks it (decision D4: no VAT split); a supplier
  needs one from 1 cent, staff may have none. Every price carries its `currency`, which must be the property's (422 `cleaning_setup_currency_mismatch`): a number is never
  applied to a currency nobody stated. **The suppliers work in euro**: the requests to the suppliers, their prices and their payments are in euro and nothing is converted, so a property in another
  currency (a Swiss house, in francs) takes no supplier: an active supplier setup is 422 `cleaning_setup_supplier_currency_unsupported` (the message names the currency), and only a member of the
  staff can be its cleaner. A setup kept switched off is only kept. If the currency of a property changes after a supplier setup was saved, the automation makes no request: the action `fails` with
  `cleaning_supplier_currency_unsupported` and the host is told like for any other failure.
- **Time.** `startTime` is a wall-clock time on the clock of the property on the check-out day (default: `Property.CheckOutBy`, 10:00 when it has none); `durationMinutes`
  is 5 to 1,440 (default 120, the length a request without a catalog service gets).
- **Active.** An inactive setup is kept and never used. Saving it as inactive skips the checks of the supplier's status and coverage and of the staff member's reach, so a supplier
  suspended since never stops the host from switching the automation off.
- **Scope.** A property of another org, deleted or outside the reach of the caller answers `property_not_found` (404; in the bulk, for that property only). The bulk applies the
  reach of the caller in the query (`InScope`): a collaborator «Solo alcuni» saves only the properties it was given.

## The cleaning at check-out (T02)

### The ready routine «Pulizia al check-out»

The organization activates it from the routines page (the frontend writes it with `POST api/routines`, then switches it on). Its content:

```json
{
  "name": "Pulizia al check-out",
  "eventType": "AfterCheckout",
  "eventConfig": { "hours": 0 },
  "actions": [{ "type": "CreateCleaningRequest", "config": {} }]
}
```

`AfterCheckout` with offset 0 is the time trigger of the stay (`TimeEventEmitterJob`): it fires at the check-out time of the property on the departure date, in the zone of the
property, **even when the host never registers the departure**. An org that also wants the departure the host registered to create the cleaning at once adds a second routine on
`CheckoutOccurred` with the same action: the cleaning is created once per stay, whichever fires first. The action is always available (`RoutineActionCatalog`); the whole module is
behind `Features:Routines`, which is off by default.

### What `CreateCleaningRequest` does (`CleaningAutomationService.CreateForStayAsync`)

0. Takes the advisory lock of the stay (`PostgresAdvisoryLocks.Scope.CleaningAutomation`) and reads everything that follows under it, so the stay and the setup are judged on what is there now.
1. Reads the stay in the org of the routine and checks it happens (`AlloggiatiStatusRules`: confirmed, checked in or out, no no-show): otherwise the action is `blocked` with `routine_booking_not_active`.
2. Reads the active setup of the property: none is `blocked` with `routine_cleaning_not_configured`, and the existing «To do» of a stay that ended without a cleaning appears as today.
3. Checks the stay has no cleaning yet: a `StayCleaning` row (the key, unique per org and booking), or a
   cleaning someone asked for (a request of category cleaning that is not cancelled or refused, a staff activity of type Cleaning that is not cancelled). Either is `done` and nothing is created.
4. **Supplier**: a `ServiceRequest` made by the service of the host's own requests, with all its checks: category cleaning, the booking and the property, the check-out day at the time of
   the setup, the duration, `EstimatedAmountCents` = the agreed price and the note «prezzo concordato». The supplier takes it with the existing flow (quote, backups and timeouts under their flags).
   **Staff**: an `InternalActivity` of type Cleaning assigned to the member (checked again: one deactivated or who lost the property gets nothing), at the same time, `InternalRate` = the
   price (none when the setup has no price), `RechargeToOwner` = true (a cost entered later is recharged too).
5. Writes the `StayCleaning` row in the same transaction. Two triggers of one stay (the check-out time and the registered departure) create one cleaning.
6. Closes the open «To do» of a stay that ended without a cleaning (raised by a departure registered before the check-out time): the stay has its cleaning now.

**When the cleaning cannot be created** (supplier suspended or busy, member left) nothing is saved, the action `fails` with the code of the rule (for example `service_request_supplier_inactive`,
`supplier_commitment_conflict`, `cleaning_staff_unavailable`), the existing «To do» `CleaningDue` is raised for the stay (once) and a popup alert goes to the people the notifications of the
property go to. An **unexpected** error (a database error, a timeout) is a failure of the cleaning too: the action `fails` with `cleaning_creation_failed`, what the attempt left in the
unit of work is forgotten, and the host is told in the same way. It is never left to the bus to deliver the event again: the engine runs a routine once for each event (the execution is kept even
when it failed), so a second delivery would run nothing. Only a cancelled call is thrown.

### How the cleaning follows the stay (`CleaningBookingChangesHandler`)

| Event | Request, not taken (`Richiesto`, no backup, no proposal, no quote) | Request taken (`PresoInCarico`) or with an offer waiting | Staff activity |
|---|---|---|---|
| `BookingModified` (the check-out day changed) | moved to the new day, same length (`RescheduleAsHostAsync`) | stays; «To do» `CleaningDateChanged` and an alert: the host decides | `Pending`: moved; started or done: left alone |
| `BookingCancelled`, `BookingNoShowMarked` (the stay does not happen) | cancelled by the host with the reason «Prenotazione annullata»; the supplier is told by the notice of a cancellation | cancelled the same way, unless the work started | `Pending`: status `Cancelled` (no cost is read from it) |

A request that is `InCorso`, `Completato`, `Pagato`, `Annullato` or `Rifiutato` is never touched. Only the cleaning the automation made is followed (the `StayCleaning` key): a cleaning the host
made by hand is not. The request they act on is the one the host follows: when a backup supplier took the job in place of the primary (SP-BKP: the primary is `ReplacedPrimary`, cancelled for no
response) it is the promoted backup, not the primary the key points at. The creation and both follow-ups take the advisory lock of the stay and read the stay, the setup and the key after it, so a
change that comes while the cleaning is being made waits for it and finds its key; the cancellation reads the stay as it is now and cancels only if it still does not happen (a late
`BookingCancelled` of a cancellation that was undone cancels nothing), so events that arrive out of order give the same result. The «To do» `CleaningDateChanged` closes by itself when the
stay is cancelled or the property is declared ready.

## The cost of the completed cleanings (T03)

`ICleaningCostSource.GetCompletedAsync(orgId, propertyIds, from, to, ct)` (`Casazen.Core/Services/ICleaningCostSource.cs`) is the contract agreed on 2026-11-14 with the invoicing
plan (its task E14/F01/US02/T01 consumes it). Read only, one `CompletedCleaning` for each cleaning completed from `from` to `to`, both included, on the Europe/Rome calendar, oldest first.

- **What counts.** A service request of category cleaning that is `Completato` or `Pagato` (never `Annullato`, `Rifiutato` or still open; never a backup that waits nor a request of a
  supplier's showcase) and a staff activity of type Cleaning that is completed. **Never an amount nobody confirmed**: a supplier request whose final amount is above the quote by more than
  the tolerance (decision D7, `FinalAmountNeedsConfirmation`) is not returned until the host confirms it. It then comes back once, in the period of the confirmation: `CompletedOn` is the date the
  period is judged on (the confirmation, else the completion), so the statement of a closed period never changes and an amount confirmed late falls in the open one. `SourceId` is always the
  request. A request the host marked as paid by hand with the confirmation still open counts from the day it was paid.
- **Amount.** Supplier: `FinalAmountCents`, else `QuotedAmountCents`, else `QuoteTotalCents`, else `EstimatedAmountCents`; none at all is 0 with `AmountVat = Unknown`. Staff: `InternalRate`
  in cents. The currency is the one of the property (requests and rates carry none).
- **VAT nature (what the model says).** Every amount of a service request is documented «gross as declared (decision D4: no VAT split)»: supplier amounts are `IncludesSupplierVat`; a staff
  cost is `NotApplicable`; `ExcludesVat` is not produced, because no amount of CasaZen is net. `Unknown` is returned only where there is no amount. Whether the company applied VAT
  is not the cost source's: the agency states it on the request (FIX/BE-CLEANING-VAT, `GET`/`POST api/service-requests/{id}/cleaning-vat`, columns `ServiceRequests.SupplierVat*`, never
  shown to the supplier), see [`owner-accounting.md`](owner-accounting.md) «The VAT of the cleaning company».
- **Supplier invoice reference.** The model links no invoice or receipt of the supplier to a request or to its payment (`ServiceRequestPayment` holds the identifiers of the payment provider,
  which are not an invoice), so `SupplierInvoiceReference` is always `null` today.
- **Whose.** The tenant filter and an explicit `orgId` on the property, the request and the activity: a property id of another organization returns nothing, `propertyIds` `null` is every
  property of the org, and an empty list is an empty answer, never «every property».

## Operations

- **Flags and configuration.** None new. The automation and the changes handler read `Features:Routines` (off by default: nothing is read or written with it off). The setup API has no flag.
- **Migration** `AddPropertyCleaningSetup`: the tables `PropertyCleaningSetups` and `StayCleanings`, nothing else. Rolling back the code leaves them unused; dropping them loses the setups and
  the keys of the automatic cleanings, after which a stay that already has its cleaning would be seen as «planned» by its request or activity, not by the key.
- **Checks on the test environment.** With `Features:Routines` on: set a cleaner on a property, activate the routine, end a stay (or wait for its check-out time) and look for the request of the
  supplier (or the activity of the member) and for the line of the routine log (`done`, `blocked` or `failed` with its code).
