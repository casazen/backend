# Runbook: supplier invites, self-serve registration and claim

Task SU-01 (audit defects A4-03, A4-04, A4-21, A4-24) and SU-02 (claim after login, supplier onboarding, no link by
unverified email: A4-02, A4-23, A1-13). The code is in place; the product owner sets the pilot comuni (section 3),
checks the Auth0 claims (section 2.3) and the web app URLs (section 4) on each environment.

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

`429 rate_limited` when the client exceeds the limits of section 5.

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
| Without token | Only when Auth0 says the account email is **verified** (section 2.3): links the **one** supplier profile with that email that no account holds (a lost token, or a registration made before SU-02, which has no token). Several such profiles: 409 `supplier_claim_ambiguous` (admin fixes it) |
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

## Known limits (other tasks)

- The admin repair `fix-orphaned` still matches users and profiles by email (task SU-14, "fix-orphaned sicuro").
- Supplier emails are not unique yet (SU-14): a supplier who lost the claim token and registers again gets a second
  profile; the first one stays `Pending`, invisible to hosts. With a verified email the claim without token links
  the old one, as long as it is the only unclaimed profile for that email.
- A supplier-only user who then completes the host onboarding keeps using the supplier org as `User.OrgId`
  (A1-40, task PL-05).
- The activation requirements (only the ToS today) are task SU-05.
