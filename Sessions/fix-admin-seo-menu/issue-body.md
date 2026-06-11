## Summary

Admin users cannot see **SEO Compliance** in the admin sidebar because `admin.seo.read` is missing from `ContextAccessBootstrap` JWT fallback permissions.

## Acceptance criteria

- [ ] `GET /api/me/contexts` returns `admin.seo.read` for Admin JWT fallback
- [ ] SEO nav item visible in admin sidebar after re-login
- [ ] Unit test covers admin fallback permissions

Related: #263
