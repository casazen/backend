# Runbook: onboarding gate and legal consents

Task PL-02 (audit defects A1-05, A1-33 gating part). Before it, a user registered on Auth0 who skipped the web
onboarding got the short-rent context with every write permission from the default DB role (`PropertyOwner`) or from
a JWT role, and could create an org, properties and guests without accepting the Terms of Service, the Privacy notice
and the DPA. The consent was enforced only by the web guard.

## What the backend does

| Concern | Behaviour | Code |
|---|---|---|
| New users | Created with `UserRole.None` (value 7, appended: the stored values 0-6 keep their meaning). The onboarding sets `PropertyOwner` / `LongTermLandlord` (an `Admin` stays `Admin`) | `User.Role`, `UserService.GetCurrentUserAsync`, `CompleteOnboardingAsync` |
| Host gate | The `short-rent` and `long-rent` contexts, and every permission they carry, are granted only when `User.OnboardingCompletedAt` is set **and** the user's org has `ConsentRecords` for Terms (`Tos`), Privacy and DPA at the **current** versions (`Legal:Documents:{Tos,Privacy,Dpa}:Version`). JWT roles, DB memberships and the DB role do not bypass it | `HostOnboardingGate`, `ContextAuthorizationService` |
| Answer | 403 ProblemDetails, `code: "onboarding_required"`, localized `detail` (IT/EN) | `ContextAuthorizationHandler` → `OnboardingRequiredAuthorizationResultHandler` |
| Org billing | `RequireOrgBillingAdmin` (plan, billing, custom domain) waits for the same gate, platform admins included | `OrgBillingAdminAuthorizationHandler` |
| No hidden org | The org is created only by `POST/PUT /api/users/onboarding` with the consents. `IOrgContextResolver` no longer auto-provisions one; `POST /api/devices` answers `onboarding_required` to a user without org | `OrgContextResolver`, `DevicesController` |
| Not gated | `/api/users/me`, `/api/users/onboarding`, `/api/legal/*`, `/api/onboarding/status`, `/api/me/contexts`, admin endpoints (`AdminOnly`) and supplier endpoints (`RequireSupplier`) | `CasazenPolicies` |
| Profile | `GET /api/users/me` returns `onboardingRequired` (host features withheld) and `consentsAccepted` (current Terms, Privacy, DPA accepted) | `UsersController.GetMe` |
| Cache | The gate reads the authorization snapshot (per request, then 60 s per user, `Authorization:UserCacheSeconds`), invalidated when the onboarding completes and when consents are recorded. Document versions are compared at every evaluation | `UserAuthorizationSnapshotStore` |

The subprocessors acknowledgement is still collected by the onboarding but does not block the host features: its
version changes by itself when an external AI provider is switched on (`LegalDocumentService.GetSubprocessors`).

## Clients

- **Web** (`frontend`): a 403 `onboarding_required` (read or write) opens `/onboarding` and refreshes the profile,
  except in the admin and supplier areas. A host whose onboarding is completed but whose consents are of an old
  version sees only the consents step ("Documenti legali aggiornati") and the same rental type is submitted again.
- **Mobile** (`mobile`): after the login the app reads `/users/me`; until the gate opens it shows "Completa
  l'attivazione sul sito" with a button to `EXPO_PUBLIC_WEB_URL` + `/onboarding` (no domain in the code), "Ho
  completato, riprova" and "Esci". No host screen and no push registration before that. A 403
  `onboarding_required` from any call brings the screen back.

## Publishing a new version of a legal document

1. Publish the text (task PL-14 for the pages; the texts come from the product owner, decision D14).
2. Change the version in Railway, per environment: `Legal__Documents__Tos__Version`,
   `Legal__Documents__Privacy__Version` or `Legal__Documents__Dpa__Version` (defaults in `appsettings.json`).
3. From the next request (at most the 60 s cache for users already in memory) every host gets
   `onboarding_required` until they accept the new version: web users land on the consents step, app users on the
   activation screen. Admin and supplier areas keep working.

Changing a version therefore locks every host out of the host features until re-acceptance: coordinate the change
with a communication to the hosts.

## Data migration `AssignNoneRoleToUsersWithoutOnboarding`

Applied at start-up with the other migrations, two statements:

1. Users who completed the onboarding before `OnboardingCompletedAt` existed (16 June 2026) have a `RentalType`
   (written only by the onboarding) and no timestamp: they get one, their first consent record or else their account
   creation date (an approximation, documented here). It grants nothing by itself: the gate still wants the current
   consents, so these hosts see only the consents step instead of the whole onboarding.
2. Only the users with no trace of use move to `None`: `Role = PropertyOwner`, no `OnboardingCompletedAt`, no
   `RentalType`, no org, no supplier link, no consent record, no property. Everybody else keeps the stored role
   (admins, suppliers, landlords, onboarded hosts, users with an org).

Context memberships are not touched. `Down` turns every `None` back into `PropertyOwner` (the previous default) and
keeps the backfilled timestamps (the previous code treats those users as onboarded anyway).

Existing users, after the deploy:

| User | Before | After PL-02 |
|---|---|---|
| Onboarded with current consents | Host features | Unchanged |
| Onboarded (rental type chosen), consents missing or of an old version, e.g. onboarded before the consents existed (11 June 2026) | Host features | Consents step on the web, then host features. Role and data unchanged |
| Org created automatically by an API or app call, never onboarded (no rental type) | Host features without consents | Role and data unchanged; host features after the onboarding with consents (the existing org is reused) |
| Never onboarded, no org | Host features via the default role | Role `None`; onboarding as any new user |
| Platform admin | Admin console | Admin console unchanged; host org and billing after the onboarding |
| Supplier | Supplier console | Unchanged |

Check before and after the deploy, on the database of the environment (read-only):

```sql
-- How many users each class contains (run before the deploy to know the impact).
SELECT
  count(*) FILTER (WHERE "Role" = 1 AND "OnboardingCompletedAt" IS NULL AND "RentalType" IS NULL
                   AND "OrgId" IS NULL AND "SupplierOrgId" IS NULL)              AS never_activated,
  count(*) FILTER (WHERE "OnboardingCompletedAt" IS NULL AND "RentalType" IS NOT NULL) AS onboarded_without_timestamp,
  count(*) FILTER (WHERE "OnboardingCompletedAt" IS NULL AND "RentalType" IS NULL AND "OrgId" IS NOT NULL
                   AND "SupplierOrgId" IS NULL AND "Role" IN (1, 2, 5))           AS org_without_onboarding,
  count(*) FILTER (WHERE "Role" = 7)                                             AS role_none
FROM "Users";
```

Hosts who will need to accept the consents (onboarded or with an org, without the current Terms/Privacy/DPA) can be
listed by joining `Users` with `ConsentRecords` on `UserId` and `OrgId`; tell them before the deploy.

## Test users (Maestro, manual checks)

A test user with the `PropertyOwner` role in Auth0 is no longer enough: complete the web onboarding once with it
(rental type + consents) on the test environment, or its API calls answer `onboarding_required` and the app shows
the activation screen.

## Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| Every host gets `onboarding_required` after a deploy | A `Legal:Documents:*:Version` changed | Expected: hosts accept the new version. If the change was a mistake, restore the previous value |
| A host completed the onboarding on the web but the app still shows the activation screen | Old profile in the app | Tap "Ho completato, riprova" (the gate reads `/users/me` again); at most 60 s of server cache on another instance |
| An admin gets `onboarding_required` on plan or billing pages | Billing belongs to a host org | The admin completes the onboarding (the Admin role is kept) |
