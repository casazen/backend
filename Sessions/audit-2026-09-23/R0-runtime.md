## R0 — Verifica runtime (eseguita dal coordinatore, 2026-09-23)

Ambiente: container cloud, .NET SDK 10.0.112, Node 22, PostgreSQL 16 locale, Chromium Playwright.
Limite: nessun login Auth0 reale disponibile → i flussi autenticati (console host, console fornitore, app) sono stati verificati solo tramite test d'integrazione e analisi statica; i flussi pubblici (sito booking, checkout, check-in ospite, SEO) sono stati percorsi davvero nel browser contro API + Postgres reali.

### Build, test, lint

| Repo | Comando | Esito |
|---|---|---|
| backend | `dotnet build -c Release` | ✅ 0 errori; 18 warning **tutti vulnerabilità NuGet** (NU1903/NU1904: `Microsoft.AspNetCore.DataProtection` 10.0.1 critical, `System.Security.Cryptography.Xml` 10.0.7 high, `starkbank-ecdsa` 1.3.1 critical) |
| backend | `dotnet format --verify-no-changes` | ✅ |
| backend | `dotnet test` (tutto) | 908 ✅ / 25 skipped (tutti test adapter OTA) / 1 ❌ (Testcontainers: richiede Docker) |
| backend | `dotnet ef database update` su Postgres vuoto | ✅ 33 migrazioni applicate; `has-pending-model-changes` → nessuna deriva |
| backend | **184 test d'integrazione rieseguiti su Postgres reale** (copia scratch, factory patchata) | 181 ✅ / 3 ❌ → 1 bug reale (tassa soggiorno 500), 2 test non isolati (unique index SEO) |
| frontend | `tsc -b --noEmit` | ✅ |
| frontend | `vitest run` | ✅ 37 file / 166 test |
| frontend | `vite build` | ✅ |
| frontend | `eslint .` | ❌ 70 problemi (55 errori: 37 `no-explicit-any`, 7 `exhaustive-deps`, 1 `rules-of-hooks`) — **la CI frontend non esegue né lint, né typecheck, né vitest** |
| mobile | `tsc --noEmit` | ✅ |
| mobile | `expo lint` | ❌ eslint non installato/configurato |

**Nota chiave:** `CasazenWebApplicationFactory` forza EF InMemory → nessun test backend gira su PostgreSQL in CI. I bug specifici di Npgsql (DateTime Kind, vincoli unique/FK, transazioni) non vengono mai intercettati.

### Bug confermati a runtime

| ID | Sev | Dove | Evidenza |
|---|---|---|---|
| R-01 | P0 | Date in ingresso → Npgsql | `POST /api/public/tourist-tax/calculate {"comuneSlug":"milano","numberOfAdults":2,"checkInDate":"2026-10-01","checkOutDate":"2026-10-04"}` → **500** `Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'` (`TouristTaxRateRepository.cs:25` ← `SeoContentService.cs:59`). Nessuna normalizzazione UTC globale: solo `SpecifyKind` ad hoc in 3 punti. Stessa classe di bug probabile su creazione lease (A7-05) e altri endpoint con date `yyyy-MM-dd`. Il calcolatore nella pagina SEO mostra "Impossibile calcolare la tassa". |
| R-02 | P0 | Validazione CIN (BE+FE) | Regex `^IT-\d{5}-\d{10}$` in 5 punti (`CinComplianceRules.cs:8`, `CinCodeAttribute.cs:12`, `AdminService.cs:17`, `PropertyService.cs:336`, FE `property.schema.ts:22`). Un CIN nel formato effettivamente rilasciato dalla BDSR (es. `IT013075C2ABCDEFGH`, 18 caratteri senza trattini) è marcato **"CIN non valido"** e mostrato in rosso sul sito pubblico al guest. `.claude/context/regulations/cin.md:73` stesso dice "formato da verificare". Anche la regola in `.claude/rules/compliance.md` va corretta. |
| R-03 | P1 | Sito pubblico — disponibilità | Il FE chiama `GET /api/public/bookings/property/{slug}/availability`; l'API accetta solo GUID → **400**. Con property dotata di slug (default dal 07/2026) il calendario disponibilità non si carica mai. |
| R-04 | P1 | Sito pubblico — widget → checkout | Il bottone "Procedi al checkout" naviga a `/checkout??checkIn=…&checkOut=…` (doppio `?`): check-in e ospiti persi; "Cancellazione gratuita fino a Invalid Date". Con URL corretto la pagina funziona. |
| R-05 | P0 | Prezzo mostrato ≠ prezzo registrato | Checkout mostra "Tassa di soggiorno 12,00 € — Totale 412,00 €" (hardcoded FE), il backend crea il booking con `amount: 400.00, touristTaxAmount: 0`. |
| R-06 | P1 | "Le mie prenotazioni" | FE invia `{"email":…}`, BE richiede `BookingId` → **400**; la UI mostra "Nessuna prenotazione trovata" + "Errore nella ricerca". |
| R-07 | P1 | Localizzazione backend | Tutte le ProblemDetails mostrano chiavi grezze (`"detail":"UnauthorizedDetail"`, `"InternalServerErrorDetail"`): `SharedResources` è nel namespace `Casazen.Web.Resources` + `ResourcesPath="Resources"` (`Program.cs:115`) → risorsa cercata in `Casazen.Web.Resources.Resources.SharedResources`. Solo 3 usi di `IStringLocalizer` in tutto il BE; il resto dei messaggi è inglese hardcoded ("Complete Stripe onboarding before accepting guest payments", "Supplier not found"…). |
| R-08 | P2 | i18n FE | "3 nottei" (plurale rotto) nel widget e nel checkout. |
| R-09 | P2 | Contenuti SEO | Pagine comune con corpo segnaposto "Milano: contenuto generato per affitti brevi, CIN e tassa di soggiorno." |
| R-10 | P2 | API pubblica | `GET /api/public/bookings/property/{guid-inesistente}/availability` → 200 con lista vuota invece di 404. `POST /webhooks/ota/{platform}` → 500 se manca `OTA:WebhookSecret` (feature in freeze ma endpoint esposto). |
| R-11 | P2 | Checkout "paga in struttura" | Con Connect non configurato il FE mostra solo "Impossibile avviare il checkout" (messaggio BE inglese non propagato). Con account Connect presente il booking OnSite viene **confermato subito** senza alcuna garanzia (coerente con A3-06). |

### Cosa funziona davvero (verificato)
- Health, 401 su endpoint protetti, verifica firma webhook Stripe/Connect/e-sign (400/401 senza firma).
- Read-model pubblico org/property (niente OwnerId esposto; `contactEmail` sì).
- Checkout "paga in struttura" crea un booking confermato quando l'org ha Connect.
- Portale check-in con token non valido → messaggio corretto in italiano.
- Pagina aiuto iCal, pagine SEO renderizzate (client-side), sitemap compliance.
