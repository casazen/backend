---
description: Step 1 — clarify a raw requirement and run council review to produce a refined backlog item. Three modes: default (run clarifier), mode=read-answers (process PO replies), mode=council (launch parallel council + item creation). Requires label `raw-requirement` or `council-ready`.
disable-model-invocation: true
allowed-tools: Bash Read Grep Glob
---

Run the Step 1 requirement-refine workflow.

Full instructions: @.claude/workflows/step1-requirement-refine.md

Execute the following steps:

1. Parse arguments:
   ```bash
   ISSUE_NUMBER=$1
   MODE=${2:-default}
   ```

2. Verify issue exists:
   ```bash
   gh issue view $ISSUE_NUMBER --json number,title,labels
   ```

3. Route by MODE:

   **MODE=default** (entry — label `raw-requirement` expected):
   - Verify label `raw-requirement` is present OR `awaiting-clarification`
   - Run Phase A: `@requirement-clarifier` reads issue, detects ambiguities
   - If ambiguous: agent posts ≤3 business questions, sets label `awaiting-clarification`, stops
   - If clear: agent sets label `council-ready`, continue to MODE=council

   **MODE=read-answers** (re-entry after PO comment):
   - Verify label `awaiting-clarification` is present
   - Run Phase A re-entry: `@requirement-clarifier` reads latest human comment, appends `## Refined Requirements` to issue body
   - If still ambiguous AND round < 2: post follow-up questions, stop
   - If clear OR round ≥ 2: set label `council-ready`, continue to MODE=council

   **MODE=council** (Phase B + C — label `council-ready` expected):
   - Verify label `council-ready` is present:
     ```bash
     gh issue view $ISSUE_NUMBER --json labels --jq '.labels[].name' | grep -q "^council-ready$"
     ```
   - Phase B — Launch parallel council (CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS):
     - `@product-owner`: classify Epic/Feature/Story/Bug, check duplicates
     - `@architect`: BE/FE scope, API contract gaps, DB impact, external integrations
     - `@regulatory-agent`: scan `.claude/context/regulations/` for matching obligations
     - `@analyzer-agent`: read `codebase_map.md`, classify MISSING/PARTIAL/OUTDATED/COMPLIANT
   - Phase C — `@scrum-master-casazen` synthesizes council outputs:
     - Creates 1 backlog issue on `casazen/backend` with label `pending-po-approval`
     - If FE scope identified: creates linked issue on `casazen/frontend`
     - Posts comment on original issue with link to new backlog item
