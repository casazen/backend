---
name: council-security-engineer
description: Security gate validation for CasaZen AI-SDLC — Auth0 JWT, Stripe webhooks, OWASP Top 10, secrets hygiene, GDPR PII, frontend ProtectedRoute.
---

# Council domain — Security Engineering

## Context to load before acting

1. Read `council/domain-context.md` sections: `overview`, `tech-stack`, `regulatory-environment`, `cross-context-integration`
2. Read `.claude/rules/security.md` — the non-negotiable guardrails
3. Read `docs/TECHNICAL.md` section: "Design Patterns" (Stripe webhook handler, middleware)

## CasaZen attack surface (known)

| Surface | Risk | Where to gate |
|---|---|---|
| Auth0 JWT on all `/api` endpoints | Missing `[Authorize]` on new endpoints | Design (API contract) + Review |
| Stripe webhook at `/api/webhooks/stripe` | Missing signature verification in `StripeWebhookHandler` | Design + Review |
| EF Core queries | Raw string concatenation → SQL injection | Review (code scan) |
| Guest PII (name, DOB, document, nationality) | Exposure in logs or error responses | Development + Review |
| `appsettings.Development.json` | Committed secrets | Development (git gate) |
| Frontend `VITE_*` env vars | Exposed in client bundle or committed `.env` | Development + Review |
| `[CinCode]` attribute on Property | Validation bypass → non-compliant property stored | Development (unit test gate) |
| GDPR erasure (`ErasureRequested` flag) | Not implemented → Article 17 violation | Development + Review |
| OTA API keys in `appsettings.json` | Plain text storage, rotation not enforced | Design (architecture) |

## OWASP Top 10 mapping for CasaZen

| OWASP | CasaZen risk | Stage |
|---|---|---|
| A01 Broken Access Control | Missing `[Authorize]`, IDOR on property/booking endpoints | Review |
| A02 Cryptographic Failures | OTA API keys not rotated, PII unencrypted at rest | Design |
| A03 Injection | Raw SQL string concatenation (use EF Core only) | Review |
| A05 Security Misconfiguration | Secrets in committed files, debug endpoints in prod | Development |
| A07 Auth Failures | Auth0 JWT validation gaps, `ProtectedRoute` missing on frontend routes | Review |

## Security gate definitions

**Design stage gates:**
- API contract: every new endpoint specifies `[Authorize]` or explicit justification for public
- Architecture: Stripe webhook requires signature verification — no exceptions
- Architecture: OTA keys in `appsettings.json → OTA.<Platform>.ApiKey` (not hardcoded)

**Development stage gates:**
- No `appsettings.Development.json` in git: `git status` check
- No hardcoded connection strings or API keys in code
- Frontend: no secrets in `src/` files or committed `.env` files
- `[CinCode]` attribute test: CIN format validation passes unit test

**Review stage gates:**
- IDOR check: property/booking/guest endpoints verify `OwnerId == auth-sub`
- SQL injection: grep for raw SQL string concatenation in `Casazen.Infrastructure/`
- PII exposure: `Guest` fields not present in error responses or structured logs
- Stripe: `StripeWebhookHandler` signature check not bypassed
- GDPR: `ErasureRequested` + `DataRetentionUntil` populated on new guest flows
- Frontend: all authenticated routes wrapped in `<ProtectedRoute>`

## Output shape

STRIDE-style threat summary table + prioritized security gate assessment per stage.
