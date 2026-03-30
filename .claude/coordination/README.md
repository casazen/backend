# Cross-Repository Coordination

This directory contains coordination documents for features that span both backend and frontend repositories.

## Purpose

When a feature requires implementation in **both** repositories, the `scrum_master_casazen` agent creates coordination documents to track progress and synchronize deployments.

## Document Format

**File**: `{feature-id}-status.md`

Contains:
- Cross-linked issue numbers (backend + frontend)
- Progress checkpoints with target dates
- Dependency graph (mermaid diagram)
- Current blockers
- Deployment coordination
- Status updates

## Usage

### For Cross-Repo Features

1. **Creation**: Scrum master creates document when feature spans both repos
2. **Tracking**: Updated daily during active development
3. **Checkpoints**:
   - Checkpoint 1: Backend API ready for frontend integration
   - Checkpoint 2: Frontend integration complete
   - Checkpoint 3: Production deployment synchronized

### Example

```markdown
# 🎯 Cross-Repo Status: Booking.com Integration

## Issues
- **Backend**: casazen/backend#123 - Status: ✅ Complete
- **Frontend**: casazen/frontend#456 - Status: ⏳ In Progress

## Progress Checkpoints

### ✅ Checkpoint 1: Backend API Ready
- [x] API endpoints implemented
- [x] Tests passing
- [x] Deployed to staging
- [x] Frontend team notified

### ⏳ Checkpoint 2: Frontend Integration
- [x] Frontend consumes staging API
- [x] Components implemented
- [ ] Tests passing
```

## Commands

```bash
# List all coordination docs
ls -la

# View specific feature coordination
cat booking-com-integration-status.md

# Search for blockers
grep -r "Current Blocker" .
```

## Integration with Scrum Master Agent

The `scrum_master_casazen` agent:
- Creates coordination docs for cross-repo features
- Updates status daily
- Notifies teams when checkpoints are reached
- Coordinates synchronized deployments

## Notes

- Single-repository features don't need coordination docs (use generic agents instead)
- Backend typically implemented first (APIs before UI)
- Frontend labeled `needs-backend` until backend API is deployed
- Production deployments always synchronized (backend first, then frontend)
