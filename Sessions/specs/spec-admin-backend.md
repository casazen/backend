# Spec — Admin Backend & Admin Panel (Issue #11)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Implementare il layer amministrativo completo: backend con user management CRUD,
statistiche di piattaforma, monitoring CIN compliance e supervisione job Hangfire;
frontend con admin shell separata e pannello di controllo.

L'infrastruttura Auth0 (`AdminOnly` policy) e l'entity `User` esistono già.
Questa spec aggiunge il layer applicativo mancante (controller, service, repository)
e la UI dedicata.

Issue di riferimento: **#11** (OPEN — user management + RBAC non implementati)  
Stage di ingresso: **Stage 02 Design** (issue approvata, spec tecnica da completare)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Come Admin, voglio:

1. Vedere tutti gli utenti registrati e poter modificarne i ruoli o disattivarli
2. Avere una dashboard con KPI di piattaforma (proprietà, prenotazioni, revenue)
3. Monitorare la compliance CIN: quante proprietà hanno CIN valido, invalido, mancante
4. Supervisionare lo stato dei job Hangfire e triggerare manualmente se necessario

Come utente autenticato (owner o landlord), voglio poter aggiornare il mio profilo e
vedere i miei dati tramite `GET /api/users/me`.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — User Management

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `GET /api/users` (policy `AdminOnly`) — lista paginata con filtri opzionali: `role`, `isActive` (bool), `search` (su email/nome)
  - Response: `{ items: UserSummaryDto[], totalCount, page, pageSize }`
  - `UserSummaryDto`: `{ id, email, firstName, lastName, role, isActive, createdAt }`
  - Default: `page=1`, `pageSize=20`, ordinati per `createdAt DESC`

- **AC2**: `GET /api/users/{id}` (policy `AdminOnly`) — dettaglio utente (200 o 404)

- **AC3**: `GET /api/users/me` (autenticato, qualsiasi ruolo) — profilo dell'utente corrente
  - Crea il record `User` se è il primo login (upsert su `sub` claim)

- **AC4**: `PUT /api/users/me` (autenticato) — aggiorna `firstName`, `lastName`, `phone`
  - Validazione: phone opzionale, max 20 caratteri

- **AC5**: `PUT /api/users/{id}/role` (policy `AdminOnly`) — cambia ruolo utente
  - Body: `{ role: "PropertyOwner" | "LongTermLandlord" | "Admin" | "PropertyManager" }`
  - Effetti: (1) aggiorna `User.Role` nel DB; (2) chiama Auth0 Management API per sincronizzare il ruolo nel JWT
  - Log strutturato: `userId`, `targetUserId`, `oldRole`, `newRole`

- **AC6**: `DELETE /api/users/{id}` (policy `AdminOnly`) — soft delete (`isActive = false`)
  - 204 on success; 404 se utente non trovato
  - 400 se l'Admin prova a eliminare se stesso

- **AC7**: Tutti gli endpoint `/api/users` (escluso `/me`) → 403 se il caller non è Admin

### Backend — Admin Stats & Monitoring

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: `GET /api/admin/stats` (policy `AdminOnly`) — KPI piattaforma:
  ```json
  {
    "totalProperties": 42,
    "activeProperties": 38,
    "totalBookings": 210,
    "bookingsThisMonth": 15,
    "upcomingCheckIns": 3,
    "totalRevenue": 48500.00,
    "cinCompliance": { "valid": 30, "missing": 5, "invalid": 3, "total": 38 },
    "otaSyncHealth": { "synced": 28, "failed": 2, "neverSynced": 8 }
  }
  ```

- **AC9**: `GET /api/admin/cin-compliance` (policy `AdminOnly`) — lista proprietà con CIN status per audit
  - Response: `{ items: [{ propertyId, propertyName, ownerId, cinCode, cinStatus, city }], totalCount }`
  - Filtro opzionale: `?cinStatus=Invalid|Missing|Valid`

- **AC10**: `GET /api/admin/jobs` (policy `AdminOnly`) — stato job Hangfire ricorrenti
  - Response: `[{ jobName, cronExpression, lastRun, lastStatus, nextRun }]`
  - Include: `OtaSyncJob`, `BookingPullJob`, `DynamicPricingJob`, `GdprDataRetentionJob`, `AlloggiatiWebReportJob`

