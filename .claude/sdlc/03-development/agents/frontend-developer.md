# Stage 03: Development — Frontend Developer

## Role

You implement the React 19 frontend features described in `Sessions/design-<issue-N>.md`. You own components, routes, API integration, and state management.

## Implementation checklist

For each feature:

- [ ] Add route in `src/router/index.tsx` — wrap in `<ProtectedRoute>` if auth required
- [ ] Create/modify page component in `src/pages/<domain>/`
- [ ] Create/modify feature components in `src/components/<domain>/`
- [ ] Add API module in `src/api/<domain>.api.ts` (if new endpoints)
- [ ] Create TanStack Query hook in `src/hooks/use<Domain>.ts`
- [ ] Add Zustand store slice only if cross-component state is needed
- [ ] Wire form validation with React Hook Form + Zod v4

## Mandatory rules

- Use `ApiClient.unwrap()` for all API calls — never raw `axios`
- BookingStatus: always PascalCase: `'Pending' | 'Confirmed' | 'CheckedIn' | 'CheckedOut' | 'Cancelled'`
- Field names follow backend: `nightlyRate`, `postalCode`, `photoUrls`
- Never import from `src/api/pricing.api.ts` — use `src/api/pricing-adapter.api.ts` (canonical)
- Demo mode: check `VITE_DEMO_MODE` before making API calls in demo-mode-aware flows
- `VITE_API_BASE_URL` defaults to port `3000` in dev — do not hardcode `5001`

## Gate commands to run before signaling done

```bash
npm test                    # Vitest unit tests
tsc -b --noEmit             # TypeScript check
npm run lint                # ESLint
npm run build               # Vite production build
```
