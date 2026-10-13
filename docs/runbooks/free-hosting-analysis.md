# Analisi: hosting gratuito del backend dopo la chiusura di Railway

Task HOSTING. Data dell'analisi: **2026-10-02**. Solo analisi: nessuna modifica a codice, workflow o `.claude/rules/*`.

Contesto: il PO ha cancellato Railway e non vuole pagare nulla. Il database resta su Supabase (piano free), il frontend su Vercel. Il backend (ASP.NET Core .NET 10, Docker, `ASPNETCORE_URLS=http://+:8080`) deve girare su una soluzione gratuita.

> **Affidabilità delle fonti.** I siti ufficiali `docs.oracle.com`, `supabase.com`, `docs.cloud.google.com`, `vercel.com`, `koyeb.com`, `news.ycombinator.com` e `infoq.com` sono **bloccati dalla rete di sessione**. Dove possibile ho usato `cloud.google.com/run/pricing` e le discussioni su `github.com` (lette direttamente); per il resto le cifre arrivano dai risultati di WebSearch (estratti di pagine ufficiali e blog di terzi). Ogni dato non letto sulla fonte ufficiale è marcato **[non verificato sulla fonte ufficiale]**. Molti blog di "pricing 2026" sono SEO aggregati: li ho usati solo quando concordano tra loro. Prima di decidere, il PO (o un agente con rete aperta) deve ricontrollare le pagine ufficiali elencate in § 9.

---

## 1. Sintesi esecutiva e raccomandazione

**Raccomandazione: VM Oracle Cloud Always Free (Arm A1, 2 OCPU / 12 GB, regione Francoforte o Milano) con Docker Compose + Caddy, account aggiornato a Pay As You Go (resta a 0 EUR dentro i limiti Always Free), deploy da GitHub Actions via GHCR + SSH, connessione a Supabase tramite session pooler, backup notturno del DB fuori da Supabase.**

Perché, in una riga: è l'unica opzione realmente gratuita che dà un **processo sempre acceso con RAM abbondante** (Hangfire, advisory lock e webhook funzionano senza modifiche al codice), nella stessa regione di Supabase.

Verità scomode che il PO deve conoscere:

1. **Nessuna opzione è "gratuita, sempre accesa, senza carta, senza rischi".** Le piattaforme che non chiedono la carta (Render free, Hugging Face Spaces, Koyeb free) hanno 0,1 vCPU o dormono. Quelle con risorse vere (Oracle, Cloud Run, Azure) chiedono la carta (solo verifica, con rischio di addebito se si esce dai limiti).
2. **Oracle ha ridotto l'Always Free Arm il 15/06/2026** (enforcement 18/08/2026): da 4 OCPU/24 GB a **2 OCPU/12 GB** per tenancy. Per CasaZen bastano ampiamente (misurato: ~300 MB RSS). Restano però capacity esaurita in Europa e rischio sospensione account.
3. **Il pooler transazionale di Supabase (6543) rompe advisory lock e `Hangfire.PostgreSql`**: tutte le opzioni devono usare il **session pooler (porta 5432, host `*.pooler.supabase.com`, IPv4)** oppure la connessione diretta IPv6. Il session pooler free ha un **pool size di default 15**: il backend oggi, senza configurazione, apre ~26 connessioni (misurato) e va in `EMAXCONNSESSION`. **Va limitato** (§ 6.5).
4. **Il database free di Supabase è il vero punto debole dello stack**: nessun backup automatico/PITR, pausa dopo 7 giorni di inattività, 500 MB. Qualunque hosting scegliamo, serve un backup `pg_dump` periodico (§ 6.5).
5. **Vercel Hobby è solo per uso personale non commerciale**: un SaaS a pagamento viola i termini. Alternativa gratuita: Cloudflare Pages (commercial use ammesso), con lavoro di migrazione (§ 8).

Piano B (zero carta, subito, qualità inferiore): Render free + ping esterno gratuito (cron-job.org) per tenerlo sveglio. Piano C (carta Google, nessuna VM da gestire, ma richiede una piccola modifica di codice): Cloud Run scale-to-zero + Cloud Scheduler che chiama un endpoint "tick". Self-hosting domestico con Cloudflare Tunnel come opzione di nicchia.

Se il PO accettasse una spesa minima (fuori dal vincolo "0"): VPS Hetzner/simili da ~4 EUR/mese o Fly.io da ~2-5 USD/mese risolverebbero quasi tutti i rischi sotto. Va detto al PO come alternativa, ma non è la raccomandazione.

---

## 2. Cosa richiede davvero il backend (verificato sul codice)

