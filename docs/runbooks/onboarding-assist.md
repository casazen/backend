# Runbook: «Fallo per me», the support vault, the checklist and «Fatto»

Tasks E20/F03/US01/T01 (the vault, §§ 1-8) and E20/F03/US01/T02 (the checklist, «Fatto» verified or declared, «Non funziona», § 9) of the operations
plan of 2026-11-14 (decision O5; PO-19 refined on 3/11, ONB-04, UX-15, NF-15, NF-107). The «Do it for me» requests themselves (the host asks, staff
takes, completes or closes) are E10/F01/US03/T01: `docs/TECHNICAL.md`, `in-app-notifications.md` § 10.

## 1. What it is

A structure that CasaZen creates on Octorate gets an Octorate user whose credentials arrive **only** at a CasaZen address,
`octorate-{orgId:N}@{Octorate__OwnerEmailDomain}` (`OctoratePropertyCreator`, PO-19): the host receives nothing from Octorate. Linking the police
(Alloggiati Web) and ISTAT in the Octorate panel can only be done from that panel, so CasaZen support does it for the host, with the host's
credentials. The **support vault** is where those credentials live, instead of a mailbox:

* the user of the panel of a structure (kind `OctoratePanel`), copied by support from the welcome e-mail;
* the police credentials of the property: the existing write-only `PropertyQuesturaCredentials` (CO-14), never copied;
* the regional ISTAT credentials of the property: the host enters them in its own write-only form (`PropertyIstatCredentials`), or support stores
  the ones the host handed over (kind `RegionalIstat`).

Nobody reads a value except through the **controlled opening**: a platform administrator, on an **open** «Do it for me» request of the same org and
property, with a reason, at most five times an hour, audited, and told to the host in its activity log. Everything sits behind
`Features__OctorateOnboarding` (off by default), like the requests: 404 while it is off.

## 2. The data

| Table | What | Rules |
|---|---|---|
| `SupportVaultSecrets` | `OrgId`, `PropertyId?` (none = the whole org), `Kind` (`OctoratePanel` 1, `RegionalIstat` 2), `Username`, `Password`, `ExtraCode?` (**encrypted**, purpose `Casazen.SupportVault`), `CreatedByUserId`, `CreatedAt`, `UpdatedByUserId`, `UpdatedAt` | **unique** `(OrgId, PropertyId, Kind)` with `NULLS NOT DISTINCT` (`UIX_SupportVaultSecrets_OrgId_PropertyId_Kind`): a second store replaces the values |
| `SupportVaultAccesses` | `OrgId` (of the request), `RequestId`, `PropertyId?`, `Kind` (`OctoratePanel` 1, `Police` 2, `RegionalIstat` 3), `Reason` (10-300), `AccessedByUserId`, `AccessedAt` | one row per opening, **never the value**; index `(RequestId, AccessedAt)` counts the openings of the last hour |
| `PropertyIstatCredentials` | `PropertyId` (unique), `OrgId`, `Username`, `Password`, `StructureCode?` (**encrypted**, purpose `Casazen.PropertyIstatCredentials`), `CreatedAt`, `UpdatedAt` | write-only for clients, like `PropertyQuesturaCredentials` |

Migration: `AddSupportVault`, the one migration of the two tasks: it creates the three tables and adds three nullable columns to
`OnboardingAssistRequests` (`Outcome` 20, `HostNote` 500, `ReopenedAt`, § 9); the rows that exist keep `NULL`. The encryption is the one of
`encryption.md`: the values go through the Data Protection converter of `AppDbContext` and the key ring of the environment; nothing new to configure.

**Erasure.** The three tables follow their org **and** their property with `ON DELETE CASCADE` (unlike the other tenant tables, which restrict), and
the audit follows its request: the vault never outlives what it belongs to, and never blocks its erasure. A credential can also be removed by staff
(§ 3) and the host removes its ISTAT credentials from its form (§ 4).

## 3. Storing a credential (staff)

When CasaZen creates a structure, Octorate sends the welcome e-mail with the credentials of its user to `octorate-{orgId:N}@{domain}`. The 32 hex
digits of the address are the id of the org.

1. Open the e-mail in the support mailbox. Find the property: the structure is named in the e-mail; the admin pages list the properties of the org.
2. Store it: `PUT /api/admin/onboarding/support-vault` (policy `AdminOnly`), body
   `{ "orgId": "…", "propertyId": "…", "kind": "OctoratePanel", "username": "…", "password": "…", "extraCode": null }`. A user name and a password of
   at most 200 characters, a further code of at most 100 (422 `support_vault_value_invalid`); 404 `support_vault_org_not_found` for an id that is not
   a host org, 404 `property_not_found` for a property of another org. The answer is the presence (`id`, `kind`, `propertyId`, dates, who), never a value.
