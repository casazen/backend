## Summary

Spike + ADR for Holidu-style domain resolution: `*.casazen.it`, path mode, custom CNAME. Informs Fase 1 `spec-custom-domain-booking`.

**Spec:** `Sessions/specs/spec-custom-domain-booking.md` (US-024)

## Acceptance criteria

- [ ] ADR: Vercel middleware vs Cloudflare, SSL, `resolve-host` API contract
- [ ] PoC on staging: one test subdomain resolves to org branding
- [ ] Security notes: host-header allowlist
