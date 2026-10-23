# ADR-007: ConcordatoFacile HMAC Signed Link Generation

**Status:** Accepted  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-05 (ConcordatoFacile HMAC signed link)

## Context

CasaZen generates time-limited signed URLs to redirect landlords to the ConcordatoFacile (CF) partner portal. The link carries lease identification and municipality data, signed with HMAC-SHA256.

**Requirements** (from `kb/intake/requirements.md` NF-05):
- HMAC-SHA256 signature per CF spec v1.3 section 2
- Link expires 15 minutes after generation
- Test vectors MUST match: leaseId `0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`, expiresAt `2026-10-13T10:00:00Z`, secret `test_link_secret_0123456789abcdef0123456789abcdef`, signature `fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea`
- Municipality from ISTAT territorial data (Q-A05 answer)
- Audit via LeaseEvent enum (Q-A06 answer)

**Existing code** (from `kb/gap/gap-analysis.md`):
- HMAC pattern in `ServiceCustomerIndex.cs:23-67` (`HMACSHA256.HashData(key, UTF8.GetBytes(normalized)).ToHexString().ToLowerInvariant()`)
- `LeaseEvent` entity for audit logging
- Property.City free text field (needs ISTAT FK for official names)

**Repo scope:** Backend (link service, ISTAT migration), Frontend (link button UI)

**Mobile:** NOT-APPLICABLE - mobile has no contract detail screens

## Decision

### HMAC-SHA256 Signature Algorithm

Create `ConcordatoFacileLinkService.GenerateSignedLinkAsync(leaseId)`:

1. Load secret from `CONCORDATOFACILE_LINK_SECRET` environment variable (>=32 chars)
2. Calculate `expiresAt = UtcNow + 15 minutes` in format `yyyy-MM-ddTHH:mm:ssZ`
3. Compute HMAC-SHA256 of exact string `leaseId.ToLowerInvariant() + "." + expiresAt` (pre-URL-encoding, UTF-8)
4. Output signature as hex lowercase
5. Build URL with parameters: `partner=casazen`, `leaseId`, `expiresAt`, `signature`, `comune`, `provincia`
6. URL-encode each value individually AFTER signature calculation

### ISTAT Territorial Data

Per Q-A05 answer, use ISTAT FK for accurate municipality data:

- Create `Comuni` table (~8000 rows from ISTAT CSV): `IstatCode` varchar(6) PK, `OfficialName`, `ProvinceCode` char(2), `RegionCode` char(2)
- Add `Property.CityIstatCode` varchar(6) nullable FK to `Comuni`
- Property form UI changes to city dropdown/autocomplete
- Link generation retrieves `OfficialName` and `ProvinceCode` from Comuni table

### Audit Logging via LeaseEvent

Per Q-A06 answer, add `LeaseEventType.ConcordatoFacileLinkGenerated` enum value:

- Record `LeaseContractId`, `UserId`, `OccurredAt`
- NO storage of URL, signature, or secret in database or logs

### Frontend State-driven Button

Three button states based on `/access` response and practice list:

1. `hasAccess=false`: "Procedi con la creazione del contratto" -> navigate to offer page
2. `hasAccess=true + practices.length=0`: "Accedi alla tua pratica" -> POST generate link -> `window.open(url, '_blank', 'noopener,noreferrer')`
3. `practices.length>0`: button hidden, status section visible

### Rejected Alternatives

