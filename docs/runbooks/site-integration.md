# Runbook: connecting an agency's own website (pilot)

Epics E40-E43, requirements NF-140 to NF-150, issue casazen/backend#974; this runbook is task E43/F01/US02/T01. The pilot is Quokka360: the example host in every command and record below is `prenota.quokka360.com` (written `<host>` where a placeholder is clearer). No real record, token or password of the customer is written here.

**What it is.** The agency's own domain (a verified custom domain such as `prenota.<agency domain>`) serves the booking site, the guest area and the owner portal with the agency's brand. The booking engine can also be embedded in the agency's existing website: an iframe that the script `/embed/v1/embed.js` mounts, and a WordPress plugin ships it. **The payment never happens inside an iframe**: «Prenota» moves the whole page to the checkout of the agency's domain. Everything new is behind three flags, all **off** by default (section 7). The apex and `www` of the agency's domain stay with its WordPress hosting.

**Who.** «The user» is the product owner of CasaZen, who owns Railway, Vercel, Auth0 and GitHub. «The agency's webmaster» runs the agency's DNS and WordPress. «The agency administrator» is the administrator of the organization in the CasaZen web app (policy `RequireOrgBillingAdmin`). «A platform administrator» is a CasaZen user with the role `Admin`. «Automatic» is done by the platform. This runbook is text only: nothing in it runs by itself, and every change in Railway, Vercel, Auth0, DNS or WordPress is a step of the table below.

**Conventions.** `$API` is the public URL of the Railway service of the environment (for test the GitHub variable `RAILWAY_TEST_URL`, [`infra.md`](../../.claude/rules/infra.md)); `apiBase` in the manifest of the host (`/embed/v1/manifest.json`) is the same address with `/api` at the end. `$TOKEN` is the access token of the person who makes the call (an organization administrator, or a platform administrator for `api/admin`); it is never written into a file, a ticket or a chat. Screen labels are the Italian ones of the web app, with the English twin in parentheses when it helps.

## 1. Summary and who does what

| # | WHAT | WHERE | WHO |
|---|---|---|---|
| 1 | Set `Vercel__ApiToken`, `Vercel__ProjectId` (and `Vercel__TeamId`) on the services of test **and** production (section 3) | Railway, project `casazen-api`, service `backend`, Variables of each environment | the user |
| 2 | Create the CNAME and the verification TXT of `prenota.<agency domain>` (section 4) | the agency's DNS provider | the agency's webmaster |
| 3 | Add `https://<host>` to Allowed Callback URLs, Allowed Logout URLs and Allowed Web Origins of the SPA application: by hand, or automatic with `Features__Auth0HostSync`, `Auth0__HostSync__ClientId` and the Management API permissions `read:clients` and `update:clients` (section 6) | Auth0 dashboard (the permission is granted there too) | the user (automatic path: «automatic» once the user has set it up) |
| 4 | Switch the three flags on in the TEST environment (section 7) | Railway, environment `test`, Variables; or the user's script `railway-flags.mjs` (section 7) | the user, only on their explicit order |
| 5 | Set the domain, press «Controlla ora», switch the guest area, the owner portal and the links on (section 5); once the plugin is connected it does the same from its own screen (section 12) | the CasaZen web app, `/app/short-rent/settings/domain`, or the screen of the plugin in WordPress | the agency administrator |
| 6 | Record the owner sign-in as ready when `Features__Auth0HostSync` is off (section 6) | `PUT api/admin/orgs/{orgId}/owner-login-state` | a platform administrator |
| 7 | Download the plugin with «Scarica il plugin», install it and press «Connetti a CasaZen»; then run its guided setup (sections 8 and 12) | the CasaZen web app, page «Incorpora nel tuo sito»; WordPress: Plugins, Add New, Upload Plugin; then Settings, CasaZen | the agency's webmaster; the agency administrator confirms the connection in the CasaZen web app |
| 8 | Invite the owners (there is no login with a Property ID) (section 6, end) | the CasaZen web app, `/app/short-rent/onboarding/owners`, phase «Accesso al portale» | the agency administrator |
| 9 | The guided proof on test with fake data (section 9) | the test environment, a test WordPress and the CasaZen web app | the user, with the agency's webmaster |

**Not a step any more.** Nobody has to push a release tag or set a Vercel variable to make the plugin downloadable: the button «Scarica il plugin» is always in the page «Incorpora nel tuo sito» (section 8). Steps 1 and 3 stay: they are the platform's, done once by the user (section 12.1 says what the agency does by itself).