### Frontend — Admin Shell

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: Aggiungere `Admin = 'Admin'` in `src/lib/auth-roles.ts`

- **AC12**: Route `/admin` e sotto-route protette da `<ProtectedRoute role="Admin">` nel router

- **AC13**: `AdminAppShell` — shell separata con `AdminSidebar`:
  - Nav items: Dashboard (`/admin`), Utenti (`/admin/users`), CIN Compliance (`/admin/cin`), Jobs (`/admin/jobs`)
  - Header: nome utente + badge "Admin"

- **AC14**: `/admin` — dashboard KPI: 6 metric card (`totalProperties`, `activeProperties`, `totalRevenue`, `cinCompliance.valid%`, `otaSyncHealth.failed`, `upcomingCheckIns`)

- **AC15**: `/admin/users` — tabella paginata con:
  - Colonne: email, nome, ruolo (badge), stato (attivo/inattivo), data creazione
  - Azioni per riga: cambio ruolo (dropdown), disattiva (confirm dialog)
  - Filtri: ricerca testo, dropdown ruolo, toggle attivi/inattivi

- **AC16**: `/admin/cin` — tabella con filtro per CIN status; badge colorato (verde/rosso/giallo)

- **AC17**: `/admin/jobs` — tabella job Hangfire con last run, stato, next run; colore rosso se lastStatus = "Failed"

