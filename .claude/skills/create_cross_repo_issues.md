# Skill: Create Cross-Repo Issues - Simplified

## Description
Quick skill to create linked GitHub issues for features that span both backend and frontend repositories. Uses the scrum_master agent internally.

## When to Use
- Feature requires both backend AND frontend
- Need cross-linked issues for coordination
- After architecture is designed (by architect agent)

## How to Invoke
```
/cross-repo-issues
```

**Note**: This is a thin wrapper around the scrum_master agent. For complex coordination, invoke the agent directly.

## Prerequisites
- Architecture plan must exist: `.claude/plans/[REQUEST-ID]-plan.md`
- GitHub CLI (`gh`) must be configured
- User must have write access to both repositories
- Issue templates must be prepared

## Workflow

### Step 1: Read Architecture Plan
Read the implementation plan to extract:
- Feature name
- Backend tasks checklist
- Frontend tasks checklist
- API endpoints to implement/consume
- Dependencies
- Acceptance criteria
- Priority level

### Step 2: Prepare Backend Issue
Create backend issue content with:

```markdown
# [Feature Name] - Backend Implementation

**Request ID**: [PO-YYYY-MM-DD-NNN]
**Priority**: [Critical|High|Medium|Low]
**Plan**: .claude/plans/[REQUEST-ID]-plan.md

## Overview
[Brief description of the feature]

## Backend Tasks
- [ ] Database migration: `Add[FeatureName]`
- [ ] Entities: [list entities to create/modify]
- [ ] Repositories: [list repository interfaces/implementations]
- [ ] Services: [list service interfaces/implementations]
- [ ] Controllers: [list controllers to create/modify]
- [ ] External integrations: [OTA adapters, Stripe, SendGrid, etc.]
- [ ] Unit tests: [list test files]
- [ ] Integration tests: [list test scenarios]
- [ ] Swagger documentation update
- [ ] Deploy to staging
- [ ] Deploy to production

## API Endpoints

### Endpoint 1: [Name]
- **Path**: `/api/v1/resource`
- **Method**: POST
- **Auth**: Required (JWT)
- **Description**: [What this endpoint does]

[Repeat for each endpoint]

## Dependencies
- [List backend dependencies]
- [List blocking issues]

## Acceptance Criteria
- [ ] [Criterion 1 from PO request]
- [ ] [Criterion 2 from PO request]
- [ ] All unit tests pass
- [ ] All integration tests pass
- [ ] Code coverage > 80%
- [ ] No security vulnerabilities
- [ ] Swagger documentation updated

## Testing Strategy
- Unit tests: [coverage targets]
- Integration tests: [scenarios to test]
- External integration tests: [OTA, Stripe, etc.]

## Definition of Done
- [ ] All tasks completed
- [ ] All tests passing
- [ ] Code reviewed and approved
- [ ] Deployed to staging (verified)
- [ ] Frontend team notified (API ready)
- [ ] Deployed to production

## Related Issues
- **Frontend Issue**: casazen/frontend#[will be updated]
- **Plan**: .claude/plans/[REQUEST-ID]-plan.md
- **Request**: .claude/requests/[REQUEST-ID].md
```

### Step 3: Create Backend Issue
Use GitHub CLI to create the backend issue:

```bash
gh issue create \
  --title "[Feature] Feature Name - Backend Implementation" \
  --body-file .claude/issues/backend-issue-body.md \
  --label "feature,backend,po-request,priority-[level]" \
  --assignee "@me"
```

Store the backend issue number for cross-linking.

### Step 4: Prepare Frontend Issue
Create frontend issue content with:

```markdown
# [Feature Name] - Frontend Implementation

**Request ID**: [PO-YYYY-MM-DD-NNN]
**Priority**: [Critical|High|Medium|Low]
**Related Backend Issue**: casazen/backend#[BACKEND_ISSUE_NUM]

## Overview
[Brief description of the feature]

## Frontend Tasks
- [ ] API service layer: [create/modify API service files]
- [ ] Pages: [list pages to create/modify]
- [ ] Components: [list components to create/modify]
- [ ] State management: [Redux actions/reducers or Context]
- [ ] Routing: [add/modify routes]
- [ ] Validation: [form validation, input validation]
- [ ] Error handling: [error boundaries, user feedback]
- [ ] Loading states: [spinners, skeletons]
- [ ] Component tests: [list test files]
- [ ] Integration tests: [API integration scenarios]
- [ ] E2E tests: [user flow tests]
- [ ] Deploy to staging
- [ ] Deploy to production

## API Integration

### Endpoint 1: [Name]
- **Path**: `/api/v1/resource`
- **Method**: POST
- **Usage**: [How this is used in the frontend]
- **Component**: [Which component makes this call]

[Repeat for each endpoint]

## User Flow
1. [Step 1: User action]
2. [Step 2: System response]
3. [Step 3: API call]
4. [Step 4: Success/error handling]

## Dependencies
- **Backend API must be deployed first** (casazen/backend#[BACKEND_ISSUE_NUM])
- [Other frontend dependencies]
- [Blocking issues]

## Acceptance Criteria
- [ ] [Criterion 1 from PO request]
- [ ] [Criterion 2 from PO request]
- [ ] All component tests pass
- [ ] Integration tests pass
- [ ] E2E tests pass
- [ ] Responsive design (mobile, tablet, desktop)
- [ ] Accessibility (WCAG AA)
- [ ] No console errors/warnings

## Testing Strategy
- Component tests: [coverage targets]
- Integration tests: [API integration scenarios]
- E2E tests: [complete user flows]

## Definition of Done
- [ ] All tasks completed
- [ ] All tests passing
- [ ] Code reviewed and approved
- [ ] Backend API integration verified
- [ ] Deployed to staging (verified)
- [ ] User acceptance testing (UAT) completed
- [ ] Deployed to production

## Related Issues
- **Backend Issue**: casazen/backend#[BACKEND_ISSUE_NUM]
- **Plan**: Available in backend repo - .claude/plans/[REQUEST-ID]-plan.md
- **Request**: Available in backend repo - .claude/requests/[REQUEST-ID].md
```