**Order.** 1; then 4 (the flags) and, for the automatic Auth0 path, 3 (the permission and the client id); then 5 (the agency saves the domain, and the console shows the records); then 2; then 5 again (check, then the surfaces); on the manual Auth0 path then 3 and 6 (the agency's switch puts the state to `Pending`, a platform administrator lists it, the user adds the URLs, the administrator records `Ready`); then 7, 8 and 9. Two reasons fix it: the console shows the ownership TXT only after the domain is saved, and the automatic registration in Auth0 runs when a domain *becomes* verified, so the flags, `Auth0__HostSync__ClientId` and the permission must exist before that moment ([`auth0.md`](auth0.md) § 13.1).

## 2. What the code does

Paths are in this repository; `frontend:` is the repository `casazen/frontend`. Every row is read in the merged code.

| Part | What it does | Where |
|---|---|---|
| Three flags | `Features__WhiteLabelHost` (guest area, owner portal, links and the phone proof on the agency's domain), `Features__Auth0HostSync` (automatic registration of the host in Auth0; works only with `WhiteLabelHost` on), `Features__SiteEmbed` (the `/embed/*` pages, the page «Incorpora nel tuo sito», the plugin widgets, the Google Tag Manager id). All `false` in `appsettings.json`; the web app reads `whiteLabelHost`, `auth0HostSync` and `siteEmbed` from `GET api/public/features`. Off, everything behaves as before | `Casazen.Core/Features/FeatureFlags.cs`, `Casazen.Web/appsettings.json`, `Casazen.Web/Controllers/PublicFeaturesController.cs`, [`feature-flags.md`](feature-flags.md) |
| `resolve-host` | `GET api/public/resolve-host?host=` answers the new members `surfaces` (`guestArea`, `ownerPortal`), `ownerLoginReady`, `embedEnabled` and `tracking` (`gtmContainerId`). Surfaces are true only with `WhiteLabelHost` on, a verified custom domain the organization pays for and the agency's own switch; `ownerLoginReady` needs the owner portal on and the state `Ready`; `embedEnabled` is the flag `SiteEmbed`. `GET api/public/orgs/{slug}` reports `embedEnabled` too | `Casazen.Web/Controllers/PublicHostController.cs`, `Casazen.Core/DTOs/ResolveHostResponseDto.cs`, `Casazen.Infrastructure/Services/PublicHostResolver.cs`, `Casazen.Core/Services/HostSurfaceRules.cs` |
| The agency's choices | `GET api/orgs/{orgId}/domain` has a `surfaces` block (`guestAreaOnOwnDomain`, `ownerPortalOnOwnDomain`, `useOwnDomainForLinks`, `ownerLoginHostState`, `ownerLoginAutomatic`, `effective`) and a `tracking` block. `PUT .../domain/surfaces` saves the three switches (`useOwnDomainForLinks` is **on by default**, but moves a link only when its surface is on); `PUT .../domain/tracking` the container id; `POST .../domain/owner-login` retries the Auth0 registration. Plan: the domain's (Pro or Scale) | `Casazen.Web/Controllers/OrgDomainController.cs`, `Casazen.Web/DTOs/Orgs/OrgDomainDtos.cs`, `Casazen.Core/Entities/Org.cs` |
| Owner sign-in in Auth0 | With `Auth0HostSync` on, a domain that becomes verified is registered in the SPA application (three lists, one `PATCH`, read back); states `NotRequested`, `Pending`, `Ready`, `Failed`; notices `domain-verified`, `owner-login-ready`, `owner-login-failed` to the agency administrators (bell and live update). With it off, a platform administrator does it by hand | `Casazen.Infrastructure/Services/Auth0HostRegistrar.cs`, `Casazen.Infrastructure/Services/OwnerLoginHostSync.cs`, `Casazen.Web/Controllers/AdminOwnerLoginHostsController.cs`, [`auth0.md`](auth0.md) § 13 |
| CORS on verified domains | The verified domain of any agency that pays for it is allowed on `/api/public/*` and `/api/legal/*` (public data, no token, not tied to one agency; the guest area is under them); with the owner portal on **for that domain's own agency**, also on `/api/owner-portal/*` and the two acceptance endpoints of the invitations. One reflected origin, **no credentials**. CORS cannot see the token, so what binds an owner to **one** agency is a second requirement on the `OwnerPortal` policy (`OwnerPortalHostOriginRequirement`): a token used from the domain of another agency is refused with 403 | `Casazen.Web/Infrastructure/OrgHostCorsOriginSource.cs`, `Casazen.Web/Authorization/OwnerPortalHostOriginHandler.cs`, [`cors-security-headers.md`](cors-security-headers.md) |
| Frame headers | The API answers `frame-ancestors 'none'` everywhere. The web app answers `frame-ancestors https:` and `X-Robots-Tag: noindex, nofollow` **only** on the pages of `/embed/*`; the scripts under `/embed/v1/*` and every other path (`/login`, `/checkin/*`, `/my-bookings`, `/portale-proprietari/*`) keep `frame-ancestors 'none'`, except `/book/*` and `/fornitori/*`, which allow the same origin only (`'self'`) | `Casazen.Web/Middleware/SecurityHeadersMiddleware.cs`, `frontend: vercel.json`, the table of every rule in [`cors-security-headers.md`](cors-security-headers.md) § 3 |
| Links on the agency's domain | Guest links (check-in, booking area, booking confirmation) move with `useOwnDomainForLinks` plus the guest area; owner links (invitations, conversations, accounting) with `useOwnDomainForLinks` plus the owner portal plus a `Ready` sign-in. Otherwise the CasaZen link, byte for byte | `Casazen.Infrastructure/Email/AgencyDomainLinks.cs` |
| Guest lookup with the last four digits | `POST api/public/guest/session` takes `{ orgSlug, bookingCode, phoneLast4 }` instead of `email` (only with `WhiteLabelHost` on); five wrong tries on one code in 60 minutes lock the code; a uniform 404 for every wrong proof | `Casazen.Web/Controllers/GuestAreaController.cs`, `Casazen.Web/DTOs/GuestSessionRequest.cs`, `Casazen.Web/Infrastructure/GuestPhoneLookupLockout.cs`, [`direct-booking.md`](direct-booking.md) § 8.4.1 |
| Tracking | A Google Tag Manager id per agency (`GTM-` plus 4 to 12 letters or digits), served by `resolve-host` only with `SiteEmbed` on; the container loads on the agency's own pages only after the visitor accepted statistics | `Casazen.Core/Services/GtmContainerIds.cs`, `frontend: src/features/host-tracking/` |
| Pages on the agency's domain | The booking site, `/my-bookings`, then (surfaces on) `/checkin/:token` and `/portale-proprietari/*` inside the agency's frame; the widgets `/embed/booking`, `/embed/property/<slug>`, `/embed/guest-access` (on the CasaZen domain `/embed/<orgSlug>/...`). Every other address of the console is «not found» there | `frontend: src/routes/host-site-routes.tsx`, `src/routes/embed-routes.tsx`, `src/features/owner-portal/owner-host-login-page.tsx` |
| Loader and manifest | The build writes `/embed/v1/embed.js`, `/embed/v1/search.js` and `/embed/v1/manifest.json` (`v`, `apiBase`, `widgets`, `loader`, `search`); `apiBase` is `VITE_API_BASE_URL` of the Vercel environment that built it | `frontend: scripts/build-embed.mjs`, `src/embed/embed.ts`, `src/embed/search.ts` |
| Page «Incorpora nel tuo sito» | `/app/short-rent/settings/embed`: domain status, choice of the widget, preview, code to paste, plugin steps, Google Tag Manager id | `frontend: src/features/settings/embed/` |
| The plugin | WordPress plugin `CasaZen` (blocks, shortcodes, guided setup, and the connection to the agency's account of section 12); CI «WordPress plugin»; the tag `wp-plugin-v<version>` makes a release with `casazen-<version>.zip` (a release with a version, never needed for the download: section 8) | `frontend: integrations/wordpress/casazen/`, `.github/workflows/wordpress-plugin.yml`, `.github/workflows/wordpress-plugin-release.yml` |
| Connection of a site | A billing administrator confirms in the console that a site may connect (`POST api/orgs/{orgId}/site-connections/authorize`: a one-time code of five minutes, bound to the org, the account, the site and a PKCE `S256` challenge); the plugin exchanges it for its own credential `czs_…` (`POST api/public/site-connections/token`, anonymous); the console lists and revokes the connected sites (`GET api/orgs/{orgId}/site-connections`, `DELETE api/orgs/{orgId}/site-connections/{connectionId}`). Only the SHA-256 of the code and of the credential is stored | `Casazen.Web/Controllers/SiteConnectionsController.cs`, `Casazen.Web/Controllers/PublicSiteConnectionsController.cs`, `Casazen.Infrastructure/Services/SiteConnectionService.cs`, `Casazen.Core/SiteConnections/` |
| The plugin's account and domain | With its credential (scheme `SiteToken`, `Authorization: Bearer czs_…`) the plugin calls `api/site/v1/*` for **its own org only**: `GET account` (the org, `embedEnabled`, `publicAddress`, `publicAddressKind`, the `domain` object of the console), `PUT domain` (a name, or `null` for the CasaZen address), `POST domain/verify` (`{ verify, account }`), `PUT domain/surfaces` (behind `WhiteLabelHost`) and `DELETE connection`. They are the console's own operations: the same service, the same outcomes and codes, the same objects, and the same commercial module (`direct-booking`) | `Casazen.Web/Controllers/SiteApiController.cs`, `Casazen.Infrastructure/Services/SiteAccountBuilder.cs`, `Casazen.Web/DTOs/Orgs/OrgDomainMapper.cs`, `Casazen.Web/Infrastructure/OrgDomainOutcomeResults.cs`, `Casazen.Web/Infrastructure/SiteDomainAccessAttribute.cs` |
| Cross-origin proof | A page of another origin loads the real `embed.js`, the widgets mount, «Prenota» moves the whole page, the pages that must not be framed refuse to be | `frontend: e2e/embed-cross-origin.spec.ts` (job «E2E embed (cross-origin)» of «E2E Tests») |

## 3. Prerequisites: the Vercel variables (step 1)

WHAT: `Vercel__ApiToken` (secret), `Vercel__ProjectId`, and `Vercel__TeamId` when the project belongs to a team. WHERE: Railway, service `backend`, in **each** environment. WHO: the user.

Without them every custom domain stays `Pending` with the detail `vercel_not_configured`, and nothing the agency does activates it. How to create the token, where to read the ids, the DNS values Vercel recommends and the end-to-end try are in [`seo-domain.md`](seo-domain.md) § 10, «Product owner: what to configure»: they are not repeated here, and no value is written in this runbook. The rows are also in [`deploy-checklist.md`](deploy-checklist.md) (`Vercel__*`).

One Vercel project serves both environments ([`docs/INFRA.md`](../INFRA.md), «Vercel Setup»): the `develop` deployment is Preview (Preview variables, the test API) and `main` is Production (the production API). Both Railway environments therefore name the same project, and a domain added by either is on that project. The platform adds a domain **by its name only** (`VercelDomainsClient.AddDomainAsync` sends `{"name": ...}`): it never chooses a branch. Section 4 says how to check which deployment answers on the host.

Check:

```bash
API=https://<railway url of the environment>

# "vercel": "healthy" (or "degraded" naming the missing variable names, never their values)
curl -sS "$API/api/health/ready" | grep -o '"vercel"[^}]*'
```

## 4. DNS (step 2)

WHAT: two records for the subdomain the agency chose. WHERE: the DNS provider of the agency's domain. WHO: the agency's webmaster, with the values the console shows after the agency administrator saved the domain (section 5): the values below are the shape.

| Type | Name | Value |
|---|---|---|
| TXT | `_casazen-challenge.prenota.quokka360.com` | the token the console shows (random for each organization; never reuse one) |
| CNAME | `prenota.quokka360.com` | `cname.vercel-dns.com`, or the value the console shows (`PublicHost__VercelCnameTarget`; the accepted values are in [`seo-domain.md`](seo-domain.md) § 10) |
| TXT, only if the console asks | `_vercel.prenota.quokka360.com` | the value the console shows when the domain was used on another Vercel account (`vercel_verification_pending`) |

The apex `quokka360.com` and `www.quokka360.com` are **not touched**: they stay with the WordPress hosting. An A record exists only for a domain without a subdomain label (a root cannot have a CNAME), which is not this case.

**Proxy warning.** If the DNS provider proxies the record (for example Cloudflare's orange cloud), the proxy answers with its own addresses and the domain stays `dns_not_pointing`: set both records to «DNS only» ([`seo-domain.md`](seo-domain.md) § 10, Troubleshooting).

Checks (from any shell, after the records propagate, which can take hours):

```bash
HOST=prenota.quokka360.com

dig +short TXT _casazen-challenge.$HOST     # "<the token of the console>"
dig +short CNAME $HOST                      # cname.vercel-dns.com.
dig +short A $HOST                          # the addresses of Vercel, behind the CNAME
dig +short A quokka360.com                  # the apex: still the WordPress hosting
```

**Which deployment answers on the host (TEST only).** After the domain is verified, read the manifest:

```bash
curl -sS "https://$HOST/embed/v1/manifest.json"
# {"v":1,"apiBase":"https://<backend host>/api","widgets":["booking","property","guest-access","search"],"loader":"/embed/v1/embed.js","search":"/embed/v1/search.js"}
```

`apiBase` must be the **test** API. If it names the production API, the Production deployment (`main`) answers on the host: it calls the production API, where the test organization does not exist (`resolve-host` answers 404, «Sito non trovato»), and it carries the last release, not `develop`. For the proof on test the host has to be served by the `develop` deployment: in the Vercel dashboard (project, Settings, Domains) connect the host to the `develop` branch. This is a step of the user, done once for a test host, and the platform never does it. Use the agency's real host in one environment at a time: both Railway environments name the same project, so a domain that the job of one environment takes off the project (section 10) is gone for the other too.

## 5. The domain in CasaZen (step 5)

WHAT: save the domain, check it, switch the surfaces on. WHERE: the web app, Impostazioni, group «Sito di prenotazione», «Dominio pubblico» (`/app/short-rent/settings/domain`, page «Dominio prenotazioni pubblico», "Public booking domain"). WHO: the agency administrator.

1. In «Modalità di pubblicazione» ("Publication mode") choose «Dominio personalizzato (Pro)» ("Custom domain (Pro)"), write `prenota.quokka360.com` in the field «Dominio personalizzato» and press «Salva impostazioni dominio» ("Save domain settings"). The plan has to include custom domains (Pro or Scale, [`open-access.md`](open-access.md)): otherwise the page says «I domini personalizzati sono disponibili con Pro.», and a plan that lapsed later says «Il tuo piano non include più il dominio personalizzato».
2. The page lists «Record DNS da creare» ("DNS records to create"): the ownership TXT, the CNAME and, only when Vercel asks, a third TXT. Give them to the webmaster (section 4).
3. Press «Controlla ora» ("Check now") or wait: the job `domain-recheck` checks it again by itself ([`seo-domain.md`](seo-domain.md) § 10). The badge goes «In attesa del DNS» ("Waiting for DNS"), «In attivazione» ("Activating"), «Attivo» ("Active"); «Non attivo» ("Not active") is a failure, and the line under the badge says which one (`detail`, section 11).
4. With `Features__WhiteLabelHost` on and the domain «Attivo», the block «Cosa passa dal tuo dominio» ("What runs on your domain") appears with three switches: «Area ospite sul mio dominio» ("Guest area on my domain"), «Portale proprietari sul mio dominio» ("Owner portal on my domain") and «Usa il mio dominio nei link che invio» ("Use my domain in the links I send"). A switch that is on but not in effect yet says so under it (for example «Per ora i link restano quelli di CasaZen.»). The last switch is on by default, but a link moves only with its surface: the guest links with the guest area, the owner links with the owner portal **and** a `Ready` sign-in. **From the moment the guest area is on, every new check-in, booking-area and confirmation link of that agency points to its domain**: switch it on only when the host is healthy (section 9, a and b).
5. The line «Accesso dei proprietari» ("Owner sign-in") shows the state of section 6: «non ancora richiesto», «in attesa di abilitazione da parte di CasaZen», «registrazione in corso», «attivo», «registrazione non riuscita» (with «Riprova», "Try again").
6. With `Features__SiteEmbed` on, the page «Incorpora nel tuo sito» ("Embed in your site", `/app/short-rent/settings/embed`, in the same settings group) shows the domain status, the choice of the widget with its live preview and the code to paste, the plugin steps and the field «Identificativo del contenitore» (Google Tag Manager, `GTM-ABC123`). Only the organization administrator opens it.

Check (the answer is the whole configuration; `$ORG_ID` is the id of the test organization):

```bash
curl -sS -H "Authorization: Bearer $TOKEN" "$API/api/orgs/$ORG_ID/domain"
# domainVerificationStatus "Verified"; status.detail null; canUseCustomDomain true;
# surfaces: the three switches, ownerLoginHostState, ownerLoginAutomatic, effective { guestArea, ownerPortal, ownerLoginReady, linksOnOwnDomain }
```

## 6. Auth0 (steps 3 and 6)

**Warning: one Auth0 tenant for test and production** ([`auth0.md`](auth0.md) § 1.2). The tenant itself, the Action `CasaZen claims`, the quotas and the tenant-wide settings are common to both environments, while the SPA application, the Management API (M2M) application and the database connection of the users are one for each environment ([`auth0.md`](auth0.md) § 1.2, rules 1 and 5); a host registered in an application is a working sign-in address until somebody removes it: nothing removes it when a test ends. So: (1) prove first with a **fake host** (a name under a domain the user owns, never the real host of a customer), and remove it from Auth0 and from CasaZen afterwards; (2) never register a host the agency has not verified (the code refuses `Ready` for it); (3) each environment has its own SPA application: add the host to the application whose client id is `VITE_AUTH0_CLIENT_ID` of the Vercel environment that serves the host, and set `Auth0__HostSync__ClientId` of a Railway environment to that environment's application, never to the other one; (4) **append only, never reorder**: the first callback URL of an application stays the platform origin ([`auth0.md`](auth0.md) § 1.2 rule 6).

The web app signs in with `redirect_uri` equal to the origin of the page (`frontend: src/config/auth.config.ts`) and signs out with `returnTo` equal to the same origin (`frontend: src/contexts/auth-bridge.tsx`); on the host, `/portale-proprietari/accedi` sends the owner straight to the sign-in and back to `/portale-proprietari` (`frontend: src/features/owner-portal/owner-host-login-page.tsx`). So the origin of the host, `https://<host>` and nothing else (no path, no wildcard), has to be in three lists. Until the state is `Ready`, the owner portal of the host is one page, «Accesso proprietari non ancora attivo» ("Owner access not active yet"), and no owner link moves to the agency's domain.

States of an agency (`ownerLoginHostState`, in `GET api/orgs/{orgId}/domain`): `NotRequested` (nobody asked), `Pending` (waiting: for CasaZen with the sync off, or for its configuration), `Ready` (the three URLs are there), `Failed` (the registration failed; the agency can retry). A new domain, or a change to the platform path or a subdomain, puts it back to `NotRequested` (or `Pending` when the owner portal switch stays on).

### Manual path: `Features__Auth0HostSync` off (the default)

WHO: the user in Auth0 and a platform administrator for the endpoints; the agency reads «in attesa di abilitazione da parte di CasaZen» meanwhile. The two endpoints answer 404 while `Features__WhiteLabelHost` is off.

1. The agency administrator switches «Portale proprietari sul mio dominio» on (section 5): the state becomes `Pending`.
2. A platform administrator lists the hosts that wait (the default of `state` is `Pending`):

   ```bash
   curl -sS -H "Authorization: Bearer $TOKEN" "$API/api/admin/owner-login-hosts?state=Pending"
   # [{ "orgId": "...", "displayName": "...", "domain": "prenota.quokka360.com", "state": "Pending", "domainVerified": true }]
   ```

3. For a row with `domainVerified: true`, the user opens the Auth0 dashboard, Applications, the SPA application of **this** environment, Settings, and **appends** `https://prenota.quokka360.com` at the end of Allowed Callback URLs, Allowed Logout URLs and Allowed Web Origins (comma separated, saved).
4. A platform administrator records it:

   ```bash
   curl -sS -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{ "state": "Ready" }' "$API/api/admin/orgs/$ORG_ID/owner-login-state"
   # {"state":"Ready"}; 422 org_domain_not_verified if the domain is not verified or not paid; 404 for an unknown organization
   ```

   The agency administrators are told (`owner-login-ready`) and the cached `resolve-host` answer of the domain is dropped. `{ "state": "Failed" }` tells them it could not be done (`owner-login-failed`).

With the sync off the platform never removes a host from Auth0: when a domain registered by hand is removed, remove its three strings by hand ([`auth0.md`](auth0.md) § 13.2 has the monthly comparison).

### Automatic path: `Features__Auth0HostSync` on (with `Features__WhiteLabelHost`)

WHO: the user sets it up once per environment; then «automatic».

1. In Auth0, Management API, grant the M2M application of the environment the scopes `read:clients` and `update:clients` ([`auth0.md`](auth0.md) § 4).
2. On Railway set `Auth0__HostSync__ClientId` to the client id of the SPA application **of the same environment** (not a secret; the value of `VITE_AUTH0_CLIENT_ID` of its Vercel environment). Check that the first Allowed Callback URL of that application is the platform origin.
3. Switch `Features__WhiteLabelHost` and `Features__Auth0HostSync` on (section 7) **before** the domain becomes verified: the registration runs when a domain becomes verified, when the agency saves the owner portal switch on while the state is `NotRequested` or `Failed`, and on the agency's retry. A domain that was verified before the flag stays `NotRequested` or `Pending` until its registration is asked again. The console draws «Riprova» only in the state `Failed`, so for these two states the agency administrator calls the retry with their own token, `curl -sS -X POST -H "Authorization: Bearer $TOKEN" "$API/api/orgs/$ORG_ID/domain/owner-login"` (it works from every state but `Ready`; at most five calls in ten minutes per person; [`proxy-ip.md`](proxy-ip.md)); or you list the waiting hosts with the endpoint above (`state=Pending`, then `state=NotRequested`) and use the manual path.
4. The platform reads the three lists, writes them back in one `PATCH` with the origin appended and reads them again; `Ready` only if the read-back has it. The logs say `Auth0 host registration: <Result> (<code>)` with no domain, token or body. Removing or replacing the domain removes exactly those three strings at the next run of `domain-recheck`.

### Invite the owners (step 8)

WHO: the agency administrator, once the state is `Ready`. WHERE: `/app/short-rent/onboarding/owners` («Proprietari e rendiconti»), phase «Accesso al portale» ("Portal access"): preview, then confirm; no invitation leaves by itself. The owner portal belongs to the agency plan: `Features__AgencyTier` on and an agency organization, and the batch screen needs `Features__OctorateOnboarding` (read both in `GET api/public/features`). The invitation e-mail link starts with `https://<host>/portale-proprietari/invito?token=` once the links are on the agency's domain with a `Ready` sign-in; there is **no** login with a Property ID.

Checks:

```bash
curl -sS -H "Authorization: Bearer $TOKEN" "$API/api/orgs/$ORG_ID/domain"     # surfaces.ownerLoginHostState "Ready"
curl -sS "$API/api/public/resolve-host?host=prenota.quokka360.com"             # "ownerLoginReady": true
```

## 7. The flags (step 4)

WHAT: the three variables on the Railway service `backend`, in the **TEST** environment only. WHO: the user, **only on their explicit order**. Production stays off until the user decides.

| Variable | Default | What it opens | Needs |
|---|---|---|---|
| `Features__WhiteLabelHost` | off | guest area, owner portal and links on the agency's verified domain; `PUT .../domain/surfaces` (the console's, and the plugin's `PUT api/site/v1/domain/surfaces`), `POST .../domain/owner-login`, the two `api/admin` endpoints; the phone proof; CORS of the owner portal; the three notices | a verified custom domain on a plan that includes it |
| `Features__SiteEmbed` | off | the `/embed/*` pages (`embedEnabled`), the page «Incorpora nel tuo sito», the widgets of the plugin, the Google Tag Manager id (`PUT .../domain/tracking`), the connection of the plugin to the account and its API `api/site/v1/*` (section 12) | nothing else |
| `Features__Auth0HostSync` | off | the automatic registration of section 6 | `Features__WhiteLabelHost` on, `Auth0__HostSync__ClientId`, the permissions `read:clients` and `update:clients` |

Order: `WhiteLabelHost`, then `SiteEmbed`, then `Auth0HostSync` only if the user has given the permission. With a flag off the behaviour is today's.

**Way 1, the Railway dashboard.** Project `casazen-api`, environment `test`, service `backend`, Variables: add `Features__WhiteLabelHost` with `true` (and the others in turn); Railway redeploys.

**Way 2, the user's script `octorate-harness/railway-flags.mjs`** (outside this repository; it touches the TEST environment only and prints no secret; run it **only on the explicit order of the user**). Read its header first: `apply` also sets, when they are missing, four secrets (generated, never printed) and one label that other flags need. **Always pass `--flags`**: without the option, or with a misspelt one, the script takes its own default list and turns ten other flags on in TEST. Run `check` first.

```bash
# from the folder of the script
node railway-flags.mjs check --flags WhiteLabelHost,SiteEmbed     # reads, changes nothing
node railway-flags.mjs apply --flags WhiteLabelHost,SiteEmbed     # sets Features__<Name>=true
node railway-flags.mjs wait 40 --flags WhiteLabelHost,SiteEmbed   # polls GET api/public/features up to 40 minutes, then reads /api/health
```

The script only turns flags **on**: to switch one off use the dashboard.

Check, with either way:

```bash
curl -sS "$API/api/public/features"     # "whiteLabelHost": true, "siteEmbed": true, "auth0HostSync": false (until section 6 is ready)
```

## 8. The plugin (step 7)

WHAT: install the plugin and run its guided setup. WHERE: WordPress, Plugins, Add New, Upload Plugin; then Settings, CasaZen. WHO: the agency's webmaster (for the proof, whoever runs the test WordPress).

**The ZIP.** The button «Scarica il plugin» ("Download the plugin") of the page «Incorpora nel tuo sito» is **always** shown: nobody pushes a release tag and nobody sets a Vercel variable for it. It links to `https://github.com/casazen/frontend/releases/download/wp-plugin-<channel>/casazen.zip`, the asset that the CI of the repository `casazen/frontend` replaces at every push to `develop` and to `main` that changes the plugin, on the two moving releases `wp-plugin-develop` (a pre-release) and `wp-plugin-main` (never marked «latest»); the notes of the release say commit, version and date. The console links the channel `main` in a Production build of Vercel and `develop` in any other, and the ZIP carries the address of the console of its own channel (`includes/build-config.php`: `https://casazen-test-app.vercel.app` for `develop`, `https://www.casazen.space` for `main` and for a release with a version), so the ZIP of the test web app connects to the test console and the one of production to the production console. A release **with a version** is still made by the tag `wp-plugin-v<version>` (workflow «WordPress plugin release», run from a commit where «WordPress plugin» is green; `frontend: docs/technical/wordpress-plugin.md`, «Rilascio»), and the Vercel variable `VITE_WORDPRESS_PLUGIN_URL` can still replace the link with the address of another ZIP: both are optional and neither is a step of the pilot. For a test the ZIP is also the artifact `casazen-wordpress-plugin` (14 days) of a green run of «WordPress plugin».

**Install and connect.** Upload and activate the ZIP, then press «Connetti a CasaZen» ("Connect to CasaZen"): the address and the slug fill themselves in and the plugin manages the domain from its own screen (section 12). The manual way stays under «Avanzate» ("Advanced") and works as before. Settings, CasaZen, tab «Impostazioni» ("Settings"): write the **CasaZen address** `https://prenota.quokka360.com` (on the CasaZen domain instead of the agency's, also the organization slug) and save: the plugin tests the connection by itself, and «Prova la connessione» ("Test connection") repeats it. The box «Stato della connessione» ("Connection status") shows **Agenzia** ("Agency"), the name of the agency that answers at that address: it proves the address serves the embed files of the web app (`/embed/v1/manifest.json`) **and** that `resolve-host` knows it as a verified domain of an agency that pays for it. It also shows «Incorporamento» (Attivo, `embedEnabled`) and «Accesso dei proprietari» (Pronto, `ownerLoginReady`). The codes after a test (`ok`, `embed_disabled`, `host_not_found`, `manifest_unreachable`, `manifest_invalid`, `resolve_unreachable`, `not_configured`) are in the guide.

**Guided setup.** Tab «Procedura guidata» ("Guided setup"), after a successful test: «Crea le pagine» makes «Prenota», «Check-in online» and «Area proprietari» (and the menu entries, if a menu is chosen), safely twice, overwriting nothing. The hero bar of the home is `[casazen_search target="/prenota/"]`.

Guides: [plugin guide, Italian and English](https://github.com/casazen/frontend/blob/develop/integrations/wordpress/casazen/README.md) and [maintainer guide with the pilot checklist](https://github.com/casazen/frontend/blob/develop/docs/technical/wordpress-plugin.md), both in the repository `casazen/frontend`.

## 9. The guided proof on TEST, with fake data

WHO: the user, with the agency's webmaster for the WordPress part. Before starting, steps 1 to 8 of section 1 are done: the flags `WhiteLabelHost` and `SiteEmbed` are on in test, the host is verified and served by the `develop` deployment, the owner sign-in is `Ready`, the plugin is installed and an owner is invited. The test organization has a plan with a custom domain, **at least one published home** (invented data), a way to take a card payment in Stripe test mode ([`stripe.md`](stripe.md)) and a test guest with a phone number you know. The host is the fake host of section 6's warning, or `prenota.quokka360.com` once the user decides. Test only: never run this with real guests or a live Stripe key.

| Step | Where | Who | What you must see | If not |
|---|---|---|---|---|
| (a) Domain verified, surfaces on | the console `/app/short-rent/settings/domain`; commands (a) | the agency administrator; the user | badge «Attivo» and the three switches on; `resolve-host` has `surfaces.guestArea` and `surfaces.ownerPortal` true, `embedEnabled` true (and `ownerLoginReady` true after section 6); the manifest answers 200 and its `apiBase` is the test API | section 11: `dns_not_pointing`, `ownership_txt_missing`, `vercel_not_configured`; `apiBase` of production: section 4 |
| (b) Headers | commands (b), from any shell; a browser | the user | `/embed/booking`: `content-security-policy: frame-ancestors https:` and `x-robots-tag: noindex, nofollow`. `/embed/v1/embed.js`: `frame-ancestors 'none'`, `cross-origin-resource-policy: cross-origin`. `/login`, `/checkin/x` and `/portale-proprietari/accedi`: `frame-ancestors 'none'` and `x-frame-options: DENY`. In a browser `https://<host>/login` is the agency's «not found» page, never the CasaZen login (the single-page app answers every path with status 200, and the page is drawn by the browser) | the deployed web app is older than the `vercel.json` rules of E41, or another deployment answers on the host (section 4) |
| (c) The hero bar leads to «Prenota» | the home of the test WordPress with the bar | the agency's webmaster | dates and guests chosen, «Verifica disponibilità» opens the page «Prenota» with the dates and the guests already in the widget | section 11: the frame shows the fallback link; an empty page with a cache plugin; `host_not_found`; `embed_disabled` |
| (d) Results, a home, checkout, test payment | the page «Prenota» of the test WordPress, then the checkout | the user | results and a home inside the frame (its height follows the content, no scrollbar); «Prenota» moves the **whole** page to `https://<host>/property/<slug>/checkout?checkin=...&checkout=...&guests=...&utm_source=agency-site&utm_medium=embed`; the Stripe test card `4242 4242 4242 4242` (any future expiry, any CVC) pays; the confirmation page is on `https://<host>/booking/<id>` | a payment form **inside** the frame is a defect: stop and run (b); Stripe not in test mode: [`stripe.md`](stripe.md) |
| (e) E-mails on the agency's domain | the inbox of the test guest, or the Resend dashboard, Emails, of the test environment ([`email.md`](email.md)) | the user | the links of the confirmation e-mail and of the check-in e-mail start with `https://<host>/` (the check-in page `/checkin/<token>`, the booking area `/my-bookings`, with `?code=` when the e-mail carries the booking code) | section 11: a booking link still on the CasaZen domain |
| (f) «Check-in online» | the page «Check-in online» of the test WordPress (widget `guest-access`) | the user | the code typed in the widget moves the whole page to `https://<host>/my-bookings?code=...`; there the e-mail **or** the last four digits of the phone open the booking; the widget itself sends nothing but the code | wrong digits give the same «not found» as a wrong e-mail; five wrong tries lock the code for 60 minutes (429, `Retry-After: 3600`): use the e-mail ([`direct-booking.md`](direct-booking.md) § 8.4.1); the booking must be a direct one with a phone of at least four digits |
| (g) An invited owner signs in | «Area proprietari» of the test WordPress; the invitation e-mail | the agency administrator invites (section 6); a test owner signs in | the invitation link starts with `https://<host>/portale-proprietari/invito?token=`; the owner accepts, signs in at Auth0, returns to `https://<host>/portale-proprietari`; after «Esci» the browser lands on `https://<host>` | Auth0 «Callback URL mismatch»: step 3 on the application the host uses; «Accesso proprietari non ancora attivo»: the states of section 6; no invitation screen: the flags of the invitation, section 6 |
| (h) Events in `dataLayer` | the browser console of the page «Prenota»; of an agency page with a `GTM-` id after the consent | the user | commands (h): `search` (from the bar), `view_item_list`, `view_item` and `begin_checkout` with the GA4 shape; on the agency's own pages, after the visitor accepts statistics and the agency saved a `GTM-` id, the container loads and `purchase` is pushed once per booking | the attribute `data-casazen-datalayer="true"` (the plugin has `datalayer` on by default); the `GTM-` id in «Incorpora nel tuo sito» |
| (i) CI evidence on the deployed commit | GitHub Actions of `casazen/frontend`; commands (i) | the user | on the deployed commit: workflow «E2E Tests», job «E2E embed (cross-origin)» (`e2e/embed-cross-origin.spec.ts`, seven tests) green, and workflow «WordPress plugin» (jobs `lint`, `phpunit`, `smoke`, `plugin-check`, `package`) green | a red job means the proof has not passed: the fix is in the frontend repository, and the proof is repeated on the new commit |

Commands (a) to (i):

```bash
API=https://<railway url of the TEST environment>
HOST=prenota.quokka360.com

# (a) the answer of the platform for the host (the host serves only the web app: this call goes to the API, not to the host)
curl -sS "$API/api/public/resolve-host?host=$HOST"
# { "orgId": "...", "slug": "...", "publicHostMode": "CustomDomain", "planTier": "Pro", "branding": { ... },
#   "surfaces": { "guestArea": true, "ownerPortal": true }, "ownerLoginReady": true, "embedEnabled": true, "tracking": { "gtmContainerId": null } }
curl -s -o /dev/null -w "%{http_code}\n" "https://$HOST/embed/v1/manifest.json"     # 200
curl -sS "https://$HOST/embed/v1/manifest.json"                                      # apiBase: the TEST API

# (b) the headers
for p in /embed/booking /embed/v1/embed.js /login /checkin/x /portale-proprietari/accedi; do
  echo "== $p"
  curl -s -D - -o /dev/null "https://$HOST$p" | grep -iE "^(content-security-policy|x-frame-options|x-robots-tag|cross-origin-resource-policy):"
done
# the same checks the other way round: CORS of the agency's origin on a public endpoint, and on the owner portal when it is on
curl -si -X OPTIONS "$API/api/public/orgs/<slug>" -H "Origin: https://$HOST" -H "Access-Control-Request-Method: GET" | grep -iE "^access-control-allow-origin|^vary"
curl -si -X OPTIONS "$API/api/owner-portal/properties" -H "Origin: https://$HOST" -H "Access-Control-Request-Method: GET" -H "Access-Control-Request-Headers: authorization" | grep -iE "^access-control-allow-origin"
# never on a console endpoint: no access-control-allow-origin at all
curl -si -X OPTIONS "$API/api/properties" -H "Origin: https://$HOST" -H "Access-Control-Request-Method: GET" | grep -i "^access-control-allow-origin" || echo "rejected (expected)"

# (i) the runs of the deployed commit (the commit is shown by Vercel on the develop deployment)
gh run list --repo casazen/frontend --commit <sha> --json workflowName,status,conclusion
gh run view <run id> --repo casazen/frontend          # the jobs of one run
```

(h), in the console of the page:

```js
window.dataLayer.filter((entry) => entry.event).map((entry) => entry.event)   // ["search", "view_item_list", "view_item", "begin_checkout", ...]
```

## 10. Rollback

From the least to the most; nothing here needs a migration.

1. **Take the agency's pages and links back** (the agency administrator): in `/app/short-rent/settings/domain` switch off «Area ospite sul mio dominio», «Portale proprietari sul mio dominio» and «Usa il mio dominio nei link che invio». New e-mails go back to the CasaZen domain, and the check-in page and the owner portal stop being served on the agency's domain, so the links already sent there stop working until the switches are back; the agency's domain keeps its booking site.
2. **Take one agency out of Auth0** while the flags are still on: remove its three strings `https://<host>` from the SPA application (the other entries and the first callback URL untouched), then `PUT api/admin/orgs/{orgId}/owner-login-state` with `{ "state": "Pending" }`; with `Features__Auth0HostSync` on, removing the domain in step 4 makes the platform do it at the next run of `domain-recheck`.
3. **Switch the flags off** (the user, Railway test, and production if it was ever on; the dashboard, the script has no off switch): `Features__SiteEmbed=false` (the frames of `/embed/*` say «Questo widget non è disponibile» ("This widget is not available"), the plugin hides its widgets, no container id is served, the connection of the plugin and its API `api/site/v1/*` answer 404 and a credential issued earlier opens nothing until the flag is back), `Features__Auth0HostSync=false` (nothing calls Auth0; the URLs already registered stay), `Features__WhiteLabelHost=false` (the agency's domain serves the booking site only; no surface in `resolve-host`; the links go back to the CasaZen domain; the admin endpoints answer 404; the phone proof stops). Nothing changes for an organization without a verified domain. **Links already sent on the agency's domain** (check-in and owner invitations) stop working until `WhiteLabelHost` is back; the booking-area links (`/my-bookings`) keep working, because that page belongs to the booking site, which the verified domain serves whatever the flag says; links sent on the CasaZen domain keep working.
4. **Remove the domain** (the agency administrator, in the console, choosing the platform path or a subdomain): the domain leaves the Vercel project at the next run of `domain-recheck` ([`seo-domain.md`](seo-domain.md) § 10). The agency's webmaster deletes the CNAME and the TXT.
5. **Deactivate the plugin** (the agency's webmaster): Plugins, Deactivate. The pages and the menu entries stay (they are the agency's content); Delete removes the plugin's settings only.
6. **Disconnect a site** (the agency administrator, or the plugin by itself): «Scollega» ("Disconnect") in the plugin, or the list «Siti collegati» ("Connected sites") of the page «Incorpora nel tuo sito» (section 12.3). The credential of that site stops working at once; the domain, the surfaces and the pages stay as they are. Deleting the plugin disconnects it too, and does not fail if CasaZen does not answer.

## 11. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `dns_not_pointing` | no CNAME to Vercel's target yet; the record is proxied (orange cloud); Vercel recommends a value that is not in the accepted list | the webmaster fixes the CNAME and sets «DNS only»; accepted values: [`seo-domain.md`](seo-domain.md) § 10 step 4; then «Controlla ora» |
| `ownership_txt_missing` | the TXT `_casazen-challenge.<host>` is missing, has another value, or has not propagated | create it exactly as the console shows; check with `dig +short TXT _casazen-challenge.<host>`; then «Controlla ora» |
| `vercel_not_configured` (the console says «L'attivazione dei domini personalizzati non è ancora disponibile sulla piattaforma») | `Vercel__ApiToken` or `Vercel__ProjectId` missing on this environment | step 1 (section 3); the domain activates by itself at the next run of `domain-recheck`. The other `vercel_*` and `domain_taken` codes: [`seo-domain.md`](seo-domain.md) § 10, Troubleshooting |
| The host answers «Sito non trovato» or `resolve-host` gives 404 for a verified domain | the plan no longer includes custom domains, or the **Production** deployment answers on a test host (its API does not know the organization) | check `canUseCustomDomain`; read `apiBase` (section 4) and connect the test host to the `develop` branch |
| Every `/portale-proprietari/*` address of the host is the agency's «not found» page | the owner portal switch is off (or `Features__WhiteLabelHost` is off): no owner page is served on the host | the agency switches «Portale proprietari sul mio dominio» on (the flag: section 7) |
| The owner sign-in page says «Accesso proprietari non ancora attivo» (state `NotRequested`) | the owner portal switch is on and nobody asked for the registration | ask it: the agency administrator calls the retry, `POST api/orgs/{orgId}/domain/owner-login` (section 6: the console draws «Riprova» only in `Failed`); with the sync off the state then goes to `Pending` (section 6, manual path) |
| The same page, state `Pending` | with the sync off: CasaZen has not done step 3 and 6 yet; with the sync on: `Auth0__HostSync__ClientId` or the Management credentials are missing (`NotConfigured`, nothing is called) | section 6, manual path; or set the variable **and then** ask the registration again, because setting it registers nothing: the agency administrator calls `POST api/orgs/{orgId}/domain/owner-login` (the console draws «Riprova» only in `Failed`) |
| The same page, state `Failed`, or the line «registrazione non riuscita» | the Management API call failed. The logs show `Auth0 host registration: <Result> (<code>)`: `http_403` is a missing scope (`read:clients`, `update:clients`), `http_404` a wrong `Auth0__HostSync__ClientId`; also `rate_limited`, `busy`, `read_back_mismatch`, `auth0_management_token_failed`, `auth0_management_credentials_rejected` | fix the cause, then the agency presses «Riprova» (`POST api/orgs/{orgId}/domain/owner-login`; 429 after five calls in ten minutes) |
| Auth0 shows «Callback URL mismatch» when an owner signs in on the host | the host is not in the three lists of the application that this deployment uses (test and production have different applications) | section 6, step 3 on the right application |
| The frame shows the link «Prenota su prenota.quokka360.com» instead of the widget | the frame did not load: embedding is off (`SiteEmbed` off, `embedEnabled` false), the domain is not verified or not paid (`resolve-host` 404), or a header or a security rule blocks the frame (`/embed/*` must answer `frame-ancestors https:`) | commands (a) and (b); the link itself still opens the booking site in the whole page |
| The page is empty and the site has a cache or optimization plugin | the plugin delays, minifies or combines the script | exclude every address containing `/embed/v1/` from those options in the cache plugin, clear the page cache ([plugin guide](https://github.com/casazen/frontend/blob/develop/integrations/wordpress/casazen/README.md)) |
| `host_not_found` in the plugin's test | the address or the slug is unknown: the agency's domain is not verified, a typo, `http` instead of `https`, or the CasaZen domain without the organization slug | check `resolve-host` (command a); write the address and, on the CasaZen domain, the slug |
| `embed_disabled` in the plugin's test | `Features__SiteEmbed` is off in the environment that answers on that address | section 7 |
| `manifest_unreachable` or `manifest_invalid` in the plugin's test | the host does not answer, or answers with the single-page app instead of JSON (a deployment without the embed files) | `curl` the manifest (section 4); deploy the web app that has the E41 build |
| A booking link in an e-mail is still on the CasaZen domain | `WhiteLabelHost` off; the domain is not verified or the plan lapsed; «Usa il mio dominio nei link che invio» is off; guest links need «Area ospite sul mio dominio», owner links need «Portale proprietari sul mio dominio» **and** a `Ready` sign-in; an e-mail sent before the change keeps its link | the line under the switch says which («Per ora i link restano quelli di CasaZen.»); `GET api/orgs/{orgId}/domain`, `surfaces.effective` |
| The right last four digits are refused | the code is locked after five wrong tries in 60 minutes (429), the booking has no phone (fewer than four digits), or it is not a direct booking | wait, or use the e-mail ([`direct-booking.md`](direct-booking.md) § 8.4.1) |
| A signed-in owner lands on «Questo account non ha accesso all'area proprietari» ("This account has no access to the owner area", `/portale-proprietari/accesso-negato`) | the owner is not an owner of **this** agency: the second requirement of the `OwnerPortal` policy wants this agency in the `agency_ids` claim of the owner | check that the owner accepted the invitation of this agency ([`cors-security-headers.md`](cors-security-headers.md)) |
| The plugin says the connection is no longer valid (its calls answer 401) | the credential does not open: it was revoked (in the console, by the plugin itself, or replaced by a newer connection of the same site), the organization is no longer active, or the call carried something that is not a credential of a site (the token of a person opens no address of `api/site/v1/*`). The answer never says which | the agency administrator connects again with «Connetti a CasaZen» (section 12.4); «Siti collegati» shows what is still active |
| Every address of the plugin's API answers 404 | `Features__SiteEmbed` is off in the environment of that console: the answer is the one of a missing route, before authentication | section 7 |
| Only `PUT api/site/v1/domain/surfaces` answers 404 | `Features__WhiteLabelHost` is off (the console's endpoint answers 404 too) | section 7 |
| The plugin's account or domain answer 403 `commercial_module_required` (with `module`: `direct-booking`) or `onboarding_required` | the agency's commercial sign-up did not buy the module of the direct booking, or its first payment is not confirmed: the console's page of the domain says the same. Disconnecting is never refused | the agency adds the module or completes the payment; nothing to change in the plugin |
| 403 `plan_required` on `PUT domain`, `POST domain/verify` or `PUT domain/surfaces` | the plan has no custom domain (Pro or Scale); the account still reads, with `domain.canUseCustomDomain` false | the agency changes plan ([`open-access.md`](open-access.md)) |
| 429 `rate_limited` from the plugin | more than 120 calls a minute from one credential, or more than 20 checks of the domain in ten minutes (section 12.5) | wait the seconds of `Retry-After`; a plugin that does it in a loop is disconnected and reported |
| 503 `site_public_address_not_configured` on the account | `App__PublicSiteBaseUrl` is not set and the organization has no domain of its own that is served (Development and Testing only: elsewhere the startup fails) | set the variable ([`seo-domain.md`](seo-domain.md)) |

## 12. Collegare il plugin all'account (connecting the plugin to the account)

Since the update of the plan of 27 November 2026 (§10, requirements NF-200 to NF-204) the plugin does not need the CasaZen address and the slug to be typed: it connects to the agency's account with one button, and from then on it manages the domain of that account by itself. This section is for whoever supports an agency: what the agency does, what the plugin calls, how to see and revoke the connections, what a 401 means and the limits. All of it is behind `Features__SiteEmbed` (section 7): with the flag off every address named here answers 404 and a credential issued earlier opens nothing. The page of the web app and the screens of the plugin that this flow uses come with the frontend slices of the same plan (`PLG-FE-CONSOLE`, `PLG-FE-PLUGIN`); the API they call is described in 12.2.

### 12.1 The flow, for the agency

WHO: the agency administrator (the person of the policy `RequireOrgBillingAdmin`) and the agency's webmaster, who can be the same person. The only step outside CasaZen and WordPress is the DNS.

1. In the CasaZen web app, page «Incorpora nel tuo sito»: press «Scarica il plugin» ("Download the plugin"). The button is always there (section 8).
2. In WordPress: Plugins, Add New, Upload Plugin, Activate. The screen of the plugin has one button: «Connetti a CasaZen» ("Connect to CasaZen").
3. The browser opens the CasaZen web app on `/app/connect/wordpress`, signed in as the administrator (it asks to sign in otherwise), and asks «Collegare il sito *www.quokka360.com* a *Quokka360*?». The address of the site is written in full: read it, the page exists so that nobody connects a site that is not theirs. Press «Collega» ("Connect"). The browser goes back to WordPress: «Collegato a Quokka360». The address and the slug are filled in; the next step is «Crea le pagine» (section 8).
4. On the same screen, in the section «Dominio» ("Domain"): write `prenota.quokka360.com` and press «Collega dominio». The DNS records appear (CNAME and TXT, each with a copy button) with the state, which refreshes by itself; when it is «Attivo» the plugin uses the address of the domain by itself. Under it the three choices of the console (guest area, owner portal, links of the e-mails: section 5, step 4).
5. Create the two DNS records where the DNS of the domain lives (section 4): the only step outside the two screens, done by whoever manages that DNS.

The platform steps do not change: `Vercel__ApiToken`, `Vercel__ProjectId` and `Vercel__TeamId` (section 3: without them every domain stays «in attesa») and, for the automatic owner sign-in, the Auth0 permissions and `Auth0__HostSync__ClientId` (section 6).

### 12.2 What the plugin calls

Everything is under `$API/api/site/v1/`, with `Authorization: Bearer czs_…` (the credential of the connection, which is not a sign-in of a person). The organization is the one of the credential: it is never in the address or the body, and a body that names another organization is not read. JSON in camelCase; errors as in the rest of the API, with a stable `code`.

| Call | What it does | Answers |
|---|---|---|
| `GET account` | who the plugin is connected to and where the organization is served | 200 `{ connectionId, org { id, slug, displayName }, embedEnabled, publicAddress, publicAddressKind, domain }`. `publicAddressKind` is `customDomain` (the verified domain of an organization that pays for it, served now: `publicAddress` is its origin) or `casazen` (the address of `App__PublicSiteBaseUrl`: the plugin keeps the slug); `embedEnabled` is what `resolve-host` answers; `domain` is the object of `GET api/orgs/{orgId}/domain` |
| `PUT domain` with `{ "customDomain": "prenota.quokka360.com" }` or `{ "customDomain": null }` | sets the custom domain, or takes the organization back to the CasaZen address; the member is required, `null` included (a body without it is a 400, never read as «go back») | 200 with the whole `account`, the DNS records being in `domain.dnsInstructions`; 400 `org_domain_invalid`; 409 `org_domain_in_use`; 403 `plan_required` with `requiredPlan` |
| `POST domain/verify` | checks the domain now (the ownership TXT, the DNS, the Vercel project), as «Controlla ora» does | 200 `{ verify, account }`, `verify` being the object of the console's check; 400 `org_domain_not_configured` without a domain; 403 `plan_required` |
| `PUT domain/surfaces` with `guestAreaOnOwnDomain`, `ownerPortalOnOwnDomain`, `useOwnDomainForLinks` | what passes from the domain; the three are required | 200 with the whole `account`, `domain.surfaces` updated; 404 while `Features__WhiteLabelHost` is off; 400 `org_domain_not_configured`; 403 `plan_required` |
| `DELETE connection` | the plugin disconnects itself («Scollega», and when it is deleted) | 204 |

Each call is one of the console's own operations: the same service (`OrgDomainService`), the same status and code for the same outcome (one mapping), the same objects (one mapper), so the plugin cannot read or do what the console would refuse. The four calls on the account and the domain need the commercial module `direct-booking` that an agency of a commercial sign-up has to have bought, like the console's page of the domain (403 `commercial_module_required` with the module, or `onboarding_required` while the first payment is not confirmed); the disconnection never does, so a lapsed module never leaves a credential that cannot be revoked.

For the support, with the credential of a TEST connection in `$CRED` (it is never written into a file, a ticket or a chat):

```bash
curl -sS -H "Authorization: Bearer $CRED" "$API/api/site/v1/account"
curl -sS -X PUT -H "Authorization: Bearer $CRED" -H "Content-Type: application/json" \
  -d '{ "customDomain": "prenota.quokka360.com" }' "$API/api/site/v1/domain"
curl -sS -X POST -H "Authorization: Bearer $CRED" "$API/api/site/v1/domain/verify"
```

### 12.3 Seeing and revoking the connections

- **In the web app** the agency administrator, and only an administrator of that organization, sees the active connections in the list «Siti collegati» ("Connected sites") of the page «Incorpora nel tuo sito» (the address of the site, when it was connected, when it was last used) and revokes one with «Scollega». The same by API: `GET api/orgs/{orgId}/site-connections` returns `[ { "id", "siteUrl", "createdAt", "lastUsedAt" } ]` (the active ones, the newest first: a revoked one is not listed) and `DELETE api/orgs/{orgId}/site-connections/{connectionId}` answers 204 (also for one that is revoked already) or 404 `site_connection_not_found` for a connection that is not the organization's; the id of another organization in the address is 403. Neither is ever behind the commercial module.
- **The plugin** disconnects itself with `DELETE api/site/v1/connection`, and does it when it is deleted from WordPress.
- **A new connection of the same site** (the same address, the same organization) revokes the earlier one: one active credential for each organization and site.
- A revoked credential is dead at once, for every address of `api/site/v1/*`, and cannot be revived: the site connects again.
- The credential is shown once, to the plugin, in the answer of the exchange of the code. The database keeps only its SHA-256, and the same for the one-time code (five minutes, one use, bound to the organization, the account, the site and a PKCE challenge); neither is ever written in a log, an address or an error. A lost credential is a new connection.

### 12.4 What a 401 from the plugin's calls means

The credential does not open, and the answer never says why. It is the same for all of these: it was revoked (from the web app, by the plugin itself, or by a newer connection of the same site); the organization is no longer active; the value is not one of ours (mistyped or cut); or the call carries a sign-in of a person (a token of the web app opens no address of `api/site/v1/*`, and a credential of a site opens no address of the console: 401 both ways). What to do: the agency administrator connects the site again (12.1, steps 2 and 3); the plugin keeps its address and slug. A 404 on every address of the plugin is another thing: `Features__SiteEmbed` is off (section 11).

### 12.5 The limits

Per credential, so that two sites behind one hosting address never share a budget (the settings are in [`proxy-ip.md`](proxy-ip.md)): `SiteApi`, 120 calls a minute, every call of the plugin; `SiteDomainVerify`, 20 checks of the domain in ten minutes (the check asks DNS and the Vercel API, so it has a budget of its own and does not use up the 120). The two calls that make a connection have their own: 10 confirmations in ten minutes for each administrator (`SiteConnectAuthorize`) and 20 exchanges of the code in ten minutes for each client address (`SiteConnectToken`). Above a limit the answer is 429 `rate_limited` with `Retry-After`. The plugin refreshes the state of the domain at most every 30 seconds, for at most 15 minutes and only while it is waiting, so an ordinary use never meets them.

### 12.6 Check on TEST

With the flags on (section 7) and a site connected: the list of 12.3 has the site and its `lastUsedAt` moves; `GET account` with its credential answers the organization with `publicAddressKind` `casazen`, or `customDomain` once the domain is verified and the plan allows it; `PUT domain` with a domain answers the records to create; the same credential on `$API/api/orgs/$ORG_ID/domain` answers 401; after «Scollega» every call answers 401.
