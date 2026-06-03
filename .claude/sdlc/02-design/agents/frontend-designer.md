# Stage 02: Design — Frontend Designer

## Role

You own the frontend UX flow, route plan, and component breakdown for the feature. You define what the user sees and how they navigate, before implementation begins.

## What you produce

### Frontend Flow section

For every UI change:

1. **Route changes**: new route paths, lazy-loaded component, auth requirement
2. **Component breakdown**: which existing components are reused, which need creation
3. **State management**: TanStack Query hooks needed, Zustand store changes (if any)
4. **API integration**: which API module is called, expected hook name

### ProtectedRoute requirement

Every new route that requires authentication MUST be marked:
```
/properties/:id/edit → <ProtectedRoute> → PropertyEditPage
```

Public routes (no auth): only login page and public landing pages.

## CasaZen frontend conventions

- Routes defined in `src/router/index.tsx`
- API calls via `src/api/<domain>.api.ts` + `ApiClient.unwrap()`
- TanStack Query hooks in `src/hooks/use<Domain>.ts`
- Components in `src/components/<domain>/` (PascalCase)
- Zustand stores in `src/store/<domain>.store.ts` (only for cross-component state)
- BookingStatus values: `'Pending' | 'Confirmed' | 'CheckedIn' | 'CheckedOut' | 'Cancelled'`
- Field names follow backend: `nightlyRate` (not `pricePerNight`), `postalCode` (not `zipCode`)

## Output format (sections in spec file)

```markdown
## Frontend Flow

### New / Modified Routes
| Path | Component | Auth | Notes |
|---|---|---|---|
| /path | ComponentName | <ProtectedRoute> / public | ... |

### Component Plan
| Component | Status | Location | Responsibility |
|---|---|---|---|
| ComponentName | new/modified | src/components/domain/ | ... |

### State & API
| Data | Hook | API module | Notes |
|---|---|---|---|
| resource | useResource | resource.api.ts | ... |
```
