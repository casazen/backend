# Workflow: Step 1 — Requirement Refine (Raw Issue → Backlog Item)

**Orchestrator**: `@scrum-master-casazen`
**Clarifier**: `@requirement-clarifier`
**Council**: `@product-owner`, `@architect`, `@regulatory-agent`, `@analyzer-agent`
**Item Creator**: `@scrum-master-casazen`

Invoked via: `/step1-refine <issue_number>`
Auto-triggered by: GitHub Actions on label `raw-requirement`, `awaiting-clarification` (comment), `council-ready`

---

## Label State Machine

```
raw-requirement
  ↓ [Phase A — @requirement-clarifier]
  │  ambiguous?
  ├─ YES → awaiting-clarification  (pipeline paused, waiting for PO)
  │          ↓ [on issue_comment by non-bot]
  │        [Phase A re-entry — incorporate answers]
  └─ NO  → council-ready
             ↓ [Phase B — Council, CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS]
           pending-po-approval  (on new backlog item)
             ↓ [human PO adds label]
           approved  → triggers Step 2
```

---

## Phase A — Clarification Loop (`@requirement-clarifier`)

**Entry condition**: label `raw-requirement`
**Re-entry condition**: label `awaiting-clarification` + new human comment on issue

```bash
# Entry: check current round
gh issue view $ISSUE_NUMBER --json comments \
  --jq '[.comments[].body | select(contains("clarification-round"))]'
```

### Round detection

| Condition | Action |
|---|---|
| No `<!-- clarification-round: N -->` found | Round 0: assess clarity |
| N < 2, label `awaiting-clarification` | Re-entry: incorporate PO answers, reassess |
| N ≥ 2 | Force proceed to `council-ready` |

### Clarification comment format

```markdown
## Requirement Clarification — Step 1

Before this requirement can be reviewed by the council, I need answers to the following:

1. [Question — most blocking ambiguity]
2. [Question — second most blocking ambiguity]
3. [Question — third most blocking ambiguity]

Please reply to this comment. Once answered, the pipeline will resume automatically.

<!-- clarification-round: N -->
<!-- issue-id: ISSUE_NUMBER -->
```

Rules:
- Max 3 business questions per round (never ask about tech choices or regulatory interpretation)
- Max 2 rounds total
- If requirement is clear on first read: skip directly to `council-ready` without posting

### Re-entry: update issue body

When PO responds, append a `## Refined Requirements` section to the issue body incorporating the answers as a clean, council-ready spec.

### Label transitions

```bash
# Set awaiting-clarification (when questions posted)
gh issue edit $ISSUE_NUMBER --add-label "awaiting-clarification"

# Set council-ready (when clear)
gh issue edit $ISSUE_NUMBER \
  --remove-label "raw-requirement" \
  --remove-label "awaiting-clarification" \
  --add-label "council-ready"
```

---

## Phase B — Council (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`)

**Trigger**: label `council-ready`

All four agents run **in parallel**. Each receives the full issue body (including `## Refined Requirements` if present).

### `@product-owner` output

```bash
# Duplicate check
gh issue list --repo casazen/backend --search "<keywords from title>" --state open
```

Produce:
- **Classification**: Epic / Feature / Story / Bug
- **Duplicate status**: none found / see #N
- **Strategic fit**: on roadmap? links to which Epic?

### `@architect` output

Produce:
- **Scope**: BE only / FE only / Fullstack
- **Layers touched**: entities, migrations, repos, services, controllers, OTA adapters
- **API changes**: new endpoints, contract changes (method + path + DTOs)
- **DB impact**: new tables, column additions, index changes
- **External integrations**: Auth0, Stripe, SendGrid, OTA platforms

### `@regulatory-agent` output

```bash
ls .claude/context/regulations/
```

Scan each regulation file for matching obligations. Produce:
- **Regulation match**: CIN / Alloggiati Web / GDPR / Tourist Tax / Cedolare Secca / OTA Normativa / none
- **Severity**: CRITICAL / HIGH / MEDIUM / NONE
- **Deadline**: if applicable
- **Open Questions**: list any questions that require human decision before
  implementation can safely proceed (e.g., interpretation of a regulation,
  commercialista confirmation, legal review of OTA clauses). Use this format:

  ```
  ## Open Questions (from @regulatory-agent)
  - [ ] [Question] — requires: [PO / commercialista / legal / external expert]
  ```

  If none: omit this section entirely. Only include questions that are
  genuinely blocking — not hypothetical or low-risk items.