3. **Delete the e-mail** from the mailbox **and from its trash**. The credentials must not stay in a mailbox (requirement of the ADR list, item 11).

`GET /api/admin/onboarding/support-vault?orgId=…` lists what the vault holds for an org (presence only); `DELETE /api/admin/onboarding/support-vault/{id}`
removes a credential (204 also when there was none). The application logs say who stored, replaced or removed which credential (ids only).

## 4. The host's ISTAT credentials

`PUT|GET|DELETE /api/properties/{propertyId}/istat-credentials`, the same policies and checks as the Questura credentials (`PropertyRead` for the
status, `PropertyWrite` to change; another org's property is 404, a property the caller may not manage is 403). Body of the `PUT`:
`{ "username", "password", "structureCode"? }` (100, 200 and 50 characters; the password is kept exactly as typed). Every answer is
`{ "configured", "configuredAt" }`, never a value.

## 5. The controlled opening

`POST /api/admin/onboarding/assist-requests/{id}/reveal`, policy `AdminOnly`, body `{ "kind": "OctoratePanel" | "Police" | "RegionalIstat", "reason": "…" }`.

| Rule | Answer when broken |
|---|---|
| The request exists | 404 `assist_request_not_found` |
| The request is open (`Requested` or `InProgress`) | 409 `support_vault_request_not_open` |
| The reason is 10 to 300 characters once trimmed | 422 `support_vault_reason_invalid` |
| The kind is one the request needs: the panel for every request; the police credentials for the police connection (or the whole compliance part) of a property; the ISTAT ones for the ISTAT connection (or the whole compliance part) of a property | 422 `support_vault_kind_not_for_request` |
| At most **5 openings of the request in the last hour**, whatever the kind, counted under the transaction lock of the request (`OnboardingPanelAssist`, key `assist:{id:N}`, the lock of its staff updates) | 429 `support_vault_too_many_openings` |
| The credential exists **for the org and the property of the request** (the ISTAT ones: the host's form first, then the vault) | 404 `support_vault_secret_missing` |

The answer is `{ kind, username, password, extraCode, openedAt, visibleUntil }` with `Cache-Control: no-store`, once; `visibleUntil` is one minute
later and the client hides the values then. For the police credentials `extraCode` is the web service key; for the ISTAT ones the code of the
structure. In the same save as the opening:

* a row of `SupportVaultAccesses` (who, when, request, kind, reason; **never the value**);
* the line `OnboardingAssistUpdated` in the log of the **org of the request**, staff user as the actor, details `part`, `context`, `status` (the
  request's, unchanged) and `credential` (`panel`, `police`, `regionalIstat`): the host reads «Il supporto CasaZen ha usato le credenziali per
  collegare …». Never the value, never the reason (`org-team.md`).

## 6. What never leaks

The values are never in a log line (the services log ids, kinds and counts), an exception (the errors are typed codes with resource keys), a
ProblemDetails, a Hangfire argument (nothing of the vault is queued) or the activity log; the inputs, the opening and the DTOs hide themselves from
`ToString`. The reason of an opening is kept in the audit only and is not logged either. Proof: `SupportVaultNoSecretInLogsTests`.

## 7. Checking, rolling back

* What the vault holds, without decrypting anything:
  `select "OrgId", "PropertyId", "Kind", "UpdatedAt", "UpdatedByUserId" from "SupportVaultSecrets" order by "UpdatedAt" desc;`
* The openings of the last day:
  `select "OrgId", "RequestId", "Kind", "AccessedByUserId", "AccessedAt" from "SupportVaultAccesses" where "AccessedAt" > now() - interval '1 day';`
* Switch off: `Features__OctorateOnboarding=false` hides every route (404); the tables stay.
* Roll back the schema: `dotnet ef database update <the migration before AddSupportVault>` drops the three tables **and the credentials in them**
  (support must store them again from the source, and the hosts enter their ISTAT credentials again) and the three columns of § 9 (the outcomes of
  «Fatto» and the notes of «Non funziona» are lost; the requests and their statuses stay).

## 8. Code and tests

`Casazen.Core/Entities/{SupportVaultSecret,SupportVaultAccess,PropertyIstatCredentials}.cs`,
`Casazen.Core/Services/{ISupportVaultService,IPropertyIstatCredentialsService}.cs`,
`Casazen.Infrastructure/Services/{SupportVaultService,PropertyIstatCredentialsService}.cs`,
`Casazen.Infrastructure/Data/Configurations/SupportVaultConfigurations.cs`, `EncryptedColumns.cs`,
`Casazen.Web/Controllers/{AdminSupportVaultController,PropertyIstatCredentialsController,AdminOnboardingAssistController}.cs`,
`Casazen.Web/DTOs/Onboarding/SupportVaultDtos.cs`. Tests: `SupportVaultTests` (the opening only on an open request, admin only, the audit without
the value, the ISTAT credentials write-only, the cascades and the encryption), `SupportVaultNoSecretInLogsTests`. § 9: `OnboardingAssistService`
(`GetAsync`, `UpdateAsync`, `ReopenAsync`), `OnboardingAssistOutcomeNotice`, `OrgScopedPanelVerification` (`OnboardingPanelVerifiers.cs`);
tests `OnboardingAssistVerificationTests`.

## 9. The checklist, «Fatto» and «Non funziona» (E20/F03/US01/T02)

**The checklist.** `GET /api/admin/onboarding/assist-requests/{id}` (`AdminOnly`) answers the request and, per property, what support needs before
working: `panelUser` (the user of the panel in the vault), `policeCredentials`, `istatCredentials` with `requirement` (`required` for a request about the
ISTAT connection, `notRequired` outside Italy, otherwise `toVerify`: the region decides and the table of the regions, E21, is not there yet), `sender`
(the stored sender of the property and whether the host declared it active; `null` = nothing stored, the default applies) and `steps` (each panel step
of the request with `instructionsKey`, the guide of the host's guided step, and `state`: `notStarted` or the status of the step). A request of the whole
org lists the org itself first (the user of the panel of the whole org and its steps), then its properties (at most 100). Presence and dates only:
nothing is decrypted to build it. Every read names the org of the request.

**«Fatto».** `PUT /api/admin/onboarding/assist-requests/{id}` with `done` on a panel context first runs the verifier of the step **for the org of the
request** (`OrgScopedPanelVerification`: a scope of its own under that org, because the staff user's own context reads only its org and may not call the
channel manager for another one), outside the lock of the request:

| What the panel says | The request (`outcome`) | The step | The host is told |
|---|---|---|---|
| police: `policeAccount` readable and `valid` | `verified` | `verified` (`VerifiedAt`, the staff user as who completed it) | `assist-verified`: «Il collegamento è attivo» |
| not readable (a structure created through the API has no `policeAccount`, S25; ISTAT never; the access to a channel unless the marketplace verifier reads it) | `declared` | `confirmed`, the staff user as who completed it | `assist-declared`: «Il supporto dichiara di aver collegato …» |
| readable and **not valid** | 409 `assist_done_not_valid`, nothing changes | unchanged (`assistRequested`) | nobody |
| the read failed (the channel manager down, the quota used) | the error of the channel manager (502, 429 …), nothing changes | unchanged | nobody |

A request of a whole part has no step: `done` records `declared`. After the commit every administrator of the org (the owner and the `Admin`s, who
alone can ask for help) gets a push, an entry of the bell (the request as its entity) and the live update, so the open page reads the request again and
shows the pop-up (`OnboardingAssistOutcomeNotice`; key `assist-request:{id}:done:{instant}:{user}`, one per «Fatto» and person). Best effort: each administrator
is told on its own, so a failure for one (its bell entry, its push) is logged and never stops the others nor fails the update.

**«Non funziona».** `POST /api/onboarding/assist-requests/{id}/reopen` (`OrgBillingAdmin` + `HostScope`), body `{ "note": "…" }` (required, at most 500
characters, no e-mail address: 422 `assist_reopen_note_required`). Only a request `Done` (409 `assist_request_not_done`), and not while another request
of the same part, property and channel is open (409 `assist_request_already_open`); a request of another org or of a property the caller does not reach
is the 404 of a request that does not exist. The request goes back to `Requested` with `hostNote` and `reopenedAt` (the outcome, the completion and the
assignee are cleared), its step goes back to support (`assistRequested`, what «Fatto» recorded on it cleared), the host's "To do" comes back (the request names none, `ActivityId` cleared in the same save,
until the new one is raised after the commit: if that fails, the next «Fallo per me» of the host finds the request open with no "To do" and raises it), the line
`OnboardingAssistRequested` (status `requested`, the host as the actor) is written and the platform staff is told live (`assist-reopened`).
