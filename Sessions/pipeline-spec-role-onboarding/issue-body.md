## User Story

Come nuovo utente che ha appena creato il mio account Auth0, voglio:
1. Essere guidato a scegliere se gestisco affitti brevi, locazioni di lungo periodo, o entrambi
2. Vedere immediatamente la sezione dell'app corretta in base alla mia scelta
3. Poter modificare la mia scelta in futuro dalle impostazioni profilo

Come sistema, voglio assegnare i ruoli Auth0 corretti in base alla scelta dell'utente così che il JWT successivo porti già i claim corretti e la UI si adatti senza ricarica manuale.

**Design input**: `Sessions/specs/spec-role-onboarding.md`  
**Depends on**: #11 (Auth0 Management API — done)

## Acceptance Criteria

### Onboarding Flow
- **AC1**: Nuovo utente autenticato con 0 ruoli nel JWT → redirect automatico a `/onboarding`
- **AC2**: `/onboarding` mostra 3 opzioni come card visuali (Affitti brevi, Locazioni lungo periodo, Entrambi)
- **AC3**: Click → `POST /api/users/onboarding` → redirect home per ruolo (`/app/short-rent`, `/app/long-rent/leases`, dual → short-rent con switcher)
- **AC4**: Dopo success API → refresh JWT (`getAccessTokenSilently` cache off) e aggiornare stato utente
- **AC5**: Utente già con ruoli → `/onboarding` redirect home (no loop)
- **AC6**: Da profile → sezione Tipo operatore → Modifica → `/onboarding`

### Backend
- **AC7**: `POST /api/users/onboarding` — body `{ rentalType }`, assegna ruoli Auth0, aggiorna DB, `200 { rolesAssigned }`
- **AC8**: `PUT /api/users/onboarding` — idempotente, rimuove ruoli precedenti poi assegna nuovi
- **AC9**: Admin bypass onboarding guard
- **AC10**: Validazione `rentalType` enum — 400 se sconosciuto

### Frontend
- **AC11**: `OnboardingPage` standalone full-page a `/onboarding`
- **AC12**: `OnboardingGuard` tra ProtectedRoute e WorkspaceProvider
- **AC13**: Route `/onboarding` fuori da OnboardingGuard
- **AC14**: Sequenza mutation → token refresh → navigate
- **AC15**: Errore API → toast + retry
- **AC16**: Demo `VITE_DEMO_PROFILE=onboarding`
- **AC17**: Profile sezione Tipo operatore con link Modifica

## Technical Notes

- Backend: `UsersController` onboarding endpoints, `User.RentalType` enum + migration, extend `Auth0ManagementService` multi-role
- Frontend: feature slice `onboarding/`, guard, router update, `users.api.ts`, E2E Playwright
- Mapping: ShortTerm→PropertyOwner, LongTerm→LongTermLandlord, Both→both roles
- No migration impact on OTA/tourist tax; GDPR: user self-service role choice only
