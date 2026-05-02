# Skill: Diff Context - Compare Previous and Current Context

## Description
This skill describes how to compare the previous and current state of regulatory context files to identify changes, updates, and new elements.

## When to Use It
- Before updating a context file, to understand what has changed
- After a regulatory update, to produce a changelog
- To verify whether an update introduced new requirements

## Procedure

### Step 1: Previous Snapshot
Read the `_last_updated.json` file to obtain:
- Last update date
- Previous index hash

If the hash is not available (first execution), consider everything as "new".

### Step 2: Current State Reading
Read all files in `.claude/context/regulations/` and `.claude/context/_index.md`.

### Step 3: Comparison
For each context file, compare:

| Aspect | What to Look For |
|--------|-----------------|
| **New files** | Files in `regulations/` that did not exist before |
| **Modified files** | Content differs from the last read |
| **New requirements** | Obligations not present in the previous version |
| **Changed deadlines** | Modified effective dates |
| **Updated penalties** | Changes in amounts or types of penalties |
| **Repeals** | Regulations repealed or replaced |

### Step 4: Diff Report Generation

Output format:

    # Diff Report - [DATE]

    ## Comparison with last update on [PREVIOUS_DATE]

    ### New Files
    - `regulations/[name].md` - [short description]

    ### Modified Files
    - `regulations/[name].md`
      - ADDED: [description of new content]
      - MODIFIED: [what has changed]
      - REMOVED: [what is no longer valid]

    ### New Requirements Identified
    | Requirement | Source | Deadline | Priority |
    |-------------|--------|----------|----------|
    | [desc] | [law] | [date] | CRITICAL/HIGH/MEDIUM/LOW |

    ### Removed/Repealed Requirements
    | Requirement | Reason |
    |-------------|--------|
    | [desc] | [repealed by / replaced by] |

    ### No Changes
    - `regulations/[name].md` - unchanged

### Step 5: Metadata Update
After the comparison, update `_last_updated.json` with the new state.

## Special Cases Handling

### First Execution
If `_last_updated.json` has `last_update: null`:
- Treat everything as "new"
- Do not generate a diff, but an initial report

### Corrupted or Missing File
If a context file is missing or corrupted:
- Report it
- Recreate it from the available context
- Note that data may be incomplete

## Best Practices
- Always run the diff BEFORE overwriting context files
- Store the diff report for traceability
- New requirements identified in the diff must be passed to the `analyzer_agent`