# Integrazione di CasaZen con ChatGPT e Claude: piano di lavoro (Wave 8)

> **Stato**: piano. Nessuna implementazione, nessun codice, nessuna configurazione esterna. Data del documento e delle consultazioni: **2026-10-01**.
> **Richiesta del PO (2026-10-01)**: "Prevedi, al termine degli sviluppi già previsti, di integrare CasaZen con ChatGPT e Claude, con i nuovi plugin che si integrano direttamente con l'interfaccia di OpenAI e Anthropic. Cerca la documentazione sulle ultime versioni dei plugin."
> **Task**: `AI-01` … `AI-12`, tutti in `Sessions/risanamento/tasks.json` (wave 8), da eseguire **dopo** tutti i task già pianificati, FN-01…FN-05 inclusi. Il protocollo di esecuzione resta `Sessions/risanamento/AGENT-PROTOCOL.md`; le decisioni vincolanti sono `DECISIONI.md`.
> **Relazione con altri documenti**: `docs/AI-STRATEGY.md` descrive l'AI *dentro* CasaZen (CasaZen paga l'inferenza). Questo documento descrive l'AI *fuori* da CasaZen: l'host usa il proprio ChatGPT o Claude e questi si collegano a CasaZen. Nessun costo di inferenza per CasaZen: lo sostiene il piano ChatGPT/Claude dell'host.

