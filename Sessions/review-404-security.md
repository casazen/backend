# Stage 04 Security Audit — PR #404

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/404 |
| Title | `feat(onboarding): Stage 03 PLG activation + Marketing consent (#271)` |
| Base | `develop` |
| Head | `feature/271-onboarding-plg` |
| Work unit | Delivery Stage 03 `SPEC:onboarding-plg` / Issue #271 |
| Auditor | Stage 04 security-auditor (fresh context) |
| Date | 2026-08-13 |
| Scope sources | `Sessions/design-271.md` (Security Notes + API Contract); `git diff origin/develop...feature/271-onboarding-plg` for listed runtime/test files only |
| Verdict | **APPROVE** |

## Scope reviewed (ONLY)

1. `Sessions/design-271.md` — **API Contract** + **Security Notes**
2. Diff `origin/develop...feature/271-onboarding-plg`:
   - `Casazen.Infrastructure/Services/OnboardingService.cs`
   - `Casazen.Core/Entities/Enums/ConsentType.cs`
   - `Casazen.Web/Controllers/OnboardingController.cs` (**no diff** vs `develop`; audited as current contract surface)
   - `Casazen.Tests/Integration/PlgOnboardingIntegrationTests.cs`
3. OWASP checklist (this PR): AuthZ on status; `[AllowAnonymous]` only on legal; no IDOR via OrgId in path; no secrets; marketing consent append-only

**Out of scope for this audit:** FE ProtectedRoute (frontend 403 / not in BE PR), LegalController body (unchanged; class-level `[AllowAnonymous]` confirmed as existing public-legal surface), Stripe, Guest Art. 17 flows.

---

## Design contract (Security Notes + API Contract)

| Endpoint | Auth (design) | Notes |
|---|---|---|
| `POST/PUT /api/users/onboarding` | `[Authorize]` | JWT `sub` subject; consents append-only |
| `GET /api/onboarding/status` | `[Authorize]` | Scoped `sub` → User → OrgId; **no OrgId in path** |
| `GET /api/legal/*` | `[AllowAnonymous]` | Public legal transparency / pre-consent (AC4) |

Design IDOR rule: status + consent writes keyed by JWT `sub` only. Secrets: N/A. Marketing opt-in → `ConsentType.Marketing` when true.

---

## OWASP checklist (implementation)

### 1. AuthZ on `GET /api/onboarding/status`

| Check | Evidence | Result |
|---|---|---|
| Controller auth | `OnboardingController` class `[Authorize]`; route `api/onboarding` | **PASS** |
| No `[AllowAnonymous]` on status | Absent on controller/action | **PASS** |
| Subject binding | `GetSub()` from JWT `sub` / NameIdentifier; `GetActivationStatusAsync(sub)` | **PASS** |
| Unauthenticated | Design + existing surface → 401 | **PASS** |

### 2. `[AllowAnonymous]` only on legal

| Check | Evidence | Result |
|---|---|---|
| Onboarding status | `[Authorize]` only | **PASS** |
| Legal public docs | Design + existing `LegalController` `[AllowAnonymous]` (not modified in this PR) | **PASS** (justified public-legal) |
| New anonymous endpoints in scoped diff | None | **PASS** |

### 3. No IDOR via OrgId in path

| Check | Evidence | Result |
|---|---|---|
| Path params | `GET /api/onboarding/status` — no OrgId / PropertyId | **PASS** |
| Org resolution | `user.OrgId` from DB row for JWT `userId` | **PASS** |
| Property / booking / org queries | Filtered by that `orgId` only (`IgnoreQueryFilters` + OrgId predicate) | **PASS** |
| Client-supplied Org targeting | Not accepted on status | **PASS** |

### 4. No secrets

| Check | Evidence | Result |
|---|---|---|
| Hardcoded API keys / tokens / connection strings in scoped diff | None | **PASS** |
| `App:PublicSiteBaseUrl` | Read via `IConfiguration`; default public URL `https://casazen.app` | **PASS** |
| `appsettings*.json` in PR | Not in scoped files | **PASS** |
| `publicBookingUrl` | Derived from config base + `org.Slug` when site published | **PASS** (intentional public share URL) |

### 5. Marketing consent append-only

