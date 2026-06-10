# Escalation: Stage 05 — Release

## Pipeline: property-detail
## Date: 2026-06-05

## Root Cause

`AddContextAuthorization` migration was **pending** on `casazen_test` and `casazen_prod`.
The migration SQL used `ON CONFLICT ("UserId", "ContextKey")` **before** the unique index was created → migration failed silently on deploy.

Deployed code queries `UserContextMemberships` / `LastUsedContextKey` via `ContextAuthorizationService` → unhandled `PostgresException` → **HTTP 500** on all context-protected endpoints (`/api/properties`, `/api/bookings`, etc.).

## Pipeline Gap

Stage 05 smoke tests only checked **unauthenticated 401** — never caught authenticated 500s.

## Fix Applied

1. Reordered migration: create unique index **before** seed SQL
2. `HasPermissionAsync`: JWT role fallback when user not in `Users` table (Auth0-only users)
3. Applied migration manually to `casazen_test` and `casazen_prod`
4. CI: fail if `/api/properties` or `/api/bookings` return 500 (unauthenticated)
5. Unit tests: `ContextAuthorizationServiceTests`

## User Action

Refresh staging app and retest login → properties list → bookings list.
