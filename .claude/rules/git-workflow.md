# Git Workflow

## Branches
- Main: `main`
- Features: `feature/descriptive-name`
- Fixes: `fix/descriptive-name`
- Hotfixes: `hotfix/descriptive-name`

## Commits
- Format: Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`)
- **NEVER** include "Co-Authored-By: Claude" signature (user preference)
- Example: `feat(cin): add CIN code validation to Property entity`

## Pull Requests
- **MUST** pass all CI checks before merge
- **MUST** include tests for new features
- **MUST** link to related issue: `Closes #123`
- Include screenshots for UI changes
