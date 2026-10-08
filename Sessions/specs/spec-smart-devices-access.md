---
id: US-029
slug: smart-devices-access
title: Smart devices — automatic property access
phase: 3
type: feature
priority: P2
status: planned
issue: 464
depends_on: [guest-check-in-portal, direct-checkout, saas-billing, tenant-boundary]
blocks: []
exit_contributes_to: Post-MVP host automation — unique access code per stay, remote lock, battery status
last_reviewed: 2026-10-08
---

# Spec — Smart devices, automatic property access (US-029)

> Template contract: `Sessions/specs/_TEMPLATE.md`.

## Overview

Hosts who use a smart lock still send door codes by hand. CasaZen already confirms bookings (direct and iCal) and already emails a guest check-in link. This feature connects the lock account to the org, maps a device to a property, and issues one access code per stay. The host can also lock or unlock remotely and see whether the lock is online and how much battery it has.

The product reference is Smoobu Smart Devices (page reviewed 2026-10-08, “Powered by Seam”): a catalog of lock brands, a paid-plan gate, unique codes per stay, remote control, and battery insight. Availability of each action depends on the lock brand.

**Done** means a Pro or Scale host can connect a sandbox lock, attach it to a property, see a unique code appear on a confirmed booking, have that code reach the guest by CasaZen email and on the check-in page, revoke it when the booking is cancelled, and lock or unlock from the booking. A Starter host sees the same catalog and cannot turn the integration on. No paid vendor account and no new price are required for that path.

**Phase:** 3 — Espansione · **Type:** feature · **Status:** planned · **Issue:** #464

Design: none yet. ADRs: none.

---

## User Story

As a host, I want CasaZen to talk to my smart lock so that each stay gets its own door code, I can open the door remotely, and I can see the battery before a guest arrives.

As a guest, I want the door code in the check-in email and on the check-in page so that I can enter without asking the host.

As CasaZen, we want one provider port, a free sandbox, and a plan gate, so that we do not sign a paid lock contract and do not invent a subscription price during prevendita.

---

## Acceptance Criteria

### Backend

- **AC1**: Tenant-scoped entities, each with `OrgId` (RF1):
  - `SmartAccessAccount` `{ Id, OrgId, Provider (Sandbox|Seam), DisplayName, Status (Connected|Disconnected|Error), ConnectedAt, LastSyncAt, LastError? }`. Provider credentials stored only as protected payload, never as a plaintext column.
  - `SmartDevice` `{ Id, OrgId, AccountId, ExternalId, Brand, DisplayName, Capabilities (flags: AccessCode, RemoteLock, Battery, OnlineStatus, KeyHandover), BatteryPercent?, Online?, PropertyId?, LastSyncedAt }`.
  - `StayAccessGrant` `{ Id, OrgId, BookingId, DeviceId, Status (Scheduled|Provisioning|Active|Failed|Revoked|Expired|Unsupported), ValidFromUtc, ValidUntilUtc, CodeProtected, ProviderGrantId?, FailureReason?, CreatedAt, UpdatedAt }`.
  - Unique index on `(BookingId, DeviceId)` for grants that are not `Revoked`.

- **AC2**: `IEntitlementService.CanUseSmartDevicesAsync(orgId)` is `true` only for effective tier `Pro` or `Scale`, and `false` for `Starter` (including Pro/Scale downgraded to Starter by the existing past-due rule). Connect, map, provision, and remote actions return **403** ProblemDetails with `code = smart_devices_plan_required` when the check fails. `GET` of the catalog (brand list, no secrets) stays allowed on every tier.

- **AC3**: Port `ISmartAccessProvider` with operations `ListDevices`, `CreateAccessCode`, `UpdateAccessWindow`, `RevokeAccessCode`, `Lock`, `Unlock`, `RefreshStatus`. `SandboxSmartAccessProvider` is always registered and performs no network call and no paid API. `SeamSmartAccessProvider` is registered only when `SmartAccess:Seam:ApiKey` is non-empty. When the key is absent, no code path calls Seam.

