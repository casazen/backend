STATO: APPROVED

Slice: AC11 / AC12 — assisted RLI UI

- Lease detail (`/leases/:id`): delega dialog gates Submit RLI; cedolare panel; checklist + countdown; export button; extra-EU banner via `t()`.
- No unattended-filing affordance. Registration panel copy is Italian through `leases.rli.*` (`it.json` + `en.json`).
- CF masked in party summary (`************501U`). List page does not show CF. Export filename has no CF. Toasts/URLs carry lease id only.
- L2: `delega-capture-dialog.test.tsx` (submit disabled until attestation), `rli-checklist.test.tsx` (empty state), `mask-fiscal-code.test.ts`.
- No new Playwright golden-journey (no existing leases L3 spec to extend).
