STATO: APPROVED

Slice: AC2 / AC6 / AC7 / AC8 — poll-only, checklist, reminders, flag

- `LeaseRegistrationStatusPollingJob` still only calls `PollStatusAsync`. No Hangfire submit job. L1: `LeaseRegistrationStatusPollingJobTests`.
- `RliDeadlineReminderJob` daily `0 8 * * *` (`rli-deadline-reminder`). Milestones t-15 / t-7 / t-1 / overdue; idempotent via `DeadlineReminderSent` + payload. Extra-EU distinct reminder (`extra-eu`, Questura subject).
- `GET /api/leases/{id}/rli/checklist` includes Questura item iff `HasExtraEUTenant`.
- `Rli:FilingEnabled` default false; `OpenapiLeaseRegistrationProvider` remains stub (no live HTTP) even if the flag is true. Equivalent to the plan's `Openapi:FilingEnabled` gate.
- L1: `RliDeadlineReminderJobTests`, `RliChecklistServiceTests`.
