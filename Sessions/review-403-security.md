# Stage 04 Security Audit — PR #403

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/403 |
| Title | docs(sdlc): Stage 02 design for onboarding-plg (#271) |
| Base | `develop` |
| Head | `feature/271-onboarding-plg` |
| Work unit | Delivery Stage 02 design `SPEC:onboarding-plg` / Issue #271 |
| Auditor | Stage 04 security-auditor (fresh context) |
| Date | 2026-08-13 |
| Evidence | Stage 02 gate-runner `Sessions/loop/evidence/delivery-13/` (claimed overall pass G1–G10, G9b) — design artifact only |

## Scope reviewed

- Auditor brief: `.claude/sdlc/04-review/agents/security-auditor.md`
- Guardrails: `.claude/rules/security.md`, council-security-engineer skill
- `gh pr view 403` / `gh pr diff 403` (`casazen/backend`)
- Design: `Sessions/design-271.md` (primary)
- Spec cross-check: `Sessions/specs/spec-onboarding-plg.md` (AC1–AC12)
- Context only (not in PR diff; used to validate design claims vs tree): `LegalController`, `OnboardingController`, `UsersController` onboarding path, `OnboardingService`
- Changed files (5):
  1. `Sessions/design-271.md` — Stage 02 design (API/FE/security/GDPR/AC Test Map)
  2. `e2e/README.md` — scaffold notice
  3. `e2e/onboarding-plg.spec.ts` — L2 path scaffold
  4. `e2e/l3/onboarding-plg-l3.spec.ts` — L3 path scaffold
  5. `scripts/quality/check-ac-matrix.ps1` — `Get-RepoParent` path resolution for Cloud `/workspace`

**No new runtime controller, EF migration, Stripe, or appsettings changes in this PR.** Design documents surfaces that already exist partially in tree; this audit grades the **design contract**, not Stage 03 implementation PASS.

---

## Focus area assessment

### 1. Auth gates — `[Authorize]` vs `[AllowAnonymous]` for `/api/legal/*`

| Endpoint | Design auth | Justification | Result |
|---|---|---|---|
| `POST/PUT /api/users/onboarding` | `[Authorize]` | JWT `sub` is subject user; consents + Org provision | **PASS** |
| `GET /api/onboarding/status` | `[Authorize]` | Activation checklist is tenant-scoped | **PASS** |
| `GET /api/legal/subprocessors` | `[AllowAnonymous]` | GDPR transparency / pre-consent public list (AC4) | **PASS** |
| `GET /api/legal/dpa` | `[AllowAnonymous]` | Public legal doc metadata | **PASS** |
| `GET /api/legal/tos` | `[AllowAnonymous]` | Public legal doc metadata | **PASS** |
| `GET /api/legal/privacy` | `[AllowAnonymous]` | Public legal doc metadata | **PASS** |

Anonymous legal endpoints are limited to versioned **document metadata** (`version`, `effectiveAt`, `title`, `summary`, `documentUrl?`) and subprocessor name/purpose/region — not account or Guest data. Aligns with CasaZen public-legal pattern (e.g. ADR-001 public branding). Explicit justification present in API contract + Security Notes.

FE: `/onboarding` behind `<ProtectedRoute>`; `/legal/subprocessors` intentionally public. Demo-mode bypass called out for AC12 regression only.

### 2. IDOR — `GET /api/onboarding/status` (`sub` → Org only)

| Check | Design | Result |
|---|---|---|
| Path params with OrgId / PropertyId | None — status has empty path | **PASS** |
| Scoping rule | JWT `sub` → User → `OrgId` only | **PASS** |
| Consent writes | Caller `UserId` + provisioned `OrgId` | **PASS** |
| Cross-tenant OrgId client supply | Not accepted on status | **PASS** |

Design correctly forbids client-supplied Org targeting. (Tree context: `OnboardingController` already calls `GetActivationStatusAsync(sub)` — Stage 03 must keep this contract; do not add OrgId query/body.)

### 3. Consent IP / PII handling

| Check | Design | Result |
|---|---|---|
| IP purpose | Art. 7 consent demonstrability (`IpAddress` nullable, max 100) | **PASS** |
| Source | `X-Forwarded-For` / remote IP | **PASS** (documented) |
| Error responses | Generic validation + `staleDocuments` codes; **no IP echo** | **PASS** |
| Logging | Do not log full consent payloads with unnecessary PII | **PASS** |
| Guest fields | Explicitly absent from this flow | **PASS** |

### 4. GDPR scope accuracy (no Guest PII)

| Check | Design | Result |
|---|---|---|
| In scope | Operator consent evidence (type, version, `RecordedAt`, IP); DPA/controller-processor; subprocessors; optional marketing | **PASS** |
| Out of scope | Guest name/DOB/document — **N/A** | **PASS** |
| Minimization | Role, org, consent metadata only | **PASS** |
| Marketing | Stored as `ConsentType.Marketing` when true (opt-in) | **PASS** |

No invented Guest erasure / Alloggiati / CIN surface in this design — correct.

### 5. Secrets in design / diff

| Check | Result |
|---|---|
| Credential / token / private key / connection string patterns in `gh pr diff 403` | **PASS** — none (only mentions of Auth0/Stripe/SendGrid as **subprocessor names**, `getAccessTokenSilently`, `VITE_DEMO_MODE`, `E2E_AUTH0_EMAIL` env **names**) |
| OTA/Stripe secrets introduced | **N/A** — design states none; legal content from `ILegalDocumentService` constants |
| `appsettings*.json` | Not in PR |

**Secrets hygiene: PASS**

### 6. `scripts/quality/check-ac-matrix.ps1` (path resolution)

| Check | Result |
|---|---|
| Change type | `Get-RepoParent` hardening when `Split-Path` returns empty on single-segment roots (`/workspace`) |
| User input / auth / secrets | None |
| Risk | Low — quality-gate filesystem candidate resolution only; does not serve HTTP or change auth |

**PASS** (low risk)

---

## Attack surface table (PR diff)

| Surface | Result | Notes |
|---|---|---|
| Auth0 JWT / new `[Authorize]` | **Design PASS** | Mutating + status endpoints require JWT; legal anonymous justified |
| IDOR (Org/property/booking) | **Design PASS** | Status keyed by `sub` → Org; no OrgId in path |
| EF Core / raw SQL | **N/A** | No Infrastructure SQL in diff |
| Stripe webhook signature | **N/A** | Not in surface |
| Guest PII (errors / logs) | **Design PASS** | Explicitly out of scope |
| Secrets / appsettings | **PASS** | Diff clean |
| Frontend `<ProtectedRoute>` | **Design PASS** | `/onboarding` protected; legal page public by intent |

**Do not treat this PR as Stage 03 runtime security PASS.** Controllers already in tree must be re-audited when Stage 03 closes remaining AC gaps / L2–L3 tests.

---

## Compliance gates (design stage)

| Gate | Result | Notes |
|---|---|---|
| G5 No IDOR | **Design PASS** | Contract forbids cross-tenant status |
| G6 No raw SQL | **N/A → PASS** | No SQL in PR |
| G7 PII not exposed | **Design PASS** | No IP in errors; no Guest PII |
| G8 Stripe signature | **N/A → PASS** | Not touched |
| G9 GDPR guest fields | **N/A → PASS** | No Guest flows |
| G10 Frontend auth routes | **Design PASS** | ProtectedRoute + intentional public legal |

---

## Findings by severity

### 🔴 Critical

0 findings.

### 🟡 High

0 findings.

### 🟢 Medium / informational

1. **Trusted proxy for consent IP (Stage 03):** Design records IP from `X-Forwarded-For`. Without `ForwardedHeaders` / known-proxy configuration, clients can spoof Art. 7 evidence. Stage 03 should confirm only the reverse-proxy hop is trusted and that IP is never returned in API responses (already stated).

2. **`documentUrl?` on public legal DTOs (Stage 03):** Ensure URLs are server-controlled HTTPS (or relative) constants — not client-influenced — to avoid open-redirect / content injection via metadata.

3. **Demo mode (`VITE_DEMO_MODE`) (Stage 03 FE):** AC12 requires demo bypass of Auth0. Confirm production builds cannot enable demo auth bypass; keep `/onboarding` behind `<ProtectedRoute>` in real sessions.

4. **Operator consent retention vs Art. 17 (informational):** Guest erasure is correctly N/A. Account/org deletion later should define retention of `ConsentRecord` rows (legal demonstrability vs erasure) — out of this design’s MVP scope, track for GDPR account lifecycle.

5. **Design ≠ runtime PASS:** Partial BE already exists (`LegalController` `[AllowAnonymous]`, `OnboardingController` `[Authorize]`, consent IP via `UsersController.GetClientIpAddress`). Stage 03 + Stage 04 must re-verify versioned `consentsAccepted`, activation derivation, and IDOR tests — design PASS does not close implementation gaps.

6. **E2E scaffolds:** L2/L3 files are path-exists stubs (`expect(true)` / skip without Auth0). They do not yet assert auth/IDOR; Stage 03 must expand titled AC tests without inventing security coverage from scaffolds alone.

7. **`check-ac-matrix.ps1`:** Parent fallback to filesystem root separator is quality-gate only; negligible security impact.

---

## Merge recommendation

| Metric | Value |
|---|---|
| 🔴 Critical (open) | **0** |
| 🟡 High (open) | **0** |
| Secrets hygiene | **PASS** |
| Auth / AllowAnonymous justifications | **PASS** |
| IDOR design (`sub` → Org) | **PASS** |
| Consent IP / PII design | **PASS** |
| GDPR Guest scope | **PASS** (N/A — accurate) |
| `check-ac-matrix.ps1` | **Low risk PASS** |
| Invented Stage 03 runtime security PASS | **No** |
| **Merge OK** | **yes** |

No security blockers for merge to `develop`. Stage 02 design adequately specifies Auth gates, IDOR scoping, consent IP minimization, and GDPR boundaries. Follow-ups above are Stage 03 implementation / re-audit notes only.

Merge OK: yes
