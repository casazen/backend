# Workflow: Feature Implementation (Cross-Repo)

## Overview
Complete workflow for implementing features that require both **backend** (API) and **frontend** (UI) work, coordinating across two repositories.

**Duration**: Typically 7-15 days depending on complexity
**Repositories**: casazen/backend + casazen/frontend

---

## Trigger
- Product Owner requests a new feature
- User story is ready for implementation
- OTA integration is needed
- Major enhancement is planned

---

## Workflow Steps

### Step 1: Requirements Gathering
**Agent**: `product_owner` (global)

```bash
# Invoke product_owner agent
Task: Gather requirements for [feature name]
```

**Product Owner collects**:
- Feature description and business goal
- User stories with acceptance criteria
- Priority level (Critical, High, Medium, Low)
- Scope (backend, frontend, or both)
- Regulatory requirements (CIN, GDPR, Tax, etc.)
- External integrations (OTA, Stripe, SendGrid)

**Output**: Structured requirements document

**Duration**: 1-2 hours

---

### Step 2: Architecture Design
**Agent**: `architect` (global)

```bash
# Invoke architect agent with requirements
Task: Design architecture for [feature name]
Input: [requirements from Step 1]
```

**Architect designs**:
- **Backend**: API endpoints, database schema, services, external integrations
- **Frontend**: Pages, components, state management, user flows
- **Dependencies**: Implementation order (backend first, then frontend)
- **Testing strategy**: Unit, integration, E2E tests
- **Risk assessment**: Security, performance, complexity

**Output**:
- Architectural plan document
- API contracts (request/response DTOs)
- Database migration plan
- Component hierarchy (frontend)

**Duration**: 2-4 hours for medium complexity

---

### Step 3: Cross-Repo Issue Creation & Coordination
**Agent**: `scrum_master` (project-specific)

```bash
# Invoke scrum_master agent
Task: Create cross-repo issues for [feature name]
Input: [architecture plan from Step 2]
```

