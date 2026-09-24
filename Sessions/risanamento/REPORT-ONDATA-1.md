# Risanamento: report di fine ondata 1 (prerequisiti, ricerche, fondamenta, tenant)

Data: 2026-09-24 · Branch di integrazione: `claude/app-analysis-fixes-plan-p0mx0a` (backend, frontend, mobile)

## Stato

| | Valore |
|---|---|
| Task completati | 42 su 166 |
| Parziali / bloccati | RS-7 parziale; RS-6 e FD-19 bloccati (vedi sotto) |
| In corso | SU-03, LT-11, MO-01, BK-02, PL-01, BK-21 |
| Difetti coperti da task completati | 109 su 335 |
| P0 chiusi | 25 su 46 |
| Test backend (su PostgreSQL reale) | 2.186 passati, 0 falliti (all'inizio: 908, su InMemory) |
| Test frontend (vitest) | 488 passati, 0 falliti; lint a 0 errori |

Completati:
- **Prerequisiti:** PR-1, PR-2, PR-3.
- **Ricerche:** RS-1, RS-2, RS-3, RS-4, RS-5, RS-8, RS-9.
- **Fondamenta:** FD-01…FD-18, FD-20, FD-21.
- **Tenant:** TN-1, TN-2, TN-3, TN-4.
- **Funzionalità:** CO-01, CO-02, CO-03, CO-11, BK-01, MO-02, PC-01, PL-10.

P0 chiusi, per ID: A1-02 A2-01 A2-02 A2-03 A3-01 A3-03 A3-07 A4-01 A5-01 A5-03 A5-04 A5-05 A5-06 A5-07 A6-02 A7-05 A8-01 A9-01 A9-02 A9-03 A9-04 A9-05 A9-06 R-01 R-02.

## Cosa esiste ora (convenzioni usate da tutti i task successivi)

- **Errori:**
  - codici stabili (`ProblemCodes`) e messaggi IT/EN;
  - 422 per regole di dominio, 409 per conflitti, 404 per risorse mancanti;
  - niente più 500 o 503 per errori previsti.
- **Isolamento tenant:**
  - ospiti e 19 entità figlie filtrati per org in automatico, con test architetturale;
  - autorizzazione resource-based con policy centrali;
  - contesto org caricato in modo asincrono e sicuro in concorrenza.
- **Date:** tutto in UTC. "Oggi" si calcola su Europe/Rome.
- **Email:** Resend con template IT/EN, invio in coda Hangfire.
- **Storage:** Supabase Storage con bucket pubblico e bucket privato.
- **Feature flag:** le API OTA partner sono spente (D10). Spenta anche la discovery AI dei fornitori (D11).
- **AI:** budget controllato prima della chiamata, rate limit, niente note host nei prompt.
- **Sicurezza:**
  - fetch esterni anti-SSRF;
  - HTML sanificato con allowlist;
  - CORS esatto, header di sicurezza, CSP del frontend in Report-Only;
  - log senza PII.
- **Stripe:** webhook idempotenti (anche Connect); una sola subscription per org; stati `incomplete` e `unpaid` senza accesso.
- **CI:** frontend e backend con gate reali. Test d'integrazione su PostgreSQL.
- **CIN:** formato ufficiale verificato, un solo validatore.
- **Alloggiati:** stato onesto, invio manuale guidato, dati copiabili.
- **Prenotazioni host:** sorgente `Manual`, sempre Confermate, mai annullate in automatico.

Runbook in `docs/runbooks/`: alloggiati, auth0, ai, ci-backend, ci-frontend, cin-format, cors-security-headers, email, external-fetch, feature-flags, guest-tenant-migration, hangfire, health-checks, mobile-release, proxy-ip, storage, stripe, tenant-child-orgid-migration, tourist-tax-rates.

## Azioni esterne obbligatorie prima del deploy del branch

Senza queste l'API **non parte**: Railway tiene la versione precedente.

### Railway (test, poi prod)

| Variabile | Note | Runbook |
|---|---|---|
| `Cors__AllowedOrigins` | Origini esatte del frontend | cors-security-headers |
| `Cors__VercelPreviewPattern` | Solo su test | cors-security-headers |
| `Email__ApiKey`, `Email__FromAddress` | Dominio verificato su Resend | email |
| `App__PublicSiteBaseUrl` | Dominio pubblico (D3: configurabile) | email |
| `Storage__*` | Endpoint S3 Supabase, chiavi, nomi bucket | storage |
| `Hangfire__Schema` | Controllare prima `hangfire.server` su Supabase | hangfire |
| Segreti webhook Stripe reali (platform e Connect) | Col segnaposto il webhook risponde 500. Con chiave ristretta `rk_`, vedi i permessi nel runbook | stripe |

Impostazioni del servizio Railway (runbook health-checks):
- Healthcheck Path `/api/health/ready`;
- "Wait for CI" spento;
- verificare che l'ambiente test abbia ASPNETCORE_ENVIRONMENT corretto.

Reti del proxy Railway da verificare: runbook proxy-ip.

### Auth0 (runbook auth0)

- Applicazione M2M per la Management API (sync dei ruoli). Senza, il cambio ruolo risponde 502 `auth0_management_not_configured`.
- Riparare gli utenti con doppio ruolo che hanno già perso ruoli (§ 9 del runbook).
- Client **Native** per l'app mobile: arriva con MO-01, in corso.

### Supabase

- Creare i bucket pubblico e privato (runbook storage).
- Decidere se usare un progetto separato per la produzione (vedi decisioni).

### GitHub

- Rendere obbligatori i check CI su `develop` e `main`.
- Togliere dai required il check "pointer", se presente.

### Ambiente di sviluppo di questa sessione

Il proxy blocca `www.istat.it`, normattiva, Gazzetta Ufficiale, alloggiatiweb, agenziaentrate e i portali regionali (elenco nel § 8 di `.claude/context/regulations/istat-flussi-turistici.md`). Per sbloccare RS-6 servono i domini abilitati oppure il CSV ISTAT dei comuni.

## Decisioni richieste al product owner

### Bloccanti

| # | Tema | Domanda |
|---|---|---|
| B1 | FD-19 pulizia | Il sistema di permessi ha negato all'agente la rimozione di file tracciati. Serve un ok esplicito per rimuovere: `.idea/`, `StripeWebhookHandler.cs.bak`, `Migrations/AddContextAuthorization.sql` (dump, non migrazione), `Payments/{IPaymentGateway,StripeGateway,PaypalGateway}.cs` (vuoti), `Casazen.Web.http` (template weatherforecast), `.claude/settings.local.json` (permessi personali tracciati). |
| B2 | RS-6 ISTAT | Abilitare `www.istat.it` nell'ambiente oppure fornire il CSV dei codici comuni. |

### Prodotto

L'agente ha già implementato una scelta prudente: confermala o cambiala.

| # | Tema | Scelta attuale | Alternativa |
|---|---|---|---|
| P1 | Billing `past_due` | Grace di 7 giorni (`Billing:PastDueGraceDays`, spec AC6) | 0 = blocco immediato |
| P2 | Prenotazioni host rimaste Pending col vecchio codice | Marcate Manual, non confermate: ognuna la conferma l'host | Conferma in blocco con la migrazione |
| P3 | Token Auth0 nel browser | Resta in localStorage: la sessione sopravvive al reload | Cache in memoria + refresh token: più sicuro, serve un custom domain Auth0, altrimenti logout al reload |
| P4 | Alloggiati, soggiorno ≤ 1 notte senza orari | Trattato come "≤ 24 ore", quindi termine di 6 ore | Termine di 24 ore |
| P5 | Alloggiati, stato "Inviato manualmente" | Dichiarazione dell'host con data; conta come chiuso | — |
| P6 | Alloggiati, storico dei vecchi "Submitted" | Portato a "Da inviare manualmente": resta nel cockpit finché l'host non lo segna | Marcatura massiva come "inviato manualmente" |
| P7 | Piano Pro regalato prima di FD-18 | Torna Starter | Piano omaggio o pilota concesso dall'admin; comunicazione agli utenti |
| P8 | Link relativi nei contenuti SEO/HTML | Rimossi (solo http, https, mailto assoluti) | Ammettere i link interni |
| P9 | AI (DeepSeek) | Spenta finché non si compilano sede e base giuridica del trasferimento extra-UE | Attivarla implica la riconferma dei subprocessori da parte degli host; budget solo di piattaforma (500k token al mese) o anche per org/piano? |
| P10 | Rate limit | Lettura 120/min, status e lookup 30/min, iCal 60/min, registrazioni 5 ogni 10 min, check-in fornitore 20/min | Valori diversi |
| P11 | Lingua delle email | Sempre italiano: i destinatari non hanno ancora una preferenza di lingua, ma i testi inglesi sono pronti | Salvare la lingua del destinatario (utente, ospite, fornitore) |
| P12 | Terminologia | "Landlord" = Locatore | Proprietario |
| P13 | Check-in ospite (CO-02) | Documento mascherato (\*\*\*\*\* + ultime 3 cifre); dopo il completamento il link mostra solo lo stato | Mostrare anche il nome struttura |
| P14 | Checkout guest (BK-01) | Telefono facoltativo; paese di residenza senza preselezione | Preselezione Italia |
| P15 | Onboarding PLG | "Prima prenotazione" conta solo le prenotazioni dirette dal sito, non le manuali | Contare anche le manuali |
| P16 | Stato CIN nell'API pubblica | Il campo `cinStatus` è nel DTO pubblico; il codice CIN è mostrato all'ospite solo se valido | Togliere `cinStatus` dall'API pubblica |
| P17 | Supabase | Un solo progetto con schema `casazen_test` / `casazen_prod` (come oggi) | Progetto prod separato (consigliato dagli agenti per storage e segreti) |
| P18 | Accesso alle org via ruoli JWT PropertyManager/Admin (TN-3) | Autorizzati da ruolo | Permessi espliciti su DB |
| P19 | Ospiti (TN-1) | Senza prenotazioni → org di quarantena `casazen-unassigned`; DELETE = soft delete + anonimizzazione se solo passate, 409 se ci sono prenotazioni aperte | — |

### Per legale, commercialista e DPO (non bloccanti per lo sviluppo)

| Area | Domande | Riferimento |
|---|---|---|
| Alloggiati | Identificazione de visu (CdS 9101/2025) vs self check-in | `.claude/context/regulations/alloggiati.md` |
| Sicurezza (D.L. 145/2023) | 10 domande sulla checklist (estintori, gas/CO, impianti) | `sicurezza.md` |
| RLI / firma | Chi paga ~12,90 € a pratica; obblighi FEA; professionista depositante | `docs/integrations/rli-esign.md` |
| Fiscale | OSS, reverse charge, soglia cedolare per contribuente | `fiscale.md` |
| Canone concordato MB | 19 discrepanze sull'accordo 15/03/2024: domande all'associazione firmataria | `canone_concordato.md` |
| ISTAT / Ross1000 / CIR | 6 domande (ruolo privacy, credenziali Ross1000, sanzioni, ambiente di test ARIA) | `istat-flussi-turistici.md`, `cir-lombardia.md` |
| Imposta di soggiorno | Confermare sui PDF ufficiali Milano, Como, Firenze, Napoli; Milano 9,50 € solo 2026 | `docs/runbooks/tourist-tax-rates.md` |
| DPO | Dati sensibili nelle copie di backfill degli ospiti (TN-1); ConsentRecord vecchi con IP inaffidabile (FD-10) | runbook guest-tenant-migration, proxy-ip |

## Prossima ondata

Funzionalità, priorità ai P0 rimasti:
- **Tassa di soggiorno:** BK-03, con estensione del modello `TouristTaxRate` per categorie, stagioni e percentuali (RS-7).
- **Fornitori:** SU-01 (invito e registrazione).
- **LTR:** LT-01 (RLI), LT-02 (firma), LT-03 (template), LT-04 (scadenze), LT-05 (landlord solo-LTR).
- **Compliance:** CO-12 (N ospiti per soggiorno).
- **SEO:** SE-02 (dominio canonico).

Poi il resto dei P1 per area (PL, PC, BK, SU, CO, MO, LT, SE) e infine i task FN di chiusura.
