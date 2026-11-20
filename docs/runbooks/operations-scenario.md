# Operations scenario: the stay of the four origins, end to end (E28/F01/US01, NF-41, NF-119 to NF-123)

The acceptance proof of the operations steps «O» of `pilota-flusso-completo-2026-11-14.md`: one test for each way a stay is booked (a portal through the channel manager, the
agency's own site, the CasaZen site, the host's own entry), each from the booking to the day after the departure, on the real code. It is the test that fails when a link of the
chain breaks: a handler nobody registers, a rule that stops handing the stay to the next step, a flag that no longer reaches its job. It adds no behaviour: no flag, no setting, no
migration, no endpoint.

## 1. Where it is and how to run it

| What | Where |
|---|---|
| The tests | `Casazen.Tests/Unit/Operations/OperationsChainTests.cs` |
| The world of the tests (the agency, the composition, the steps) | `Casazen.Tests/Unit/Operations/OperationsScenarioBuilder.cs` and `OperationsScenarioBuilder.Steps.cs` |
| This page | `docs/runbooks/operations-scenario.md` |

```
dotnet test Casazen.Tests/Casazen.Tests.csproj -c Release --filter "FullyQualifiedName~OperationsChainTests"
dotnet test Casazen.Tests/Casazen.Tests.csproj -c Release --filter "Scenario~B7"     # one step of the scenario
```

The tests run on the in-memory database (no server needed), so they are in the folder of the unit tests and in the fast set of every run; CI runs them in its shards like any class.
No test has a display name of its own (the planner of the shards reads the default name). The trait `Scenario` of each test lists the steps of `pilota-flusso-completo-2026-11-14.md`
it covers.

## 2. What the tests prove

| Test | Origin | The stay, step by step | Rule it proves |
|---|---|---|---|
| `OperationsChain_PortalOrigin_Test` | A portal (Booking.com) through the channel manager | imported with no address of the guest (the origin reads «portal») → the daily job issues the link and, with the chat of the portal not licensed, the delivery says `WaitingChatPermission` with one «To do» → when the permission arrives the link goes once in the thread of the reservation and the «To do» closes → the guest completes the check-in → the guests reach the channel manager on their own (never as a draft, places as its table names them) → the stay reads «entrusted», yellow → the photo of the document stays while no receipt covers the stay → the receipt of the day for the flat turns the stay green → the run that follows deletes the photo and keeps the legal record → the portal collects the tourist tax: nothing is payable online → the cleaning is made once at the check-out time → the review is skipped for a portal stay | data: the documents of a guest go only when a receipt covers the stay, the legal data never; the link never goes twice |
| `OperationsChain_AgencySiteOrigin_Test` | The agency's own site (`utm_source=agency-site`) | the booking stores the link's source and reads «agency site» → the payment of the checkout includes the tourist tax → the confirmation carries the link of the check-in by e-mail → the guest registers, the host confirms the tax → nothing is payable online | money: the tax already in the payment is never charged again |
| `OperationsChain_CasaZenSiteOrigin_Test` | The CasaZen site | the booking is planned: one scheduled message the day before the arrival, at the check-in time of the flat → the routine sends it, once, with the link made at that moment → the guest registers a member who owes the tax the checkout did not count → the difference is paid online (webhook of the connected account) → the day after the departure one invitation to review | money: only the difference is collected, and no `Payment` row is made for it |
| `OperationsChain_ManualOrigin_Test` | A booking the host enters by hand | confirmed at once, no payment → the daily job issues the link and queues the e-mail → the guest registers → the whole tax is payable online (CasaZen collected none of it) → the cleaning for the member of the staff, and once done its cost is in the read of the invoicing (`ICleaningCostSource`) | money: the tax of a host's booking is paid once, whole; the cost of a cleaning is the one agreed |
| `OperationsChain_NoDoubleTouristTax_Test` | The CasaZen site, with a difference | the guest opens the payment twice before paying and gets the same payment intent (Stripe is asked for one only) → the tax is paid once → the same event delivered again (dropped by the claim of the event) and the same payment told by another event (another id, as the other endpoint sends it: the tourist tax itself records it once, one push) change nothing; nor do a request to pay after the payment and a second confirmation of the tax | money: the tourist tax is never collected twice |
| `OperationsChain_IdentityBeforeTheGuestsReachTheChannelManager_Test` | A portal, with the identity step on | the guest registers; the data is complete but nobody verified who the guest is, and the step has escalated to the operator: nothing leaves → the operator accepts → the guests reach the channel manager, once | privacy: no guest data leaves before the identity passed |
| `OperationsChain_IstatRegionOfTheFlat_Test` | The flat in Como | the region of the flat reads «to verify» and the host is told to ask the support → the staff verifies the row on its source and saves the coverage → the next step is the connection in the panel → the proof of the month is filed and listed | data: a region is never claimed covered before a person verified it; CasaZen sends nothing to ISTAT |
| `OperationsChain_RoutinesNeedAnAiProvider_Test` | A booking entered by hand, in a world whose AI provider is the one the application ships (`Ai:Provider=Stub`) | at the check-out time the ready routine fires and the planner has no provider: the execution is closed `Failed` with `routine_ai_unavailable` and no cleaning is made | prerequisite: the routine steps (B9, B10, B11) need an AI provider in effect, see § 4 |

Every test asserts **states**, never texts: the state of the delivery of the link, the status and the light of the stay, the rows a step leaves, what the channel manager and Stripe
were asked, what is left in the storage.

## 3. What is real and what is replaced