- **AC4**: Authenticated host routes, org-scoped:
  - `GET /api/smart-devices/catalog` returns the brand list below plus `disclaimer` and `canUseSmartDevices`.
  - `POST /api/smart-devices/accounts` with `{ provider: "sandbox" }` creates a connected sandbox account and two demo devices (one with `AccessCode|RemoteLock|Battery|OnlineStatus`, one with `KeyHandover` only). `provider: "seam"` returns **503** `smart_devices_provider_unconfigured` when `SmartAccess:Seam:ApiKey` is empty, and never creates an account in that case.
  - `POST /api/smart-devices/accounts/{id}/sync` refreshes devices.
  - `PUT /api/smart-devices/{id}/property` body `{ propertyId: guid|null }` maps or unmaps. The property must belong to the same org. Unmapping does not delete historical grants.

- **AC5**: Brands returned by the catalog, in this order: Yale, Nuki, August, Schlage, Igloohome, Salto, Tedee, SmartThings, KeyNest, plus `moreBrands: true`. The catalog text states that functions vary by provider. A device is usable for automatic codes only when `AccessCode` or `KeyHandover` is set. Remote lock actions exist only when `RemoteLock` is set.

- **AC6**: When a booking enters `Confirmed` and the property has at least one mapped device, enqueue `StayAccessProvisionJob` (Hangfire, not inline HTTP). One grant per mapped device:
  - device has `AccessCode` or `KeyHandover` → status `Scheduled`;
  - otherwise → status `Unsupported` and no provider call.
  A property with zero mapped devices creates no grant and does not change booking confirmation.

- **AC7**: The job runs when `now >= ValidFromUtc - 24h` (provision lead time). It calls `CreateAccessCode` once. Success sets `Active`, stores the code only in `CodeProtected`, and stores `ProviderGrantId`. A second run for the same `(BookingId, DeviceId)` does not create a second provider code. Provider slot-full or timeout sets `Failed` with `FailureReason` and notifies the host. The code value is unique per grant (sandbox: 6 digits, not reused across active grants of that device).

- **AC8**: Validity window uses the property timezone. Default check-in time 15:00 and check-out time 10:00 local when the booking has no explicit times. `ValidFromUtc` = local check-in minus **60 minutes**. `ValidUntilUtc` = local check-out plus **60 minutes**. At `ValidUntilUtc` a sweep job sets still-`Active` grants to `Expired` and calls `RevokeAccessCode`.

- **AC9**: Booking date change on a `Scheduled` or `Active` grant updates the window. If the provider cannot update, the job revokes and creates a new code (still one non-revoked grant per device). Host cancel and guest cancel both revoke every non-terminal grant for that booking (`Revoked`) and call `RevokeAccessCode` when a `ProviderGrantId` exists. Disconnecting an account revokes `Scheduled`, `Provisioning`, and `Active` grants for its devices.

- **AC10**: `POST /api/smart-devices/{id}/lock` and `.../unlock` require `RemoteLock`. They call the provider, append `SmartAccessEvent` `{ OrgId, PropertyId, DeviceId, BookingId?, ActorUserId, Action (Lock|Unlock|CodeCreated|CodeRevoked|SyncFailed), OccurredAt }`, and return the new lock state. Missing capability returns **409** `smart_devices_capability_unsupported`. Events store no guest name and no access code.

- **AC11**: `POST /api/smart-devices/accounts/{id}/sync` updates `BatteryPercent` and `Online` when those capabilities exist. A transition to offline, or battery falling below **20%**, sends one host notification per transition (email from the CasaZen sender plus `DeviceNotification`). Recovery above 20% or back online clears the alert so the next drop can notify again.

