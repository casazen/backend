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
and the payment notification to the supplier (SU-09, A4-28). Section 19: the supplier's catalog of services with prices
and the category `electrical` (SP-02, redesign wave). Section 20: the supplier's agenda (weekly hours, time off, blocks, the
rules, the calendar) and the slot planner (SP-03, redesign wave). Section 21: the service request with a time and a price (start,
cancel, propose another time, reminder, final amount, photos), the inbox filters, the batch accept, `today` and `checklist`, the
automatic cancellation of the requests nobody answers, and what the supplier sees before the take (SP-04, redesign wave).
Section 25: the supplier's Stripe Connect account (SP-14). Section 26: paying the supplier inside CasaZen with the CasaZen commission (payment mode of a
request, the payer's link, the host's confirmation of an amount above the quote, the supplier's payment request and offline record; SP-15a, redesign wave; the webhook, the jobs, the refunds and the admin tools, 26.7, SP-15b).

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
`Richiesto → PresoInCarico` (take), `Richiesto → Rifiutato` (reject), `PresoInCarico → InCorso` (start, SP-04),
`PresoInCarico/InCorso → Completato` (complete), `Completato → Pagato` (mark-paid) and, since SP-04,
`Richiesto/PresoInCarico/InCorso → Annullato` (cancel). `Rifiutato`, `Pagato` and `Annullato` are final. Who may cancel, the new
error codes and the new endpoints are in section 21.2.

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
   - the services of the duplicate's price catalog (`SupplierServiceListings`, SP-02, section 19), the deleted ones too,
     move to the keeper before the profile is deleted (the cascade would take what stayed); a slug the keeper already
     uses gets the next free suffix (`pulizie` → `pulizie-2`); the keeper may end up above the 30 services limit, which
     only stops it from creating more (`serviceListingsMoved`);
   - the duplicate's agenda (SP-03, section 20.8): its time off, blocks, extra openings and calendar engagements move; its
     weekly hours and its settings move only when the keeper has none (`agendaRowsMoved`);
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
`serviceListingsMoved`, `agendaRowsMoved`, `duplicateOrgDeleted`), `danglingLinksCleared[]` and `supplierLinksBackfilled[]` (user ids), `orphanProfiles[]`,
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
| `category`, `urgency`, dates of the request | yes | yes | yes |
| `propertyName`, `notes` (host's notes) — **SP-04, decision D9: not before the take** | **no** (null) | yes | **no** (null) |
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
- The host's notes are free text written by the host: the supplier sees them as written, **once it has taken the request** (SP-04,
  section 21.6: until then neither the notes nor the name of the property are shown, and the emails and pushes to the supplier name only the
  comune). The client (the host org) is named from the start, with `clientId`/`clientName`.
- A rejected request never shows address and contact: it can only be rejected before the take (section 8.1).
- The old `GET /api/service-requests/{id}` and `?view=supplier` still answer the supplier with the host-shaped DTO
  (no address, no contact, no guest data); the web console does not use them.

### 11.2 Endpoints

| Endpoint | Notes |
|---|---|
| `GET /api/supplier/inbox?status=&from=&to=&page=&pageSize=` (SP-04 adds `tab`, `service`, `comune`, `when`, `clientId`: section 21.7) | `status`: `open` (default: `Richiesto`, `PresoInCarico`, `InCorso`), `history` (`Completato`, `Pagato`, `Rifiutato`, `Annullato`), `all`, or one status name (any case). `from`/`to`: Europe/Rome days `YYYY-MM-DD`, both included, each optional. `pageSize` 1–100 (default 20). Answer `{ items, total, page, pageSize }`, newest activity first. 400 `validation_error` for another status (`SupplierInboxStatusInvalid`) or `from` after `to` (`SupplierInboxPeriodInvalid`). |
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
| `InCorso` (SP-04) | `StartedAt` | `Supplier` |
| `Completato` | `CompletedAt` | `Supplier` |
| `Pagato` | `PaidAt` | `Host` |
| `Rifiutato` | `UpdatedAt` (final status, nothing updates it later) | `Supplier`, with `reason` |
| `Annullato` (SP-04) | `CancelledAt` (else `UpdatedAt`) | `CancelledBy` (`Host`, `Supplier` or `System`; `Host` for a request cancelled before the column existed), with `reason` |

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
- **SP-15a:** a request paid inside CasaZen (`paymentMode: Online`, section 26) cannot be marked paid by hand: `mark-paid` answers 422
  `service_request_online_payment` (the payer pays with the emailed link, or the supplier records the exception). "Segna pagato" stays the way for every
  `Manual` request: the ones that exist since before SP-15a, the ones taken while the flag `SupplierOnlinePayments` is off, and the ones taken
  online but completed with the flag off or with an amount that cannot be charged online (they fall back to `Manual` at the completion).
  A request completed as `Online` stays `Online` if the flag is switched off afterwards.
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

## 18. Public showcase of a supplier (v0) — SU-13

Audit A4-16, issue #303 (minimum). Before SU-13 the page `/s/:slug` called the API with a relative URL (on Vercel it got
`index.html`), nobody ever generated the slug, and the error said "Supplier not found" in English.

