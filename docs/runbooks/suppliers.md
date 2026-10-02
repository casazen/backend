# Runbook: supplier invites, self-serve registration, claim and service requests

Task SU-01 (audit defects A4-03, A4-04, A4-21, A4-24) and SU-02 (claim after login, supplier onboarding, no link by
unverified email: A4-02, A4-23, A1-13). Section 9: one profile per email and the admin repair `fix-orphaned` (SU-14,
A4-22). The code is in place; the product owner sets the pilot comuni (section 3),
checks the Auth0 claims (section 2.3) and the web app URLs (section 4) on each environment. Section 7: what a
service request is tied to (task SU-07, decision D2). Section 10: supplier jobs and QR check-in removed, dashboard
KPIs from the service requests (SU-11, decision D12). Section 11: what the supplier sees of a request (address, date,
host contact), request detail page and inbox history (SU-08, A4-14). Section 12: iCal calendar sync (SU-15). Section 14:
the platform admin's supplier list, suspension and invites (SU-12, A4-29).
Section 15: what the host sees of a request (timeline, rejection reason, "Segna pagato" with confirmation, asking another supplier)
and the payment notification to the supplier (SU-09, A4-28).

## 1. How a supplier joins

| Path | Steps | Code |
|---|---|---|
| **Admin invite** | Admin: *Invita fornitore* (`POST /api/admin/suppliers/invite`) → email with the link `{App__PublicSiteBaseUrl}/register?inviteToken=…` → the web app page reads the invite (`POST /api/suppliers/invites/lookup`) and shows email and comune, locked → **Crea account / Accedi** starts the Auth0 login of the web app (SDK: PKCE and `state`, `login_hint` = invited email) and comes back to the same page → the supplier enters business name and phone → `POST /api/suppliers/register` (signed in) → the account is linked to the new supplier org and gets the Auth0 role `Supplier` (additive) → **Completa il profilo** opens the activation wizard | `SuppliersController`, `SupplierService.RegisterAsync`, web `src/pages/supplier-register-page.tsx` |
| **Self-serve, signed in** | `/register` without token while signed in (also from the host onboarding: *Sono un fornitore*): email locked to the account, comune among the pilot comuni, business name, phone → linked at once, Auth0 role `Supplier` (additive) → activation wizard | same |
| **Self-serve, anonymous** (SU-02) | `/register` signed out → `POST /api/suppliers/register` answers a **claim token** (section 2) → *Crea il tuo account* / *Ho già un account* opens the Auth0 signup or login (email pre-filled) and comes back to `/register/claim` → `POST /api/suppliers/claim` links the account and assigns the `Supplier` role → *Completa il profilo* opens the activation wizard | `SuppliersController.Claim`, `SupplierService.ClaimAsync`, web `src/pages/supplier-claim-page.tsx`, `src/lib/supplier-claim.ts` |

The backend no longer serves an HTML `/register` page (it sent users to Auth0 with `response_type=code` without
`state`/PKCE and a `/callback` the web app does not have). Old invite emails pointing at the API domain now get 404.

### Invite token

- 32 random bytes (64 hex characters) created with the invite, sent **only** in the email. The database keeps its
  SHA-256 (`SupplierInviteRecords.TokenHash`); the invite id is an identifier, never the secret.
- Valid 7 days, **one use**. Bound to the invited email (the signed-in account and the form must both have it,
  case-insensitive) and to the invited comune; the invite's service categories are copied to the profile.
- Accepting requires a signed-in account: an anonymous request with a token gets `supplier_invite_login_required`.
- Concurrent acceptances of the same token are serialized (PostgreSQL advisory lock): one wins, the other gets
  `supplier_invite_used`.
- **Invites created before SU-01** (no `TokenHash`; their link used the invite id) can no longer be accepted and no
  longer block a new invite for the same email: send a new invite if one was pending.

### Error codes (422, ProblemDetails `code`)

| Code | Meaning |
|---|---|
| `supplier_invite_invalid` | Token malformed, truncated, a legacy id, or unknown (never a 500) |
| `supplier_invite_expired` / `supplier_invite_used` | Invite past its expiry / already accepted |
| `supplier_invite_login_required` | Token sent without a signed-in account |
| `supplier_invite_email_mismatch` | Signed-in account (or form) email is not the invited one |
| `supplier_invite_comune_mismatch` | Form comune differs from the invited comune |
| `supplier_account_email_missing` | Signed in, but the account email is unknown: no email claim in the access token (`https://casazen.app/email`, `email`; Auth0 Action, [`auth0.md`](auth0.md) §6) and the Management API (M2M client, §4-5) is not configured or has no email for the user |
| `supplier_account_email_mismatch` | Signed-in self-serve with an email that is not the account's |
| `supplier_self_serve_unavailable` | No pilot comune configured (section 3) |
| `supplier_comune_not_pilot` | Self-serve for a comune outside the pilot list |

`409 supplier_email_taken` when a supplier profile already has the email (trimmed, case-insensitive; SU-14, section
9): the owner of that profile links it with the claim (section 2), a second profile is never created. The admin invite
answers the same 409 for such an email. `429 rate_limited` when the client exceeds the limits of section 5.

## 2. Claim of an anonymous registration (SU-02)

### 2.1 Claim token

- Only an **anonymous** self-serve registration gets one (`claimToken`, `claimExpiresAt` in the 201 response): an
  invite needs a signed-in account and a signed-in registration is linked at once.
- 32 random bytes (64 hex characters), same format as the invite token. The database keeps only its SHA-256
  (`SupplierProfiles.ClaimTokenHash`, unique; migration `SupplierClaimToken`) and its expiry (`ClaimTokenExpiresAt`).
  Never logged.
- Valid **7 days** (`SupplierClaimTokens.Validity`, the same as an invite), usable by **one** account: the profile is
  "claimed" as soon as any account is linked to it. The hash is kept after the claim, so a reused token answers
  `supplier_claim_used`.
- The web app keeps it in `localStorage` (`cz-supplier-claim`: token, registered email, expiry) and also passes it in
  the Auth0 `appState` of the login, which stays in the browser. `localStorage` rather than `sessionStorage` because
  the email verification of a new Auth0 account often ends in another tab. It is removed when the claim succeeds,
  when the token is refused for good (invalid, expired, used, account already linked), when it expires, or with
  *Continua senza collegare*. The token alone grants nothing: it links only a signed-in account with the registered
  email.
- While a valid claim is stored and the account has no supplier link, the app guard opens `/register/claim` instead
  of the host onboarding.

### 2.2 `POST /api/suppliers/claim` (signed in, policy `Authenticated`)

Body `{ "claimToken": "…" }` (optional). Answer 200 `{ orgId, redirectUrl, rolesSynced, rolesSyncError }`.

| Case | Rule |
|---|---|
| With token | Token of that registration, not expired, profile not yet held by an account, and account email = registered email (case-insensitive). The email need not be verified yet: the token proves the registrant, and a new Auth0 account usually signs in before verifying |
| Without token | Only when Auth0 says the account email is **verified** (section 2.3): links the **one** supplier profile with that email that no account holds (a lost token, or a registration made before SU-02, which has no token). Several such profiles: 409 `supplier_claim_ambiguous`; since SU-14 an email has at most one profile, so this no longer happens |
| Already linked | A caller already linked to a supplier org gets that org back (idempotent), unless the token belongs to another profile (409 `supplier_account_already_linked`) |
| Role | Every successful call assigns the Auth0 role `Supplier` (additive, FD-14). `rolesSynced: false` does not undo the link: the console already works through the DB link; the page shows *Riprova ad applicare i permessi* (repeats the claim) and *Rinnova l'accesso e continua* (PL-01) |

Error codes (ProblemDetails `code`):

| Code | Status | Meaning |
|---|---|---|
| `supplier_claim_invalid` | 422 | Token malformed or unknown |
| `supplier_claim_expired` / `supplier_claim_used` | 422 | Token past its expiry / profile already held by an account |
| `supplier_claim_email_mismatch` | 422 | Signed-in account email is not the registered one: sign in with that email |
| `supplier_claim_email_unverified` | 422 | No token and the account email is not verified (or its status is unknown) |
| `supplier_claim_not_found` | 422 | No token and no unclaimed profile for the verified email |
| `supplier_account_email_missing` | 422 | The account email is unknown (no claim in the token, Management API not configured) |
| `supplier_claim_ambiguous` / `supplier_account_already_linked` | 409 | See the table above |

### 2.3 No link by email alone (A4-23, A1-13) and Auth0 requirements

