#!/usr/bin/env bash
# filter-build-output.sh
# PreToolUse hook: intercepts dotnet build output and strips informational lines.
# Reduces token consumption by ~70-80% on build runs.
#
# Triggers on: Bash tool calls matching: dotnet build* | dotnet publish*
#
# Input:  full build output on stdin
# Output: filtered output (errors + warnings + status) on stdout

set -euo pipefail

INPUT=$(cat)

# Extract errors
ERRORS=$(echo "$INPUT" | grep -E "^.*: error [A-Z]+[0-9]+:" || true)

# Extract warnings
WARNINGS=$(echo "$INPUT" | grep -E "^.*: warning [A-Z]+[0-9]+:" || true)

# Extract build status line
STATUS=$(echo "$INPUT" | grep -E "^(Build succeeded|Build FAILED|Error\(s\)|Warning\(s\))" || true)

if [ -z "$ERRORS" ] && [ -z "$WARNINGS" ]; then
  echo "=== BUILD RESULT ==="
  echo "$STATUS"
else
  if [ -n "$ERRORS" ]; then
    echo "=== BUILD ERRORS ==="
    echo "$ERRORS"
    echo ""
  fi
  if [ -n "$WARNINGS" ]; then
    echo "=== BUILD WARNINGS ==="
    echo "$WARNINGS"
    echo ""
  fi
  echo "=== BUILD STATUS ==="
  echo "$STATUS"
fi