* **Real**: the composition of the application. The world calls the extension methods `Program.cs` calls (`AddCasazenServices`, `AddCasazenBookingOperations`,
  `AddCasazenOctorate`, `AddCasazenOctorateGuestAutoSend`, `AddCheckInLinkDelivery`, `AddCasazenTouristTaxCollection`, `AddScheduledMessages`, `AddMessagesHub`, the repositories, the
  authorization), over the options of `Casazen.Web/appsettings.json` and one in-memory database. The outbox is delivered by the real dispatcher over the real event bus, each handler in
  a scope of its own under the org of its event; the jobs (`GuestCheckInSendJob`, `CheckInLinkDeliveryRetryJob`, `TimeEventEmitterJob`, `OutboxDispatcherJob`) are built as Hangfire
  builds them. A step that stands for a request of the host runs under the org of the agency; a step of a guest or of a job runs without a tenant.
* **Replaced**: the channel manager (`Mock<IOctorateApiClient>`), Stripe (`Mock<IStripeService>`) and **the AI provider that the engine of the routines asks** (`IAiProvider`, a strict mock that
  answers «every operation of the routine»; see § 4, it is a prerequisite): nothing reaches a network. The e-mail queue, the push queue and the Hangfire client record what they are given and run
  nothing; the files are in memory; the clock is the scenario's. The Stripe mock does what Stripe does for the tourist tax: the same idempotency key gets the same payment intent, and an intent is read
  back by its id. The Stripe webhook is the real `StripeWebhookHandler` given an event.
* **A recorder for one door**: the write of an automatic message in the conversation of a direct booking updates the thread with a bulk `ExecuteUpdate`, which the in-memory provider
  cannot run. `DirectThreadRecorder` takes the place of `IGuestMessageService` for that door only and keeps the text and the key of each message; the write itself is proved by the tests
  of the message layer (`guest-messages.md`), on PostgreSQL.
* **Seeded as the wizards leave it**: the agency (plan Agency, Stripe account ready, `AutoSendGuestsToOctorate` and `TouristTaxOnlinePaymentEnabled` on), the flat in Como on the channel
  manager, the official rate of the tourist tax, the rule of the portal that collects it, the member of the staff and the default cleaner, the two routines of the chain. The conversation
  of the portal reservation is seeded as the copy of the chat leaves it; the state of the chat of the listing is what the sync leaves.
* **Flags on**: `OctorateConnector`, `PortalChatCheckInLink`, `TouristTaxOnlinePayment`, `PurgeDocumentsAfterReceipt`, `Routines`, `ScheduledMessages`, `ComplianceTrafficLight`, and `CheckInIdentityVerification` in the test of the identity step only. Nothing else, and no
  flag is turned on anywhere by this page: the flags stay off in every environment until the coordinator turns them on in the test environment.

## 4. Limits

* **Prerequisite of the steps that go through the routines (B9, B10, B11).** The scheduled message, the cleaning at the check-out and the request of a review are actions of routines, and **every
  execution of a routine asks an AI provider which of its operations to run** (`RoutineAgentPlanner`). The world answers «all of them», so the chain is proved without a model. In a real
  environment the three steps run only with a provider in effect: `Ai__Provider` other than `Stub` (the default) and `Ai__ApiKey`. Without one the execution is closed `Failed` with
  `routine_ai_unavailable` (the host reads it in the log of the routine): nothing is cleaned, sent or asked. `OperationsChain_RoutinesNeedAnAiProvider_Test` shows it with the provider the application
  ships (`StubAiProvider`). The Railway test service has no `Ai__ApiKey`: set it there before relying on B9, B10 and B11. Second prerequisite: an organization with a commercial signup
  (`Org.CommercialSignupJson`) runs routines only with the add-on `ai-agents` paid and the subscription active (`RoutineService`, a missing access starts nothing and records nothing); the agency of
  the world is a legacy organization (no signup), which has the access.
* The in-memory provider keeps no foreign key, unique index or transaction and cannot run a bulk `ExecuteUpdate`: what depends on them is proved on PostgreSQL by the slice that wrote it
  (the `*Postgres*` classes of each feature). This chain proves that the steps hand the stay to one another.
* The recognition of the guest (the selfie, the models) is not run: no model is installed in a test. The identity test starts from what the flow leaves when it escalates to the operator (the session waiting, one row in the log of the verifications) and proves the gate the operator's decision opens; the pipeline has its own tests (`biometrics`). Every other test leaves the identity flag off, so the guests are written to the channel manager as soon as their data is complete.
* The e-mail of the link of a direct or manual booking is **queued**, not sent: the test asserts the state of the session (`Queued`) and the job handed to the background.
* The check-in link of the confirmation e-mail of a direct booking is recorded `Sending` while its e-mail is queued, and `Sent` or `Failed` once the job of the e-mail has run (the world runs no job): the test accepts the two states a queued link can be in (`Sent` or `Sending`).

## 5. When a feature of the operations chain changes

* A new step goes in the test of the origins it concerns, as a method of the world (`OperationsScenarioBuilder.Steps.cs`) that opens a scope and calls the real service, and an assertion on a
  state. A step that is not behind a flag needs no change of the world.
* A new flag of an operations step goes in `OperationsConfiguration.Settings`; it is never turned on for the tests of other classes.
* A new handler on the bus needs no change: the world registers the application's own extension methods, so a handler that is registered runs, and a handler that is not registered
  makes the chain stop at the step that needed it. A new extension method of a feature of the chain (a new `Add...` that `Program.cs` calls) is added to `Build()`.
* A step that cannot run in memory (a bulk `ExecuteUpdate`, a raw SQL) gets a recorder like `DirectThreadRecorder`, declared here, and a proof on PostgreSQL in its own feature.
