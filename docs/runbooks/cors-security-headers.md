# Runbook: CORS, security headers and logs without personal data

Task FD-17 (audit defects A3-29, A9-28, A9-29 header part, A1-34, A9-36, A2-32 claim logging). The code is in
place; the product owner sets the Railway variables of section 1 and follows section 4 to turn the web app CSP from
report-only to enforced.

## What the code does

| Concern | Behaviour | Code |
|---|---|---|
| CORS origins | The origin of the public web app `App__PublicSiteBaseUrl` (SE-02, always: the domain may change without touching CORS), the exact origins of `Cors__AllowedOrigins`, plus the Vercel previews matched by `Cors__VercelPreviewPattern` when it is set. No origin is written in code (decision D3). In `Development` any `http://localhost:<port>` is accepted (Vite dev server) | `Casazen.Web/Configuration/CorsOriginOptions.cs` |
| CORS policy | Methods `GET POST PUT PATCH DELETE OPTIONS`, headers `Authorization Content-Type Accept X-Requested-With`, **no credentials**: the API authenticates with a Bearer token and sets no cookie | `Casazen.Web/Infrastructure/CasazenCorsPolicyProvider.cs` |
| Custom domains | Extension point `ICorsOriginSource` (asked only about origins the configuration rejects). Nothing is registered yet: the hosts' custom domains are task BK-16 | same file |
| Startup check | A malformed entry (path, wildcard, no scheme) or an invalid pattern stops the startup. Outside `Development`/`Testing` it stops too when neither `Cors__AllowedOrigins` nor `App__PublicSiteBaseUrl` gives an origin (Railway keeps the previous deployment) | `CorsOriginOptionsValidator` |
| API headers | Every response: `Content-Security-Policy: frame-ancestors 'none'`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`. Outside `Development`/`Testing` (both Railway environments) also `Strict-Transport-Security: max-age=31536000; includeSubDomains` | `Casazen.Web/Middleware/SecurityHeadersMiddleware.cs` |
| Web app headers | `vercel.json` → `headers`: see section 3 | frontend `vercel.json` |
| Logs | No email address in clear (`LogRedaction.MaskEmail` → `m***@example.it`), no names, no JWT claims. Provider error messages are masked too (`LogRedaction.MaskEmails`) | `Casazen.Core/Utilities/LogRedaction.cs` |
| Hangfire dashboard key | `X-Hangfire-ApiKey` compared in constant time (SHA-256 of both values + `CryptographicOperations.FixedTimeEquals`) | `Casazen.Web/Infrastructure/HangfireAuthorizationFilter.cs` |
| Web app console | ESLint `no-console` (only `warn`/`error` allowed) on `src/`; the access token is never logged | frontend `eslint.config.js`, `src/lib/axios.ts` |

## 1. Railway variables (per environment)

| Variable | Value | Required |
|---|---|---|
| `Cors__AllowedOrigins` | **Other** origins of the web app of **this** environment, comma separated: scheme + host (+ port), no path, no trailing wildcard. The origin of `App__PublicSiteBaseUrl` (the public domain, [`seo-domain.md`](seo-domain.md)) is always allowed and need not be repeated; list here e.g. a second host that still serves the web app | only when the web app is reached from an origin other than `App__PublicSiteBaseUrl` |
| `Cors__VercelPreviewPattern` | Regular expression for the **label** before `.vercel.app` of our own previews. Anchored and case-insensitive; only `https` on the default port; a label with a dot never matches | no; set it on **test only**, leave production empty |

Vercel preview hostnames are `<project>-<hash>-<team-slug>.vercel.app` (one deployment) and
`<project>-git-<branch>-<team-slug>.vercel.app` (branch). Copy two real preview URLs from Vercel → Project →
Deployments and write the tightest pattern that matches them, for example (project `casazen-app`, team slug
`casazen-team`):

```
Cors__VercelPreviewPattern=casazen-app-(git-[a-z0-9-]+|[a-z0-9]{9})-casazen-team
```

Security note: the pattern is a convenience for the test environment, not a security boundary. A `*.vercel.app` name
is first come, first served: someone could name a project so that its production URL matches the pattern. Because the
policy has no credentials and the token lives only in the storage of the real web app origin, such a page can only
send requests without the user's token. Keep the pattern tight and never set it on production.

Checks after a deploy (replace the URLs):

```bash
API=https://<railway url of the environment>
# Allowed origin: the answer contains "access-control-allow-origin: <origin>" and NO "access-control-allow-credentials"
curl -si -X OPTIONS "$API/api/properties" -H "Origin: https://<web app host>" \
  -H "Access-Control-Request-Method: GET" -H "Access-Control-Request-Headers: authorization" | grep -i "^access-control"
# Foreign origin: no access-control-allow-origin at all
curl -si -X OPTIONS "$API/api/properties" -H "Origin: https://evil.vercel.app" \
  -H "Access-Control-Request-Method: GET" | grep -i "^access-control" || echo "rejected (expected)"
