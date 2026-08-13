# Prompt template — `Sessions/loop/next-prompt.md`

`sdlc-prompt-gen` overwrites this file each tick. Agents must execute it verbatim as the work order.

```markdown
# Tick <N> — <gap-id>

## Objective
Close <REQ-ID>: <one-line text from requirements.json / matrix>

## Context files
- @.claude/process/sdlc-reliability-loop/PROCESS.md
- @Sessions/quality/gap-backlog.md
- @Sessions/quality/requirements.json
- @Sessions/quality/ac-matrix-mvp.md
- <linked spec path if any>
- <linked ADR path if any>
- <design / pipeline state if any>

## Allowed actions
- Use skills: sdlc-init, sdlc-stage-run, sdlc-contract-check, sdlc-gate-runner, sdlc-matrix-writeback
- Implement fixes on feature branch `feature/<issue>-<slug>` (or create via sdlc-init)
- Open/update PRs to `develop` with `Refs #N` only

## Gate list (must pass via sdlc-gate-runner)
- <gate-id>: <exact command>
- ...

## Done when
1. `Sessions/loop/evidence/<tick>/gates.json` has `"overall": "pass"`
2. Matrix row for this gap is `pass` (or `stub` with status:stub justification)
3. `sdlc-matrix-writeback` has updated ac-matrix-mvp.md

## Forbidden
- Declaring PASS without evidence/
- Closing GitHub issue before Stage 05 Phase B
- Promoting develop → main unless this prompt lists Phase B/C gates and they PASS
- Marking N/A on a layer with non-empty git diff
- Skipping L3 for UI ACs
```
