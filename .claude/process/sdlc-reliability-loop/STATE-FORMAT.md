# State file formats

## Outer loop — `Sessions/loop/state.md`

```markdown
# SDLC Reliability Loop

## Status
- status: running | completed | escalated
- tick: 0
- started: <ISO-8601>
- last_updated: <ISO-8601>
- open_p0_gaps: <int>
- current_gap_id: (none) | <gap-id>
- consecutive_fails_on_current_gap: 0
- last_result: (none) | pass | fail
- last_prompt: Sessions/loop/next-prompt.md
- last_evidence: (none) | Sessions/loop/evidence/<tick>/

## Notes
- <optional human/HITL notes>
```

## Evidence — `Sessions/loop/evidence/<tick>/gates.json`

```json
{
  "tick": 1,
  "gap_id": "MATRIX:native-host:AC4",
  "started": "<ISO-8601>",
  "finished": "<ISO-8601>",
  "overall": "pass | fail",
  "gates": [
    {
      "id": "G9b",
      "command": ".\\scripts\\quality\\run-l3-local.ps1 -SpecFilter ...",
      "exit_code": 0,
      "log": "gate-G9b.log"
    }
  ]
}
```

Companion files: `gate-<id>.log` stdout/stderr per gate.

## Gap backlog — `Sessions/quality/gap-backlog.md`

Ordered table; row 1 is next work.

```markdown
# Gap backlog
**Updated:** <ISO-8601>
**Open P0:** <n>

| Priority | Gap ID | REQ-ID | Source | Status | Fail ticks | Suggested action |
|---|---|---|---|---|---|---|
| 1 | ... | ... | matrix\|adr\|spec | open\|blocked | 0 | ... |
```

## Requirements — `Sessions/quality/requirements.json`

Array of objects:

```json
{
  "id": "ADR-001-R1",
  "source": "docs/adr/ADR-001-custom-domain-booking.md",
  "priority": "P0",
  "text": "...",
  "active": true,
  "matrix_status": "pass | fail | missing-test | in-progress | stub | unknown"
}
```

## Inner pipeline — `Sessions/pipeline-<slug>/state.md`

Unchanged from legacy format (see historical `sdlc-pipeline` docs). Stage History **Gates** column must cite evidence path, never emoji-only narrative.
