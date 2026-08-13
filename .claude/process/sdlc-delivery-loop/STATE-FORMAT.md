# Delivery loop — state file formats

Runtime under `Sessions/loop/` (gitignored). Keep committing process/skills only.

## `Sessions/loop/delivery-state.md`

```markdown
# SDLC Delivery Loop

## Status
- status: running | completed | escalated
- tick: 0
- started: <ISO-8601>
- last_updated: <ISO-8601>
- work_units_done: 0
- max_work_units: 20
- current_work_id: (none) | <gap-id|SPEC:slug|#N>
- current_kind: (none) | gap | feature_stage
- sticky_pipeline: (none) | <slug>
- consecutive_fails_on_current: 0
- last_result: (none) | pass | fail | blocked
- last_prompt: Sessions/loop/next-prompt.md
- last_evidence: (none) | Sessions/loop/evidence/delivery-<tick>/
- pr_urls: (none) | <url>[, <url>...]
- merge_wait: (none) | checks_pending
- last_notify_event: (none) | pr_merged | review_failed | merge_wait | escalated | goal_done | ...
- goal_path: Sessions/loop/goal.md | (none)

## Notes
- <blockers / secrets missing / merge wait detail>
```

**Obsolete fields (rewrite if present):**
- `status: awaiting_human_pr_review` → set `running` (and `merge_wait` if PR still open with pending checks)
- `skip_pr_wait` → ignore / delete

## `Sessions/loop/goal.md` (optional)

```markdown
# Delivery goal
- mode: limited | until_empty
- include: []
- exclude: []
- max_work_units: 20
- stop_on: continue_next_item | escalate_only
```

Empty `include` with `mode: until_empty` = full unified queue.  
`limited` with non-empty `include` = only matching work_ids / SPEC slugs / issue numbers.  
Do **not** use `stop_on: awaiting_human_pr_review` (obsolete).

## `Sessions/loop/work-queue.md`

```markdown
# Delivery work queue
**Updated:** <ISO-8601>
**Source:** sdlc-work-queue
**Open items:** <n>

| Priority | Work ID | Kind | Source | Status | Notes |
|---|---|---|---|---|---|
| 1 | sticky:native-host-app | feature_stage | pipeline | running | Stage 03 |
| 2 | MATRIX:... | gap | gap-backlog | open | P0 |
| 3 | SPEC:slug | feature | specs+gh | planned | #299 |
```

## `Sessions/loop/work-queue.json` (optional machine-readable)

```json
{
  "updated": "<ISO-8601>",
  "items": [
    {
      "priority": 1,
      "work_id": "MATRIX:native-host:AC15",
      "kind": "gap",
      "source": "gap-backlog",
      "status": "open",
      "req_id": "SPEC:native-host-app:AC15",
      "notes": ""
    }
  ]
}
```

## `Sessions/loop/journal.md` (append-only)

One line per run:

```markdown
| tick | ISO-8601 | work_id | kind | result | pr_url | evidence |
|---|---|---|---|---|---|---|
| 1 | 2026-08-12T20:00:00Z | MATRIX:... | gap | pass | https://... | Sessions/loop/evidence/delivery-1/ |
```

## `Sessions/loop/metrics.md`

```markdown
# Delivery metrics
- ticks_total: 0
- work_units_done: 0
- gaps_closed: 0
- stages_advanced: 0
- prs_opened: 0
- prs_merged: 0
- escalations: 0
- last_tick: (none)
```

## Evidence — `Sessions/loop/evidence/delivery-<tick>/gates.json`

Same shape as reliability evidence (`STATE-FORMAT` in reliability-loop), with `"tick": "delivery-<n>"` or numeric `tick` plus `"loop": "delivery"`.

## `merge_wait: checks_pending`

Set when review gates PASS but GitHub required checks are not yet green. Next Automation tick re-checks and merges when green (or fails/escalates if checks fail).

## Goal handoff (loop complete only)

When status becomes `completed` for `goal_done` / `queue_empty` / `max_work_units`, `sdlc-goal-handoff` writes:

| File | Purpose |
|---|---|
| `Sessions/loop/goal-handoff.md` | Always — capability list + journal |
| `Sessions/loop/goal-handoff.html` | `kind=demo` only — graphical walkthrough (Mermaid + screens + API) |
| `Sessions/loop/last-handoff.json` | Machine payload (kind, what_you_can_do, routes, endpoints) |

`kind=demo` if the journal includes a feature and design/spec has FE routes or BE endpoints; otherwise `kind=report`. Webhook extra fields: `artifact_kind`, `artifact_path`, `html_path`.