### `@analyzer-agent` output

```bash
cat .claude/context/codebase_map.md
```

For each area touched by the requirement, classify:
- **MISSING** — feature not implemented
- **PARTIAL** — partially implemented (list what's missing)
- **OUTDATED** — implemented but needs update for new requirement
- **COMPLIANT** — fully implemented, no change needed

---

## Phase C — Backlog Item Creation (`@scrum-master-casazen`)

Synthesize council outputs into one backlog issue.

**Before creating the issue**: check whether any council agent (especially
`@regulatory-agent`) produced an `## Open Questions` section in its output.
If yes, the backlog item must include a `## ⚠️ Open Questions` section and
receive the label `open-questions` in addition to `pending-po-approval`.

```bash
gh issue create \
  --repo casazen/backend \
  --title "[STORY|FEATURE|BUG|EPIC] <concise title>" \
  --label "pending-po-approval" \
  --body "$(cat <<'EOF'
## Summary
[2-3 sentences synthesized from council outputs]

## Type
[Epic / Feature / Story / Bug — from @product-owner]

## Scope
[BE / FE / Fullstack — from @architect]

## Regulatory Impact
**Level**: CRITICAL / HIGH / MEDIUM / NONE
**Regulation**: [reference if applicable]
**Deadline**: [if applicable]

## Codebase State
[MISSING / PARTIAL / OUTDATED / COMPLIANT — from @analyzer-agent]
**Affected areas**: [list from codebase_map.md]

## API Changes Required
[From @architect — new endpoints, contract changes, or "none"]

## DB Changes Required
[From @architect — migrations needed, or "none"]

## User Story
As a [role], I want [action], so that [benefit].

## Acceptance Criteria
- [ ] GIVEN [precondition], WHEN [action], THEN [expected result]
- [ ] [additional criteria]

## Dependencies
[Cross-repo? casazen/frontend? External service? or "none"]

## ⚠️ Open Questions
> Include this section ONLY if @regulatory-agent or another council agent
> flagged unresolved questions. When all questions are resolved, the PO
> replaces this section with `## ✅ Legal Decisions` documenting each answer.
> Step 2 will NOT dispatch while this section is present.

- [ ] [Question 1] — requires: [PO / commercialista / legal]
- [ ] [Question 2] — requires: [...]

## References
- Original issue: #ORIGINAL_ISSUE_NUMBER
- Duplicate check: [none found / see #N]
EOF
)"
```

If the backlog item includes `## ⚠️ Open Questions`, add the `open-questions` label:

```bash
gh issue edit $NEW_ISSUE_NUMBER --repo casazen/backend \
  --add-label "open-questions"
```

Step 2 checks for this label as a secondary gate (in addition to scanning the
body for `⚠️ Open Questions`). The PO does **not** need to edit the issue body
manually — reply to the blocking comment with decisions, and `@question-resolver`
will update the body, remove `open-questions`, and restore `approved` automatically.

If `@architect` identified FE scope, create a linked issue on frontend:

```bash
gh issue create \
  --repo casazen/frontend \
  --title "[FE] <title>" \
  --label "pending-po-approval" \
  --body "Backend counterpart: casazen/backend#NEW_ISSUE_NUMBER\n\n[FE-specific scope from @architect]"
```

Post comment on original issue:

```bash
gh issue comment $ORIGINAL_ISSUE_NUMBER \
  --body "Backlog item created: casazen/backend#$NEW_ISSUE_NUMBER — awaiting PO approval (label \`approved\` to trigger Step 2)."
```

---

## Notes

- `@product-owner` and `@architect` are virtual roles — invoke as part of the council team, not as separate named agents in `.claude/agents/`
- CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS must be enabled for Phase B to run the four agents in parallel
- Phase A uses `@requirement-clarifier` (haiku model, cheap and fast)
- Phase B uses sonnet for all council agents
- Human in the loop: PO must manually add `approved` label to the backlog item to trigger Step 2
- See `.claude/rules/github-flow-mandatory.md` — no code is written in this workflow, only GitHub issues