- **AC12**: Guest read: `GET /api/public/check-in/{token}` adds `accessCodes: [{ deviceName, code, validFromLocal, validUntilLocal }]` only for `Active` grants of that booking whose window contains `now`. Expired, revoked, failed, and unsupported grants are omitted. The plaintext code is not written to logs, traces, or ProblemDetails.

- **AC13**: On transition to `Active`, send one CasaZen email to the guest when `Booking` has a guest email. Subject and body are Italian and contain property name, code, and local validity window. No email when the guest email is missing (iCal block without an address): the grant still becomes `Active` and the host API returns the code. Sender is the CasaZen platform address, not the host’s personal address and not the lock vendor.

- **AC14**: `GET /api/bookings/{id}/access` (host, same org) returns each grant’s status, device name, window, and the plaintext code only while status is `Active`. Other orgs receive **404**. Provider secrets and `CodeProtected` ciphertext are absent from every host and guest DTO except the single plaintext `code` field defined here and in AC12.

### Frontend — host web

- **AC15**: Configuration nav entry **Dispositivi smart** with badge **Beta**, route `/app/short-rent/settings/smart-devices`. The page shows, in Italian:
  - intro: “Automatizza check-in e check-out collegando la serratura smart al tuo account CasaZen. Un codice unico per soggiorno, controllo remoto e stato della batteria.”
  - disclaimer: “La disponibilità delle funzioni può variare in base al fornitore della serratura.”
  - the nine brands of AC5, then “…e decine di altri marchi, aggiunti di continuo.”
  - text link “Scopri di più sui Dispositivi smart” scrolling to an on-page section that repeats the three capabilities (codice, controllo remoto, batteria). No new marketing hostname.

- **AC16**: When `canUseSmartDevices` is false, a banner states exactly: “Questa integrazione non può essere attivata perché richiede un piano a pagamento.” The primary action links to `/app/short-rent/settings/plan`. The page shows no euro amount and no “Subscribe” price. Connect and map controls are not actionable.

- **AC17**: When `canUseSmartDevices` is true and the account list is empty, the empty state says “Nessuna serratura collegata. Collega un account per generare un codice unico a ogni soggiorno.” Primary button “Collega serratura” calls `POST /api/smart-devices/accounts` with `provider: "sandbox"`. A failed connect shows the ProblemDetails title in Italian, not a stack trace. When Seam is unconfigured, the page does not offer a Seam button.

- **AC18**: With devices, each card shows brand, name, online/offline, battery percent when known, and the mapped property or “Nessun alloggio”. “Assegna alloggio” lists only properties of the org and saves with `PUT /api/smart-devices/{id}/property`. After save, the card shows the property name.

- **AC19**: Booking detail shows a “Accesso” section: one row per grant with Italian status (Programmato, In preparazione, Attivo, Non riuscito, Revocato, Scaduto, Non supportato), the window, and the code when the API returns it. For `RemoteLock` devices, buttons “Apri” and “Chiudi” call AC10 and show the new state. Unsupported capability hides those buttons. A failed grant shows `FailureReason` as a human message.

### Frontend — guest

- **AC20**: On `/check-in/:token`, when `accessCodes` is non-empty, a section “Codice di accesso” lists device name, code, and local window in Italian. When the array is empty or absent, the check-in form does not show an empty access card and the existing check-in success screen is unchanged.

### App host

- **AC21**: The host app booking screen shows the same grant rows as AC19 (status, code when present, Apri/Chiudi when `RemoteLock`). Battery below 20% or offline shows the notification from AC11 and opens that booking. The app does not host the connect wizard or the brand catalog; those stay on the web page.

### Regression and prevendita

- **AC22**: Confirming, rescheduling, or cancelling a booking on a property with no smart device keeps today’s booking responses and does not call `ISmartAccessProvider`.