- **Property.City free text for municipality**: Inaccurate names, no province codes, not aligned with CIN validation requirements
- **Separate ConcordatoFacileLinkGeneration entity**: Overkill, LeaseEvent sufficient for audit and frequency tracking

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-007-R1 | P0 | Signature MUST be HMAC-SHA256 of exact decoded string `leaseId.ToLowerInvariant() + "." + expiresAt` (before URL encoding), UTF-8 bytes, output hex lowercase |
| ADR-007-R2 | P0 | **TEST VECTOR CONFORMANCE:** Unit test `GenerateSignature_WithSpecTestVectors_ReturnsExpectedSignature` MUST verify: input leaseId `0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`, expiresAt `2026-10-13T10:00:00Z`, secret `test_link_secret_0123456789abcdef0123456789abcdef` produces signature `fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea` exactly; test MUST fail if algorithm or encoding is wrong |
| ADR-007-R3 | P0 | HMAC secret MUST be loaded from `CONCORDATOFACILE_LINK_SECRET` environment variable; missing in Production MUST fail startup; Development/Testing MAY use fixed placeholder with warning log |
| ADR-007-R4 | P0 | HMAC secret length MUST be >= 32 characters; shorter secret MUST fail startup with "ConcordatoFacile link secret must be at least 32 characters" |
| ADR-007-R5 | P0 | **NO-LOGGING:** Link generation service MUST NOT log secret value, signature, or full URL; log only leaseId, userId, timestamp; architecture test MUST verify no log calls with "secret" parameter in ConcordatoFacileLinkService |
| ADR-007-R6 | P0 | URL construction MUST include parameters: `partner=casazen`, `leaseId=<uuid-lowercase>`, `expiresAt=<timestamp>`, `signature=<hex>`, `comune=<official-name>`, `provincia=<province-code>`, with each value URL-encoded individually AFTER signature calculation |
| ADR-007-R7 | P0 | Municipality and province MUST be retrieved from Comuni table JOIN on Property.CityIstatCode: `SELECT OfficialName, ProvinceCode FROM Comuni WHERE IstatCode = property.CityIstatCode`; return 422 `property_municipality_unknown` if CityIstatCode IS NULL or Comuni row not found |
| ADR-007-R8 | P0 | Link generation MUST record LeaseEvent with EventType=`ConcordatoFacileLinkGenerated`, LeaseContractId, UserId, OccurredAt; MUST NOT store URL or signature in database |
| ADR-007-R9 | P0 | Comuni table migration MUST import ~8000 rows from ISTAT CSV with schema: `IstatCode` varchar(6) PK, `OfficialName` varchar(255), `ProvinceCode` char(2), `RegionCode` char(2); Property.CityIstatCode varchar(6) nullable FK to Comuni |
| ADR-007-R10 | P0 | `POST /api/leases/{leaseId}/concordatofacile/link` MUST authorize with LeaseCreate policy (write-level action) and verify access via `IConcordatoFacileAccessService` before generating link |
| ADR-007-R11 | P0 | Link endpoint response MUST use `Cache-Control: private, no-store` header to prevent caching of temporary signed URL |
| ADR-007-R12 | P0 | Frontend link button MUST execute `window.open(url, '_blank', 'noopener,noreferrer')` to prevent opener reference leakage |
| ADR-007-R13 | P0 | Frontend state (2) button MUST display warning "Il modulo riprende solo sullo stesso dispositivo/browser utilizzato" below button |
| ADR-007-R14 | P1 | Status read failure (no cached snapshot + API error) MUST show state (2) button with warning "Non riusciamo a verificare lo stato: se hai gia inviato il modulo non riaprirlo" |
| ADR-007-R15 | P1 | i18n keys: `concordatofacile.entry.{proceed, access, warning, statusFailed, generating, error}` (it/en) |
| ADR-007-R16 | P1 | data-testid: `cf-entry-button`, `cf-access-warning` |

## Consequences

**Follow-on work:**
- Backend: `ConcordatoFacileLinkService` with HMAC-SHA256 implementation
- Backend: Comuni table migration with ISTAT CSV import (~8000 rows)
- Backend: Property.CityIstatCode FK column and entity update
- Backend: Configuration validation for link secret
- Frontend: Property form UI change to city dropdown/autocomplete
- Frontend: State-driven link button component
- Tests: Unit test with exact test vectors (CRITICAL for partner integration)
- Tests: Architecture test verifying no logging of secrets

**Risks:**
- Test vectors MUST match exactly (hex lowercase, UTF-8, no URL encoding before signature); wrong algorithm makes all links fail at CF
- Clock skew: CF tolerates 2-minute skew (spec); Railway/Vercel use NTP
- Seveso pilot case: ISTAT code 108044, official name "Seveso", province "MB"

**Objections resolved:**
- O-adr-security-1 (P0): Addressed by ADR-007-R5 (no-logging requirement)
- O-adr-security-2 (P0): Addressed by ADR-007-R2 (test vector unit test)

**CI cost:** ~10s for test vector conformance test

**Cross-repo contract:**
- Backend: `POST /api/leases/{leaseId}/concordatofacile/link` returns `{url: string}`
- Frontend: Opens URL in new tab with noopener,noreferrer