**Decision:** the automatic link "same email → same supplier profile" is **removed**, not merely gated on
`email_verified`. `SupplierOrgContextResolver` / `SupplierService.GetOrProvisionSupplierOrgIdAsync` (every
`/api/supplier/*` request) now use only the account's own link (`User.SupplierOrgId`, or the legacy `User.OrgId` of a
supplier org, section 13); a Supplier role given by hand without any link still gets a new empty profile, never someone else's. A
pending admin invite is no longer consumed by an account that merely shows the invited email: it is accepted only with
its token. The only link that uses the email is the explicit claim without token, and only with a verified email.

Where `email_verified` comes from (checked in this order, `SuppliersController.ResolveAccountEmailAsync`):

1. The access token claim `https://casazen.app/email_verified` (or a standard `email_verified`), set by the Post Login
   Action of [`auth0.md`](auth0.md) §6. **Deploy that Action on every tenant**; it already writes the claim next to
   `https://casazen.app/email`.
2. Without the claim, the Management API (`GET /api/v2/users/{id}`, M2M client with `read:users`, auth0.md §4-5). Its
   flag counts only when Auth0 reports the same email as the token.
3. Neither: treated as **not verified** (`supplier_claim_email_unverified`). Claims with a token keep working.

The claim is copied into the token at login: a user who verifies the email after signing in needs a new token. The
claim page's *Riprova* renews the token (`getAccessTokenSilently({ cacheMode: 'off' })`) before retrying. Keep the
Auth0 database connection's verification email enabled (default).

### 2.4 Onboarding

- `GET /api/users/me` returns `supplierOrgId` (also for a legacy supplier-only user whose `orgId` is the supplier org;
  since PL-05 a supplier-only user has no `orgId`, section 13). The
  web guard never sends a linked supplier to the host onboarding, even before the `Supplier` role reaches the token;
  its home is the activation wizard (`/app/supplier/activation`, which forwards an active supplier to the dashboard).
- A host with a supplier profile keeps the host org (`orgId`) and the supplier org (`supplierOrgId`): the context
  switch (`GET /api/me/contexts`) offers both.
- The host onboarding shows *Sono un fornitore* to users without org, role or claim: *Registrati come fornitore* opens
  `/register` (signed-in self-serve, only for the pilot comuni; "solo su invito" when none is configured) and *Hai già
  registrato la tua attività? Collega il profilo* opens `/register/claim` (verified-email claim).

## 3. Pilot comuni (Railway variables, per environment)

The spec allows self-serve signup only "for pilot comune" (`Sessions/specs/spec-supplier-console-web.md` AC3) and
does not name them, so there is **no default**: while the list is empty self-serve is **off** and suppliers join only
by invite (the page says so). Admin invites are not limited to the pilot comuni.

| Variable | Meaning |
|---|---|
| `Suppliers__PilotComuni__0__Code` | Code of the first pilot comune, max 20 characters |
| `Suppliers__PilotComuni__0__Name` | Name shown to suppliers (and in invite emails for that code), max 100 characters |
| `Suppliers__PilotComuni__1__Code`, `…__1__Name`, … | Next comuni |

- Code and name are both required and codes must be unique (case-insensitive): otherwise the **startup fails** with
  the list of problems and Railway keeps the previous deployment.
- **The code is the ISTAT code of the comune** (6 digits, e.g. `058091`, SU-04). Once the official ISTAT list is imported
  ([comuni-istat.md](comuni-istat.md)) it is validated against it: the name shown to the supplier is the one of the
  list, a cadastral code (`H501`) is turned into the ISTAT code, and a code that is not an active comune is **not
  offered** (self-serve is off when none remains; `GET /api/admin/comuni` and the health check `comuni` name it). The
  registration form's code is compared with the pilot's as the same comune, and the supplier profile stores the pilot's
  ISTAT code in `ComuniJson` and in `ComuneIstatCodesJson`. Without the list the configuration is used as written.
- Matching with the hosts' properties is **by ISTAT code** (the property's chosen comune, the supplier's chosen comuni);
  what a supplier wrote as text (`H501`, `Roma`) is resolved against the list and only compared as a name when it cannot be
  (list not imported, ambiguous name). `ItalianComuneRegistry` is gone: it knew 12 comuni and got four codes wrong
  (Torino, Bellagio, Menaggio, Varenna) and mapped the cadastral code `F205` to Firenze while it is Milano (A4-12).
- Admin invites: with the list imported the invite's comune must be an active comune (ISTAT code, cadastral code or a
  unique name) and is stored as its ISTAT code (422 `comune_istat_unknown`); invite emails show "Name (code)".
- The staging golden journey (`frontend/e2e/golden-journey-*.spec.ts`) self-registers suppliers with comune
  `058091`: on the test environment either configure that code as a pilot comune or expect `supplier_self_serve_unavailable`.

Check: `curl -s "$RAILWAY_TEST_URL/api/suppliers/registration-options"` returns `selfServeEnabled` and the list.

## 4. URLs

| Setting | Value |
|---|---|
| `App__PublicSiteBaseUrl` (Railway) | Web app URL of the environment: base of the invite link (`/register?inviteToken=…`), no fallback domain ([`email.md`](email.md)) |
| Auth0 SPA application → Allowed Callback URLs, Allowed Web Origins, Allowed Logout URLs | The web app origin(s) of the environment (unchanged: the SDK returns to the origin and the app then navigates back to the invite or claim page from `appState`). No `/callback` URL is needed; remove the API domain if it was added for the old backend page |

## 5. Rate limits (FD-10, per client IP, [`proxy-ip.md`](proxy-ip.md))

| Endpoint | Policy | Default |
|---|---|---|
| `POST /api/suppliers/register` | `PublicRegistration` | 5 / 10 min |
| `POST /api/suppliers/invites/lookup`, `GET /api/suppliers/registration-options` | `PublicRead` | 120 / min |

The invite page calls `lookup` once per load and `register` once per submit: an invited supplier who logs in and
submits stays far below the limits. `POST /api/suppliers/claim` requires a signed-in account and a 256-bit token: it has
no public rate limit.

## 6. After a deploy

- [ ] Section 3 variables set (or self-serve intentionally off); `registration-options` answers as expected.
- [ ] Auth0 Post Login Action deployed with `https://casazen.app/email_verified` (auth0.md §6).
- [ ] Signed out, self-serve in a pilot comune → *Crea il tuo account* → Auth0 signup with the same email → back on
      *Collega il tuo profilo fornitore* → *Profilo collegato* → *Completa il profilo* opens the activation wizard;
      `GET /api/users/me` has `supplierOrgId`. Reloading `/register/claim` answers the same org (idempotent).
- [ ] Same flow, signing up with **another** email: "registrato con un'altra email", nothing linked.
- [ ] A new account without org: the host onboarding shows *Sono un fornitore*; it opens `/register`.
- [ ] Admin invite to a mailbox you control: the email links to the **web app** `/register?inviteToken=…`, shows the
      comune (name when configured), the expiry in Italian time and no promise of automatic activation.
- [ ] Open the link signed out: invite email and comune shown and locked; *Crea account* opens Auth0 signup with the
      email pre-filled and comes back to the invite page; submit → *Completa il profilo* opens the activation wizard.
- [ ] Open the same link again: "invito già usato". Open it truncated: "invito non valido".
- [ ] Signed in with another account on a fresh invite: the page asks to switch account; the API answers
      `supplier_invite_email_mismatch`.

## 7. Service requests: per stay (short-rent) or per property (long-rent) — SU-07

Decision D2 (`Sessions/risanamento/DECISIONI.md`), audit A4-13 / A4-33, issue #340. Before SU-07 the web created
requests without a booking and listed a booking's requests by property, while the app listed them by booking: a
request created on the web never showed in the app.

### 7.1 Rules

| Context | Tied to | API (host side) | Rule (422, ProblemDetails `code`) |
|---|---|---|---|
| **Short-rent** (`RentalContext = ShortRent`, 0) | One stay: `BookingId` + its `PropertyId` | `api/service-requests` (policies `property.read` / `property.write` in short-rent) | `bookingId` missing → `service_request_booking_required`; a booking that is not of that property and org (or does not exist) → `service_request_booking_mismatch` |
| **Long-rent** (`RentalContext = LongRent`, 1) | The property only (`BookingId` null) | `api/long-rent/service-requests` (policies `property.read` / `property.write` **in long-rent**) | a `bookingId` → `service_request_booking_not_allowed` |

