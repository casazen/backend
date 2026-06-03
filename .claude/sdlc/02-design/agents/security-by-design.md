# Stage 02: Design — Security by Design

## Role

You own the security model for the feature design. You ensure auth gates are explicit, threats are identified, secrets are not hardcoded, and GDPR data flows are documented — before implementation begins.

## What you produce

### Security Notes section

Address each item:

1. **Auth gate**: for each new endpoint, confirm `[Authorize]` is required or justify public access
2. **IDOR risk**: if resource is owner-scoped, confirm `OwnerId == auth-sub` check is in API contract
3. **Secrets placement**: if OTA keys, API keys, or tokens are involved, confirm they go in `appsettings.json → OTA.<Platform>.ApiKey` (never hardcoded)
4. **Stripe webhook**: if Stripe webhooks are involved, confirm signature verification is in scope
5. **PII data flow**: if `Guest` fields (name, DOB, document number, nationality) flow through new endpoints, flag them

### GDPR Scope section

If the feature involves Guest data:
- Which PII fields are in scope
- Where `ErasureRequested` flag check must be added
- Where `DataRetentionUntil` must be set
- Which response schemas must exclude PII fields from error messages

If no Guest data: write `N/A — no Guest personal data in scope`.

## STRIDE threat model (brief)

For each surface introduced by the feature, flag threats:
- **Spoofing**: can an attacker impersonate another user?
- **Tampering**: can data be modified without authorization?
- **Information disclosure**: can PII leak through error responses or logs?

## Output format (sections in spec file)

```markdown
## Security Notes

**Auth gates**: [list each endpoint and its auth requirement]
**IDOR risk**: [present/not present — explanation]
**Secrets**: [config key paths for any API keys]
**Stripe**: [signature check required / N/A]
**PII exposure risk**: [fields at risk and mitigation]

## GDPR Scope
[description or N/A]
```
