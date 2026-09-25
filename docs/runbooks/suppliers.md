# Runbook: supplier invites, self-serve registration, claim and service requests

Task SU-01 (audit defects A4-03, A4-04, A4-21, A4-24) and SU-02 (claim after login, supplier onboarding, no link by
unverified email: A4-02, A4-23, A1-13). Section 9: one profile per email and the admin repair `fix-orphaned` (SU-14,
A4-22). The code is in place; the product owner sets the pilot comuni (section 3),
checks the Auth0 claims (section 2.3) and the web app URLs (section 4) on each environment. Section 7: what a
service request is tied to (task SU-07, decision D2).

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
`/api/supplier/*` request) now use only the account's own link (`User.SupplierOrgId`, or `User.OrgId` of a supplier
org); a Supplier role given by hand without any link still gets a new empty profile, never someone else's. A
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

- `GET /api/users/me` returns `supplierOrgId` (also for a supplier-only user whose `orgId` is the supplier org). The
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
- The code is compared with the form value trimmed and case-insensitive, and stored on the supplier profile
  (`SupplierProfiles.ComuniJson`) exactly as configured. Use the **same code format as the admin invites** of that
  comune. Matching with the hosts' properties still goes through `ItalianComuneRegistry` until SU-04 introduces the
  ISTAT registry and picker (A4-12): a code that the registry does not know matches only a property whose city is
  written exactly the same way.
- Invite emails show "Name (code)" when the invite's comune is a configured pilot comune, otherwise only the code.
  `ItalianComuneRegistry` is deliberately not used for names: it knows 12 comuni and maps the cadastral code `F205`
  to Firenze, while `F205` is Milano (A4-12, fixed by SU-04).
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
| `POST /api/suppliers/register` | `PublicRegistration` (bucket shared with `POST /api/auth/register`) | 5 / 10 min |
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
   - service requests (`ServiceRequests.SupplierOrgId`, any state) and legacy supplier jobs move to the keeper;
   - availability days move; a day the keeper already has keeps the keeper's value (the duplicate's is dropped);
   - the duplicate's categories and comuni the keeper lacks are appended (bio, photos, VAT, calendar settings of the
     duplicate are not copied: the keeper's profile is the one in use);
   - accounts: `SupplierOrgId` = duplicate → keeper; a supplier-only account (`OrgId` = duplicate, no `SupplierOrgId`)
     gets `SupplierOrgId` = keeper;
   - the duplicate profile is deleted; its org too, after moving `User.OrgId` and push devices to the keeper, **unless
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
(`keeperOrgId`, `duplicateOrgId`, `serviceRequestsMoved`, `supplierJobsMoved`, `availabilityDaysMoved`,
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

## 10. Calendar sync (iCal) — SU-15

The supplier's iCal feed (activation wizard and *Sincronizza calendario*) is synced in Hangfire, never inside the
request: saving the URL answers 202 `Syncing` and queues the first sync, "Sincronizza ora" queues another one, the
15-minute job `ical-supplier-sync` also covers suppliers still in activation (`Pending`). The sync frees only the days
of the feed (`SupplierAvailability.Source`), never those the supplier set by hand. API, rules, migration of the
existing days and checks: [ical.md](ical.md#supplier-calendars-su-15).

## Known limits (other tasks)

- A supplier who lost the claim token cannot register again with the same email (409 `supplier_email_taken`): the
  claim without token links the existing profile once the Auth0 email is verified (section 2.2). The web pages show
  the localized message of the 409; a dedicated "link it" button for that code is a frontend follow-up.
- A supplier-only user who then completes the host onboarding keeps using the supplier org as `User.OrgId`
  (A1-40, task PL-05).
- The activation requirements (only the ToS today) are task SU-05.
- Service requests: the supplier still sees no address, dates or host contact (SU-08); host timeline, rejection
  reason and "paid" confirmation are SU-09; the app's supplier choice (today the first result) is MO-10 and its
  error states MO-07. `chargeToGuest` is still always refused, also for long-rent (open product point).