**Legenda del livello di verifica** (usata in tutto il documento, in particolare nell'Appendice A):

| Etichetta | Significato |
|---|---|
| **[V]** | Verificato il 2026-10-01 leggendo direttamente la pagina ufficiale, il registro dei pacchetti (npm, NuGet) o il repository ufficiale. |
| **[E]** | Pagina ufficiale **non raggiungibile** dalla rete di sessione (dominio bloccato, non aggirato). Il contenuto arriva dall'estratto del motore di ricerca che cita quella pagina. Va riverificato in AI-01. |
| **[S]** | Fonte secondaria (blog, articoli, forum, issue di terzi). Solo indicativa. |

## Indice

0. [Sintesi](#0-sintesi)
1. [Obiettivo e casi d'uso](#1-obiettivo-e-casi-duso)
2. [Architettura proposta](#2-architettura-proposta)
3. [Sicurezza e privacy](#3-sicurezza-e-privacy)
4. [Percorso di pubblicazione](#4-percorso-di-pubblicazione)
5. [Task AI-*](#5-task-ai-)
6. [Domande aperte per il PO](#6-domande-aperte-per-il-po)
- [Appendice A: ricerca documentale](#appendice-a-ricerca-documentale)
- [Appendice B: fonti](#appendice-b-fonti)

---

## 0. Sintesi

**Scelta**: **un solo server MCP remoto** (Streamable HTTP) dentro il backend .NET, **una sola UI MCP Apps** e **due pacchetti di distribuzione** (Claude: connettore + plugin bundle con Skills; ChatGPT: plugin "With MCP" + skills, distribuibile anche in Codex). Ciò che resta specifico per canale è poco e ben delimitato (Appendice A.7).

**Fatti chiave verificati** (dettagli e URL in Appendice A):

1. **Anthropic** [V]: due percorsi di sottomissione dal portale `claude.ai/directory/manage`: *MCP connector* (server remoto, `https://`) e *Plugin bundle* (repository GitHub con `.claude-plugin/plugin.json`, `claude plugin validate`, scansione di sicurezza, revisione di una persona sulla nuova inserzione). Se si ha un server MCP remoto si sottomettono **entrambi**. Possono sottomettere i piani Pro, Max, Team, Enterprise (su Team/Enterprise un Owner). Il connettore finisce di default come **Community** senza azioni; la revisione dei plugin non ha tempi fissi.
2. **MCP 2026-07-28** [V]: core stateless, `initialize` e `Mcp-Session-Id` eliminati, CIMD standard, DCR deprecata (funziona ancora, sarà rimossa), estensioni formali (MCP Apps, Tasks, EMA). **Claude ha annunciato il supporto "a breve" senza date** [V]: oggi la sua documentazione di autenticazione segue le specifiche 2025-03-26, 2025-06-18 e 2025-11-25. Il server deve parlare **entrambe** le generazioni di protocollo. L'SDK C# 2.x lo consente (modalità ibrida stateless/stateful in 2.2.0) [V].
3. **MCP Apps** [V]: specifica stabile datata 2026-01-26; client elencati nel README ufficiale: ChatGPT, Claude, VS Code, Goose, Postman, MCPJam, mcp-use, Alpic. Esiste il pacchetto NuGet ufficiale `ModelContextProtocol.Extensions.Apps` (2.2.0, 2026-08-13). Lo stesso `ui://` resource serve Claude e ChatGPT; cambia solo `ui.domain` per Claude (SHA-256 dell'URL del server, 32 caratteri esadecimali, più `.claudemcpcontent.com`; riprodotto localmente l'esempio della documentazione).
4. **OpenAI** [E]: il 2026-07-09 le "ChatGPT apps" sono diventate "plugins" e la App Directory è diventata Plugin Directory (condivisa da ChatGPT e Codex). La sottomissione avviene dal portale in OpenAI Platform: verifica d'identità (individuo o azienda), permesso `api.apps.write`, opzione **With MCP**, URL di produzione `/mcp`, scansione dei tool, verifica del dominio (`/.well-known/openai-apps-challenge`), CSP esatta, credenziali per i revisori, **5 test positivi e 3 negativi**, annotazioni di ogni tool con giustificazione. Tutto da riverificare: `developers.openai.com` e `help.openai.com` sono bloccati dalla rete di sessione.
5. **Regole di contenuto OpenAI** [E] che pesano su CasaZen: commercio solo per beni fisici, **nessuna vendita di servizi digitali/abbonamenti né promozione di upgrade di piano nel plugin**; niente dati PCI, PHI, **identificativi governativi** o credenziali. **Regola Anthropic** [V]: niente software che trasferisce denaro o esegue transazioni finanziarie.
6. **Auth0** [E]: come authorization server per MCP funziona con CIMD (registrazione manuale), DCR (impostazione di tenant, da evitare in produzione) e parametro `resource` (profilo di compatibilità). Nessuna conferma diretta che i metadata di Auth0 dichiarino ciò che serve a Claude per scegliere CIMD: **spike in AI-01/AI-03**.
7. **Da non dare per acquisito**: la conversione automatica delle app approvate in plugin Codex (non confermata; risulta una directory universale condivisa), la data esatta del 25/09/2026 per il portale Anthropic (la documentazione non la riporta), la disponibilità dei plugin per utenti in SEE/Italia (nessuna fonte conclusiva trovata).

**Rischi principali** (dettaglio nelle sezioni 3 e 5):

| Rischio | Mitigazione prevista |
|---|---|
| Prompt injection da testi di ospiti, calendari iCal importati dalle OTA, messaggi dei fornitori | Dati non fidati marcati e troncati, nessun tool di invio verso terzi, scritture solo con conferma fuori dal modello (§3.1, §3.4) |
| Dati personali verso OpenAI/Anthropic (GDPR) | Allowlist per campo, mai documenti d'identità né dati Alloggiati, opt-in per org, parere DPO (§3.2, §3.3) |
| Policy di directory (pagamenti, upsell di piano, identificativi governativi) | Esclusi per progetto; matrice di conformità (§3.6) |
| Auth0 non espone ciò che serve a Claude/ChatGPT per CIMD o per `iss` RFC 9207 | Scala di ripiego: CIMD, credenziali predefinite, DCR solo con OK del PO (§2.3) |
| Documentazione OpenAI non verificabile da qui | AI-01 apre con la riverifica; nessuna sottomissione prima (§5) |
| Review esterne senza tempi garantiti | Beta privata prima della pubblica, un solo catalogo di tool congelato (§4) |

---

## 1. Obiettivo e casi d'uso

### 1.1 Obiettivo

Permettere a un host CasaZen di chiedere in linguaggio naturale, dentro ChatGPT o Claude, lo stato della propria attività e di compiere poche azioni a basso rischio, **con gli stessi permessi che ha nell'app**: stessa autenticazione Auth0, stesse policy TN-3, stesso `HostScope`, stesso filtro tenant. L'assistente è un **altro client dell'API**, non una scorciatoia.

Priorità: **lettura prima, bozze poi, scrittura per ultima**, sempre con conferma umana fuori dal modello e idempotenza.

### 1.2 Casi d'uso per l'host/gestore (canale primario)

| ID | Richiesta tipica | Tool (§2.4) | Fase | Sensibilità |
|---|---|---|---|---|
| UC-01 | "Come sono le prenotazioni di domani?" (arrivi, partenze, ospiti, stato) | `get_arrivals_departures` | R | Media (nome ospite ridotto) |
| UC-02 | "Quali immobili ho e in che stato sono?" | `list_properties` | R | Bassa |
| UC-03 | "Il 12 e il 13 luglio il bilocale è libero?" / calendario del mese | `check_availability`, `get_calendar` | R | Bassa |
| UC-04 | "Quali immobili hanno il CIN mancante o non valido?" | `get_cin_compliance` | R | Bassa |
| UC-05 | "A che punto è Alloggiati per la prenotazione X? Cosa manca?" | `get_alloggiati_status` | R | Alta (solo stato e nomi dei campi mancanti) |
| UC-06 | "Che richieste ho aperte con i fornitori?" | `list_service_requests` | R | Bassa |
| UC-07 | "Ho richieste da approvare?" | `list_pending_approvals` | R | Media |
| UC-08 | "Come sto andando questo mese?" (KPI) | `get_dashboard_kpis` | R | Bassa |
| UC-09 | "Riepilogo tassa di soggiorno e cedolare secca dell'anno" (sola lettura, informativo) | `get_tax_summary` | R | Media (aggregati) |
| UC-10 | "Scrivimi un messaggio di benvenuto per l'ospite di domani" (bozza) | `get_guest_message_context` | B | Media |
| UC-11 | "Approva la richiesta di Mario Rossi del 5 agosto" | `prepare_booking_decision` + conferma | W | Alta (con conferma) |
| UC-12 | "Apri una richiesta di pulizia per la prenotazione X" (D2: breve = `bookingId`, lungo = proprietà) | `prepare_service_request` + conferma | W | Media (con conferma) |

Fasi: **R** lettura, **B** bozza (nessuna scrittura su CasaZen: il testo lo compone l'assistente a partire da fatti minimi e **non viene mai inviato** da CasaZen), **W** scrittura con conferma.

### 1.3 Esclusi per progetto (non sono "da fare dopo")

| Escluso | Motivo |
|---|---|
| Pagamenti, rimborsi, addebiti, Stripe Connect, segna come pagato | Policy Anthropic: niente transazioni finanziarie [V]. Guardrail di prodotto anche per OpenAI |
| Documenti d'identità, numeri di documento, scansioni, codice fiscale dell'ospite, dati delle parti dei contratti LTR | Dati personali ad alto rischio; OpenAI vieta identificativi governativi [E]; minimizzazione GDPR |
| Invio Alloggiati/Questura, "segna come inviato manualmente", credenziali Questura | Atto con valore legale; D6: mai "Inviato" senza ricevuta reale |
| Firma e registrazione dei contratti (RLI), cancellazioni, GDPR erasure, gestione utenti, ruoli, piano e fatturazione | Irreversibili o amministrativi: restano nell'app |
| Invio di email/SMS agli ospiti | Canale di esfiltrazione e di injection; FD-13 impone template e coda Hangfire |
| Modifica prezzi, regole di prezzo, tasse (`TaxRate`) | Impatto economico; fuori da v1 |
| Contenuti liberi scritti dall'ospite (note, richieste speciali) | Ingresso principale della prompt injection; esclusi in v1 (§3.1) |

### 1.4 Canali ospite e fornitore: valutazione

| Canale | Valutazione | Proposta |
|---|---|---|
| **Ospite** | Gli ospiti non hanno account Auth0: accedono con link a token o email (BK-11). I dati sono di terzi. Check-in e documenti non devono mai passare da una chat. Prenotare e pagare dentro il chatbot cade nelle restrizioni su transazioni e commercio. | **Fuori da v1.** Al massimo, in una v2, uno strumento di *scoperta* che rimanda al sito pubblico (SEO/funnel). Decisione del PO (§6, AI-02) |
| **Fornitore** | Esiste il ruolo `Supplier` (`SupplierOrgId`) e la console. Casi: "quali richieste ho?", "accetta/completa". Valore medio, ma utenti con doppio ruolo (host e fornitore) complicano la visibilità dei tool e lo snapshot di review. | **Fuori da v1.** Se serve, un secondo connettore (path `/mcp/supplier`, scope e audience propri, inserzione separata) dopo la GA host |

---

## 2. Architettura proposta

### 2.1 Decisione: modulo `Casazen.Mcp` dentro il processo del backend

**Proposta**: nuovo progetto `Casazen.Mcp` (libreria con tool, filtri, handler di autenticazione, asset UI) referenziato da `Casazen.Web`, montato su `/mcp` con l'SDK C# ufficiale (`ModelContextProtocol.AspNetCore`, versione corrente 2.2.0, target net8/net9/net10 [V]). **Non** un servizio separato in v1.

| Criterio | Dentro `Casazen.Web` (proposto) | Servizio separato |
|---|---|---|
| Riuso di DI, `ITenantOwned`, middleware tenant (TN-4), `InactiveAccountMiddleware` (PL-03), policy TN-3, `HostScope` | Immediato, nessun bypass possibile | Va duplicata la composizione, con rischio di divergenza dei controlli |
| Connessioni al database (Supabase) | Un solo pool | Pool doppio: pesa sui limiti del piano Supabase |
| Deploy | Un solo servizio Railway | Secondo servizio, secondo ambiente, secondo health check |
| Isolamento del carico e del "blast radius" | Condiviso: mitigato da rate limit dedicati e flag | Migliore |
| Estrazione futura | Il progetto separato la rende meccanica | n/a |

**Criteri per estrarre in futuro** (da scrivere nell'ADR di AI-01): p95 dell'API web degradato da traffico MCP, incidente di sicurezza che richiede isolamento, esigenza di una release indipendente.

Le tool call chiamano **i servizi applicativi esistenti in-process** (`IBookingService`, `IHostDashboardService`, `PropertyService`, `FiscalService`, `ServiceRequestService`…), **mai** le API REST via HTTP e mai direttamente il `DbContext`. Ogni tool dichiara, con un attributo, la policy che richiede (`CasazenPolicies.*`) e il filtro di tool la valuta con `IAuthorizationService` sul `ClaimsPrincipal` del token. La singola risorsa usa `IsAuthorizedAsync(User, HostResource, XxxOperations.Read|Write)`; le liste usano `User.GetHostScope(orgId)` filtrato in SQL. **I servizi non controllano i ruoli** (protocollo TN-3).

Un test architetturale `McpToolArchitectureTests` (stile `EndpointAuthorizationArchitectureTests`) fallisce se un tool: non ha policy, non ha `title` e annotazioni, ha un nome oltre 64 caratteri, restituisce un'entità invece di un DTO dedicato, non scrive audit, o non è nel catalogo documentato.

Protocollo: **Streamable HTTP**, endpoint unico `/mcp`. Modalità **stateless** (nessuna affinità di sessione, compatibile con più istanze Railway e con MCP 2026-07-28) con ibrido per i client 2025-11-25 (`HttpServerSessionMode`, SDK 2.2.0 [V]). Nessun CORS aperto su `/mcp` (le chiamate vengono dall'infrastruttura di Anthropic e OpenAI, non dal browser). Validazione dell'header `Origin` quando presente. TLS terminato da Railway (HTTP semplice nel container, come da `infra.md`).

### 2.2 Feature flag, kill-switch, rate limit

| Livello | Meccanismo | Note |
|---|---|---|
| Globale | Flag FD-20 `AiAssistants` in `Casazen.Core/Features/FeatureFlags.cs` e `Features:AiAssistants` (default spento). Un middleware di gate risponde 404 su `/mcp` e sugli endpoint `.well-known` dedicati quando è spento | `[FeatureGate]` vale per gli endpoint MVC: per `MapMcp` serve un gate equivalente |
| Scrittura | Secondo flag `AiAssistantsWrite` (default spento) | I tool `prepare_*` e `confirm_*` non vengono nemmeno registrati se spento |
| Per organizzazione | `OrgAssistantSettings` (`ITenantOwned`): `Enabled`, versione del consenso, chi e quando, `WriteEnabled`. **Default spento**: opt-in dell'amministratore dell'org (`OrgBillingAdmin`) | Spento: ogni tool risponde con errore neutro "non attivo per la tua organizzazione" (niente upsell: §3.6) |
| Rate limit | Per utente (`sub`), per org e per tool, sul modello di `AiRequestRateLimiter`. **Non per IP**: le richieste arrivano dagli indirizzi di Anthropic/OpenAI (documentati: egress Anthropic `160.79.104.0/21` [V]) | Risposta 429 ProblemDetails con `rate_limited` e `Retry-After`; limiti in `RateLimiting:Mcp:*` |
| Dimensioni | Proposta: massimo 50 elementi per risposta e risposta sotto ~20.000 caratteri (limite dell'host: ~150.000 caratteri [V]) | Meno costo e meno esposizione |

### 2.3 Autenticazione: Auth0 come authorization server

**API dedicata** in Auth0: un nuovo "API" con identificatore uguale all'URL esatto del server MCP (es. `https://<host-mcp>/mcp`, dominio configurabile come da D3, nessun default nel codice), distinta dall'audience dell'API web (`https://casazen-api` in `appsettings.json`). Effetto: un token MCP **non** vale sull'API web e viceversa (il `ValidAudience` dell'API web non cambia). **Mai passare il token MCP a servizi a valle** (confused deputy): i servizi sono in-process.

Il server pubblica il **Protected Resource Metadata** (RFC 9728) e risponde `401` con `WWW-Authenticate: Bearer resource_metadata="…"` (Claude richiede il 401 e ignora l'header su un 200 [V]). Requisiti di Claude [V]: `resource` identico all'URL come lo inserisce l'utente; `authorization_servers` con l'issuer di Auth0 **per primo** (usa solo il primo); metadata dell'authorization server raggiungibili da Anthropic; endpoint di discovery, registrazione e token entro 10 secondi (refresh 30); `/token` accetta `application/x-www-form-urlencoded`; PKCE S256 annunciato in `code_challenge_methods_supported`; rotazione dei refresh token per i client pubblici; errore `invalid_grant` a refresh non valido; `offline_access` richiesto solo se annunciato.

I due endpoint pubblici nuovi (`/.well-known/oauth-protected-resource` e, per OpenAI, `/.well-known/openai-apps-challenge`) sono **pubblici per progetto** e vanno nell'allow-list motivata di `EndpointAuthorizationArchitectureTests` (regola `security.md`: nessun endpoint senza JWT salvo pubblico esplicito).

**Come si registra il client (scala di ripiego, da decidere in AI-01/AI-03 con uno spike su un tenant di test):**

| Ordine | Opzione | Claude [V] | ChatGPT [E] | Note |
|---|---|---|---|---|
| 1 | **CIMD** (Client ID Metadata Document) | Usato solo se i metadata dell'AS annunciano `client_id_metadata_document_supported: true` **e** `none` in `token_endpoint_auth_methods_supported` | Preferito quando l'AS lo supporta | È la direzione della spec 2026-07-28 [V]. Auth0 offre la registrazione CIMD manuale (importa il documento da URL) [E]: da verificare che i suoi metadata annuncino ciò che Claude cerca |
| 2 | **Client predefinito/credenziali tenute dal provider** | `oauth_anthropic_creds`: si crea un client confidenziale in Auth0 e si scrive a `mcp-review@anthropic.com`; il segreto non va per email | "Predefined OAuth clients" supportati | Niente registrazione dinamica; richiede coordinamento |
| 3 | **DCR** (Dynamic Client Registration) | Supportata di default | Supportata | In Auth0 è un'impostazione di **tenant** (Advanced), richiede il profilo di compatibilità per `resource`, connessioni a livello di dominio e crea client "terzi" [E]. Apre la registrazione sul tenant degli utenti di produzione: **non attivare senza OK esplicito del PO** (§6, AI-03) |

Altri punti da verificare nello spike: il parametro **`resource` (RFC 8707)** deve diventare l'audience del token (in Auth0, "Resource Parameter Compatibility Profile" [E]); **RFC 9207 (`iss`)**: ChatGPT usa il redirect stabile `https://chatgpt.com/connector_platform_oauth_redirect` solo se l'AS annuncia `authorization_response_iss_parameter_supported: true` [E]; redirect di Claude `https://claude.ai/api/mcp/auth_callback` e loopback con porta qualsiasi per Claude Code [V]; **OpenAI accetta solo OAuth 2.1** (niente chiavi API o header statici) [S].

**Mappatura ruoli, tenant e `HostScope` sui token:**

| Elemento CasaZen | Dove sta | Come lo risolve il server MCP |
|---|---|---|
| Utente | `sub` del token | `ClaimsPrincipalExtensions.GetUserId` |
| Organizzazione (tenant) | **Non nel token**: `User.OrgId` (una org per utente; `SupplierOrgId` a parte) | Middleware TN-4 → `IRequestTenantContext.SetOrgId`; filtro globale `ITenantOwned` |
| Ruoli (`PropertyOwner`, `PropertyManager`, `Staff`, `Admin`) | Claim `https://casazen.app/roles` solo se l'Action lo aggiunge anche per l'audience MCP | **Autorevole è il DB** (`ContextAuthorizationService`, membership). Attenzione: `GetHostScope` usa i ruoli del JWT (`HostRoles.OrgWide`). Senza claim un PropertyManager vedrebbe solo i propri immobili (fallimento sicuro, ma funzionalmente diverso). In AI-03: Action anche per l'audience MCP **oppure** backfill dal DB come per `Supplier`, più un **test di parità** tra token web e token MCP dello stesso utente |
| Contesti (`short-rent`, `long-rent`) e permessi | DB | Policy `RequireContext:*` valutate da `IAuthorizationService` |
| Utente disattivato | DB | `InactiveAccountMiddleware` (PL-03): 403 `account_inactive` anche su `/mcp` |
| Cosa può fare l'assistente | `scope` del token | `casazen:read`, `casazen:write` (grossolani). I permessi fini restano ruoli e membership |
| Quale assistente ha chiamato | `azp`/`client_id` | Audit (campo `client`) |

**Permesso effettivo** = scope del token ∩ permessi dell'utente (policy) ∩ impostazione dell'org ∩ flag globale ∩ fase del tool. Se uno manca: niente.

**Revoca**: disattivare l'org o l'utente blocca subito (controlli a ogni chiamata, nessuna cache lunga); in più revoca dei grant e dei refresh token dell'utente in Auth0 tramite Management API (già usata da FD-14; endpoint e permessi M2M da verificare in AI-03).

Enterprise Managed Auth (SSO senza consenso, grant JWT bearer) [V] è **fuori da v1**: non supporta DCR e richiede supporto dell'AS; valutabile per clienti Enterprise.

### 2.4 Catalogo dei tool v1 e mappatura sugli endpoint esistenti

Nomi in inglese, `snake_case`, massimo 64 caratteri; **descrizioni dei tool in inglese** (le leggono modelli e revisori, e devono descrivere cosa fa il tool senza istruire il modello [V]); **risultati ed errori localizzati** IT/EN (`IStringLocalizer`, `.resx`). Eccezione motivata alla regola i18n sui testi statici dei tool. Ogni tool restituisce `structuredContent` (per la UI) **e** `content` testuale (per gli host senza UI, Claude Code incluso [V]).

| Tool | Fase | Servizio/endpoint esistente | Policy TN-3 | Annotazioni | Dati ammessi (allowlist) |
|---|---|---|---|---|---|
| `list_properties` | R | `GET api/properties` (`PropertyService`, `HostScope`) | `SharedPropertyRead` | readOnly | id, nome, città, stato annuncio, stato CIN |
| `get_arrivals_departures` | R | `GET api/booking` con filtro date (`HostBookingService`, `HostScope`); "oggi" con `RomeCalendar` | `BookingRead` | readOnly | codice prenotazione, immobile, nome ospite ridotto (nome e iniziale del cognome), numero ospiti, date di soggiorno senza ora, stato, origine, stato pagamento (enum) |
| `get_booking` | R | `GET api/booking/{id}` + risorsa host | `BookingRead` + `IsAuthorizedAsync` | readOnly | come sopra più notti; **niente** contatti, note ospite, importi per prenotazione |
| `get_calendar` | R | `GET api/booking/calendar` con `PropertyOccupancy` (BK-05) | `BookingRead` | readOnly | per immobile e notte: libera/occupata/bloccata e tipo di fonte, senza nomi |
| `check_availability` | R | `PropertyOccupancy` (nessuna logica nuova) | `BookingRead` | readOnly | disponibile sì/no, tipo di sovrapposizione |
| `list_pending_approvals` | R | `GET api/bookings/approval-requests` (BK-06) | `BookingRead` | readOnly | codice, immobile, nome ridotto, date, scadenza della richiesta |
| `get_cin_compliance` | R | `GET api/properties/cin-compliance` (MO-12, `HostScope`) | `PropertyRead` | readOnly | immobile, stato CIN (valid/missing/invalid, calcolato in lettura), CIN normalizzato, scadenze |
| `get_alloggiati_status` | R | `GET api/alloggiati/summary`, `/{id}/status`, `/guest-progress` | `BookingRead` | readOnly | stato (D6: onesto, es. "da inviare manualmente"), conteggi, **nomi** dei campi mancanti. **Mai** valori, numeri di documento, file |
| `list_service_requests` | R | `GET api/service-requests` (scope di servizio; solo affitti brevi, LTR escluso in v1) | `PropertyRead` | readOnly | id, categoria, immobile, codice prenotazione (D2), stato, date, ragione sociale del fornitore |
| `get_dashboard_kpis` | R | `GET api/dashboard/kpis` | `BookingRead` | readOnly | KPI già aggregati |
| `get_tax_summary` | R | `GET api/fiscal/reports/annual/{anno}` e `reports/tourist-tax` | `RequireContext:short-rent:property.read` | readOnly | totali aggregati per immobile e anno, con etichetta "informativo, non consulenza fiscale"; nessun dato per ospite, nessuna aliquota scritta nel tool (`TaxRate`) |
| `get_guest_message_context` | B | composizione di prenotazione e immobile | `BookingRead` | readOnly | fatti minimi (nome di battesimo, date, immobile, lingua, orari di check-in/out). Il testo lo scrive l'assistente. **Nessun invio** |
| `prepare_booking_decision` / `confirm_booking_decision` | W | `POST api/bookings/{id}/approve` e `/decline` | `BookingWrite` + `IsAuthorizedAsync(Write)` | prepare: da decidere in AI-01 (effetto reale: riga effimera). confirm: destructive | riepilogo dell'azione e `confirmationId` |
| `prepare_service_request` / `confirm_service_request` | W | `POST api/service-requests` (D2) | `PropertyWrite` | confirm: idempotent, non destructive | riepilogo e `confirmationId`. `chargeToGuest` resta rifiutato (decisione non presa) |

**Dettagli di progetto:**

- **DTO dedicati**: mai riusare i DTO delle API web. Ogni campo esposto è nell'**allowlist** del catalogo (`docs/integrations/ai-assistants-tool-catalog.md`, creato in AI-01/AI-02), che contiene anche la giustificazione delle annotazioni richiesta da OpenAI [E] e i casi di test.
- **Date**: `DateTime` UTC internamente; date di soggiorno senza ora; "domani" calcolato in Europe/Rome (`RomeCalendar`); nessun `DateTime.Today`.
- **Errori**: `DomainRuleException`/`NotFoundException`/`DomainConflictException` (FD-05) tradotti in errori MCP azionabili con `code` stabile e testo localizzato, mai "Internal Server Error" generico (rifiutato dalla review [V]); mai dettagli interni.
- **Un tool, una semantica**: lettura e scrittura sempre in tool separati (un tool "misto" viene rifiutato [V]).
- **Versionamento**: solo modifiche additive al catalogo; una modifica incompatibile crea un nuovo tool (`…_v2`) e ritira il vecchio dopo una finestra dichiarata. `serverInfo.version` semantico. Un **test di snapshot** su `tools/list` (nomi, descrizioni, schemi, annotazioni) fallisce a ogni modifica non voluta: OpenAI parla di "review snapshot" e manutenzione delle versioni [E], quindi anche una modifica di descrizione può richiedere una nuova revisione.
- **Skill che richiamano i tool**: seguono lo stesso catalogo congelato (AI-06).

### 2.5 Scritture: conferma fuori dal modello, idempotenza, audit

La conferma non può dipendere dal modello. Anche in Claude un tool destructive chiede sempre conferma [V], ma l'utente può aver scelto "Always allow"; quindi serve una prova di intenzione umana verificabile dal server.

| Schema | Funzionamento | Uso |
|---|---|---|
| **A. Conferma in chat con la UI** | `prepare_*` salva sul server il payload e restituisce `confirmationId` e il riepilogo; il widget mostra il riepilogo **letto dal server** e il pulsante "Conferma", che chiama `confirm_*`, un tool con `_meta.ui.visibility: ["app"]`. La specifica MCP Apps dice che l'host **non** deve metterlo nell'elenco dei tool del modello [V]: solo la UI può chiamarlo | Host con supporto MCP Apps (Claude chat/desktop/mobile, ChatGPT) |
| **B. Conferma fuori banda in CasaZen** | `prepare_*` restituisce un link profondo; la conferma avviene nell'app o nel web di CasaZen (anche con notifica push, MO) | Host senza UI (Claude Code) e ripiego |
| ~~C. `confirm_*` visibile al modello, affidato all'approvazione dell'host~~ | Non accettabile da sola | Scartato |

`confirm_*` riceve **solo** il `confirmationId` (non gli argomenti): ciò che l'utente ha visto è ciò che viene eseguito. `confirmationId` legato a utente, org, tool e hash del payload; **monouso**; scadenza breve (proposta: 10 minuti); la seconda conferma restituisce l'esito della prima (idempotenza). Eseguire l'azione ripassa da servizio e autorizzazione come l'API REST. Limiti: massimo N conferme per utente/ora e per org/giorno (`RateLimiting:Mcp:Write:*`), mai operazioni in blocco.

**Audit** (`AssistantToolAuditEntry`, `ITenantOwned`): org, utente (`sub`), tool, client (`azp`), esito (ok/negato/errore/rate-limited), durata, hash degli argomenti, ID delle entità toccate, correlation id, data UTC. **Nessun payload, nessun testo di ospiti, nessuna PII.** Retention configurabile con job Hangfire (default prudente 90 giorni, §6). I log applicativi seguono la stessa regola (`ILogger` con ID, mai valori).

Migrazioni EF solo con `dotnet ef migrations add … --project Casazen.Infrastructure --startup-project Casazen.Web` (protocollo). Nuove entità `ITenantOwned` (test `TenantQueryFilterArchitectureTests`).

### 2.6 UI: widget MCP Apps

Regole dell'host Claude [V]: scheda inline con al massimo 2 azioni e 4-5 dati, senza menu, popover, drill-in o scroll annidato; carousel da 3 a 8 elementi; schermo intero per grafici e dashboard; tema chiaro e scuro con i token dell'host; WCAG AA; target touch da almeno 44 pt; skeleton al posto degli spinner; su mobile Claude usa una WebView nativa (nessuna fotocamera, microfono, posizione). Quindi i widget sono **piccoli e mirati**, non l'app web in un iframe.

| Widget | Modalità | Contenuto |
|---|---|---|
| Arrivi e partenze | Scheda inline / carousel | Elenco con stato; apre CasaZen con `ui/open-link` verso domini dichiarati |
| Disponibilità e calendario | Inline (striscia) e schermo intero (mese) | Notti libere/occupate/bloccate |
| Stato conformità | Scheda inline | CIN e Alloggiati per immobile e prenotazione, senza dati personali |
| Conferma di un'azione | Scheda inline | Riepilogo letto dal server e pulsante "Conferma" (tool app-only, §2.5) |

**Riuso dal frontend**: il widget è un **entry Vite separato** nel repository `frontend` (es. `src/mcp-apps/`), costruito in file HTML autocontenuti (asset inline). Si riusano componenti di presentazione, token di design e chiavi i18n (`it.json`/`en.json`, `t()`, nessun `defaultValue`). **Non** si riusano router, store Zustand, client Axios o Auth0: il widget riceve i dati da `structuredContent` e chiama solo tool del server tramite l'host. Stati di caricamento, errore e vuoto sempre distinti (regola FE: un errore non è mai "lista vuota"). Nessun `connectDomains`; CSP dichiarata esatta per risorsa (`_meta.ui.csp`); nessuna libreria da CDN in produzione (la documentazione di Claude consiglia di servire lo script dalla propria origine o di includerlo nell'HTML, elencando solo quell'origine [V]). Il backend serve i file come risorse `ui://` con MIME `text/html;profile=mcp-app` tramite `ModelContextProtocol.Extensions.Apps`; la pipeline di build ne fissa la versione (hash) per evitare disallineamenti.

`ui.domain` per Claude = primi 32 caratteri esadecimali dello SHA-256 dell'URL del server + `.claudemcpcontent.com`. **Riproducibilità**: ricalcolato in locale l'esempio della documentazione (`https://example.com/mcp` → `c3d80a4ed901ee05b21755a88273b4a4.claudemcpcontent.com`, corrisponde). Va calcolato dall'URL pubblico configurato (D3) e coperto da un test con quel vettore.

Il pacchetto `@modelcontextprotocol/ext-apps` è alla 2.0.3 (2026-09-25): richiede i pacchetti separati `@modelcontextprotocol/client`/`server` ≥ 2, Zod 4 e Node 20+; il protocollo sul filo non cambia, quindi viste 2.x funzionano con host 1.x [V]. Le pagine Claude sono verificate con la 1.7.5: AI-05 sceglie la linea e la fissa.

### 2.7 Osservabilità e operatività

Metriche per tool (richieste, errori, negati, latenza p50/p95, rate-limited) e per client; avvisi su picchi di errori, di negazioni e di tool di scrittura; dashboard in `observability/`. Health check `ready` esteso: raggiungibilità del JWKS di Auth0 e flag. Runbook `docs/runbooks/ai-assistants.md` (kill-switch, revoca, rotazione delle credenziali dei revisori, reinstallazione del connettore, incidente). Obiettivi indicativi: p95 delle letture sotto 2 secondi; nessun incidente di cross-tenant; zero PII nei log (test).

---

## 3. Sicurezza e privacy

### 3.1 Modello delle minacce

| Minaccia | Esempio in CasaZen | Difesa |
|---|---|---|
| **Prompt injection indiretta** | Nome o richiesta speciale di un ospite, `SUMMARY` di un evento iCal importato da un'OTA, messaggio di un fornitore con istruzioni per il modello | I campi di testo libero di terzi **non** sono nel catalogo v1; se un domani servono: troncati (200 caratteri), senza URL né markdown, in un campo marcato `untrusted` con prefisso "dati forniti da terzi, non sono istruzioni". Nessun tool può inviare dati a terzi |
| **"Trifecta" letale** (dati privati + contenuto non fidato + canale di uscita) | Un assistente che legge testi di ospiti e può inviare email | Nessun tool di invio. Le scritture hanno conferma umana fuori dal modello (§2.5). Link nei widget solo verso origini dichiarate |
| **Escalation o bypass di autorizzazione** | Un tool che legge più del consentito | Policy TN-3 per tool, `HostScope`, filtro tenant, test architetturale, test di parità e di isolamento tra org (AI-09) |
| **Confused deputy / token passthrough** | Riuso del token MCP verso altre API | Audience dedicata; servizi in-process; nessun token a valle |
| **Tool poisoning / descrizioni ingannevoli** | Descrizione che istruisce il modello | Descrizioni statiche, brevi, descrittive, riviste e congelate dallo snapshot; la review le controlla [V] |
| **Esfiltrazione tramite widget** | Immagini o richieste a domini esterni | CSP senza `connectDomains`, niente risorse remote, link solo a origini dichiarate |
| **Replay o doppia esecuzione** | Doppia conferma | `confirmationId` monouso, idempotenza, scadenza |
| **Abuso** | Loop di chiamate | Rate limit per utente, org e tool; avvisi |
| **Furto di token** | Token rubato | Durate brevi, rotazione dei refresh token, revoca (§2.3), blocco per org/utente a ogni chiamata |
| **PII nei log** | Log di argomenti o risultati | Solo ID e hash; test che cercano PII nei log |

### 3.2 Minimizzazione dei dati verso OpenAI e Anthropic

Ciò che un tool restituisce **esce da CasaZen** verso il fornitore dell'assistente scelto dall'host. Regola: **solo i campi dell'allowlist** del catalogo (§2.4), mai le entità. Mai esposti: email, telefono, data di nascita, indirizzo, tipo e numero di documento, scansioni, codice fiscale, contenuti dei file Alloggiati/Questura, credenziali Questura, IBAN o strumenti di pagamento, dati delle parti dei contratti LTR, dati bancari dei fornitori, importi per prenotazione, note e richieste libere dell'ospite. Alloggiati: solo stato, conteggi e **nomi** dei campi mancanti. Il nome dell'ospite è ridotto a nome e iniziale. I dati dei documenti d'identità non sono mai nel catalogo: è una scelta di progetto di questo piano, coerente con il divieto OpenAI sugli identificativi governativi [E].

### 3.3 GDPR, DPA, informativa, sub-responsabili (impatto su PL-14)

**Ipotesi di lavoro, non decisione legale** (D14: i testi ToS/Privacy/DPA li fornisce il PO; gli agenti non li scrivono): l'host è titolare dei dati dei propri ospiti; CasaZen è responsabile; quando l'host collega ChatGPT o Claude, **CasaZen comunica dati a un terzo su istruzione dell'host autenticato** (il consenso OAuth). Da stabilire con il DPO:

1. Qualificazione di OpenAI e Anthropic (destinatari scelti dall'host, responsabili dell'host, titolari autonomi a seconda del piano, consumer o business/enterprise, uso per addestramento, conservazione presso il fornitore).
2. Se vadano nell'**elenco dei sub-responsabili** di PL-14 (CasaZen non li ingaggia: li sceglie l'host) o in una sezione distinta "destinatari su iniziativa dell'host" dell'informativa e del DPA.
3. Trasferimenti extra-SEE (Stati Uniti) e base giuridica (nessun dato dedotto: nessuna regione assunta; PL-14 ha già segnalato che per Auth0 la regione dedotta dal codice è US mentre `docs/runbooks/auth0.md` prescrive tenant EU).
4. Necessità di una DPIA e aggiornamento del registro dei trattamenti.
5. Diritti degli interessati: CasaZen non può cancellare una conversazione presso OpenAI o Anthropic; va scritto nell'informativa.
6. Avvisi di trasparenza AI (ruolo di CasaZen rispetto all'AI Act: non fornitore del chatbot; verificare se le bozze di messaggi agli ospiti richiedano un'indicazione).

**Infrastruttura che costruiscono i task** (AI-08, AI-11): consenso **per organizzazione**, versionato e tracciato (chi, quando, versione), prima di ogni uso; nuova versione dei documenti che richiede nuova accettazione (infrastruttura PL-02/PL-14); schermata "Assistenti AI" con elenco delle categorie di dati esposti e link alle pagine legali; finché i testi non esistono, la pagina mostra "in preparazione" e l'attivazione resta riservata alle org in lista (beta).

### 3.4 Limiti di scrittura

Nessuna scrittura in GA v1 senza decisione del PO (§6). In beta: solo UC-11 e UC-12, con conferma fuori dal modello, idempotenza, tetti per utente/org, audit, flag `AiAssistantsWrite` e `WriteEnabled` per org. Nessuna azione irreversibile oltre alla decisione su una richiesta di prenotazione (verificare in AI-04 che `approve`/`decline` non muovano denaro: se sì, restano fuori).

### 3.5 Consenso, disattivazione e revoca

| Evento | Effetto |
|---|---|
| Org attiva l'integrazione | Registro del consenso con versione; i tool rispondono per quell'org |
| Org la disattiva | Tutte le chiamate dell'org falliscono subito; i grant dell'org vengono revocati |
| Utente disattivato | 403 `account_inactive` (PL-03) |
| Utente si scollega dall'assistente | Revoca grant e refresh token; nuova autorizzazione richiesta |
| Incidente | Flag globale `AiAssistants` spento: 404 su `/mcp` e `.well-known`; runbook |

### 3.6 Matrice di conformità alle policy dei canali

| Requisito | Fonte | Come lo rispetta CasaZen |
|---|---|---|
| Niente trasferimenti di denaro o transazioni finanziarie | Anthropic [V] | Nessun tool di pagamento, rimborso, payout, segna-pagato (§1.3) |
| Raccogliere solo i dati necessari; privacy policy con categorie, finalità, destinatari, conservazione, controlli | Anthropic [V], OpenAI [E] | Allowlist; informativa del PO (D14) con URL pubblico (§4.3) |
| Niente istruzioni nelle descrizioni dei tool; titolo e annotazioni su ogni tool; nomi ≤ 64; separare lettura e scrittura; errori azionabili; risposte di dimensione ragionevole; chiamare solo API proprie | Anthropic [V] | §2.4; test architetturale |
| Nessuna pubblicità o contenuto sponsorizzato | Anthropic [V] | Nessuno |
| Non interrogare memoria o cronologia di Claude | Anthropic [V] | Nessun accesso |
| Commercio solo di beni fisici; **nessuna vendita di servizi digitali o abbonamenti, nessun piano mostrato, nessun upgrade promosso** | OpenAI [E] | L'integrazione non mostra né vende piani. Se l'org non è abilitata: errore neutro. Il prezzo/piano dell'integrazione è fuori dal plugin (§6, AI-11) |
| Niente dati PCI, PHI, identificativi governativi, credenziali | OpenAI [E] | §3.2 |
| Annotazioni accurate con giustificazione; scansione del server di produzione; dominio verificato; CSP esatta; 5 test positivi e 3 negativi; credenziali dei revisori senza MFA | OpenAI [E] | AI-12, AI-10 |
| Dichiarare le 7 conferme di conformità (linee guida, API proprie, transazioni finanziarie, media generati da AI, prompt injection, raccolta dei dati di conversazione, documentazione pubblica) | Anthropic [V] | AI-10 con revisione legale |

---

## 4. Percorso di pubblicazione

### 4.1 Claude

| Percorso | Cosa serve | Note |
|---|---|---|
| **Privato: connettore personalizzato** | URL del server. Team/Enterprise: un Owner lo aggiunge in *Organization settings > Connectors > Add > Custom*; Free/Pro/Max: *Customize > Connectors > Add custom connector* (Free: uno) [V] | Nessuna review. Mostra l'avviso "non verificato". Ideale per beta con host selezionati, ognuno aggiunge l'URL |
| **Privato: rollout di un plugin all'organizzazione** | Repository marketplace con `.claude-plugin/marketplace.json`; *Organization settings > Plugins & skills*; disponibilità "Installed by default/Required" [V] | Per il team CasaZen e i suoi test. Sincronizza dal branch di default |
| **Pubblico: connettore MCP nel directory** | Portale `claude.ai/directory/manage` > Submit new > MCP connector. URL `https://`, tool con `title` e annotazioni, nome (≤100 caratteri), frase (≤200), descrizione (≤2.000), 1-5 categorie, URL documentazione, URL privacy policy, contatto di supporto, icona, slug (**permanente**), casi d'uso, azienda, autenticazione, gestione dei dati, **account di test per i revisori**, 7 conferme; per MCP App: **3-5 screenshot PNG** larghi almeno 1000 px, ritagliati sulla sola risposta, con il prompt a parte [V] | Scansione automatica e, di default, etichetta **Community** senza altre azioni; l'escalation a **Verified** è decisa da Anthropic. Contatti: `mcp-review@anthropic.com` |
| **Pubblico: plugin bundle** | Repository GitHub (**pubblico prima della pubblicazione**), `.claude-plugin/plugin.json` (`name` minuscolo, `displayName`, `version`, `description`, `author`, `license`), `.mcp.json` con **lo stesso URL** del connettore, `skills/`, `README.md` ≥ 40 parole (codice escluso), `LICENSE`; `claude plugin validate`, poi **Validate** nel portale; domande sulla gestione dei dati; scansione di sicurezza a ogni commit; revisione di una persona per una nuova inserzione; **Publish**; aggiornamenti con merge sul branch seguito e `version` incrementata [V] | Si sottomettono **entrambi** e si abbinano se dalla stessa org. Ordine: connettore poi plugin. Contatto: `directory@anthropic.com` |

Superfici [V]: chat (web, desktop, mobile), Cowork, Claude Code. Le Skills si caricano ovunque; il server remoto compare nella scheda Connectors; agenti e hook solo in Cowork e Claude Code; le MCP App si vedono solo in chat (Claude Code mostra il testo).

Requisiti sull'account: piano Pro, Max, Team o Enterprise; su Team/Enterprise sottomette un **Owner**; l'inserzione appartiene all'organizzazione che sottomette, quindi serve un'org Anthropic della società giusta (§6, AI-10). Limite di 10 sottomissioni per org ogni 24 ore: irrilevante.

### 4.2 ChatGPT e Codex (tutto [E]: da riverificare in AI-01)

| Percorso | Cosa serve | Note |
|---|---|---|
| **Privato: modalità sviluppatore** | *Settings > Security and login > Developer mode* (dipende dalla policy dell'workspace); *Plugins > +*, nome e descrizione, URL pubblico `/mcp` o tunnel sicuro | Per i test interni. Gli admin dell'workspace abilitano lo sviluppo e i connettori MCP personalizzati |
| **Privato: pubblicazione nell'workspace** | Un plugin pubblicato nell'workspace **non** va nella directory universale e resta nei confini dell'organizzazione | Beta con clienti Business/Enterprise |
| **Pubblico: Plugin Directory** | Verifica d'identità in OpenAI Platform (individuo o azienda) prima di sottomettere; owner dell'organizzazione o permesso `api.apps.write`; bozza del plugin con **With MCP**: URL di produzione `/mcp` (non un segnaposto), scansione dei tool, **verifica del dominio** con token in `/.well-known/openai-apps-challenge`, CSP esatta, configurazione OAuth o credenziali dei revisori **senza MFA, SMS, codici email, VPN**, **5 casi positivi e 3 negativi**, giustificazione di ogni annotazione; "Publish" dopo l'approvazione | Esiste anche un percorso "solo skills" (ZIP) e uno per convertire un plugin Claude Code. Il plugin approvato compare nella directory condivisa da ChatGPT e Codex |
| **Tempi** | Nessuna garanzia. Fonte secondaria: metà delle decisioni entro 13 giorni; rifiuti in pochi giorni; approvazioni anche in mesi [S] | Pianificare margini ampi |

**Disponibilità in SEE e in Italia**: non verificata. Una pagina di aiuto sui connettori riporta che alcune funzioni non sono disponibili in SEE, Svizzera e Regno Unito [S] e le "dots" escludono Pro nel SEE [S]. Se i plugin non fossero disponibili per gli host italiani, il lancio parte da Claude. Verifica obbligatoria in AI-01 e domanda al PO (§6, AI-10).

### 4.3 Checklist trasversale di sottomissione

- **Documenti pubblici**: informativa privacy e termini (testi del PO, D14) con **URL pubblico stabile**, pagina di supporto con email, pagina di documentazione d'uso (IT e EN). Deve dichiarare categorie di dati, finalità, destinatari, conservazione e controlli (§3.3).
- **Dominio**: URL del server MCP stabile e di produzione (D3: configurabile, nessun default nel codice); dominio del server verificato per OpenAI; link ammessi per Claude solo su origini di CasaZen.
- **Account di test per i revisori** (AI-12): organizzazione demo isolata con **dati fittizi completi** (immobili, prenotazioni, CIN, Alloggiati, richieste), utente dedicato **senza MFA** (vincolo OpenAI [E]; anche Claude chiede credenziali per un account "completamente popolato" [V]), accessi in sola lettura salvo le azioni di prova, reset periodico, credenziali ruotate a ogni sottomissione, istruzioni passo-passo. Mai dati reali.
- **Asset grafici**: icona e logo del connettore; per Claude 3-5 screenshot PNG di almeno 1000 px per la MCP App, con prompt abbinati, ritagliati sulla sola risposta; modello Figma ufficiale disponibile [V]. Formati richiesti da OpenAI: da verificare in AI-01.
- **Testi dell'inserzione** in IT e EN (nome, frase, descrizione); nome e slug definitivi (§6).
- **Casi di test per OpenAI**: 5 positivi e 3 negativi, scritti nel catalogo dei tool.
- **Conferme di conformità** (7 per Claude): lette e dichiarate dal PO o dal legale.

### 4.4 Fasi di rilascio

| Fase | Ambito | Uscita (gate) |
|---|---|---|
| **0. Interna** | Ambiente di test, flag spento in produzione, connettore personalizzato in Claude e modalità sviluppatore in ChatGPT con org di prova | Tutti i tool di lettura funzionano su entrambi gli host; test AI-09 verdi |
| **1. Beta privata** | Produzione con flag acceso solo per org in lista, **sola lettura**; testi legali del PO pubblicati; consenso per org attivo | Nessun incidente; zero PII nei log; feedback; DPO ok |
| **2. Pubblico Claude** | Sottomissione connettore e plugin (sola lettura, più scritture solo se decise); poi OpenAI (se disponibile in IT) | Approvazione; monitoraggio |
| **3. Scritture e GA** | `AiAssistantsWrite` per le org che lo scelgono | Esito delle scritture e audit verificati |
| **Ripiego** | Flag spento; delist (Claude: Delist plugin; OpenAI: ritiro) | Runbook |

---

## 5. Task AI-*

Tutti in `Sessions/risanamento/tasks.json` con `wave: 8` e `findings: []` (non chiudono difetti dell'audit: i totali 166 task e 335 difetti restano riferiti al risanamento). **Nota sull'ID**: `AI-xx` (task di questa integrazione) non va confuso con gli ID dei difetti d'audit `A1-xx`.

**Dipendenze comuni a tutti**: i task già pianificati, FN-01…FN-05 inclusi (FN-05 dipende da tutti gli altri, quindi la copre); per la parte privacy PL-14 e i testi legali del PO (`LEGAL-TEXTS`: non è un task del registro, è un gate esterno, D14). **Attenzione allo scheduler**: `sched.py` interpreta `deps: ["*"]` come "tutti gli ID non `FN-`"; con i nuovi ID i task FN-01, FN-02 e FN-05 aspetterebbero i task AI e questi aspettano FN-05 (ciclo). In questo task lo snapshot `stato/` è stato allineato: `stato/tooling/sched.py` esclude anche gli ID `AI-` dal carattere jolly, `stato/tasks.json` contiene i nuovi task e `stato/status.json` li segna `pending` (senza queste voci `sched.py ready` solleva `KeyError`). **La copia viva dello scheduler e il suo `status.json` vanno allineati allo stesso modo dall'orchestratore.**

**Stime** in giorni-persona di lavoro effettivo (agente o sviluppatore), **indicative, ±40%**, escluse le attese di review esterne.

| ID | Titolo | Repo | Dipende da | Stima |
|---|---|---|---|---|
| **AI-01** | Ricerca di conferma, spike tecnici e design (ADR-004, catalogo tool) | backend | FN-01, FN-02, FN-03, FN-04, FN-05 | 5 gg |
| **AI-02** | Server MCP read-only (`Casazen.Mcp`, tool di lettura, UI-less) | backend | AI-01, FD-20, TN-3, TN-4 | 10 gg |
| **AI-03** | OAuth con Auth0 per il server MCP | backend | AI-02, FD-14, PL-11 | 6 gg |
| **AI-11** | Impostazioni organizzazione: consenso, attivazione, disattivazione, revoca (BE+FE) | backend, frontend | AI-03, PL-02, PL-12, PL-14 | 6 gg |
| **AI-04** | Tool di bozza e di scrittura con conferma e idempotenza | backend | AI-02, AI-03, AI-11, BK-06, SU-07 | 8 gg |
| **AI-05** | UI MCP Apps (widget) | frontend, backend | AI-02, AI-04 | 10 gg |
| **AI-06** | Plugin bundle Claude con Skills | backend | AI-05 | 4 gg |
| **AI-07** | Plugin/app ChatGPT (con Codex) | backend | AI-05 | 5 gg |
| **AI-08** | Privacy, DPA e legale: infrastruttura e catalogo dei dati | backend, frontend | AI-02, PL-14 | 5 gg |
| **AI-12** | Account demo per i revisori, asset e documentazione pubblica | backend, frontend | AI-05, AI-08 | 4 gg |
| **AI-09** | Test e sicurezza (injection, isolamento tenant, contratto, carico, e2e sugli host) | backend, frontend | AI-06, AI-07 | 8 gg |
| **AI-10** | Sottomissione e rollout (beta, Claude, ChatGPT) | backend | AI-08, AI-09, AI-11, AI-12 | 5 gg + attese |

Totale ≈ **76 giorni-persona**. Percorso critico ≈ AI-01, AI-02, AI-03, AI-11, AI-04, AI-05, AI-06/07, AI-09, AI-10 ≈ 63 giorni: circa 13 settimane lavorative con due persone o agenti in parallelo sui rami indipendenti (AI-08, AI-12, AI-06 con AI-07), più le attese di review.

### AI-01. Ricerca di conferma, spike tecnici e design

- **Scopo**: chiudere ciò che qui è [E] o non verificato, provare le parti rischiose e congelare il design.
- **Lavoro**: (1) riverificare ogni fatto [E] su `developers.openai.com`, `help.openai.com`, `platform.openai.com`, `modelcontextprotocol.io`, `auth0.com` (richiede rete aperta o documenti caricati; vedi §6, AI-01); (2) **spike Auth0 su tenant di test**: CIMD (i metadata annunciano `client_id_metadata_document_supported` e `none`?), `resource`→audience, RFC 9207 `iss`, DCR, durata e rotazione dei refresh token, claim dei ruoli per l'audience MCP; (3) **spike SDK C# 2.2** su .NET 10: stateless e ibrido, handler di autenticazione e PRM, accesso al `ClaimsPrincipal` nei tool, filtri, annotazioni, `Extensions.Apps`; (4) **server "hello" in sola lettura** collegato a un connettore personalizzato Claude e alla modalità sviluppatore ChatGPT su ambiente di test, osservando i prompt di conferma reali; (5) verificare la disponibilità di ChatGPT plugins per utenti italiani; (6) catalogo tool v1 definitivo con allowlist dei campi; (7) `docs/adr/ADR-004-ai-assistants-mcp.md`.
- **Accettazione**: ogni riga [E] dell'Appendice A diventa [V] o resta dichiarata non verificabile con ripiego; ADR approvata; catalogo `docs/integrations/ai-assistants-tool-catalog.md` con campi, policy, annotazioni e giustificazioni; esito scritto dello spike Auth0 con la scala di §2.3 scelta; risposte del PO alle domande bloccanti (§6).
- **Rischi**: rete ancora bloccata (ripiego: documenti PDF/estratti forniti dal PO, ma resta [E]); Auth0 non soddisfa CIMD e RFC 9207 (si ricade su credenziali predefinite: ritardo di coordinamento con i provider); l'SDK non espone l'utente ai tool come previsto.

### AI-02. Server MCP read-only

- **Lavoro**: progetto `Casazen.Mcp`; mount `/mcp`; flag `AiAssistants` e gate; tool di lettura del catalogo (R e B); DTO e allowlist; `McpToolArchitectureTests`; snapshot di `tools/list`; rate limit per utente/org/tool; `AssistantToolAuditEntry` e migrazione; errori localizzati IT/EN con `.resx` (test `SharedResourcesLocalizationTests`); metriche; health check; schema di autenticazione **di test** per i test di integrazione (quello reale è AI-03). Proposta di aggiornamento di `.claude/rules/integrations.md` (serve l'OK del PO per toccare i file di regole).
- **Accettazione**: tutti i tool R rispondono con policy TN-3 e `HostScope`; test di isolamento tra org e tra ruoli (PropertyOwner, PropertyManager, Staff, landlord LTR senza short-rent); nessun campo fuori allowlist (test sui DTO); zero PII nei log e nell'audit (test); flag spento: 404; "domani" corretto in Europe/Rome con `FakeTimeProvider`; `dotnet test`, `dotnet format --verify-no-changes`, nessun warning.
- **Rischi**: tool che aggira un filtro per errore (mitigato dai test architetturali); carico che pesa sull'API web (rate limit e metriche); pool di connessioni.

### AI-03. OAuth con Auth0 per il server MCP

- **Lavoro**: API Auth0 "MCP" (audience = URL del server); scope `casazen:read`/`casazen:write`; PRM e 401 con `resource_metadata`; handler JWT per l'audience MCP; Action (o backfill dal DB) per i ruoli; registrazione del client secondo la scala scelta in AI-01; `/.well-known/openai-apps-challenge`; test di parità dei permessi web/MCP; revoca di grant e refresh token; runbook `docs/runbooks/ai-assistants.md` (configurazione Auth0: **passi per il PO**, D9); allow-list degli endpoint pubblici.
- **Accettazione**: connessione end-to-end da Claude (connettore personalizzato) e da ChatGPT (modalità sviluppatore) con login reale su tenant di test; un token web non funziona su `/mcp` e viceversa; utente disattivato o org spenta: negati; refresh e rotazione funzionanti; tutte le impostazioni esterne documentate.
- **Rischi**: DCR come unica via (si ferma e si chiede al PO); regione del tenant (EU/US); latenze oltre 10 secondi sui metadata.

### AI-11. Impostazioni organizzazione: consenso, attivazione, disattivazione, revoca

- **Lavoro**: `OrgAssistantSettings` (`ITenantOwned`) e migrazione; endpoint `OrgBillingAdmin`; pagina FE "Assistenti AI" (attiva/disattiva, categorie di dati esposti, link alle pagine legali, elenco delle connessioni e scollegamento); registro del consenso versionato (riuso dell'infrastruttura PL-02/PL-14); revoca dei grant alla disattivazione; i18n IT/EN (`t()`, nessun `defaultValue`); stati di caricamento, errore e vuoto; rotta in `ROUTE_MANIFEST` dietro `featureFlag`.
- **Accettazione**: per default spento; solo l'amministratore dell'org può attivare; ogni attivazione registra versione, utente e data; disattivare blocca subito i tool; test BE e vitest FE; senza testi legali del PO la pagina mostra "in preparazione" e l'attivazione resta limitata alle org in lista.
- **Rischi**: testi del PO in ritardo (gate di beta); interazione con PL-12 se si sceglie un piano a pagamento (§6).

### AI-04. Tool di bozza e di scrittura con conferma

- **Lavoro**: `AssistantPendingAction` e migrazione; `prepare_booking_decision`/`confirm_booking_decision`, `prepare_service_request`/`confirm_service_request`; conferma schema A (tool app-only) e schema B (link profondo, con notifica); `confirmationId` monouso legato a utente, org, tool e hash; idempotenza; scadenza; tetti per utente/org; flag `AiAssistantsWrite` e `WriteEnabled`; audit; verifica che `approve`/`decline` non muovano denaro; D2 per le richieste fornitore (breve: `bookingId`; lungo: proprietà).
- **Accettazione**: nessuna scrittura senza conferma valida; doppia conferma = stesso esito, nessun doppione; payload diverso da quello visto: impossibile; prompt injection di prova non riesce a confermare (AI-09); test con harness senza UI.
- **Rischi**: l'host non rispetta `visibility: ["app"]` (verificato in AI-01); azione ritenuta "finanziaria" dalla review (si esclude); UX della conferma fuori banda.

### AI-05. UI MCP Apps

- **Lavoro**: entry Vite `src/mcp-apps/` nel frontend, bundle HTML autocontenuti; widget di §2.6; stati di caricamento/errore/vuoto; tema chiaro/scuro con token dell'host; i18n IT/EN; accessibilità; `registerAppResource`/`Extensions.Apps` lato server; `ui.domain` per Claude con test sul vettore documentato; CSP esatta; pipeline di build con hash fissato; screenshot di base.
- **Accettazione**: i widget si vedono e funzionano in Claude (web, desktop, mobile) e in ChatGPT; senza UI il testo basta; nessuna richiesta di rete dal widget; Lighthouse/axe senza errori gravi; `tsc`, `eslint`, `vitest`, `vite build` verdi; test del widget di conferma contro il contratto di AI-04.
- **Rischi**: differenze tra host (display mode, `ui.domain`); dimensione dei bundle; cambio di linea di `ext-apps` (1.x contro 2.x).

### AI-06. Plugin bundle Claude con Skills

- **Lavoro**: cartella `integrations/ai-assistants-plugin/` nel repository backend (privato), da pubblicare in un **repository pubblico dedicato** prima del listing (organizzazione GitHub e licenza: §6, n. 11; il repository pubblico contiene solo il plugin): `.claude-plugin/plugin.json`, `.mcp.json` con l'URL configurabile (nessun segreto), `skills/` (briefing del giorno, controllo conformità CIN/Alloggiati, bozza messaggio all'ospite, richiesta a fornitore secondo D2, riepilogo fiscale informativo), `README.md` (cosa fa, cosa invia, dove), `LICENSE`; generazione dei manifest da un'unica sorgente per non divergere da AI-07; `claude plugin validate` in CI; test sulle tre superfici (chat, Cowork, Claude Code).
- **Accettazione**: `claude plugin validate` passa; **Validate** del portale senza blocchi; le Skills non istruiscono a chiamare strumenti non richiesti e non contengono dati normativi inventati (D14); funziona caricato come ZIP.
- **Rischi**: nome già preso o "simile a un'inserzione esistente" (revisione); `bin/` vietato; il repository deve essere pubblico.

### AI-07. Plugin/app ChatGPT (con Codex)

- **Lavoro**: pacchetto plugin per ChatGPT e Codex nella stessa cartella e dalla stessa sorgente di AI-06 (skills + MCP); formato del manifest da confermare (`.codex-plugin/plugin.json` e/o manifest Agent Plugins 1.0.0 `plugin.json` con `$schema`, `skills/`, `mcp.json`); bozza "With MCP"; casi di test 5+3; giustificazione delle annotazioni; configurazione OAuth; verifica del dominio.
- **Accettazione**: bozza completa nel portale senza errori di scansione (a livello di account di test); prova in modalità sviluppatore e in un workspace; manifest validi per i formati scelti.
- **Rischi**: documentazione non verificabile fino a AI-01; formato dei manifest in evoluzione (Agent Plugins nato da poco [S]); disponibilità in Italia.

### AI-08. Privacy, DPA e legale

- **Lavoro**: catalogo dei dati esposti ai tool come allegato per DPA e informativa; voci per l'elenco dei destinatari/sub-responsabili in PL-14 **senza inventare** ragione sociale, luogo o base di trasferimento (campi "in definizione", come PL-14); sezione "Assistenti AI" nelle pagine legali ("in preparazione" finché mancano i testi del PO); bozza del registro dei trattamenti e della DPIA (struttura, non conclusioni); URL pubblici di privacy, termini e supporto.
- **Accettazione**: ogni campo esposto ha una riga nel catalogo; l'elenco sub-responsabili/destinatari resta veritiero rispetto al codice; nessun testo legale scritto dall'agente; parere del DPO richiesto e registrato (non sostituito).
- **Rischi**: ritardo dei testi del PO; qualificazione GDPR diversa dall'ipotesi.

### AI-12. Account demo per i revisori, asset e documentazione pubblica

- **Lavoro**: org demo con dati fittizi completi (seed dedicato, mai dati reali), utente senza MFA, reset periodico con job, credenziali ruotabili; documentazione d'uso pubblica IT/EN; icona, logo, screenshot (3-5 PNG ≥ 1000 px per Claude); testi delle inserzioni; 5+3 casi di test di OpenAI scritti per il catalogo.
- **Accettazione**: un revisore esterno completa ogni caso di test con le sole istruzioni scritte; l'org demo non è raggiungibile dai dati di produzione; il reset funziona.
- **Rischi**: un utente senza MFA nel tenant di produzione è una superficie d'attacco (isolare, limitare, ruotare); formati degli asset non ancora noti per OpenAI.

### AI-09. Test e sicurezza

- **Lavoro**: red team di prompt injection (note ospite, iCal, messaggi fornitore) contro tutti i tool; test di isolamento tra org e tra ruoli; parità di permessi tra token web e MCP; test di contratto (snapshot del catalogo); test di carico e rate limit; ricerca di PII nei log e nell'audit; e2e in CI con un client MCP di prova (Inspector) e prove manuali documentate su Claude e ChatGPT; revisione di sicurezza (`security-review`); prova di revoca e di kill-switch.
- **Accettazione**: nessun tool restituisce dati oltre l'allowlist né di un'altra org; l'injection di prova non provoca scritture né esfiltrazioni; snapshot stabile; p95 entro obiettivo; elenco dei rischi residui.
- **Rischi**: flaky sotto carico (regola dei test dipendenti dal clock e dei `TimeProvider` fissi); ambienti esterni non automatizzabili.

### AI-10. Sottomissione e rollout

- **Lavoro**: beta privata (flag per org, connettori personalizzati e workspace); sottomissione del connettore e del plugin Claude dal portale; poi OpenAI se disponibile in Italia; risposta ai revisori; monitoraggio; runbook di operazione; decisione su scritture e GA (§6).
- **Accettazione**: gate di §4.4 rispettati; inserzioni pubblicate o motivazione dei rifiuti; metriche e avvisi attivi; piano di ripiego provato.
- **Rischi**: tempi delle review non controllabili; rifiuto per policy (pagamenti, identificativi, upsell: §3.6); titolarità degli account (§6).

---

## 6. Domande aperte per il PO

Sono anche in `Sessions/risanamento/DOMANDE-APERTE.md`, sezione 8 (formato `- [ ] AI-xx — domanda — default prudente — data`). Finché non c'è risposta si applica il default.

| # | Task | Domanda | Default prudente |
|---|---|---|---|
| 1 | AI-10 | Titolarità degli account: quale società possiede l'organizzazione Anthropic (piano Team/Enterprise, un Owner sottomette) e l'organizzazione OpenAI Platform (verifica azienda)? Chi sono Owner e firmatari delle conferme? | Account aziendale della società che gestisce CasaZen, mai personale; nessuna sottomissione finché non indicato |
| 2 | AI-02 | Quali ruoli usano l'assistente: PropertyOwner, PropertyManager, Staff, landlord LTR? Contratti LTR in lettura? | Gli stessi ruoli e policy dell'app; Staff solo lettura; **LTR escluso** (dati delle parti) |
| 3 | AI-04 | Quali azioni di scrittura in GA? | Nessuna in GA v1. In beta solo approvare/rifiutare richiesta on-site e aprire richiesta fornitore, con conferma fuori dal modello. Mai pagamenti, rimborsi, prezzi, invio messaggi, Alloggiati, cancellazioni |
| 4 | AI-02 | Canali ospite e fornitore: sì o no, e quando? | Solo host in v1 |
| 5 | AI-11 | Piani e prezzo: inclusa nei piani a pagamento, add-on o per tutti? OpenAI vieta di mostrare piani o promuovere upgrade nel plugin | Inclusa nei piani a pagamento esistenti; nessun riferimento a piani nel plugin (errore neutro) |
| 6 | AI-10 | Nome, slug (permanente su Claude), icona, dominio del server MCP (D3), testi IT/EN | Nome "CasaZen", testi IT/EN, nessun dominio scritto nel codice; si decide prima della sottomissione |
| 7 | AI-08 | Regione dati: tenant Auth0 EU o US? Base per il trasferimento verso OpenAI e Anthropic? | Nessun dato personale ospite oltre il minimo; parere DPO prima della beta con dati reali |
| 8 | AI-08 | Come qualificare OpenAI e Anthropic nel GDPR e dove metterli (sub-responsabili PL-14, DPA, informativa)? DPIA necessaria? | Elencati come "destinatari scelti dall'host" in attesa del parere; integrazione spenta di default |
| 9 | AI-12 | Account di test per i revisori: org demo nel tenant di produzione o tenant separato? Chi lo gestisce? | Org demo isolata nel tenant di produzione, utente dedicato senza MFA, credenziali ruotate a ogni sottomissione |
| 10 | AI-10 | I plugin ChatGPT sono disponibili per gli host italiani? Si parte comunque da Claude? | Prima Claude; ChatGPT dopo la verifica di AI-01 |
| 11 | AI-06 | Repository pubblico del plugin (richiesto prima del listing) e licenza (`LICENSE` obbligatoria)? Quale organizzazione GitHub? | Repository dedicato con solo manifest, Skills e README; licenza permissiva a scelta del PO; il codice del server resta privato |
| 12 | AI-02 | Retention dell'audit delle chiamate (senza PII)? | 90 giorni, configurabile; da confermare con il DPO (nessun periodo verificato) |
| 13 | AI-03 | È accettabile attivare DCR sul tenant Auth0 di produzione se CIMD e credenziali predefinite non bastano? | No: ci si ferma e si chiede |
| 14 | AI-02 | Il riepilogo fiscale/cedolare: solo aggregati già calcolati da CasaZen, con l'etichetta "informativo, non consulenza"? | Sì, nessuna raccomandazione e nessuna aliquota scritta nei tool |
| 15 | AI-02 | Note e richieste libere dell'ospite nei tool? | Escluse in v1 (prompt injection e minimizzazione) |
| 16 | AI-01 | Sbloccare per AI-01 `developers.openai.com`, `help.openai.com`, `platform.openai.com`, `modelcontextprotocol.io`, `auth0.com`, oppure fornire le pagine come documenti | AI-01 parte con le fonti [E] e le riverifica quando possibile |
| 17 | AI-10 | Lasciare ad Anthropic la decisione tra Community e Verified, e chiedere o no il percorso "Verified"? | Nessuna richiesta: Community di default |

---

## Appendice A: ricerca documentale

Tutte le consultazioni del **2026-10-01**. Elenco completo degli URL in Appendice B (i riferimenti `C*`, `M*`, `O*`, `A*` rimandano lì).

### A.1 Verifica delle affermazioni di partenza del PO

| Affermazione | Esito | Evidenza |
|---|---|---|
| A gennaio 2026 nasce l'estensione MCP Apps (SEP-1865): UI interattive in iframe, renderizzate da Claude, ChatGPT e altri | **Confermata nella sostanza [V]**; il numero SEP non è comparso nelle fonti lette | README di `ext-apps` (M2): "current stable specification is dated 2026-01-26", client: ChatGPT, Claude, VS Code, Goose, Postman, MCPJam, mcp-use, Alpic. Specifica (M3) |
| A luglio 2026 le "ChatGPT apps" diventano "plugins" | **Confermata [E]/[S]** | Il 2026-07-09: Codex integrato nell'app desktop ChatGPT, app rinominate plugin, App Directory sostituita dal Plugin Directory (O12, O10). Le pagine ufficiali `developers.openai.com/plugins/...` sono bloccate; `/apps-sdk` reindirizza alla sezione plugin [S]. Nel registro npm **non esiste** `@openai/apps-sdk` [V] |
| Le app approvate si convertono in plugin Codex | **Non verificata** | Le fonti dicono: directory universale condivisa da ChatGPT e Codex, "le connessioni delle app esistenti restano valide", plugin che includono skills, app e template. Nessuna fonte parla di conversione automatica. Esiste un percorso per sottomettere a OpenAI un plugin pensato per Claude Code (O8) |
| Spec MCP datata 2026-07-28 ("MCP 2.0", core stateless) | **Confermata [V]** | M1 (blog del progetto MCP, 28 luglio 2026) |
| Anthropic ha aperto il 25/09/2026 il portale `claude.ai/directory/manage` con due percorsi | **Percorsi e portale confermati [V]; la data non è riportata dalla documentazione** | C2, C3, C4. Articolo di terzi citato nei risultati di ricerca [S], non letto |
| `.claude-plugin/plugin.json`, `claude plugin validate`, scansione di sicurezza, revisione, Directory Terms/Policy | **Confermati [V]** | C2, C6, C3 |
| Pagine Claude indicate (indice, submit, build, checklist, directory/publish, connectors submission, submission-status) | **Tutte raggiungibili e lette [V]** | C1-C7, C17 |
| `developers.openai.com` bloccato | **Confermato**; non aggirato | Anche `help.openai.com`, `learn.chatgpt.com`, `platform.openai.com`, `modelcontextprotocol.io`, `auth0.com`, `vercel.com`, `x.com` e alcuni blog risultano bloccati in questa sessione (il PO li riteneva raggiungibili: `help.openai.com`, `platform.openai.com`, `modelcontextprotocol.io`). Raggiungibili: `claude.com`, `support.claude.com`, `github.com` (repository pubblici), `raw.githubusercontent.com`, `registry.npmjs.org`, `api.nuget.org`, `blog.modelcontextprotocol.io`, più WebSearch |

### A.2 Claude (Anthropic)

| Tema | Fatto | Fonte |
|---|---|---|
| Due tipi di inserzione | *Plugin bundle* (cartella con skill, comandi, agenti, hook, riferimenti a server MCP; da repository GitHub che deve essere pubblico prima della pubblicazione) e *MCP connector* (un server remoto, solo URL). Con un server remoto si sottomettono entrambi, e si possono abbinare dalla stessa org | C3, C2 |
| Chi sottomette | Pro, Max, Team, Enterprise; Free no. Team/Enterprise: un Owner (su Enterprise anche chi ha il permesso Directory). L'inserzione appartiene all'org; il primo che sottomette una cartella la possiede. MCPB/extension desktop: deprecati nel directory | C3 |
| Plugin: manifest | `.claude-plugin/plugin.json`: `name` minuscolo con trattini, max 64 caratteri, nome specifico e non riservato (`claude`, `anthropic`, `official`, `plugin`, `mcp`, `test`); `displayName`, `version` (da incrementare a ogni release), `description`, `author`, `license`. README ≥ 40 parole (codice escluso) e `LICENSE` o campo `license`. Nessun file `.DS_Store`; niente link simbolici; `bin/` impedisce l'installazione in claude.ai e Cowork. Server remoto: `type` `http`/`sse`/`ws` e URL `https://`; **nessun segreto** nei file | C6, C7, C8 |
| Plugin: esiti dei controlli | **Blocca**, **Trattenuto per un revisore**, **Avviso**, **Nota**. La scansione di sicurezza cerca comportamenti non dichiarati (invio dati altrove, codice nascosto, modifica dei permessi); sorgenti leggibili, README che descrive tutto | C6 |
| Plugin: flusso | Validate nel portale → dettagli → gestione dei dati → conformità (4 conferme) → invio; scansione a ogni commit; revisione di una persona sulla nuova inserzione; **Publish** (di default richiesta a un revisore Anthropic); aggiornamenti con merge sul branch/tag seguito, webhook GitHub opzionale; ritiro e relist; limite 10 sottomissioni/24 h | C2, C3 |
| Stati | Plugin: Draft, Scanning, Needs changes, In review, Approved, Published, Not live yet, Delisted, Withdrawn. Connettore: Draft, In review, Changes requested, Not approved, Approved, Published. Contatti: `directory@anthropic.com` (plugin), `mcp-review@anthropic.com` (connettore). I tempi di review **non sono fissi** | C17 |
| Connettore: requisiti | URL `https://`; OAuth 2.0 per i servizi con account, o nessuna autenticazione per dati pubblici; ogni tool con `title` e `readOnlyHint` o `destructiveHint`; test su Claude come connettore personalizzato; URL documentazione e privacy policy, contatto, icona; **account di test completamente popolato** per i revisori | C4 |
| Connettore: passi del portale | Connection, Tools, Listing (nome ≤100, frase ≤200, descrizione ≤2.000, 1-5 categorie, slug permanente), Use cases, Company, Authentication, Data handling, Test & launch, Compliance (**7 conferme**), Review. Per MCP App: 3-5 PNG ≥1000 px | C4 |
| Criteri di review dei tool | Separare lettura e scrittura (un tool "catch-all" è rifiutato); nomi ≤ 64; descrizioni accurate e non istruttive; niente istruzioni nascoste; errori azionabili; risposte di dimensione ragionevole; solo API proprie; no trasferimenti di denaro, no media AI. Annotazioni: read-only senza conferma, **destructive sempre con conferma** | C5 |
| Etichette | Community (default, scansione automatica), Verified (escalation decisa da Anthropic, test di ogni tool), Custom | C18, C5 |
| Policy | Niente transazioni finanziarie, niente pubblicità, solo dati necessari con privacy policy chiara, non interrogare memoria o file dell'utente | C19, C5 |
| Distribuzione privata | Connettore personalizzato per URL (nessuna review); rollout del plugin all'org da repository marketplace (*Organization settings > Plugins & skills*); disponibilità Not available / Available / Installed by default / Required | C10, C9, C8 |
| Superfici | Chat (web, desktop, mobile), Cowork, Claude Code. Skills ovunque; MCP remoto in chat dalla scheda Connectors; agenti e hook in Cowork e Code | C8 |
| Limiti | Risultato tool ~150.000 caratteri (Code: 25.000 token); timeout 240 s per chiamata. Streamable HTTP (SSE in deprecazione). Non supportati: sottoscrizioni a risorse, sampling, capacità avanzate o in bozza | C11 |
| OAuth | 401 con `resource_metadata` obbligatorio; `resource` = URL esatto; primo `authorization_servers`; metadata AS raggiungibili da `160.79.104.0/21`; DCR o CIMD (CIMD solo se `client_id_metadata_document_supported: true` e `none` tra i metodi); `oauth_anthropic_creds` su richiesta; `client_credentials` non supportato; PKCE S256; callback `https://claude.ai/api/mcp/auth_callback`; Claude Code: loopback con porta qualsiasi, CIMD proprio; timeout 10/30 s; token endpoint form-urlencoded; rotazione refresh; spec seguite 2025-03-26, 2025-06-18, 2025-11-25 | C12, C11 |
| Enterprise Managed Auth | Grant JWT bearer (RFC 7523) con asserzione dell'IdP del cliente; Team ed Enterprise; **non** con DCR | C13 |
| MCP 2026-07-28 in Claude | "Supporto in arrivo nei prossimi prodotti Claude", nessuna data; il blog cita oltre 950 server nel directory | C20 |
| MCP Apps in Claude | Chat/desktop/mobile; mobile con WebView; Claude Code solo testo; `ui.domain` = SHA-256 dell'URL (32 hex) + `.claudemcpcontent.com` (esempio riprodotto); `frameDomains` limitato; guide di design | C14, C15, C16 |

### A.3 ChatGPT / OpenAI (tutto [E] salvo diversa indicazione)

| Tema | Fatto | Fonte |
|---|---|---|
| Cosa sono oggi | "Plugins" (ex apps): pacchetti che raggruppano skills, app/MCP e UI; directory universale condivisa da ChatGPT e Codex; il portale rifiuta file `.mcpb`; la configurazione sul portale è in OpenAI Platform, non nell'workspace | O1, O10, O12 |
| Server MCP | Remoto, pubblico, URL stabile tipicamente `/mcp`; trasporto **Streamable HTTP**; SDK TypeScript e Python; la UI è opzionale e si aggiunge dopo | O5, O7, O11 |
| Annotazioni | Ogni tool deve impostare `readOnlyHint`, `openWorldHint`, `destructiveHint` in modo accurato **con una giustificazione**; "funzionalmente di sola lettura" non rende read-only un tool dichiarato non read-only | O2 |
| UI | Se il server restituisce UI, CSP con i domini esatti; esempi ufficiali usano `_meta.ui.resourceUri`; estensione `window.openai` ancora presente nell'esempio "kitchen sink" (M9 [V]); ChatGPT è elencato come client MCP Apps nel README di `ext-apps` [V] | O2, M9, M2 |
| Autenticazione | OAuth 2.1; PRM `/.well-known/oauth-protected-resource`; 401 con `WWW-Authenticate`; client: CIMD (preferito se supportato), DCR, client predefiniti, PKCE; redirect stabile `https://chatgpt.com/connector_platform_oauth_redirect` quando l'AS supporta RFC 9207; **solo OAuth** (niente chiavi API) [S] | O4, O12 |
| Sottomissione | Verifica d'identità (individuo o azienda) nel dashboard; permessi `api.apps.write` (scrivere) e `api.apps.read` (leggere); owner dell'org possono sottomettere; **With MCP**: URL di produzione, scansione dei tool aggiornata, **verifica del dominio** (`/.well-known/openai-apps-challenge`), CSP, credenziali o istruzioni OAuth per i revisori **senza MFA/SMS/email/VPN**, **5 test positivi e 3 negativi**; "Publish" dopo l'approvazione; "universal MCP server URL" salvo URL per workspace ("Template") | O1, O2 |
| Percorsi alternativi | Plugin solo skills (ZIP); conversione di un plugin Claude Code | O8, O1 |
| Pubblicazione privata | Un plugin pubblicato nell'workspace resta nell'workspace | O1 |
| Modalità sviluppatore | *Settings > Security and login > Developer mode*; *Plugins > +*; URL pubblico o tunnel; dipende dall'admin dell'workspace | O7 |
| Linee guida | Commercio solo per beni fisici; **no** vendita di prodotti o servizi digitali, abbonamenti, crediti, neanche via freemium; **no** piani mostrati né upgrade promossi; vietati adulti, gioco d'azzardo, droghe, farmaci con ricetta; privacy policy pubblica (categorie di dati, finalità, destinatari, conservazione, controlli); dati minimi; **no** PCI, PHI, identificativi governativi, credenziali | O3 |
| Sicurezza e privacy | Privilegio minimo, consenso esplicito per collegamento account e scrittura, difesa in profondità con prompt injection presunta, validazione server-side, conferma umana per le operazioni irreversibili, log senza PII | O6 |
| Tempi di review | Mediana ~13 giorni, rifiuti in pochi giorni, approvazioni anche mesi; ottobre 2026: nuovo flusso di sottomissione con tracciamento della review [S] | forum e siti di terzi |
| Disponibilità EU | Non verificata (§4.2) | [S] |
| Versioni SDK | Vedi A.5 | npm [V] |

### A.4 Standard: MCP, MCP Apps, Agent Plugins

- **MCP 2026-07-28** [V, M1]: richieste autodescrittive (versione e client in `_meta`), `initialize` e `Mcp-Session-Id` eliminati, `server/discover` opzionale, intestazione e cache (`ttlMs`, `cacheScope`) nelle liste; **autorizzazione**: `iss` RFC 9207, CIMD standard, **DCR deprecata** (compatibile, rimozione futura), credenziali legate all'issuer; estensioni formali (Tasks, MCP Apps, Enterprise Managed Authorization); finestre di deprecazione di 12 mesi per Roots, Sampling, Logging.
- **SDK C# 2.0.0 (2026-07-28)** [V, M5/M6]: supporto primario a 2026-07-28 con interoperabilità 2025-11-25 e precedenti tramite negoziazione, stateless per default, OAuth con PKCE S256 obbligatorio, `IdentityAssertionGrantProvider`, pacchetto `ModelContextProtocol.Extensions.Apps`. **2.1.0**: `subscriptions/listen`. **2.2.0**: `HttpServerSessionMode` per servire insieme client stateful e stateless. (Le date mostrate dal riassunto della pagina release erano errate: valgono quelle del registro NuGet.)
- **MCP Apps** [V, M2/M3]: tool con `_meta.ui.resourceUri`; **`visibility`** `["model","app"]` per default, `["app"]` nasconde il tool al modello (l'host **non deve** elencarlo); risorsa `ui://` con MIME `text/html;profile=mcp-app`; `_meta.ui.csp` (`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`), `permissions`, `domain`, `prefersBorder`; modalità `inline`, `fullscreen`, `pip`; l'app chiama i tool tramite l'host (`tools/call`), `ui/open-link`, `ui/message`, `ui/update-model-context`; iframe in sandbox con CSP restrittiva di default e registrazione delle chiamate dell'host.
- **Agent Plugins 1.0.0** [V per la specifica, S per l'adozione]: manifest `plugin.json` alla radice con `$schema` `https://agent-plugins.org/schemas/1.0.0/plugin.schema.json`, componenti portabili `skills/` e `mcp.json`; migrazione additiva dai formati proprietari (si tengono i file legacy). Fonti secondarie: promosso da Vercel con AWS, Cursor, GitHub, Microsoft, OpenAI; client al lancio ChatGPT, Codex, Cursor, GitHub Copilot, Kiro, VS Code. **Claude non risulta tra i client** nelle fonti lette; la documentazione Claude descrive `.claude-plugin/plugin.json`. Quindi: un solo sorgente (skills + URL MCP), manifest per canale generati.

### A.5 Versioni correnti (registri ufficiali, 2026-10-01) [V]

| Pacchetto | Ultima versione | Data | Note |
|---|---|---|---|
| `ModelContextProtocol` / `.AspNetCore` / `.Core` (NuGet) | **2.2.0** | 2026-08-13 | 2.0.0 il 2026-07-28, 2.1.0 il 2026-08-05; net8.0, net9.0, net10.0 (core anche netstandard2.0) |
| `ModelContextProtocol.Extensions.Apps` (NuGet) | **2.2.0** | 2026-08-13 | Estensione MCP Apps per .NET |
| `@modelcontextprotocol/sdk` (npm) | **1.31.0** | 2026-09-28 | Linea 1.x (1.30.0 il 2026-07-27) |
| `@modelcontextprotocol/server`, `/client` (npm) | **2.2.0** | n/d | Linea 2.x; `/node` 2.1.0 |
| `@modelcontextprotocol/ext-apps` (npm) | **2.0.3** | 2026-09-25 | 2.0.0 il 2026-09-08; richiede client/server ≥ 2, Zod 4, Node 20+; 1.7.5 il 2026-07-23 |
| `openai` (npm) | **7.25.0** | 2026-09-29 | |
| `@openai/agents` | **0.18.0** | 2026-09-10 | |
| `@openai/codex` | **0.159.3** | 2026-09-30 | |
| `@openai/chatkit` | **1.9.0** | n/d | |
| `@openai/apps-sdk` | inesistente | n/a | Il nome "Apps SDK" non ha più pacchetto: le app usano MCP e `ext-apps` |

### A.6 Auth0 come authorization server per un server MCP remoto [E]/[S]

- Documentazione Auth0 "Auth for MCP" (disponibilità generale annunciata) [E]: il server MCP è il resource server (valida i token e pubblica PRM RFC 9728); i client inviano `resource` (RFC 8707). Claude e altri client mandano `resource` ma non `audience`: in Auth0 serve il **Resource Parameter Compatibility Profile**.
- **DCR**: si abilita nelle impostazioni avanzate del tenant; richiede connessioni a livello di dominio ("domain-level"); i client registrati sono applicazioni di terze parti. Auth0 stesso raccomanda per la produzione la **registrazione CIMD manuale** (identità verificata dal dominio) invece della DCR [E].
- **CIMD**: nelle impostazioni del tenant esiste "Client ID Metadata Document Registration"; si importa il documento da un URL; esiste un endpoint Management API per registrare/aggiornare un client CIMD [E].
- **Da provare** (AI-01): i metadata dell'AS di Auth0 annunciano `client_id_metadata_document_supported`, `none` e `authorization_response_iss_parameter_supported`? L'audience resta quella dell'API MCP con `resource`? I token contengono i claim necessari? Quali sono durata e rotazione dei refresh token?
- **Tenant**: `docs/runbooks/auth0.md` prescrive due tenant EU (test e produzione); PL-14 ha dedotto dal codice la regione US. Il tenant che serve i token MCP deve essere quello degli utenti reali (il tenant di test per le prove).

### A.7 Portabilità: un server, una UI, due canali

| Elemento | Condiviso | Specifico Claude | Specifico ChatGPT |
|---|---|---|---|
| Server | Un solo `/mcp` Streamable HTTP, stesso catalogo di tool, annotazioni (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`), `structuredContent` + `content` | Nomi ≤ 64; descrizioni non istruttive; account di test "popolato"; 7 conferme; URL documentazione/privacy; scheda dei link ammessi | Giustificazione delle annotazioni; verifica dominio `/.well-known/openai-apps-challenge`; 5+3 test; verifica d'identità e `api.apps.write`; niente piani/upsell |
| UI | Un solo bundle MCP Apps (`ui://`, `text/html;profile=mcp-app`), CSP esatta, tema, i18n | `ui.domain` = hash + `.claudemcpcontent.com`; 3-5 screenshot PNG ≥1000 px; `frameDomains` limitato | Estensione `window.openai` solo se serve (evitarla); formati asset da verificare |
| Autenticazione | Un'API Auth0, PRM, PKCE S256, `resource`, refresh con rotazione, scope | Redirect `https://claude.ai/api/mcp/auth_callback` e loopback (Claude Code); `oauth_anthropic_creds` | Redirect stabile `connector_platform_oauth_redirect` con RFC 9207; solo OAuth 2.1 |
| Pacchetto | Skills (`SKILL.md`) e URL MCP da un'unica sorgente | `.claude-plugin/plugin.json` + `.mcp.json`; repository GitHub pubblico; portale `claude.ai/directory/manage` | Manifest plugin (formato da confermare: Codex/Agent Plugins) in ZIP; portale in OpenAI Platform |
| Rollout privato | Flag per org, consenso, sola lettura | Connettore personalizzato; rollout org da marketplace | Modalità sviluppatore; pubblicazione nell'workspace |

---

## Appendice B: fonti

Consultate il **2026-10-01**. Livello: **[V]** letta direttamente, **[E]** solo estratto di ricerca (dominio bloccato), **[S]** secondaria.

**Anthropic [V]**

| ID | URL |
|---|---|
| C1 | https://claude.com/docs/llms.txt |
| C2 | https://claude.com/docs/plugins/submit |
| C3 | https://claude.com/docs/directory/publish |
| C4 | https://claude.com/docs/connectors/building/submission |
| C5 | https://claude.com/docs/connectors/building/review-criteria |
| C6 | https://claude.com/docs/plugins/pre-submission-checklist |
| C7 | https://claude.com/docs/plugins/build |
| C8 | https://claude.com/docs/plugins/platform-support |
| C9 | https://claude.com/docs/plugins/org-rollout |
| C10 | https://claude.com/docs/connectors/custom/add-unlisted |
| C11 | https://claude.com/docs/connectors/building/index |
| C12 | https://claude.com/docs/connectors/building/authentication |
| C13 | https://claude.com/docs/connectors/building/enterprise-managed-auth |
| C14 | https://claude.com/docs/connectors/building/mcp-apps/getting-started |
| C15 | https://claude.com/docs/connectors/building/mcp-apps/quickstart |
| C16 | https://claude.com/docs/connectors/building/mcp-apps/design-guidelines |
| C17 | https://claude.com/docs/directory/submission-status |
| C18 | https://claude.com/docs/connectors/verification |
| C19 | https://support.claude.com/en/articles/13145358-anthropic-software-directory-policy (riassunto della pagina; i termini completi: https://support.claude.com/en/articles/13145338-anthropic-software-directory-terms, non letti) |
| C20 | https://claude.com/blog/bringing-mcp-2026-07-28-to-claude |

**MCP e registri [V]**

| ID | URL |
|---|---|
| M1 | https://blog.modelcontextprotocol.io/posts/2026-07-28/ |
| M2 | https://github.com/modelcontextprotocol/ext-apps (README) |
| M3 | https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx |
| M4 | https://github.com/modelcontextprotocol/ext-apps/releases |
| M5 | https://github.com/modelcontextprotocol/csharp-sdk e `/releases` |
| M6 | https://api.nuget.org/v3-flatcontainer/modelcontextprotocol.aspnetcore/index.json, registrazioni NuGet dei pacchetti `ModelContextProtocol*` |
| M7 | https://registry.npmjs.org/@modelcontextprotocol/sdk e pacchetti `@modelcontextprotocol/*`, `@openai/*`, `openai` |
| M8 | https://github.com/agentplugins/agent-plugins-spec (specifica 1.0.0) e https://github.com/agentplugins/agent-plugins-example |
| M9 | https://github.com/openai/openai-apps-sdk-examples (README) |

**OpenAI [E] (pagine ufficiali non raggiungibili, lette solo come estratti di ricerca)**

| ID | URL |
|---|---|
| O1 | https://developers.openai.com/plugins/deploy/submission |
| O2 | https://developers.openai.com/plugins/deploy/app-review |
| O3 | https://developers.openai.com/plugins/plugin-guidelines |
| O4 | https://developers.openai.com/plugins/build/auth |
| O5 | https://developers.openai.com/plugins/build/mcp-server |
| O6 | https://developers.openai.com/plugins/guides/security-privacy |
| O7 | https://developers.openai.com/plugins/deploy/connect-chatgpt |
| O8 | https://developers.openai.com/plugins/guides/submit-claude-plugin |
| O9 | https://developers.openai.com/plugins/build/plugins e https://developers.openai.com/plugins/deploy/submission-errors |
| O10 | https://help.openai.com/en/articles/20001256-plugins-in-chatgpt e https://help.openai.com/en/articles/6825453-chatgpt-release-notes |
| O11 | https://developers.openai.com/api/docs/mcp |
| O12 | https://github.com/dnobj/mail-letter-irl/issues/476 (issue di terzi che riassume le note di rilascio del 2026-07-09, letta [S]) |

**Auth0 [E]**

| ID | URL |
|---|---|
| A1 | https://auth0.com/ai/docs/mcp/get-started/authorization-for-your-mcp-server |
| A2 | https://auth0.com/ai/docs/mcp/guides/registering-your-mcp-client-application/dynamic-client-registration |
| A3 | https://auth0.com/ai/docs/mcp/guides/registering-your-mcp-client-application/manual-cimd-registration e https://auth0.com/docs/get-started/auth0-overview/create-applications/register-applications-with-cimd |
| A4 | https://auth0.com/blog/auth0-auth-for-mcp-servers-generally-available/ |

**Secondarie [S]** (solo indicative): articoli e guide su sottomissione dei plugin OpenAI (sunpeak.ai, flowlines.ai, galust.ai), tempi di review (reviewtimes.fyi, forum OpenAI Developer Community), Agent Plugins (vercel.com/blog/introducing-agent-plugins, the-decoder.com, daily.dev), guide Auth0 per MCP (aembit.io, zuplo.com), disponibilità in SEE (mixed-news.com e altri).
