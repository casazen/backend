## Summary

Post-release follow-ups for #258 programmatic compliance SEO: populate prod sitemap, expand comune registry, bootstrap generation on startup, and admin bulk actions.

## Acceptance criteria

- [ ] `ItalianComuneRegistry` includes 12 priority comuni (Lombardia + major cities)
- [ ] `Seo:BootstrapOnStartup=true` enqueues generation when DB has zero SEO pages
- [ ] `POST /api/admin/seo/generate` with empty `comuneCodes` targets all registry comuni
- [ ] `POST /api/admin/seo/approve-all-drafts` bulk-publishes Draft pages with counsel gate
- [ ] `GET /api/admin/seo/comuni` returns registry for admin dashboard
- [ ] Admin dashboard: "Genera tutti" + "Approva tutte" buttons
- [ ] E2E AC13 covers bulk generate and approve
- [ ] After deploy, `/sitemap-compliance.xml` lists Reviewed pages

## Parent

#258