| Requisito | Evidenza nel repo | Conseguenza sull'hosting |
|---|---|---|
| Hangfire server nello stesso processo dell'API, storage `Hangfire.PostgreSql`, schema per ambiente | `Casazen.Web/Extensions/HangfireServiceCollectionExtensions.cs` (`AddHangfireServer`, nessun `WorkerCount`), `HangfireStorageSettings`, `docs/runbooks/hangfire.md` | Serve un processo **sempre acceso** (o un wake-up affidabile). Con 20 worker di default (5 x core, max 20) la pressione sulle connessioni DB è alta |
| ~20 job ricorrenti (5-15 min: sync iCal, `checkout-hold-expiry`, polling firma/registrazione, `domain-recheck`, push receipts; orari: charge 06:00, rent-collection 07:00, GDPR 03:00, stay-alerts orari, ecc.) | `docs/runbooks/hangfire.md`, tabella dei job | Un'API che dorme fa saltare i job a 5-15 min; ritardi di minuti sono tollerabili per quelli giornalieri se l'istanza si sveglia (Hangfire recupera i job scaduti), non per `checkout-hold-expiry` (BK-21) |
| `[DisableConcurrentExecution]` = lock di distribuzione in tabella + lock `pg_advisory_*` | 27 file con advisory lock (24 in `Infrastructure/Services`, 2 `Repositories`, 1 `Data`, + test) | **Connessione di sessione obbligatoria** (non il transaction pooler). Cfr. § 3 e § 6.5 |
| Migrazioni all'avvio | `Program.cs:282` `db.Database.Migrate()` + `EncryptedColumns.EncryptLegacyPlaintextAsync` | L'avvio include la migrazione: cold start più lento e **rischio di migrazioni concorrenti** se più istanze partono insieme (Cloud Run con più istanze). Meglio `max-instances=1` |
| Webhook Stripe/Auth0/OTA/e-sign | `WebhooksController` fa `Enqueue<...>` e risponde 200 subito (righe 89-95, 143-147, 228-232, 289-293) | Il webhook è leggero: il rischio è solo il cold start dell'API (Stripe riprova per giorni, quindi non perde eventi, ma i ritardi si accumulano); l'elaborazione vera è un job Hangfire che richiede il server acceso |
| Health check | `/api/health/live` (sempre 200), `/api/health/ready` (db, hangfire, config; 503 se unhealthy) | `live` per i ping di keep-alive e per i probe; `ready` per il verify del deploy |
| Commit SHA nel readiness | `BuildInfo`: legge `RAILWAY_GIT_COMMIT_SHA` **oppure `GIT_COMMIT_SHA`** (`Casazen.Web/Configuration/BuildInfo.cs`) | Il verify CI funziona su qualunque host impostando `GIT_COMMIT_SHA` nel `docker run`/compose. Nessuna modifica al codice |
| Header forward / IP client | `UseForwardedHeaders` primo middleware; `ForwardedHeaders__KnownNetworks/KnownProxies/ForwardLimit` (`docs/runbooks/proxy-ip.md`) | Dietro Caddy sullo stesso host: `KnownProxies` = IP del gateway docker, oppure `127.0.0.1` con rete host. Va rieseguita la procedura di `proxy-ip.md` su ogni piattaforma nuova |
| TLS | Il container ascolta in HTTP 8080; `SecurityHeadersMiddleware` assume TLS terminato prima | Caddy (Let's Encrypt automatico) o TLS della piattaforma |
| Host header / CORS dinamico / domini custom (BK-16/BK-17) | `docs/runbooks/seo-domain.md` | Il reverse proxy deve passare l'`Host` originale e `X-Forwarded-Proto`. Con Cloud Run/Render va bene; con un tunnel va verificato |
| Storage file | Supabase Storage via S3 API (`docs/runbooks/storage.md`) | Indipendente dall'hosting; l'API non scrive su disco locale in produzione |
| Connessione DB | `NpgsqlConnectionStringNormalizer`: accetta URI `postgres://` e imposta `SslMode=Require`; `docs/INFRA.md:293` documenta già il session pooler per l'uso locale | Il session pooler è già noto al team |
| Regione DB | `docs/INFRA.md:115,221`: progetto Supabase `eu-central-1` (Francoforte). **Incoerenza**: lo stesso file (riga 293) cita come esempio `aws-0-eu-west-1.pooler.supabase.com` | **Domanda aperta Q1**: confermare la regione reale dal dashboard (Settings > Infrastructure) |

### Misure locali (build Release, macchina 4 core, Postgres 16 locale)

Pubblicazione `dotnet publish -c Release` (66 MB di output), `ASPNETCORE_ENVIRONMENT=Development`, DB vuoto, Hangfire attivo:

| Misura | Valore |
|---|---|
| Tempo fino a `/api/health/live` = 200, **primo avvio (applica tutte le migrazioni)** | ~10 s |
| Idem, **avvio successivo (nessuna migrazione)** | ~7 s |
| `/api/health/ready` a regime | 200 in ~80 ms |
| RSS a riposo (dopo 15 s / 45 s) | 285-295 MB (picco 340-350 MB al primo avvio) |
| Thread | ~61 |
| CPU nei primi 45 s | ~11-16 s di CPU (cioè ~25-30% di un core mediato) |
| Connessioni PostgreSQL aperte a riposo | **26** (Hangfire con 20 worker + polling + EF) |

Note: la macchina ha 4 core e carico condiviso con altri agenti; su 1-2 vCPU Arm o su 0,1 vCPU (Render/Koyeb free) l'avvio sarà **2-5 volte più lento**. Un cold start di 7-10 s su hardware buono vuol dire 20-60 s su un'istanza "free" a CPU ridotta. Non ho misurato il tempo di pull dell'immagine né il ritardo di rete verso Supabase.

Riduzione del cold start (in ordine di rapporto beneficio/rischio):
1. `max-instances=1` e niente scale-to-zero dove possibile (elimina il problema).
2. **ReadyToRun** (`PublishReadyToRun=true` con RID `linux-x64`/`linux-arm64`): riduzione tipica 30-80% del JIT [WebSearch, blog di terzi; non misurato qui]. Basso rischio, aumenta l'immagine. Da misurare nel task di migrazione.
3. `TieredPGO`/`TieredCompilation` default; `DOTNET_TieredPGO=0` per risparmiare CPU all'avvio.
4. Separare migrazioni dall'avvio (job CI `dotnet ef database update` o flag `Database__MigrateOnStartup`): richiede una modifica di codice, utile solo per lo scale-to-zero.
5. Native AOT: **sconsigliato**, EF Core, Hangfire, reflection, QuestPDF/PdfSharp e Auth0 lo rendono non banale.

Dimensionamento minimo realistico: **512 MB di RAM è il limite inferiore** (con workstation GC, `DOTNET_GCHeapHardLimit` ~350 MB e pochi worker Hangfire); **1 GB è il minimo ragionevole**; 2 vCPU consigliati. Una VM E2.1.Micro di Oracle (1/8 OCPU, 1 GB) è quindi al limite e non adatta.

---

## 3. Vincolo eliminatorio: Supabase, connessioni di sessione, IPv6, limiti

Fatti (fonti in § 9):

- La connessione diretta `db.[ref].supabase.co:5432` sul piano free è **solo IPv6** (dal 29/01/2024; l'indirizzo IPv4 dedicato è un add-on a pagamento, ~4 USD/progetto) [GitHub discussion supabase #17817, letta direttamente].
- **Session pooler (Supavisor) `aws-N-<region>.pooler.supabase.com:5432`**: IPv4, supporta sessione intera, prepared statement e advisory lock. Utente `postgres.<project-ref>`.
- **Transaction pooler `:6543`**: stato di sessione (`SET`, lock advisory di sessione, `LISTEN`) non sopravvive al confine della transazione; per questo **rompe silenziosamente** advisory lock e il `[DisableConcurrentExecution]`/lock di Hangfire.PostgreSql. Dal 28/02/2025 la modalità sessione sulla 6543 è stata rimossa [GitHub discussion supabase #32755, risultato di ricerca].
- Limiti free (nano compute): 60 connessioni dirette, 200 client sul pooler; in **session mode il numero effettivo di connessioni è limitato dal "Pool Size"** (default 15 per coppia utente+database) [Supabase docs/FAQ Supavisor e issue GitHub `EMAXCONNSESSION`, **non verificato sulla fonte ufficiale**]. Il pool size si può cambiare dal dashboard (Database settings), entro i limiti dell'istanza.
- Il backend oggi **non** limita `WorkerCount` di Hangfire né `Maximum Pool Size` di Npgsql (grep su `Casazen.Web` e `Casazen.Infrastructure`): misurate 26 connessioni a riposo.

Compatibilità delle piattaforme con Supabase free:

| Piattaforma | Uscita IPv6 | Come arriva a Supabase | Esito |
|---|---|---|---|
| Oracle Cloud VM | Possibile (VCN con IPv6 abilitato: da configurare), IPv4 pubblico sempre | Session pooler (IPv4) oppure diretta IPv6 se abilitata | OK |
| Cloud Run | Solo con Direct VPC egress dual-stack; default IPv4 | Session pooler | OK |
| Render | **No** (ENETUNREACH sulla diretta) [GitHub supabase #36958 / blog] | Session pooler | OK solo con pooler |
| Koyeb, Fly.io | Fly.io sì (IPv6 nativo); Koyeb non verificato | Session pooler | OK con pooler |
| Railway | Il feature flag IPv6 ha dato problemi con Supabase [Railway station, GitHub supabase #43111] | Session pooler | (non più rilevante) |
| Azure App Service / Container Apps, AWS | IPv6 in uscita non garantito | Session pooler | OK con pooler |
| Casa (Raspberry) | Dipende dall'ISP (spesso sì) | Pooler o diretta | OK |

Conclusione: **nessuna piattaforma è eliminata**, perché il session pooler gratuito è IPv4 e supporta tutto ciò che serve. Il vincolo diventa invece **il pool size di 15**, da rispettare con `WorkerCount` e `Maximum Pool Size` (§ 6.5). Chi vuole la diretta IPv6 (60 connessioni) può usarla da Oracle abilitando IPv6 nella VCN.

---

## 4. Matrice comparativa

Legenda criteri eliminatori (E): E1 sempre acceso o wake-up affidabile; E2 RAM >= 512 MB utili; E3 gratuito oggi per un nuovo account (ottobre 2026); E4 uso commerciale non vietato dai termini. Punteggi 0-5 (5 = meglio) sui criteri ponderati: affidabilità, setup, lock-in, latenza EU, rischio addebiti/sospensione.

| Opzione | Carta? | Sempre acceso | RAM/CPU | Regione EU | IPv6 -> Supabase | E1 | E2 | E3 | E4 | Affid. | Setup | Lock-in | Rischio | **Tot (20, + latenza)** |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Oracle Always Free A1 (2 OCPU/12 GB)** | Sì (verifica; PAYG consigliato) | Sì | 12 GB / 2 | Sì (Francoforte/Milano, capacity scarsa) | Pooler OK; IPv6 configurabile | OK | OK | OK | OK | 3 | 2 | 5 | 2 | **12** (+5 latenza = 17) |
| **Cloud Run scale-to-zero + tick (Cloud Scheduler)** | Sì (billing obbligatorio) | No (risvegli) | fino a 512 MB-2 GB | Sì (`europe-west1/8/12`) | Pooler OK | OK con tick | OK | OK (con tetto) | OK | 4 | 3 | 3 | 3 | **13** (+5 latenza = 18) |
| **Render free + ping esterno** | **No** | Quasi (750 h/mese >= 744 h di un mese) | 512 MB / 0,1 vCPU | Sì (Francoforte) | Solo pooler | OK (con ping) | Al limite | OK | Dubbio | 2 | 5 | 5 | 4 | **16** (+5 latenza = 21) ma 0,1 vCPU e dubbi sui termini (E2/E4 al limite) |
| Self-hosting casa + Cloudflare Tunnel | No (hardware proprio) | Sì (se casa su) | Dipende | Casa (Italia) | Dipende ISP | OK | OK | OK (hardware: costo una tantum) | OK | 2 | 2 | 5 | 4 | **13** (+3 latenza = 16) |
| Cloud Run min-instances=1 | Sì | Sì | 512 MB+ | Sì | Pooler | OK | OK | **No (~3-10 USD/mese)** | OK | 5 | 5 | 3 | 3 | scartata (non gratuita) |
| Azure Container Apps (free grant) | Sì | min replica 1 | 0,25-0,5 vCPU | Sì | Pooler | OK | OK | **No** (idle ~ addebito) | OK | 4 | 3 | 3 | 3 | scartata come always-on; come Cloud Run con tick è equivalente ma con meno community |
| Koyeb free instance | Sì ($29 hold, piano Pro di default) | No (scale to zero dopo 1 h) | 512 MB / 0,1 vCPU | Sì (Francoforte) | ? | No | Al limite | Quasi | OK | 2 | 4 | 4 | 3 | scartata |
| Fly.io | Sì | Sì | da 256 MB | Sì | Sì (IPv6) | OK | OK | **No** (nessun free per nuovi account: trial 2 h / 7 gg) | OK | 4 | 4 | 3 | 3 | scartata |
| Railway | Sì | Sì | - | Sì | - | OK | OK | **No** (trial 5 USD una tantum; free 1 USD/mese) | OK | - | - | - | - | scartata |
| Azure App Service F1 | Sì | **No** (60 min CPU/giorno, no Always On) | 1 GB (condiviso) | Sì | - | **No** | OK | OK | OK | - | - | - | - | scartata (E1) |
| AWS (Lightsail/EC2/App Runner) | Sì | Sì | - | Sì | - | OK | OK | **No** (dal 15/07/2025 solo 200 USD di crediti per 6 mesi) | OK | - | - | - | - | scartata (E3) |
| Google Compute Engine e2-micro | Sì | Sì | 1 GB / 0,25 vCPU | **No (solo `us-west1/us-central1/us-east1`)** | - | OK | Al limite | OK | OK | 3 | 3 | 3 | 3 | scartata (latenza ~100 ms+ verso Francoforte, 1 GB, 1 GB egress/mese) |
| Hugging Face Spaces (Docker) | No | No (sleep dopo 48 h; riavvio perde i dati effimeri) | 2 vCPU / 16 GB | Non scelta | ? | **No** | OK | OK | **Dubbio** (spazi pensati per demo ML; creare Docker Space richiede piano a pagamento secondo alcune fonti) | - | - | - | - | scartata |
| Northflank Sandbox | Sì (verifica) | Sì (nessuno sleep) | piccole (2 servizi, 1 DB, 2 cron) | Sì | ? | OK | Da verificare | OK | OK | 3 | 4 | 4 | 3 | **candidata da verificare** (limiti RAM non confermati) |
| Cloudflare Containers | - | - | - | - | - | - | - | **No** (richiede Workers Paid, 5 USD/mese) | OK | - | - | - | - | scartata |
| GitHub Actions/Codespaces come server | - | - | - | - | - | - | - | - | **No** (Actions: non per servire applicazioni; Pages vieta SaaS commerciali) | - | - | - | - | **esclusa per ToS** |
| Zeabur / Back4app (free) / Leapcell / Sevalla | Back4app e Zeabur dichiarano free senza carta (Back4app 256 MB/0,25 vCPU: **insufficiente**); Leapcell: free per Python/Node/Go (non .NET); Sevalla: free solo statico | - | - | - | - | - | **No** | - | - | - | - | - | - | scartate |
| Deta, Cyclic, Glitch, Adaptable | **Non esistono più** (Cyclic e Glitch chiusi, Deta Space dismesso) **[non verificato in questa sessione]** | | | | | | | | | | | | | scartate |
| Clever Cloud, Scalingo | Nessun free tier permanente (trial a tempo) **[non verificato]** | | | | | | | | | | | | | scartate |

Nota sul punteggio: i totali sono indicativi (somma di affidabilità, setup, lock-in e rischio; la latenza EU è indicata a parte, 5 se l'host è nella regione del DB) e pesano allo stesso modo i criteri. Render ottiene il punteggio più alto per facilità e assenza di carta, ma E2 (0,1 vCPU, 512 MB per un processo che a riposo usa 290 MB di RSS e a regime Hangfire con decine di job) è il suo punto critico: lo declasso a Piano B. Il TOP 3 finale tiene conto del rischio operativo, non solo della somma.

---

## 5. Cosa succede con lo scale-to-zero (Hangfire, webhook, cold start)

Fatti sulle piattaforme:

- **Cloud Run**: con CPU "solo durante le richieste" il container ha CPU solo quando serve una richiesta; thread in background (Hangfire) sono rallentati/sospesi fuori dalle richieste. Per i carichi in background la documentazione consiglia `min-instances >= 1` con CPU sempre allocata o un "wake-up request" [Cloud Run docs: min-instances/instance autoscaling, via risultato di ricerca]. Un'istanza che scala a zero **non può risvegliarsi senza una richiesta**: servono Cloud Scheduler (3 job gratuiti per account di fatturazione, **[non verificato sulla fonte ufficiale]**) o cron esterni.
- **Free tier Cloud Run** (letto su `cloud.google.com/run/pricing`): 2 M richieste, 360.000 GiB-s, 180.000 vCPU-s al mese. Un'istanza sempre accesa da 0,5 vCPU / 512 MiB consuma 1,3 M vCPU-s e 1,3 M GiB-s al mese: **non rientra** nel free tier (~3-10 USD/mese). La pagina riassunta afferma anche che il free tier non si applica alla fatturazione "instance-based" con min-instances: **da verificare**.
- **Render free**: dopo 15 minuti senza traffico l'istanza si ferma; il risveglio richiede 30-60 s. 750 h/mese gratuite per workspace: un servizio solo copre 744 h (31 giorni), quindi un ping ogni <15 minuti lo mantiene sveglio **ma consuma tutte le ore**: nessun secondo servizio.
- **GitHub Actions cron** come ping: minimo 5 minuti ma ritardi di 5-30 min fino a ore (segnalati 2025-2026), run saltate e workflow disabilitati dopo 60 giorni di inattività: **non adatto come keep-alive o scheduler** (ok per il backup notturno). **cron-job.org**: gratuito, 1 minuto, timeout 30 s, nessun SLA, mantenuto da una persona/community. **Cloud Scheduler**: affidabile, ma richiede un progetto GCP con fatturazione.

Design "tick" (solo per l'opzione Cloud Run), da implementare nel task di migrazione:
1. Endpoint protetto (es. `POST /internal/tick`, API key o OIDC di Cloud Scheduler) che tiene la richiesta aperta N secondi (es. 45-90 s) mentre il server Hangfire elabora la coda; in alternativa avviare il server Hangfire solo dentro il tick.
2. Cloud Scheduler ogni 5 minuti. Con 1 vCPU / 1 GiB e una richiesta di 60 s ogni 5 minuti l'istanza è attiva ~20% del mese (~518.000 s): ~518.000 vCPU-s e GiB-s, cioè **sopra** i 180.000 vCPU-s gratuiti (circa 1,5 USD di eccedenza a 0,000024 USD/vCPU-s e meno per la memoria). Con 0,25-0,5 vCPU e tick più brevi (20-30 s) si resta dentro il free tier. Il costo reale va **calcolato col calcolatore Google** prima di decidere: l'obiettivo è 0 EUR, ma non è garantito.
3. Latenza dei job: fino a 5 minuti. Il webhook Stripe viene accolto subito (200 in cold start) ma il job `StripeWebhookJob` parte al tick successivo (o subito se la richiesta tiene l'istanza viva e il server Hangfire è già su).
4. Rischio: più istanze contemporanee = migrazioni e job concorrenti. `max-instances=1`.

Giudizio: fattibile, ma è un cambiamento architetturale con rischi di regressione sui job (`checkout-hold-expiry`, firme, push). **Preferire un processo sempre acceso.**

---

## 6. TOP 3 in dettaglio

### 6.1 TOP 1: Oracle Cloud Always Free, VM Ampere A1

**Condizioni attuali (2026):**
- A1 Flex: **2 OCPU e 12 GB per tenancy** (1.500 OCPU-ore e 9.000 GB-ore/mese; prima 4 OCPU/24 GB). Il taglio è comparso nella documentazione intorno al 15/06/2026 e dal **18/08/2026** le istanze oltre il limite vengono fermate e poi cancellate (dopo 30 giorni) se non si fa upgrade o resize [InfoQ 07/2026, Linuxiac, HN, DEV Community, daily.dev: risultati di ricerca; docs.oracle.com bloccato, **non verificato sulla fonte ufficiale**]. Due E2.1.Micro AMD (1/8 OCPU, 1 GB) restano invariate.
- **Idle reclamation**: istanze Always Free considerate inattive se, in 7 giorni, CPU p95 < 20%, rete < 20% (e memoria < 20% sulle A1). Un'API a basso traffico **rientra in questa regola**. Passando l'account a **Pay As You Go** l'istanza non viene più reclamata e non si paga finché si resta nei limiti Always Free [Oracle FAQ/Always Free docs e blog 51sec, risultati di ricerca].
- **Capacity**: "Out of host capacity" per A1 a Francoforte, Amsterdam e Milano per tutto il 2026 [community.oracle.com]. Con PAYG la creazione è di solito più facile; esistono script di retry (`oci-arm-host-capacity`). **La home region non si può cambiare dopo la registrazione.**
- **Sospensione/terminazione**: casi documentati di account bloccati senza preavviso [DEV Community "Beware of Oracle Cloud: unexplained account termination"]. Account free inattivi 30-60 giorni possono essere sospesi.
- Carta: sì, per verifica (hold temporaneo). PAYG: **addebiti solo se si superano i limiti Always Free**; usare Budget con alert a 0,01 USD e non creare altre risorse a pagamento. Oracle non offre un "hard cap" automatico, ma l'account "Always Free" puro non può fatturare (a costo di capacity/idle reclaim).
- Banda: 10 TB/mese di uscita [**non verificato**], 50 Mbps su E2.1.Micro.
- Lock-in: nessuno (Docker Compose standard).

**Architettura:**
```
Browser/Stripe/Auth0 --HTTPS--> Cloudflare DNS (solo DNS, nuvola grigia) --> VM OCI A1 (Francoforte)
                                                                           Caddy :443 -> api:8080 (container .NET 10 arm64)
                                                                           Hangfire nello stesso processo
api --> Supabase session pooler (IPv4, :5432, utente postgres.<ref>) --> Postgres (schema casazen_prod)
api --> Supabase Storage S3, Resend, Stripe, Auth0
GitHub Actions: build+test -> push immagine su GHCR (multi-arch arm64) -> ssh deploy (compose pull && up -d) -> verify-deploy.sh
```

**Passi ad alto livello:** registrazione OCI (regione Francoforte o Milano, la più vicina a Supabase), upgrade a PAYG con Budget alert, creazione VM A1 (Ubuntu 24.04 arm64, 2 OCPU/12 GB, boot volume <= 100 GB di quelli gratuiti 200 GB totali), firewall (VCN security list + `iptables`/`ufw`: solo 22 da IP del PO, 80 e 443), Docker + compose, Caddy, utente non-root, `fail2ban`, aggiornamenti automatici; secret in `/opt/casazen/.env` (mai nel repo) con tutte le variabili oggi su Railway; DNS `api.<dominio>` (A, e AAAA se IPv6) verso l'IP pubblico riservato; workflow GitHub Actions con `docker buildx` (QEMU o runner arm64) verso GHCR e deploy via SSH con chiave dedicata.

**Rischi e mitigazioni:**

| Rischio | Mitigazione |
|---|---|
| Account Oracle sospeso/terminato senza preavviso | Backup DB e dei file di configurazione fuori da OCI (§ 6.5); `docker-compose.yml` e Caddyfile nel repo; **runbook di ripiego su Render o Cloud Run** con le stesse variabili; non tenere dati unici sulla VM |
| Capacity A1 non disponibile | Script di retry, PAYG, prova Milano/Francoforte; ripiego temporaneo su Render (Piano B) |
| Idle reclamation | PAYG (esente); in alternativa carico artificiale (sconsigliato) |
| Job Hangfire: processo fermo per riavvio VM | `restart: unless-stopped`; Hangfire recupera i job scaduti; il lock di distribuzione scade dopo 30 min (`Hangfire:DistributedLockTimeoutMinutes`) |
| Advisory lock/pooler | Session pooler porta 5432, **mai 6543**; `WorkerCount`<=4-5, `Maximum Pool Size`=8-10 (§ 6.5) |
| Cold start | Non esiste (sempre acceso). Le migrazioni all'avvio (~10 s) sono dentro la finestra del `docker compose up` con healthcheck |
| IP client (`ForwardedHeaders`) | Caddy sullo stesso host: `KnownProxies`=IP del gateway della rete docker, `ForwardLimit=1`; ripetere `proxy-ip.md` § verifica |
| Sicurezza della VM (porte, SSH) | Solo 80/443 pubbliche, SSH con chiave e limitato per IP, aggiornamenti automatici, nessun segreto nel repo |
| Costo | 0 EUR se nei limiti. Alert a 0,01 USD, nessuna risorsa extra (no Load Balancer a pagamento, no IP non riservati, volumi entro 200 GB) |

**Effort:** 2-3 giorni (compresa la sorveglianza della capacity).

### 6.2 TOP 2: Google Cloud Run + Cloud Scheduler (tick)

**Pro:** piattaforma affidabile, regioni EU (`europe-west1/3/4/8/12`), TLS e domini gestiti, deploy `gcloud run deploy` da GitHub Actions con Workload Identity (nessuna chiave), nessuna VM da amministrare; nessun problema di capacity.
**Contro:** richiede billing (carta); lo scale-to-zero impone il design "tick" (§ 5) e il codice cambia; **non c'è un hard cap di spesa** (solo Budget alert; mitigazioni: `max-instances=1`, `concurrency` limitata, quota Cloud Run, disattivare la fatturazione via funzione se si supera il budget); i costi dipendono da come si legge il free tier; il suo free tier è soggetto a variazioni.
**Passi:** progetto GCP, billing + Budget a 1 EUR con automazione che scollega la fatturazione, Artifact Registry (free tier 0,5 GB) o GHCR, Cloud Run service `europe-west1`/`europe-west8` (Milano) `--min-instances=0 --max-instances=1 --cpu-boost --memory=1Gi`, variabili da Secret Manager (6 secret gratuiti, **non verificato**), Cloud Scheduler `*/5 * * * *` verso `/internal/tick`, domini custom con mapping Cloud Run o Cloudflare davanti.
**Rischi:** migrazioni concorrenti (mitigato da `max-instances=1`), job in ritardo, 20+ connessioni verso il pooler (limitare worker), IPv6 non necessario, log verbosi (Cloud Logging 50 GiB free).
**Effort:** 4-6 giorni (incluso il tick, i test sui job e il verify).

### 6.3 TOP 3: Render free + keep-alive esterno (Piano B, nessuna carta)

**Pro:** nessuna carta, deploy Docker da GitHub, TLS e domini inclusi, regione Francoforte, setup di un'ora.
**Contro:** 0,1 vCPU e 512 MB (il processo misura ~290 MB RSS a riposo, picchi 350 MB), avvio da 30-60 s o più con le migrazioni, **solo IPv4 via session pooler**, 750 h/mese = un solo servizio sempre acceso solo se pingato, nessun SLA, **termini d'uso per carichi commerciali non verificati** (il free tier è pensato per prototipi), dipendenza da cron-job.org (community) per il keep-alive; una sola istanza.
**Passi:** `render.yaml` con servizio web Docker, variabili d'ambiente, `healthCheckPath=/api/health/live`, ping cron-job.org ogni 10 min a `/api/health/live` (+ secondo ping da un monitor tipo UptimeRobot free per ridondanza), `WorkerCount=2`.
**Quando usarlo:** come **ponte immediato** (oggi il backend è giù) mentre si prepara Oracle, o come ripiego se Oracle non fornisce capacity.
**Effort:** 0,5-1 giorno.

### 6.4 Opzione di nicchia: self-hosting domestico (Raspberry/mini PC) + Cloudflare Tunnel

Gratuito (hardware proprio), sempre acceso, nessuna carta, `cloudflared` esce in uscita (nessuna porta aperta, nessun IP pubblico). L'uso di Cloudflare Tunnel per API/HTML è ammesso, il divieto riguarda il servire contenuti non HTML pesanti (clausola 2.8 delle condizioni self-serve) [Cloudflare Community, blog di terzi; **non verificato sul testo ufficiale**]. Limiti: corrente, rete domestica e ISP (CGNAT non è un problema col tunnel), aggiornamenti e sicurezza a carico del PO, un sistema con **dati di ospiti (GDPR, Alloggiati)** su hardware domestico è difficile da difendere in un audit, outage di Cloudflare (es. 2025). Non lo raccomando per una produzione con dati personali; va bene per l'ambiente di test.

### 6.5 Mitigazioni comuni a tutte le opzioni

**Pool di connessioni** (da implementare nel task di migrazione; il repo non lo configura oggi):
- `Hangfire:WorkerCount` configurabile (`AddHangfireServer(o => o.WorkerCount = n)`), default proposto **4**.
- Connection string con `Maximum Pool Size=10;Minimum Pool Size=0;Connection Idle Lifetime=60;Timeout=15;No Reset On Close=true` e verifica che totale (EF + Hangfire worker + lock) resti sotto il **pool size di sessione (15 di default, aumentabile dal dashboard)**.
- Una sola istanza del backend (due istanze sommano le connessioni).
- Rete: porta **5432** sul host `*.pooler.supabase.com`, utente `postgres.<ref>`, `SSL Mode=Require`, `SearchPath=casazen_prod`. Variabile `Hangfire__Schema` può restare quella derivata (`hangfire_casazen_prod`).
- Test di accettazione: `SELECT count(*) FROM pg_stat_activity` e l'esecuzione concorrente di due job con `[DisableConcurrentExecution]`.

**Pausa a 7 giorni di Supabase free:** il keep-alive esiste già (`supabase-keepalive.yml`, cron settimanale). Un backend sempre acceso e i job ogni 5-15 minuti mantengono il DB attivo comunque; il workflow resta come rete di sicurezza.

**Backup del DB (rischio dati, priorità alta):** il piano free non ha backup/PITR. Soluzione gratuita: GitHub Actions cron notturno con `pg_dump` (via session pooler) dei due schemi, cifrato (`age`/`gpg`) e caricato su un bucket **Cloudflare R2** (free 10 GB) o su un secondo account Supabase Storage; conservare 14-30 giorni; **test di ripristino trimestrale**. Il workflow cron di GitHub può ritardare ma per un backup notturno è accettabile; `workflow_dispatch` manuale prima di ogni release.

**Latenza:** tenere API e DB nella stessa regione. Se il progetto Supabase è `eu-central-1` -> OCI `eu-frankfurt-1`, Cloud Run `europe-west3` (Francoforte), Render Frankfurt. Se è `eu-west-1` (Irlanda) -> Cloud Run `europe-west1` (Belgio) o Oracle `uk-london-1`/`eu-amsterdam-1`. Dipende dalla risposta a Q1.

---

## 7. Esperienze di altri sviluppatori

Fonti reali, consultate via ricerca. Dove il link originale era bloccato, lo segnalo.

- **Oracle, taglio 2026 e terminazioni**: discussione HN "Oracle cut its Always Free ARM limits to 2 OCPU / 12GB, enforced Aug 18" (https://news.ycombinator.com/item?id=49183750, bloccato in sessione, solo titolo letto); InfoQ "Oracle Quietly Halves Free Tier Ampere A1 Compute Limits with No Public Announcement" (https://www.infoq.com/news/2026/07/oracle-cloud-free-tier-limits/, bloccato); Linuxiac (https://linuxiac.com/oracle-quietly-cuts-free-tier-ampere-a1-resources-in-half/); DEV Community "How to Check If Your Oracle Cloud A1 Free Tier Instance Just Got Quietly Downgraded" (https://dev.to/cash602cmd/how-to-check-if-your-oracle-cloud-a1-free-tier-instance-just-got-quietly-downgraded-2h6m). Tema comune: nessun annuncio chiaro, istanze fermate allo scadere del termine.
- **Oracle, account terminato**: DEV Community "Beware of Oracle Cloud: my experience with unexplained account termination" (https://dev.to/nobinkhan/beware-of-oracle-cloud-my-experience-with-unexplained-account-termination-12i); Oracle Community "Free Tier Instance Terminated Without Warning" (https://community.oracle.com/customerconnect/discussion/875400/).
- **Oracle, capacity in Europa**: Oracle Community "Host capacity in Frankfurt" (https://community.oracle.com/customerconnect/discussion/593801/host-capacity-in-frankfurt), "Ampere out of capacity eu-frankfurt-1" (https://community.oracle.com/customerconnect/discussion/738457/), Amsterdam (https://community.oracle.com/customerconnect/discussion/595905/host-capacity-amsterdam); strumento di retry https://github.com/oeufmeister/oci-arm-host-capacity.
- **Oracle, idle e PAYG**: blog 51sec (https://blog.51sec.org/2023/02/oracle-cloud-cleaning-up-idle-compute.html) e commento su HN (https://news.ycombinator.com/item?id=36010572): chi passa a PAYG non viene reclamato.
- **Supabase e IPv6**: discussione GitHub supabase #17817 (https://github.com/orgs/supabase/discussions/17817, letta), #32755 (porta 6543 solo transaction mode), #36958 (Render, 404/ENETUNREACH), #43111 (Railway); gstack issue #1301 (https://github.com/garrytan/gstack/issues/1301): nuovi progetti solo session pooler; ALTUSplace PR #17 (https://github.com/ALTUSplace/ALTUSplace/pull/17): advisory lock di sessione rotti dal transaction pooler, il migration runner rifiuta la 6543; GNU-connect/Server-Node issue #108 (https://github.com/GNU-connect/Server-Node/issues/108): `EMAXCONNSESSION` sul session pooler.
- **Render free e sleep**: GitHub community #197645 (https://github.com/orgs/community/discussions/197645) e Odown "How to keep a Render free service from sleeping" (https://odown.com/blog/how-to-keep-a-render-free-service-from-sleeping/): il ping consuma le ore, 30-60 s di cold start.
- **Cron esterni**: confronti DEV Community (https://dev.to/ronency/best-external-cron-job-services-compared-2026-8a3) e discussioni GitHub sui ritardi dei cron di Actions (https://github.com/orgs/community/discussions/156282): ritardi da 5 minuti a più ore.
- **Fly.io**: confronti 2026 (https://www.saaspricepulse.com/tools/flyio, https://costbench.com/software/developer-tools/flyio/free-plan/): nessun free tier per i nuovi account, solo trial.
- **Koyeb**: chiusura Starter free a nuovi utenti il 26/02/2026 (https://github.com/robhunter/agentdeals/pull/2209, https://www.koyeb.com/blog/sustaining-free-compute-in-a-hostile-environment).
- **.NET su PaaS, cold start**: ~9 s di cui ~6 di JIT per un servizio ASP.NET Core 8 vanilla; ReadyToRun riduce il 30-80% (https://stackharbor.com/en/knowledge-base/paas-runtime-dotnet-core-trim-aot-image-size/).

**Limite di questa sezione:** non sono riuscito a leggere i thread Reddit (r/dotnet, r/selfhosted, r/oraclecloud) né Stack Overflow: non li cito per non inventare link. Il PO può cercare "Always Free A1 PAYG idle reclaim" su r/oraclecloud prima di decidere.

---

## 8. Altri costi dello stack (tabella breve)

| Servizio | Piano gratuito oggi (2026) | Rischio / impatto su CasaZen | Azione proposta |
|---|---|---|---|
| **Vercel Hobby** | Gratuito ma **solo uso personale non commerciale**; uso commerciale richiede Pro (20 USD/seat/mese) [Vercel fair use policy, via blog e risultati di ricerca; vercel.com bloccato, **non verificato sulla fonte ufficiale**] | **Alto**: un SaaS a pagamento in produzione viola il Hobby. Vercel può sospendere il progetto | Migrare il frontend a **Cloudflare Pages** (commerciale ammesso; build 500/mese; banda illimitata; Pages Functions 100.000 req/giorno) oppure Netlify free (hard cap a 300 crediti/mese, commerciale ammesso). GitHub Pages **no** (vieta SaaS commerciali) |
| **Impatto della migrazione frontend (BK-15, BK-17, vercel.json)** | - | `vercel.json` (rewrite SPA, regole `user-agent` per i crawler), `api/seo.ts` e `api/sitemap.ts` sono **Vercel Functions**: vanno riscritte come Cloudflare Pages Functions (`functions/`) o Worker; `Vercel__ApiToken/ProjectId` e il job `domain-recheck` (BK-17) usano la Vercel Domains API: su Cloudflare servirebbe **Cloudflare for SaaS (Custom Hostnames, primi 100 gratuiti sui piani Free/Pro/Business; wildcard e apex solo Enterprise)** [domainee.dev, community Cloudflare; **non verificato sulla fonte ufficiale**] e un nuovo adapter nel backend. `PublicHost__BaseDomain` (wildcard `*.dominio`) con Cloudflare for SaaS non è gratuito/semplice | Stima 5-8 giorni (SPA + 2 funzioni + CSP + header + nuovo adapter dominio). **Ponte**: finché si è piccoli, restare su Vercel Hobby è un rischio contrattuale da accettare consapevolmente e per iscritto, non una soluzione |
| **Auth0 free** | 25.000 MAU, 1 custom domain (con carta), social illimitati [blog di terzi, **non verificato sulla fonte ufficiale**]; le fonti **discordano** su RBAC, MFA e M2M nel free (una dice assenti, la documentazione storica li dava limitati) | **Il backend usa RBAC (ruoli `Admin`, `PropertyOwner`, `LongTermLandlord`, `Supplier`), una Action e un'app M2M per la Management API** (`docs/runbooks/auth0.md`). Se M2M/RBAC non fossero nel free, la sincronizzazione ruoli si romperebbe | Verificare nel dashboard del tenant (Settings > Subscription/Features) e nella pagina prezzi Auth0 prima di dichiarare "0 EUR". Verificare anche i rate limit della Management API |
| **Resend free** | 3.000 email/mese, **100 al giorno**, 3 domini, retention 30 giorni [blog di terzi] | Il limite giornaliero è quello stretto: reminder check-in, alert CIN/stay alerts, inviti, ricevute possono superare 100/giorno al crescere degli host | Monitorare; codificare una coda con rinvio al giorno dopo (Hangfire già lo permette) |
| **Stripe** | Nessun costo fisso | Commissioni per transazione; webhook gratuiti | Nessuna |
| **Supabase free** | 500 MB DB, 1 GB storage, 5 GB egress, 2 progetti attivi, **nessun backup**, pausa a 7 giorni [blog di terzi, concordi] | **Rischio dati alto**; i file (documenti ospiti, foto) arrivano a 1 GB rapidamente; il progetto unico condivide test e prod (due schemi) | Backup `pg_dump` (§ 6.5); monitorare lo spazio; separare i progetti test/prod quando possibile (secondo progetto free: 2 inclusi); valutare Pro (25 USD) appena ci sono dati reali di clienti paganti |
| **Expo/EAS free** | ~15 build Android + 15 iOS al mese, coda a bassa priorità, timeout 45 min [blog di terzi] | Sufficiente per i rilasci; i certificati Apple (99 USD/anno) e Google Play (25 USD una tantum) **non sono gratuiti** | Includere nel budget se si pubblica sugli store |
| **Dominio** | Non gratuito | ~10-15 EUR/anno | Registrare su Cloudflare Registrar (a prezzo di costo) o simile |
| **Cloudflare (DNS, Pages, R2)** | Gratuito per DNS e Pages; R2 10 GB | Dipendenza aggiuntiva | Usabile per DNS e backup |

---

## 9. Fonti consultate (data di consultazione: 2026-10-02)

Ufficiali (letti direttamente quando indicato):
- Cloud Run pricing: https://cloud.google.com/run/pricing (letta direttamente; riassunto automatico, cifre del free tier concordano con le altre fonti)
- Supabase, IPv4 deprecation e Supavisor: https://github.com/orgs/supabase/discussions/17817 (letta direttamente, 2024)
- Supabase, connessione al DB: https://supabase.com/docs/guides/database/connecting-to-postgres e `/pooling-and-limits` (**bloccati**, solo estratti di ricerca)
- Oracle Always Free: https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm e https://www.oracle.com/cloud/free/faq/ (**bloccati**, solo estratti di ricerca)
- Cloud Run min-instances e autoscaling: https://docs.cloud.google.com/run/docs/configuring/min-instances, https://docs.cloud.google.com/run/docs/about-instance-autoscaling (**bloccati**)
- Cloud Run IPv6 (Direct VPC egress dual-stack): https://docs.cloud.google.com/run/docs/configuring/vpc-dual-stack-subnet (**bloccato**)
- Azure Container Apps pricing: https://azure.microsoft.com/en-us/pricing/details/container-apps/; App Service: https://azure.microsoft.com/en-us/pricing/details/app-service/linux/ (estratti)
- AWS Free Tier (dal 15/07/2025, 200 USD di crediti): https://aws.amazon.com/about-aws/whats-new/2025/07/aws-free-tier-credits-month-free-plan/
- Koyeb: https://www.koyeb.com/docs/faqs/pricing, https://www.koyeb.com/docs/reference/instances
- Hugging Face Spaces: https://huggingface.co/docs/hub/en/spaces-overview
- Cloudflare Containers: https://www.cloudflare.com/products/containers/; Pages limits: https://developers.cloudflare.com/pages/platform/limits
- GitHub Pages limits (vieta SaaS commerciali): https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits
- Expo EAS pricing: https://expo.dev/pricing

Sviluppatori e terze parti: vedi § 7. Aggregatori di prezzi 2026 (usati solo se concordi): https://www.saaspricepulse.com, https://costbench.com, https://agentdeals.dev, https://snapdeploy.dev/state-of-free-hosting, https://makerkit.dev, https://www.srvrlss.io.

Cose da **ricontrollare sulle fonti ufficiali** prima della decisione finale: (a) Oracle Always Free: limiti A1, regola di idle reclaim e esenzione PAYG, capacity in Milano/Francoforte; (b) Supabase: pool size del session pooler e connessioni sul free; (c) Auth0 free: RBAC, M2M, Actions, custom domain; (d) Vercel Hobby fair use; (e) Cloud Run: free tier con min-instances, Cloud Scheduler 3 job gratuiti; (f) termini Render free per uso commerciale e riavvii periodici.

---

## 10. Piano di migrazione (se si sceglie Oracle, TOP 1)

Stime in giorni-persona, un agente o sviluppatore.

| # | Task | Dettagli | Stima |
|---|---|---|---|
| H1 | Decisioni PO e account | Risposte alle domande aperte (§ 11); registrazione OCI, PAYG, Budget alert | 0,5 |
| H2 | Provisioning VM | A1 2 OCPU/12 GB, Ubuntu arm64, firewall, Docker, utente non-root, IP riservato | 0,5-1 (capacity variabile) |
| H3 | Configurazione pool | `Hangfire:WorkerCount` (default 4), `Maximum Pool Size`, documentare in `hangfire.md`; test su Postgres con limite connessioni; opzione di avvio sicura (migrazioni) | 0,5-1 |
| H4 | Dockerfile e compose | `docker-compose.prod.yml` (api + Caddy), healthcheck su `/api/health/live`, `restart: unless-stopped`, variabili da `.env`; Dockerfile multi-arch (`TARGETARCH`), valutare ReadyToRun, rimuovere il commento "Railway" | 0,5 |
| H5 | Workflow GitHub Actions | `ci-cd.yml`: sostituire `verify-test`/`verify-prod` (Railway) con job di build+push GHCR (arm64) + deploy SSH + `scripts/verify-deploy.sh` (già pronto: usa `GIT_COMMIT_SHA`); rinominare le variabili `RAILWAY_*_URL` in `API_TEST_URL`/`API_PROD_URL`; `deploy-preview.yml`: aggiornare i link PR | 1 |
| H6 | Ambienti test/prod | Una sola VM (2 OCPU/12 GB) può ospitare **due container** (test, prod) con due schemi/Hangfire schema separati e due host Caddy; oppure test solo su `develop` con un container più piccolo | 0,5 |
| H7 | DNS e TLS | `api.<dominio>` e `api-test.<dominio>` verso la VM, Caddy per i certificati; aggiornare `VITE_API_BASE_URL` (Vercel/Cloudflare), CORS (`Cors__...`), `App__ApiBaseUrl`, Stripe/Auth0 URL di callback/webhook (rieseguire la verifica firma), Auth0 allowed origins | 0,5 |
| H8 | Forwarded headers | Rieseguire `proxy-ip.md` (endpoint diagnostico) con Caddy; impostare `KnownNetworks/KnownProxies` | 0,25 |
| H9 | Backup | Workflow `db-backup.yml` (pg_dump cifrato, R2), test di ripristino documentato | 0,5-1 |
| H10 | Documentazione | **Proposta di modifica** (da applicare da chi può): `.claude/rules/infra.md` (tabella hosting, "Deploy model", "Environment URLs", "Backend API port", "Secrets management": togliere Railway e `RAILWAY_*`), `docs/INFRA.md` (sezioni Railway, Verifica, Keep-alive), `docs/runbooks/deploy-checklist.md` (39 riferimenti a Railway), `hangfire.md` (passi Railway nella sezione problema), `health-checks.md` (log di Railway -> `docker logs`), `proxy-ip.md`, `storage.md` (sezione "Railway variables"), `ci-backend.md`, `seo-domain.md`; testi di `BuildInfo`/`ForwardedHeaders` che citano Railway | 1 |
| H11 | Cutover | Fermare job sul vecchio ambiente (non c'è più), avviare il nuovo, verifica `/api/health/ready`, Stripe webhook test, prova di un job Hangfire reale, monitor esterno (UptimeRobot) | 0,5 |
| H12 | Frontend (separato) | Migrazione da Vercel Hobby a Cloudflare Pages + Functions + domini custom (§ 8) | 5-8 |
| | **Totale backend (H1-H11)** | | **~6-9 giorni** |

Per il Piano B (Render): H3, H4 (immagine amd64), H5 (webhook di deploy o integrazione GitHub nativa di Render), H7, H8, H9, H10: ~3 giorni. Per Cloud Run (TOP 2): in più il tick (2-3 giorni) e la gestione Workload Identity: ~7-9 giorni.

---

## 11. Domande aperte per il PO

1. **Regione del progetto Supabase**: è davvero `eu-central-1` (Francoforte) o `eu-west-1`? (`docs/INFRA.md` è incoerente.) Determina la regione del backend.
2. **Carta di credito**: accetta di inserire una carta (solo verifica) per Oracle (consigliato con PAYG) o Google? Se **no**, resta solo Render/Hugging Face (qualità inferiore) o l'hardware di casa.
3. **Spesa minima**: accetta ~4 EUR/mese (Hetzner o simili) come alternativa a tutti i rischi di Oracle? È la soluzione più sicura per un SaaS con dati personali.
4. **Un ambiente o due**: serve davvero l'ambiente `test` online sempre acceso? Si potrebbe far girare il test solo su richiesta (stessa VM, container fermo).
5. **Vercel**: è disposto a migrare il frontend a Cloudflare Pages ora, o accetta il rischio di restare su Hobby finché non ci sono ricavi? Quando partono i primi pagamenti il rischio contrattuale esiste.
6. **Auth0**: verificherà (o autorizza una verifica) nel dashboard che RBAC, M2M e Actions siano nel piano free? In caso contrario il costo non è zero.
7. **Dati e backup**: accetta il backup `pg_dump` notturno su un servizio terzo (Cloudflare R2/altro account), e chi riceve gli avvisi se fallisce? I dati degli ospiti (GDPR) fuori da Supabase richiedono una riga nel registro dei sub-responsabili (cifratura obbligatoria).
8. **Supabase Pro**: quando i primi clienti paganti entrano, è disposto a passare a Pro (25 USD/mese: backup giornalieri, no pausa)?
9. **Dominio**: ha già un dominio? Dove è gestito il DNS?
10. **Accettazione del rischio account Oracle**: sa che Oracle può sospendere un account gratuito senza preavviso e che il ripiego (Render/Cloud Run) richiederebbe ore di lavoro e un downtime?
11. **Self-hosting casa**: esclude l'uso per la produzione dei dati personali?

---

## 12. Decisione proposta

1. **Subito (oggi, 0,5 giorni)**: se il backend è già spento, mettere il **Piano B Render** come ponte (senza carta) con `WorkerCount=2` e il ping esterno; consapevoli dei limiti.
2. **Entro la settimana**: provisioning Oracle A1 (PAYG, budget alert) e migrazione secondo § 10.
3. **In parallelo**: backup del DB (H9) e verifica Auth0/Vercel (Q5, Q6): sono più importanti dell'hosting stesso.
4. Se Oracle non rilascia capacity entro 7 giorni o l'account viene sospeso: Cloud Run + tick (TOP 2), oppure la spesa minima (Q3).
