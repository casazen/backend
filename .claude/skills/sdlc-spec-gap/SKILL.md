---
name: sdlc-spec-gap
description: >-
  Refresh Sessions/quality/requirements.json and gap-backlog.md from ADRs,
  active specs, and ac-matrix-mvp.md. Use when starting a loop tick or after
  matrix write-back.
---

# sdlc-spec-gap

## Steps

1. Prefer executable extract: `.\scripts\quality\extract-requirements.ps1`
2. Prefer coverage check: `.\scripts\quality\check-spec-coverage.ps1` (non-zero = open gaps)
3. If scripts need manual supplement, parse:
   - `docs/adr/ADR-*.md` → `## Requirements` table rows → `ADR-00N-Rk`
   - `Sessions/specs/spec-*.md` ACs (skip `deferred`/`frozen`) → `SPEC:<slug>:AC*`
   - `Sessions/quality/ac-matrix-mvp.md` P0 rows with status `fail`|`missing-test`|`in-progress`
4. Write/update:
   - `Sessions/quality/requirements.json`
   - `Sessions/quality/gap-backlog.md` (priority-ordered; P0 fail first; respect golden-journey deps in `Sessions/specs/README.md`)
5. Update `Sessions/loop/state.md` field `open_p0_gaps` to the open P0 count.

## Output

Report: `open_p0_gaps=<n>`, top 3 gap IDs. Do not implement fixes in this skill.
