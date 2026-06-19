---
id: US-XXX                    # User story ID (optional for non-roadmap work)
slug: spec-example            # Filename without .md — must match spec-{slug}.md
title: Short human title
phase: 1                      # 0 | 1 | 1.5 | 2 | 3 | 4 | ops | compliance | maintenance
type: feature                 # feature | enabler | fix | compliance | ops | spike
priority: P1                  # P0 (now) | P1 (this phase) | P2 (next) | P3 (later) | —
status: specced               # idea | specced | planned | in-dev | shipped | blocked | deferred
issue:                        # GitHub issue # when planned+ (e.g. 271)
depends_on: []                # slugs of other specs
blocks: []                    # slugs this unblocks
exit_contributes_to:          # Phase exit criterion this item helps satisfy (one line)
last_reviewed: YYYY-MM-DD
---

# Spec — {Title} ({id or Issue #})

## Overview

One paragraph: problem, scope, why now.

**Phase:** {N} — {phase name} · **Type:** {type} · **Status:** {status}

---

## User Story

As a …, I want …, so that …

---

## Acceptance Criteria

### Backend

- **AC1**: …

### Frontend

- **AC8**: …

---

## Technical Notes

| File | Action |
|---|---|
| `path/to/file` | Create / Modify — … |

**Complexity:** S | M | L  
**Migration:** yes/no — …  
**Dependencies:** `spec-…`

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] items, if any

---

## Out of Scope

- …