| What | Behaviour |
|---|---|
| Address | `{App__PublicSiteBaseUrl}/fornitori/{slug}`, a page of the CasaZen web app in its public shell (`PublicSiteShell`, not an org's booking site). **No domain is written in the code**: the API builds the absolute URL from `App__PublicSiteBaseUrl` (`PublicSiteLinks.TryPublicPage`) and the web app only knows the path. Without that variable the preview says the public address is not configured and shows no link. The old `/s/:slug` redirects to the new path |
| Slug | `SupplierProfiles.ShowcaseSlug`: lowercase ASCII from the business name (`Pulizie Città Srl` → `pulizie-citta-srl`), `-2`, `-3`, `-4`, then `-<6 hex>` on a collision. Generated when the profile is **activated** and then **never changed** (a link already shared keeps working after a rename). Unique index `UIX_SupplierProfiles_ShowcaseSlug` (where not null), migration `SupplierShowcaseSlugUnique`; the 23505 of a race retries with the next candidate |
| Suppliers activated before SU-13 | They have no slug. `GET /api/supplier/showcase` (the console's *Vetrina* page) generates it the first time they open it; there is no batch backfill. Before the migration check that no two profiles share a hand-set slug: `SELECT "ShowcaseSlug", count(*) FROM "SupplierProfiles" WHERE "ShowcaseSlug" IS NOT NULL GROUP BY 1 HAVING count(*) > 1;` |
| Public API | `GET /api/public/suppliers/{slug}` (anonymous, `PublicRead` rate limit): only an **Active** supplier with that slug (looked up lowercase); a pending, suspended or unknown one is 404 `not_found` with the localized message. Content: business name, category codes, comuni by name, description, photos, the availability of the next 14 days. **Never** the phone, email, VAT number or status |
| Preview | `GET /api/supplier/showcase` (supplier policy): the same content from the caller's own profile whatever its status, plus `status`, `published`, `slug`, `publicPath`, `publicUrl`, `indexable: false`. A pending supplier previews a page nobody else can open (no slug, no URL) with the way to the activation; a suspended one is told it is not visible |
| Web | Sidebar *Vetrina* and the *Anteprima vetrina* button on the profile page (`/app/supplier/showcase`): the preview, the public URL (copy, open in a new tab). Public page: loading skeleton; **404 → "Fornitore non trovato"**; any other failure → an error with a retry (never "not found"); translated categories, only absolute photo URLs |

### SEO: `noindex` in v0, consistent with BK-15

BK-15 makes the booking sites and the guides indexable (crawler HTML from the Vercel Function, sitemaps) and keeps the
private pages out. The supplier showcase is **not** indexable in v0: the page sets `<meta name="robots"
content="noindex,nofollow">` in every state, `/fornitori/` is in `DISALLOWED_PATHS` of `robots.txt` (production), the API
answer carries `X-Robots-Tag: noindex`, the page is not in any sitemap and no crawler rewrite serves it. Making it
indexable is a product decision (it publishes the business name and description of every active supplier): it needs the
crawler page and the sitemap entry of BK-15's pattern and the removal of these three signals.

### After a deploy

- [ ] Migration `SupplierShowcaseSlugUnique` applied (a unique index).
- [ ] `App__PublicSiteBaseUrl` set: `GET /api/supplier/showcase` of an active supplier returns `publicUrl` on that domain.
- [ ] Activate a test supplier: `ShowcaseSlug` is set; `GET /api/public/suppliers/{slug}` is 200 with `X-Robots-Tag: noindex`;
      the same call for a pending supplier's slug (set by hand) is 404.
- [ ] Open `/fornitori/{slug}` signed out: the page shows the supplier in the public shell; `/fornitori/non-esiste` says
      "Fornitore non trovato"; with the API down it shows an error with *Riprova*, not "not found".
- [ ] `https://<domain>/robots.txt` (production build) has `Disallow: /fornitori/`.

## 19. The supplier's service catalog with prices — SP-02

Redesign wave task SP-02 (branch `feature/rd-supplier-catalog`, backend only: no screen yet; the console screens are SP-06).
Gap report 05 §4.1 and decisions D2-D11 of `redesign/docs/wave/WAVE-SPEC.md`. Before this, a supplier only had its
categories (`SupplierProfiles.CategoriesJson`) and no prices. Stacked on this branch, and **not part of it**: the agenda and
slot planner (SP-03), the time and price on a service request (SP-04), iCal by the hour (SP-05), the public reads and the booking
from the showcase (SP-09, SP-10) and the payments (SP-15). `ServiceRequest` is not touched.

### 19.1 What a service is

Table `SupplierServiceListings` (entity `SupplierServiceListing`, migration `AddSupplierServiceCatalog`, applied at startup,
a new table: no backfill, every catalog starts empty).

| Field | Meaning |
|---|---|
| `id`, `slug` | `slug` is unique **per supplier** among the services that are not deleted (partial unique index `UIX_SupplierServiceListings_OrgId_Slug`): it comes from the name when the service is created (`Pulizia città` → `pulizia-citta`, `-2`, `-3` on a collision, `servizio` when the name has no usable character) and then never changes, except that a **draft** follows a change of its name (a draft was never public). It will be the last part of `/fornitori/{supplierSlug}/servizi/{slug}` (SP-09) |
| `name` (≤ 60), `category`, `summary` (≤ 200), `description` (≤ 2000) | `category` is a code of `GET /api/service-categories` (anything else: 422 `invalid_service_category`) |
| `priceFromCents`, `priceUnit`, `pricesIncludeVat`, `requiresQuote` | "from" price in euro cents (1 to 10,000,000); `null` is **on quote**. `priceUnit`: `PerJob`, `PerHour`, `PerSet`, `PerSquareMeter`. `pricesIncludeVat` (decision D4: one price with a flag) is **false until the supplier says so**: CasaZen promises nothing about VAT on its behalf (the legal and tax wording is `[CONSULENTE FISCALE]`). `requiresQuote`: the customer waits for the supplier's offer |
| `durationMinutes` (5 to 1,440), `minNoticeHours` (0 to 720, `null` = the supplier's default notice, SP-03), `weekdays` | `weekdays` are `Monday` ... `Sunday` (stored as the bit mask `WeekdaysMask`, bit 0 Monday ... bit 6 Sunday; left out = every day, an empty list = no day). They restrict the supplier's working hours (SP-03), never replace them |
| `supplements` (≤ 10) | `[{ code, label, amountCents, per, max }]`, structured because the booking's price estimate (SP-09) is computed from them. `code`: lowercase letters, digits and single hyphens (≤ 40), unique in the service; `label` ≤ 80; `amountCents` 1 to 10,000,000; `per`: `flat`, `bathroom`, `sqm30`, `set`, `hour`; `max`: 1 to 99 or `null` |
| `included`, `excluded` | lists of lines (≤ 10 each, ≤ 100 characters each; blank and repeated lines are dropped) |
| `photoUrls` (≤ 6) | absolute URLs of objects in the public bucket under `suppliers/{orgId}/photos/`, the first is the cover |
| `status` | `Draft` → `Active` (publish) ⇄ `Paused`; never back to `Draft` |
| `sortOrder` | position (0 to 9,999), then creation date; a new service goes last |
| `publishable`, `missingForPublication` | computed on read: what the service still lacks to be published (below) |
| `version` | PostgreSQL `xmin`, see 19.5 |

The limits are constants in `Casazen.Core/Suppliers/SupplierServiceCatalogLimits.cs`: technical bounds against typos and
oversized bodies, not product rules. **30 services per supplier** (the deleted ones do not count): checked when one is
created or duplicated, under the catalog lock.

### 19.2 Endpoints (policy `RequireSupplier`, supplier org from the caller's own link, not behind a feature flag)

| Method and path | Answer |
|---|---|
| `GET api/supplier/services` | `{ items[], total, limit }`: the supplier's services that are not deleted |
| `POST api/supplier/services` | 201 + `Location`: a **draft** (may be incomplete: the wizard saves a draft at every step). Only `name` and `category` are needed |
| `GET api/supplier/services/{id}` | the service |
| `PUT api/supplier/services/{id}` | a **replacement** of the content with the `version` the client read (a field left out takes its default); `photoUrls` and `sortOrder` stay when the body does not carry them. `photoUrls`, when sent, are the photos to **keep, in the new order**: a subset of the service's own (anything else: 422); the others leave the list and their objects are deleted when nothing else uses them. The status and the slug do not change (a draft's slug follows its name) |
| `DELETE api/supplier/services/{id}` | 204, **soft** delete (`DeletedAt`): hidden from every read, the slug is free again, the photo objects stay |
| `POST .../{id}/publish` | 200, `Draft` or `Paused` → `Active`; an active one is returned as it is |
| `POST .../{id}/pause` | 200, `Active` → `Paused`; a paused one is returned as it is |
| `POST .../{id}/duplicate` | 201: a **draft** copy (same content and photos, a new slug, " (copia)" / " (copy)" after the name, last position) |
| `POST .../{id}/photos` | 200, multipart field `photos` (up to 6 files of 10 MB, JPEG, PNG or WebP checked on their **content**, 6 photos per service, all or none): the service with its new `version` |

Errors (ProblemDetails `code`): 404 `supplier_service_not_found` (also another supplier's service and a deleted one; `not_found`
when the caller has no linked supplier org: the catalog never provisions one); 400 `validation_error` for a text or a list over
its limit; 422 `invalid_service_category`; 422 `supplier_service_invalid` and `supplier_service_not_publishable`, both with
`fields` (the JSON names of the fields at fault, `supplements[1].amountCents` for a supplement); 422
`supplier_service_limit_reached`, `supplier_service_cannot_pause` (a draft is not published), `supplier_service_photo_none`,
`supplier_service_photo_invalid_type`, `supplier_service_photo_invalid_size`, `supplier_service_photo_limit_reached`; 409
`supplier_service_changed`. Messages are keys of `SharedResources` (Italian and English); the list of the catalog's keys is
`SupplierServiceCatalogErrors.MessageKeys`.

### 19.3 When a service can be published

`Name`, a known `category`, a `durationMinutes` and a price (`priceFromCents`) **or** `requiresQuote`. Publishing a draft or a
paused service checks it (422 `supplier_service_not_publishable`, `fields` says what is missing: `name`, `category`,
`durationMinutes`, `priceFromCents`). A **published service stays complete**: a `PUT` that would take one of these away is the
same 422 (the same rule the profile has, SU-05). A draft and a paused service may be incomplete. The supplier's own status
(`Pending`, `Active`, `Suspended`) does not limit the catalog: like the profile, it can be edited by a suspended supplier;
whether a service is **shown** will also depend on the supplier being `Active` (public reads, SP-09).

### 19.4 Tenancy: keyed by the supplier org, not `ITenantOwned`

The table follows `SupplierProfile` and `SupplierAvailability`: `OrgId` is the **supplier** org (`SupplierProfile.OrgId`,
foreign key in cascade). It is **not** `ITenantOwned`: a supplier acts as `User.SupplierOrgId` and a supplier-only account has
no `User.OrgId` (PL-05, section 13), so the global tenant filter (host org) would give it zero rows, and a dual-role host would
get the wrong org. It is in the allow-list of `TenantQueryFilterArchitectureTests` with the reason, and **every read and write
carries an explicit `OrgId` predicate**: they all go through `SupplierServiceCatalogService.Listings(orgId)`. Guards:
`SupplierServiceListingTenancyTests` (no other file may use the table; every statement of the two allowed files carries the
predicate or is an insert), `SupplierServiceCatalogServiceTests` and `SupplierServiceCatalogIntegrationTests` (another
supplier's service is 404 on every endpoint), and `SupplierServiceCatalogPostgresTests` on PostgreSQL. Whoever adds a reader
(the public reads of SP-09) must add the file to that allow-list, filter by the supplier org **and** require the supplier to be
`Active`.

### 19.5 Concurrency

- **Optimistic**: `version` is `xmin`. `PUT` must carry the version the client read; another one is 409
  `supplier_service_changed` (reload and apply the change again). Every update changes it, a **photo upload too**: use the
  `version` in the answer of the upload for the next `PUT`.
- **Serialized**: every change of one supplier's catalog (create, duplicate, edit, delete, publish, pause, photos) takes the
  PostgreSQL advisory lock `SupplierServiceCatalog` (scope 1_302, key = the supplier org id) and reads the row after taking it,
  so two requests never both see room for the 30th service, never choose the same slug and never overwrite each other's
  photo list. The unique slug index is the guarantee behind the lock (23505 is answered with the same 409).
- The lock only exists on PostgreSQL: the tests that prove it are `[PostgresFact]` (they run on CI, not on a laptop without
  a database).

### 19.6 Photos

`IImageStorageService.UploadSupplierPhotoAsync` (public bucket, `suppliers/{orgId}/photos/{random}.{ext}`). The file is validated
entirely before anything is stored (type by extension, declared type **and** first bytes, size, count), the objects of a failed or
over-limit upload are removed again. A photo that leaves a service (a `PUT` with a shorter `photoUrls`) is deleted from the
bucket after the commit **only if** it is in the supplier's own folder and no other service of the supplier, deleted ones
included, still lists it (a duplicate shares the objects with its original). Deleting a service keeps its objects.

### 19.7 Merge of duplicate profiles (`fix-orphaned`)

The services of a duplicate profile move to the keeper (section 9.3), under the catalog lock of both suppliers and in the
repair's transaction (a dry run rolls it back). The report has `serviceListingsMoved`.

### 19.8 Category `electrical`

`ServiceCategories.Electrical = "electrical"`, appended **last** to `ServiceCategories.All` (the order is the one the clients
show; never reorder or rename). `GET /api/service-categories` returns it, the supplier profile, the invites and the service
requests accept it, and the e-mails label it (`ServiceCategory_electrical`: *Elettricista* / *Electrician*). The demo's
mapping: pulizie → `cleaning`, manutenzione → `maintenance`, idraulico → `plumbing`, lavanderia → `laundry`, checkin →
`check-in`, giardinaggio → `gardening`, elettricista → `electrical`. **The web and the app still need the label**
(`serviceRequest.categories.electrical` in the web `it.json` / `en.json` and `i18n.test.ts`, the app's `it.ts` / `en.ts`):
until then they show the raw code `electrical` (the web falls back to the value, it does not break).
`docs/runbooks/service-categories.md` lists the codes.

### 19.9 Feature flags introduced here

`Features:SupplierShowcaseBooking` (booking from the public showcase, decision D34, SP-09/SP-10) and
`Features:SupplierOnlinePayments` (payment of the supplier's work inside CasaZen, decision D2, SP-15): both **off by default**
(`appsettings.json`), listed in `FeatureFlags.All` and therefore exposed by `GET /api/public/features` as
`supplierShowcaseBooking` and `supplierOnlinePayments`. The catalog does not depend on them. `SupplierOnlinePayments` is consumed
since SP-14 (the supplier's Stripe account, section 25). See `feature-flags.md`.

### 19.10 After a deploy

- [ ] Migration `AddSupplierServiceCatalog` applied (table `SupplierServiceListings`, index `UIX_SupplierServiceListings_OrgId_Slug`
      with `WHERE "DeletedAt" IS NULL`, four `CK_SupplierServiceListings_*` checks).
- [ ] `GET /api/public/features` has `supplierShowcaseBooking: false` and `supplierOnlinePayments: false`.
- [ ] As a supplier (test environment): `POST /api/supplier/services` with a name and a category → 201 `Draft`;
      `POST .../publish` → 422 until a duration and a price (or `requiresQuote`) are set; then 200 `Active`; `.../pause`,
      `.../duplicate` and `DELETE` answer as above; another supplier's `GET {id}` is 404.
- [ ] `GET /api/service-categories` returns 11 codes, the last one `electrical`.

## 20. The supplier's agenda: hours, time off, blocks, rules and the slot planner — SP-03

Redesign wave task SP-03 (branch `feature/rd-supplier-agenda`, backend only: no screen yet, the console screens are SP-07;
stacked on SP-02). Gap report 05 §4.1 and decisions D10 and D34 of `redesign/docs/wave/WAVE-SPEC.md`. Before this a
supplier only had `SupplierAvailability`, a yes/no per day. Stacked on this branch, and **not part of it**: the time and
price on a service request (SP-04), iCal events by the hour (SP-05), the public slots and the estimate (SP-09), the booking
from the showcase with its holds (SP-10), the endpoint of the supplier's settings (`api/supplier/settings`, SP-16). No public
endpoint, no feature flag, nothing new in the configuration.

### 20.1 What the agenda is

| Table (entity) | One row is |
|---|---|
| `SupplierWorkingHours` | a band of the **weekly** hours: `Weekday` (the number of `DayOfWeek`, Sunday is 0), `StartMinute` and `EndMinute` as **minutes after midnight on the wall clock of Rome** (`EndMinute` up to 1440, midnight). A supplier has up to **3 bands a day**; a weekday with none is a rest day. Unique on supplier + weekday + start |
| `SupplierTimeOff` | days the supplier does not work: `FromDate` to `ToDate`, both included (calendar days of Rome), a `Reason` (`Vacation`, `Holiday`, `Illness`, `Other`: only a label) and an optional `Label` (≤ 80 characters, only the supplier's console shows it) |
| `SupplierBusyWindow` (table `SupplierBusyWindows`) | hours in UTC: `StartUtc`, `EndUtc`, a `Kind` — `Block` (the supplier blocks them), `ExtraOpening` (opened on top of the weekly hours) or `External` (an engagement of the supplier's own calendar) — a `Source` (`Manual` or `ICalFeed`), a `Label` (≤ 80, **never public**) and the `ExternalUid` of an iCal event |
| `SupplierSettings` | **one row per supplier** (the key is the supplier org): the five rules (below), `ParallelJobs` (decision D10: **1**, not editable from the console), `HoursConfiguredAt`, and the columns the next tasks use (`RespondWithinMinutes` 180, `OnlineBookingEnabled` false, `AutoAcceptRegulars`, three notification switches): no endpoint changes those yet |

`SupplierAvailability` (one row per day) **stays as it was** and is now read as the *override of the day*: `Available = false`
closes the whole day (by hand, or by an all-day event of the calendar feed), `true` is no override. There is no backfill:
a supplier with no hours has no slot until it saves some. The migration is `AddSupplierAgenda` (four new tables, applied at
startup): every agenda starts empty.

The settings row is **lazy**: a supplier has none until it saves a rule or its hours. A read without a row answers the
defaults and writes nothing; the first write creates the row (under the lock, so two first writes never make two). The
defaults are in `SupplierAgendaDefaults`: buffer **30** minutes, **3** jobs a day, notice **24** hours, horizon **35** days,
slot step **60** minutes. `HoursConfiguredAt` is the moment of the last save of the hours that left at least one band
(`null` while there is none): the supplier's checklist (SP-04) reads it.

### 20.2 Endpoints (policy `RequireSupplier`, supplier org from the caller's own link, not behind a feature flag)

The org comes from `ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync`, like the catalog (section 19.2): the agenda is
business data and never provisions a supplier org; a `Supplier` account with no link is 404 `not_found`.

| Method and path | Answer |
|---|---|
| `GET api/supplier/availability/hours` | `{ days[], configuredAt }`: **seven** days, Monday first, each `{ weekday, bands[{ startMinute, endMinute }] }` (a rest day has no band) |
| `PUT api/supplier/availability/hours` | replaces the week with the body `{ days: [{ weekday, bands: [{ startMinute, endMinute }] }] }` (a weekday that is not sent becomes a rest day; `days: []` clears the hours); answers like the `GET`. A band that stays keeps its row |
| `GET api/supplier/availability/time-off` | `{ items[], total, limit }`: the time off that has **not ended** (last day today or later), by first day; `limit` 100 |
| `POST api/supplier/availability/time-off` | 201 + `Location`, body `{ fromDate, toDate, reason?, label? }` (`reason` left out is `Vacation`) |
| `DELETE api/supplier/availability/time-off/{id}` | 204 |
| `GET api/supplier/availability/blocks` | `{ items[], total, limit }`: the blocks and extra openings the supplier set **by hand** that have not ended, by start; `limit` 200 |
| `POST api/supplier/availability/blocks` | 201 + `Location`, body `{ kind, startUtc, endUtc, label? }` with `kind` `Block` or `ExtraOpening` |
| `DELETE api/supplier/availability/blocks/{id}` | 204; only a manual window |
| `GET api/supplier/availability/rules` | `{ bufferMinutes, maxJobsPerDay, minNoticeHours, horizonDays, slotStepMinutes }` (the defaults while none was saved) |
| `PUT api/supplier/availability/rules` | replaces the five rules; **all five are needed** (a missing one is a 422 naming it, never silently reset to its default); answers like the `GET` |
| `GET api/supplier/calendar?from&to` | what the console calendar draws, see 20.3 |

The endpoints that already existed **do not change**: `GET/PUT api/supplier/availability` (the override of the day),
`calendar/status`, `calendar/ical` and `calendar/sync` (section 12 and `ical.md`).

Errors (ProblemDetails `code`): 404 `supplier_time_off_not_found` and `supplier_block_not_found` (another supplier's entry,
a deleted one, and — for a block — an engagement of the calendar feed, all answer the same: it does not exist for the
caller); 400 `validation_error` for a malformed body (a weekday, a reason or a date that is not one) and for a calendar range
that is reversed or longer than 62 days; 422 with **`fields`** (the JSON names of what is wrong, a day or a band by its
position: `days[1].bands[0].endMinute`) `supplier_hours_invalid`, `supplier_time_off_invalid`, `supplier_block_invalid`,
`supplier_rules_invalid`; 422 `supplier_time_off_limit_reached` and `supplier_block_limit_reached` (the limit is the
message argument). The messages are keys of `SharedResources` (Italian and English); the list is
`SupplierAgendaErrors.MessageKeys`.

What is refused (the limits are constants in `Casazen.Core/Suppliers/SupplierAgendaLimits.cs`, technical bounds rather than
product rules, checked by `SupplierAgendaRules`; the database mirrors them with check constraints):

| Value | Rule |
|---|---|
| Weekly hours | up to **3 bands** a day; a start 0 to 1439, an end 1 to 1440 **after its start**; two bands of a day must not overlap (bands that only touch are accepted: the planner joins them); a weekday at most once; `days` is required |
| Time off | both dates; last day not before the first, **at most 366 days**, not already over; first day at most 730 days ahead; `reason` one of the four; label ≤ 80 characters without control characters; at most **100** entries that have not ended |
| Block / extra opening | `kind` `Block` or `ExtraOpening` (`External` is written only by the calendar sync); both instants (UTC); the end after the start, **at least 15 minutes**; a block at most 31 days; an **extra opening inside one day of Rome** (it may end at the midnight that closes the day); not already over; start at most 730 days ahead; label as above; at most **200** that have not ended |
| Rules | buffer 0 to 240 minutes in **steps of 5**; jobs a day 1 to 50; notice 0 to 720 hours; horizon 1 to 365 days; slot step 15 to 240 minutes in **steps of 5** |

### 20.3 The calendar

`GET api/supplier/calendar?from=2026-10-01&to=2026-10-31` (dates `yyyy-MM-dd`, Rome days, both included; **at most 62
days**; left out: from today, and 30 days after `from`) answers, for the supplier's own agenda only:

- `workingHours`: the weekly hours, as the `GET hours` (seven days);
- `closedDays[]`: `{ date, source }` for the days of the range closed by hand or by the calendar feed (`Available = false`);
- `timeOff[]`: the time off that touches the range;
- `blocks[]`: the blocks, extra openings **and calendar engagements** that touch the range (`kind`, `source`, `startUtc`, `endUtc`,
  `label`), by start;
- `requests[]`: the supplier's service requests that have a day, as **whole-day items**: `{ id, date, status, category }`.
  The day of a request is, today, the check-out day of the stay of a short-rent request (what the inbox calls `scheduledFor`);
  a long-rent request, or an old one not tied to a stay, has no day and is not here until it gets a time (SP-04). Every status
  except `Rifiutato`. Nothing else of the request: **no property, address, host or guest** (those are the request's detail, and the
  supplier sees them only after taking it, section 11).
- `timeZone`: `Europe/Rome`.

### 20.4 The slot planner

`SupplierSlotPlanner` (`Casazen.Core/Suppliers`) is a **pure function**: it reads no clock, no database, no configuration. The
input is a `SupplierPlanningInput` (the instant "now", the rules, the weekly hours, the time off, the closed days, the extra
openings and the list of what takes the supplier's time) and a `SupplierSlotQuery` (the duration of the service, and
optionally the service's own notice and the weekdays it is offered on). `PlanDay` and `PlanRange` (at most 366 days) answer,
for each Europe/Rome day, either **why it has no slot** (`SupplierDayClosure`) or its free slots as UTC instants.

A day has **no slot** when, in this order: it is before today (`Past`); the supplier is off (`TimeOff`); the day is closed by
hand or by the feed (`DayClosed`); the service is not offered on that weekday (`ServiceNotOffered`: the days of a service restrict
the supplier's hours, never replace them, so an extra opening does not open them); there is no weekly band for that weekday
and no extra opening that day (`NoHours`); `MaxJobsPerDay` is reached (`MaxJobsReached`); the **whole day** is inside the notice,
that is it ends before `now + notice` (`WithinNotice`); it is later than `today + HorizonDays` (`BeyondHorizon`: the horizon is
inclusive, 35 days means the 35th day after today can still be booked; the gap report lists it among the rules, not among
the closed days). An open day can still have no *free* slot (everything is taken): that is a day that is full of work, not a
closed one.

On an open day the weekly bands (wall clock of Rome, turned into UTC for that date) and the extra openings of the day are
**joined into continuous bands** (bands that touch or overlap are one: an extra hour that fills the lunch break makes one band,
and a 90-minute service can then cross it). In each band a slot starts at the beginning of the band and then every
`SlotStepMinutes` of real time, as long as `start + duration ≤ end of the band`. A slot is **free** when:

- it starts no earlier than `now + notice` (the notice of the service when it has one, otherwise the supplier's);
- it does not overlap anything that takes the supplier's time, each stretch **widened by `BufferMinutes` before and after**:
  requests with hours (also the ones not accepted yet), **holds** that have not expired, blocks and calendar engagements. The
  buffer applies to all of them alike. It does not apply to the edges of a working band (nothing is before the first slot);
- with `ParallelJobs` above 1 (not offered by the console, decision D10), it is enough that the widened stretches are **never as
  many as the capacity at any one instant** of the slot: two jobs one after the other need one place, not two. With 1 this is
  "nothing overlaps".

`SupplierOccupancy` is **the input door for everything that takes the supplier's time**: `TimedRequest(start, end)`,
`DatedRequest(day)`, `Hold(start, end, expiresAt)`, `Block(start, end)` and `External(start, end)`. A request that only has a
day (the host's requests today: the day is the check-out) takes **no hour** but counts for `MaxJobsPerDay`. Requests and holds count
for the daily maximum on the Europe/Rome day of their start (or of their date); blocks and engagements never do. A hold is a
request waiting for its e-mail check: it counts, so that two customers cannot take the last place of a day at the same time (this
goes one step beyond the gap report, which names only the requests; the planner ignores a hold altogether once it expired, the
instant "now" being part of its input).

### 20.5 Daylight saving time

Working hours are **wall-clock times of Rome**: 09:00 stays 09:00 all year, so on 28 March (CET) it is 08:00 UTC and on 29 March
(CEST) 07:00 UTC. `RomeCalendar.ToUtc(date, time)` (and `ToUtc(date, minutesAfterMidnight)`, 0 to 1440) does the conversion
and has a **rule for the two days a year the clock changes**, so a time never has two answers or none:

| Case | Rule | Example (2026) |
|---|---|---|
| a time that **does not exist** (the hour skipped when summer time starts: 02:00-02:59 on the last Sunday of March) | read with the offset in force **before** the change (+01:00): it lands one hour later on the clock | 29 March 02:30 → 03:30 CEST = 01:30 UTC; 02:00 and 03:00 are the same instant |
| a time that **happens twice** (the hour repeated when summer time ends: 02:00-02:59 on the last Sunday of October) | its **first** occurrence, in summer time (+02:00) | 25 October 02:30 → 00:30 UTC, not 01:30 UTC |

(`TimezoneHelper.ConvertLocalToUtc` is not used for this: it throws for a time that does not exist and takes the second
occurrence of a repeated one.) Nothing else moves a slot: the planner turns the bands into UTC instants **first** and then walks
the grid in real elapsed minutes. So 29 March is a 23-hour day, with nothing offered in the skipped hour (hours 00:00-06:00 give
five hourly slots, at 00:00, 01:00, 03:00, 04:00 and 05:00 on the clock), and 25 October is a 25-hour day on which the repeated
hour offers its slots twice, once per pass (hours 00:00-06:00 give seven). A band that starts in the skipped hour and ends right
after it has no real length and is dropped. The notice and the end of a day use the real length of the day too. All of this has
tests (`RomeCalendarTests`, `SupplierSlotPlannerTests`).

### 20.6 Tenancy: keyed by the supplier org, not `ITenantOwned`

The four tables follow `SupplierProfile`, `SupplierAvailability` and the catalog (section 19.4): `OrgId` is the **supplier** org
(foreign key to `SupplierProfiles`, in cascade). They are **not** `ITenantOwned` (a supplier-only account has no `User.OrgId`,
PL-05, so the global host-org filter would give it zero rows), each is in the allow-list of `TenantQueryFilterArchitectureTests`
with its reason, and **every read and write carries an explicit `OrgId` predicate**: they all go through `HoursOf`,
`TimeOffOf`, `WindowsOf` and `SettingsOf` of `SupplierAgendaService` (or are inserts of a row that has its `OrgId`).
`SupplierAgendaTenancyTests` guards the model, the SQL of those queries and that **no other file** reads the tables (the repair,
`SupplierService.Maintenance.cs`, and the context are the only others, each statement with the predicate): whoever adds a reader
(SP-05 writes the windows of the iCal feed, SP-09 reads the slots) must add the file to `AllowedFiles` with its reason and the
predicate; a public read also needs the supplier to be `Active` and must never expose a `Label`. The tests
`SupplierAgendaPostgresTests` prove the isolation between two suppliers on PostgreSQL. The service request of a supplier is read
through `ISupplierServiceRequestReader.ListForAgendaAsync`, which keeps the guarantee of section 11 (columns of the request only,
the guest is never read).

### 20.7 Concurrency: the lock

**Every write of the agenda takes the advisory lock `SupplierCalendarSync` (scope 1_065, key = the supplier org id, the same lock
as the iCal sync and the manual days, section 12) in a READ COMMITTED transaction and reads after taking it**: so a limit
(100 time off, 200 blocks) is never decided on a stale read, two first writes never create two settings rows, two saves of the hours
never mix their bands, and a sync never writes the same rows at once. A save of the hours is by difference (the bands that stay keep their row,
one that only changes its end is updated), so the unique index never sees a row leave and come back. One lock per supplier: another
supplier is never held back. The lock only exists on PostgreSQL: the tests that prove it (`SupplierAgendaPostgresTests`: every
write waits for the lock held by another connection, parallel writes at the limit, parallel first writes) are `[PostgresFact]`
(they run on CI, not on a laptop without a database). There is **no optimistic version**: `PUT` is a replacement and the last
writer wins (the console saves the whole section it shows).

### 20.8 Merge of duplicate profiles (`fix-orphaned`)

The agenda of a duplicate profile moves to the keeper before the profile is deleted (section 9.3; the foreign keys cascade, so
what stayed would be deleted with it), under the `SupplierCalendarSync` lock of both suppliers and in the repair's transaction
(a dry run rolls it back): its **time off, blocks, extra openings and engagements always move** (dropping a closure would offer a
slot the supplier had closed); its **weekly hours move only when the keeper has none**, and then the keeper's `HoursConfiguredAt`
follows; its **settings row moves only when the keeper has none** (the keeper's rules are the ones in use). The report has
`agendaRowsMoved` per merge (what moved: time off + windows + the bands and the settings row when they came over).

### 20.9 For the tasks stacked on this one

- **SP-04** (requests with hours) — **done, see section 21**: add `SupplierOccupancy.TimedRequest(startUtc, endUtc)` to the list of
  `ISupplierAgendaService.BuildPlanningInputAsync` for the requests `Richiesto`, `PresoInCarico` and `InCorso` that have hours
  (and keep `DatedRequest(day)` for the ones that do not), and use `PlanAsync` under the lock before taking a slot. The
  planner does not change. `ISupplierServiceRequestReader.ListForAgendaAsync` gets the hours too.
- **SP-05** (iCal by the hour): write the events as `SupplierBusyWindow` with `Kind = External`, `Source = ICalFeed` and the
  `ExternalUid`, under the same lock (`CalendarSyncService.AvailabilityLock`), freeing only the windows of the feed; add its own
  unique index on the event and its file to the allow-list of `SupplierAgendaTenancyTests`. `BuildPlanningInputAsync` already
  turns `External` windows into occupancies, and the calendar already lists them.
- **SP-09** (public slots): `PlanAsync(orgId, from, to, new SupplierSlotQuery(durationMinutes, service.MinNoticeHours, service.WeekdaysMask))`
  for a published service of an `Active` supplier; show only the slot instants (never a label, a kind or a reason of closure).
- **SP-10** (holds): add `SupplierOccupancy.Hold(startUtc, endUtc, expiresAtUtc)` and recompute under the lock before creating the hold.

### 20.10 After a deploy

- [ ] Migration `AddSupplierAgenda` applied (tables `SupplierWorkingHours`, `SupplierTimeOff`, `SupplierBusyWindows`,
      `SupplierSettings`; unique index `UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute`; the `CK_Supplier*` checks).
- [ ] As a supplier (test environment): `GET /api/supplier/availability/hours` → seven rest days and `configuredAt: null`;
      `PUT .../hours` with a Monday band `{ startMinute: 480, endMinute: 780 }` → 200 and `configuredAt` set; a fourth band in a
      day → 422 `supplier_hours_invalid` with `fields`; `GET .../rules` → 30, 3, 24, 35, 60.
- [ ] `POST .../time-off` and `POST .../blocks` answer 201, their `DELETE` 204, and `GET /api/supplier/calendar` lists them;
      another supplier's `DELETE` of the same id is 404.
- [ ] The existing `GET/PUT /api/supplier/availability` and `GET /api/supplier/calendar/status` still answer as before.

## 21. Service requests with a time and a price: lifecycle, inbox, today and checklist — SP-04

Redesign wave task SP-04 (branch `feature/rd-supplier-requests`, backend only: the console screens are SP-06/07/08; stacked on
SP-03, which is stacked on SP-02). Gap report 05 §4–§4.1 and decisions D7, D8 and D9 of `redesign/docs/wave/WAVE-SPEC.md`.
Before this a request had no time of its own (its day was the check-out of the stay), no price, no way to be started or
cancelled, and completing it **replaced the host's notes** with the supplier's. Not part of it: the booking from the public
showcase and `Showcase` requests (SP-10), the payment and the customer's confirmation of the final amount (SP-15), iCal events by
the hour (SP-05), and the fields of #466/#467 (`SupplierOrgId?`, `OpenedBy`, `LeaseContractId`). `PropertyId` and `BookingId` are
**not** nullable (decision D2: a host's request is tied to a stay or a property). `mark-paid` ("Segna pagato") is unchanged.

### 21.1 What a request carries now

One migration, `AddServiceRequestSchedule` (columns added to `ServiceRequests`, nothing rewritten, **no backfill**: a request that
exists keeps no time and no price, "da concordare"):

| Group | Columns | Notes |
|---|---|---|
| Time | `ScheduledStartUtc`, `ScheduledEndUtc` | both or none (`CK_ServiceRequests_ScheduledInterval`: end after start). Index `(SupplierOrgId, ScheduledStartUtc)` |
| Service | `ServiceListingId` (catalog of SP-02, `ON DELETE SET NULL`), `ServiceNameSnapshot` (≤ 60), `OptionsJson` | the snapshot keeps the name when the service is deleted; `OptionsJson` (`[]`) is reserved for the options of SP-10 |
| Price | `EstimatedAmountCents`, `QuotedAmountCents`, `FinalAmountCents` (1 to 10,000,000, `CK_ServiceRequests_Amounts`), `PriceLinesJson`, `FinalAmountNeedsConfirmation` | decision D7, section 21.4 |
| Life | `ResponseDueAt`, `StartedAt`, `LastRemindedAt`, `CancelledAt`, `CancelledBy`, `CancellationReason` (≤ 500), `CompletionNotes` (≤ 1000), `WorkPhotosJson` | `CancelledBy`: 0 `Host`, 1 `Supplier`, 2 `System` (`ServiceRequestActorParty`, explicit integers) |
| Proposal | `ProposedStartUtc`, `ProposedEndUtc`, `ProposedAt`, `ProposedByUserId`, `ProposalMessage` | all together or none (`CK_ServiceRequests_ProposedInterval`) |

The three JSON lists are `jsonb NOT NULL DEFAULT '[]'` and the boolean `DEFAULT FALSE`: a row written by the previous release while
the migration is applied still gets valid values. The status **`Annullato` is `6`** (explicit integer, after `Rifiutato = 5`).

### 21.2 States, who may do what, and what refuses

`Casazen.Core/Suppliers/ServiceRequestStateMachine.cs` is the only table:

| From | To | Who | Endpoint |
|---|---|---|---|
| `Richiesto` | `PresoInCarico` | the supplier | `POST api/service-requests/{id}/take` (optional body: time, end, quote) |
| `Richiesto` | `Rifiutato` | the supplier | `POST …/{id}/reject` (reason required) |
| `PresoInCarico` | `InCorso` | the supplier | `POST …/{id}/start` |
| `PresoInCarico`, `InCorso` | `Completato` | the supplier | `POST …/{id}/complete` (optional body: notes, final amount, extras) |
| `Completato` | `Pagato` | the host for a `Manual` request (refused with 422 `service_request_online_payment` for an `Online` one); the supplier for a payment received outside CasaZen (section 26) | `POST …/{id}/mark-paid`; `POST api/supplier/requests/{id}/payment/offline` |
| `Richiesto`, `PresoInCarico`, `InCorso` | `Annullato` | see below | `POST …/{id}/cancel` (reason required, ≤ 500) |

`Rifiutato`, `Pagato` and `Annullato` are final. **Who may cancel** (`ServiceRequestStateMachine.CanCancel`): the **host** up to and
including `InCorso` (the work in progress); the **supplier** only before the start (`Richiesto`, `PresoInCarico`); **CasaZen** (the
job of section 21.8) only a `Richiesto` request. `POST api/service-requests/{id}/cancel` serves both people and evaluates the policy of the
branch it takes (the supplier it was sent to, else the host with `property.write`); the long-rent route is
`POST api/long-rent/service-requests/{id}/cancel`.

Besides these, two things that are **not** transitions: `POST …/{id}/remind` (the host, section 21.5) and `POST …/{id}/propose-time` with the
host's two answers `…/proposal/accept` and `…/proposal/reject` (section 21.5). Refusals (ProblemDetails `code`, localized `detail` IT/EN):

| Status | `code` | When |
|---|---|---|
| 422 | `service_request_invalid_transition` | the state machine refuses; the message says which action (`ServiceRequestCannotStart`, `…CannotCancel`, `…CannotPropose`, `…CannotRemind`, `…CannotAddPhotos`, plus the old ones) |
| 409 | `service_request_state_changed` | another change got there first (`xmin`, section 8.2); the loser saves nothing and notifies nobody. Valid for every new transition too |
| 409 | `supplier_slot_unavailable` | the time is not one of the slots the supplier offers any more (section 21.3) |
| 422 | `service_request_time_needs_service` | a time without a service of the catalog (or a service with no duration) |
| 422 | `service_request_time_invalid` | the end is not after the start, not whole minutes, over 24 hours, or an end without a start; or a time for a request whose length is unknown |
| 422 | `service_request_time_already_set` | `take` with a time other than the one the request already has |
| 404 / 422 | `supplier_service_not_found` / `service_request_service_unavailable` / `service_request_service_category_mismatch` | the service is not the supplier's (or was deleted) / is a draft or paused / is of another category |
| 422 | `service_request_amount_invalid` / `service_request_final_amount_invalid` | an amount outside 1 cent and 100,000 euro, more than 10 extras, an extra without a label; a declared total lower than its extras |
| 422 | `service_request_remind_too_soon` | the host reminded this request less than 6 hours ago (the message names the hours) |
| 422 | `service_request_no_proposal` | the host answers a proposal the request does not have |
| 422 / 404 | `service_request_photo_invalid` / `service_request_photo_limit_reached` / `service_request_photo_not_found` | section 21.9 |

### 21.3 Time: the host picks a slot, the supplier sets or proposes one

- **From the host.** `POST api/service-requests` (and `api/long-rent/service-requests`) accept `serviceListingId?` and `scheduledStartUtc?`.
  With the time, the end is the start plus the duration of the service, and the slot is checked with the **planner of SP-03**
  (`SupplierSlotPlanner`: weekly hours, time off, closed days, notice, daily maximum, buffer, what the supplier already has, the
  weekdays of the service) **under the supplier's advisory lock `SupplierCalendarSync`**, read after taking it: two requests for the
  same hour never both get it (409 `supplier_slot_unavailable` for the second). The time must be **one of the slots the planner offers**
  (not just inside the hours): a quarter past is refused when the supplier works in steps of an hour. Without the time the request stays
  "to agree", as before; the deadline `ResponseDueAt` is set either way. The estimate is the "from" price of the service when it is
  priced **per job** and not on quote; per hour, per set, per square meter and on-quote services have no estimate (the quantity is unknown).
- **From the supplier at the take.** `POST …/take` takes an optional body `{ scheduledStartUtc?, scheduledEndUtc?, quotedAmountCents? }`; no
  body (or `{}`) takes the request as it is, as before. A request that has no time gets one (the end is the one given, else start +
  duration of its service, else 422 `service_request_time_invalid`) after the same slot check under the lock. A request that already has a
  time keeps it: sending that time again is not a change, another time is 422 `service_request_time_already_set` (the supplier proposes
  it instead). **Outside the working hours there is no slot**: the supplier opens an extra opening in its agenda first (SP-03).
- **Proposing another time** (the semantics chosen for this task). `POST …/{id}/propose-time { startUtc, endUtc?, message? }`, only on a
  `Richiesto` request, stores the proposal (a new proposal replaces the old one), checks the slot like a take does and tells the host. The
  request stays `Richiesto` and **the proposal holds no slot** (an offer, not a reservation). The host answers with
  `POST …/proposal/accept`: it **takes the request on the supplier's behalf at that time** (`PresoInCarico`, `TakenByUserId` = who proposed),
  after checking the slot again under the lock (409 if it went meanwhile, the proposal stays) and only if the supplier is still active;
  `…/proposal/reject` clears the proposal and the request stays `Richiesto`, still waiting for the supplier's answer. A request with a
  proposal is **not** cancelled for no answer (section 21.8) and cannot be reminded. A `take`, a `reject` or a cancellation drops the proposal.
- A cancelled or rejected request **frees its slot**; a completed or paid one is no longer a job. Short-rent and long-rent requests of one
  supplier share its calendar.

### 21.4 Price and completion

- `take` may set `quotedAmountCents` (the agreed price); `EstimatedAmountCents` is the service's "from" price (above).
- `complete` takes `{ notes?, finalAmountCents?, extras?: [{ label, amountCents }] }` (all optional; photos are uploaded first, section 21.9).
  The **reference** is the quote, else the estimate. The final price is: the declared total when `finalAmountCents` is given (it must cover
  the extras, the base line is what the extras leave), else the reference plus the extras; with no reference, no total and no extras there is
  **no final amount**. `price.lines` lists a `base` line (named after the service) and one `extra` line per extra, and they always add up to
  the total. `price.amountCents` is the final amount, else the quote, else the estimate, and `price.basis` says which (`final`,
  `quoted`, `estimated`).
- **Decision D7: a final amount more than the tolerance (20 %, `Suppliers__ServiceRequests__FinalAmountTolerancePercent`) above the reference
  is flagged** (`price.needsCustomerConfirmation`, `FinalAmountNeedsConfirmation`), and the request is `Completato` all the same. **SP-04 gave the
  data and the flag; the host's confirmation is SP-15a** (`POST api/service-requests/{id}/final-amount/confirm`, section 26), which clears the flag and, for a request
  paid inside CasaZen, creates the payment. Exactly 20 % is not over the tolerance.
- **The supplier's closing notes are `CompletionNotes`**, a field of their own (≤ 1000). They no longer replace `Notes`, the host's words.
  The host reads both.

### 21.5 Reminder

`POST api/service-requests/{id}/remind` (long-rent: `api/long-rent/service-requests/{id}/remind`), the host, `property.write`: only on a `Richiesto`
request without a pending proposal, **at most once every 6 hours** (`Suppliers__ServiceRequests__RemindIntervalHours`), recorded in
`LastRemindedAt`. It tells the supplier by email and push and **does not move the deadline**. Two reminders at once: one wins, the other
gets 409 `service_request_state_changed`.

### 21.6 What the supplier sees before the take (decision D9)

Before the take the supplier sees only what it needs to decide, **in the inbox, in the detail, in the legacy host-shaped DTO
(`GET api/service-requests/{id}`, `?view=supplier`, the answers of its own actions) and in the emails and pushes**:

| Field | `Richiesto`, `Rifiutato`, `Annullato` | `PresoInCarico`, `InCorso`, `Completato`, `Pagato` |
|---|---|---|
| `city` (comune), `postalCode`, `scheduledFor`, `scheduledStart/End`, `respondBy`, `price` | yes | yes |
| `clientId`, `clientName` (the host org, D9: "host: nome org") | yes | yes |
| **`propertyName`, `notes`** (host's notes) | **no** (null) — **changed by this task** | yes |
| `address`, `hostContact` (name, email, phone) | no (null) | yes |
| guest of the stay | never | never |

A request cancelled before the take **keeps hiding the property and the notes**. The messages to the supplier name the **comune**, never
the property or the street. `propertyName` of the inbox DTO is now nullable: the console must not assume it.

### 21.7 The inbox, the batch accept, `today` and `checklist` (policy `RequireSupplier`, supplier org from the caller's own link)

`GET /api/supplier/inbox?tab=&service=&comune=&when=&clientId=&status=&from=&to=&page=&pageSize=`:

| Parameter | Values |
|---|---|
| `tab` | `nuove` (`Richiesto`, the earliest `respondBy` first), `programmate` (`PresoInCarico`, `InCorso`, the next job first, the ones to agree last), `da-incassare` (`Completato`), `archivio` (`Pagato`, `Rifiutato`, `Annullato`), latest activity first. Any case, `_` or `-`. When it is sent, `status` is ignored. Another value: 400 `validation_error` (`SupplierInboxTabInvalid`) |
| `service` | the id of a service of the catalog, or a category code |
| `comune` | ISTAT code of the property's comune, or its name (case-insensitive, exact) |
| `when` | `oggi`, `settimana` (the next 7 days), `mese` (this month): the **day of the job** in Europe/Rome, the scheduled start, else the check-out day of the stay (a request with neither never matches). Another value: 400 (`SupplierInboxWhenInvalid`) |
| `clientId` | the host org (the `clientId` of the items) |
| `status`, `from`, `to`, `page`, `pageSize` | as in section 11.2 (`history` now includes `Annullato`) |

Each item adds `price`, `scheduledStart`, `scheduledEnd`, `source` (`casazen`; `showcase` is reserved for SP-10), `respondBy` (only while `Richiesto`),
`startedAt`, `clientId`/`clientName`, `serviceListingId`/`serviceName`, `proposal`, `cancelledAt`/`cancelledBy`/`cancellationReason`,
`completionNotes` and `workPhotos`; the detail adds `history`.

`POST /api/supplier/inbox/accept { ids: [up to 20] }` takes the requests one by one **as a `take` without a time** (the same rules, terms of
service and active-supplier check) and answers `{ results: [{ id, accepted, status, code, message }], accepted, failed }` with the
**localized** message of each refused row; a request of another supplier or an unknown id answers `service_request_not_found`, as if it did not
exist. Over 20 ids, none, or an empty body: 400.

`GET /api/supplier/today`: `date` and `timeZone` (Europe/Rome), `jobs` (the requests taken, started, completed or paid whose day is today, by time),
`newRequests` (up to 10, by deadline) with `newRequestsTotal`, `earnings` (the month's final amounts of the completed and paid jobs, the amount still to
collect and its jobs, `estimated: true` **until the payments of SP-15 exist**) and `averageResponseMinutes` (`TakenAt − CreatedAt` of the requests
taken in the last 90 days, null when there are none).

`GET /api/supplier/checklist`: `profileCompletionPercent`, `profileComplete`, `activeServices`, `hoursConfigured` and `hoursConfiguredAt`,
`showcasePublished` (an `Active` supplier with its address), `firstRequestAnswered` (a request taken or rejected) and `paymentsActive`, which is
**always `null` for now**: the payments of the suppliers are not available yet, and "not yet" is not a "no".

### 21.8 The request nobody answers: automatic cancellation (decision D8)

A host's request gets `ResponseDueAt = creation + 120 minutes` (`Suppliers__ServiceRequests__HostResponseMinutes`; the 180 minutes of the showcase
are SP-10's). The recurring job **`service-request-auto-cancel`** (every 10 minutes UTC, `ServiceRequestAutoCancelJob` →
`ServiceRequestAutoCancelService`) moves to `Annullato` the `Richiesto` requests past their deadline that have no proposal: `CancelledBy = System`,
`CancellationReason = NoResponse`, and it tells the host ("nessuna risposta") and the supplier. It is **idempotent** (a cancelled request leaves the
set it reads; the change is saved under the `xmin` check, so a supplier that takes the request at the same moment wins and the run counts a conflict),
it runs one at a time (a PostgreSQL session lock, scope `ServiceRequestAutoCancelRun` 1_310, on top of Hangfire's), and a run handles at most **500**
requests, oldest deadline first.

It is **behind the flag `Features__SupplierRequestAutoCancel`, off by default**: it changes what happens to the requests that exist. With the flag off the
job is not scheduled (an earlier schedule is removed) and the service refuses to run if triggered by hand; nothing is ever cancelled by time. Requests
created **before** this task have no `ResponseDueAt` and are never cancelled by it. **Before turning it on** check that the console and the mobile app
know `Annullato` (section 21.11), and expect that the first run cancels every open request that is already overdue (up to 500 every 10 minutes until
none is left).

### 21.9 Photos of the work

`POST api/service-requests/{id}/photos` (multipart, field `photos`; the supplier, while the request is `PresoInCarico` or `InCorso`): JPEG, PNG or WebP
checked **on their content**, up to 10 MB each and **6 per request**, all or none (400 for no file, 422 `service_request_photo_invalid` for a bad one,
422 `service_request_photo_limit_reached`). They are stored in the **private** bucket under `service-requests/{requestId}/photos/` (never served as
static files) and listed in `workPhotos[]` with a `url`. They are read, one at a time, at `GET api/service-requests/{id}/photos/{photoId}` by the supplier it
was sent to and by the host (the host of a long-rent request at `api/long-rent/service-requests/{id}/photos/{photoId}`), with `Cache-Control: private,
no-store`; another org gets 404. A save that fails removes the objects it stored.

### 21.10 Emails and pushes

Queued **after** the change is saved, by the winner of a race only; a queue that fails never undoes the change. Italian and English.

| Event | To | Email | Push `type` |
|---|---|---|---|
| New request | the supplier | comune, time, amount, link to the inbox | `service-request-created` |
| Taken, started, completed (with final amount, the over-quote note and the supplier's notes), rejected | the host | status email | `service-request-taken`, `-started`, `-completed`, `-rejected` |
| Cancelled by the supplier / by CasaZen (no answer) | the host | status email | `service-request-cancelled` |
| Cancelled by the host / by CasaZen (no answer) | the supplier | comune, reason | `service-request-cancelled` |
| Reminder | the supplier | reminder | `service-request-reminder` |
| Another time proposed | the host | the interval and the message | `service-request-time-proposed` |
| Proposal accepted / turned down | the supplier | answer | `service-request-proposal-accepted` / `-rejected` |
| Marked as paid | the supplier | unchanged | `service-request-paid` |

A status that has no message (a new request, a paid one, a request the host cancelled itself, **a status the code does not know**) sends
**nothing**, and it is not an error: before this task `EmailTemplates.ServiceRequestStatusChanged` threw for any status other than taken, completed and rejected, and the error was only logged.

### 21.11 Mobile app

Statuses travel **by name**. **`Annullato` is new** and a client that does not know it may fail to read the request or show it badly; the new push
`type`s above are new too. The mobile repository is not in this task's worktree and was not checked: verify it before turning
`Features__SupplierRequestAutoCancel` on (and before a host cancels in production).

### 21.12 Configuration

Section `Suppliers:ServiceRequests` (validated at startup, `appsettings.json` has the defaults): `Suppliers__ServiceRequests__HostResponseMinutes` (120,
10 to 4320), `…__FinalAmountTolerancePercent` (20, 0 to 100), `…__RemindIntervalHours` (6, 1 to 72). Flag: `Features__SupplierRequestAutoCancel`
(`feature-flags.md`, `deploy-checklist.md` § 2.11). Hangfire: `hangfire.md`.

### 21.13 After a deploy

- [ ] Migration `AddServiceRequestSchedule` applied (columns of section 21.1; index `IX_ServiceRequests_SupplierOrgId_ScheduledStartUtc`; checks
      `CK_ServiceRequests_ScheduledInterval`, `…_ProposedInterval`, `…_Amounts`; foreign key to `SupplierServiceListings` `ON DELETE SET NULL`).
- [ ] `GET /api/public/features` has `supplierRequestAutoCancel: false`; `service-request-auto-cancel` is **not** in the Hangfire recurring jobs.
- [ ] As a host (test environment): create a request with `serviceListingId` and `scheduledStartUtc` of a free slot → 201 with `scheduledStart/End`,
      `respondBy` and `price`; the same slot again → 409 `supplier_slot_unavailable`.
- [ ] As the supplier: `GET /api/supplier/inbox?tab=nuove` lists it with the comune and the price and **without** the property name and the notes;
      `POST …/take` with no body → 200 and now the property name and the notes are there; `start`, `complete` with an extra → 200 and `price.lines`.
- [ ] As the host: `cancel` with a reason → 200; `remind` twice → the second is 422 `service_request_remind_too_soon`.
- [ ] `GET /api/supplier/today` and `GET /api/supplier/checklist` answer for a supplier with and without requests (`paymentsActive: null`).
- [ ] An old request (created before the deploy) still opens everywhere with no time and no price.

## 25. The supplier's Stripe Connect account — SP-14

Redesign wave task SP-14 (branch `feature/rd-supplier-payments-account`, backend only: no screen yet, the console screens are
SP-16), stacked on SP-02. Gap report 05 §4.3, decisions D2 (direct charge on the supplier's own Stripe account), D3 (commission,
SP-15) and D11 ("Verificato"). The flow, the endpoints, the Stripe settings and the checks are in
[`stripe.md`](stripe.md) § "Connect onboarding of the suppliers (SP-14)"; this section is what is specific to the suppliers.

### 25.1 What a supplier can do now

| | |
|---|---|
| Connect its Stripe account (Express) | `POST api/supplier/payments/onboarding-link` (creates the account when missing) → the supplier completes the form **on Stripe** and comes back to `/app/supplier/settings?stripe_return=1` (`?stripe_refresh=1` when the link expired). CasaZen stores no identity document and no bank detail, only whether Stripe enabled charges and payouts and the names of the fields Stripe still needs |
| See the state | `GET api/supplier/payments/account` (database) or `…?refresh=true` (Stripe first): `hasAccount`, `chargesEnabled`, `payoutsEnabled`, `detailsSubmitted`, `requirementsDue`, `canReceivePayments`, `verified`, `verificationMissing` |
| Open its Express Dashboard | `POST api/supplier/payments/dashboard-link`: balance, payouts and bank details on Stripe. Needs an existing account (422 `supplier_payments_not_ready` otherwise) |

This section is the account only. The payment, the commission and the payer's page of a completed request are SP-15a (section 26); the webhook that
records the money as received, the refunds and the admin tools are SP-15b (section 26.7); the earnings pages are SP-16 and SP-17. "Segna pagato" by the host (`mark-paid`, section 15)
stays the way a `Manual` request becomes paid.

### 25.2 Rules

- The routes are behind `Features:SupplierOnlinePayments` (**off by default**: 404 before authentication) and the policy
  `RequireSupplier`. The org is the caller's own supplier link, never provisioned; a **suspended** or still `Pending` supplier can
  connect its account and read its state like it can edit its profile (section 14.2), but it is not "Verificato".
- **"Verificato"** (D11, `SupplierVerification`): `Active` profile + a linked account with charges **and** payouts enabled + a VAT
  number (`SupplierProfiles.VatNumber`, not blank). It is a read-only rule: the activation (section 16) is **not** changed and does
  not wait for Stripe. `verificationMissing` lists `profile_not_active`, `payments_not_enabled`, `vat_number_missing`. The public
  showcase does not show it yet and stays `noindex` (section 18).
- One account per supplier org, created under the advisory lock `OrgConnectAccount` with the Stripe idempotency key
  `connect-account:{orgId}`, exactly like the host's. A host that is also a supplier has two accounts, one per org.
- The webhook `account.updated` of a supplier's account is processed by the existing handler whatever the flag says, and updates only
  the org that holds that account id.
- Follow-up for whoever merges SP-04 (the supplier checklist, a different branch) after this one: its `paymentsActive` can read
  `SupplierVerification.CanReceivePayments` through `ISupplierPaymentsAccountService`.

### 25.3 After a deploy

- [ ] `GET /api/public/features` has `supplierOnlinePayments: false` (leave it off in production until the supplier legal texts, D-C,
      and the tax treatment of the commission, D4, are approved).
- [ ] Test environment with `Features__SupplierOnlinePayments=true` and Stripe test keys: the checks of `stripe.md` § "Connect onboarding
      of the suppliers (SP-14)" → Verification, point 2.
- [ ] With the flag off, `GET /api/supplier/payments/account` answers 404 `not_found` also with a valid supplier token.

## 26. Paying the supplier inside CasaZen, with the CasaZen commission — SP-15a

Redesign wave task SP-15a (branch `feature/rd-supplier-payments`, backend only: the payer's page and the console screens are frontend
tasks), stacked on SP-14 and SP-04. Gap report 05 §4.3, decisions D2 (a direct charge on the supplier's own Stripe account, CasaZen holds
no funds), D3 (the commission), D5 (the manual flow stays, as an exception), D7 (an amount above the quote) and D24 (the emails). The
charge model, the endpoints, the statuses and the Stripe checks are in [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)";
this section is what is specific to the suppliers' requests. **SP-15b** (the webhook that records a payment as paid, the sync, reminder and
pending-request jobs, the refunds, the admin tools and the monthly export of the commission) is the pull request stacked on this one: 26.7.
**Keep `Features__SupplierOnlinePayments` off in production** until the legal texts and the tax treatment of the commission are approved.

### 26.1 What changes for a request

| Moment | What happens |
|---|---|
| The supplier takes the request (`take`, or the host accepts the time it proposed) | `PaymentMode` is fixed (`ServiceRequests.PaymentMode`, `paymentMode` in the answers of the host and of the supplier): `Online` if the flag is on **and** the supplier's Stripe account is linked with charges and payouts enabled, else `Manual`. A request that exists before SP-15a is `Manual` |
| The supplier completes it | An `Online` request with a final amount of at least `SupplierPayments__MinAmountCents` gets its payment (`ServiceRequestPayments`, status `Requested`, with the commission snapshot) in the same save, and the contact address of the host org gets the email with the link. With no final amount (or one below the minimum), or if the flag was switched off since, the request is completed as `Manual`: nothing is stuck and the host marks it paid as before |
| The final amount is above the quote by more than the tolerance (D7, section 21.4) | The host confirms first: `POST api/service-requests/{id}/final-amount/confirm` (long-rent twin under `api/long-rent/service-requests`); it clears `price.needsCustomerConfirmation`, keeps `FinalAmountConfirmedAt`, creates the payment and sends the link. Until then there is no payment and no link, and `payment-request` is 422 `service_payment_amount_unconfirmed`. Confirming twice is not an error |
| The supplier cannot be paid yet, or the host org has no email | The payment exists but is **pending** (no link, nothing sent). The supplier asks again when it can |
| The payer pays | With the emailed link (`/service/pay/{paymentId}?token=…`, which calls `api/public/service-payments/*`) or, signed in, from the host console (`POST api/service-requests/{id}/payment-session`): a direct charge on the supplier's account with the commission as the application fee. The payment becomes `Paid` (and the request `Pagato`) with the webhook (26.7), only if what Stripe reports is what was asked for |
| The payer did not pay | `POST api/supplier/requests/{id}/payment-request` sends a reminder with a new link (the old one stops working), at most once a day (422 `service_payment_request_too_soon`), and only while the flag is on |
| The supplier was paid outside CasaZen | `POST api/supplier/requests/{id}/payment/offline` (D5): the request becomes `Pagato`, a payment still waiting is withdrawn (its PaymentIntent canceled) and a payment **without commission** is kept (`PaidVia = Offline`); the history of the request credits it to the supplier
(`ServiceRequests.PaidBy`); the host is emailed (with the reason). For an `Online` request the `reason` is mandatory (422 `service_payment_offline_reason_required`) and a warning log marks the exception. A payment already paid, or being processed by Stripe, makes it a 409 |

### 26.2 Rules

- **The commission is configuration, never code** (D3): `SupplierPayments__CommissionPercent` (provisional 10, `deploy-checklist.md` § 2.8) and,
  per supplier, `SupplierProfiles.CommissionPercentOverride` (an admin sets it, 26.7). It is computed once, when the payment is
  created, and kept on the payment (`CommissionPercent`, `ApplicationFeeCents`, `NetCents`); the payer never sees it, the supplier sees
  "prezzo, commissione CasaZen, netto per te, prima delle commissioni di Stripe" and no text promises a payout time. **Never a commission on
  the guests' bookings or on the rent** (`StripeServiceApplicationFeeTests`, `ApplicationFeeArchitectureTests`).
- **The host cannot mark an `Online` request as paid**: `mark-paid` (section 15.2) is 422 `service_request_online_payment`. The supplier
  declares a payment received outside CasaZen, with a reason. Every `Manual` request works as before (section 15.2).
- **The flag stops only the creation** of payments (a request is taken as `Manual`; `payment-request` is a 404 before authentication). The
  payer's page and sessions, the host's session and the supplier's offline record are **not** behind it: money in flight, and the way out
  when online payments are not available.
- **A suspended supplier, or one that has not accepted the current Terms, performs no action**, `payment-request` and `payment/offline`
  included (SU-12, SU-05); the payer can still pay a payment that exists.
- **One live payment per request** (unique partial index: every status but `Canceled`). Whatever creates, reuses or drops a PaymentIntent,
  re-issues a link (the supplier's request or reminder) or records an offline payment runs under the advisory lock `ServiceRequestPayment`
  (key: the request id), with Stripe idempotency keys; the first link, issued with the completion or the host's confirmation, takes no lock:
  the `xmin` check of the request and the unique index decide, and the loser gets a 409 (`service_request_state_changed`,
  `service_payment_state_changed`), never a 500.
- **The link** is a 256-bit token whose SHA-256 is stored, valid `SupplierPayments__PaymentLinkValidityDays` (30) from the email that carries it;
  a wrong id, a wrong token, a payment with no link and an expired link answer the same 404 `service_payment_link_invalid`.
- **Tenancy.** `ServiceRequestPayments` is not `ITenantOwned` (two parties and an anonymous payer, like the request itself): every read has an
  explicit predicate (the payment id with its token on the public routes, the caller's supplier org or host org elsewhere), it is in the
  allow-list of `TenantQueryFilterArchitectureTests` and `ServiceRequestPaymentTenancyTests` guards it.
- **A request with no quote and no estimate** has nothing to compare the final amount with (SP-04), so decision D7 never asks the host to
  confirm it: the link goes out at once with the amount the supplier declared. The payer chooses to pay or not. **To be confirmed by the PO**
  (require the confirmation for any amount of an online request that had no reference?).
- **Open points, not decided by the code**: the VAT on the commission and DAC7 (`[CONSULENTE FISCALE]`, D4: `FeeVatMode` and `FeeVatCents`
  exist and stay empty) and the drafts of the legal texts `fornitori` and `servizi` (LG-01), written for another model: align them with D2
  before the flag goes on.

### 26.3 Endpoints at a glance

| Endpoint | Who | Behind `SupplierOnlinePayments` |
|---|---|---|
| `POST api/public/service-payments/{id}`, `…/{id}/payment-session` | the payer, with the token of the link in the body (rate limited per IP) | no |
| `POST api/service-requests/{id}/payment-session`, `POST api/long-rent/service-requests/{id}/payment-session` | the host (`property.write`) | no |
| `POST api/service-requests/{id}/final-amount/confirm`, `POST api/long-rent/service-requests/{id}/final-amount/confirm` | the host (`property.write`) | no |
| `POST api/supplier/requests/{id}/payment-request` | the supplier | **yes** |
| `POST api/supplier/requests/{id}/payment/offline` | the supplier | no |
| `GET api/admin/supplier-payments`, `GET api/admin/service-payments/{id}`, `POST api/admin/service-payments/{id}/refund`, `GET`/`PUT api/admin/suppliers/{orgId}/commission`, `GET api/admin/supplier-payments/export?month=yyyy-MM` | a platform admin (SP-15b, 26.7) | no |

The request answers carry `paymentMode`; the payment itself (status, split) is returned only by the two supplier routes and the admin
routes. The earnings page and the payments list of the supplier come with SP-16.

### 26.4 Configuration

Section `SupplierPayments` (validated at startup, `appsettings.json` has the defaults): `SupplierPayments__CommissionPercent` (**required**,
0 to 50, provisional 10), `…__LateAfterDays` (7) and `…__ReminderDays__0`, `__1` (2 and 7; both used by the daily job of SP-15b), `…__PaymentLinkValidityDays`
(30), `…__MinAmountCents` (50), and `…__CommissionVatPercent` (SP-15b, optional, 0 to 100, **empty**: only repeated in the commission export). Settings: `deploy-checklist.md` § 2.8. Flag: `Features__SupplierOnlinePayments` (`feature-flags.md`, `deploy-checklist.md` § 2.11). Emails:
`EmailTexts.resx` (`ServicePaymentRequest`, `ServicePaymentReminder`, `ServicePaymentReceived`, `ServicePaymentOfflineRecorded`; SP-15b: `ServicePaymentFailed`,
`ServicePaymentRefundedPayer`, `ServicePaymentRefundedSupplier`, `ServicePaymentAdminAlert`), see `email.md`.

### 26.5 For the tasks stacked on this one

- **SP-15b** (26.7): the webhook `kind=service-charge` (before the generic `payment_intent.succeeded`, comparing account, amount, currency and
  fee with the payment), `SendPendingPaymentRequestsJob` (also from `account.updated`, BE-SP14-2), the reminders, the refunds, the admin tools
  and the export. Done.
- **Frontend**: the page `/service/pay/:paymentId` (it only needs the two public routes), the host's "Paga ora" and the "Conferma importo"
  button, the supplier's "Richiedi pagamento" / "Pagato fuori da CasaZen" and the split of the amounts; the admin screens of SP-15b (the list and
  its `NeedsReview` queue, the detail, the refund, the commission of a supplier, the export).
- **Checklist** (SP-04): `paymentsActive` is still always `null` (BE-SP14-3).

### 26.6 After a deploy

- [ ] Migration `AddSupplierPayments` applied (columns `ServiceRequests.PaymentMode` default 0 = `Manual`, `FinalAmountConfirmedAt` and `PaidBy`,
      `SupplierProfiles.CommissionPercentOverride`; table `ServiceRequestPayments` with its checks and the two unique partial indexes).
- [ ] `SupplierPayments__CommissionPercent` is the figure the product owner decided (the committed 10 is provisional).
- [ ] `GET /api/public/features` has `supplierOnlinePayments: false` in production until the legal texts and the tax
      treatment of the commission are approved.
- [ ] Test environment with `Features__SupplierOnlinePayments=true`, Stripe test keys and a supplier with a ready test account: take a
      request (`paymentMode: Online`), complete it with an amount, open the link of the email and `POST …/payment-session` (a client secret;
      the PaymentIntent on the supplier's account carries the application fee), pay it with the test card
      (`stripe.md` § "Services of the suppliers (SP-15)" → Verification, point 2). With the flag off a new request is `Manual`.
- [ ] An old request (created before the deploy) still opens everywhere, with `paymentMode: Manual`, and "Segna pagato" still works.

### 26.7 What SP-15b adds: the webhook, the jobs, the refunds and the admin tools

SP-15b (branch `feature/rd-supplier-payments-webhook`, backend only, stacked on SP-15a) closes the chain. The model of the charge, the events,
the jobs, the refunds and the export are in [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)", the schedule in
[`hangfire.md`](hangfire.md) § 13; this is what changes for a request.

| Moment | What happens |
|---|---|
| The payer pays | `payment_intent.succeeded` on the Connect endpoint (or `service-payment-sync`, if the event was lost): the payment is `Paid` (`PaidVia = Stripe`) and the request `Pagato` (`PaidBy = Host`) **only if** the account, the PaymentIntent, the amount received, the currency and the commission are the ones of the payment. The supplier gets "Pagamento ricevuto" (price, commission, net), once |
| What arrived is not what was asked for | The payment becomes `NeedsReview` (`FailureCode = review:…`), the request stays `Completato` (**never** `Pagato`), an error is logged and the active platform admins are emailed. Nothing is refunded by itself: an admin looks at the charge on the Stripe Dashboard. The same email goes out when a charge succeeds for a payment the supplier had already withdrawn (a payment recorded as received outside CasaZen) |
| A debit that was in flight fails | `Failed`, and the payer gets "Pagamento non riuscito" with a **new** link (the old one stops working) |
| The payer does not pay | `service-payment-reminders` (daily, 07:30 UTC, flag on) sends the reminder at +2 and +7 days from the first request, while the payment has had fewer than three emails with a link, never two in 24 hours, and flags the payment late after `SupplierPayments__LateAfterDays` (7). The supplier can still ask with `payment-request` |
| A supplier becomes ready (`account.updated`) | `SendPendingPaymentRequestsJob(orgId)` is queued after the commit and sends the pending first requests of that supplier; the daily job does it too, as a catch-up |
| A refund | Only a platform admin (`POST api/admin/service-payments/{id}/refund`), in full or in part, for a payment paid through Stripe. It is created on the supplier's account (paid from its balance) with `refund_application_fee=true`: **the commission goes back in full for a full refund, in proportion for a partial one**. The payer and the supplier are emailed; the payment becomes `PartiallyRefunded` or `Refunded` and the request stays `Pagato`. A payment recorded as received outside CasaZen is not refundable here |
| A dispute (`charge.dispute.created`) | An error in the log and an email to the platform admins; nothing changes in CasaZen. The admin answers on the Stripe Dashboard |

**Rules**

- **Money in flight is not behind the flag.** The webhook, `service-payment-sync` and the admin tools work with `Features__SupplierOnlinePayments`
  off; the daily job then only flags the late payments and sends nothing.
- **The webhook routes on `metadata.kind = service-charge` before the generic `payment_intent.succeeded` case** and never settles a request on
  its own word: it compares what Stripe reports with the snapshot of the payment. A source-order test and the unit tests of the handler guard it.
- **Order and repetition do not matter.** The state is derived from what the event says and from what the payment already is; the claim of the
  event id and the update are one transaction; every change runs under the advisory lock `ServiceRequestPayment` (key: the request id).
- **A refund is reserved before Stripe is called** (`ServiceRequestPaymentRefunds`, `Pending`, counted as refunded for the limit), with the key
  `service-charge-refund:{payment}:{n}`: two admins at once, a retry or a lost answer never refund twice. The table is not `ITenantOwned` (it
  belongs to a payment with two parties) and `ServiceRequestPaymentRefundTenancyTests` allows only the payments service and the admin service.
- **The commission of a supplier** (`GET`/`PUT api/admin/suppliers/{orgId}/commission`): its own percentage, 0 to 50 (0 is a free period),
  optionally until a date, with a mandatory reason; it applies to the payments created afterwards and the change is in the audit trail of the
  supplier (`CommissionChanged`, `GET api/admin/suppliers/{orgId}/audit`).
- **The monthly export** (`GET api/admin/supplier-payments/export?month=yyyy-MM`) is what the accountant needs to invoice the commission by hand:
  one line per payment and per refund, with the supplier's P.IVA, the gross amount and the VAT rate only if `SupplierPayments__CommissionVatPercent`
  is set (empty by default). **VAT on the commission and DAC7 stay open** (`[CONSULENTE FISCALE]`, D4); nothing is sent to the tax authority.
- **Open points**: a payment in `NeedsReview` is resolved on the Stripe Dashboard (CasaZen has no "accept" or "refund this charge" action for it yet,
  follow-up); there is no evidence-submission flow for disputes; who bears a refund or a lost dispute when the supplier's balance is short depends
  on the liability settings of the Connect accounts (to be confirmed with Stripe); and the restricted key must be tried in test mode for the
  application fee and the refunds (`stripe.md` → Stripe settings, Verification).

**After a deploy of SP-15b**

- [ ] Migration `AddSupplierPaymentRefunds` applied (table `ServiceRequestPaymentRefunds` with its two checks and three unique indexes;
      `SupplierProfiles.CommissionOverrideUntil` and `CK_SupplierProfiles_CommissionOverrideUntil`).
- [ ] The Connect webhook endpoint of the environment sends the events listed in [`INFRA.md`](../INFRA.md) § Stripe, in particular `charge.dispute.created`.
- [ ] `service-payment-sync` (`*/15`) and `service-payment-reminders` (07:30) are among the recurring jobs (`GET api/admin/jobs`).
- [ ] Test environment (flag on, Stripe test keys): pay a request and see it `Pagato` with the receipt to the supplier; refund half and then the rest from
      the admin tool and check in the Stripe Dashboard that the refund came out of the supplier's balance and that the commission came back; set a
      supplier's own commission and see the next payment carry it; download the export (`stripe.md` → Verification, points 2 and 3).
- [ ] Production: `Features__SupplierOnlinePayments` still off until the legal texts and the tax treatment of the commission are approved.

## Known limits (other tasks)

- A supplier who lost the claim token cannot register again with the same email (409 `supplier_email_taken`): the
  claim without token links the existing profile once the Auth0 email is verified (section 2.2). The web pages show
  the localized message of the 409; a dedicated "link it" button for that code is a frontend follow-up.
- Service requests: the host timeline, rejection reason and "paid" confirmation are in section 14 (SU-09), but only on
  the web: the app's supplier choice (today the first result) is MO-10 and its error states MO-07. `chargeToGuest` is
  still always refused, also for long-rent (open product point).
