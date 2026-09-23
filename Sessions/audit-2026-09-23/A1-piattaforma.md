## A1 — Piattaforma, accesso, onboarding, billing, admin

Audit statico (solo lettura), eseguito il 2026-09-23 su `backend` (HEAD `4cbaeaa`), `frontend` (HEAD `0b91e3c`) e `mobile`. Nessun build o test eseguito. Le issue GitHub #271, #273 e #274 risultano **aperte**. #230 è stata riaperta il 2026-06-19.
Percorsi abbreviati: `BE/` = `/home/user/backend/`, `FE/` = `/home/user/frontend/src/`, `MOB/` = `/home/user/mobile/`.

Premessa importante, perché spiega molti bug a cascata:
- **L'access token Auth0 non contiene `email`, `given_name` né `family_name`.** L'Action documentata aggiunge solo i ruoli (`BE/docs/AUTH0_SETUP.md:136-150`). Il team lo sa già: il commento in `BE/Casazen.Web/Infrastructure/SupplierOrgContextResolver.cs:42-54` dice che il JWT "lacks the email claim". Di conseguenza tutto il codice che legge email e nome dal JWT (`UsersController.cs:77-85`, `OrgContextResolver.cs:60-77`) crea utenti e org con email vuota e nome "La mia organizzazione".
- **I ruoli Auth0 vengono scritti con un token M2M statico** (`Auth0:ManagementApiToken`), che scade. Tutti gli errori vengono inghiottiti (`BE/Casazen.Infrastructure/Services/Auth0ManagementService.cs:45-101,105-175`). Il backend compensa solo in parte con il fallback "ruolo DB" (`ContextAuthorizationService.cs:50-59`).
- **Ogni utente nuovo nasce con `Role = PropertyOwner` nel DB** (`UserService.cs:133`). Il fallback lo trasforma in permessi `short-rent` completi, anche senza onboarding e senza consensi.

---

### 1. Copertura del piano

Legenda stati: DONE / PARTIAL / STUB / MISSING / BROKEN.

#### 1.1 spec-tenant-boundary (US-004, #202 — registry: "shipped")

