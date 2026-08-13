---
name: sdlc-work-queue
description: >-
  Build Sessions/loop/work-queue.md (+ optional work-queue.json) from sticky
  pipelines, P0 gaps, planned/in-dev specs, and open GitHub issues.
---

# sdlc-work-queue

## Steps

1. Ensure `Sessions/loop/` exists.
2. Collect candidates (later filtered by `goal.md` in delivery-tick):

### A — Sticky pipelines (highest)

Scan `Sessions/pipeline-*/state.md` for `status: running`.  
Emit `work_id: sticky:<slug>`, `kind: feature_stage`, note `current_stage`.

If multiple running, prefer the one named in `delivery-state.md` → `sticky_pipeline`; else most recently `last_updated`.

### B — P0 gaps

Read `Sessions/quality/gap-backlog.md` rows with Status `open` (skip `blocked`).  
Emit `kind: gap`, preserve backlog priority order.

If backlog missing/stale, optionally run `sdlc-spec-gap` first when delivery-tick requests refresh.

### C — Features planned / in-dev

From `Sessions/specs/README.md` registry rows with Status `planned` or `in-dev` (skip `frozen`, `shipped`, `deferred`, `blocked`, `idea` unless goal include forces).  
Respect build-order notes in that README / `Sessions/PLANNING.md`.

Enrich with open issues: `gh issue list --state open --limit 50` (JSON). Match `#N` from registry Issue column.

Emit `work_id: SPEC:<slug>` or `#<n>`, `kind: feature`.

3. Deduplicate: sticky slug wins over same SPEC; gap MATRIX rows stay separate from SPEC feature rows.
4. Assign sequential Priority 1..n.
5. Write `Sessions/loop/work-queue.md` and `Sessions/loop/work-queue.json` per STATE-FORMAT.
6. Return top work_id + kind (do not execute work).

## Goal filtering (caller)

`sdlc-delivery-tick` applies `Sessions/loop/goal.md` `include` / `exclude` after this skill. Queue file stores the unfiltered unified list unless delivery-tick asks for filtered write — default: write full queue; pick applies filter.

## Script

Prefer regenerating via:

```powershell
.\scripts\quality\build-work-queue.ps1
.\scripts\quality\build-work-queue.ps1 -DryRunPick
.\scripts\quality\build-work-queue.ps1 -ApplyGoal
.\scripts\quality\build-work-queue.ps1 -ApplyGoal -GapsOnly
.\scripts\quality\build-work-queue.ps1 -SkipGh   # offline
```

## Dry-run

Allowed: print queue + top pick with no code changes. Use for Fase seed validation.

## Forbidden

- Declaring gaps closed
- Reordering to bypass P0 freeze for promote (queue may list features after gaps; promote still blocked)
- Inventing issue numbers without `gh` / registry evidence