| Check | Evidence | Result |
|---|---|---|
| Enum | `ConsentType.Marketing` added (int storage; no destructive migration) | **PASS** |
| Persist when opt-in | `if (consents.MarketingOptIn == true) records.Add(...)` | **PASS** |
| Write path | `db.ConsentRecords.AddRange(records)` — no Update/Delete of prior consent rows | **PASS** (append-only) |
| Opt-out / false | No Marketing row when not opted in (correct for optional opt-in) | **PASS** |
| Tests | AC1 asserts Marketing row + IP when `marketingOptIn: true` | **PASS** |

---

## Attack surface table (scoped runtime)

| Surface | Result | Notes |
|---|---|---|
| Auth0 JWT / `[Authorize]` on status | **PASS** | Class-level Authorize; sub-bound |
| IDOR (`OrgId` in path / cross-tenant) | **PASS** | No OrgId path; org from caller user |
| EF Core / raw SQL injection | **PASS** | EF LINQ only; no `FromSqlRaw` concat |
| Stripe webhook | **N/A** | Not in surface |
| Guest PII in API errors/logs | **PASS** | Production path unchanged; Guest only in test seed |
| Secrets / appsettings | **PASS** | Config key only |
| Marketing consent integrity | **PASS** | Append-only opt-in rows |

---

## Compliance / GDPR (operator consent)

| Gate | Result | Notes |
|---|---|---|
| Consent write validation | **PASS** | Required Tos/Privacy/Dpa/SubprocessorsAck + server version check before any insert |
| Marketing opt-in | **PASS** | Separate `ConsentType.Marketing` row; append-only |
| Guest Art. 17 / CIN / Alloggiati | **N/A** | No production Guest creation in service; test seed only |
| IP on consent rows | **PASS** (retained) | Tests still assert IP; design: no IP in error responses |

---

## Findings by severity

### 🔴 Critical

0 findings.

### 🟡 High

0 findings.

### 🟢 Medium / informational

1. **`consentsAccepted` read-path still weaker than design wording** (`OnboardingService.GetActivationStatusAsync`): design says required consent types at **current versions**; implementation checks `Any` Tos for `userId`. Write path still enforces all four + versions. Not AuthZ/IDOR; UX/activation signal accuracy only — track for follow-up hardening (not blocking).

2. **Marketing row `Version = consents.TosVersion`:** append-only Marketing evidence reuses ToS version string rather than a dedicated marketing-policy version. Acceptable for MVP demonstrability; prefer a dedicated version constant later.

3. **Trusted proxy for consent IP (carry-forward):** IP still recorded from forwarded headers on onboarding POST (UsersController, outside this diff). Ensure only reverse-proxy hops are trusted so Art. 7 evidence cannot be trivially spoofed.

4. **FE Stage 03 not in this BE PR:** ActivationChecklist / public `/legal/subprocessors` / `<ProtectedRoute>` ordering remain frontend SoT; backend cannot certify FE auth gates here (write access to `casazen/frontend` blocked per PR body).

---

## Integration test security coverage (scoped)

| Test | Security-relevant assert | Result |
|---|---|---|
| Marketing opt-in POST | 5 consent rows incl. `ConsentType.Marketing`; IP retained | **PASS** |
| Status after onboard | Auth client; checklist flags; `activated` false until milestones | **PASS** |
| Six-bool activated | Caller-scoped status; `publicBookingUrl` contains `/book/` | **PASS** |
| PUT without consents | Still authenticated path; no consent rewrite required | **PASS** |

No test injects foreign OrgId into status path (none exists) — IDOR absence aligns with API shape.

---

## Merge recommendation

| Metric | Value |
|---|---|
| 🔴 Critical (open) | **0** |
| 🟡 High (open) | **0** |
| AuthZ on status | **PASS** |
| AllowAnonymous only on legal | **PASS** |
| IDOR (no OrgId in path) | **PASS** |
| Secrets | **PASS** |
| Marketing consent append-only | **PASS** |
| **Verdict** | **APPROVE** |
| **Merge OK (security)** | **yes** |

No open 🔴. Stage 04 security gate satisfied for PR #404 scoped BE changes. Do **not** merge from this auditor (orchestration / CI green owns merge).

**Verdict: APPROVE**
