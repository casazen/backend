# ADR-003: Native host app repository layout

**Status:** Accepted (Fase 0 spike)  
**Date:** 2026-06-19  
**Issue:** #287  
**Informs:** `spec-native-host-app` (US-025, Fase 1)

## Context

CasaZen needs an Expo/React Native host app complementing the full web console. Fase 0 delivers a buildable scaffold with Auth0 PKCE proof-of-life.

## Decision

### Repository layout

Dedicated repo **`casazen/mobile`** (sibling to `backend` and `frontend`):

```
casazen/
  backend/
  frontend/
  mobile/          ← Expo SDK 52+, TypeScript
    app/           ← Expo Router file-based routes
    src/api/       ← Shared API client
    src/auth/      ← Auth0 PKCE
    eas.json       ← development | preview | production
```

**Rationale:** Separate release cadence (App Store / Play Store) vs web; EAS builds should not block FE CI.

### Auth — Auth0 PKCE

- Native app type in Auth0 dashboard.
- `expo-auth-session` + `expo-secure-store` for tokens.
- No client secret in bundle.

### API client

Hand-written TypeScript client targeting same OpenAPI surface as web. Base URL from `EXPO_PUBLIC_API_URL`.

### Deep links

Scheme: `casazen://` — booking detail `casazen://bookings/{id}` (Fase 1).

### EAS profiles

| Profile | Use |
|---|---|
| `development` | Simulator + dev client |
| `preview` | Internal TestFlight / APK |
| `production` | Store release |

### Maestro smoke (F0)

`mobile/.maestro/smoke.yaml` — launch app → login screen visible.

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-003-R1 | P0 | Native host app lives in dedicated repo `casazen/mobile` (Expo SDK 52+, TypeScript, Expo Router) |
| ADR-003-R2 | P0 | Auth0 PKCE via `expo-auth-session` + `expo-secure-store`; no client secret in bundle |
| ADR-003-R3 | P0 | Hand-written TS API client uses `EXPO_PUBLIC_API_URL` against the same API surface as web |
| ADR-003-R4 | P1 | Deep link scheme `casazen://` with booking detail route |
| ADR-003-R5 | P0 | EAS profiles: `development`, `preview`, `production` |
| ADR-003-R6 | P0 | Maestro smoke (`mobile/.maestro/smoke.yaml`) launches to login screen |

## Consequences

- Fase 1 implements M1–M7 screens per `spec-native-host-app`.
- Monorepo extraction possible later via npm workspaces if shared types grow.