**Scrum Master creates**:
1. **Backend GitHub issue** (casazen/backend#XXX)
   - Backend tasks checklist
   - API endpoints to implement
   - Database migrations
   - Tests required

2. **Frontend GitHub issue** (casazen/frontend#YYY)
   - Frontend tasks checklist
   - API endpoints to consume
   - Components to create
   - Tests required
   - Dependency note: "Requires backend API first"

3. **Cross-links**: Both issues reference each other
4. **Coordination doc**: `.claude/coordination/[FEATURE]-status.md`

**Output**:
- casazen/backend#XXX (issue created)
- casazen/frontend#YYY (issue created)
- Coordination tracking document

**Duration**: 30 minutes

---

### Step 4: Backend Implementation
**Agent**: `feature_developer` (global)

```bash
# Invoke feature_developer agent for backend
Task: Implement backend for [feature name]
Repository: backend
Issue: casazen/backend#XXX
```

**Developer implements**:
1. Database migration (`dotnet ef migrations add [Name]`)
2. Entities/models
3. Repository interfaces and implementations
4. Service layer with business logic
5. API controllers
6. Request/response DTOs
7. Validation and error handling
8. External integrations (if needed)

**Output**:
- Code committed and pushed
- Pull request created
- All backend tasks checked off

**Duration**: 3-7 days (depends on complexity)

---

### Step 5: Backend Testing
**Agent**: `test_engineer` (global)

```bash
# Invoke test_engineer agent
Task: Write tests for backend implementation
Issue: casazen/backend#XXX
```

**Test Engineer writes**:
- Unit tests (services, business logic)
- Integration tests (API endpoints, database)
- External integration tests (mocked OTA APIs, etc.)

**Verification**:
```bash
dotnet test
# All tests must pass (target: 80%+ coverage)
```

**Output**: Comprehensive test suite

**Duration**: 1-2 days (parallel to implementation)

---

### Step 6: Backend Code Review
**Agent**: `code_reviewer` (global)

```bash
# Invoke code_reviewer agent
Task: Review backend PR
PR: casazen/backend#[PR-NUM]
```

**Reviewer checks**:
- Code quality and standards
- Security (OWASP top 10, input validation)
- Test coverage
- API contract correctness
- Documentation (Swagger)
- Performance considerations

**Output**: PR approved or requested changes

**Duration**: 2-4 hours

---

### Step 7: Backend Deployment to Staging
**Agent**: `release_manager` (global)

```bash
# Invoke release_manager agent
Task: Deploy backend to staging
Branch: main (after PR merge)
Environment: staging
```

**Release Manager**:
1. Merges PR to main
2. Triggers CI/CD pipeline
3. Deploys to staging environment
4. Runs smoke tests
5. Verifies API is accessible

**Verification**:
```bash
curl https://api-staging.casazen.app/health
curl https://api-staging.casazen.app/swagger
```

**Output**: Backend live on staging

**Duration**: 30 minutes

---

### Step 8: Notification to Frontend Team
**Agent**: `scrum_master` (project-specific)

```bash
# Scrum master notifies frontend
Task: Notify frontend that backend API is ready
```

**Scrum Master posts**:
```
Comment on casazen/frontend#YYY:
"✅ Backend API is ready for integration!

Staging API: https://api-staging.casazen.app
Swagger docs: https://api-staging.casazen.app/swagger

Endpoints available:
- POST /api/v1/resource
- GET /api/v1/resource

You can now start frontend implementation."
```

Updates frontend issue label: `needs-backend` → `backend-ready`

**Duration**: 5 minutes

---

### Step 9: Frontend Implementation
**Agent**: `feature_developer` (global)

```bash
# Invoke feature_developer agent for frontend
Task: Implement frontend for [feature name]
Repository: frontend
Issue: casazen/frontend#YYY
Backend API: https://api-staging.casazen.app
```

**Developer implements**:
1. API service layer (calls to backend)
2. Pages and components
3. State management (Redux/Context)
4. Form validation and error handling
5. Loading states and user feedback
6. Routing

**Output**:
- Code committed and pushed
- Pull request created
- All frontend tasks checked off

**Duration**: 4-8 days (depends on complexity)

---

### Step 10: Frontend Testing
**Agent**: `test_engineer` (global)

```bash
# Invoke test_engineer agent
Task: Write tests for frontend implementation
Issue: casazen/frontend#YYY
```

**Test Engineer writes**:
- Component tests (React Testing Library)
- Integration tests (API calls)
- E2E tests (Cypress/Playwright) for critical paths

**Verification**:
```bash
npm test
# All tests must pass
```

**Output**: Frontend test suite

**Duration**: 2-3 days (parallel to implementation)

---

### Step 11: Frontend Code Review
**Agent**: `code_reviewer` (global)

```bash
# Invoke code_reviewer agent
Task: Review frontend PR
PR: casazen/frontend#[PR-NUM]
```

**Reviewer checks**:
- Code quality and standards
- Accessibility (WCAG AA)
- Responsive design
- Error handling
- API integration correctness
- Test coverage

**Output**: PR approved or requested changes

**Duration**: 2-4 hours

---

### Step 12: Frontend Deployment to Staging
**Agent**: `release_manager` (global)

```bash
# Invoke release_manager agent
Task: Deploy frontend to staging
Repository: frontend
Branch: main (after PR merge)
Environment: staging
```

**Release Manager**:
1. Merges PR to main
2. Triggers CI/CD pipeline
3. Deploys to staging
4. Verifies frontend connects to backend

**Verification**:
```bash
curl https://staging.casazen.app/health
# Test the feature manually in browser
```

**Output**: Frontend live on staging

**Duration**: 30 minutes

---

### Step 13: Integration Testing
**Agent**: `test_engineer` (global)

```bash
# Invoke test_engineer agent
Task: Run integration tests (backend + frontend)
Environment: staging
```

**Test Engineer verifies**:
- Frontend successfully calls backend API
- Data flows correctly end-to-end
- Error scenarios handled properly
- E2E tests pass

**Output**: Integration verified

**Duration**: 1-2 hours

---

### Step 14: Coordinated Production Deployment
**Agent**: `scrum_master` (project-specific)

```bash
# Invoke scrum_master agent
Task: Deploy feature to production (both repos)
Feature: [feature name]
```

**Scrum Master coordinates**:

1. **Deploy Backend First**:
   ```bash
   # Release manager deploys backend
   Deploy: casazen/backend to production
   Verify: curl https://api.casazen.app/health
   ```

2. **Deploy Frontend Second**:
   ```bash
   # Release manager deploys frontend
   Deploy: casazen/frontend to production
   Verify: curl https://casazen.app/health
   ```

3. **Smoke Tests**:
   - Test critical paths
   - Verify feature works end-to-end

4. **Monitoring**:
   - Watch logs for 30-60 minutes
   - Monitor error rates, performance

**Output**: Feature live in production

**Duration**: 1-2 hours

---

### Step 15: Issue Closure & Completion Report
**Agent**: `scrum_master` (project-specific)

```bash
# Invoke scrum_master agent
Task: Close issues and create completion report
Feature: [feature name]
```

**Scrum Master**:

1. **Closes Backend Issue**:
   ```
   ✅ Backend complete and deployed to production

   Delivered:
   - API endpoints: https://api.casazen.app
   - Tests: 15 unit, 8 integration (all passing)
   - Coverage: 87%

   Related: casazen/frontend#YYY (also complete)
   ```

2. **Closes Frontend Issue**:
   ```
   ✅ Frontend complete and deployed to production

   Delivered:
   - Feature: https://casazen.app/[feature-url]
   - Tests: 12 component, 5 integration, 3 E2E (all passing)

   Related: casazen/backend#XXX (complete)
   ```

3. **Updates Coordination Doc**:
   ```markdown
   ## Status: ✅ COMPLETED

   Completed: YYYY-MM-DD
   Duration: 12 days

   Production URLs:
   - Backend: https://api.casazen.app
   - Frontend: https://casazen.app

   Metrics:
   - Backend: 23 commits, 87% coverage
   - Frontend: 18 commits, 85% coverage
   - Total tests: 43
   ```

**Output**:
- Both issues closed
- Completion report
- Coordination doc archived

**Duration**: 30 minutes

---

## Summary Timeline

| Phase | Duration | Agent(s) |
|-------|----------|----------|
| Requirements | 1-2 hours | product_owner |
| Architecture | 2-4 hours | architect |
| Issue Creation | 30 min | scrum_master |
| Backend Dev + Test | 3-7 days | feature_developer, test_engineer |
| Backend Review | 2-4 hours | code_reviewer |
| Backend Deploy Staging | 30 min | release_manager |
| Frontend Notification | 5 min | scrum_master |
| Frontend Dev + Test | 4-8 days | feature_developer, test_engineer |
| Frontend Review | 2-4 hours | code_reviewer |
| Frontend Deploy Staging | 30 min | release_manager |
| Integration Testing | 1-2 hours | test_engineer |
| Production Deploy | 1-2 hours | scrum_master, release_manager |
| Closure | 30 min | scrum_master |
| **TOTAL** | **7-15 days** | Multiple |

---

## Critical Success Factors

✅ **Backend before Frontend**: Always implement backend API first
✅ **Cross-linking**: Issues must reference each other
✅ **Staging first**: Test on staging before production
✅ **Coordinated deployment**: Backend deploys before frontend
✅ **Communication**: Scrum master keeps both teams synchronized
✅ **Testing**: Comprehensive tests at every level

---

## Example: Booking.com Integration

**Initial Request**: "Integrate with Booking.com to sync property listings"

**Execution**:
```
Day 1: Requirements (product_owner) + Architecture (architect)
Day 2: Issues created (scrum_master) + Backend starts (feature_developer)
Day 3-6: Backend implementation (API, adapter, tests)
Day 7: Backend review + deploy staging
Day 8: Frontend notified, starts implementation
Day 9-12: Frontend implementation (pages, components, integration)
Day 13: Frontend review + deploy staging
Day 14: Integration testing + production deployment
Day 15: Issues closed, feature live

Result:
- casazen/backend#234 (closed)
- casazen/frontend#567 (closed)
- Booking.com integration live in production
```

---

## Related Workflows
- `critical_issue_response.md` - If issues arise during implementation
- `regulatory_compliance.md` - If feature has compliance requirements

## Related Documents
- `CLAUDE.md` - Project guidelines
- `.claude/config/project.json` - Technical configuration
- `.claude/agents/scrum_master.md` - Cross-repo coordination agent
