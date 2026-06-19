# Design — Batch F0 implementation (#287, #288, #289, #301)

Epic parent: #286. Branch: `feature/batch-f0-implementation`.

## Scope summary

| Issue | Layer | Deliverable |
|---|---|---|
| #288 | BE + FE contract | `GET /api/public/resolve-host?host=` |
| #289 | BE spike | `ICalImportSpike` + unit tests (no EF migration) |
| #301 | FE E2E | `golden-journey-web.spec.ts` steps 1–4 sequential |
| #287 | mobile repo | Expo scaffold + `scripts/init-mobile-repo.ps1` (sibling repo) |

## API Contract

### New — resolve-host (#288)

| Method | Path | Auth | Request | Response |
|---|---|---|---|---|
| GET | `/api/public/resolve-host` | Public (`[AllowAnonymous]`) | `?host={hostname}` | 200 `ResolveHostResponseDto` / 404 |

**ResolveHostResponseDto:**
```json
{
  "orgId": "uuid",
  "slug": "villa-mare",
  "publicHostMode": "CasazenSubdomain",
  "branding": { "slug", "displayName", "logoUrl", "themeColor", "contactEmail" }
}
```

**Resolution rules (F0):**
- `{slug}.casazen.it` → lookup org by slug via `IOrgService.GetPublicBySlugAsync`
- Reserved subdomains: `www`, `api`, `app`, `admin`, `staging`, `test`, `mail` → 404
- Custom domain CNAME → **out of scope F0** (Fase 1 US-024)

### Existing — unchanged

| Method | Path | Auth |
|---|---|---|
| GET | `/api/public/orgs/{slug}` | Public |
| GET | `/api/bookings/calendar` | `[Authorize]` |

### iCal (#289) — spike only

No new HTTP endpoints in F0. Internal spike:
- `ICalImportSpike.ParseImport(ics)` → `CalendarBlockSlice[]`
- `ICalImportSpike.BuildExportFeed(blocks)` → `text/calendar` string
- `ICalImportSpike.Overlaps(start, end, blocks)` → bool

## Frontend Flow

### GJ E2E (#301)

| File | Change |
|---|---|
| `e2e/golden-journey-web.spec.ts` | Single sequential test GJ steps 1–4 (demo mocks via branded-booking helpers) |
| `e2e/helpers/golden-journey-mock.ts` | **Removed** — use `branded-booking-mock.ts` |

Steps 5–12: `test.fixme` until Fase 1.

### resolve-host consumer (F1)

FE edge middleware will call resolve-host before render; F0 adds E2E contract test with mocked API response.

## Security Notes

- `resolve-host` returns **public branding only** — no Stripe IDs, plan tier, or PII
- Host allowlist prevents resolving reserved infrastructure subdomains
- iCal export spike: no guest PII in SUMMARY (Fase 1 enforces on entity export)
- All new authenticated routes: N/A (only public endpoint added)

## Migration Plan

N/A — no schema changes. `PublicHostMode` enum and resolver use existing `Org.Slug`.

## GDPR Scope

N/A — no Guest entity changes.

## Open Questions

None.
