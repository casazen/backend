# Stage 01: Planning — Quality Harness

## Entry Criteria

- Raw idea, regulatory change, or stakeholder request exists (verbal, email, or Slack)
- No open GitHub Issue for this topic yet (check `gh issue list --state open`)

## Council Run

Coordinator spawns: `product-strategist`, `tech-architect`, `regulatory-analyst`

Topic handed to council:
> "Create a fully-specified GitHub Issue for: [input description]. Include user story, acceptance criteria, technical impact, and compliance labels."

## Quality Gates

All gates must pass before exiting.

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | GitHub Issue exists | `gh issue view <N>` | Issue is open with title and body |
| G2 | Acceptance criteria present | Read issue body | At least 2 testable ACs in `## Acceptance Criteria` section |
| G2b | Spec file when registry used | If `Sessions/specs/spec-*.md` created/updated | Follow `Sessions/specs/_TEMPLATE.md` (Verifiable Outcomes + UX/Export sections when applicable); validate with `.\scripts\quality\check-ac-depth.ps1 -SpecPath …` |
| G3 | Technical scope specified | Read issue body | `## Technical Notes` section present with migration/OTA/background job impact noted |
| G4 | Regulatory label applied | `gh issue view <N> --json labels` | `compliance` label if CIN/GDPR/Alloggiati/tourist-tax affected; `none-required` label otherwise |
| G5 | Priority label applied | `gh issue view <N> --json labels` | One of: `priority:critical`, `priority:high`, `priority:medium`, `priority:low` |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G5 fails) AND (iteration < max_iterations):
  1. Coordinator identifies failed gates
  2. Spawns specialists with failure list as context
  3. Specialists produce fixes (update issue, add missing sections)
  4. Re-check all failed gates
  5. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE: create escalation note in issue body
  Human decision required before proceeding
```

## Exit Artifact

GitHub Issue `#N` with:
- Title: `feat: <feature-name>` or `fix: <fix-name>` or `compliance: <regulation>-<change>`
- `## User Story` section
- `## Acceptance Criteria` section (≥ 2 items)
- `## Technical Notes` section
- Labels: type + area + priority + compliance (if applicable)

## Handoff to Stage 02

Pass to design with:
- Issue number: `#N`
- Spec file target: `Sessions/design-<issue-N>.md`