- The context is stored on the request (`ServiceRequests.RentalContext`) and is never inferred from `BookingId`.
- The host endpoints of one context never reach the other context's requests: a short-rent `GET`/`mark-paid` of a
  long-rent request is 404, and vice versa; a user with only the long-rent context gets 403 on every
  `api/service-requests` host endpoint (LT-05: long-rent shares only the property core).
- The supplier side (inbox, `take`, `complete`, `reject` on `api/service-requests`) is the same for both contexts.
- `bookingId` / `propertyId` of another org: 404 (`booking_not_found`, `property_not_found`), on lists too.

### 7.2 Same queries on web and app

| Where | Query |
|---|---|
| Web booking detail, app booking screen | `GET /api/service-requests?bookingId={id}&pageSize=50` |
| Web property detail (short-rent), overview with a link to each stay | `GET /api/service-requests?propertyId={id}` |
| Web marketplace, "Le tue richieste" | `GET /api/service-requests` (every short-rent request in scope) |
| Web long-rent property detail | `GET /api/long-rent/service-requests?propertyId={id}` |

Creating: web booking detail and app booking screen send the booking (`bookingId`) and its property; the web
marketplace asks which stay of the property (current and upcoming first, cancelled ones never offered); the long-rent
property page sends `POST /api/long-rent/service-requests` with the property only. Long-rent endpoints:

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/long-rent/service-requests?propertyId=` | long-rent `property.read` | the long-rent requests in scope (owner or org-wide role) |
| `GET /api/long-rent/service-requests/suppliers?propertyId=&category=` | long-rent `property.read` | active suppliers of the property's comune (same answer as `GET /api/suppliers?propertyId=`) |
| `POST /api/long-rent/service-requests` | long-rent `property.write` | body as the short-rent one, without `bookingId` |
| `POST /api/long-rent/service-requests/{id}/mark-paid` | long-rent `property.write` | completed long-rent request only |

### 7.3 Existing requests (migration `AddServiceRequestRentalContext`)

- Every existing request becomes short-rent: before SU-07 requests were created only in the short-rent context.
- Short-rent requests **without** `BookingId` are tied to a stay **only when it is unique**: the request's creation day
  (Europe/Rome) falls within exactly one non-cancelled booking of the same property and org (check-in and check-out
  days included). Requests created between stays, on a turnover day shared by two stays, or on a property without
  stays are **left on their property** (`BookingId` null): they show in the property overview ("Senza soggiorno
  (richiesta precedente)") and in no booking, on the web and in the app. Nothing is deleted, `UpdatedAt` is unchanged.
- The migration logs `AddServiceRequestRentalContext: N short-rent requests tied to their stay, M left on their
  property` (`RAISE NOTICE`). To see the ones left after the deploy:

  ```sql
  SELECT "Id", "OrgId", "PropertyId", "CreatedAt" FROM "ServiceRequests"
  WHERE "RentalContext" = 0 AND "BookingId" IS NULL ORDER BY "CreatedAt";
  ```

  They stay valid as property-level requests; there is no manual fix to apply (and the rule "no manual DB
  workarounds" applies: do not guess their stay by hand).

### 7.4 After a deploy

- [ ] Migration log shows the NOTICE above with plausible counts.
- [ ] Web: from a booking detail, *Richiedi fornitore* → choose supplier → the request appears under the booking;
      open the same booking in the app → same request.
- [ ] App: *Richiedi fornitore* from a booking → the request appears in the web booking detail and in the property
      overview with *Vai al soggiorno*.
- [ ] Web marketplace: the dialog asks the stay; without one *Invia richiesta* stays disabled.
- [ ] Long-rent: from `/app/long-rent/properties/{id}`, *Richiedi fornitore* → the request is listed there and not in
      the short-rent property overview; the supplier sees it in the inbox.

## 8. Service requests: validation, errors and concurrent transitions — SU-10

Audit A4-17 / A4-18 / A4-19. Every error of `api/service-requests` and `api/long-rent/service-requests` has the FD-05
shape (`code` + localized `detail`, IT default, EN with `Accept-Language: en`); none of the audit scenarios answers 500.

### 8.1 States and errors

Allowed transitions (`Casazen.Core/Suppliers/ServiceRequestStateMachine.cs`, the only table):
`Richiesto → PresoInCarico` (take), `Richiesto → Rifiutato` (reject), `PresoInCarico/InCorso → Completato`
(complete), `Completato → Pagato` (mark-paid). `Rifiutato` and `Pagato` are final.

| Status | `code` | When |
|---|---|---|
| 400 | `validation_error` | body: `propertyId` / `supplierOrgId` / `bookingId` equal to `00000000-…`, no `category`, `notes` over 1000 characters (create and complete), reject without `reason` (`{}`, `null`, blank) or over 500 characters. Field errors in `errors` |
| 404 | `service_request_not_found` | the request does not exist or is outside the caller's scope (also `mark-paid`, short- and long-rent) |
| 404 | `property_not_found` / `supplier_not_found` | create: property not in the org, supplier without profile |
| 403 | `forbidden` | a supplier acts on a request sent to another supplier |
| 422 | `invalid_service_category` | category not a code of `GET /api/service-categories` (SU-03) |
| 422 | `service_request_supplier_inactive`, `service_request_supplier_outside_comune`, `service_request_charge_to_guest_not_allowed` | create rules (before SU-10: 409 / 400 with free text) |
| 422 | `service_request_invalid_transition` | the transition is not allowed from the current status (e.g. mark-paid before complete, reject after take; before SU-10: 409) |
| 409 | `service_request_state_changed` | another operation changed the request between read and save (below) |

The check-out wizard still turns a supplier refused at check-out (missing, not active, outside the comune) into its
own 422 `checkout_service_request_invalid`.

### 8.2 Concurrent transitions (`xmin`)

`ServiceRequest.Version` is mapped by Npgsql to PostgreSQL's `xmin` system column (optimistic concurrency token, no
column is added: the migration `AddServiceRequestConcurrencyToken` only updates the EF model). Every transition is an
`UPDATE … WHERE "Id" = @id AND xmin = @read`: when two members of the supplier org (or two tabs) click *Presa in
carico* and *Rifiuta* together, one save wins, the other updates no row and answers 409
`service_request_state_changed`. The host email and the push are queued **after** the save, so only the winning
transition notifies the host. A second click after the first one completed is a 422 `service_request_invalid_transition`
(the request is no longer `Richiesto`). Clients reload the request on either code.

Raw SQL that updates `ServiceRequests` (manual fixes are not allowed anyway) changes `xmin` too: a user who had the
request open simply gets the 409 and reloads.

### 8.3 After a deploy

- [ ] The migration `AddServiceRequestConcurrencyToken` is listed in `__EFMigrationsHistory` (no DDL is run).
- [ ] Supplier console: reject a new request without a reason → the form shows *Indica il motivo del rifiuto.*
      (400), the request stays new.
- [ ] Host: *Segna pagato* on a request that is not completed → 422 with *Solo le richieste completate possono essere
      segnate come pagate.*

## 9. One profile per email and the admin repair `fix-orphaned` — SU-14

Audit A4-22. Before SU-14 supplier emails were not unique (the old auto-provisioning and repeated anonymous
registrations created duplicates), and `fix-orphaned` deleted the "duplicate" orgs without looking at their service
requests (FK `Restrict`, so the endpoint answered 500 and repaired nothing) nor at the accounts linked to them, which
kept pointing to deleted orgs. It also linked accounts to profiles by email (A4-23).

### 9.1 One profile per email

- Unique index `UIX_SupplierProfiles_NormalizedEmail` on `lower(btrim("Email"))` of `SupplierProfiles`, blank emails
  excluded (they are not an identity). Stricter than `lower("Email")`: also `" a@x.it"` and `"A@X.IT"` collide. Raw
  SQL in the migration (EF cannot model an expression index), so it is not in the EF model
  (`Casazen.Infrastructure/Data/SupplierProfileEmailIndex.cs`).
- Every path that creates a profile answers **409 `supplier_email_taken`** instead of a duplicate: registration (with
  or without invite, anonymous or signed in), admin invite (it could never be accepted), and the auto-provisioning of a
  Supplier role given by hand to an account without a link (`/api/supplier/*`: the account links the existing profile
  with the claim, section 2). A parallel registration that passes the check hits the index (23505) and gets the same
  409, never a 500.
- The anonymous registration answer reveals that a supplier profile exists for an email (required to tell the
  supplier what to do); it is rate limited like every public registration (section 5).

### 9.2 Migration `SupplierProfileEmailUnique` (applied at startup)

Migrations run when the app starts (`Program.cs`): a migration that simply failed on existing duplicates would stop
the very deployment that ships the safe repair, and the previous deployment's `fix-orphaned` is the broken one. So the
migration **first merges the duplicates with the rules of section 9.3** (same keeper, same moves; SQL in the migration
class, kept in step with `SupplierService.Maintenance.cs`), then creates the index. It writes `RAISE NOTICE` lines with
the merged org ids and the counts (no email): check them in the Railway deploy log.

It **fails on purpose** when a duplicate group needs a decision (codes of section 9.4: `supplier_duplicate_several_accounts`,
`supplier_duplicate_suspended`). The error lists the org ids, e.g.
`SupplierProfileEmailUnique: 1 supplier email group(s) need a manual decision and were not merged: [<org>, <org>] supplier_duplicate_several_accounts`.
The migration runs in one transaction: **nothing is changed**, the index is not created, Railway keeps the previous
deployment. That case needs a product decision (which account keeps the supplier) and a follow-up release that applies
it: open an issue with the log line; do not edit the database by hand.

Before promoting to production you can see whether that can happen, read-only:

```sql
SELECT array_agg(DISTINCT sp."OrgId") AS orgs,
       count(DISTINCT u."Id") AS accounts,
       count(DISTINCT sp."OrgId") FILTER (WHERE u."Id" IS NOT NULL) AS held_profiles,
       bool_or(sp."Status" = 2) AS any_suspended
FROM "SupplierProfiles" sp
LEFT JOIN "Users" u ON u."SupplierOrgId" = sp."OrgId" OR u."OrgId" = sp."OrgId"
WHERE btrim(sp."Email") <> ''
GROUP BY lower(btrim(sp."Email"))
HAVING count(DISTINCT sp."OrgId") > 1;
```

No rows: nothing to merge. Rows with `held_profiles > 1 AND accounts > 1`, or `any_suspended`: the migration will stop.
Other rows are merged automatically. Down drops the index only (merged profiles are not split again).

### 9.3 `POST /api/admin/suppliers/fix-orphaned` (policy `AdminOnly`)

**Dry run by default**: without `?dryRun=false` every step runs inside a transaction that is rolled back, and the
report shows what would change. Apply with `?dryRun=false`. One run at a time (PostgreSQL advisory lock
`SupplierMaintenance`); a claim of a profile being merged waits for the run (per-profile claim lock) and then answers
`supplier_claim_invalid`/`supplier_claim_not_found` instead of linking an account to a deleted org. Idempotent. After
the migration there are no duplicate emails left, so a run normally only does the link repairs below.

1. **Duplicates** (same email, trimmed, case-insensitive, blank excluded) are merged into one **keeper**: the
   `Active` profile, then the one an account holds (`User.SupplierOrgId` or `User.OrgId`), then the oldest. For each
   duplicate, in one savepoint per group:
   - service requests (`ServiceRequests.SupplierOrgId`, any state) move to the keeper (the legacy supplier jobs were
     removed by SU-11, section 10);
   - availability days move; a day the keeper already has keeps the keeper's value (the duplicate's is dropped);
   - the duplicate's categories and comuni the keeper lacks are appended (bio, photos, VAT, calendar settings of the
     duplicate are not copied: the keeper's profile is the one in use);
   - accounts: `SupplierOrgId` = duplicate → keeper; a supplier-only account (`OrgId` = duplicate, no `SupplierOrgId`)
     gets `SupplierOrgId` = keeper;
   - the duplicate profile is deleted; its org too, after detaching the legacy `User.OrgId` links to it (`OrgId` = null,
     section 13) and moving push devices to the keeper, **unless
     the org also holds host data** (not a supplier org, consents, signup attribution, or rows such as properties or
     bookings that reference it): then the org stays, without its supplier profile, and its accounts keep it as `orgId`.
   - the duplicate's claim token dies with it: its registrant links the keeper with the verified-email claim.
2. **Links**: accounts whose `SupplierOrgId` points to an org that no longer exists are unlinked
   (`danglingLinksCleared`); accounts whose `OrgId` is a supplier org with a profile get the same `SupplierOrgId`
   (`supplierLinksBackfilled`, their own link).
3. **Profiles no account holds**: never linked by email, which the repair cannot prove (no claim token, no Auth0
   `email_verified`). Listed in `orphanProfiles`, or as `supplier_link_requires_claim` when accounts with the same
   email exist: the supplier links the profile from *Collega il profilo* (section 2).

Response (200): `dryRun`, `profilesScanned`, `duplicateGroups`, `duplicatesMerged`, `serviceRequestsMoved`, `merges[]`
(`keeperOrgId`, `duplicateOrgId`, `serviceRequestsMoved`, `availabilityDaysMoved`,
`availabilityDaysDropped`, `categoriesAdded`, `comuniAdded`, `supplierLinksMoved`, `orgMembersMoved`, `devicesMoved`,
`duplicateOrgDeleted`), `danglingLinksCleared[]` and `supplierLinksBackfilled[]` (user ids), `orphanProfiles[]`,
`manualInterventions[]` (`code`, `orgIds`, `userIds`). Ids and counts only: no email, no name.

Errors: 409 `supplier_maintenance_conflict` when a concurrent change stops the run (nothing saved: run it again); 403
for non-admins. A conflict inside one duplicate group only skips that group (`supplier_duplicate_merge_conflict`).

Audit log: event `SupplierRepair` (id 4140), one line per merge, cleared or backfilled link and manual case (org ids,
user ids, counts; warning level when applied, information in a dry run), and a summary line per run. No PII.

### 9.4 Manual interventions (`manualInterventions[].code`)

| Code | Meaning | What to do |
|---|---|---|
| `supplier_duplicate_several_accounts` | Profiles of one email held by different accounts: merging would show each account the other's requests, and neither proved the email | Product decision: which account keeps the supplier (contact the supplier). Not merged |
| `supplier_duplicate_suspended` | A profile of the email is suspended: a merge could lift the suspension | Decide after the suspension review. Not merged |
| `supplier_duplicate_merge_conflict` | A concurrent change or a constraint stopped the merge of the group | Run the repair again |
| `supplier_link_requires_claim` | A profile no account holds, and accounts with its email exist | Nothing to do in the admin: the supplier links it with *Collega il profilo* (verified email) |

### 9.5 After a deploy

- [ ] Deploy log: the `SupplierProfileEmailUnique` NOTICE lines (merged org ids, counts), and the index exists:
      `SELECT indexdef FROM pg_indexes WHERE indexname = 'UIX_SupplierProfiles_NormalizedEmail';` (read-only).
- [ ] `POST /api/admin/suppliers/fix-orphaned` (dry run): `duplicateGroups` is 0; review `danglingLinksCleared`,
      `manualInterventions`. Then `?dryRun=false` to apply the link repairs.
- [ ] Self-serve registration with the email of an existing supplier (another case): 409 with *Esiste già un profilo
      fornitore con questa email…*; no second profile.

## 10. Supplier jobs removed; dashboard KPIs from the service requests — SU-11

Audit defect A4-15, decision D12: `SupplierJob` (with QR check-in and a price) had no creation point on the host side
(`POST /api/supplier/jobs` assigned the job to the calling supplier itself), yet the supplier dashboard counted it. A
supplier with five completed service requests read "0 completati". Supplier work is now a `ServiceRequest` only.

### 10.1 What is gone

| Removed | Now |
|---|---|
| `GET/POST /api/supplier/jobs`, `POST /api/supplier/jobs/{id}/accept\|check-in\|check-out` (`SupplierJobController`) | 404 |
| `GET /api/public/check-in/{jobId}`, `POST …/check-in`, `POST …/check-out` (`PublicCheckInController`, anonymous, rate limit `PublicSupplierCheckIn`) | 404; the policy and `RateLimiting__PublicSupplierCheckIn__*` are gone (a leftover Railway variable is ignored) |
| Web page `/check-in/:jobId` (`supplier-check-in.tsx`) | route removed: the SPA shows its not-found page; old QR codes lead nowhere |
| Entity `SupplierJob`, enum `SupplierJobStatus`, `QrCodeService`, table `SupplierJobs` | migration `RemoveSupplierJobs` drops the table (Down recreates it **empty**) |
| `supplierJobsMoved` in the `fix-orphaned` report (section 9.3) | removed: only the service requests are moved |
| `totalJobs`, `completedJobs`, `upcomingJobs` in `GET /api/supplier/dashboard` | replaced by `GET /api/supplier/dashboard/kpis` (10.2) |

The guest online check-in (`/api/public/checkin/{token}`, web `/checkin/:token`) is another feature and is unchanged.

### 10.2 `GET /api/supplier/dashboard/kpis?period=` (policy `RequireSupplier`)

Counts the service requests whose `SupplierOrgId` is the caller's supplier org (never another supplier's, whatever the
host org). `period`: `CurrentMonth` (default), `PreviousMonth`, `Last30Days`, `CurrentYear`; any other value is 400
`validation_error`. Dates are Europe/Rome calendar days: "today" comes from `TimeProvider` (`RomeCalendar`), the period
is `[00:00 Rome of from, 00:00 Rome of the day after to)`.

| Field | Meaning |
|---|---|
| `period`, `from`, `to`, `timeZone` | the resolved period (`YYYY-MM-DD`, both ends included) and `Europe/Rome` |
| `completed` | `Completato` or `Pagato` with `CompletedAt` in the period (a paid request counts on its completion date) |
| `rejected` | `Rifiutato` with the rejection in the period (`UpdatedAt`: `Rifiutato` is a final state, nothing updates it later) |
| `awaitingAcceptance` | `Richiesto` now, whatever the period |
| `upcoming` | `PresoInCarico` or `InCorso` now (taken, not completed), whatever the period |
| `totalRequests` | every request ever assigned to the supplier org: 0 shows the web dashboard's empty state |

A request completed before `CompletedAt` existed (none expected: the field dates from the first service request
migration) has no completion date and is never counted as completed in a period.

### 10.3 Before the deploy: rows in `SupplierJobs`

No web, app or host flow ever created a supplier job: rows can only come from direct calls of the removed
`POST /api/supplier/jobs` (tests, demos). The migration drops the table **with its rows** at startup. Check each
environment first (read-only):

```sql
SELECT count(*) AS jobs, min("CreatedAt") AS first, max("CreatedAt") AS last FROM "SupplierJobs";
```

If the count is not 0 and the rows matter, export them before deploying, e.g. with `psql`:

```sql
\copy (SELECT * FROM "SupplierJobs") TO 'supplier_jobs_backup.csv' WITH CSV HEADER
```

A rollback of the migration (`dotnet ef database update <previous migration>`) recreates an **empty** table.

### 10.4 After a deploy

- [ ] `SELECT to_regclass('"SupplierJobs"');` returns null.
- [ ] As a supplier with completed requests: the web dashboard shows the completed count of the month, the requests
      waiting to be taken and the upcoming ones; changing the period reloads the counts.
- [ ] `GET /api/public/check-in/<any id>` answers 404.

## 11. Supplier console: address, date, host contact, request detail and history — SU-08

Audit A4-14: the supplier received only property name, category, urgency and notes, with no address, date, host
contact or stay, and no detail page: it had to phone the host. The supplier console now reads its own endpoints
(policy `RequireSupplier`, scoped by the caller's supplier org), built by `SupplierServiceRequestReader` from a
column-by-column projection: the guest of the stay is never joined nor loaded.

### 11.1 What the supplier sees, before and after the take

The rule is `Casazen.Core/Suppliers/SupplierJobDisclosure.cs` (GDPR data minimization: before accepting, the supplier
needs to know where roughly and when; the exact place and the host's contact only to do the job).

| Field (JSON) | New (`Richiesto`) | Taken, in progress, completed, paid | Rejected (`Rifiutato`) |
|---|---|---|---|
| `propertyName`, `category`, `urgency`, `notes` (host's notes), dates of the request | yes | yes | yes |
| `city` (comune, as the host wrote it), `postalCode` (the zone) | yes | yes | yes |
| `scheduledFor`: day of the job, `YYYY-MM-DD` Europe/Rome | yes | yes | yes |
| `stay`: `bookingId`, `checkIn`, `checkOut` (Europe/Rome days) | yes | yes | yes |
| `address` (street address) | **no** (null) | yes | **no** |
| `hostContact`: `name` (org display name), `email` (org contact email), `phone` (phone of the property owner's profile, when filled in) | **no** (null) | yes | **no** |
| `contactDisclosed` | `false` | `true` | `false` |
| Guest of the stay (name, email, phone, document, address, special requests) | **never** | **never** | **never** |

- `scheduledFor` of a short-rent request is the **check-out day** of its stay (the turnover). A request has no date of
  its own: long-rent requests (per property) and older short-rent requests not traced to a stay (section 7.3) have
  `scheduledFor` and `stay` null: the page shows "da concordare con l'host".
- The host's notes are free text written by the host: the supplier sees them as written.
- A rejected request never shows address and contact: it can only be rejected before the take (section 8.1).
- The old `GET /api/service-requests/{id}` and `?view=supplier` still answer the supplier with the host-shaped DTO
  (no address, no contact, no guest data); the web console does not use them.

### 11.2 Endpoints

| Endpoint | Notes |
|---|---|
| `GET /api/supplier/inbox?status=&from=&to=&page=&pageSize=` | `status`: `open` (default: `Richiesto`, `PresoInCarico`, `InCorso`), `history` (`Completato`, `Pagato`, `Rifiutato`), `all`, or one status name (any case). `from`/`to`: Europe/Rome days `YYYY-MM-DD`, both included, each optional. `pageSize` 1–100 (default 20). Answer `{ items, total, page, pageSize }`, newest activity first. 400 `validation_error` for another status (`SupplierInboxStatusInvalid`) or `from` after `to` (`SupplierInboxPeriodInvalid`). |
| `GET /api/supplier/inbox/{id}` | the request with `history`; 404 `service_request_not_found` when it does not exist **or was sent to another supplier** (same answer). |
| `POST /api/service-requests/{id}/take` \| `complete` \| `reject` | unchanged (section 8): the page calls them and reloads the request; on 409/422 it reloads too. |

**Activity date** (period filter and order of the inbox): completion date for `Completato`/`Pagato` (the date the
dashboard KPIs count them on, section 10.2), rejection date for `Rifiutato`, date received for the open ones. The
period is `[00:00 Rome of from, 00:00 Rome of the day after to)`: a job completed at 00:30 of 1 September in Rome
(22:30 UTC of 31 August) is in September.

### 11.3 History of a request

`history` lists the transitions of the state machine (section 8.1), oldest first, each with `status`, `at` (UTC
instant, shown in Europe/Rome) and `actor` (`Host` or `Supplier`). It is rebuilt from the dates the request keeps
(`Casazen.Core/Suppliers/ServiceRequestHistory.cs`), no table is added:

| Step | Date | Actor |
|---|---|---|
| `Richiesto` | `CreatedAt` | `Host` |
| `PresoInCarico` | `TakenAt` | `Supplier`, with `actorName` = first and last name of the member who took it (only a user of the same supplier org is named) |
| `Completato` | `CompletedAt` | `Supplier` |
| `Pagato` | `PaidAt` | `Host` |
| `Rifiutato` | `UpdatedAt` (final status, nothing updates it later) | `Supplier`, with `reason` |

Host members are never named to the supplier; the host is identified by the host contact. Completion and rejection
record no member (the request keeps no user for them): the page shows "Il tuo team".

### 11.4 Web console

- `/app/supplier/inbox`: tabs **Da fare** (open requests with *Presa in carico*, *Rifiuta* with reason, *Completa*) and
  **Storico** (status filter, *Dal*/*Al* dates, pagination from the server). Every card shows comune and zone, day of
  the job and opens the detail. Loading, error with *Riprova* and empty states per tab.