| ID | Punto pianificato | Stato | Evidenza (file:riga) | Note |
|---|---|---|---|---|
| TB-AC1 | Entity `Org` con slug unico | DONE | `BE/Casazen.Core/Entities/Org.cs:12-92`; snapshot `AppDbContextModelSnapshot.cs:851-968` | Ha anche campi billing, Connect e dominio. |
| TB-AC2 | `OrgId` FK Restrict su Property/Booking/Lease/Payment | DONE | `BE/Casazen.Infrastructure/Data/AppDbContext.cs:424-436` | |
| TB-AC3-5 | Migrazione in 3 step (nullable → backfill → NOT NULL) | DONE (con nota) | `Migrations/20260609100314_AddOrgIdNullable.cs`, `20260609100413_BackfillDefaultOrgs.cs:18-114`, `20260609101050_MakeOrgIdRequired.cs:14-81` | Il requisito dei "3 deploy separati" (AC10b) non è garantito: `Program.cs:280-285` esegue `Database.Migrate()` all'avvio e applica tutto in un solo deploy. I test controllano solo le stringhe SQL (`Casazen.Tests/Unit/Infrastructure/MigrationSqlTests.cs:141-185`). |
| TB-AC6 | Disciplina dello snapshot | DONE (non verificabile a runtime) | snapshot allineato alle entity campionate (Org) | |
| TB-AC7 | `ITenantContext` + filtro globale EF | PARTIAL | `AppDbContext.cs:463-469`; `Casazen.Web/Infrastructure/TenantContext.cs:26-53` | Il filtro copre 10 tabelle. `Guest` **non ha `OrgId`** (viola RF1). `ConsentRecord`, `GuestCheckInSession`, `DeviceRegistration`, `PlatformInvoice`, `ServiceRequest` e `SupplierProfile` hanno `OrgId` ma nessun filtro. `TenantContext` mette in cache `null` prima del provisioning (bug A1-20). |
| TB-AC8 | Entitlement e limite proprietà | PARTIAL | `PropertiesController.cs:113-140`; `EntitlementService.cs:19-66` | Il 403 `plan_limit_reached` funziona. Però `ReservePropertySlotAsync` non riserva nulla (A1-21) e il piano si alza gratis (A1-03). |
| TB-AC9 | `User.OrgId` + `/api/users/me` con org | DONE | `UsersController.cs:69-89,278-301` | |
| TB-AC10 | Regressione dopo il backfill | DONE (plausibile) | `Casazen.Tests/Integration/TenantBoundaryIntegrationTests.cs:91-128` | |
| TB-AC10b | Pre-flight NULL + down-migration | PARTIAL | `MakeOrgIdRequired.cs:14-27`; `BackfillDefaultOrgs.cs:118-135` (Down è un no-op documentato) | Nessun test eseguito contro un DB reale. |
| TB-AC11 | FE: org + badge piano nell'header | DONE (con difetti) | `FE/components/org/org-badge.tsx:11-30`; `FE/components/layout/header.tsx:32` | Il badge punta sempre a `/app/short-rent/settings/plan`, anche dalla shell long-rent e admin (A1-36). Tier mostrato non tradotto. |
| TB-AC11b | Gestione piano MVP (onboarding, `PUT /orgs/me/plan`, `PATCH /admin/orgs/{id}/plan`) | PARTIAL / insicuro | `OrgsController.cs:67-108`; `AdminController.cs:107-130`; `FE/features/settings/plan-settings-page.tsx:11-72`; `change-org-plan-dialog.tsx` | Funziona, ma chiunque si porta a Scale gratis (#274 aperta). Il PATCH admin ignora le subscription Stripe. |
| TB-AC12 | Messaggio IT "limite piano" + link | DONE | `FE/lib/entitlement-error.ts:20-25`; `FE/features/properties/property-create-page.tsx:21-26` | |

#### 1.2 spec-role-onboarding (#198 — registry: "shipped")

| ID | Punto | Stato | Evidenza | Note |
|---|---|---|---|---|
| RO-AC1 | 0 ruoli → `/onboarding` | DONE (logica cambiata) | `FE/lib/onboarding.ts:47-68`; `FE/components/auth/onboarding-guard.tsx:9-28` | Ora la priorità è `orgId` mancante, non i ruoli. Vedi RO-AC9. |
| RO-AC2 | 3 card | DONE | `FE/features/onboarding/onboarding-page.tsx:141-153`; `rental-type-card.tsx` | |
| RO-AC3 | POST + redirect per tipo | PARTIAL | `onboarding-page.tsx:85-92`; `UsersController.cs:92-94,167-230` | L'assegnazione dei ruoli Auth0 è "best effort" e silenziosa (A1-02). Con "Both" il DB salva solo `roles[0]` (`UserService.cs:225`). |
| RO-AC4 | Refresh del token dopo l'onboarding | DONE | `FE/contexts/auth-bridge.tsx:116-119` (`cacheMode:'off'`); `onboarding-page.tsx:91-92` (reload completo) | Se il refresh fallisce, compare il toast di errore anche se il backend ha completato. |
| RO-AC5 | Utente con ruoli → redirect alla home | PARTIAL | `onboarding-page.tsx:63-65` | Solo se l'utente ha già un'org. Con ruoli ma senza org resta bloccato (A1-01). |
| RO-AC6 / AC17 | Profilo "Tipo di operatore" + Modifica | PARTIAL | `FE/features/profile/components/operator-type-section.tsx:9-31`; `onboarding-page.tsx:54-56` | In modalità edit si salta direttamente allo step piano. Il piano scelto viene ignorato (A1-15). Se il JWT non ha ruoli, l'edit usa POST e fallisce (A1-02). Etichette hardcoded (`lib/onboarding.ts:5-9`). |
| RO-AC7 | `POST /api/users/onboarding` assegna i ruoli Auth0 | BROKEN in produzione | `Auth0ManagementService.cs:105-175`; `UserService.cs:244` | Token M2M statico che scade; errori catturati e loggati; la risposta dice comunque `rolesAssigned`. |
| RO-AC8 | `PUT` idempotente | PARTIAL | `UsersController.cs:97-99,200-203` | Restituisce 400 se l'utente non ha org. È proprio il caso in cui il FE usa PUT (A1-01). |
| RO-AC9 | Admin salta l'onboarding | BROKEN | `FE/lib/onboarding.ts:54-57` | Un admin senza org viene mandato su `/onboarding` e non ne esce più (A1-01). |
| RO-AC10 | 400 su `rentalType` sconosciuto | PARTIAL | `UsersController.cs:171-172`; `UserService.cs:254-261` | `"7"` supera `Enum.TryParse`, poi arriva `ArgumentOutOfRangeException` → 500 (A1-35). |
| RO-AC11 | Pagina standalone | DONE | `FE/routes/index.tsx:179-182` | |
| RO-AC12 / 13 | `OnboardingGuard` e ordine delle route | DONE | `FE/routes/index.tsx:172-187` | Il guard avvolge anche `/app/admin` e `/app/supplier`: è la causa dei blocchi. |
| RO-AC14 | Sequenza mutation → token → navigate | DONE | `onboarding-page.tsx:85-92` | Usa il reload al posto di `useUserStore`: accettabile. |
| RO-AC15 | Toast di errore + retry | PARTIAL | `onboarding-page.tsx:93-97,175,201` | L'errore avviene allo step "plan", ma il retry compare solo allo step "role". Dopo l'errore `selectedType=null`, quindi il pulsante di conferma è disabilitato. |
| RO-AC16 | Demo mode `VITE_DEMO_PROFILE=onboarding` | BROKEN fuori da Playwright | `onboarding-guard.tsx:13-21`; `auth-bridge.tsx:52`; `FE/e2e/test.ts:37-47` (mock `users/me`) | Senza i mock il guard riceve un errore su `/users/me` e fa loop verso `/onboarding` (A1-18). |

#### 1.3 spec-onboarding-plg (#271 — in-dev, aperta)

| ID | Punto | Stato | Evidenza | Note |
|---|---|---|---|---|
| PLG-AC1 | Provisioning idempotente dell'org con `PlanTier` | DONE (con difetti) | `OrgService.cs:52-87`; `UserService.cs:240` | Scegliere il piano all'onboarding = upgrade gratuito (A1-03). Race check-then-insert (A1-14). |
| PLG-AC2 | Blocco consensi + 400 | DONE | `OnboardingService.cs:16-36`; `UsersController.cs:195-198` | |
| PLG-AC3 | `ConsentRecord` append-only con IP | PARTIAL | `OnboardingService.cs:38-78`; migrazione `20260611213956_AddConsentRecords.cs:63-78` | Scritto **dopo** org e ruoli, in modo non atomico (`UsersController.cs:205-220`). IP da `X-Forwarded-For` falsificabile (#273, A1-12). |
| PLG-AC4 | Endpoint legali anonimi | PARTIAL / STUB | `LegalController.cs:11-33`; `LegalDocumentService.cs:12-31,40-60`; `appsettings.json:83-94` | Solo metadati: `DocumentUrl: null`, nessun testo. Elenco sub-responsabili non veritiero (A1-06). |
| PLG-AC5 | `GET /api/onboarding/status` | DONE (solo BE) | `OnboardingController.cs:16-26`; `OnboardingService.cs:80-154` | Nessun consumer FE o mobile. |
| PLG-AC6 | Milestone derivate dallo stato reale | PARTIAL | `OnboardingService.cs:119-122` | `sitePublished` = `Org.IsActive` (true di default) + 1 proprietà attiva: non c'è un vero "pubblica". |
| PLG-AC7 | PUT mantiene org e consensi | PARTIAL | `UsersController.cs:202-203` | Vedi RO-AC8. |
| PLG-AC8 | Wizard con step consensi | DONE (con difetti) | `FE/features/onboarding/components/consents-step.tsx:17-129` | Nessun link al testo dei documenti. Stato di errore senza retry né "indietro" (A1-39). |
| PLG-AC9 | POST + refresh + dashboard | DONE | `onboarding-page.tsx:85-92` | |
| PLG-AC10 | Widget checklist di attivazione in dashboard | **MISSING** | `activation-checklist.tsx` non esiste; `FE/features/dashboard/dashboard-page.tsx:1-14` non la importa; nessun uso di `onboarding/status` nel FE | |
| PLG-AC11 | Pagina pubblica sub-responsabili + link nel footer | **MISSING** | nessuna route in `FE/routes/index.tsx:112-193`; `FE/features/legal/` non esiste | Non esistono pagine pubbliche per termini, privacy e DPA. |
| PLG-AC12 | Regressione guard e demo | PARTIAL | vedi RO-AC16 | |
| PLG-mobile | Gating su mobile | MISSING | `MOB/app/index.tsx:4-8`; `fetchMe` (`MOB/src/api/host.api.ts:13`) mai usato | Dall'app si usa la piattaforma senza onboarding né consensi (A1-05). |

#### 1.4 spec-saas-billing (#230 — registry: "shipped"; issue riaperta)

| ID | Punto | Stato | Evidenza | Note |
|---|---|---|---|---|
| SB-AC1 | `GET /api/billing/plans` | DONE (solo BE) | `BillingController.cs:23-35` | Price id placeholder in `appsettings.json:29-31`. Nessun consumer FE. |
| SB-AC2 | `POST /api/billing/checkout-session` | PARTIAL | `BillingController.cs:37-91`; `StripeBillingService.cs:30-61` | Nessun controllo su subscription già attiva (A1-10); nessuna IVA in checkout (A1-08); URL di default su dominio inesistente (A1-31). |
| SB-AC3 | Portal session | PARTIAL (solo BE) | `BillingController.cs:93-112`; `StripeBillingService.cs:63-81` | `PortalReturnUrl` punta a `https://app.casazen.app/settings/billing` (`appsettings.json:26`): dominio e route inesistenti. |
| SB-AC4 | `GET /api/billing/subscription` | DONE (solo BE) | `BillingController.cs:114-126,173-187` | Manca `seats`. |
| SB-AC5 | Webhook piattaforma separato (RF2) + idempotenza | PARTIAL | `WebhooksController.cs:46-137`; `StripeWebhookHandler.cs:31-118` | Il routing è corretto. L'idempotenza perde gli eventi in caso di errore (A1-09). Mapping degli stati fail-open (A1-11). |
| SB-AC6 | Downgrade e riattivazione | PARTIAL | `EntitlementService.cs:68-105`; `StripeWebhookHandler.cs:120-155` | `SyncFromSubscriptionAsync` sovrascrive `PlanTier` in modo permanente. |
| SB-AC7 | Matrice IVA/OSS [COUNSEL] | **BROKEN** | `VatCalculationService.cs:18-37` | OSS con aliquota IT 22%; EU B2C sotto soglia a 0% (A1-08). |
| SB-AC7b | Contatore soglia OSS €10k | PARTIAL | `OssRevenueTracker.cs:12-55`; `StripeWebhookHandler.cs:190-195` | Manca il test richiesto ("prima vendita oltre soglia ad aliquota di destinazione"); il reset annuale ignora la regola dell'anno precedente. |
| SB-AC8 | SDI FatturaPA | **STUB** | `SdiEInvoiceService.cs:7-16` (no-op, restituisce null) | `SdiStatus` resta sempre "pending". |
| SB-AC9 | Entry gate "P.IVA + SDI" | DONE | `BillingEntryGate.cs:14-38` | Chiude anche l'ambiente test di Railway, che gira con `ASPNETCORE_ENVIRONMENT=Production` (`BE/secrets/railway.test.variables.example.json:2`) → il billing non è testabile. |
| SB-AC10 | FE `plans-page.tsx` | **MISSING** | nessun file `FE/features/billing/*`, `billing.api.ts`, `use-billing.ts`; grep `checkout-session` nel FE = 0 risultati | Non è mai esistito (nessuna traccia in `git log`). |
| SB-AC11 | FE `billing-settings-page.tsx` + portale | **MISSING** | idem | |
| SB-AC12 | FE: paese e P.IVA al checkout | **MISSING** | idem (esiste solo BE `PUT /api/billing/profile`, `BillingController.cs:128-171`) | |
| SB-AC13 | Route billing solo per admin org + badge stato | **MISSING** | idem | |
| #274 | Upgrade del piano solo con subscription attiva | **MISSING** (issue aperta) | `OrgsController.cs:67-108`; bypass aggiuntivo in `UsersController.cs:174-179` | Il test `TenantBoundaryIntegrationTests.cs:160` ("Owner_CanChangePlanTier") codifica il bypass come comportamento atteso. |
| #273 | Hardening di `X-Forwarded-For` | **MISSING** (issue aperta) | `UsersController.cs:244-251`; `Program.cs:472-481` (chiave del rate limiter) | Non c'è `UseForwardedHeaders`. |

#### 1.5 spec-admin-backend (#11 — registry: "shipped")

| ID | Punto | Stato | Evidenza | Note |
|---|---|---|---|---|
| AD-AC1 | `GET /api/users` paginato con filtri | PARTIAL | `UsersController.cs:26-50`; `UserRepository.cs:48-81` | Ordinato per email invece che `createdAt DESC`; `page≤0` genera OFFSET negativo → 500; N chiamate Auth0 sequenziali per pagina (A1-26). |
| AD-AC2 | `GET /api/users/{id}` | DONE | `UsersController.cs:53-62` | |
| AD-AC3 | `GET /api/users/me` con upsert | DONE (dati vuoti) | `UsersController.cs:69-89`; `UserService.cs:91-142` | Email e nome vuoti (premessa). |
| AD-AC4 | `PUT /api/users/me` | DONE | `UsersController.cs:102-122`; `DTOs/Users/UpdateProfileDto.cs` | |
| AD-AC5 | `PUT /api/users/{id}/role` + sync Auth0 | BROKEN | `UsersController.cs:125-146`; `UserService.cs:196-210`; `Auth0ManagementService.cs:43-102` | Rimuove **tutti** i ruoli Auth0; errori silenziosi; `oldRole` non loggato (A1-17). |
| AD-AC6 | Soft delete | PARTIAL | `UsersController.cs:149-165`; `UserRepository.cs:83-93` | La disattivazione non blocca le policy `AdminOnly` né gli endpoint `[Authorize]` (A1-04). |
| AD-AC7 | 403 per i non-admin | DONE | `UsersController.cs:27,54,126,150`; `ServiceCollectionExtensions.cs:163` | |
| AD-AC8 | `GET /api/admin/stats` | DONE (inefficiente) | `AdminService.cs:33-89` | Carica tutte le proprietà in memoria; "OTA failed" significa in realtà "sync > 6h". |
| AD-AC9 | `GET /api/admin/cin-compliance` | DONE (BE) | `AdminController.cs:51-89`; `AdminService.cs:91-141` | Paginazione in memoria. |
| AD-AC10 | `GET /api/admin/jobs` | PARTIAL | `AdminService.cs:143-189` | Eccezioni inghiottite → lista vuota; `AlloggiatiWebReportJob` non è ricorrente (`Program.cs:379-470`). |
| AD-AC11 | Ruolo `Admin` nel FE | DONE | `FE/lib/auth-roles.ts:8` | |
| AD-AC12 | Route admin protette | DONE (meccanismo diverso) | `FE/config/route-manifest.ts:398-497` + `ContextRouteGuard` | Un admin senza org è bloccato prima (A1-01). |
| AD-AC13 | AdminAppShell + sidebar | DONE | `FE/components/layout/admin-app-shell.tsx:11-25`; `admin-sidebar.tsx` | La voce CIN manca perché la route non esiste. |
| AD-AC14 | Dashboard KPI | DONE | `FE/features/admin/admin-dashboard-page.tsx:10-119` | |
| AD-AC15 | Tabella utenti | PARTIAL | `admin-users-page.tsx:12-96`; `user-management-table.tsx:17-126` | Mancano il filtro attivi/inattivi e la colonna data; ruoli non tradotti; nessuno stato di errore; nessun debounce. |
| AD-AC16 | `/admin/cin` | **BROKEN (irraggiungibile)** | `FE/features/admin/admin-cin-page.tsx` esiste ma non è in `route-manifest.ts` | Il test `FE/e2e/admin.spec.ts:114-117` passa perché la route non esiste. |
| AD-AC17 | `/admin/jobs` con "Failed" in rosso | PARTIAL | `admin-jobs-page.tsx:7-24`; `job-status-badge.tsx` | Nessuno stato di errore; stati Hangfire come "Scheduled" o "Deleted" → "unknown". |
| AD-AC18 | Admin + Owner possono cambiare contesto | PARTIAL | `FE/contexts/workspace-provider.tsx:116-136` | Funziona solo se l'admin ha già un'org. |

#### 1.6 Debito noto (PLANNING.md §"Debito noto", righe 319-331)

| Punto | Stato | Evidenza | Note |
|---|---|---|---|
| 403 `no_org_context` alla creazione proprietà | DONE (con effetto collaterale) | `OrgContextResolver.cs:24-47`; `PropertiesController.cs:113-122` | L'org viene creata automaticamente su ~30 endpoint (grep `GetOrProvisionOrgIdAsync`), **senza consensi** (A1-05). |
| 404 piano/billing senza org | DONE (BE) / PARTIAL (FE) | `OrgsController.cs:79-81`; `FE/features/settings/plan-settings-page.tsx:25-27` | Il FE rimanda all'onboarding, che per gli utenti con ruoli è un vicolo cieco (A1-01). |
| Onboarding admin che salta la creazione org | **BROKEN (peggiorato)** | `FE/lib/onboarding.ts:54-57` + `BE UsersController.cs:202` | La correzione #285 blocca completamente gli admin senza org. |

#### 1.7 Altri punti della piattaforma

| Punto | Stato | Evidenza | Note |
|---|---|---|---|
| spec-org-seats-collaboration (US-013) | MISSING (non iniziata) | nessun `OrgInvitation`, `SeatLimit` o `team-page` (grep = 0) | Nessuna scrittura su `UserContextMembership` in tutto il codice: la membership esiste solo come seed di migrazione (`20260604193911_AddContextAuthorization.cs:159-189`). |
| `POST /api/auth/register` | STUB | `AuthController.cs:13-46` ("In production, this would create user in Auth0") | Anonimo, crea righe `User` con Id casuale e ignora la password (A1-30). |
| Impostazioni org (nome, logo, slug, email contatto) | MISSING | nessun endpoint scrive `Org.Name`, `DisplayName`, `LogoUrl` o `Slug` (grep) | Tutte le org si chiamano "La mia organizzazione" con slug `org-<sub Auth0>` (A1-22, A1-23). |
| Auth mobile | PARTIAL | `MOB/src/auth/AuthProvider.tsx:99-127`; `token-store.ts:6-11` | Il refresh token viene salvato ma mai usato (A1-33). |

---

### 2. Bug e difetti

#### P0 — bloccano l'uso, perdono dati o sono problemi di sicurezza

**A1-01 · P0 · Vicolo cieco nell'onboarding per chi ha ruoli Auth0 ma nessuna org (admin inclusi)**
- **Dove:** `FE/lib/onboarding.ts:54-57`; `FE/features/onboarding/onboarding-page.tsx:41,88,96,175,201`; `BE/Casazen.Web/Controllers/UsersController.cs:200-203`.
- **Descrizione:** `needsOnboarding` restituisce true per chiunque non abbia `orgId`, anche un Admin. È il contrario di RO-AC9. Nella pagina, `hasRoles=true` porta a `needsConsentsStep=false` e `isUpdate=true`, quindi una PUT senza consensi. Il BE risponde 400 "Initial onboarding must be completed with required consents." perché `!requireConsents && !hadOrg`. Dopo l'errore `selectedType=null`, quindi il pulsante di conferma è disabilitato. Il pulsante "Indietro" è nascosto (`!isOrgBackfill`) e il retry è visibile solo allo step "role".
- **Scenario:** un admin creato come da `AUTH0_SETUP.md` step 4 (ruolo assegnato dalla dashboard Auth0, nessuna proprietà, quindi nessuna org da backfill) fa login. `/app/admin` lo porta su `/onboarding` → sceglie → piano → "Completa" → toast di errore → schermata morta. Ricaricare non cambia nulla. Stessa sorte per qualunque owner creato a mano in Auth0 senza proprietà, e per gli utenti arrivati dopo il backfill.
- **Fix:**
  - FE: `needsOnboarding` non forza gli Admin (la creazione org diventa una CTA opzionale "Opera come host").
  - FE: quando `profile.orgId` è null, usare **sempre** POST con lo step consensi (`needsConsentsStep = needsOrgSetup(profile) || !hasRoles`) e calcolare `isUpdate` da `profile.onboardingCompletedAt && profile.orgId`, non dai ruoli JWT.
  - FE: pulsanti retry e indietro sempre visibili.
  - BE: se PUT arriva senza org, restituire un codice esplicito (`code:"consents_required"`) che il FE usa per mostrare i consensi.
  - Test L3 reale sul percorso di backfill.
- **Effort:** M.

**A1-02 · P0 · Sincronizzazione ruoli Auth0 con token M2M statico ed errori silenziosi**
- **Dove:** `BE/Casazen.Infrastructure/Services/Auth0ManagementService.cs:45-56,97-101,107-117,171-175`; `UserService.cs:205,225,244`; `OrgBillingAdminAuthorizationHandler.cs:12-17,43-48`.
- **Descrizione:** il token viene letto da `Auth0:ManagementApiToken`. Il codice non ha nessun flusso client-credentials (grep `GetTokenAsync`/`client_credentials` = 0). I token del Management API scadono (di default 24h). Ogni errore viene loggato e ignorato, e l'endpoint risponde 200 con `rolesAssigned`. Il DB salva un solo ruolo (`user.Role = roles[0]`).
- **Scenario:** dal giorno dopo la configurazione, ogni nuovo onboarding lascia l'utente senza ruoli nel JWT. Conseguenze:
  1. `RequireOrgBillingAdmin`, che controlla solo i claim di ruolo, restituisce 403 su dominio personalizzato e billing (`OrgDomainController.cs:16`).
  2. Chi sceglie "Entrambi" perde il contesto long-rent, perché il fallback DB conosce solo `PropertyOwner`.
  3. "Modifica tipo operatore" usa POST (`hasRoles=false`) senza consensi → 400.
  4. Il cambio ruolo in admin mostra "Ruolo aggiornato" ma Auth0 resta invariato.
  L'esempio delle variabili Railway (`secrets/railway.test.variables.example.json`) non contiene nemmeno il token.
- **Fix:**
  - Ottenere il token con `AuthenticationApiClient.GetTokenAsync(ClientCredentialsTokenRequest)`, in cache fino a `expires_in - 60s`, con ClientId e Secret M2M su Railway.
  - Propagare l'esito: `rolesSynced:false` nella risposta e 502 per il cambio ruolo admin.
  - Scrivere `UserContextMembership` per **tutti** i ruoli mappati, così l'autorizzazione BE non dipende da Auth0.
  - FE: se `rolesSynced=false`, mostrare un avviso e rifare il refresh.
  - Documentare in `AUTH0_SETUP.md`.
- **Effort:** M.

#### P1 — funzionalità rotta o incompleta, sicurezza e compliance

**A1-03 · P1 (diventa P0 al go-live del billing) · Upgrade del piano gratuito (#274) e piano scelto all'onboarding senza pagamento**
- **Dove:** `OrgsController.cs:67-108` (solo `[Authorize]`, blocca solo se `SubscriptionId` è valorizzato); `UsersController.cs:174-179` + `OrgService.cs:70-75`; `EntitlementService.cs:97-101` (`None → storedTier`); `FE/features/settings/plan-settings-page.tsx:19-23`.
- **Scenario:** un utente Starter clicca "Passa a Scale" (oppure sceglie Scale nel wizard). Ottiene proprietà illimitate e il dominio personalizzato "Pro" (`OrgDomainService.cs:41,69`) a costo zero. Anche un utente Supplier o Staff può cambiare il piano.
- **Fix:**
  - `PUT /orgs/me/plan`: solo downgrade o Starter senza subscription attiva, altrimenti 403 `code:"subscription_required"` (AC di #274).
  - Onboarding: ignorare `planTier ≠ Starter` finché non esiste una subscription (oppure registrarlo come "intento" e aprire il checkout).
  - Aggiornare il test `TenantBoundaryIntegrationTests.cs:160`.
- **Effort:** S.

**A1-04 · P1 · Utente disattivato ancora operativo**
- **Dove:** `UserRepository.cs:83-93` (solo `IsActive=false`); `ServiceCollectionExtensions.cs:163` (`AdminOnly` = `RequireRole`); `ContextAuthorizationService.cs:99-102` (unico punto che controlla `IsActive`); `UsersController.cs:69-122` (`/me`, onboarding senza controllo).
- **Scenario:** un admin disattiva un altro admin. Quest'ultimo continua a usare `/api/admin/*`, `/api/users/*` (cambio ruoli, disattivazioni), `PUT /orgs/me/plan` e il billing finché il suo JWT ha il ruolo Admin. Auth0 non viene toccato. Nel FE `/api/me/contexts` restituisce 403 → fallback sui ruoli JWT (`workspace-provider.tsx:127-131`) → UI visibile ma piena di 403, senza alcun messaggio.
- **Fix:** policy globale o middleware che rifiuta con 403 `account_inactive` gli utenti con `IsActive=false`, con cache per richiesta. `DeleteUserAsync` deve chiamare Auth0 `blocked=true` e rimuovere i ruoli. Il FE mostra una pagina "account disattivato".
- **Effort:** M.

**A1-05 · P1 (compliance) · Consensi ToS, Privacy e DPA aggirabili**
- **Dove:** `UserService.cs:127-137` (default `Role=PropertyOwner`); `ContextAuthorizationService.cs:50-59` (fallback su ruolo DB → permessi short-rent); `OrgContextResolver.cs:40-46` (org creata automaticamente); `MOB/app/index.tsx:4-8`.
- **Scenario:** un utente si registra su Auth0 e apre l'app mobile, oppure usa l'API con il token. Senza JWT roles il fallback DB gli dà `short-rent` con tutti i permessi di scrittura: crea org, proprietà e ospiti (dati personali) **senza aver accettato DPA e ToS**. Il consenso è imposto solo dal guard FE web.
- **Fix:**
  - Aggiungere `UserRole.None = 7` (in coda) come default per i nuovi utenti.
  - `ContextAuthorizationService` non concede contesti host se `OnboardingCompletedAt == null` o se mancano i consensi della versione corrente; risposta 403 `onboarding_required`.
  - Mobile: chiamare `fetchMe` e rimandare all'onboarding web.
- **Effort:** M.

**A1-06 · P1 (GDPR) · Documenti legali inesistenti ed elenco sub-responsabili non veritiero**
- **Dove:** `LegalDocumentService.cs:12-31` (`DocumentUrl: null`, solo titolo e sommario); `appsettings.json:83-94`; `Program.cs:89` (email = Resend, non SendGrid); `BE/secrets/vercel.variables.example.json:5-7` (tenant `dev-mp6wadq7j6bophl5.us.auth0.com`, cioè USA); `PushNotificationService.cs:179` (Expo); `AiProviderRegistration.cs:15-17` (DeepSeek).
- **Scenario:** l'utente spunta "Accetto i Termini di Servizio v2026-06-v1" senza poterli leggere (nessun link, nessuna pagina: `consents-step.tsx:65-85`). L'elenco dichiara "SendGrid — EU" e "Auth0 — EU", ma le email passano da Resend e Auth0 sta su un tenant US. Non sono elencati Expo (push), DeepSeek (se `Ai:Provider=DeepSeek`) e Vercel. Il consenso raccolto non è dimostrabile (Art. 7 e 28 GDPR).
- **Fix:** pubblicare i testi (pagine FE pubbliche `/legale/*` più `DocumentUrl`), correggere l'elenco, aumentare le versioni e aggiungere un flusso di ri-accettazione quando cambia versione (oggi `consentsAccepted=false` non attiva nulla). Aggiungere la pagina PLG-AC11 e i link nel footer. Revisione legale.
- **Effort:** M (più tempo del legale).

**A1-07 · P1 · Frontend del SaaS billing completamente assente**
- **Dove:** nessun `FE/features/billing/*`, `api/billing.api.ts` o `queries/use-billing.ts`; `route-manifest.ts` senza `/settings/billing`.
- **Scenario:** nessun operatore può pagare, aprire il portale o inserire la P.IVA. Il registry (`Sessions/specs/README.md:126`) dichiara "shipped".
- **Fix:** implementare SB-AC10…AC13: pagina piani con checkout, pagina billing con subscription e portale, form paese/P.IVA (`PUT /api/billing/profile`), gestione di `billing_gate_closed` e `managed_by_stripe`, stringhe in `t()`. La pagina piano esistente reindirizza al checkout quando il gate è aperto.
- **Effort:** L.

**A1-08 · P1 (fiscale) · IVA e OSS calcolate male, IVA non incassata**
- **Dove:** `VatCalculationService.cs:29-37`; `StripeBillingService.cs:49-58`; `StripeWebhookHandler.cs:179-209`.
- **Descrizione e scenario:**
  - (a) OSS applica `ItalianVatRate` (22%) invece dell'aliquota del paese di destinazione (DE 19%, FR 20%). AC7b lo vieta.
  - (b) Un cliente EU B2C sotto soglia paga 0% invece dell'IVA italiana 22%.
  - (c) Un cliente extra-UE finisce etichettato "EuBelowThreshold".
  - (d) Il checkout Stripe non applica tasse (nessun `automatic_tax` né `tax_rates`), ma `PlatformInvoice.VatAmount` registra IVA: l'importo incassato è l'imponibile e l'IVA dovuta resta a carico di CasaZen.
- **Fix:** tabella delle aliquote per paese; sotto soglia → IT 22%; extra-UE → fuori campo art. 7-ter; Stripe Tax o `tax_rates` sul checkout coerenti con `PlatformInvoice`; test AC7b. [COUNSEL_REQUIRED]
- **Effort:** L.

**A1-09 · P1 · Idempotenza del webhook Stripe che perde eventi**
- **Dove:** `StripeWebhookHandler.cs:34-38,83-87,94-118`.
- **Scenario:** l'evento viene "claimato" (insert in `ProcessedStripeEvents`, commit) **prima** dell'elaborazione. Se `HandleInvoicePaymentFailedAsync` va in timeout sul DB, il job lancia l'eccezione e Hangfire ritenta, ma il retry trova l'evento già claimato e lo salta. L'org non passa mai a `PastDue`, oppure la subscription non viene mai registrata.
- **Fix:** claim ed elaborazione nella stessa transazione, oppure cancellare la riga di claim nel `catch` prima del rethrow.
- **Effort:** S.

**A1-10 · P1 · Checkout che crea subscription duplicate**
- **Dove:** `BillingController.cs:37-91`.
- **Scenario:** un'org con subscription Pro attiva chiama di nuovo checkout per Scale → seconda subscription → doppio addebito. Nessun controllo su `SubscriptionId` o sullo stato.
- **Fix:** 409 `already_subscribed` con redirect al portale quando lo stato è Active, Trialing o PastDue.
- **Effort:** S.

**A1-11 · P1 · Mapping degli stati subscription fail-open**
- **Dove:** `StripeWebhookHandler.cs:141-145,284-296`; `EntitlementService.cs:100`.
- **Scenario:** con una subscription `incomplete`, `incomplete_expired` o `paused`, lo stato mappato diventa `None` ma `PlanTier` prende il tier del prezzo. `ResolveEffectiveTier(None)` restituisce il tier salvato, quindi un pagamento non riuscito concede Scale.
- **Fix:** stati sconosciuti o incomplete → trattati come `Canceled` ai fini dell'entitlement; aggiornare `PlanTier` solo quando lo stato è Active o Trialing.
- **Effort:** S.

**A1-12 · P1 (#273) · `X-Forwarded-For` falsificabile**
- **Dove:** `UsersController.cs:244-251`; `Program.cs:159-167,472-481`.
- **Scenario:** un header `X-Forwarded-For: 1.2.3.4` fa salvare un IP arbitrario in `ConsentRecord.IpAddress`, quindi la prova del consenso non vale. Ruotando l'header si aggira il rate limit `PublicResolveHost`.
- **Fix:** `ForwardedHeadersOptions` con `KnownNetworks` del proxy Railway, `app.UseForwardedHeaders()` prima di tutto, poi usare `Connection.RemoteIpAddress`. Test sull'header falsificato.
- **Effort:** S.

**A1-13 · P1 (accesso) · Collegamento account tramite email non verificata**
- **Dove:** `SupplierService.cs:375-386`; `SupplierOrgContextResolver.cs:42-54`. Nessun controllo `email_verified` in tutto il codice (grep = 0).
- **Scenario:** un fornitore si registra con `POST /suppliers/register` (anonimo) usando l'email X e non ha ancora un account Auth0. Un attaccante crea un account Auth0 con l'email X (database connection, email non verificata) e chiama un endpoint supplier: il resolver collega la sua utenza all'org del fornitore.
- **Fix:** collegare per email solo se `email_verified=true` (dal Management API o da un claim aggiunto nell'Action), oppure con un token di invito firmato.
- **Effort:** S. Area di confine con Supplier.

**A1-15 · P1 · Modifica del tipo operatore: piano ignorato in silenzio e step saltati**
- **Dove:** `onboarding-page.tsx:54-56,166-197`; `UsersController.cs:97-99`; `OrgService.cs:62-66`.
- **Scenario:** Profilo → "Modifica tipo" porta direttamente allo step piano. L'utente sceglie Pro e clicca "Salva modifiche": risposta 200 ma il piano resta Starter, perché con org esistente `EnsureOrgForUserAsync` restituisce l'org senza modificarla.
- **Fix:** in modalità edit togliere il selettore piano (rimandare alla pagina piano) e partire dallo step ruolo.
- **Effort:** S.

**A1-16 · P1 · Pagina Admin CIN irraggiungibile e test e2e vuoto**
- **Dove:** `FE/features/admin/admin-cin-page.tsx:13`; `FE/config/route-manifest.ts:398-497` (nessuna voce `/app/admin/cin`); `FE/e2e/admin.spec.ts:114-117`.
- **Scenario:** l'admin non ha menu né URL per l'audit CIN (AD-AC16). Il test passa perché la route non esiste.
- **Fix:** aggiungere la voce nel manifest (`requiredPermissions:['admin.cin.read']`, `navKey`) e un test che verifica tabella e filtro.
- **Effort:** S.

**A1-17 · P1 · Cambio ruolo admin distruttivo**
- **Dove:** `Auth0ManagementService.cs:67-91`; `FE/features/admin/components/change-role-dialog.tsx:17-24`; `UserService.cs:196-210`.
- **Scenario:** l'admin imposta PropertyOwner su un utente "Entrambi" o Supplier. Auth0 rimuove tutti i ruoli, quindi l'utente perde LongTermLandlord e Supplier. Il dialog offre Guest e Staff (senza senso per l'app) ma non Supplier. Le membership DB legacy non cambiano (A1-29). `oldRole` non viene loggato (spec AD-AC5).
- **Fix:** UI multi-ruolo (aggiungi/rimuovi); API `PUT /users/{id}/roles` che confronta i ruoli correnti con quelli nuovi e aggiorna le membership; log di audit con `oldRole`; esito della sincronizzazione visibile.
- **Effort:** M.

**A1-18 · P1 · Demo mode rotta fuori da Playwright**
- **Dove:** `FE/components/auth/onboarding-guard.tsx:13-21`; `FE/contexts/auth-bridge.tsx:52`; `FE/e2e/test.ts:37-47`; `FE/DEMO_MODE.md` ("si apre direttamente sulla dashboard").
- **Scenario:** con `npm run dev:demo` e senza mock, `/api/users/me` riceve `Bearer demo-token` → 401 o errore di rete → `profileError` → redirect a `/onboarding` → la conferma chiama `window.location.assign('/app/short-rent')` → il guard rimanda a `/onboarding`, in loop. Le demo per presentazioni non funzionano.
- **Fix:** in `isDemoMode`, `useMe` restituisce un profilo demo (org e `onboardingCompletedAt` derivati dal persona), oppure il guard salta il controllo del profilo.
- **Effort:** S.

**A1-22 · P1 · Identità org e utente vuota, nessuna impostazione org**
- **Dove:** `UserService.cs:127-137`; `OrgContextResolver.cs:33-36`; `OrgService.cs:69,76`; `StripeBillingService.cs:21-25`. Nessun endpoint scrive `Org.Name`, `DisplayName`, `LogoUrl` o `ThemeColor` (grep).
- **Scenario:** siccome l'access token non ha email né nome, ogni nuova org si chiama "La mia organizzazione" con `ContactEmail=""`. Il sito pubblico mostra quel nome (`PublicOrgDto.displayName`); il customer Stripe viene creato senza email, quindi le fatture non vengono inviate; la ricerca per email nella tabella admin non trova nulla; `EnrichUsersFromAuth0Async` fa fino a 20 chiamate Management API sequenziali a ogni pagina (`UserService.cs:152-193`).
- **Fix:** aggiungere nell'Action Auth0 i claim `https://casazen.app/email`, `name` ed `email_verified` sull'access token e leggerli nel BE; backfill degli utenti esistenti; nuovo `PUT /api/orgs/me` (nome, email contatto, logo) con pagina "Impostazioni organizzazione"; step "Nome attività" nel wizard.
- **Effort:** M.

**A1-40 · P1 · Utente fornitore che diventa host: proprietà create nell'org fornitore**
- **Dove:** `SupplierService.cs:94-97,444-445` (imposta `User.OrgId` = org supplier); `OrgContextResolver.cs:40-41`; `OrgService.cs:62-66`.
- **Scenario:** un fornitore si registra (`OrgId` = org di tipo Supplier) e poi fa l'onboarding host. `EnsureOrgForUserAsync` restituisce l'org Supplier, quindi proprietà, prenotazioni, piano e sito pubblico finiscono nell'org `OrgType.Supplier`.
- **Fix:** il resolver host ignora le org con `OrgType != Host` e ne crea una nuova; migrazione per separare i casi esistenti.
- **Effort:** M.

#### P2 — qualità, UX, debito

- **A1-14 · P2 · Race sul primo accesso.** `UserService.cs:127-141` e `OrgService.cs:59-86` fanno check-then-insert. Due richieste parallele sul primo login (FE più mobile, oppure i widget della dashboard) causano violazione di PK o dello slug unico, e `ErrorHandlingMiddleware.cs:105-111` restituisce 500. **Fix:** upsert con `ON CONFLICT`, oppure catch della violazione e rilettura. **Effort:** S.
- **A1-19 · P2 · Errore transitorio su `/users/me` rimanda all'onboarding.** In `onboarding-guard.tsx:19-21` un 5xx o un timeout porta un utente attivo su `/onboarding`, dove può rifare la PUT e cambiare i propri ruoli. **Fix:** schermata di errore con retry, redirect solo su 404 o `needsOnboarding`. **Effort:** S.
- **A1-20 · P2 · `TenantContext` mette in cache `OrgId=null` prima del provisioning.** `TenantContext.cs:33-52` e `OrgContextResolver.cs:26-46`: nella stessa richiesta in cui si crea l'org, i filtri EF usano `null` e le letture tornano vuote. **Fix:** `ITenantContext.SetOrgId()` chiamato dal resolver dopo il provisioning. **Effort:** S.
- **A1-21 · P2 · `ReservePropertySlotAsync` non riserva nulla.** `EntitlementService.cs:37-66` apre una transazione serializable, conta e fa commit; l'insert avviene in un'altra transazione, quindi due POST paralleli superano il limite. In più il conteggio usa il filtro tenant, perciò la PATCH admin mostra `usage=0` (`AdminController.cs:122`). **Fix:** insert e controllo nella stessa transazione, oppure advisory lock per org; `IgnoreQueryFilters()` con `OrgId` esplicito. **Effort:** S.
- **A1-23 · P2 · Slug dell'org derivato dal `sub` Auth0 e non modificabile.** Con `OrgService.cs:142-157` l'URL pubblico diventa `/book/org-google-oauth2-1098…`: espone l'identificativo dell'IdP ed è poco leggibile. Nessun endpoint per cambiarlo. **Fix:** slug dal nome dell'attività con controllo di disponibilità nel wizard. **Effort:** S.
- **A1-24 · P2 · Il middleware errori espone dettagli interni.** `ErrorHandlingMiddleware.cs:84-97` restituisce `ex.Message` per `InvalidOperationException` (con status 503!) e `StripeException` (ad esempio "User … must exist before org provisioning", "Billing price not configured"). Viola `.claude/rules/security.md`. **Fix:** messaggi localizzati generici, dettaglio solo nei log. **Effort:** S.
- **A1-25 · P2 · Violazioni i18n.**
  - BE: messaggi hardcoded in `UsersController.cs:172,178,203`, `OrgsController.cs:77,81,88`, `BillingController.cs:44,47,59,71`, `OnboardingService.cs:23,30,33`, `LegalDocumentService.cs:15-30` e `PlanCatalog.cs:20-22`, nessun `IStringLocalizer`.
  - FE: `protected-route.tsx:25,37` (inglese); `context-route-guard.tsx:17` e `legacy-redirect.tsx:14` ("Loading workspace..."); `lib/onboarding.ts:5-9` (etichette IT hardcoded); `user-management-table.tsx:55` e `change-role-dialog.tsx:71-72` (enum ruoli grezzi); `plan-badge.tsx:23` (tier grezzo).
  - **Fix:** chiavi `.resx` e `t()`. **Effort:** M.
- **A1-26 · P2 · Pagine admin senza stati di errore e con filtri incompleti.** `admin-users-page.tsx:18` ignora `isError`; `admin-jobs-page.tsx:9` ignora `isError` e mostra "vuoto" anche su 500; `AdminService.cs:182-186` inghiotte l'eccezione. Mancano il filtro `isActive` e la colonna `createdAt`; l'ordinamento è per email (`UserRepository.cs:75`); `page=0` causa 500 (`UserRepository.cs:76`); nessun debounce sulla ricerca. **Effort:** S.
- **A1-27 · P2 · Statistiche e CIN admin caricano tutte le proprietà in memoria.** `AdminService.cs:45,105` usano `ToListAsync()` sull'intera tabella con paginazione in RAM. **Fix:** aggregati e paginazione lato SQL (`CinStatus` calcolato o regex in SQL). **Effort:** S.
- **A1-28 · P2 · Invariante RF1 violata o parziale.** `Guest` non ha `OrgId`; `GuestsController.cs:28-37` carica **tutti** gli ospiti della piattaforma e fa un controllo di accesso N+1 per ciascuno. `ConsentRecord`, `GuestCheckInSession`, `DeviceRegistration`, `PlatformInvoice` e `ServiceRequest` non hanno filtro globale. **Fix:** `Guest.OrgId` con migrazione in 3 step e filtro; filtri o policy per le altre tabelle. **Effort:** L.
- **A1-29 · P2 · Membership legacy mai revocate e `LastUsedContextKey` mai scritto.** Il seed in `20260604193911_AddContextAuthorization.cs:159-189` non viene più aggiornato; `ContextAuthorizationService.cs:62-82` unisce le membership DB con i ruoli JWT, quindi un ruolo revocato continua a dare accesso agli utenti pre-giugno. **Fix:** tenere le membership sincronizzate a ogni cambio ruolo (vedi A1-02, A1-17). **Effort:** S.
- **A1-30 · P2 · `POST /api/auth/register` anonimo e stub.** `AuthController.cs:13-46` crea utenti fantasma (Id casuale, password ignorata) e permette spam nel DB e nella lista admin; `/logout` non fa nulla. **Fix:** rimuovere l'endpoint oppure proteggerlo e farlo delegare ad Auth0. **Effort:** S.
- **A1-31 · P2 · Configurazione billing inutilizzabile in test e produzione.**
  - `secrets/railway.test.variables.example.json:2` (test = `Production`) più chiave `sk_test` fanno chiudere il gate (`BillingEntryGate.cs:20-27`).
  - Gli URL di ritorno di default (`BillingController.cs:87-88`, `appsettings.json:26`) puntano a `app.casazen.app`, dominio non in CORS, e a `/settings/billing`, route che non esiste nel FE.
  - I price id sono placeholder (`appsettings.json:29-31`).
  - `successUrl` e `cancelUrl` arrivano dal client senza allow-list.
  - **Fix:** `ASPNETCORE_ENVIRONMENT=Staging` per il test, URL costruiti da `App:PublicSiteBaseUrl` su route FE reali, allow-list dei domini. **Effort:** S.
- **A1-32 · P2 · Stesso tenant Auth0 per preview e produzione.** `secrets/vercel.variables.example.json:5-7`: un ruolo assegnato dal test vale anche in prod, e gli utenti di test finiscono in prod. **Fix:** tenant separati. **Effort:** S (ops).
- **A1-33 · P2 · Mobile senza refresh token e senza gating.** `MOB/src/auth/AuthProvider.tsx:99-127` salva il refresh token ma non lo usa mai: alla scadenza arriva un 401 e serve un nuovo login. Nessun controllo su onboarding, org o `IsActive`. **Effort:** M.
- **A1-34 · P2 · Hygiene di sicurezza FE e Hangfire.** `FE/lib/axios.ts:50-56` logga il prefisso del token e fa debug verboso in produzione. Il token sta in `localStorage` (`auth.config.ts:11-12`). `HangfireAuthorizationFilter.cs:24-25` confronta la API key senza tempo costante. La CORS accetta qualunque `*.vercel.app` con credenziali (`ServiceCollectionExtensions.cs:251`). `StripeWebhookHandler.cs.bak` è versionato. **Effort:** S.
- **A1-35 · P2 · Validazione degli enum.** `Enum.TryParse` accetta stringhe numeriche (`UsersController.cs:129,171`): `rentalType:"7"` produce un 500 (`UserService.cs:254-261`), `role:"9"` salva un ruolo inesistente. **Fix:** `Enum.IsDefined` più `[EnumDataType]`. **Effort:** S.
- **A1-36 · P2 · Piano non gestibile per i landlord LTR-only.** Il badge punta a una route short-rent (`org-badge.tsx:19`, `entitlement-error.ts:7`); `me/entitlement` richiede `short-rent:property.read` (`OrgsController.cs:37-38`); `OrgBillingAdmin` esclude `LongTermLandlord` (`OrgBillingAdminAuthorizationHandler.cs:12-17`). Un landlord solo LTR non può vedere né cambiare piano o billing. **Effort:** S.
- **A1-37 · P2 · Milestone `sitePublished` sempre vera.** `OnboardingService.cs:119-122`: basta una proprietà attiva, perché `Org.IsActive` è true di default. **Fix:** flag esplicito "sito pubblicato", oppure dati minimi di branding e policy di checkout attiva. **Effort:** S.
- **A1-38 · P2 · Pagina piano: promise non gestita e 409 generico.** In `plan-settings-page.tsx:19-23` un errore di `mutateAsync` lascia `selectedTier` bloccato. Il 409 `managed_by_stripe` mostra un toast generico senza link al portale (che comunque non esiste). **Effort:** S.
- **A1-39 · P2 · Step consensi senza retry e versioni obsolete non gestite.** `consents-step.tsx:30-38`: se un documento legale non si carica, la schermata è morta (nessun retry né indietro). Un 400 `staleDocuments` non viene gestito e il FE continua a rinviare le vecchie versioni. **Effort:** S.
- **A1-41 · P2 · PATCH piano admin ignora Stripe.** `AdminController.cs:107-130` modifica il piano anche con una subscription attiva; il webhook successivo lo sovrascrive. **Fix:** 409 oppure aggiornare la subscription Stripe. **Effort:** S.
- **A1-43 · P2 · Test che non verificano nulla di significativo.**
  - FE: gli e2e di onboarding girano solo in demo mode con `window.location` e non chiamano mai `POST /users/onboarding` (`FE/e2e/onboarding.spec.ts:5-21`; `golden-journey-web.spec.ts:47-64`); `admin.spec.ts:114-117` è vuoto.
  - BE: `PlgOnboardingIntegrationTests.cs:186` codifica il 400 della PUT che causa A1-01; `TenantBoundaryIntegrationTests.cs:160` codifica il bypass #274.
  - Mancano test per i webhook `customer.subscription.*` e `invoice.*`, per OSS AC7b, per il fallimento della sync Auth0 e per l'utente disattivato. `BillingIntegrationTests.cs` ha 4 test.
  - Le migrazioni sono testate solo con string-match.
  - **Effort:** M.

---

### 3. Piano di fix dell'area

Ordine con dipendenze. Le stime sono in giorni-persona.

**Fase 0 — Sbloccare l'accesso (prerequisito del Golden Journey step 3), ~3-4 g**
1. **A1-01 (vicolo cieco nell'onboarding e bypass admin)**: FE (`needsOnboarding`, POST al posto di PUT quando manca l'org, pulsanti retry e indietro) più BE (codice `consents_required`). Nessuna dipendenza.
2. **A1-02 (sync Auth0 affidabile)**: token client-credentials in cache, esito propagato, `UserContextMembership` scritte per tutti i ruoli. Serve per 3, 7 e 12.
3. **A1-22 (claim email e nome nell'access token + impostazioni org)**: modifica dell'Action Auth0, lettura dei claim, backfill, `PUT /api/orgs/me` con pagina dedicata, step "Nome attività" e slug (A1-23). Serve per il billing (email del customer Stripe).
4. **A1-18 (demo mode)** e **A1-19 (errore transitorio su `/me`)**: correzioni piccole nel guard.

**Fase 1 — Hardening accesso e sicurezza, ~3 g** (dopo la Fase 0)
5. **A1-05**: gate BE `onboarding_required` (`UserRole.None` come default, consensi obbligatori per i contesti host) e controllo lato mobile. Dipende da 1 e 2, altrimenti blocca gli utenti legittimi.
6. **A1-04**: enforcement di `IsActive` e blocco in Auth0. Dipende da 2.
7. **A1-12 (#273)**: forwarded headers.
8. **A1-13**: collegamento solo con email verificata, coordinato con l'area Supplier. **A1-40**: separare org host e supplier.
9. **A1-30, A1-24, A1-34, A1-35**: pulizia (register stub, middleware errori, log token, enum).

**Fase 2 — Legale e PLG (chiude #271), ~3 g + legale**
10. **A1-06**: testi legali, pagine pubbliche `/legale/*` e sub-responsabili (PLG-AC11), elenco corretto, bump delle versioni, flusso di ri-accettazione (sfrutta il gate del punto 5).
11. **PLG-AC10**: widget checklist di attivazione sul `GET /api/onboarding/status` già esistente, più **A1-37** (vero flag "sito pubblicato"). Dipende dall'area del sito brandizzato.
12. **A1-15, A1-39**: correzioni della modalità edit e dello step consensi.

**Fase 3 — Billing (chiude #230 e #274), ~7-10 g**
13. **A1-03 (#274)**: gating dei piani a pagamento dietro una subscription attiva, sia su PUT sia all'onboarding.
14. **A1-09, A1-10, A1-11**: robustezza di webhook e checkout, con test sugli eventi `customer.subscription.*` e `invoice.*`.
15. **A1-31, A1-32**: configurazione degli ambienti (test in Staging, URL di ritorno, price id, tenant Auth0 separati).
16. **A1-07**: FE billing (piani, checkout, portale, paese/P.IVA, badge stato). Dipende da 13-15.
17. **A1-08**: IVA/OSS corretta con Stripe Tax o tax_rates, test AC7b; SDI resta [COUNSEL] + provider reale (SB-AC8). Blocca il go-live, non la demo.

**Fase 4 — Admin e qualità, ~3-4 g**
18. **A1-16** (route CIN), **A1-17** (ruoli multipli e audit), **A1-26**, **A1-27**, **A1-41**.
19. **A1-28 / A1-29**: RF1 su `Guest` (migrazione in 3 step) e filtri per le tabelle scoperte.
20. **A1-25 (i18n)**, **A1-36**, **A1-38**, **A1-14**, **A1-20**, **A1-21**, **A1-33**.
21. **A1-43**: test L3 reali (utente Auth0 di test: onboarding POST, backfill con PUT, admin senza org), rimozione dei test vuoti, aggiornamento del registry `Sessions/specs/README.md` (billing e admin non sono "shipped").

---

### 4. Verdetto

**Completamento reale stimato: circa 50% per l'area**, a fronte del ~90% che il registry lascia intendere. Pesi indicativi: tenant boundary ~80%, admin ~65%, role-onboarding ~55%, PLG ~50%, SaaS billing ~25% (FE 0%), org seats 0% (fase 2, fuori dal calcolo).

- Il backend del tenant boundary è la parte più solida: `Org`, FK, filtro globale e migrazioni in 3 step sono fatti con cura (`AppDbContext.cs:463-469`, `MakeOrgIdRequired.cs:14-81`). Però l'invariante RF1 è bucata su `Guest` e su diverse tabelle nuove.
- Il flusso di accesso è fragile in modo sistemico. I ruoli dipendono da un token Auth0 statico che scade in silenzio (`Auth0ManagementService.cs:45`). Gli utenti con ruoli ma senza org, admin compresi, restano chiusi in un onboarding senza uscita (`onboarding.ts:55` più `UsersController.cs:202`). L'access token non porta email né nome, quindi org e customer Stripe nascono anonimi.
- Consensi e documenti legali ci sono solo di facciata: si accettano documenti senza testo, con un elenco di sub-responsabili falso, e il backend concede comunque accesso host a qualunque utente autenticato, a prescindere dai consensi (`ContextAuthorizationService.cs:50-59`).
- Il SaaS billing ha endpoint BE e webhook ma nessuna interfaccia. Il calcolo IVA/OSS è sbagliato, l'SDI è uno stub, e il piano a pagamento si ottiene gratis con un clic (#274).

Le priorità assolute sono A1-01, A1-02, A1-05 e A1-03, prima di dichiarare lo step 3 del Golden Journey eseguibile.
