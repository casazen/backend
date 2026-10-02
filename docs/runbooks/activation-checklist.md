# Runbook: activation checklist and "site published" flag

Task PL-15 (audit defects A1-37, A3-26, spec PLG-AC10). Before it, `GET /api/onboarding/status` said `sitePublished`
as soon as the org had one active property: a paused property counted, a property waiting for its compliance
activation counted, and a site nobody could pay on counted (no Stripe account, or Connect started but
`chargesEnabled = false`). The host shared the link from the "Vetrina" page and every guest got a 409 at the checkout.

## Rule: a step is done only when the stored state proves it

`ActivationChecklist` (`Casazen.Core/Services/ActivationChecklist.cs`) is a pure function of facts loaded by
`OnboardingService`. Nothing is a manual flag; a start (a Connect onboarding begun, a property created) is never done.

| Step (`key`) | Done when | Not done: `state` / `reason` |
|---|---|---|
| `account` | role chosen, org provisioned, current consents accepted | `todo` / `onboarding_incomplete` |
| `organization` | the org name is not the generated one (`Org.PlaceholderName`) **and** the slug is not a generated one (`org-` + 8 chars, `OrgSlugHelper.IsNeutral`): the host chose both (PL-04 settings page) | `todo` / `org_profile_incomplete` |
| `property` | at least one non-deleted property (PC-05) | `todo` / `no_property` |
| `cin` | **every** non-deleted property has a valid CIN (`CinFormat`, computed on read, never stored). `done`/`total` count them | `todo` or `inProgress` / `cin_missing_or_invalid`; `blocked` / `no_property` |
| `payments` | `Org.CanTakeDirectPayments`: a connected account **and** `ConnectChargesEnabled` (the rule of the checkout, `BookingService`) | `todo` / `connect_not_started`; `inProgress` / `connect_requirements_due` (details not submitted or requirements due) or `connect_pending_verification` (submitted, Stripe has not enabled charges) |
| `sitePublished` | the org is active **and** at least one property is `PublicListing.IsPublished` **and** the org can take payments | `blocked` / `org_inactive`, `no_property`; `todo` / `properties_paused`, `compliance_pending`, `properties_inactive`; `inProgress` / `payments_not_ready` |
| `firstBooking` | at least one confirmed `Direct` booking (history: stays done if the site is hidden later) | `blocked` / `site_not_published`; `todo` / `awaiting_first_booking` |

`state` is `done`, `todo`, `inProgress` or `blocked` (another step comes first). `reason` and `state` are stable codes:
the clients translate them (never rename one).

### Decision on paused properties (PC-03, note of the task)

A **paused property does not count as a published site.** `sitePublished` uses `PublicListing.IsPublished` itself
(active, not paused, compliance `Active`), evaluated by the database: the checklist cannot drift from what the public
search, the property page, the availability and the checkout show. If every property that could be published is paused
the step says `properties_paused` and the dashboard links to the properties list to reactivate one. One paused plus
one published property is a published site.

### What `activated` means

Unchanged: role, org, consents, property created, **published site** (now real) and first direct booking. The
`organization` and `cin` steps are guidance on top: a host can be `activated` with a generated slug. The dashboard
widget (`ActivationChecklist`) stays visible until **every** step is done, then hides.

### Link to share

`publicBookingUrl` is returned only when `sitePublished` is true (and `App:PublicSiteBaseUrl` is configured, D3): no
link to a site that guests cannot book.

## Where the data comes from

- `ConnectChargesEnabled` is the cached value of Stripe's `account.updated` webhook (`StripeWebhookHandler`) and of
  the status read of the Payments page (`GET /api/connect/status?refresh=true`, `ConnectOnboardingService`). If a host
  completed the Stripe verification and the checklist still says `connect_pending_verification`, open the
  "Pagamenti" page once (it refreshes from Stripe) or check that the Connect webhook endpoint is configured
  ([`stripe.md`](stripe.md)).
- A slug chosen by hand that looks exactly like a generated one (`org-` + 8 characters of `abcdefghijkmnpqrstuvwxyz23456789`)
  is read as "not chosen yet". A legacy slug (before A1-23) that is not of that shape counts as chosen.

## Clients

- Web dashboard: `src/features/onboarding/components/activation-checklist.tsx` (`useOnboardingStatus`), with loading,
  error + retry, and the steps with state, reason and a link to the page that fixes it (checked against the
  ROUTE_MANIFEST and the user's permissions). Texts: `activation.*` in `it.json` / `en.json`.
- Web "Vetrina": `site-publication-banner.tsx` warns while the site is not really published and says why (A3-26).
- Guest side: a checkout on an org that cannot take payments already answers 409
  `direct_booking_payments_not_ready` with a localized message (BK-07).
- Mobile does not read `onboarding/status`.

## Not covered

- **CD-AC9** ("modalità host": path, subdomain or custom domain) is not a checklist step: the path mode works with no
  configuration and a custom domain is optional (Pro/Scale, BK-13..BK-16). It would otherwise show a requirement that
  does not exist.
