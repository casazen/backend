# CasaZen: AI-Oriented Discoverability (GEO/AEO)

Analisi tecnica e requisiti.
Repo analizzati: `casazen/frontend` e `casazen/backend` (branch `develop`)
Stato: BOZZA per revisione | Data: 2026-10-21

---

## 1. Obiettivi

Rendere ogni superficie pubblica di CasaZen individuabile, comprensibile, citabile e prenotabile/contattabile dagli assistenti AI e dalla ricerca AI (ChatGPT / OAI-SearchBot, Gemini / AI Overviews / AI Mode, Perplexity, Claude, Copilot / Bing, Meta AI, Apple, DuckAssist, Mistral, DeepSeek), mantenendo il SEO classico come base.

Superfici in ambito, tutte gestite dal **modulo SEO esistente** (dashboard admin SEO + `PublicSeoController` + `Casazen.Web/Seo/*` + `api/seo.ts` + `api/sitemap.ts`):

| ID | Superficie | Percorso pubblico | `kind` attuale |
|---|---|---|---|
| S1 | Sito marketing CasaZen | `/`, `/funzionalita`, `/per-i-fornitori`, `/chi-siamo` | prerender statico (`middleware.ts`, `scripts/prerender-site.mjs`) |
| S2 | Guide compliance + hub | `/p/affitti-brevi`, `/p/affitti-brevi/:region/:comune` | `hub`, `guide` |
| S3 | Pagine tassa di soggiorno | `/p/tassa-soggiorno/:comune` | `tourist-tax` |
| S4 | Vetrine fornitori | `/fornitori/:slug` | `supplier` |
| S5 | Siti di prenotazione host (path) | `/book/:orgSlug`, `/book/:orgSlug/property/:p` | `org`, `property` |
| S6 | Siti host su host proprio (sottodominio / dominio custom) | `/`, `/property/:p` | `host` |

Obiettivi misurabili (vedi sezione 9):

1. Ogni pagina pubblicata e' scaricabile dai crawler AI come HTML completo, senza JavaScript.
2. Ogni entita' (CasaZen, org host, immobile, fornitore, guida comune) espone dati strutturati coerenti e una rappresentazione machine-readable.
3. Le pagine rispondono a domande reali in una forma citabile da un LLM (breve, autosufficiente, con fonti, datata).
4. Traffico e conversioni dai canali AI sono misurati (nel rispetto della regola A8-11: nessun dato personale).
5. Nulla e' inventato: solo dati presenti nel DB, solo testi approvati da una persona (regola SE-01 invariata).

---

## 2. Stato attuale (verificato nel codice)

