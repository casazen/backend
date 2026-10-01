# Runbook: SEO funnel — featured properties, events without personal data, admin widget

Task SE-04 (audit defects A8-10, A8-11, A8-18; spec US-026 #300 AC2, AC3, AC8, AC9). Related:
[`seo-domain.md`](seo-domain.md) (public domain, sitemap), [`proxy-ip.md`](proxy-ip.md) (rate limits),
[`gdpr.md`](gdpr.md), [`ai.md`](ai.md) (the AI text of the pages).

## What exists

| Piece | Where |
|---|---|
| Featured properties of a comune | `GET /api/public/seo/{comune}/featured-properties` (anonymous, `PublicRead` rate limit). `{comune}` is a slug or an ISTAT code. Published properties only (`PublicListing.IsPublished`: active, not paused, compliance activated; org active), newest first, at most 6. The page text, meta and CTA stay on the read APIs of #258 (`GET /api/public/content/...`, AC1). |
| Funnel events | `POST /api/public/seo/events` (anonymous, `PublicSeoEvents` rate limit, 204). Body `{ event, comuneSlug, utmSource?, utmMedium?, utmCampaign?, referrerHost? }`, `event` = `cta_click` or `signup_start`. Table `SeoEvents`. |
| Admin report | `GET /api/admin/seo/top-comuni?days=30&limit=10` (`AdminOnly`): per comune the CTA clicks, the signups started, and the host signups attributed to the comune (`SignupAttributions`, SE-03) in the window. Widget "Comuni che convertono" on the admin SEO dashboard. |
| Retention | Job `seo-event-retention` (03:30 UTC) deletes events older than `Seo__Events__RetentionDays` (default **90**). The report window is capped to the retention. |
| Web app | The CTA of the SEO pages sends `cta_click` with `navigator.sendBeacon` (a plain public request when the browser has none); the `/signup` page sends `signup_start` once when it comes from a comune page. Both fire and forget: a failure never reaches the visitor. |

## No personal data (A8-11)

A row of `SeoEvents` is `{ Id, Event, ComuneCode, UtmSource, UtmMedium, UtmCampaign, ReferrerHost, OccurredAt }`.

- **Never stored:** IP address, user id, visitor or session id, cookie, user agent, the full referrer URL (host only),
  the path. The IP is used only by the rate limiter, in memory, for the window.
- The marketing values follow `SignupAttributionRules` (same as the signup attribution, SE-03): letters, digits and
  `- _ . ~ + | : , / ( ) !` — no `@`, so an e-mail address cannot pass; a value outside the rule is refused (400), never
  truncated. The comune is resolved to its ISTAT code; an unknown one is refused (422 `seo_event_unknown_comune`).
- Because no row identifies a person, **nothing is deduplicated** (a repeated click counts again) and no hashing with a
  rotating salt, consent or banner is needed. If the product owner later wants unique visitors, that needs a compliance
  opinion first (`.claude/rules/compliance.md`): a daily salted hash of IP+user agent is still personal data under GDPR.
- Platform admins read only totals per comune; no endpoint lists events.
- Document the processing in the privacy notice (texts are the product owner's, D14): anonymous counters of the clicks
  on the SEO pages, kept 90 days.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `Seo__Events__RetentionDays` | `90` | Days an event is kept (0 or less reads as 90). |
| `RateLimiting__PublicSeoEvents__PermitLimit` / `__WindowSeconds` | `60` / `60` | Events per client IP per window. |
| `VITE_PLAUSIBLE_DOMAIN` (Vercel) | unset | Optional. When set, the web app also calls `window.plausible('cta_click' / 'signup_start', { props: { comune } })` if the Plausible script is on the page. The app does not load the script: the product owner adds Plausible's snippet to the site (and allows its host in the CSP of `vercel.json`) when they want it. Unset: nothing goes to a third party. |

## Featured properties: which comune is a property in

A property is listed under the comune whose official name equals its city (case-insensitive) until SU-04: the host
chooses the comune (`Property.ComuneIstatCode`) from the official list; then the match is on the ISTAT code and the
free-text city is no longer read. A property without a chosen comune is not listed under any page once SU-04 is in.

## Checks after a deploy

- `GET /api/public/seo/como/featured-properties` → 200 with the published properties of Como (empty list if none),
  `…/not-a-comune` → 404.
- On a published guide, click "Pubblica la tua casa" and open the admin SEO dashboard: the widget shows the click for
  the comune (within the minute) or "Nessun clic registrato" if none.
- `POST /api/public/seo/events` with `{"event":"cta_click","comuneSlug":"como","utmSource":"a@b.it"}` → 400.
- Hangfire dashboard: job `seo-event-retention` is scheduled.
