---
name: list-issues
description: List GitHub issues dynamically (compliance-focused, no stale cache)
invocable: true
---

# List Open Issues

Retrieve current open issues from GitHub, focused on regulatory compliance.

## Usage

```bash
# All compliance issues
gh issue list --label compliance --state open --json number,title,labels,createdAt

# By priority
gh issue list --label "compliance,priority:critical" --state open --json number,title,labels

# With details
gh issue list --label compliance --state open --json number,title,labels,createdAt,body | head -50
```

## Output
Returns live GitHub data (no stale cache).

## Replaces
Old static file: `.claude/context/open_issues.md` (now deprecated)

## Benefits
- Always up-to-date
- No manual maintenance
- Direct GitHub API query