### Step 5: Create Frontend Issue
Use GitHub CLI to create the frontend issue:

```bash
gh issue create \
  --repo casazen/frontend \
  --title "[Feature] Feature Name - Frontend Implementation" \
  --body-file .claude/issues/frontend-issue-body.md \
  --label "feature,frontend,po-request,priority-[level]" \
  --assignee "@me"
```

Store the frontend issue number for cross-linking.

### Step 6: Cross-Link Issues
Update both issues with bidirectional links:

```bash
# Update backend issue
gh issue comment [BACKEND_ISSUE_NUM] \
  --body "**Related Frontend Issue**: casazen/frontend#[FRONTEND_ISSUE_NUM]"

gh issue edit [BACKEND_ISSUE_NUM] \
  --add-label "has-frontend"

# Update frontend issue
gh issue comment [FRONTEND_ISSUE_NUM] \
  --repo casazen/frontend \
  --body "**Related Backend Issue**: casazen/backend#[BACKEND_ISSUE_NUM]"

gh issue edit [FRONTEND_ISSUE_NUM] \
  --repo casazen/frontend \
  --add-label "has-backend"
```

### Step 7: Create Coordination Document
Create a coordination status document:

```bash
.claude/coordination/[REQUEST-ID]-status.md
```

With initial content:
```markdown
# Implementation Status - [Feature Name]

## Metadata
- **Request ID**: [REQUEST-ID]
- **Created**: YYYY-MM-DD HH:MM
- **Status**: Ready for Implementation
- **Last Updated**: YYYY-MM-DD HH:MM

## Issues
- **Backend**: casazen/backend#[BACKEND_ISSUE_NUM] - Status: Open
- **Frontend**: casazen/frontend#[FRONTEND_ISSUE_NUM] - Status: Open

## Progress
Backend: Not started
Frontend: Waiting for backend API

## Next Steps
1. Begin backend implementation
2. Deploy backend API to staging
3. Notify frontend team when API is ready
4. Begin frontend implementation
5. Integration testing
6. Production deployment (both repos)

## Checkpoints
- [ ] Checkpoint 1: Backend API ready (target: Day 5)
- [ ] Checkpoint 2: Frontend components ready (target: Day 11)
- [ ] Checkpoint 3: Integration complete (target: Day 14)
- [ ] Checkpoint 4: Production deployment (target: Day 15)
```

### Step 8: Notify Stakeholders
Create a notification for the Product Owner and team:

```markdown
✅ **GitHub Issues Created: [Feature Name]**

**Request ID**: [REQUEST-ID]

**Backend Issue**: casazen/backend#[BACKEND_ISSUE_NUM]
https://github.com/casazen/backend/issues/[BACKEND_ISSUE_NUM]

**Frontend Issue**: casazen/frontend#[FRONTEND_ISSUE_NUM]
https://github.com/casazen/frontend/issues/[FRONTEND_ISSUE_NUM]

**Status**: Ready for implementation
**Coordination**: .claude/coordination/[REQUEST-ID]-status.md

**Next Steps**:
1. Backend team starts implementation
2. Frontend team prepares (awaits backend API)
3. Integration testing once backend is deployed

Issues are cross-linked for easy tracking.
```

## Error Handling

If issue creation fails:
1. Check GitHub CLI authentication: `gh auth status`
2. Check repository access: `gh repo view casazen/backend`
3. Check rate limits: `gh api rate_limit`
4. Retry with exponential backoff
5. If all fails, provide manual instructions

## Output Files
- Two GitHub issues (backend + frontend) with cross-links
- Coordination status document
- Notification to stakeholders

## Best Practices
- Always create cross-links (backend ↔️ frontend)
- Use consistent labeling across both repositories
- Include all relevant context in both issues
- Reference the implementation plan from both issues
- Set appropriate priority labels
- Assign to the right team members

## Notes
- Backend issue is usually created first (logical dependency)
- Frontend issue explicitly states "waiting for backend API"
- Coordination document tracks progress across both repos
- Issues should be detailed enough to work independently

## Related Skills
- `open_github_issue` - Base skill for creating issues
- `sync_implementation` - Monitor and sync progress

## Related Agents
- `orchestrator_agent` - Uses this skill to create issues
- `architecture_planner` - Provides the plan this skill uses
