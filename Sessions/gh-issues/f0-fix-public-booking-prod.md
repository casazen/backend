## Summary

Pre-Fase 0 blocking fix. Public direct booking surface (`/book/{slug}`) is not correctly deployed or redirects in production — Golden Journey step 4 cannot complete on prod/staging.

**Planning:** `Sessions/PLANNING.md` § Debito noto  
**Blocks:** GJ step 4, Fase 0 outcome (GJ executable through step 4 on staging)

## Acceptance criteria

- [ ] `GET /book/{slug}` on staging + prod serves React SPA (not env placeholder)
- [ ] Anonymous guest can reach checkout flow for an Active property
- [ ] Vercel deploy smoke passes (`E2E_DEPLOY_SMOKE=1`)
- [ ] Document correct public URL in `docs/INFRA.md` if config changed

## Spec / deps

- Builds on shipped US-002 / US-003 (#226, #215)
- Superseded UI tracked in `spec-public-site-design-system` (Fase 1) — this issue is **deploy/routing only**