- `/app/supplier/inbox/:id`: where (comune, zone, address after the take), when (job day, stay dates), host contact
  after the take (`tel:` and `mailto:` links), host notes, history with date (Europe/Rome) and actor, actions of the
  status in a bar kept at the bottom of the screen on a phone. A request of another supplier shows "Incarico non
  trovato". Texts in `supplier.inbox.*` and `supplier.request.*` (IT/EN).

### 11.5 After a deploy

- [ ] As a supplier with a new request: the detail shows comune, CAP, the check-out day and the stay dates, and says
      that address and host contact come after the take; no guest name anywhere.
- [ ] *Presa in carico* → the page shows the street address, the host's name, email and (if the owner filled in the
      phone in the profile) the phone; the history shows the take with the member's name.
- [ ] *Storico*: filter *Rifiutato* and a month → only the requests rejected in that month; pages of 20.
- [ ] Opening `/app/supplier/inbox/<id of another supplier's request>` shows "Incarico non trovato".

## 12. Calendar sync (iCal) — SU-15

The supplier's iCal feed (activation wizard and *Sincronizza calendario*) is synced in Hangfire, never inside the
request: saving the URL answers 202 `Syncing` and queues the first sync, "Sincronizza ora" queues another one, the
15-minute job `ical-supplier-sync` also covers suppliers still in activation (`Pending`). The sync frees only the days
of the feed (`SupplierAvailability.Source`), never those the supplier set by hand. API, rules, migration of the
existing days and checks: [ical.md](ical.md#supplier-calendars-su-15).

## 13. The Supplier org never becomes the host org — PL-05 (A1-40)

Before PL-05 the supplier registration, claim and auto-provisioning also wrote the new supplier org into `User.OrgId`
when it was empty, and the **host** onboarding then reused that `OrgId` without checking its type: a supplier who
became a host got properties, bookings, plan, consents and public site on an `OrgType.Supplier` org.

**Rule now: `User.OrgId` is the user's host org only; the supplier link is `User.SupplierOrgId` only.**

- `SupplierService` (register, claim, auto-provisioning) writes only `SupplierOrgId`; `fix-orphaned` detaches legacy
  supplier-only accounts from a deleted duplicate (`OrgId` = null) instead of moving them to the keeper (section 9.3).
- The host resolvers accept only an org with `OrgType = Host`, so a legacy or wrong `OrgId` is treated as "no host org
  yet" (fail-closed, like a brand-new user, PL-02/A1-05):
  - `TenantContext.ResolveAsync`: the EF tenant filter, read once per request before every controller;
  - `OrgContextResolver.GetOrProvisionOrgIdAsync` (host controllers);
  - `UserAuthorizationSnapshotStore` (host onboarding gate: the consents count only for a host org);
  - `OrgService.EnsureOrgForUserAsync` (`POST`/`PUT /api/users/onboarding`): a non-host `OrgId` is never reused; a
    **new** Host org is created, and a legacy supplier link in `OrgId` is first copied to `SupplierOrgId`.
- `POST /api/devices` of a supplier-only account (no host org) registers the device under its linked supplier org, as
  before, so the supplier keeps receiving the push notifications of its requests.
- A user who is host and supplier sees the host data through `OrgId` (tenant filter) and the supplier data through
  `SupplierOrgId` (`/api/supplier/*`, explicit `SupplierOrgId` predicates): two orgs, never one.

### 13.1 Migration `SeparateSupplierOrgFromHostOrgId` (applied at startup, one transaction)

It repairs the existing rows, for every org with `OrgType = 1` (Supplier):

| Case | What happens |
|---|---|
| Account with `OrgId` = a supplier org with a profile and no `SupplierOrgId` (pre-SU-08) | `SupplierOrgId` = that org first (count `supplier_links_backfilled`) |
| Supplier org **without host data** (the common case: a supplier-only account) | Its accounts get `OrgId` = null and keep `SupplierOrgId` (`host_links_cleared`). An account whose `SupplierOrgId` is **another** org keeps the old `OrgId` (it would otherwise leave this profile held by nobody); no host resolver reads it (`supplier_org_links_kept`) |
| Supplier org **with host data** (A1-40 already happened: a supplier who became a host before PL-05) | The org becomes `OrgType = Host` and keeps everything host: properties, bookings, guests, payments, consents, slug and aliases, plan, Stripe customer and subscription, Connect account, public site and domain, the accounts' `OrgId` (`orgs_reclassified_host`). Its **supplier side moves to a new supplier org** (`supplier_sides_split`): supplier profile (with its public showcase slug, so `/s/<slug>` links keep working), availability days, `ServiceRequests.SupplierOrgId`, `StayCheckouts.CleaningSupplierOrgId`, `Users.SupplierOrgId`. The new org gets the profile's legal name and email, plan Starter, slug `supplier-<random>`. Nothing is deleted |

"Host data" = a Stripe customer or subscription, a subscription status, a paid tier, a Connect account, a custom domain
or subdomain on the org, or a row of **any** table whose foreign key references `Orgs` (read from the PostgreSQL
catalog, so a host table added later counts too), except the supplier and identity links (`SupplierProfiles.OrgId`,
`ServiceRequests.SupplierOrgId`, `Users.OrgId`, `DeviceRegistrations.OrgId`).

The migration takes a write lock on `Users`, `Orgs` and `SupplierProfiles` for its duration (seconds), so an old
instance still running cannot link accounts meanwhile. It is idempotent (a second run finds nothing to do). Down is a
no-op: restoring the old links would bring the bug back. Log (no email, no name):
`SeparateSupplierOrgFromHostOrgId: supplier_links_backfilled=…, host_links_cleared=…, supplier_org_links_kept=…,
orgs_reclassified_host=…, supplier_sides_split=…, split_without_holder=…`, then the split pairs
`<host org id> -> <new supplier org id>`.

### 13.2 Before the deploy (read-only, on the environment's schema)

```sql
-- Accounts whose OrgId is a supplier org, with the host data that decides the case.
SELECT u."Id" AS user_id, o."Id" AS org_id, u."SupplierOrgId" = o."Id" AS supplier_link_is_this_org,
       EXISTS (SELECT 1 FROM "SupplierProfiles" sp WHERE sp."OrgId" = o."Id") AS has_profile,
       (SELECT count(*) FROM "Properties" p WHERE p."OrgId" = o."Id") AS properties,
       (SELECT count(*) FROM "Bookings" b WHERE b."OrgId" = o."Id") AS bookings,
       (SELECT count(*) FROM "ConsentRecords" c WHERE c."OrgId" = o."Id") AS host_consents,
       o."StripeCustomerId" IS NOT NULL AS stripe_customer, o."PlanTier"
FROM "Users" u JOIN "Orgs" o ON o."Id" = u."OrgId"
WHERE o."OrgType" = 1;
```

Rows with properties, bookings, host consents or a Stripe customer are the orgs that will be **split**: note their ids
and, if a supplier of them has push or web sessions open, nothing to do (the next request reads the new links).
In-flight Hangfire pushes addressed to the old supplier org id of a split org reach nobody (the supplier is now on the
new org): at most one notification lost per open request.

### 13.3 After the deploy

- [ ] Deploy log: the `SeparateSupplierOrgFromHostOrgId` NOTICE lines. `split_without_holder` should be 0 (a split
      supplier profile that no account holds: run `fix-orphaned` in dry run, section 9.3, it lists it in
      `orphanProfiles`).
- [ ] Both must be 0:
      ```sql
      -- No account has a host data org typed Supplier, no supplier org holds a host row of the main tables.
      SELECT count(*) FROM "Users" u JOIN "Orgs" o ON o."Id" = u."OrgId"
       WHERE o."OrgType" = 1 AND u."SupplierOrgId" = o."Id";
      SELECT count(*) FROM "Orgs" o WHERE o."OrgType" = 1
         AND (EXISTS (SELECT 1 FROM "Properties" p WHERE p."OrgId" = o."Id")
              OR EXISTS (SELECT 1 FROM "Bookings" b WHERE b."OrgId" = o."Id")
              OR EXISTS (SELECT 1 FROM "ConsentRecords" c WHERE c."OrgId" = o."Id"));
      ```
- [ ] For each split pair of the log: the host still sees properties and bookings (`/app/...`), and the supplier
      console (`/app/supplier/inbox`, dashboard, calendar) still shows its requests and availability.
- [ ] Register as a supplier (self-serve or invite), then complete the host onboarding with the same account:
      `GET /api/users/me` has an `orgId` different from `supplierOrgId`, a property created then is in the host org,
      and the supplier console still works.

## 14. Admin: supplier list, suspension and invites — SU-12

Everything here is behind the policy `AdminOnly` (Auth0 role `Admin`); the web page is `/app/admin/suppliers` (menu
*Fornitori*, permission `admin.users.manage`), with the tabs **Fornitori** and **Inviti** and a button to the existing
invite form. No manual database work is needed to suspend a supplier or to handle an invite.

### 14.1 API

| Endpoint | What it does |
|---|---|
| `GET /api/admin/suppliers?search=&status=&page=&pageSize=` | Suppliers, newest first, **paginated in SQL** (`pageSize` 1-100, `page` at least 1; out of range values are clamped). `search` matches the legal name or the email, case-insensitive, LIKE wildcards escaped. `status` is `Pending`, `Active` or `Suspended`. Each item has the status, categories, comuni, the suspension date and note, and `openRequests` (new, taken, in progress). |
| `POST /api/admin/suppliers/{orgId}/suspend` | Body `{ "reason": "…" }`, required, at most 500 characters (400 `validation_error` otherwise). 404 `supplier_not_found`, 409 `supplier_already_suspended`. |
| `POST /api/admin/suppliers/{orgId}/reactivate` | 404 `supplier_not_found`, 409 `supplier_not_suspended`. |
| `GET /api/admin/suppliers/{orgId}/audit` | Audit trail of the supplier, newest first (at most 50 lines): action, admin (name or email, from `Users`), time, reason, status before and after. |
| `GET /api/admin/suppliers/invites?search=&state=&page=&pageSize=` | Invites, newest first, paginated in SQL. `state` is `Pending`, `Used`, `Expired` or `Revoked`; `search` matches the invited email. The link token is never returned (only its hash is stored, section 1). |
| `POST /api/admin/suppliers/invites/{id}/resend` | A **new token** (the old link stops working), a new 7-day expiry, the invite email queued on Hangfire. Allowed for a pending or expired invite (also one created before SU-01, which had no token hash). 409 `supplier_invite_not_resendable` (used or revoked), 409 `duplicate_invite` (another invite for the email is pending), 409 `supplier_email_taken` (a profile with the email exists since), 404 `supplier_invite_not_found`. |
| `DELETE /api/admin/suppliers/invites/{id}` | Revokes a pending invite (204): the row is kept with `RevokedAt`, and the link answers 422 `supplier_invite_revoked` in the lookup and in the registration. 409 `supplier_invite_not_pending` (used, expired or already revoked), 404 `supplier_invite_not_found`. A revoked invite does not block a new invite for the same email. |

The invite state is computed on read: `Used` (accepted), `Revoked`, `Expired` (past its expiry, or no token hash), otherwise
`Pending`.

### 14.2 What a suspended supplier can and cannot do (A4-29)

| | Suspended supplier |
|---|---|
| Receives new requests | No: it is not in the host search (only `Active` suppliers are) and a request addressed to it is refused with 422 `service_request_supplier_inactive` |
| Take, complete or reject a request | **No**: 422 `service_request_supplier_not_active`, checked before the state machine, after the 404 and the 403 for another supplier's request. Nothing is saved and nobody is notified. The same applies to a supplier that is still `Pending` |
| Lift the suspension with the activation wizard | No: `POST /api/supplier/profile/activation/complete` answers 422 `supplier_suspended`. Only an admin reactivates it |
| Sign in, read the inbox, the detail and the history, edit the profile | Yes (read access stays): the console shows a banner *Account fornitore sospeso* and disables the buttons of the requests. The reason of the suspension is an internal note and is **never** sent to the supplier |
| Be paid for work already done | Yes: *Segna pagato* is the host's action and does not depend on the supplier's status |
| Open requests (new, taken, in progress) | They stay as they are. The list shows how many each supplier has open, and the suspend dialog warns about them. What to do with them is a product question (see the open questions of SU-12) |

Reactivation sets the status back to `Active` if the supplier had accepted the terms (it was active before),
otherwise to `Pending`: a reactivation never skips the activation wizard.

### 14.3 Audit trail

Table `SupplierAdminAuditEntries` (migration `SupplierAdminSuspension`): one row per admin action, written in the same
transaction as the change, never updated: `Suspended`, `Reactivated`, `InviteResent`, `InviteRevoked` with the admin's Auth0
subject, the time (UTC), the reason (suspension) and the status before and after. The rows of a supplier are shown in
the page (*Storico*); the invite rows (`InviteId`) can be read in the database. There is no foreign key: the trail
survives the deletion of an org by `fix-orphaned` (section 9). The logs carry only ids and the masked email, never
the reason.

### 14.4 After a deploy

- [ ] As admin: `/app/admin/suppliers` lists the suppliers with their status; the filter *Sospeso* and the search by
      name or email work, 20 per page.
- [ ] *Sospendi* on an active supplier without a reason is not possible; with a reason the badge becomes *Sospeso*,
      the row shows the note, *Storico* shows who did it and when.
- [ ] As that supplier: the console shows the suspension banner, the buttons of the requests are disabled, and the
      supplier is no longer offered to hosts (marketplace and request creation).
- [ ] *Riattiva* brings it back (*Attivo* if it had accepted the terms).
- [ ] *Inviti*: *Reinvia* on an expired invite → the email arrives with a new link that works; the old link does not.
      *Revoca* on a pending invite → its link shows "Questo invito è stato revocato".

## 15. Host: timeline, rejection reason, "Segna pagato", another supplier — SU-09

### 15.1 The timeline of a request

Every request the host reads (`GET /api/service-requests`, `GET /api/service-requests/{id}`, and the long-rent twins
under `/api/long-rent/service-requests`) carries `history`: one step per transition of the state machine, oldest
first, rebuilt from the dates the request keeps (the same builder as the supplier console, section 11.3, so host and
supplier read the same story and no column was added):

| Step | Party | Date |
|---|---|---|
| `Richiesto` | `Host` | `createdAt` |
| `PresoInCarico` | `Supplier` | `takenAt` |
| `Completato` | `Supplier` | `completedAt` |
| `Pagato` | `Host` | `paidAt` |
| `Rifiutato` | `Supplier` | `updatedAt` (a rejection is final, nothing updates the request afterwards), with the reason in `reason` |

- **Who:** the web shows the host's steps as "Il tuo team" and the supplier's steps with the supplier's business name
  (`supplierName`). A **member of the supplier's team is never named to the host**: `actorName` is always null in
  the host answer (the supplier console names the member to the supplier's own team, section 11).
- **Dates** are UTC instants; the web shows them in Europe/Rome.
- The web (`ServiceRequestTimeline`, used by the booking, the property, the long-rent property and the marketplace
  pages) also says what the request is waiting for ("In attesa che il fornitore accetti o rifiuti", ...).
  Loading, load error with *Riprova* and empty states are in the cards and in the marketplace page.

### 15.2 "Segna pagato" and the payment notification

- Payment of the supplier stays a **manual flag** (DECISIONI.md, undecided points: no Stripe integration towards the
  supplier): CasaZen does not pay the supplier and there is no transfer to it. The web asks for an explicit confirmation before the call ("Confermi di aver pagato ...?
  CasaZen non effettua il pagamento: questo segna solo che l'hai già fatto. L'operazione non si può annullare"). The
  buttons *Segna pagato* and *Richiedi ad altro fornitore* need `property.write` of the context (short-rent or
  long-rent) and are hidden otherwise; the API checks it anyway. A 409 or 422 (the request changed meanwhile, for
  example paid from another tab) reloads the list.
- When the host marks a request as paid the **supplier is notified**, on Hangfire and never inside the host's
  request: an email to the supplier profile's address (template `service-request-paid`, `EmailTexts.resx` IT/EN, says
  that the payment is made outside CasaZen and links the supplier console, from `App:PublicSiteBaseUrl`) and one push
  to the supplier org's devices (`service-request-paid`, delivery key `service-request:{id}:Pagato`, opens the app's
  property list like the "new request" push). A failure to queue them is logged and never an error for the host.
  A supplier suspended after completing the work (SU-12) is notified too: it is still owed what it completed.
- The host is told of a rejection (email with the reason, and push) since A6-08; the reason is never on the lock screen.

### 15.3 Asking another supplier after a rejection

*Richiedi ad altro fornitore* appears on a rejected request (not when a newer, not rejected request for the same job
already exists: then the row says "Già richiesto a un altro fornitore"). It opens the normal request form for the same
property (short-rent: the same stay, **D2**; long-rent: the property) with the same category and notes, and without the
suppliers that already rejected that job (same property, stay and category) in the list. The API does not forbid asking
a supplier that rejected again: the exclusion is only the default of the form. The result is an ordinary new request
(`POST /api/service-requests` or `/api/long-rent/service-requests`), linked to the old one only by the stay.

### 15.4 After a deploy

- [ ] Reject a new request as the supplier with a reason → on the booking, the property and the marketplace page the
      host sees *Rifiutato*, the date and "Motivo del rifiuto: ...", and the button *Richiedi ad altro fornitore*.
- [ ] The button opens the form with the stay, category and notes filled in, without the supplier that rejected; the
      new request appears in the supplier's inbox and the old row says "Già richiesto a un altro fornitore".
- [ ] Take and complete a request → the timeline shows each step with the date in Italian time, then *Segna pagato*
      asks for confirmation; cancelling calls nothing; confirming marks it *Pagato* and the supplier gets an email and
      a push ("Richiesta segnata come pagata").
- [ ] As a user without `property.write` (read-only role): the timeline is readable, with no buttons.

## 16. Activation: real requirements, 5-step wizard saved by the server, versioned Terms — SU-05

Audit A4-09 / A4-31. Before SU-05 the Terms alone activated a profile: a supplier with no category and no comune showed
as "Active, visible to hosts" and appeared in no search; the step of the wizard lived in the browser; the acceptance
stored only a date, with no version and no link to the text.

### 16.1 Requirements (checked on the stored profile, never on what the client says)

`POST /api/supplier/profile/activation/complete` `{ tosAccepted, tosVersion }` activates only when the stored profile has:

| Blocker code (`blockers` of the 409) | Requirement | Wizard step |
|---|---|---|
| `legal_name_missing` | business name not blank | 1 `identity` |
| `phone_invalid` | 6 to 15 digits, optional leading `+`, separators ` - . ( )` only (E.164 max 15). It is a format check: no SMS verification exists | 1 |
| `categories_missing` | at least one **known** category code (`ServiceCategories`) | 2 `services` |
| `comuni_missing` | at least one comune (ISTAT code or the old text) | 2 |
| `bio_missing` | description not blank | 4 `profile` |
| `tos_not_accepted` | `tosAccepted: true` | 5 `terms` |

Otherwise 409 `supplier_activation_blocked` with `blockers: [codes]` and the localized `detail`; nothing is saved. Steps 3
(`showcase`, photos) and the calendar of step 5 are optional and never block. The VAT number is optional (a supplier
without one is not refused: product decision to confirm, see the open questions).

`PUT /api/supplier/profile` of an **Active** supplier cannot take a requirement away (empty categories, comuni or
description, blank name or invalid phone): 422 `supplier_profile_requirements`, nothing saved. Only a requirement the
edit newly takes away counts: a profile activated before SU-05 that already lacks one (say, the description) can still
be edited and upload photos. A Pending supplier edits freely. The `profile/photos` upload and the admin repairs are unaffected.

### 16.2 The wizard is saved by the server

- `GET /api/supplier/profile/activation` answers `{ status, currentStep, steps[{ id, status, blocker, required }], tos }`.
  `status` is the real one: `Pending` (never activated), `Active`, `Suspended` (the wizard only says an admin must
  reactivate it). Each step derives from the stored profile.
- `PUT /api/supplier/profile/activation/step` `{ step: 1-5 }` saves the step reached (`SupplierProfiles.ActivationStep`,
  null until saved: the first incomplete required step is opened). Any device resumes there; a step outside 1-5 is 400.
- The web wizard saves each step's data with `PUT /api/supplier/profile` and then the step number; the last step lists
  what is still missing (with a link to its step) and keeps *Attiva profilo* disabled until nothing is.

### 16.3 Versioned Terms of Service and re-acceptance

- The supplier accepts the **Terms of Service** (`Legal:Documents:Tos`, the same document the hosts accept; the drafts
  `2026-10-v1` already cover the marketplace suppliers, [`legal-documents.md`](legal-documents.md)). The checkbox names
  the version and links `/legale/termini` in a new tab.
- `tosVersion` must be the version in force: otherwise 409 `supplier_tos_version_stale` (the page reloads and shows the
  new one). The server stores `SupplierProfiles.TosVersion` + `TosAcceptedAt` and a `ConsentRecords` row of the supplier
  org (`Type = Tos`, user, version, IP, time): the history of every acceptance (like the host consents).
- A new version (`Legal__Documents__Tos__Version`) makes `tos.reacceptanceRequired` true for every active supplier
  that accepted another one. From then on **take, complete and reject** answer 422
  `supplier_tos_reacceptance_required` (`tos.blocksActions`) until the supplier accepts: the dashboard shows a banner and
  `/app/supplier/activation` shows the acceptance (`POST /api/supplier/profile/tos/accept` `{ tosVersion }`, 204,
  status unchanged). Hosts still see the supplier and can still send requests; the supplier answers once it accepts.
- **Existing suppliers** (accepted before SU-05): `TosVersion` is null. They are asked to accept (banner,
  `reacceptanceRequired: true`) but **are not blocked** (`blocksActions: false`): they accepted a text nobody could
  read, and blocking every working supplier at deploy was judged too harsh without the product owner's say. Decision to
  confirm; to block them too, treat a null version as stale in `SupplierActivationRules.TosState`.
- Before changing the version tell the suppliers (it blocks them) and publish the text first (`legal-documents.md` §2).

### 16.4 After a deploy

- [ ] Migration `SupplierActivationTosVersion` is in `__EFMigrationsHistory` (two nullable columns).
- [ ] New supplier: `GET …/activation` → `currentStep` 2 (or 1), `tos.currentVersion` = the configured version.
- [ ] `POST …/activation/complete` on a profile without categories → 409 with `blockers: ["categories_missing", …]`.
- [ ] Complete the 5 steps on the web, reload in the middle: the wizard resumes at the same step on another browser.
- [ ] After activation: `SupplierProfiles.TosVersion` is set and a `ConsentRecords` row (`Type = 0`, supplier org) exists.
- [ ] Raise the Terms version on test: an active supplier sees the banner, take/reject answer 422 until it accepts.
- [ ] Suppliers that are `Active` with no category or no comune (activated before SU-05) are not touched; list them with

  ```sql
  SELECT "OrgId", "LegalName" FROM "SupplierProfiles"
  WHERE "Status" = 1 AND ("CategoriesJson" = '[]' OR ("ComuniJson" = '[]' AND "ComuneIstatCodesJson" = '[]'));
  ```

  and ask them to complete the profile (an active supplier cannot empty these fields any more).

## 17. Error states and localized messages of the supplier pages — SU-06

Audit A4-25 / A4-27 (web and API; the app's own texts are the mobile tasks).

- **Web:** every supplier page that loads data shows an error state when the request fails, never an endless spinner
  or an empty list: activation (a 403 on `GET /api/supplier/profile` no longer leaves "Caricamento…"), profile,
  availability (a failed load used to show every day as available), dashboard, inbox, request detail, calendar
  (these three already had it). The shared component is `components/shared/error-state.tsx` (`role="alert"`, the server's
  localized message when there is one, *Riprova*). Each page has a test with a forced API error.
- **API:** the supplier endpoints no longer answer `{ "error": "No supplier org found" }` / `"Supplier profile not found"`,
  nor inline Italian or English text. They answer the FD-05 problem (`code` + localized `detail`, Italian by default, English
  with `Accept-Language: en`) with keys of `SharedResources.resx` / `.en.resx`:

  | Endpoint | Status / `code` | Key |
  |---|---|---|
  | any `api/supplier/*` without a supplier org or profile | 404 `not_found` | `SupplierProfileNotFound` |
  | `GET /api/supplier/availability` `to` before `from` / range over 90 days | 400 `validation_error` | `SupplierAvailabilityRangeInvalid` / `SupplierAvailabilityRangeTooLong` |
  | `POST /api/supplier/profile/photos` over 10 photos / invalid file / storage failure | 400 `validation_error` / 500 `server_error` | `SupplierPhotoLimit` / `SupplierPhotoInvalid` / `SupplierPhotoUploadFailed` |
  | `GET /api/suppliers` without `comune` or `propertyId`; unknown property | 400 `validation_error`; 404 `not_found` | `SupplierSearchTargetRequired`; `PropertyNotFound` |
  | `GET /api/public/suppliers/{slug}` unknown or not active | 404 `not_found` | `SupplierShowcaseNotFound` |
  | `POST /api/admin/suppliers/invite` with a pending invite for the email | 409 `duplicate_invite` | `SupplierInviteDuplicate` |

  The 409 `duplicate_invite` is thrown by the service (`DomainConflictException`); the message no longer echoes the email.
  `SharedResourcesLocalizationTests` fails when a key misses one of the two languages.
- No deploy step: no migration, no configuration.

## Known limits (other tasks)

- A supplier who lost the claim token cannot register again with the same email (409 `supplier_email_taken`): the
  claim without token links the existing profile once the Auth0 email is verified (section 2.2). The web pages show
  the localized message of the 409; a dedicated "link it" button for that code is a frontend follow-up.
- Service requests: the host timeline, rejection reason and "paid" confirmation are in section 14 (SU-09), but only on
  the web: the app's supplier choice (today the first result) is MO-10 and its error states MO-07. `chargeToGuest` is
  still always refused, also for long-rent (open product point).
