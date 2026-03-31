# Preprocessing Hooks

Hooks that run before tool execution to reduce token usage by filtering verbose output.

## Available Hooks

### `filter-test-output.sh`
**Trigger**: Before `dotnet test`, `npm test`, `pytest`, `go test`
**Action**: Filters output to show only failures and test summary
**Token Savings**: 80-90%

### `filter-build-output.sh`
**Trigger**: Before `dotnet build`, `npm run build`, `mvn compile`
**Action**: Filters output to show only errors, warnings, and build status
**Token Savings**: 70-80%

## Configuration

Hooks are activated in `.claude/settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": ".claude/hooks/filter-test-output.sh"
          }
        ]
      }
    ]
  }
}
```

## Testing Hooks

To test if a hook is working:

1. Run a command that triggers the hook (e.g., `dotnet test`)
2. Check the output - it should be filtered
3. If not working, check hook file is executable: `chmod +x .claude/hooks/*.sh`

## Disabling Hooks

Temporarily disable by commenting out in `settings.json`:

```json
{
  "hooks": {
    "PreToolUse": []  // Empty array disables all hooks
  }
}
```

## Reference

https://code.claude.com/docs/en/hooks