# Security headers, HSTS included
curl -sI "$API/api/health/live" | grep -iE "strict-transport|content-security|x-frame|x-content-type|referrer"
```

If the startup fails with `Cors__AllowedOrigins is missing` or `... is not an origin`, fix the variable: the previous
deployment keeps running meanwhile.

## 2. API security headers

Nothing to configure. HSTS is sent on both Railway environments (they run with `ASPNETCORE_ENVIRONMENT=Production`),
never locally. There is no `preload`: adding the API host to the browsers' preload list cannot be undone quickly and
would need the parent domain on HTTPS everywhere. The Hangfire dashboard and the supplier registration page served
by the API are not meant to be framed either.

## 3. Web app headers (`vercel.json`)

Two disjoint rules, so a path never gets two values of the same header:

| Paths | Framing (enforced) | CSP |
|---|---|---|
| everything except `/book/…` | `Content-Security-Policy: frame-ancestors 'none'` + `X-Frame-Options: DENY` | report-only, `frame-ancestors 'none'` |
| `/book/…` (public booking site) | `frame-ancestors 'self'` + `X-Frame-Options: SAMEORIGIN`: the "Vetrina" settings page previews the site in a same-origin iframe | report-only, `frame-ancestors 'self'` |

Both also send `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and
`Permissions-Policy: camera=(), microphone=(), geolocation=(self)` (geolocation stays for the supplier check-in page
until task SU-11 removes it). Vercel adds HSTS on its domains by itself.

The CSP (`Content-Security-Policy-Report-Only`) is:

| Directive | Value | Why |
|---|---|---|
| `default-src` | `'self'` | |
| `script-src` | `'self' https://js.stripe.com https://*.js.stripe.com` | the Vite build has no inline script; Stripe.js |
| `style-src` | `'self' 'unsafe-inline'` | styles injected at runtime by UI libraries (toasts, Radix); inline styles cannot run code |
| `img-src` | `'self' data: blob: https:` | Supabase Storage photos (`Storage__PublicBaseUrl` can be any host), Auth0/Google/Gravatar avatars, org logos, local previews of uploads (`blob:`) |
| `font-src` | `'self' data: https://*.supabase.co` | fonts from the storage |
| `connect-src` | `'self' https:` | the API origin comes from `VITE_API_BASE_URL` at build time and `vercel.json` cannot read environment variables, so every https origin is allowed; it also covers Auth0 (`/oauth/token`), `api.stripe.com` and Supabase |
| `frame-src` | `'self' https://js.stripe.com https://*.js.stripe.com https://hooks.stripe.com https://*.auth0.com` | Vetrina preview, Stripe Elements and 3-D Secure, Auth0 silent authentication (only if the refresh-token fallback is ever enabled) |
| `worker-src` | `'self' blob:` | |
| `object-src`, `base-uri`, `form-action` | `'none'`, `'self'`, `'self'` | |

The public domain appears nowhere in the policy: the web app refers to its own origin only as `'self'`, so the CSP
follows whatever domain is configured (SE-02, `src/test/vercel-routing.test.ts` fails if a domain of ours is written in
`vercel.json`).

No `'unsafe-eval'`: the app sets `z.config({ jitless: true })` (`src/lib/zod-config.ts`) so Zod never probes
`new Function()`.

Local check done for FD-17: `vite preview` of a production build answering with these headers, loaded in Chromium
with the policy **enforced**: the public pages, `/login`, `/checkin/…`, `/book/…`, `/p/…` and the host pages in demo
mode rendered with no CSP violation and no page error; `/book/…` loads in a same-origin iframe, `/app` and `/login`
do not. Auth0 login and a Stripe payment cannot be exercised locally, hence report-only.

## 4. From report-only to enforced

1. Deploy to the test environment (Vercel `develop` deployment) and open the browser DevTools console.
2. Walk through: Auth0 login and logout (a full redirect round trip, then a page reload so the refresh token is used),
   a direct booking checkout with a Stripe **test** card that triggers 3-D Secure (`4000 0027 6000 3184`), the
   Vetrina preview, a photo upload (preview thumbnails), a PDF/CSV download, the booking site `/book/<slug>`.
3. Look for console messages starting with `[Report Only] Refused to …`. Each one names the directive and the
   blocked URL: add that origin to the directive in **both** rules of `vercel.json` (and to
   `src/test/vercel-headers.test.ts`) only if it is legitimate.
   - Auth0 custom domain (e.g. `login.<domain>`): add it to `frame-src` only if silent authentication by iframe is used.
   - Vercel Toolbar (comments on previews) needs `https://vercel.live`: disable the toolbar for the project instead
     of loosening the production policy.
4. When the walk-through is clean, in both rules of `vercel.json` rename the key
   `Content-Security-Policy-Report-Only` to `Content-Security-Policy` and delete the separate
   `Content-Security-Policy: frame-ancestors …` entry (the full policy already contains `frame-ancestors`). Update
   `src/test/vercel-headers.test.ts` accordingly, deploy to test, repeat step 2, then promote.

## 5. Logs

- Never log an email, a name, a phone number, a token or the JWT claims. Log ids (`UserId` = Auth0 `sub`, `OrgId`,
  `BookingId`…). When an address really helps, log `LogRedaction.MaskEmail(email)` under a `{MaskedEmail}`
  placeholder.
- A third-party error message can quote an address: pass it through `LogRedaction.MaskEmails(...)` before logging
  it or storing it as an error detail.
- Web app: `console.log/info/debug` fail `npm run lint`; `console.warn/error` are for real problems and never carry
  tokens or personal data.