- **AC23**: With `SmartAccess:Seam:ApiKey` unset, the sandbox happy path (connect → map → confirm booking → active code → guest read → cancel → revoked) completes with zero outbound calls to Seam or any other lock vendor. No Stripe price id, live key, or euro amount is added for this feature.

---

## Verifiable Outcomes

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Inserting account, device, and grant persists `OrgId`. A second non-revoked grant for the same booking and device violates the unique index. | Row saved with null `OrgId`; duplicate active grants |
| AC2 | L1 | Starter `CanUseSmartDevicesAsync` is false and `POST /api/smart-devices/accounts` is 403 `smart_devices_plan_required`. Pro returns 201 for sandbox. `GET /api/smart-devices/catalog` is 200 for Starter and includes `canUseSmartDevices: false`. | Starter can connect; catalog hidden from Starter; 403 without `code` |
| AC3 | L1 | Resolving `ISmartAccessProvider` for Sandbox does not read `SmartAccess:Seam:ApiKey`. With the key empty, the Seam implementation is not registered. A test HTTP spy records zero Seam calls during AC23. | Seam client constructed without a key; sandbox opens a socket |
| AC4 | L1 | Sandbox connect returns two devices. Sync is idempotent on `ExternalId`. Mapping a property of another org is 404. Unmap sets `PropertyId` null and leaves old grants. | Cross-org map succeeds; sync duplicates devices |
| AC5 | L1 | Catalog `brands` equals Yale, Nuki, August, Schlage, Igloohome, Salto, Tedee, SmartThings, KeyNest and `moreBrands` is true. A device without `AccessCode` and without `KeyHandover` never receives `CreateAccessCode`. | Brand missing; code created on a remote-only device |
| AC6 | L1 | Confirmed booking on a mapped property creates one grant per device. Unsupported device → `Unsupported` and zero provider create calls. No device → zero grants and the booking still `Confirmed`. | Grant on an unmapped property; confirmation blocked by a lock error |
| AC7 | L1 | Before the 24h lead, grant stays `Scheduled` and provider create count is 0. After the lead, status is `Active`, provider create count is 1, and a second job run does not increment it. Forced provider failure sets `Failed` and one host notification. | Code created months ahead; two codes for one stay; failure leaves `Provisioning` forever |
| AC8 | L1 | For check-in date D at 15:00 and check-out D+2 at 10:00 in `Europe/Rome`, window is D 14:00 to D+2 11:00 local, stored in UTC. After `ValidUntilUtc`, status is `Expired` and revoke was called. | Window uses UTC as if it were local; expired grant stays `Active` |
| AC9 | L1 | Moving the booking moves `ValidFromUtc`. Host cancel and guest cancel set `Revoked` and the provider revoke count matches grants that had `ProviderGrantId`. Account disconnect revokes active grants of that account only. | Cancel leaves `Active`; disconnect revokes another org |
| AC10 | L1 | Unlock appends `SmartAccessEvent` with `Action=Unlock`, `ActorUserId`, and no field equal to the access code. Device without `RemoteLock` returns 409 `smart_devices_capability_unsupported`. | Event row contains the PIN; unlock without capability returns 200 |
| AC11 | L1 | Sync from 40% to 19% sends one notification. A second sync at 18% sends none. Sync to 25% then to 19% sends a second notification. Offline transition behaves the same way. | Notification on every sync; no notification on the first drop |
| AC12 | L1 | Public check-in DTO includes `code` only for an `Active` grant inside the window. Log sink for that request does not contain the code. | Code present when revoked; code found in the log assertion |
| AC13 | L1 | Active transition with guest email enqueues one message whose From is the CasaZen sender and whose body contains the code and the property name. Missing email enqueues zero messages and the host access DTO still has the code. | Email sent from the host address; missing email fails the grant |
| AC14 | L1 | Host in the org gets the code only for `Active`. A host in another org gets 404. JSON has no `codeProtected` property. | Cross-org 200; ciphertext returned to the client |
| AC15 | L2 + L3 | Page shows the Italian intro, the disclaimer sentence, all nine brands, the “decina di altri marchi” line, and the Beta badge. The learn-more control stays on the same URL. | English intro; brand missing; navigation to an external host |
| AC16 | L2 + L3 | Starter sees the exact banner sentence and a link to `/app/short-rent/settings/plan`. Screenshot text has no `€` and no digit price. Connect button is absent or disabled. | Banner missing; a price like 29 shown; Starter can press Collega |
| AC17 | L2 + L3 | Empty Pro org shows the empty-state sentence. Collega serratura results in two device cards. A stubbed 500 shows an Italian message and no stack. | Blank page; English empty state; raw exception on screen |
| AC18 | L2 + L3 | Choosing a property and saving shows that property name on the card after reload. A property of another org is not in the list. | List shows every tenant’s properties; name not saved |
| AC19 | L2 + L3 | Booking with an `Active` grant shows “Attivo”, the code, and Apri/Chiudi. Clicking Apri shows the unlocked state. An `Unsupported` row has no Apri/Chiudi and shows “Non supportato”. | Code missing while API returned it; buttons on an unsupported device |
| AC20 | L2 + L3 | Guest page with a non-empty `accessCodes` shows “Codice di accesso” and the code. Guest page with `accessCodes: []` has no such heading and the check-in form still submits. | Empty heading; check-in form removed |
| AC21 | L2 | App booking fixture with an active grant shows the code and Apri. Fixture without `RemoteLock` hides Apri. The app navigation has no “Collega serratura” route. | App requires the web connect step to display a code |
| AC22 | L1 | Booking service test with no devices: confirm, date change, and cancel perform zero calls on a provider spy. | Provider invoked for a normal booking |
| AC23 | L1 | Full sandbox path under an empty Seam key: grant ends `Revoked` after cancel, and the HTTP spy’s Seam host count is 0. Diff of billing config and entitlement price map contains no new price id and no amount. | Test skips unless a real API key is present; new `Billing__Prices__SmartDevices` key |

