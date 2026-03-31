#!/bin/bash
# Preprocessing hook to filter test output - shows only failures
# Reduces token usage by filtering verbose test output

input=$(cat)
cmd=$(echo "$input" | jq -r '.tool_input.command')

# If running tests, filter to show only failures
if [[ "$cmd" =~ ^(dotnet test|npm test|pytest|go test) ]]; then
  # For dotnet test, filter to show only failed tests and summary
  filtered_cmd="$cmd 2>&1 | grep -A 10 -E '(Failed|Error|Exception|Total tests:)' | head -100"
  echo "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\",\"updatedInput\":{\"command\":\"$filtered_cmd\"}}}"
else
  # For other commands, allow as-is
  echo "{}"
fi
