#!/usr/bin/env bash
# session-context.sh
# Prints a compact project context summary at session start.
# Triggered as a UserPromptSubmit hook on the first message.
#
# Output is injected into the conversation as a system note.
# Keeps the summary short (<200 tokens) to minimize cost.

set -euo pipefail

CONTEXT_FILE=".claude/context/codebase_map.md"
LAST_UPDATED=".claude/context/_last_updated.json"
ROADMAP=".claude/context/planning/product-roadmap.md"

echo "=== CasaZen Session Context ==="
echo ""
echo "Tech: .NET 10 / C# 13, EF Core, SQL Server"
echo "Layers: Web (API) → Core (Domain) → Infrastructure (Data+OTA) → Tests"
echo ""

# Regulatory last update
if [ -f "$LAST_UPDATED" ]; then
  LAST_RUN=$(grep -o '"last_update": "[^"]*"' "$LAST_UPDATED" | cut -d'"' -f4 | cut -c1-10 || echo "unknown")
  echo "Regulatory context last updated: $LAST_RUN"
fi

# Roadmap exists?
if [ -f "$ROADMAP" ]; then
  echo "Product roadmap: EXISTS (.claude/context/planning/product-roadmap.md)"
else
  echo "Product roadmap: MISSING — run /compliance-feature to create"
fi

# Open issues count
ISSUE_COUNT=$(gh issue list --state open --json number --jq 'length' 2>/dev/null || echo "?")
echo "Open issues: $ISSUE_COUNT"

echo ""
echo "Core guides: PLANNING.md | DEVELOPMENT.md"
echo "Quick start: /compliance-feature (plan) | /feature-implementation (implement)"
