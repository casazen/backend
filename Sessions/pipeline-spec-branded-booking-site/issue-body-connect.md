## User Story

Operator connects Stripe Express account via hosted onboarding; CasaZen tracks charges_enabled before checkout goes live.

## Acceptance Criteria

See Sessions/specs/spec-connect-onboarding.md (AC1-AC10).

## Technical Notes

- EF migration for Org connect status fields
- StripeConnectService + ConnectController + connected webhook route
- Frontend payments settings page
