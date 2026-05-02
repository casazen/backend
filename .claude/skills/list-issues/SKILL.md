---
name: list-issues
description: List open GitHub issues live from the API. Use to check backlog state before starting implementation or planning. Replaces the deprecated static open_issues.md file.
---

# List Open Issues

Always fetches live data from GitHub — no stale cache.

## All open issues

```bash
gh issue list --state open --json number,title,labels,createdAt \
  --jq '.[] | "\(.number) [\(.labels | map(.name) | join(","))] \(.title)"'
```

## Compliance issues only

```bash
gh issue list --label compliance --state open \
  --json number,title,labels,createdAt,milestone
```

## By priority (critical first)

```bash
gh issue list --label "priority:critical" --state open --json number,title,labels
gh issue list --label "priority:high"     --state open --json number,title,labels
```

## Exclude epics (implementation-ready only)

```bash
gh issue list --state open --json number,title,labels \
  --jq '[.[] | select(all(.labels[]; .name != "epic"))]'
```

## Frontend repo

```bash
gh issue list --repo casazen/frontend --state open --json number,title,labels
```

## Open issue count

```bash
gh issue list --state open --json number --jq 'length'
```