- **HTML per crawler via rewrite su user-agent.** `vercel.json` riscrive le richieste il cui `user-agent` corrisponde a un crawler verso `/api/seo?kind=...`; le persone continuano a ricevere la SPA. La regex contiene gia' `GPTBot`, `OAI-SearchBot`, `ChatGPT-User`, `ClaudeBot`, `Claude-User`, `Claude-SearchBot`, `Claude-Web`, `anthropic-ai`, `PerplexityBot`, `Perplexity-User`, `MistralAI-User`, `CCBot`, `cohere-ai`, `YouBot`, `DuckAssistBot`, `Applebot`, `Bytespider`, `Amazonbot`, `Meta-ExternalAgent`, `Meta-ExternalFetcher`, `Googlebot`, `bingbot`, piu' tool SEO e bot di link-preview.
- **`api/seo.ts`** valida gli slug, inoltra a `GET {API}/public/seo/...`, passa l'Host originale (per i domini custom delle org), gestisce 301/404/503 e risponde con `Cache-Control: no-store` + `Vary: User-Agent`. Fuori produzione: `X-Robots-Tag: noindex, nofollow`.
- **`SeoJsonLd.cs`** costruisce JSON-LD schema.org: `Organization`, `ProfessionalService` (fornitore, `areaServed`, senza telefono/email/P.IVA/indirizzo), `ItemList`, `BreadcrumbList`, `Article`, `CollectionPage`, `VacationRental` (con `Accommodation`/`EntirePlace`, servizi, coordinate arrotondate a ~1 km, prezzo notte in `makesOffer`). I dati assenti sono omessi (niente rating, check-in, disponibilita').
- **Sitemap**: `/sitemap.xml` (guide + tassa + hub), `/sitemap-book.xml` (indice), `/book/:org/sitemap.xml`, `/sitemap-supplier.xml`, servite da `api/sitemap.ts` con cache CDN 1 h + SWR 1 giorno; `lastmod` = data di approvazione / `UpdatedAt`.
- **robots.txt** statico generato in build (`src/config/robots-txt.ts`): produzione `Allow: /` + disallow privati + righe sitemap; preview `Disallow: /`. Non dipende mai dall'API.
- **Governance dei contenuti (SE-01)**: il testo generato da AI e' una *bozza*; e' pubblico solo una revisione approvata da un admin (`PublishedRevisionId`); prompt versionato (`SeoContentPrompt.VersionFor`); validazione output (`ContentStatus`).
- **Funnel (SE-03/04)**: attribuzione iscrizioni via UTM, `SeoEvents` (`cta_click`, `signup_start`), widget admin "Comuni che convertono", retention 90 giorni, nessun dato personale.
- **Sito marketing statico (SEO-VETRINA)**: `middleware.ts` serve `dist/_site/*.html` sull'host dell'app; sugli host delle org le richieste passano invariate.
- **Host org (BK-16/17)**: sottodominio `{label}.{PublicHost__BaseDomain}` e domini custom verificati servono il sito di prenotazione alla radice con canonical e sitemap propri.

Non trovato nei repo (gap, da confermare con un crawl reale):

- Nessun `llms.txt` / `llms-full.txt`, nessuna rappresentazione Markdown o JSON delle pagine per gli agenti.
- Nessun `FAQPage`, `Review`/`AggregateRating` (nessun sistema di recensioni per le pagine immobile; `site-reviews.md` esiste, da verificare), nessun `LodgingBusiness`/`Place` per l'org host, nessun `WebSite`/`SearchAction`, `sameAs`, `speakable`.
- `Cache-Control: no-store` sulle pagine crawler: ogni scansione colpisce Vercel Function + API Railway (timeout 8 s); i crawler AI possono rinunciare su pagine lente o 5xx.
- Il matching per UA lascia gli agenti nuovi/sconosciuti (o con UA da browser) con la shell SPA vuota.
- Nessuna analytics AI (hit dei crawler, referral dagli assistenti, monitoraggio della visibilita' nei prompt).
- Nessun controllo di coerenza delle entita', nessun IndexNow/Bing, nessun punteggio di visibilita' AI per org nella console host.

---

## 3. Findings e rischi

| # | Finding | Impatto | Severita' |
|---|---|---|---|
| F1 | Le pagine per AI esistono solo per UA corrispondenti alla regex (dynamic rendering). Molti fetcher AI non eseguono JavaScript e alcuni usano UA generici. | Invisibili agli agenti non in elenco; manutenzione della regex; Google considera il dynamic rendering un workaround. | Alta |
| F2 | `no-store` + `Vary: User-Agent` sulle risposte crawler. | Spreco di crawl budget, timeout sotto carico, nessuna protezione CDN dai picchi di crawler AI. | Alta |
| F3 | Nessun file di guida machine-readable (`llms.txt`) ne' alternative Markdown. | Minore probabilita' che gli agenti scelgano le pagine migliori; token sprecati in markup. | Media |
| F4 | I dati strutturati coprono le entita' ma non domande, recensioni, FAQ, identita' dell'organizzazione (`sameAs`), orari/contatti dell'host. | Grounding delle entita' piu' debole, meno risposte ricche. | Media |
| F5 | I siti host vivono su molti host/sottodomini; contenuti duplicati o sottili tra org; il canonical e' per host. | Cannibalizzazione, segnali di "thin content". | Media |
| F6 | Nessuna misurazione del traffico AI. | Impossibile provare il ROI o ottimizzare. | Media |
| F7 | Approvazione manuale e legale (prime 100 pagine con `CounselRequired`). | Limite di throughput per la scala programmatica: gestire con prioritizzazione e strumenti, mai con auto-approvazione. | Media |
| F8 | La precisione geo e' volutamente ~1 km (DB-03) e i fornitori non espongono telefono/email. | Da mantenere; il lavoro AI non deve indebolire la privacy. | Vincolo |

---

## 4. Architettura target

```
                  Assistenti AI / crawler AI / utenti
                                |
                 Vercel edge (middleware + CDN)
        +-----------------------+-----------------------+
        | qualsiasi client      | umani (UA browser)    |
        | -> HTML pre-renderizzato (CDN) | -> SPA (come oggi) |
        +-----------------------+-----------------------+
                                |
      api/seo.ts (registro kind, cache, content negotiation)
                                |
      Backend  /public/seo/*  (unica fonte di verita', solo dati approvati)
        |- SeoContentService      (guide, tassa)             [esiste]
        |- PublicSeoService       (org, immobile, fornitore) [esiste]
        |- SeoJsonLd              (builder schema.org)       [estendere]
        |- SeoHtmlRenderer        (HTML)                     [estendere]
        |- SeoMarkdownRenderer    (alternate text/markdown)  [nuovo]
        |- LlmsTxtBuilder         (llms.txt per host)        [nuovo]
        |- AiVisibilityService    (monitoraggio, scoring)    [nuovo]
        '- SeoEvents / AiCrawlerHits (analytics senza PII)   [estendere]
```

Principi di design:

- **Un renderer, molte rappresentazioni**: gli stessi dati approvati producono HTML + JSON-LD + Markdown + voci `llms.txt`. Mai scritti a mano per canale.
- **Renderizzare per tutti, non per user agent** (progressivamente): lo snapshot HTML e' un artefatto di prima classe, in cache CDN. Il matching UA resta solo come fallback di compatibilita' durante la migrazione.
- **Il backend possiede contenuti e regole** (come oggi). Il layer Vercel instrada, valida, mette in cache e negozia il content type.
- **Guidato da configurazione**: elenco bot AI, policy `llms.txt` e soglie in configurazione, non regex hard-coded in `vercel.json`.
- **Nessun dominio nel codice** (decisione D3) e **nessun dato inventato** (BK-15) restano vincolanti.

---

## 5. Requisiti funzionali

Priorita': **P0** necessario al rilascio AI-oriented, **P1** successivo, **P2** piu' avanti. I codici superficie si riferiscono alla sezione 1.

### 5.1 Crawlability e rendering

| ID | Pri | Requisito | Superfici | Criteri di accettazione |
|---|---|---|---|---|
| AI-01 | P0 | Servire HTML completo (title, meta, canonical, hreflang, OG, JSON-LD, testo visibile, link interni) a **tutti** i client che non sono una sessione browser interattiva, non solo agli UA che corrispondono alla regex. Approccio: servire lo snapshot quando mancano segnali di browser con JS OPPURE rendere lo snapshot HTML la risposta di default con idratazione SPA sopra (preferito a lungo termine). | S2-S6 | `curl -A "UnknownBot/1.0" <url>` restituisce l'HTML completo; la matrice di test copre tutti i kind. |
| AI-02 | P0 | Rendere l'elenco dei crawler **configurazione**, non una regex copiata in 8 rewrite: un'unica fonte (modulo JSON/TS) usata dalla generazione di `vercel.json` (script di build) e dai test. Aggiungere agenti mancanti (es. `Applebot-Extended` come token robots, `Diffbot`, `ImagesiftBot`, `Timpibot`, `Kagibot`, `PhindBot`, `Google-CloudVertexBot`, browser agentici con UA documentato). Elenco rivisto ogni trimestre. | tutte | Un file elenca i bot con vendor, scopo (training / search / user-fetch), URL della doc e data ultima revisione; un test CI fallisce se `vercel.json` si discosta. |
| AI-03 | P0 | Sostituire `Cache-Control: no-store` su 200/404 delle pagine crawler con `public, s-maxage=300..3600, stale-while-revalidate=86400` e **invalidazione mirata** quando un admin approva/ritira una revisione o un host modifica un immobile pubblicato (purge per tag/path). Mantenere `no-store` sui 503. | S2-S6 | La seconda richiesta dalla CDN e' un HIT; una pagina ritirata sparisce entro 5 min (o subito con purge). |
| AI-04 | P0 | Resilienza: stale-if-error dalla CDN, budget di timeout (<= 2 s p95 su cache miss), mai 5xx per una pagina pubblicata quando esiste una copia stale. | S2-S6 | Test con fault injection: API giu', la pagina in cache risponde 200. |
| AI-05 | P1 | Correttezza di `Vary`: se il contenuto dipende dall'UA solo durante la migrazione, emettere `Vary: User-Agent`; quando AI-01 (snapshot di default) e' in produzione, rimuoverlo. | tutte | Test sugli header. |
| AI-06 | P1 | Pre-renderizzare S2/S3 (guide, tassa) al momento della pubblicazione in pagine statiche/ISR per i comuni a maggior traffico; mantenere il percorso dinamico per la coda lunga. | S2, S3 | TTFB dei top-N comuni < 200 ms dall'edge. |
| AI-07 | P1 | Budget di Core Web Vitals e dimensione per gli snapshot: HTML < 200 KB, nessuno script di terze parti bloccante. | tutte | Budget Lighthouse CI. |

### 5.2 robots.txt e policy dei crawler

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-10 | P0 | Policy esplicita e documentata per classe di crawler nel `robots.txt` (build-time, come oggi): **bot di ricerca/risposta consentiti** (`OAI-SearchBot`, `ChatGPT-User`, `PerplexityBot`, `Perplexity-User`, `Claude-SearchBot`, `Claude-User`, `Googlebot`, `bingbot`, `DuckAssistBot`, `Applebot`); **bot di training** (`GPTBot`, `ClaudeBot`, `Google-Extended`, `Applebot-Extended`, `CCBot`, `Meta-ExternalAgent`, `Bytespider`, `anthropic-ai`) decisi dal product owner (proposta di default: consentiti per le pagine editoriali pubbliche S1-S4 e S6; consentiti S5; percorsi privati sempre disallow). | Tabella di policy nel repo (`docs/runbooks/ai-visibility.md`); `robots.txt` generato dalla policy; test unitario. |
| AI-11 | P0 | Mantenere i disallow privati (`/app/`, `/checkin/`, `/login`, `/book/*/my-bookings`, `/book/*/booking/`, `/book/*/requests/`, `/book/*/property/*/checkout`); aggiungere `/signup` e i percorsi API. Non bloccare mai CSS/JS/immagini necessari al rendering. | Test sul file generato. |
| AI-12 | P1 | `robots.txt` per singolo host org (sottodomini/domini custom): servito per host con la riga sitemap dell'host, `Disallow` se l'host non e' verificato. Oggi il robots e' un unico file statico. | `curl https://{host}/robots.txt` restituisce sitemap e policy dell'host. |
| AI-13 | P2 | Opt-out opzionale per org dai crawler di training (impostazione host) riflesso in robots e in meta `noai` dove supportato. | Impostazione org + test. |

### 5.3 llms.txt e alternative machine-readable

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-20 | P0 | `GET /llms.txt` sull'host dell'app: H1 con nome del sito, blockquote di sintesi, sezioni curate (Prodotto, Guide per regione, Strumenti, Fornitori, Legale), ogni link con descrizione di una riga. Costruito dal backend dalle pagine pubblicate (stesso filtro della sitemap) e servito da una funzione `api/` con cache CDN. | Formato valido secondo llmstxt.org; solo URL pubblicati; stesso host del canonical. |
| AI-21 | P0 | `GET /book/:orgSlug/llms.txt` e `/llms.txt` sugli host delle org: riassunto dell'org, elenco degli immobili pubblicati (nome, descrizione di una riga, URL), percorsi di prenotazione e contatto, policy. | Presente per ogni org con >= 1 immobile pubblicato; 404 altrimenti. |
| AI-22 | P1 | `llms-full.txt` per S1-S3 (Markdown pulito concatenato dei testi approvati) con limite di dimensione e suddivisione per sezione. | Dimensione < 1 MB; costruito solo da revisioni approvate. |
| AI-23 | P0 | **Alternativa Markdown** per ogni pagina indicizzata: `<url>.md` e/o negoziazione `Accept: text/markdown`, piu' `<link rel="alternate" type="text/markdown">` nell'head. Contenuto = titolo, sintesi, heading, corpo, fonti, ultima modifica, link canonical. | Test round-trip: il Markdown contiene gli stessi fatti dell'HTML; nessun rumore di markup. |
| AI-24 | P1 | **Alternativa JSON** (`application/json` / `.json`) per immobili e fornitori con gli stessi campi del JSON-LD piu' ID stabili, updated-at e URL canonical. Lettura pubblica, rate-limited (`PublicRead`). Mai PII, coordinate esatte, telefono/email salvo pubblicazione esplicita dell'host. | Schema documentato (OpenAPI); test di privacy (DB-03). |
| AI-25 | P2 | Descrittori pubblici in `/.well-known/` per gli agenti (es. MCP server card / discovery OpenAPI) quando AI-60 e' in ambito. | Rivisto dopo AI-60. |

### 5.4 Dati strutturati (JSON-LD) e grafo delle entita'

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-30 | P0 | Usare ancore `@id` e un unico grafo connesso per pagina (`@graph`): `WebSite` -> `Organization` -> `WebPage`/`Article`/`VacationRental` -> `BreadcrumbList`. ID stabili tra le release (`{canonical}#org`, `#property`, `#page`). | Rich Results Test + Schema Validator ok; nessun nodo orfano. |
| AI-31 | P0 | `Organization` per CasaZen e per ogni org host con `url`, `logo`, `description`, `sameAs` (solo profili inseriti dal proprietario: Google Business, social, URL annunci Airbnb/Booking), `contactPoint` solo se l'host sceglie di pubblicarlo. | `sameAs` modificabile dall'org e validato (https). |
| AI-32 | P0 | `FAQPage` generato da blocchi FAQ **visibili** nella pagina (S1 prezzi/compliance, S2 guide, S3 tassa, S5/S6 FAQ immobile). Il testo FAQ fa parte della revisione approvata (stesso flusso SE-01). | FAQ nel JSON-LD == FAQ nell'HTML (diff automatico). |
| AI-33 | P0 | Aggiunte a `VacationRental` quando i dati esistono: `checkinTime`/`checkoutTime`, `petsAllowed` (campo esplicito true/false), `numberOfRooms`, `floorSize`, `availableLanguage`; `amenityFeature` normalizzato su un elenco controllato (chiavi mappate a termini schema.org). Campi **omessi** quando il DB non ha valore (regola BK-15). | Test unitari sull'omissione; nuovi campi host nell'editor immobile (orari check-in/out, regola animali, regole della casa). |
| AI-34 | P1 | `Review` / `AggregateRating` per pagine immobile e fornitore **solo** da recensioni verificate di prima parte (vedi `site-reviews.md`) con conteggio minimo e recenza; mai rating dell'org su se' stessa. | Policy documentata; rispettate le linee guida Google sui review snippet. |
| AI-35 | P1 | `Place`/`TouristDestination` + `containedInPlace` (comune -> provincia -> regione) per le guide con codice ISTAT come `identifier`; collegare guida <-> immobili in evidenza (`ItemList`) <-> pagina tassa. | Il grafo valida; codici ISTAT presenti. |
| AI-36 | P1 | `Event`/`Offer` per promozioni host o soggiorni a prezzo fisso solo se il prodotto li ha; altrimenti niente. | n/a |
| AI-37 | P1 | Miglioramenti `Article`: `author` (persona o ruolo org), `datePublished`, `dateModified` (data di approvazione), `citation`/`isBasedOn` per le fonti ufficiali (Ministero, Regione, Comune), `about` (Place). | Presenti su S2/S3. |
| AI-38 | P2 | `speakable` sui blocchi di sintesi per gli assistenti vocali. | n/a |
| AI-39 | P0 | **Regola della fonte unica**: ogni fatto nel JSON-LD esiste nell'HTML visibile; i test confrontano entrambi. | Test CI per kind. |

### 5.5 Contenuti progettati per le risposte (GEO/AEO)

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-40 | P0 | Contratto di template per il testo generato/approvato: (1) **risposta diretta** di 40-60 parole in alto; (2) `<h2>` in forma di domanda; (3) paragrafi brevi e autosufficienti; (4) tabelle/liste per tariffe, scadenze, obblighi; (5) sezione **fonti** con URL ufficiali; (6) riga visibile "Aggiornato il {data}" e "Verificato da {ruolo}". Imposto in `SeoContentPrompt` (nuova stringa di versione) e dal validatore di output (nuovo `ContentStatus` per sintesi/fonti mancanti). | Il validatore rifiuta bozze senza sintesi/fonti; versione prompt aggiornata; la regola Lombardia continua a funzionare. |
| AI-41 | P0 | Freschezza: `dateModified` = data di approvazione; rigenerazione su cambio normativo/tariffa (refresh mensile esistente) e coda admin "da rivedere" per pagine obsolete (> 180 giorni). | Filtro nella lista admin + notifica. |
| AI-42 | P0 | Fatti solo dalle fonti verificate del prompt (regola esistente) + valori numerici da tabelle live (tassa di soggiorno) resi **lato server nella pagina** (non solo via calcolatore JS), cosi' che l'AI possa citare le tariffe. | L'HTML contiene la tabella tariffe per ogni comune con tariffa in vigore. |
| AI-43 | P1 | Matrice di copertura per intento: per ogni comune generare le pagine per le domande di ospiti/host ("tassa di soggiorno {comune} importo", "CIN obbligatorio {comune}", "Alloggiati Web come funziona", "CIR Lombardia"). Report dei gap in admin: comuni senza pagina per intento. | Dashboard di copertura. |
| AI-44 | P1 | **Pagine host/immobile** (S5/S6): paragrafo di sintesi generato da campi strutturati ("Appartamento per 4 ospiti a Como, 2 camere, vicino a ..."), fatti su quartiere/POI da dati verificati (distanza da stazione/lago) e FAQ (check-in, parcheggio, animali, tassa di soggiorno, cancellazione). L'host modifica e approva; nessuna affermazione allucinata. | L'editor immobile ha "Anteprima AI" e "Approva testo pubblico"; stesso audit di approvazione SE-01. |
| AI-45 | P1 | **Vetrine fornitori** (S4): elenco servizi, aree servite (comuni), lingue, tempi di risposta quando misurati, fasce di prezzo solo se inserite dal fornitore, FAQ. Nessun telefono/email/P.IVA/indirizzo nei dati pubblici (regola esistente); contatto tramite form di richiesta CasaZen. | Test di privacy; `areaServed` collegato alle guide dei comuni. |
| AI-46 | P1 | Link interni: guida <-> tassa <-> immobili in evidenza <-> fornitori per comune; hub per regione/provincia; breadcrumb ovunque. | Report del grafo dei link: nessuna pagina pubblicata con 0 link in ingresso. |
| AI-47 | P1 | Lingue: IT (default) + EN (e DE/FR per i comuni turistici principali). Cluster `hreflang`; il testo EN generato da AI passa per la stessa approvazione. | Validazione `hreflang` reciproca. |
| AI-48 | P2 | Pagine di dati originali con statistiche citabili (es. confronti aggregati e anonimi delle tariffe di tassa di soggiorno per provincia) aggiornate mensilmente, con metodologia. | n/a |
| AI-49 | P0 | Niente cloaking: lo snapshot mostrato ai bot contiene le stesse informazioni che vede una persona. Documentato nel runbook e testato. | Test diff snapshot vs. testo SPA renderizzato. |

### 5.6 Sitemap e segnali di indicizzazione

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-50 | P0 | Controlli di accuratezza di `<lastmod>`, alternative `xhtml:link hreflang` e (opzionale) `image:image` nelle sitemap; suddivisione per kind (`sitemap-guides`, `sitemap-tax`, `sitemap-book`, `sitemap-supplier`) sotto un unico indice `sitemap.xml`. | Nessun errore in Search Console. |
| AI-51 | P0 | **IndexNow** (Bing, Yandex, altri) su pubblicazione/ritiro/aggiornamento: un job backend invia gli URL modificati (+ file chiave alla radice, per host). | Job Hangfire; log degli invii; file chiave per host. |
| AI-52 | P1 | Verifica Google Search Console + Bing Webmaster per il dominio principale; i domini custom delle org offrono una guida opzionale nella console host. | Runbook aggiornato (`seo-domain.md` sezione 4). |
| AI-53 | P1 | Igiene dei canonical: un canonical per entita' tra path (`/book/slug`) e host (`slug.base-domain`/dominio custom); i non-canonical rispondono 301. | Matrice di test per tutte le combinazioni host/path. |

### 5.7 Azioni per gli agenti (l'AI puo' prenotare/contattare)

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-60 | P2 | **Server MCP / OpenAPI in sola lettura** che espone immobili pubblicati, riepiloghi di disponibilita' e calcolatore della tassa di soggiorno per assistenti agentici; rate-limited e con chiave. | Documento di specifica; sandbox. |
| AI-61 | P2 | Deep link con parametri precompilati per la prenotazione (`checkin`, `checkout`, `guests`) e convenzione `utm_source=ai`; la richiesta di prenotazione resta sul sito dell'host (direct booking). | Generatore di link nella console host. |
| AI-62 | P2 | Calcolatore della tassa di soggiorno esposto come API/tool stateless (`GET /public/tourist-tax/{comune}/calc`) per gli assistenti. | Documentato; rate-limited. |

### 5.8 Misurazione e monitoraggio

| ID | Pri | Requisito | Criteri di accettazione |
|---|---|---|---|
| AI-70 | P0 | **Analytics dei crawler AI** senza dati personali: conteggio hit per `(bot_name, kind, status, giorno)` in una nuova tabella `AiCrawlerHits` (niente IP, niente UA completo, niente path con PII). Retention 90 giorni (stesso schema di `SeoEvents`). Scritta da `api/seo.ts` (chiamata fire-and-forget) o da un log drain Vercel. | Widget admin "Bot AI per tipo di pagina". |
| AI-71 | P0 | **Tracciamento referral AI**: classificare referrer/UTM da `chatgpt.com`, `chat.openai.com`, `perplexity.ai`, `gemini.google.com`, `copilot.microsoft.com`, `claude.ai`, `you.com`, `duckduckgo.com` (AI) e `utm_source=chatgpt.com` nel canale `ai-assistant`; estendere `SeoEvents` / `SignupAttributions` con `Channel`. Lato host: stessa cosa per le richieste di prenotazione (attribuzione per org). | Admin: iscrizioni e richieste per canale; console org: prenotazioni per canale. |
| AI-72 | P1 | **Monitor di visibilita' nei prompt**: un job Hangfire settimanale esegue un set curato di prompt per comune/intento contro le API AI configurate (dove i termini lo consentono) o esporta per controlli manuali; salva *brand citato? URL citato? posizione* (nessun dato utente). Dashboard "Share of voice" CasaZen vs. concorrenti e per-org per gli host (premium). | Tabella `AiVisibilityChecks`; report; impostazione di tetto di costo. |
| AI-73 | P1 | **Controllo di coerenza delle entita'** per org/immobile: nome, indirizzo (comune), telefono (se pubblico), URL tra sito, JSON-LD e profili `sameAs`; alert sulle discrepanze. | Punteggio 0-100 per org nella console. |
| AI-74 | P1 | **Punteggio di AI-readiness** per pagina e per org (HTML completo, JSON-LD valido, FAQ, sintesi, fonti, freschezza, immagini con alt, voce llms, hreflang). Mostrato nella dashboard admin SEO e nella console host come checklist con correzioni. | Punteggio calcolato alla pubblicazione; visibile. |
| AI-75 | P1 | Estendere il widget admin "Comuni che convertono" con la divisione per canale AI e le top landing page. | Query + UI. |
| AI-76 | P2 | Import dell'API Search Console (click/impression per pagina) nella dashboard. | n/a |

### 5.9 Sezione SEO admin (unico punto di controllo)

Tutto quanto sopra e' gestito dalla **sezione SEO admin esistente**; aggiungere:

| ID | Pri | Requisito |
|---|---|---|
| AI-80 | P0 | Tab **"AI visibility"**: vista della policy dei crawler, hit dei bot (AI-70), referral (AI-71), punteggi di readiness (AI-74), coda delle pagine obsolete (AI-41), gap di copertura (AI-43). |
| AI-81 | P0 | La schermata di revisione mostra: box di sintesi, FAQ, fonti, anteprima JSON-LD, anteprima Markdown, esiti del validatore; "Approva e pubblica" mantiene le regole SE-01 (conferma, nota, gestione 409/422). |
| AI-82 | P1 | Strumenti bulk: rigenerare per versione di prompt/regione, mettere in coda per potenziale di traffico (popolazione, arrivi turistici, domanda di ricerca), approvazione in blocco **solo dopo flag di revisione individuale per pagina** (nessuna auto-approvazione). |
| AI-83 | P1 | Template e prompt versionati nel repo; diff tra versioni di prompt; A/B test dei template per coorti di comuni con le metriche della sezione 9. |
| AI-84 | P1 | Console host (livello org): card "Visibilita' AI" con checklist di readiness per sito di prenotazione/immobili (foto, descrizione, servizi, regole, FAQ, orari check-in, `sameAs`) e azioni in un clic. |
| AI-85 | P2 | Console fornitori: stessa checklist per le vetrine (aree, servizi, lingue). |

---

## 6. Requisiti multi-tenant (siti host e fornitori)

- **Isolamento**: ogni output JSON-LD/Markdown/llms contiene solo i dati dell'org di quell'host; test con due org (nessuna fuga).
- **Risoluzione dell'host**: riusare `PublicHostResolver`; dominio custom non verificato => `noindex` + 404 per llms/robots (come oggi per le pagine).
- **Branding**: le pagine org portano il brand dell'host, non quello di CasaZen (regola esistente); CasaZen compare come `provider`/`publisher` solo in un link nel footer e nel grafo `WebSite` dove appropriato.
- **Protezione dai contenuti duplicati**: controllo automatico di unicita' sul testo immobile generato (similarita' di shingle tra immobili dello stesso comune); numero minimo di fatti unici prima della pubblicazione.
- **Quote**: generazione di testo AI per org in base al piano (controllo costi, vedi `Ai:Provider` e `ai.md`).
- **Modalita' sottodominio** (`PublicHost__BaseDomain`): DNS wildcard; ogni sottodominio ha sitemap, robots, llms.txt e chiave IndexNow propri.

---

## 7. Requisiti non funzionali

| Area | Requisito |
|---|---|
| Performance | TTFB p95 dalla cache edge < 300 ms; render a freddo < 2 s; sitemap < 1 s. |
| Disponibilita' | Le pagine pubblicate non danno mai 5xx quando esiste una copia in cache; robots.txt indipendente dall'API. |
| Sicurezza | Validazione degli slug come oggi; nessun open redirect (esistente `isSafeRedirectPath`); rate limit su JSON pubblico ed endpoint di analytics; nessun segreto negli snapshot. |
| Privacy/GDPR | Nessun IP, user id, cookie, UA completo o referrer completo salvato; solo contatori; retention 90 giorni; informativa privacy aggiornata (D14). Coordinate arrotondate (DB-03). Dati di contatto dei fornitori mai pubblici. |
| Compliance | Testo AI approvato da una persona (SE-01); disclaimer legali sui contenuti di compliance ("non e' consulenza legale"); indicazione dell'uso di AI dove richiesto dagli obblighi di trasparenza dell'EU AI Act. |
| Osservabilita' | Metriche: latenza di render, cache hit ratio, 4xx/5xx per kind, hit dei bot per classe, tasso di successo della generazione (`ContentStatus`), tempo di approvazione. Alert su 5xx > 1% e su sitemap `503`. |
| Test | Unit (builder, validatori), contract (HTML vs JSON-LD vs Markdown), routing (`vercel-routing.test.ts` esteso), e2e (Playwright) su un'org fixture; validazione schema in CI. |
| Config | Nessun dominio nel codice (D3); tutte le nuove opzioni in `appsettings` con validatori (come `PublicSiteOptionsValidator`). |
| Docs | Nuovo runbook `docs/runbooks/ai-visibility.md`; aggiornare `seo-domain.md`, `seo-funnel.md`, `suppliers.md`. |

---

## 8. Piano di consegna

| Fase | Ambito | Criteri di uscita |
|---|---|---|
| **0 - Baseline (1 settimana)** | Audit con crawl di S1-S6 come GPTBot / ClaudeBot / PerplexityBot / UA sconosciuto; misurare chi riceve la shell vuota; set di prompt baseline (50); setup Search Console/Bing. | Report con F1-F8 confermati; metriche baseline salvate. |
| **1 - Fondamenta (2-3 settimane)** | AI-01, 02, 03, 04, 10, 11, 20, 21, 23, 30, 31, 39, 49, 70, 71. | Il test con UA sconosciuto passa; HIT in CDN; llms.txt online; grafo JSON-LD; contatori bot/referral attivi. |
| **2 - Motore dei contenuti (3-4 settimane)** | AI-32, 33, 40, 41, 42, 43, 44, 45, 46, 50, 51, 80, 81. | La nuova versione di prompt genera bozze conformi; FAQ + sintesi + fonti ovunque; tab admin attivo. |
| **3 - Scala e misura (3-4 settimane)** | AI-12, 22, 24, 34, 35, 37, 47, 53, 72, 73, 74, 82, 83, 84. | Card di readiness nella console host; monitor dei prompt settimanale; multilingua per i comuni principali. |
| **4 - Agenti (dopo)** | AI-60 a AI-62, 25, 85, 13, 36, 38, 48, 76. | Beta MCP/OpenAPI. |

Ticket suggeriti: un epic "AI-ORIENTED" con una story per ID di requisito; riusare la nomenclatura degli audit (es. `AI-01`) come per `SE-0x` / `BK-1x`.

---

## 9. KPI e accettazione del programma

- **Copertura**: 100% delle pagine pubblicate restituisce HTML completo a un UA sconosciuto; 100% ha un grafo JSON-LD valido; >= 95% ha sintesi + FAQ + fonti.
- **Scoperta**: hit settimanali dei crawler AI (per classe di bot) 3x rispetto alla baseline entro 8 settimane; pagine indicizzate in Google/Bing >= 90% degli URL in sitemap.
- **Visibilita'**: share of voice nel set di prompt curato (CasaZen citata o linkata) dalla baseline al target concordato in fase 0 (proposta: +20 punti sui prompt di compliance per comune in 12 settimane).
- **Traffico**: sessioni dal canale `ai-assistant` e organico; `signup_start` e richieste di prenotazione per canale; tassi di conversione per comune.
- **Qualita'**: pagine obsolete (> 180 giorni) < 5%; tempo di approvazione < 5 giorni lavorativi; zero incidenti di privacy; zero diff di cloaking.

---

## 10. Domande aperte per il product owner

1. Policy sui crawler di training (AI-10): consentire, consentire solo le pagine editoriali o bloccare? Impatta S1-S4 vs. siti host.
2. Dati di recensione per gli immobili (AI-34): esiste un sistema di recensioni di prima parte da esporre o e' fuori ambito?
3. Lingue: quali comuni EN/DE/FR per primi (elenco basato sugli arrivi)?
4. Gli orari di check-in/out, le regole della casa e la regola animali dell'host diventeranno campi obbligatori (servono per AI-33/AI-44)?
5. Tetto di budget per la generazione AI e per le chiamate API del monitor dei prompt (AI-72).
6. Vogliamo il "Compliance badge" (AI-STRATEGY 4.3 #6) come asset di backlink verificato in questo programma (aggiunge `sameAs`/backlink dagli annunci host)?
7. E' previsto un livello a pagamento "AI visibility" per gli host (AI-72/73/84)?

---

## 11. Vincoli ereditati dai repo (da non violare)

- D3: nessun dominio nel codice; D14: testi legali forniti dal proprietario.
- SE-01: nessuna pubblicazione senza approvazione umana; `CounselRequired` per le prime 100 pagine.
- BK-15: il JSON-LD contiene solo dati reali; nessun rating, orario o disponibilita' inventati.
- DB-03: coordinate pubbliche arrotondate a ~1 km.
- Fornitori: mai pubblicare telefono, email, partita IVA, indirizzo.
- A8-11: analytics senza dati personali; solo contatori.
- robots.txt non deve dipendere dall'API.

## 12. File probabilmente interessati

Frontend: `vercel.json` (elenco bot generato, nuovi rewrite), `api/seo.ts` (cache, negoziazione, kind `llms`, `markdown`, `json`), `api/sitemap.ts` (split + hreflang), `middleware.ts` (header S1), `src/config/robots-txt.ts` (policy), `scripts/prerender-site.mjs` (llms/markdown per S1), `src/site/*` (blocchi FAQ), pagine admin SEO, card console host, test.
Backend: `Casazen.Web/Seo/SeoJsonLd.cs`, `SeoHtmlRenderer.cs`, `PublicSeoService.cs`, nuovi `SeoMarkdownRenderer`, `LlmsTxtBuilder`, `SeoContentPrompt` (nuova versione), validatori `SeoGeneratedContent`, `PublicSeoController` (nuove route), nuove entita'/migrazioni (`AiCrawlerHits`, `AiVisibilityChecks`, colonne canale), job Hangfire (IndexNow, monitor visibilita', obsolescenza), controller admin, docs/runbook.

Nota: l'analisi si basa su `vercel.json`, `middleware.ts`, `api/seo.ts`, `api/sitemap.ts`, `SeoJsonLd.cs`, `docs/runbooks/seo-domain.md` (prima parte), `seo-funnel.md` e `docs/AI-STRATEGY.md`. I componenti UI admin SEO, `PublicSeoService.cs`, `SeoHtmlRenderer.cs`, le sezioni 8-10 del runbook e `suppliers.md` non sono stati letti riga per riga; confermare gli item AI-xx prima di aprire i ticket.
