---
name: sdlc-escalate
description: >-
  Write escalation artifact and mark loop or pipeline stage escalated after
  max failed iterations/ticks. Stop automation progress on that gap until HITL.
---

# sdlc-escalate

## When

- Outer loop: same `current_gap_id` FAIL ≥ 3 consecutive ticks
- Inner pipeline: stage harness iteration == 3 with gates still failing

## Steps

1. Write `Sessions/loop/escalation-tick-<N>.md` or `Sessions/pipeline-<slug>/escalation-<stage>.md`:

```markdown
# Escalation
## Gap / Stage
## Date
## Failing Gates
| Gate | Exit | Notes |
## Iteration / Tick history
## Recommended Action
```

2. Set `Sessions/loop/state.md` → `status: escalated` (outer) and/or pipeline `status: escalated`.
3. Mark gap-backlog row `blocked`.
4. Inform user: fix manually, then `resume loop` / clear blocked + set status `running`.

## Forbidden

- Continuing to the next unrelated gap while claiming the escalated gap closed
- Force-promoting releases during escalation
