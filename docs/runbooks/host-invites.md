# Host invites (invitation of new independent hosts)

A platform admin or a host invites a **new independent host** (a new host org, not a member of an existing one) by email. The
invite grants **no role and no privilege**: the person signs up with the normal self-service flow. The invite only binds the
email, leads to the signup page and records who invited (attribution).

## Endpoints

| Who | Route |
|---|---|
| Admin (`AdminOnly`) | `POST/GET api/admin/host-invites`, `POST api/admin/host-invites/{id}/resend`, `DELETE api/admin/host-invites/{id}` |
| Host (`OrgMembersManage`: owner and administrators of the org, after onboarding) | `POST/GET api/hosts/me/invitations`, `GET .../quota`, `POST .../{id}/resend`, `DELETE .../{id}` |
| Public | `GET api/host-invites/lookup?token=` (and `POST` with the token in the body), rate limit `PublicInvitationLookup` |
| Signed in | `POST api/host-invites/accept` body `{token}` → 204 |

The email links to `{App:PublicSiteBaseUrl}/invito-host/{token}` (`PublicSiteLinks.HostInviteSignup`).

## Rules

- Token: 32 random bytes (64 hex), only the SHA-256 is stored (`SupplierInviteTokens`, shared with the supplier invites), valid 7
  days, single use, revocable; sending it again replaces the hash (the old link stops working).
- Acceptance needs the **verified** email of the signed-in account to equal the invited one (trim, lowercase). Same account twice:
  204. Another account after use: 409 `host_invite_used`. Also 422 `host_invite_invalid`, `_expired`, `_revoked`,
  `_email_not_verified`, `_email_mismatch`.
- Limits (configuration `HostInvites`, per host **org**, admins have none): `DailyQuotaPerHost` (10 in 24 h rolling, creations and
  resends) → 429 `host_invite_quota_exceeded`; `MaxPendingPerHost` (50) → 429 `host_invite_pending_limit`.
- Duplicate: a pending invite of the same inviter for the email → 409 `duplicate_invite` (checked per inviter, so it never tells
  that someone else invited the address). Own email → 422 `host_invite_self`.
- **Account enumeration**: for a **host**, an email that already has an account is answered exactly like a sent invite (201, same
  quota use, same row in its list) and **no email is sent**. A platform admin gets 409 `host_email_taken`.
- Audit: `HostInviteAuditEntries` (created, resent, revoked; actor, actor org, time; no email). The daily quota is counted from it.

## GDPR retention

`HostInvites:RetentionDays` (30) after an invite was closed, the daily job `host-invite-retention` (03:40 UTC) deletes a closed invite
nobody used (revoked or expired) and, for a used one, replaces the email with `erased-{id}@invalid`, drops the message and sets
`PersonalDataErasedAt` (the attribution stays). The audit trail holds no email and is kept.

## Demo

The demo showcase (`DemoShowcaseCatalog.HostInvites`) writes three invites of the demo host (pending, accepted, expired, all
`@example.com`, marked `TestDataEntities.HostInvite`); the reset deletes them.