- **AC18**: Un utente `Admin` che ha anche `PropertyOwner` può navigare alla short-stay shell tramite il `LayerSwitcher` (la logica è già in `AppLayerProvider` — l'Admin con PropertyOwner diventa dual-role)

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



1. Enter the primary route for `admin-backend`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `GET /api/users` (policy `AdminOnly`) — lista paginata con filtri opzionali: `role`, `isActive` (bool), `search` (su email/nome) | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GET /api/users/{id}` (policy `AdminOnly`) — dettaglio utente (200 o 404) | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `GET /api/users/me` (autenticato, qualsiasi ruolo) — profilo dell'utente corrente | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `PUT /api/users/me` (autenticato) — aggiorna `firstName`, `lastName`, `phone` | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `PUT /api/users/{id}/role` (policy `AdminOnly`) — cambia ruolo utente | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | `DELETE /api/users/{id}` (policy `AdminOnly`) — soft delete (`isActive = false`) | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | Tutti gli endpoint `/api/users` (escluso `/me`) → 403 se il caller non è Admin | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | `GET /api/admin/stats` (policy `AdminOnly`) — KPI piattaforma: | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | `GET /api/admin/cin-compliance` (policy `AdminOnly`) — lista proprietà con CIN status per audit | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | `GET /api/admin/jobs` (policy `AdminOnly`) — stato job Hangfire ricorrenti | Outcome not met; wrong status; silent no-op |
| AC11 | L2 + L3 | Aggiungere `Admin = 'Admin'` in `src/lib/auth-roles.ts` | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Route `/admin` e sotto-route protette da `<ProtectedRoute role="Admin">` nel router | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | `AdminAppShell` — shell separata con `AdminSidebar`: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | `/admin` — dashboard KPI: 6 metric card (`totalProperties`, `activeProperties`, `totalRevenue`, `cinCompliance.valid%`, `otaSyncHealth.fa... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | `/admin/users` — tabella paginata con: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | `/admin/cin` — tabella con filtro per CIN status; badge colorato (verde/rosso/giallo) | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L1 + L2 + L3 | `/admin/jobs` — tabella job Hangfire con last run, stato, next run; colore rosso se lastStatus = "Failed" | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC18 | L2 + L3 | Un utente `Admin` che ha anche `PropertyOwner` può navigare alla short-stay shell tramite il `LayerSwitcher` (la logica è già in `AppLaye... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — File da creare/modificare

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `Casazen.Core/Repositories/IUserRepository.cs` | Creare — metodi: `GetByIdAsync`, `GetBySubAsync`, `GetAllAsync(filter, page, pageSize)`, `UpsertAsync`, `SoftDeleteAsync` |
| `Casazen.Infrastructure/Repositories/UserRepository.cs` | Implementazione EF Core |
| `Casazen.Core/Services/IUserService.cs` | Creare interfaccia |
| `Casazen.Infrastructure/Services/UserService.cs` | Implementazione — delega a repository + Auth0 Management API |
| `Casazen.Core/Services/IAdminService.cs` | Creare interfaccia — `GetStatsAsync`, `GetCinComplianceAsync`, `GetJobsStatusAsync` |
| `Casazen.Infrastructure/Services/AdminService.cs` | Implementazione — aggrega query multiple |
| `Casazen.Web/Controllers/UsersController.cs` | Creare — CRUD + `/me` endpoints |
| `Casazen.Web/Controllers/AdminController.cs` | Creare — stats, cin-compliance, jobs |
| `Casazen.Infrastructure/Services/Auth0ManagementService.cs` | Creare — wrapper `Auth0.ManagementApi` NuGet |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Registrare nuovi servizi + Auth0 Management API client |
| `Casazen.Core/Entities/User.cs` | Aggiungere `IsActive` (bool, default true) se assente |
| `Casazen.Infrastructure/Migrations/` | Migration `AddUserIsActive` se campo aggiunto |

**Auth0 Management API setup**:
```csharp
// NuGet: Auth0.ManagementApi
// Configurazione Railway env vars:
// Auth0__ManagementApiToken = <M2M token per Management API>
// Auth0__ManagementApiDomain = dev-xxx.us.auth0.com
services.AddScoped<ManagementApiClient>(sp =>
    new ManagementApiClient(config["Auth0:ManagementApiToken"],
        new Uri($"https://{config["Auth0:ManagementApiDomain"]}/api/v2")));
```

**Role assignment in Auth0**:
```csharp
// PUT /api/users/{id}/role → UserService.ChangeRoleAsync
// 1. await _userRepository.UpdateRoleAsync(userId, newRole)
// 2. await _managementClient.Users.AssignRolesAsync(auth0UserId, new AssignRolesRequest { Roles = [roleId] })
// 3. await _managementClient.Users.RemoveRolesAsync(auth0UserId, rolesIdToRemove)
```

### Frontend — File da creare

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `src/features/admin/admin-dashboard-page.tsx` | Nuova pagina — KPI cards |
| `src/features/admin/admin-users-page.tsx` | Nuova pagina — tabella utenti |
| `src/features/admin/admin-cin-page.tsx` | Nuova pagina — tabella CIN compliance |
| `src/features/admin/admin-jobs-page.tsx` | Nuova pagina — tabella Hangfire jobs |
| `src/features/admin/components/user-management-table.tsx` | Componente tabella |
| `src/features/admin/components/cin-compliance-table.tsx` | Componente tabella |
| `src/features/admin/components/admin-kpi-card.tsx` | Componente card KPI |
| `src/components/layout/admin-app-shell.tsx` | Shell admin |
| `src/components/layout/admin-sidebar.tsx` | Sidebar admin |
| `src/queries/use-admin.ts` | `useAdminStats`, `useCinCompliance`, `useAdminJobs` |
| `src/queries/use-users.ts` | `useUsers`, `useUser`, `useUpdateUserRole`, `useDeactivateUser`, `useCurrentUser`, `useUpdateProfile` |
| `src/api/admin.api.ts` | Chiamate a `/api/admin/*` |
| `src/api/users.api.ts` | Chiamate a `/api/users/*` |
| `src/types/admin.types.ts` | `UserSummaryDto`, `AdminStatsDto`, `CinComplianceItemDto`, `JobStatusDto` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **GDPR**: endpoint `/api/users` non espone `DocumentNumber`, `DocumentType`, dati Alloggiati o payment info
- **Audit trail**: ogni cambio ruolo loggato via `ILogger<UserService>` con `userId`, `targetUserId`, `oldRole`, `newRole`; non persisto in tabella separata (fuori scope)
- **IDOR**: solo Admin accede a `/api/users/{id}` (non owner); `/api/users/me` usa sempre il `sub` dal JWT

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Richiede**: Issue #9 (User ID type mismatch) completato prima — il `User.Id` deve essere stabile
- **Richiede**: Auth0 Management API M2M token configurato in Railway env vars (`Auth0__ManagementApiToken`)
- **Estende**: policy `AdminOnly` già definita in `Casazen.Web/Extensions/ServiceCollectionExtensions.cs`
- **Estende**: `User` entity in `Casazen.Core/Entities/User.cs`
- **Condivide**: Auth0 Management API client con `spec-role-onboarding.md` — implementare prima questa spec
- **Non tocca**: lease entities, OTA adapters, pricing adapter

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
