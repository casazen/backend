# FRONTEND-PROJECT.md — AI Context for CasaZen Frontend

## What this project does
CasaZen Frontend is the React SPA for the CasaZen vacation rental platform. It gives Italian property managers a UI to manage properties, bookings, payments, OTA channel sync, AI-driven pricing, and GDPR-compliant guest data. It communicates exclusively with the CasaZen backend REST API.

## Stack snapshot
- **Language**: TypeScript 5.9 (strict mode)
- **Framework**: React 19.2 + Vite 8
- **Styling**: Tailwind CSS v4 + Radix UI primitives
- **Routing**: React Router v7 (`createBrowserRouter`)
- **Server state**: TanStack Query v5 (`src/queries/`)
- **Global state**: Zustand v5 (`src/store/`)
- **Forms**: React Hook Form v7 + Zod v4
- **HTTP**: Axios v1 with JWT Bearer interceptor (`src/lib/axios.ts`)
- **Auth**: Auth0 (`@auth0/auth0-react`) — demo mode available
- **Tests**: Vitest v4 (unit) + Playwright v1.60 (e2e)

## Repo layout
```
frontend/
├── src/
│   ├── api/            # Per-domain API modules (thin wrappers over ApiClient)
│   ├── components/
│   │   ├── auth/       # Auth0 wiring: AuthInitializer, ProtectedRoute, UserMenu
│   │   ├── layout/     # AppShell, Sidebar, Header, PageHeader
│   │   ├── shared/     # ErrorBoundary, ConfirmationDialog, EmptyState, etc.
│   │   └── ui/         # Radix-based design system components
│   ├── config/         # env.config.ts, auth.config.ts, api.config.ts, demo.config.ts
│   ├── features/       # Feature slices — each owns pages, components, schemas
│   ├── hooks/          # Custom hooks (use-auth.ts)
│   ├── lib/            # axios.ts, query-client.ts, utils.ts
│   ├── pages/          # Top-level pages not owned by a feature (login-page.tsx)
│   ├── queries/        # TanStack Query hooks per domain
│   ├── routes/         # Router definition (index.tsx)
│   ├── store/          # Zustand: ui-store.ts, user-store.ts
│   ├── styles/         # Global CSS
│   ├── types/          # TypeScript interfaces, all re-exported from types/index.ts
│   ├── App.tsx         # Root component — providers wiring
│   └── main.tsx        # Vite entry point
├── e2e/                # Playwright E2E tests
├── .env.local          # Local secrets (NEVER commit)
├── vite.config.ts
└── playwright.config.ts
```

## Key conventions
- **Feature slices**: `src/features/<domain>/` contains the page, sub-components, and Zod schemas. Never import cross-feature components directly — use shared components.
- **No direct Axios calls in components**: all HTTP calls go through `src/queries/` hooks → `src/api/` modules → `ApiClient`.
- **Type imports**: all types exported from `src/types/index.ts` — import from `@/types`, not from individual type files.
- **Path alias**: `@/` resolves to `src/`. Always use it for imports.
- **Form pattern**: Zod schema in `schemas/`, `useForm({ resolver: zodResolver(schema) })`, submit via `useMutation`, toast on success/error.
- **Mutations**: always invalidate the relevant query key on success.
- **Auth**: `VITE_DEMO_MODE=true` / `npm run dev:demo` bypasses Auth0 — useful for testing without credentials.
- **Naming**: camelCase files (`booking-form.tsx`); PascalCase component exports; `use-` prefix for hooks.
- **Tests**: unit tests colocated in `__tests__/` subdirectories; E2E in `e2e/`.

## Where to find things

| Thing | Where |
|---|---|
| Route definitions | `src/routes/index.tsx` |
| App providers (Auth0, QueryClient) | `src/App.tsx` |
| Auth0 + JWT wiring | `src/components/auth/auth-initializer.tsx` + `src/lib/axios.ts` |
| Protected route guard | `src/components/auth/protected-route.tsx` |
| Axios instance (JWT interceptor) | `src/lib/axios.ts` |
| ApiClient (get/post/put/delete) | `src/api/client.ts` |
| API modules | `src/api/*.api.ts` |
| TanStack Query hooks | `src/queries/use-*.ts` |
| TypeScript types | `src/types/*.types.ts` (all re-exported from `src/types/index.ts`) |
| Zustand stores | `src/store/ui-store.ts`, `src/store/user-store.ts` |
| Environment variables | `src/config/env.config.ts` (reads `VITE_*`) |
| Demo mode config | `src/config/demo.config.ts` |
| Booking calendar | `src/features/bookings/calendar-page.tsx` + `components/booking-calendar.tsx` |
| Pricing AI dashboard | `src/features/pricing/pricing-dashboard-page.tsx` |
| OTA sync management | `src/features/ota/ota-page.tsx` |
| Shared UI components | `src/components/ui/` (Radix-based) |
| Unit tests | `src/**/__tests__/` |
| E2E tests | `e2e/` |

## Non-obvious rules / gotchas
- **API base URL default**: `VITE_API_BASE_URL` defaults to `http://localhost:3000/api` — note port **3000**, not 5001 (the .NET backend default). Set `VITE_API_BASE_URL=https://localhost:5001/api` for local backend.
- **BookingStatus PascalCase**: enum values are `'Pending'`, `'Confirmed'`, `'CheckedIn'`, `'CheckedOut'`, `'Cancelled'` — not snake_case or lowercase.
- **Property field names**: `nightlyRate` (not `pricePerNight`), `postalCode` (not `zipCode`), `photoUrls` (not `images`). These differ from what you might expect from a generic rental app.
- **Unwrap helper**: `ApiClient.unwrap()` handles both bare `T` and enveloped `{ data: T }` responses. Don't unwrap manually in API modules.
- **Duplicate pricing files**: `src/api/pricingAdapter.ts` and `src/api/pricing-adapter.api.ts` both exist; `pricing-adapter.api.ts` (kebab-case `.api.ts` pattern) is the canonical one.
- **Debug logs in axios.ts**: the interceptor has `console.log('[Auth Debug] ...')` statements — these are intentional for auth troubleshooting, not a bug.
- **Token getter registration**: `AuthInitializer` must render before any authenticated API call is made. If it hasn't rendered yet, `getAccessToken` is null and requests are sent without auth.
- **Demo mode**: `VITE_DEMO_MODE=true` skips Auth0 entirely. All routes become accessible. The demo user is `demo@casazen.com`.
- **`enabled: !!id`**: query hooks that take an entity ID use `enabled: !!id` — the query won't fire until the ID is defined. This prevents spurious 404s on create pages.

## External integrations
- **Auth0**: authentication provider. Config at `src/config/auth.config.ts`; env vars `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE`.
- **CasaZen Backend API**: the only external service. Base URL from `VITE_API_BASE_URL`.
- **Sonner**: toast library — renders at the root in `App.tsx`. Mutations call `toast.success()` / `toast.error()` directly in `onSuccess`/`onError` callbacks.
