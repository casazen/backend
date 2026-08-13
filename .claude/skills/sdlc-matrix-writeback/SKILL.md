---
name: sdlc-matrix-writeback
description: >-
  Update Sessions/quality/ac-matrix-mvp.md and related requirement matrix_status
  from gate evidence. Use after every sdlc-gate-runner run.
---

# sdlc-matrix-writeback

## Steps

1. Read `Sessions/loop/evidence/<tick>/gates.json` (or provided evidence path).
2. Identify gap / REQ-ID from `Sessions/loop/state.md` `current_gap_id` and `gap-backlog.md`.
3. If `overall=pass`:
   - Set matching matrix row Status to `pass` (or `stub` only if prompt allowed stub + status:stub).
   - Update `requirements.json` `matrix_status`.
   - Mark gap-backlog row closed / remove from open list; recompute priorities.
4. If `overall=fail`:
   - Leave or set matrix status to `fail` / `missing-test` as appropriate.
   - Increment `consecutive_fails_on_current_gap` in loop state.
5. Bump matrix `**Updated:**` date.
6. Do not claim production shipped; do not clear freeze unless all P0 fail rows are gone.

## Forbidden

- Writing `pass` without evidence overall=pass
- Deleting stub inventory rows without `status:stub` process
