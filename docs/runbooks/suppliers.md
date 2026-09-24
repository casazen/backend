# Runbook: supplier invites and self-serve registration

Task SU-01 (audit defects A4-03, A4-04, A4-21, A4-24). The code is in place; the product owner sets the pilot comuni
(section 2) and checks the Auth0 and web app URLs (section 3) on each environment.

## 1. How a supplier joins

| Path | Steps | Code |
|---|---|---|
| **Admin invite** | Admin: *Invita fornitore* (`POST /api/admin/suppliers/invite`) → email with the link `{App__PublicSiteBaseUrl}/register?inviteToken=…` → the web app page reads the invite (`POST /api/suppliers/invites/lookup`) and shows email and comune, locked → **Crea account / Accedi** starts the Auth0 login of the web app (SDK: PKCE and `state`, `login_hint` = invited email) and comes back to the same page → the supplier enters business name and phone → `POST /api/suppliers/register` (signed in) → the account is linked to the new supplier org and gets the Auth0 role `Supplier` (additive) → **Completa il profilo** opens the activation wizard | `SuppliersController`, `SupplierService.RegisterAsync`, web `src/pages/supplier-register-page.tsx` |
| **Self-serve** | `/register` without token: email, comune (list of pilot comuni), business name, phone. Anonymous, or signed in (email locked to the account). Anonymous users then create their Auth0 account with the same email | same |

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
| `supplier_self_serve_unavailable` | No pilot comune configured (section 2) |
| `supplier_comune_not_pilot` | Self-serve for a comune outside the pilot list |

`429 rate_limited` when the client exceeds the limits of section 4.

## 2. Pilot comuni (Railway variables, per environment)

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

## 3. URLs

| Setting | Value |
|---|---|
| `App__PublicSiteBaseUrl` (Railway) | Web app URL of the environment: base of the invite link (`/register?inviteToken=…`), no fallback domain ([`email.md`](email.md)) |
| Auth0 SPA application → Allowed Callback URLs, Allowed Web Origins, Allowed Logout URLs | The web app origin(s) of the environment (unchanged: the SDK returns to the origin and the app then navigates back to the invite page from `appState`). No `/callback` URL is needed; remove the API domain if it was added for the old backend page |

## 4. Rate limits (FD-10, per client IP, [`proxy-ip.md`](proxy-ip.md))

| Endpoint | Policy | Default |
|---|---|---|
| `POST /api/suppliers/register` | `PublicRegistration` (bucket shared with `POST /api/auth/register`) | 5 / 10 min |
| `POST /api/suppliers/invites/lookup`, `GET /api/suppliers/registration-options` | `PublicRead` | 120 / min |

The invite page calls `lookup` once per load and `register` once per submit: an invited supplier who logs in and
submits stays far below the limits.

## 5. After a deploy

- [ ] Section 2 variables set (or self-serve intentionally off); `registration-options` answers as expected.
- [ ] Admin invite to a mailbox you control: the email links to the **web app** `/register?inviteToken=…`, shows the
      comune (name when configured), the expiry in Italian time and no promise of automatic activation.
- [ ] Open the link signed out: invite email and comune shown and locked; *Crea account* opens Auth0 signup with the
      email pre-filled and comes back to the invite page; submit → *Completa il profilo* opens the activation wizard.
- [ ] Open the same link again: "invito già usato". Open it truncated: "invito non valido".
- [ ] Signed in with another account on a fresh invite: the page asks to switch account; the API answers
      `supplier_invite_email_mismatch`.

## Known limits (other tasks)

- After an **anonymous** self-serve registration and Auth0 signup the account is linked to the profile only by email
  when a supplier endpoint is first called, and the web app may still send the user to the host onboarding (A4-02,
  A4-23: task SU-02). Invited suppliers and signed-in self-serve registrations are linked at registration.
- The activation requirements (only the ToS today) are task SU-05.
