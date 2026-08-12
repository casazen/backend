---
name: sdlc-contract-check
description: >-
  Verify design API contract and ADR Requirements against backend controllers
  and frontend API clients. Fail on undeclared drift. Use on Stage 03/04
  boundaries and before marking API/UI gaps closed.
---

# sdlc-contract-check

## Steps

1. Inputs: design path `Sessions/design-<N>.md`, optional ADR paths, pipeline slug.
2. Extract endpoint table from design `## API Contract` (method, path, auth).
3. Write/update `Sessions/pipeline-<slug>/contract-snapshot.md` with that table.
4. For each endpoint:
   - Backend: controller/route exists (grep `Casazen.Web` / Minimal APIs).
   - Auth: `[Authorize]` or documented `[AllowAnonymous]` justification.
   - Frontend: matching client method under `../frontend` (when FE in scope) — typecheck path or api module reference.
5. For linked ADR `## Requirements` P0 rows: evidence of implementation or explicit matrix `stub`/`pass`.
6. Emit `Sessions/pipeline-<slug>/contract-check.md` with PASS/FAIL per row.
7. Overall FAIL if any P0 row fails without an ADR-approved deviation issue.

## Forbidden

- Passing on “will implement later” without stub label + issue
- Ignoring FE when `git diff` touches frontend
