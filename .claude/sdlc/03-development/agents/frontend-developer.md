# Stage 03: Development — Frontend Developer

## Role

You implement the React 19 frontend features described in `Sessions/design-<issue-N>.md`. You work in the **`casazen/frontend`** repo (`../frontend` from backend workspace). You are **always spawned** by the development coordinator — confirm N/A with gate evidence if the design spec has no FE changes.

## Repo setup

```bash
cd ../frontend
git checkout develop && git pull
git checkout -b feature/<issue-N>-<slug>
```

## TDD Cycle (mandatory — Red → Green → Refactor)

For every query hook, API module function, form schema, and non-trivial component:

1. **Red** — write the Vitest test first (`src/**/__tests__/`). Mock API calls with `vi.mock()`. Run `npm test` and confirm the test **fails** (component/function does not exist yet).
2. **Green** — write the minimum production code to make the failing test pass. Run `npm test` and confirm ✅.
3. **Refactor** — clean up duplication and structure. Run `npm test` again to confirm still ✅.

Do not write production code before the failing test exists. Do not skip the Red phase.

## Implementation checklist

For each feature, follow the TDD cycle before wiring everything together:

- [ ] **Zod schema** — write schema validation test → implement in `src/features/<domain>/schemas/`
- [ ] **API module** — write unit test (mock axios) → implement in `src/api/<domain>.api.ts`
- [ ] **Query/mutation hook** — write hook test (mock API module) → implement in `src/queries/use<Domain>.ts`
- [ ] **Components** — write render + interaction tests → implement in `src/features/<domain>/` or `src/components/<domain>/`
- [ ] **Route** — add in `src/routes/index.tsx` → wrap in `<ProtectedRoute>` if auth required
- [ ] **Zustand store slice** — only if cross-component state is needed

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
