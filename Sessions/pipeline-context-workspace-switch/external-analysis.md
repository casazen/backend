# External analysis: context / workspace switcher (split layer)

Source: `Sessions/specs/spec-split-layer.md` (user-provided, 2026-06-04)

## Summary (English — pipeline working language)

CasaZen should treat **short-rent**, **long-rent**, and **admin** as distinct **application contexts / workspaces**, not as flat pages gated by global roles.

### Recommended model

| Concept | Purpose |
|---|---|
| User | Global identity |
| Context / Workspace | `short-rent`, `long-rent`, `admin` |
| Membership | User ↔ context |
| Role | Per-context role (not necessarily global) |
| Permission | Fine-grained capabilities (`booking.read`, etc.) |

### Frontend

- Canonical URLs: `/app/short-rent/*`, `/app/long-rent/*`, `/app/admin/*`
- Central **route manifest** (`path`, `context`, `requiredPermissions`, `navLabel`, `isDefault`)
- `activeContext` in app state
- **Workspace switcher** always visible in shell (not exception UX)
- Post-login: fetch contexts → single context auto-redirect → multi: picker or last-used → derive menu/routes from active context
- Invalid context/route → redirect to valid home or managed 403

### Backend

- Avoid global roles that encode operational areas (`Admin`, `AffittiBrevi`, …)
- Authoritative access in backend; frontend reflects decisions
- Minimum data model: `User`, `UserContextMembership`, `Role`, `RolePermission` (or Workspace/Membership/PermissionGrant variant)

### Anti-patterns to avoid

- Flat routes without context namespace
- Fixed post-login redirect ignoring available contexts
- Menu from global roles vs active context
- Frontend-only permission checks
- Switcher as rare exception

### Relation to prior work

Pipeline `long-term-ui-layer` (#182) introduced long-term UI separation; this feature **generalizes** that into a unified context architecture across all three areas.

---

Full Italian source document: see `Sessions/specs/spec-split-layer.md`.
