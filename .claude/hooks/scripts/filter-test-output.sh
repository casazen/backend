#!/usr/bin/env bash
# filter-test-output.sh
# PreToolUse hook: intercepts dotnet test output and strips verbose passing-test lines.
# Reduces token consumption by ~80-90% on test runs.
#
# Triggers on: Bash tool calls matching: dotnet test*
#
# Input:  full test runner output on stdin
# Output: filtered output (failures + summary only) on stdout

set -euo pipefail

INPUT=$(cat)

# Extract failure blocks: lines starting with "Failed" or "Error" or stack traces
FAILURES=$(echo "$INPUT" | grep -E "^(Failed|Error|FAILED|  at |   at |\[xUnit)" || true)

# Extract summary line (last "Test run for..." or "Passed! ..." or "FAILED")
SUMMARY=$(echo "$INPUT" | grep -E "^(Test Run|Passed!|Failed!|Error\(s\)|Build succeeded|Build FAILED|Total tests|  Passed:|  Failed:|  Skipped:)" || true)

# If no failures — output just the summary
if [ -z "$FAILURES" ]; then
  echo "=== TEST SUMMARY (all passed) ==="
  echo "$SUMMARY"
else
  echo "=== TEST FAILURES ==="
  echo "$FAILURES"
  echo ""
  echo "=== TEST SUMMARY ==="
  echo "$SUMMARY"
fi
