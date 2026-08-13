# Spec — Role-Based Onboarding (Nuova Issue da Creare)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Quando un nuovo utente accede per la prima volta (dopo Auth0 login), deve scegliere
che tipo di operatore è: proprietario di affitti brevi (short-term), locatore di lungo
periodo (long-term), o entrambi. La scelta determina il ruolo Auth0 assegnato e l'app
shell mostrata al primo accesso.

L'attuale UI layer separation (Issue #182, COMPLETATA) presuppone che i ruoli siano già
assegnati in Auth0. Questa spec colma il gap: l'onboarding è il momento in cui l'utente
sceglie e il sistema assegna i ruoli automaticamente.

**Nuova issue richiesta** — vedere sezione "New GitHub Issue" in fondo.  
Stage di ingresso: **Stage 01 Planning** (creare l'issue prima di procedere)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Come nuovo utente che ha appena creato il mio account Auth0, voglio:

1. Essere guidato a scegliere se gestisco affitti brevi, locazioni di lungo periodo, o entrambi
2. Vedere immediatamente la sezione dell'app corretta in base alla mia scelta
3. Poter modificare la mia scelta in futuro dalle impostazioni profilo

Come sistema, voglio assegnare i ruoli Auth0 corretti in base alla scelta dell'utente
così che il JWT successivo porti già i claim corretti e la UI si adatti senza ricarica manuale.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Onboarding Flow

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Nuovo utente autenticato con 0 ruoli nel JWT → redirect automatico a `/onboarding` prima del dashboard
  - Guard: `isAuthenticated && user.roles.length === 0` → `/onboarding`
  - Utenti con almeno 1 ruolo → bypass onboarding

- **AC2**: `/onboarding` mostra 3 opzioni come card visuali con icone e descrizione:
  - **"Affitti brevi"** — icona casa, descrizione "Gestisci prenotazioni short-stay su Airbnb, Booking.com e altri"
  - **"Locazioni di lungo periodo"** — icona chiave, descrizione "Gestisci contratti di locazione, registrazione RLI, cedolare secca"
  - **"Entrambi"** — icona doppia, descrizione "Accedi a entrambe le sezioni con switcher rapido"

- **AC3**: Click su opzione → `POST /api/users/onboarding` con `{ rentalType: "ShortTerm" | "LongTerm" | "Both" }` → redirect:
  - `ShortTerm` → assegna `PropertyOwner` → redirect `/`
  - `LongTerm` → assegna `LongTermLandlord` → redirect `/leases`
  - `Both` → assegna entrambi → redirect `/` con `LayerSwitcher` visibile

- **AC4**: Dopo risposta success da API → chiamare `getAccessTokenSilently({ ignoreCache: true })` per ottenere JWT con nuovi ruoli → ricaricare `useUserStore`

- **AC5**: Utente già con ruoli → `/onboarding` redirect a home appropriata (no loop)

- **AC6**: Da `/profile` → sezione "Tipo di operatore" → pulsante "Modifica" → stessa pagina `/onboarding` riutilizzabile come modal o navigazione

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC7**: `POST /api/users/onboarding` (autenticato, qualsiasi ruolo incluso nessuno):
  - Body: `{ rentalType: "ShortTerm" | "LongTerm" | "Both" }`
  - Assegna ruoli in Auth0 Management API
  - Aggiorna `User.RentalType` (o `User.Role`) nel DB
  - Risposta: `200 OK` con `{ rolesAssigned: ["PropertyOwner"] }`

- **AC8**: `PUT /api/users/onboarding` — stessa semantica di POST (idempotente — aggiorna ruoli se utente ha già completato onboarding)
  - Prima rimuovere ruoli precedenti da Auth0, poi assegnare nuovi

- **AC9**: Admin (ruolo `Admin` nel JWT) → bypass onboarding, non soggetto al guard

- **AC10**: Body request validato: `rentalType` deve essere uno dei 3 valori enum; 400 se valore sconosciuto

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: `OnboardingPage` a `/onboarding` — **non** wrapped in AppShell, standalone full-page
  - Layout: centrato, branding CasaZen, titolo "Come vuoi usare CasaZen?"
  - 3 card con hover effect + icona + titolo + descrizione + CTA "Scegli"
  - Loading state durante chiamata API + navigazione

- **AC12**: `OnboardingGuard` — wrapper aggiunto al router tra `<ProtectedRoute>` e `<AppLayerProvider>`:
  - `isAuthenticated && roles.length === 0` → `<Navigate to="/onboarding" replace />`
  - Altrimenti `<Outlet />`

- **AC13**: Route `/onboarding` posizionata **fuori** dalla protezione di `OnboardingGuard` nel router (evitare redirect loop)

- **AC14**: Sequenza post-scelta:
  1. `useMutation` → `POST /api/users/onboarding`
  2. On success: `await getAccessTokenSilently({ ignoreCache: true })`
  3. `setUser(nuovoUserConRuoli)` in `useUserStore`
  4. `navigate(homePerRuolo)`

- **AC15**: Se chiamata API fallisce → toast errore "Errore durante la configurazione del profilo. Riprova." + retry button

- **AC16**: Demo mode — `VITE_DEMO_PROFILE=onboarding` mostra la pagina onboarding bypassando Auth0;
  click su opzione simula chiamata API e naviga alla home corretta

### Profile Page (AC6 dettaglio)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC17**: `/profile` include sezione "Tipo di operatore" con il tipo attuale (es. "Affitti brevi")
  e un link "Modifica tipo" → `/onboarding` (che in questo caso funge da settings, non da first-run)

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `role-onboarding`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Nuovo utente autenticato con 0 ruoli nel JWT → redirect automatico a `/onboarding` prima del dashboard | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `/onboarding` mostra 3 opzioni come card visuali con icone e descrizione: | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | Click su opzione → `POST /api/users/onboarding` con `{ rentalType: "ShortTerm" / "LongTerm" / "Both" }` → redirect: | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | Dopo risposta success da API → chiamare `getAccessTokenSilently({ ignoreCache: true })` per ottenere JWT con nuovi ruoli → ricaricare `us... | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | Utente già con ruoli → `/onboarding` redirect a home appropriata (no loop) | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | Da `/profile` → sezione "Tipo di operatore" → pulsante "Modifica" → stessa pagina `/onboarding` riutilizzabile come modal o navigazione | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | `POST /api/users/onboarding` (autenticato, qualsiasi ruolo incluso nessuno): | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | `PUT /api/users/onboarding` — stessa semantica di POST (idempotente — aggiorna ruoli se utente ha già completato onboarding) | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | Admin (ruolo `Admin` nel JWT) → bypass onboarding, non soggetto al guard | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | Body request validato: `rentalType` deve essere uno dei 3 valori enum; 400 se valore sconosciuto | Outcome not met; wrong status; silent no-op |
| AC11 | L2 + L3 | `OnboardingPage` a `/onboarding` — **non** wrapped in AppShell, standalone full-page | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | `OnboardingGuard` — wrapper aggiunto al router tra `<ProtectedRoute>` e `<AppLayerProvider>`: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | Route `/onboarding` posizionata **fuori** dalla protezione di `OnboardingGuard` nel router (evitare redirect loop) | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | Sequenza post-scelta: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | Se chiamata API fallisce → toast errore "Errore durante la configurazione del profilo. Riprova." + retry button | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | Demo mode — `VITE_DEMO_PROFILE=onboarding` mostra la pagina onboarding bypassando Auth0; | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L1 | `/profile` include sezione "Tipo di operatore" con il tipo attuale (es. "Affitti brevi") | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `Casazen.Web/Controllers/UsersController.cs` | Aggiungere `POST /api/users/onboarding` (dipende da spec-admin-backend) |
| `Casazen.Core/Entities/User.cs` | Aggiungere campo `RentalType` (enum: `ShortTerm`, `LongTerm`, `Both`) se non presente |
| `Casazen.Infrastructure/Services/Auth0ManagementService.cs` | Riutilizzare da spec-admin-backend — `AssignRolesAsync`, `RemoveRolesAsync` |

Mapping `rentalType` → Auth0 roles:
```csharp
var roleMapping = rentalType switch {
    RentalType.ShortTerm => new[] { "PropertyOwner" },
    RentalType.LongTerm  => new[] { "LongTermLandlord" },
    RentalType.Both      => new[] { "PropertyOwner", "LongTermLandlord" },
    _ => throw new ArgumentException()
};
```

Auth0 role assignment è idempotente — assegnare un ruolo già presente non restituisce errore.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `src/features/onboarding/onboarding-page.tsx` | Nuova feature slice — standalone page |
| `src/features/onboarding/components/rental-type-card.tsx` | Card selezionabile per tipo operatore |
| `src/components/auth/onboarding-guard.tsx` | Guard da aggiungere al router |
| `src/queries/use-users.ts` | `useCompleteOnboarding()` mutation |
| `src/api/users.api.ts` | `postOnboarding(rentalType)` → `POST /api/users/onboarding` |
| `src/routes/index.tsx` | Aggiungere `/onboarding` route + `OnboardingGuard` wrapper |
| `src/types/user.types.ts` | `RentalType`, `OnboardingRequest`, `OnboardingResponse` |

**Router structure dopo la modifica**:
```tsx
// Ordine critico: OnboardingGuard DOPO ProtectedRoute ma PRIMA di AppLayerProvider
<Route element={<ProtectedRoute />}>
  <Route path="/onboarding" element={<OnboardingPage />} />  {/* fuori dal guard */}
  <Route element={<OnboardingGuard />}>                       {/* guard qui */}
    <Route element={<AppLayerProvider><Outlet /></AppLayerProvider>}>
      {/* ... tutte le route esistenti ... */}
    </Route>
  </Route>
</Route>
```

### LayerSwitcher Interaction (nessuna modifica richiesta)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Dopo onboarding con "Entrambi":
- `AppLayerProvider` riceve i nuovi ruoli dal `useUserStore` aggiornato
- `isDualRole(user)` diventa `true` → `LayerSwitcher` appare automaticamente in sidebar
- Nessuna modifica a `src/components/layout/layer-switcher.tsx`

---

## New GitHub Issue Required

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Questa spec richiede la creazione di una nuova issue in Stage 01 prima di procedere con design e sviluppo.

**Issue proposta**:
- **Title**: `feat(auth): role-based onboarding flow for new users`
- **Labels**: `feature`, `auth`, `frontend`, `backend`, `priority:high`
- **Body**: collegare questa spec come design input; riferimento a #11 (Admin Backend — condivide Auth0 Management API)
- **Acceptance criteria**: i 17 AC di questa spec
- **Blocca**: nessuna issue esistente
- **Dipende da**: #11 (spec-admin-backend) — condivide `Auth0ManagementService`

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Dipende da**: `spec-admin-backend.md` — condivide `Auth0ManagementService.cs` e il client Auth0 Management API
  - **Implementare spec-admin-backend prima** per non duplicare il client
- **Richiede**: Auth0 Management API M2M token in Railway env vars (già richiesto da spec-admin-backend)
- **Estende**: Issue #182 (DONE) — layer separation senza modificarla
- **Non tocca**: `LayerSwitcher`, `AppLayerProvider`, `ShortStayLayerGuard` (invariati)
- **Non richiede** migration DB se `User.RentalType` è aggiunto (solo nullable enum — migration leggera)

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
