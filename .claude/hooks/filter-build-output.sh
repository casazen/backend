#!/bin/bash
# Preprocessing hook to filter build output - shows only errors and warnings
# Reduces token usage by filtering verbose compilation output

input=$(cat)
cmd=$(echo "$input" | jq -r '.tool_input.command')

# If running build commands, filter to show only errors/warnings
if [[ "$cmd" =~ ^(dotnet build|npm run build|mvn compile) ]]; then
  filtered_cmd="$cmd 2>&1 | grep -E '(error|warning|Error|Warning|Build FAILED|Build succeeded)' | head -100"
  echo "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\",\"updatedInput\":{\"command\":\"$filtered_cmd\"}}}"
else
  echo "{}"
fi
