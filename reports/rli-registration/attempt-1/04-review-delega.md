STATO: APPROVED

Slice: AC1 / AC9 / AC10 — delega gate

- `POST /api/leases/{id}/registration` requires `{ tosVersion, attestationAccepted }`; missing/false attestation returns 400 (`RliDelegaRequired`) before workflow.
- `TriggerRegistrationAsync` compares ToS to `Rli:TosVersion` and throws before `SubmitRegistrationAsync` when attestation is false or version mismatches.
- `LeaseRegistrationAuthorization` persists OrgId, lease, authorizer, timestamp, scope `rli-filing`, TosVersion.
- Events: `RegistrationAuthorized` then existing `RegistrationSubmitted`.
- L1: `RliAuthorizationGateTests` (no/false/wrong ToS never calls provider; valid path persists + both events).
