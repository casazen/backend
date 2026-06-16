# Batch Pipeline: Phase 1 Next 3 Specs (#5–7)

## Status
- status: running
- max_concurrent: 3
- active_workers: 1
- execution_mode: serialized (Stage 03+ — git workspace collision on parallel dev)
- release_lock_holder: (none)
- started: 2026-06-11T14:00:00Z
- last_updated: 2026-06-11T20:00:00Z
- stage_01_complete: 3/3
- stage_02_complete: 3/3
- stage_03_complete: 0/3

## Queue
| Order | Slug | issue | current_stage | notes |
|---|---|---|---|---|
| 1 | pipeline-spec-saas-billing | #230 | 03-development | branch `feature/230-saas-billing`; partial BE WIP in stashes |
| 2 | pipeline-spec-onboarding-plg | #271 | 03-development | branch `feature/271-onboarding-plg` |
| 3 | pipeline-spec-ltr-recurring-rent | #269 | **escalated** @ 03 | workspace pollution — see escalation |

## Blockers
- Parallel Stage 03 workers collided on single backend checkout (mixed billing/rent/onboarding files)
- Pipeline state + design specs were deleted during recovery — **recreated 2026-06-11**; design specs must be regenerated from issues if missing
- No PRs opened yet; release mutex unused

## Summary
- completed: 0
- escalated: 1
- pending: 2