---

## UX / UI Quality

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Host connects, assigns, and reads a code without a second product | L3 script below, 4 screens |
| Language | Italian labels on primary controls | L2/L3 assert the strings in AC15–AC20 |
| Empty state | No blank panel when there is no account | L2 empty fixture shows the AC17 sentence |
| Error state | 403/409/5xx show a human Italian message | L2 stub 403 shows the AC16 banner; stub 500 shows ProblemDetails title |
| Destructive / legal copy | Paid-plan sentence is exact; no invented price | L2/L3 assert the AC16 sentence and the absence of `€` |

**Happy-path script:**

1. Start at `/app/short-rent/settings/smart-devices` as a Pro host with no account.
2. Press “Collega serratura”. Two sandbox devices appear.
3. Assign the access-code device to a property. Open a confirmed booking for that property after the provision lead. The Accesso section shows “Attivo” and a code.
4. Done when Apri returns a lock state and, after cancel, the same section shows “Revocato” and the guest check-in payload no longer includes the code.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/SmartAccessAccount.cs` | Create — AC1 |
| `Casazen.Core/Entities/SmartDevice.cs` | Create — AC1, AC5 |
| `Casazen.Core/Entities/StayAccessGrant.cs` | Create — AC1, AC7 |
| `Casazen.Core/Entities/SmartAccessEvent.cs` | Create — AC10 |
| `Casazen.Core/Services/ISmartAccessProvider.cs` | Create — AC3 |
| `Casazen.Infrastructure/SmartAccess/SandboxSmartAccessProvider.cs` | Create — AC3, AC23 |
| `Casazen.Infrastructure/SmartAccess/SeamSmartAccessProvider.cs` | Create — registered only when `SmartAccess:Seam:ApiKey` is set |
| `Casazen.Core/Services/IEntitlementService.cs` | Modify — `CanUseSmartDevicesAsync` |
| `Casazen.Infrastructure/Services/BookingService` (confirm, reschedule, cancel) | Modify — enqueue or revoke grants; no provider call when unmapped (AC6, AC9, AC22) |
| `Casazen.Web/Controllers/SmartDevicesController.cs` | Create — AC2, AC4, AC10, AC14 |
| Guest check-in public DTO + mail composer | Modify — AC12, AC13 |
| Frontend route `/app/short-rent/settings/smart-devices` | Create — AC15–AC18 |
| Booking detail “Accesso” | Modify — AC19 |
| Guest `/check-in/:token` | Modify — AC20 |
| Host app booking screen | Modify — AC21 |
| EF migration | Create — four tables, unique filtered index, `OrgId` FKs |

**Complexity:** L  
**Migration:** yes — new tables only, no change to booking columns  
**Dependencies:** `guest-check-in-portal`, `direct-checkout`, `saas-billing`, `tenant-boundary`  
**Repos:** backend, frontend, mobile

Provider credentials and `CodeProtected` use the same data-protection mechanism as other secrets at rest (iCal import URL). Logs and traces redact `code`, `CodeProtected`, and `ApiKey`.

The Seam adapter, when configured later, is the single real vendor used to reach Yale, Nuki, August, Schlage, Igloohome, Salto, Tedee, SmartThings, and KeyNest. CasaZen does not implement a separate HTTP client per brand. Feature flags on the device come from the provider, not from a hardcoded brand table.

---

## Test expectations

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit on entitlement, lifecycle, redaction, and the HTTP spy for AC23 | “Compiles”; a test that no-ops when the Seam key is missing |
| L2 | Playwright with `page.route`, titled `test('ACn: …')` for AC15–AC21 | One smoke covering every AC; visibility-only without the Italian strings |
| L3 | Real local API for the sandbox path in the happy-path script | Calling Seam; mocking the booking confirm path that AC6 claims to hook |

---

## Regulatory / Legal Gates

None for the access-code flow itself.

Guest name stays off `SmartAccessEvent`. The access code is a secret, not a document sent to a public authority. Retention of `SmartAccessEvent` defaults to **24 months**, then the row may be deleted; `BookingId` is not replaced with a guest snapshot. This operational default is not a privacy-policy text: the public privacy copy stays whatever the PO already supplies.

---

## Out of Scope

- Thermostats, cameras, noise sensors, and energy dashboards.
- A native connect wizard in the host app.
- Pushing the code into Airbnb or Booking.com messages (no OTA messaging API; iCal guests without an email are a host-visible code only).
- Per-brand HTTP integrations.
- Buying or configuring a paid Seam (or other vendor) production account. The Seam adapter stays unwired until an API key is deliberately set.
- A new CasaZen price, Stripe price id, or live charge. Prevendita rule of 2026-10-08 stands: plans stay free of charge, and this spec does not pick a price.
- Multiple pins chosen by the guest, permanent owner codes, and keypad user directories.
- KeyNest store logistics beyond a `KeyHandover` code issued by the provider. Staff pickup schedules and physical key inventory are not modeled.

---

## Open Questions

- **Which tier unlocks the feature.** Default in this spec: Pro and Scale, not Starter. Same shape as custom domain. Owner: product. Date opened: 2026-10-08.
- **Lead time and grace.** Default: provision 24h before the window, window starts 60 minutes before local check-in and ends 60 minutes after local check-out. Owner: product. Date opened: 2026-10-08.
- **Low-battery threshold.** Default: 20%, one notification per crossing. Owner: product. Date opened: 2026-10-08.
- **Audit retention.** Default: 24 months, no guest snapshot. Owner: product / privacy text later. Date opened: 2026-10-08.
- **Real vendor.** Default: Seam-compatible adapter, off until a key exists. Not purchased under the 0-cost rule. Owner: product. Date opened: 2026-10-08.
