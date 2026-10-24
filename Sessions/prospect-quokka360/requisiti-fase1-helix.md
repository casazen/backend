# Requisiti per Helix — Quokka 360, fase 1 (sviluppo senza fornitori terzi)

Revisione 1 · 23 ottobre 2026 · Stato: **in sviluppo** (requisiti pronti per Helix).

Questo è il documento che guida la **prima fase** del pilota Quokka 360 e delle funzionalità Agenzia, e quello da fornire a Helix. Contiene solo il lavoro che si può sviluppare, collaudare e dimostrare **senza contratti, credenziali, sandbox o approvazioni di soggetti esterni**. Dove alla fine serve un collegamento reale (un ente, un fornitore), in questa fase si costruisce la porta con un adattatore finto e un flag spento: il collegamento reale appartiene a una fase successiva.

**Fonti e precedenze**

- Prodotto: Quokka v10, `Sessions/prospect-quokka360/analisi-funzionalita.md` nella [PR #553](https://github.com/casazen/backend/pull/553), ancora aperta, commit `e67defeff9d600bfc770598626b0eff3fae9e5d6`. Il file integrato in `develop` è la v4: non usarlo per le decisioni.
- Planning: [PLANNING.md](../PLANNING.md) e [intervista del 20 ottobre](../INTERVISTA-PLANNING-2026-10-20.md).
- Codice: `develop` del 23 ottobre (backend `9971df9a`, frontend `3c78af6`, mobile `3cf8dd7`). La sezione 3 indica punti di riuso, non attesta che una funzione sia operativa: Helix rifà il confronto con il codice corrente.
- Questo documento prevale sulla v10 per il **perimetro** della fase 1; la v10 resta la fonte delle decisioni di prodotto (sezione 2).

**Escluso: il channel management** (WP2 della v10: client del channel manager, mappatura, contenuti, disponibilità e tariffe, prenotazioni via API, inbox delle OTA, risposte AI, recensioni delle OTA). È un punto ancora aperto del PO, che sta valutando cosa offre il fornitore. La specifica Octorate ([PR #558](https://github.com/casazen/backend/pull/558)) resta una traccia separata: non implementarla e non anticiparne i contratti in questa fase.

## 0. Come usare questo documento

- Ogni requisito ha un identificativo `Q1-nn`, una priorità (**must**, **should**, **could**), la fonte nella v10, il comportamento richiesto, i criteri di accettazione e le prove. L'intake di Helix può tenere una feature `NF` per ogni `Q1`.
- **Prodotto unico (R1, R8).** Nessun identificativo, testo o regola specifica di Quokka nel codice. Le funzioni Agenzia sono del prodotto e si abilitano con il livello dell'organizzazione; i dati demo usano un'«Agenzia DEMO».
- **Nessuna chiamata esterna nuova.** Le porte verso terzi (WhatsApp, SMS, provider SDI, videochiamata, servizi degli enti) hanno solo un adattatore finto, etichettato «simulato» nell'interfaccia; ogni flag nuovo è spento per default. Alloggiati Web si sviluppa contro un server SOAP finto costruito dagli esempi del manuale: nessuna chiamata al servizio reale in sviluppo, test o demo.
- **Regime di test del processo Helix.** In locale solo build, format, lint e typecheck; una prova minima scritta per ogni regola di denaro, dati, permessi o privacy (indicate in ogni requisito); le suite le esegue la CI.
- Regole dei repository: migrazioni solo con `dotnet ef`, messaggi IT/EN in `SharedResources`, entità `ITenantOwned`, policy di `CasazenPolicies`, chiavi i18n in `it.json` ed `en.json`, catalogo demo (`DemoShowcaseCatalog`) aggiornato per ogni cosa visibile, runbook aggiornati nella stessa PR del comportamento.
- I simboli della v10 restano: **[NV]** non verificato su fonte ufficiale, **⚖** parere legale o fiscale necessario prima del lancio.

## 1. Perimetro

| Blocco della v10 | In fase 1 | Fuori dalla fase 1 (motivo) |
|---|---|---|
| WP1 Fondamenta | Livello Agenzia, paese, valuta e fuso, proprietari e mandato, accesso del proprietario, staff (Q1-01…Q1-04, Q1-22) | Listino per unità su Stripe Billing (D10 aperto); interfaccia in tedesco (rimandata) |
| WP0 Nucleo comune | Consensi per canale, coda di approvazione, costruttore di condizioni, servizio AI, canali di messaggio (Q1-05…Q1-09) | Invio reale WhatsApp e SMS (Meta, fornitore SMS); provider LLM reale in UE (D8) |
| WP7 Check-in | Documento, selfie e confronto in casa, verifica dell'operatore, QR, codice di accesso (Q1-10…Q1-13) | Videochiamata integrata (Daily); conferma scritta di fal su AuraFace, DPIA e collaudo su dati reali (gate di lancio) |
| WP6a Alloggiati | Invio automatico contro un server finto, ricevute, stati (Q1-14) | Primo invio reale supervisionato (gate di lancio) |
| WP6b Altri adempimenti | File e promemoria per Ticino, OTR e portali comunali; file Ross1000 se il tracciato ufficiale è acquisito (Q1-15) | ISTAT via web service, feratel, pagoPA; file annuali per l'AdE (prima scadenza nel 2027) |
| WP4 Proprietari | Portale, rendiconto mensile, ritenuta del 21% e F24, file di bonifico, fatture elettroniche generate (Q1-16…Q1-20) | Invio SDI reale; migrazione a Stripe Accounts v2; EBICS e aggregatori; CU e 770 (in sospeso, D11) |
| WP5 Fornitori propri e staff | Fornitori propri e attività interne dello staff (Q1-21, Q1-22) | — |
| WP3a Marketing, nucleo | CRM, segmenti, campagne email con approvazione e UTM, visibilità del sito diretto (Q1-23…Q1-25) | Dominio email per agenzia, WhatsApp reale |
| WP8 Routine | Motore QUANDO → SE → ALLORA, libreria, registro, modalità di prova (Q1-26) | Creazione da una frase (rimandata, D15); azioni sui canali OTA |
| WP2 OTA | — | **Tutto**: punto aperto del PO |
| WP3b–WP3e | — | Google Vacation Rentals, Google Ads e Meta, post organici (dipendono da terzi); server MCP e strumento CIN (non sul percorso del pilota) |

Stima indicativa: con i numeri della v10 (§7), le parti incluse valgono circa **7–11 finestre settimanali** sulle ~11,5–18,6 del percorso di Quokka (somma dei pacchetti interi o in parte, ±40%, ipotesi non verificata).

## 2. Decisioni del PO da preservare

| Rif. v10 | Decisione | Effetto sulla fase 1 |
|---|---|---|
| R1, R8 | Agenzia = tipo di host del prodotto unico; portale proprietari, staff e fornitori propri solo B2B; CRM, routine e adempimenti anche B2C con limiti di piano | Abilitazione per livello, nessun codice per un cliente |
| R4 | Nessun flusso manuale, tranne firma dei contratti, approvazione dei bonifici in banca e i casi di R12 | Il file di bonifico si approva in banca; le ricevute degli enti senza API le carica l'agenzia |
| R6/R9, R13, D5, D13 | Verifica volto-documento sviluppata in casa, predefinita nel B2B e facoltativa nel B2C; il consenso biometrico non può essere obbligatorio; modelli open source scelti | Q1-11, con un percorso senza biometria |
| R10, D7 | Fornitori CasaZen di default; fornitori propri registrati in CasaZen, 0% e fattura fuori quando lavorano per la loro agenzia; interruttore «visibile nel marketplace» | Q1-21 |
| R12, D6 | Dove c'è un'API esegue CasaZen; dove non c'è CasaZen prepara file, dati, promemoria e archivio, ed esegue l'agenzia | Q1-14, Q1-15 |
| R14, D11 | Fiscale alla pari dei concorrenti: rendiconto mensile delle ritenute e F24 precompilato; CU e 770 in sospeso; pagamento e invio all'AdE a cura dell'agenzia | Q1-18 |
| R16, D12 | Alloggiati inviato automaticamente da CasaZen con la delega dell'host; «Inviato» solo con la ricevuta | Q1-14 |
| R17, D14 | Consenso al marketing chiesto a tutti gli ospiti nel check-in, facoltativo e per canale | Q1-05 |
| D2, D3 | L'agenzia è l'esercente dei pagamenti diretti; i proprietari si pagano con un file di bonifico approvato in banca | Q1-19; nessun nuovo flusso di denaro attraverso CasaZen |
| D8 | LLM DeepSeek; per i dati degli ospiti ospitato in UE (raccomandazione aperta) | Q1-08: nessun dato personale verso il provider |
| D9 | Nessuna integrazione con le serrature; il codice si invia per messaggio solo dopo la verifica | Q1-13 |
| D15 | Nucleo comune (canali con consenso, coda di approvazione, servizio AI, costruttore di condizioni); rimandate la creazione delle routine da una frase e l'interfaccia in tedesco | Q1-05…Q1-09, Q1-26 |

Dati operativi riferiti dal PO (planning del 20 ottobre e v10): Quokka incassa e riversa le quote ai proprietari; gli annunci sono intestati a Quokka su tutte le OTA; circa 250 proprietà tra Italia e Svizzera; nessuna scadenza; numero di proprietà e territori del pilota da concordare con Quokka. Le informazioni mancanti bloccano solo le attività che ne dipendono (in questa fase l'ordine dei territori di Q1-15).

## 3. Base del codice da riusare (23 ottobre)

### Backend (`9971df9a`)

| Area | Cosa c'è (riusare) | Cosa manca |
|---|---|---|
| Organizzazione e piani | `Org.OrgType` (`Host`, `Supplier`), `Org.PlanTier` (`Starter`, `Pro`, `Scale`), `EntitlementService` (immobili 3, 50, illimitati; posti da `PlanCatalog`: 2, 10, illimitati), accesso aperto da configurazione (`docs/runbooks/open-access.md`) | Livello Agenzia e capacità B2B |
| Ruoli e perimetro | `OrgMember` con i ruoli `Owner`, `Admin`, `PropertyManager`, `Collaborator`, `Accountant` e i contesti `account`, `short-rent`, `long-rent` (`OrgRoleCatalog`, `docs/runbooks/org-team.md`); perimetro per immobile (`HostScope`, `PropertyMemberAccess`, «Solo alcuni» per il collaboratore); permessi fini `servicerequest.write`, `guest.manage`, `alloggiati.submit`; inviti al team (AM-02); `Property.ResponsibleUserId`; registro attività dell'org (AM-02b) | Proprietario esterno all'agenzia. **Attenzione ai nomi:** nel contesto `short-rent` la chiave `property_owner` indica già il **titolare dell'org** e `staff` il **collaboratore**. L'indice `UIX_OrgMembers_UserId` impone **una sola org per utente** |
| Immobili e prenotazioni | `Property.Timezone` (IANA, default `Europe/Rome`), `ComuneIstatCode`, `RegionCode`, `CinCode` (`CinFormat`); `Booking.Source` (`Direct`, `Airbnb`, `BookingCom`, `Expedia`, `Vrbo`, `TripAdvisor`, `Agoda`, `Local`, `Manual`), importi `BasePrice`, `CleaningFee`, `TotalPrice`, `TouristTaxAmount`; soggiorni OTA creati dai blocchi iCal (CO-21) e prenotazione manuale leggera (SR-06a) | Paese e valuta dell'immobile (gli importi sono in euro impliciti); importi delle prenotazioni OTA (commissione, netto incassato) |
| Check-in | Link firmato `/checkin/{token}` (da copiare o per email, job giornaliero, `GuestCheckInSession`), ospiti del soggiorno (`StayGuests`, CO-12) con le regole di Alloggiati, inserimento da parte dell'host, numeri di documento cifrati (CO-14), sessione dell'ospite con codice prenotazione ed email (DB-06a) | Foto del documento, MRZ e OCR, selfie e confronto, QR, codice di accesso. Le scansioni del vecchio portale (`Guest.DocumentScanUrl`) sono nel bucket privato ma non cifrate |
| Alloggiati Web | Stati onesti (`DaInviare`, `DaInviareManualmente`, `InviatoManualmente`, `Inviato` solo con ricevuta per il vincolo `CK_AlloggiatiWebReports_SentRequiresReceipt`, `Errore`, `Rifiutato`), job del giorno d'arrivo, scadenze di 24 e 6 ore, avvisi CO-10, file da 168 caratteri (CO-13), tabelle dei codici, credenziali per immobile cifrate e in sola scrittura (`PropertyQuesturaCredentials`, CO-14); `POST …/send` risponde 422 `alloggiati_transmission_unavailable` | Client del web service, profilo «Gestione Appartamenti», stato «ricevuta in attesa», archivio delle ricevute. Le fonti tecniche sono già raccolte in `.claude/context/regulations/alloggiati.md` (manuale del web service, rev. 01 del 24/01/2022, 12 operazioni) e in `docs/runbooks/alloggiati.md` («Web service: not implemented») |
| Consensi e GDPR | `GuestConsentRecord` in sola aggiunta (finalità `PrivacyNotice`, `Marketing`; versione del testo, origine, IP), consenso al marketing del solo prenotante nel check-in, categorie di conservazione (`DocumentScans` senza periodo deciso, `AlloggiatiData`, `Marketing`, `FiscalData`), cancellazione, anonimizzazione ed esportazione (`docs/runbooks/gdpr.md`) | Consenso per canale e per ogni ospite adulto, email reale con doppia conferma, lista di soppressione, consenso biometrico |
| Messaggi e notifiche | `INotificationChannel` con `NotificationChannelType` `Email`, `Dashboard`, `WhatsApp`, `Sms` (implementati solo `Email` e `Dashboard`); email Resend con coda Hangfire, **sempre in italiano** (gli ospiti non hanno una lingua); push e notifiche in-app (`PushTypes`, conservazione 90 giorni); thread ospite↔host (DB-14) | Lingua dell'ospite, WhatsApp e SMS, distinzione tra messaggi transazionali e di marketing |
| AI | `IAiProvider` (Stub di default, DeepSeek, OpenAI, Anthropic, servizi compatibili), budget di piattaforma, cache, limiti di frequenza, avviso di trasparenza `AiContentNotice` (`docs/runbooks/ai.md`). Regola esistente: **nessun dato personale nei prompt** | Servizio applicativo per i testi dell'agenzia |
| Fiscale e report | `FiscalService` (redditi annuali, ritenute subite sulle OTA, imposta di soggiorno per comune), report dell'host (ricavi per immobile, occupazione, canali), PDF con MigraDoc, `CsvWriter`; `PlatformInvoice` con `ISdiEInvoiceProvider` (solo fatture CasaZen → org, provider non configurato); dati fiscali dell'org (`VatId`, `PartitaIvaNumber`, `FiscalCode`, `BillingSdiRecipientCode`, `BillingPecEmail`, `LegalName`, `RegisteredOffice` su una sola riga) | Rendiconti dei proprietari, ritenuta come sostituto d'imposta, F24, file di bonifico, fatture emesse dall'agenzia, sede strutturata e regime fiscale dell'emittente |
| Fornitori | `ServiceRequest` (fornitore obbligatorio `SupplierOrgId`, stati da `Richiesto` a `Pagato`, `PaymentMode` `Manual` senza commissione o `Online`, `FinalAmountCents`, `ChargeToGuest`), `SupplierProfile.CommissionPercentOverride`, flag `SupplierOnlinePayments` spento; inviti dei fornitori solo dallo staff di piattaforma | Relazione agenzia ↔ fornitore: il brief AM-10 «fornitori di fiducia» (`redesign/docs/wave/analysis/AM/tasks/AM-10.md`, fuori dal repository) è stato escluso il 17 ottobre dal piano ridotto ed è la base di Q1-21; attività assegnate a persone dello staff |
| Job ed eventi | Hangfire (job pianificati e ricorrenti, lock), storage privato `IFileStorage` (S3), colonne cifrate (`EncryptedColumns`), indice cieco per le email dei clienti dei fornitori | **Nessun bus di eventi né outbox**: le routine hanno bisogno di una sorgente di eventi (tema ADR) |
| Sito e SEO | Dati strutturati in `src/site/site-head.ts` (frontend), sitemap pubblici (`PublicOrgSitemapController`, `ComplianceSitemapController`) | Verifica e completamento per Q1-25 |
| Demo e flag | `DemoShowcaseCatalog` (scenari di immobili, prenotazioni e inviti), `FeatureFlags.cs` (otto flag) | Scenari Agenzia |

### Frontend (`3c78af6`) e mobile (`3cf8dd7`)

| Area | Cosa c'è (riusare) | Cosa manca |
|---|---|---|
| Rotte e permessi | Router `src/routes/index.tsx` e manifest `src/config/route-manifest.ts` (aree `short-rent`, `long-rent`, `account`, `admin`, `supplier`, con permessi e flag per rotta, `ContextRouteGuard`); permessi da `GET /api/me/contexts` (`src/contexts/workspace-provider.tsx`, `hasPermission`); team in `/app/account/people` con invito in quattro passi | Aree per il proprietario e per l'agenzia. Anche nel frontend `UserRole` ha già `PropertyOwner` e `Staff` come ruoli di piattaforma: un motivo in più per non riusare quei nomi |
| Piani | `Starter`, `Pro`, `Scale`; entitlement (`maxProperties`, `maxSeats`, `canUseCustomDomain`, `canInviteMember`, `openAccess`) da `/orgs/me/entitlement`; errore `plan_limit_reached`; dialogo admin per cambiare piano | Nessuna mappa di funzioni per piano: le capacità Agenzia vanno aggiunte |
| Check-in | Portale pubblico `/checkin/:token` (`src/features/checkin/`): ospiti del soggiorno, documento solo per l'ospite singolo o il capo, informativa senza casella, casella marketing solo se esiste la versione del testo; API `src/api/checkin.api.ts`; dalla prenotazione `checkin-link-panel.tsx` e `check-in-dialog.tsx`. Il portale non è servito sui domini propri delle org | Fotocamera, foto del documento, consensi per canale e per ospite, QR, codice di accesso (la libreria `qrcode` è già usata nella pagina admin degli inviti) |
| Alloggiati | `src/features/alloggiati/`: copia dei dati, numero del documento in chiaro con registro, file da 168 caratteri, «segna come inviato manualmente», modifica degli ospiti; credenziali della Questura in sola scrittura (`questura-credentials-card.tsx`) | Nuovi stati, errori per riga, ricevute, delega, profilo «Gestione Appartamenti» |
| Ospiti e GDPR | Elenco e dettaglio dell'ospite con scheda Privacy (versione dell'informativa, consenso marketing, revoca con nota, storico, conservazione per categoria, esportazione, anonimizzazione, cancellazione) | Consensi per canale, email di marketing confermata, soppressione |
| Fiscale e report | `src/features/fiscal/` (regime per immobile, procedura guidata, report annuale, ritenute subite, imposta di soggiorno in CSV e PDF), `src/features/reports/` (ricavi, occupazione, canali in CSV) | Rendiconti dei proprietari (la guida del prodotto dice di verificarli a parte), ritenute e F24 dell'agenzia |
| Organizzazione | Ragione sociale e sede su una riga (`PUT /orgs/me/settings`); dati di fatturazione con paese e partita IVA; codice SDI, PEC e codice fiscale in sola lettura | Sede strutturata, regime fiscale dell'emittente, tipo di operatore Agenzia (oggi solo `rentalType`) |
| Messaggi | Thread della prenotazione diretta per host e ospite (`message-thread.tsx`), chiusi 7 giorni dopo il check-out | Nessuna inbox (quella delle OTA è esclusa) |
| Notifiche | — | Nel frontend non esistono la campanella in-app (UI-12b), il push web e le preferenze: gli avvisi della fase 1 passano da email, push mobile e pagine dedicate |
| Fornitori | Marketplace per comune, interventi nella prenotazione (stati, segna pagato), area fornitore; contatore «fornitori di fiducia» fermo a 0 nella Home dell'account | Fornitori propri, attività dello staff, assegnazione a persone |
| Marketing | `/app/admin/marketing` è della piattaforma e in sola lettura | CRM, segmenti, campagne, routine per l'agenzia |
| Flag | `src/config/feature-flags.ts` (cinque flag letti da `GET /api/public/features`, spenti se non caricati) | I flag nuovi vanno esposti anche lì |
| Mobile | App Expo per i soli host (`it.casazen.host`): calendario, immobili, compliance, dettaglio della prenotazione con link di check-in e stato Alloggiati, creazione e pagamento degli interventi; push registrato con `POST /api/devices` e instradato dall'elenco `NOTIFICATION_ROUTES`, da tenere allineato con `PushRoutes.cs` | Nessuna fotocamera, nessun ruolo né contesto (`UserMe` non ha ruolo), nessuna schermata di check-in, Alloggiati o messaggi. Candidati per la fase 1: i nuovi avvisi push verso la prenotazione; le attività dello staff sul mobile richiedono prima i ruoli nell'app |

## 4. Requisiti

### 4.1 Fondamenta Agenzia

#### Q1-01 — Livello Agenzia dell'organizzazione · must

Fonte: v10 §0, R1, R2, R8, WP1; amendment del business plan §5.

1. Un'organizzazione host può essere di **livello Agenzia**. La rappresentazione (nuovo valore di `PlanTier`, attributo dell'org o entrambi) è una decisione ADR; gli enum restano in sola aggiunta.
2. Le capacità **solo B2B** (proprietari e portale, attività interne dello staff, fornitori propri, rendiconti, ritenute, file di bonifico, fatture per conto) rispondono solo alle agenzie; per le altre org le API rispondono come per una funzione non disponibile e il frontend non le mostra. CRM, campagne, routine e adempimenti valgono anche per il B2C con i limiti di piano di D10; fino ad allora nessun limite nuovo.
3. In fase 1 il livello lo assegna l'**admin di piattaforma**, con registro; nessun prezzo per unità né checkout (D10 aperto). Limiti di immobili e posti dell'agenzia: almeno quelli di `Scale`.
4. Togliere il livello non cancella dati: le funzioni B2B passano in sola lettura (default, domanda D-01).

Accettazione: un'org che non è agenzia riceve il rifiuto previsto su ogni API B2B; un'agenzia vede le aree B2B; il cambio di livello è nel registro. Prova: permesso di accesso alle API B2B.

#### Q1-02 — Paese, valuta e fuso degli immobili · must

Fonte: v10 WP1, §3.3 («valuta della proprietà»), §3.5 (Svizzera).

1. Paese dell'immobile (`IT`, `CH`, estendibile), obbligatorio per i nuovi immobili; gli esistenti diventano `IT` con la migrazione.
2. Valuta dell'immobile (`EUR`, `CHF`) derivata dal paese; prenotazioni, spese e rendiconti nella valuta dell'immobile; **nessuna conversione**: i totali multi-valuta restano separati per valuta.
3. Fuso: il campo `Timezone` esiste; default `Europe/Zurich` per `CH`. Ogni «oggi» degli adempimenti si calcola nel fuso dell'immobile.
4. Comuni svizzeri: elenco ufficiale con il cantone (Ufficio federale di statistica), importato come dato di riferimento con data e link della fonte; un comune svizzero non ha codice ISTAT.
5. Adempimenti per paese: per `CH` niente CIN, Alloggiati, imposta di soggiorno italiana e flussi ISTAT, e i controlli di attivazione e compliance (`docs/runbooks/compliance.md`) non bloccano l'immobile per il CIN; per `IT` nulla cambia.
6. Prenotazione diretta per gli immobili in `CHF`: in fase 1 non pubblicata, solo prenotazioni manuali e da iCal (default, domanda D-02).

Accettazione: un immobile svizzero si attiva senza CIN e non mostra la scheda Alloggiati; un rendiconto con immobili italiani e svizzeri mostra due totali. Prova: regole di compliance per paese.

#### Q1-03 — Proprietari degli immobili e mandato · must

Fonte: v10 §3.3, §3.7, §4 (status fiscale raccolto all'onboarding), D3.

1. **Anagrafica del proprietario** nell'agenzia (`ITenantOwned`): persona fisica o giuridica, nome o ragione sociale, codice fiscale e partita IVA, indirizzo strutturato, email, telefono, lingua, IBAN, codice destinatario SDI o PEC, regime fiscale (privato o con partita IVA) e dichiarazione sull'attività d'impresa presunta oltre due appartamenti (L. 199/2025 c. 17, ⚖). IBAN e codice fiscale cifrati.
2. **Titolarità**: proprietario ↔ immobile con quota percentuale (somma 100 per immobile) e decorrenza; più proprietari per immobile; storico conservato.
3. **Mandato** per proprietario e immobile: commissione dell'agenzia (percentuale e base di calcolo), forfait di pulizie e biancheria, chi sostiene le commissioni di pagamento (ribaltate o no), spese rimborsabili, trattamento della commissione ai fini della ritenuta (Q1-18), periodicità mensile. La firma resta fuori da CasaZen (R4): si allega il PDF firmato.
4. Privacy: titolare l'agenzia, CasaZen responsabile; esportazione e cancellazione come per gli ospiti, compatibili con gli obblighi fiscali (categoria `FiscalData`).

Accettazione: quote che non sommano a 100 rifiutate con 422; IBAN mai restituito a chi non ha il permesso. Prove: isolamento tra org, cifratura di IBAN e codice fiscale.

#### Q1-04 — Accesso del proprietario · must

Fonte: v10 R2 («agenzia, CasaZen, ospite, proprietari»), §3.3.

1. Il proprietario accede a un'**area propria in sola lettura** (contenuti in Q1-16), con identità indipendente dall'appartenenza a un'org: può avere immobili gestiti da più agenzie, mentre oggi un utente appartiene a una sola org (`UIX_OrgMembers_UserId`). Il modello di accesso (contesto nuovo con login, oppure accesso con link firmato ed email come la sessione dell'ospite DB-06a) è una decisione ADR.
2. Nomi di ruolo e contesto: **non riusare** `property_owner` (titolare dell'org), `long_term_landlord`, né i ruoli di piattaforma `PropertyOwner` e `Staff` già noti al frontend.
3. Invito dall'agenzia con testo fisso, scadenza, reinvio e revoca, sul modello degli inviti esistenti: risposta uniforme, quota giornaliera.
4. Il proprietario vede solo gli immobili in cui ha una quota, nel periodo di titolarità, e **mai** dati personali degli ospiti (nomi, documenti, contatti).
5. Revoca immediata dell'accesso alla fine del mandato o su richiesta dell'agenzia.

Prove: il proprietario A non legge nulla del proprietario B né di immobili senza quota; nessun dato degli ospiti nelle risposte; accesso revocato dalla richiesta successiva.

### 4.2 Nucleo comune

#### Q1-05 — Consensi per canale, email reale e lista di soppressione · must

Fonte: v10 §3.2 («A chi si può scrivere», «Consenso nel check-in», «Qual è l'email giusta»), R17.

1. Nel check-in, **in una schermata separata dopo i passi obbligatori**, ogni ospite adulto che fornisce il proprio recapito può dare il consenso al marketing **per canale** (email, WhatsApp), con caselle distinte e non preselezionate; il check-in si completa anche senza consenso; i minori sono esclusi.
2. Testo con l'agenzia come titolare, finalità, canali, modalità di revoca e link all'informativa; la versione è registrata. CasaZen è responsabile del trattamento.
3. **Email reale** in un campo dedicato, separata dall'indirizzo della prenotazione (per Booking.com e Airbnb è un alias di inoltro), con **doppia conferma** (link firmato con scadenza); nessun invio di marketing prima della conferma.
4. Prova di ogni consenso: data e ora, versione del testo, canale, prenotazione d'origine, IP. Si estende `GuestConsentRecord` in sola aggiunta, con finalità nuove.
5. Regole d'origine non aggirabili: l'indirizzo della prenotazione Booking.com non si usa mai per il marketing; per Airbnb solo con il consenso separato del check-in; i dati di Alloggiati (`StayGuests`, documenti) non alimentano mai CRM e segmenti.
6. **Lista di soppressione per agenzia** (indirizzi e numeri normalizzati, con indice cieco come `ServiceCustomers.EmailHash`): prevale su qualunque consenso. Revoca con un clic in ogni email (link firmato senza login e intestazioni di disiscrizione one-click); resta la revoca registrata dall'host su richiesta dell'ospite.
7. Esportazione e cancellazione GDPR includono i nuovi consensi; la cancellazione non toglie l'impronta dalla lista di soppressione.
8. Per gli ospiti diretti non si usa il «soft spam» dell'art. 130 c. 4 (default, domanda D-10).

Prove: nessun invio a un indirizzo in soppressione o non confermato; l'alias della prenotazione OTA non è mai un destinatario di marketing; minori esclusi.

#### Q1-06 — Coda di approvazione · must

Fonte: v10 D15, §3.2 («con approvazione dell'agenzia»).

1. Coda per org degli elementi che richiedono una decisione umana prima dell'effetto: bozze di campagna, testi generati dall'AI, messaggi delle routine in prova o con approvazione richiesta.
2. Stati: in attesa, approvato, rifiutato, scaduto; modifica prima dell'approvazione. Chi approva: permesso nuovo (ADR), per default titolare, amministratori e property manager.
3. Registro con chi, quando e testo esatto approvato; un testo cambiato dopo l'approvazione torna in attesa.
4. Notifica in-app alla comparsa di un elemento. Modello di riferimento: il riesame delle pagine SEO (`SeoContentReviewEvent`).

Prova: nulla di ciò che richiede approvazione parte senza approvazione della versione esatta.

#### Q1-07 — Costruttore di condizioni · must

Fonte: v10 D15 (nucleo), §3.2 («Targeting»), §3.6 («Routine», condizioni).

1. Modello unico di condizioni (gruppi E/O, operatori per tipo, valori) su un catalogo di campi tipizzato, versionato e serializzabile, valutato **solo sul server**.
2. Campi per le routine: immobile o tag, canale, durata del soggiorno, numero di ospiti, anticipo della prenotazione, stato del pagamento, stato del check-in e dell'identificazione, lingua dell'ospite, notti libere prima e dopo.
3. Campi per i segmenti: origine, numero di soggiorni, paese, lingua, spesa, periodo, immobile, consenso per canale.
4. Ogni campo rispetta il perimetro di chi usa il costruttore (`HostScope`); nessun campo espone documenti o dati di Alloggiati.
5. Anteprima del numero di elementi corrispondenti per i segmenti.

Prova: una condizione non legge dati fuori dal perimetro dell'utente.

#### Q1-08 — Servizio AI dell'agenzia · should

Fonte: v10 D8, D15, §3.2 («Bozze AI IT/EN/DE/FR con approvazione umana»), §3.1 (vincoli per l'AI).

1. Usi della fase 1: bozze di campagne email (oggetto e testo), bozze dei messaggi delle routine, traduzione IT/EN/DE/FR dei testi dell'agenzia.
2. **Nessun dato personale verso il provider** (regola di `docs/runbooks/ai.md`): i testi usano segnaposto (nome dell'ospite, date) che CasaZen sostituisce localmente all'invio; ammessi i dati pubblici dell'immobile.
3. Ogni testo generato entra nella coda di approvazione (Q1-06), marcato come generato; nessun invio automatico in fase 1.
4. Budget e limiti di frequenza per org, oltre a quelli di piattaforma; con il provider Stub l'interfaccia dice che il testo non è generato (`ProviderConfigured = false`).
5. Trasparenza (AI Act art. 50): avviso nell'interfaccia dell'agenzia; la dicitura verso l'ospite per testi approvati da una persona è una decisione del PO (domanda D-11, ⚖).

Prova: il prompt costruito non contiene dati personali degli ospiti.

#### Q1-09 — Canali di messaggio · must

Fonte: v10 D15 (canali con consenso), §3.2 («Sempre (nucleo)»), §3.6 (codice inviato per messaggio).

1. Separare i messaggi **transazionali** (link di check-in, esito della verifica, codice di accesso, rendiconti) da quelli di **marketing**, che partono solo con consenso confermato e fuori dalla lista di soppressione (Q1-05), verificati al momento dell'invio.
2. Lingua dell'ospite registrata (check-in o prenotazione) e usata per i messaggi; in fase 1 IT ed EN (oggi tutte le email partono in italiano); DE e FR: domanda D-09.
3. Email con il servizio esistente (`IEmailService` e coda Hangfire): mittente CasaZen con il nome dell'agenzia e risposta all'indirizzo dell'agenzia. Il dominio proprio dell'agenzia è rimandato: richiede il provider.
4. WhatsApp e SMS: porta e adattatore finto con registro consultabile («simulato») e flag spento; nessuna integrazione con Meta o con un fornitore SMS.
5. Registro degli invii (canale, stato, errore) senza il testo nei log; **nessun pixel di apertura** (dal 29/10/2026 il Garante richiede il consenso preventivo); clic misurati solo con i parametri UTM sul sito dell'agenzia.

Prove: un messaggio di marketing non parte senza consenso confermato o con l'indirizzo in soppressione; un messaggio transazionale non porta contenuti di marketing.

### 4.3 Check-in, verifica e accessi

#### Q1-10 — Documento d'identità nel check-in · must

Fonte: v10 §3.6 (passo 1; componenti «Lettura del documento» e «Documenti»), §3.5 (Ticino).

1. Nel portale di check-in l'ospite singolo o il capo famiglia o gruppo fotografa o carica il documento (fronte e retro).
2. Lettura della **MRZ** (ICAO 9303: carte d'identità elettroniche, passaporti, permessi di soggiorno) con le cifre di controllo; OCR dove la MRZ manca (per esempio la patente italiana). I dati letti **precompilano** i campi esistenti e l'ospite li conferma o li corregge; l'inserimento manuale resta sempre possibile.
3. Solo controlli formali: cifre di controllo, scadenza, coerenza della data di nascita; nessuna verifica di autenticità del documento.
4. Elaborazione dentro l'infrastruttura di CasaZen con componenti open source (riferimento della v10: PassportEye con Tesseract o equivalenti); dove gira (server, servizio separato o browser) è una decisione ADR.
5. Conservazione: immagini nel bucket privato, cifrate, mai nei log; cancellate alla ricevuta di Alloggiati o alla scadenza decisa (domanda D-07). Per gli immobili del **Ticino nessuna copia conservata** (RL 942.120 art. 6): elaborazione in memoria e scarto immediato. Le scansioni del vecchio portale seguono la loro regola.

Prove: nessuna immagine conservata per un immobile del Ticino; immagini cancellate alla ricevuta.

#### Q1-11 — Selfie dal vivo e confronto con il documento · must

Fonte: v10 R6/R9, R13, D5, D13, §3.6 («Componenti sviluppati in casa», «Soglia e collaudo»).

1. **Consenso biometrico** esplicito, separato dall'informativa e dal marketing, registrato con la versione e **rifiutabile**: chi rifiuta passa alla verifica dell'operatore (Q1-12) senza biometria, perché il consenso non può essere obbligatorio (R6/R9; default, domanda D-06). Predefinito per le agenzie, facoltativo per il B2C (impostazione dell'org).
2. Selfie **solo dalla fotocamera** nella pagina di check-in, nessun file dalla galleria; controllo anti foto-di-foto obbligatorio (MiniFASNet, Apache-2.0, anche i pesi).
3. Confronto con il volto del documento: **AuraFace-v1, solo `glintr100.onnx`** (Apache-2.0); ripiego configurabile SFace (OpenCV Zoo, Apache-2.0); rilevatore e allineatore con licenza verificata (candidato YuNet, MIT, dati di addestramento [NV]). Vietati il rilevatore e l'allineatore di InsightFace, EdgeFace, AdaFace e i modelli addestrati su MS-Celeb-1M o VGGFace2. Provenienza, licenza e hash dei pesi registrati; pesi fuori dal repository git se troppo grandi (ADR).
4. Esito a tre vie con **due soglie configurabili**: accetta, passa all'operatore, rifiuta (nuovo tentativo, poi operatore); numero massimo di tentativi. Le soglie iniziali sono provvisorie e dichiarate tali. La calibrazione (falsi accoppiamenti 1 su 10.000, falsi rifiuti 1 su 100, NIST SP 800-63A) su coppie reali con consenso e DPIA è un collaudo prima del lancio, fuori da questa fase; in fase 1 serve lo strumento che, da un insieme etichettato, calcola soglie e tassi d'errore.
5. **Cancellazione immediata** del selfie, del ritaglio del volto e di eventuali vettori subito dopo il confronto, anche in caso d'errore; mai in log, storage o code dei job.
6. **Registro di verifica** per prenotazione, senza immagini: istante, esito, tentativi, modello e versione, soglie in uso, decisione dell'operatore.
7. Minori senza biometria.
8. Prestazioni: la v10 indica circa 74 ms su CPU per confronto; carico sull'API e hosting del modello sono un tema ADR.

Prove: cancellazione dopo il confronto, anche dopo un errore; nessuna elaborazione biometrica senza consenso o per un minore.

#### Q1-12 — Verifica dell'operatore · must

Fonte: v10 §3.6 (passo 3), R13.

1. Alla **prima foto non verificata**, al rifiuto del consenso biometrico o a un errore del modello l'operatore è **avvisato subito** per email e push mobile (nuovi tipi in `PushTypes`, rotta verso la prenotazione in `NOTIFICATION_ROUTES` dell'app), oltre alla notifica in-app del backend. Destinatari: il responsabile dell'immobile e gli amministratori, come gli avvisi esistenti.
2. Coda delle verifiche in una pagina dedicata della console web (la campanella in-app del frontend non esiste ancora), nel perimetro dell'operatore, con motivo e scadenza (l'arrivo).
3. Azioni: chiedere una nuova foto (l'ospite riceve il link), registrare la verifica fatta in videochiamata o di persona (esito, nota, chi, quando), respingere. In fase 1 la chiamata avviene fuori da CasaZen.
4. Finché la verifica non è superata l'ospite non riceve il codice di accesso.
5. Permesso dedicato (ADR); per default titolare, amministratori e property manager. Il collaboratore solo se il PO lo decide (domanda D-08, analoga ad AM-D8 per `alloggiati.submit`).

Prove: solo chi ha il permesso decide una verifica; nessun codice senza verifica superata.

#### Q1-13 — QR all'ingresso e codice di accesso · must

Fonte: v10 §3.6 (passi 4 e 5), «Accesso all'immobile», D9.

1. **Codice di accesso** per immobile impostato dall'agenzia, cifrato, mai nei log; lettura riservata a chi ha il permesso, con registro. CasaZen non lo crea e non lo revoca e lo dice all'agenzia: non è unico per soggiorno.
2. Il codice si rilascia **solo dopo la verifica superata** (biometrica o dell'operatore), nella schermata finale del check-in e per email transazionale; nessuna routine né azione manuale lo aggira.
3. **QR stampabile per immobile**: all'ingresso porta l'ospite alla verifica del suo soggiorno (riconoscimento con codice prenotazione ed email, come la sessione DB-06a); è sempre l'ultima verifica possibile se il selfie non è stato inviato.
4. Registro di ogni rilascio (soggiorno, canale, istante), senza il codice.

Prove: rilascio impossibile senza verifica superata; codice cifrato e assente da log e registro.

### 4.4 Adempimenti

#### Q1-14 — Alloggiati Web: invio automatico · must

Fonte: v10 R16, D12, §3.6 («Invio ad Alloggiati»); `docs/runbooks/alloggiati.md`; `.claude/context/regulations/alloggiati.md`.

1. Client SOAP 1.1 (`HttpClient`, `XDocument`) sulle operazioni del manuale: `GenerateToken`, `Authentication_Test`, `Test`, `Send`, `Ricevuta`, `Tabella`, `GestioneAppartamenti_Test` e `_Send`, `GestioneAppartamenti_FileUnico_Test` e `_Send`. L'anagrafica degli appartamenti (`_AggiungiAppartamento`, `_DisabilitaAppartamento`) resta sul portale in fase 1.
2. **Due profili**: struttura singola (credenziali per immobile, già presenti) e **«Gestione Appartamenti»** (un'utenza per più appartamenti: credenziali a livello di org e `IdAppartamento` per immobile, letto con `Tabella(ListaAppartamenti)`; righe da 174 caratteri con l'identificativo nelle posizioni 168–173, allineamento da verificare con `Test`). Un'agenzia con molti immobili usa probabilmente il secondo (domanda D-05).
3. **Delega**: l'agenzia o l'host autorizza CasaZen come incaricato della trasmissione (D.M. 7 gennaio 2013 come modificato nel 2021, art. 2) con un consenso in configurazione, revocabile; senza delega resta il flusso manuale di oggi.
4. Al giorno d'arrivo (job esistente): `Test` di tutte le righe del soggiorno, poi `Send` **solo se ogni riga è valida**; errori per riga mostrati all'host con il codice di `TipoErrore`; nessun invio parziale volontario.
5. Nuovo stato **«Inviato, ricevuta in attesa»** (valore nuovo in sola aggiunta) tra `Send` riuscito e ricevuta; `Inviato` solo con la ricevuta (vincolo esistente); `Errore` e `Rifiutato` usati dal client.
6. **Ricevute**: job giornaliero `Ricevuta(data)` per ogni utenza (disponibile dal giorno dopo, ultimi 30 giorni, un PDF per utenza e giorno) con archivio privato per **5 anni**; le comunicazioni del giorno passano a `Inviato` con il riferimento (contenuto di `ConfirmationNumber`: ADR). Una ricevuta mancante oltre il giorno successivo genera un avviso.
7. **Mai un secondo `Send` automatico** dopo un esito ambiguo (timeout, errore di rete): la comunicazione resta da verificare e decide l'operatore. Il comportamento di invii ripetuti non è documentato [NV].
8. Token per utenza con scadenza (nell'esempio del manuale un'ora); WSKEY aggiornata dall'host al cambio della password con il modulo in sola scrittura esistente.
9. Scadenze e avvisi: riuso degli stadi CO-10, più gli avvisi per invio fallito e ricevuta mancante.
10. Flag nuovo spento e abilitazione per org. **In fase 1 nessuna chiamata al servizio reale**: server SOAP finto con le richieste e risposte d'esempio del manuale (pp. 7–18).
11. Dopo la ricevuta si applica la conservazione decisa per documenti e dati (Q1-10, categoria `AlloggiatiData`).
12. Gate di lancio, fuori dalla fase 1: primo invio supervisionato su una struttura reale.

Prove: mai `Inviato` senza ricevuta; `Send` solo se tutte le righe hanno superato `Test`; nessun reinvio automatico dopo un esito ambiguo; credenziali e ricevute isolate per org; nessun dato personale nei log.

#### Q1-15 — Adempimenti senza API: file, dati e promemoria · should

Fonte: v10 R12, D6, §3.5 (tabella «Senza API»), WP6b.

1. **Ticino**: per gli immobili del cantone, file XML giornaliero della notifica degli ospiti alla polizia nel formato ufficiale del portale notifiche.ti.ch, promemoria entro 24 ore, caricamento a cura dell'agenzia (con la 2FA del portale) e archivio della ricevuta caricata. Precondizione: salvare nel repository il manuale ufficiale con data e link; senza tracciato verificato non si scrive codice (stessa regola di `docs/runbooks/ross1000.md`).
2. **Tassa OTR (Ticino)**: CSV dei pernottamenti per periodo.
3. **Portali comunali italiani senza API** (StayTour, TouristTax, SoggiorniAmo, Pegaso, Cinque Terre): per comune, dati pronti dal report dell'imposta di soggiorno, link diretto, promemoria con scadenza, archivio della ricevuta; file solo dove il formato ufficiale è verificato.
4. **Lombardia, Ross1000**: file per «Importa file da gestionale» solo dopo aver acquisito il tracciato ufficiale (passo 1 di `ross1000.md`); il web service resta fuori.
5. Ordine di sviluppo secondo i territori del pilota (dato da Quokka): nessuna ipotesi; i territori non ancora noti restano da fare.

Prove: i file contengono solo i dati richiesti dal formato; isolamento per org.

### 4.5 Proprietari e fiscale

#### Q1-16 — Portale proprietari · must

Fonte: v10 §3.3, R8.

1. Per il proprietario (Q1-04): i suoi immobili, occupazione e ricavi per mese, i **rendiconti pubblicati** in PDF e CSV, le fatture ricevute dall'agenzia, il riepilogo delle ritenute, i documenti condivisi dall'agenzia.
2. Nessun dato personale degli ospiti: solo date, canale e importi.
3. Notifica email nella lingua del proprietario alla pubblicazione di un rendiconto o di un documento.
4. Sola lettura: le modifiche ai dati anagrafici e bancari si chiedono all'agenzia (default).
5. Solo web in fase 1 (default, domanda D-14).

Prove: isolamento tra proprietari e tra agenzie; nessun dato degli ospiti.

#### Q1-17 — Rendiconto mensile per proprietario · must

Fonte: v10 §3.3 (punto 2), D3.

1. Per mese, proprietario, immobile e prenotazione: ricavi per canale (diretti, OTA, manuali), commissione dell'agenzia secondo il mandato, forfait, spese con allegati (interventi dei fornitori del marketplace, dei fornitori propri all'importo approvato, dello staff alla tariffa interna se riaddebitata, spese inserite a mano), commissioni di pagamento se ribaltate, ritenuta del 21% se dovuta (Q1-18), netto da pagare; ripartizione tra comproprietari.
2. **Importi delle prenotazioni OTA** senza channel manager: oggi il soggiorno OTA da iCal o manuale ha solo il totale. Servono lordo, commissione OTA e netto incassato. Default di fase 1: inserimento manuale sul soggiorno e importazione CSV dei report delle OTA (domanda bloccante D-03).
3. Commissioni Stripe delle prenotazioni dirette: lette dall'integrazione Stripe esistente sul conto dell'agenzia se disponibili, altrimenti inserite a mano; nessuna nuova integrazione.
4. Competenza: criterio di attribuzione al mese (default la data d'incasso dell'agenzia, domanda D-04); anno di cassa e anno di soggiorno conservati per ogni importo (Q1-18).
5. Ciclo: bozza → approvato dall'agenzia → pubblicato; dopo la pubblicazione nessuna modifica, le correzioni sono righe di rettifica del mese successivo.
6. Valuta dell'immobile, arrotondamento al centesimo con una regola unica (ADR), PDF con il renderer esistente e CSV/Excel.

Prove: netto = ricavi − commissione − spese − ritenuta ± rettifiche, al centesimo; le quote dei comproprietari sommano al totale; perimetro dell'agenzia.

#### Q1-18 — Ritenuta del 21% e F24 precompilato · must

Fonte: v10 R14, D11, §3.7.

1. Flag di **sostituto d'imposta** per l'agenzia e regime per proprietario: con partita IVA nessuna ritenuta.
2. Base di calcolo secondo §3.7: lordo con pulizie e biancheria a forfait; trattamento della provvigione trattenuta (deducibile o no) secondo il mandato; esclusi cauzioni, caparre e penali (⚖).
3. **Rendiconto mensile delle ritenute** per proprietario, appartamento e prenotazione, con il totale da versare, in PDF e CSV/Excel.
4. **F24 precompilato** in PDF: codice tributo 1919, mese e anno di riferimento, importo; un versamento cumulativo per tutti i proprietari entro il 16 del mese successivo, con promemoria.
5. Riconciliazione trattenuto/versato con la data di pagamento registrata dall'agenzia; anno di cassa e di soggiorno per ogni importo (le prenotazioni a cavallo d'anno restano divise).
6. Prima di scrivere testi e riferimenti normativi: riverificare la rinumerazione dal 1/1/2027 (art. 4 D.L. 50/2017 verso il D.Lgs. 117/2026, artt. 207–208 [NV]) e farla validare (⚖). Pagamento e invio restano all'agenzia; CU e 770 non si sviluppano.
7. Non confondere con le ritenute **subite** dall'host sulle OTA (`Payment.OtaWithholdingTax` e report esistente).

Prove: calcolo su casi d'esempio (privato, partita IVA, cauzione esclusa, prenotazione a cavallo d'anno); totale dell'F24 uguale alla somma del registro del mese.

#### Q1-19 — File di bonifico ai proprietari · must

Fonte: v10 §3.3 (punto 3), D3, R4.

1. Lotto di pagamento dai rendiconti approvati: un bonifico per proprietario (IBAN validato con le cifre di controllo), importo uguale al netto del rendiconto, causale con il riferimento.
2. Formati ISO 20022 **pain.001**: SEPA secondo le specifiche EPC per l'Italia e Swiss Payment Standards per la Svizzera; la variante CBI richiesta da alcune banche italiane solo se le specifiche sono pubbliche (domanda D-13 sulla banca di Quokka). Un file per valuta e conto di addebito.
3. Validazione con gli XSD ufficiali prima del download.
4. Un rendiconto entra in un solo lotto; stati del lotto: generato, scaricato, pagato su conferma dell'agenzia. CasaZen non esegue pagamenti: l'approvazione avviene in banca (R4).

Prove: la somma del file è uguale alla somma dei netti; nessun doppio pagamento dello stesso rendiconto; IBAN mai nei log.

#### Q1-20 — Fatture elettroniche per conto dell'agenzia · should

Fonte: v10 §3.3 (punto 5).

1. Fattura mensile differita dell'agenzia al proprietario per le commissioni, un solo fornitore per fattura (art. 21 DPR 633/72), con numerazione per agenzia e sezionale, aliquote e nature IVA, bollo se dovuto.
2. XML FatturaPA validato con lo schema ufficiale dell'AdE; copia di cortesia in PDF nel portale del proprietario.
3. Dati dell'emittente: dati fiscali dell'org esistenti più quelli mancanti (sede strutturata, oggi `RegisteredOffice` è una riga; regime fiscale).
4. Invio: la porta `ISdiEInvoiceProvider` va generalizzata dall'emittente CasaZen all'emittente agenzia (ADR); in fase 1 nessun provider configurato e stato «da emettere manualmente».
5. Solo per emittenti italiani: un'agenzia svizzera non usa lo SDI (domanda D-13 su Quokka).

Prove: imponibile, IVA e totale corretti al centesimo; numerazione senza buchi né doppioni per emittente.

### 4.6 Fornitori e staff

#### Q1-21 — Fornitori propri dell'agenzia · must

Fonte: v10 R10, D7, §3.4.

1. Relazione agenzia ↔ fornitore come nel brief AM-10 (fornitori di fiducia: elenco per org, immobili assegnati, priorità nelle proposte senza esclusione degli altri, invito dall'host con testo fisso, quota giornaliera e risposta uniforme); il fornitore invitato dall'agenzia è suo in automatico.
2. Fornitore **proprio**: organizzazione `Supplier` invitata e legata all'agenzia, **senza vincolo di comune** (anche in Svizzera).
3. Lavori per la propria agenzia: pagamento fuori da CasaZen (`PaymentMode.Manual`), **0% di commissione**, fattura fuori da CasaZen; l'importo approvato entra nel rendiconto del proprietario.
4. Interruttore **«visibile nel marketplace»** del fornitore: acceso, può lavorare per altri con le regole del marketplace. I fornitori CasaZen restano il default (R10).
5. Registro attività con gli eventi riservati `TrustedSupplierAdded` e `TrustedSupplierRemoved`.

Prove: nessuna commissione sui lavori del fornitore proprio per la sua agenzia; un'agenzia non vede i fornitori propri di un'altra.

#### Q1-22 — Staff e attività interne · must

Fonte: v10 §3.4 (colonna «Staff»).

1. Lo staff dell'agenzia usa il ruolo **Collaboratore** (`short-rent/staff`) e il perimetro «Solo alcuni»; nessun ruolo nuovo salvo decisione ADR.
2. Attività interne (pulizia, manutenzione, check-in) assegnate a una persona dello staff invece che a un fornitore. Oggi `ServiceRequest.SupplierOrgId` è obbligatorio: modello nuovo o estensione (ADR).
3. Tariffa interna per attività o per persona; se riaddebitata al proprietario entra nel rendiconto (Q1-17); il compenso dello staff resta fuori da CasaZen.
4. Lo staff vede e aggiorna le proprie attività sul web. Nell'app mobile servirebbero prima ruoli e contesti, che oggi l'app non conosce: fuori dalla fase 1 salvo decisione ADR.
5. Le attività si creano a mano o dalle routine (Q1-26).

Prova: lo staff vede solo le attività e gli immobili del proprio perimetro.

### 4.7 CRM e campagne

#### Q1-23 — CRM degli ospiti · should

Fonte: v10 §3.2 («Il CRM di CasaZen, con i consensi, è il vantaggio»).

1. Profilo dell'ospite per agenzia, aggregato sui soggiorni (diretti, manuali, OTA da iCal): contatti di marketing confermati, consensi per canale, lingua, paese, soggiorni, spesa, immobili, tag.
2. Ricerca e filtri nel perimetro dell'utente; esportazione e cancellazione GDPR estese ai nuovi dati.
3. Mai dati di Alloggiati né documenti nel profilo.

Prova: nessun dato di Alloggiati o documento nel profilo e nei segmenti.

#### Q1-24 — Segmenti e campagne email · should

Fonte: v10 §3.2 («Sempre (nucleo)»), WP3a.

1. Segmenti con il costruttore di condizioni (Q1-07), limitati a chi ha un consenso confermato per il canale e non è in soppressione; conteggio prima dell'invio.
2. Campagna: bozza (anche con l'AI, Q1-08), approvazione (Q1-06), programmazione, invio email con disiscrizione a un clic e senza pixel; link con UTM verso il sito di prenotazione diretta dell'agenzia.
3. Rendiconto: inviati, errori, disiscrizioni e prenotazioni dirette attribuite alla campagna tramite UTM.
4. WhatsApp: solo il modello di template (variabili e categoria del consenso), nessun invio reale.

Prova: la lista dei destinatari esclude i non consenzienti, i non confermati e i soppressi al momento dell'invio.

#### Q1-25 — Visibilità del sito di prenotazione diretta · could

Fonte: v10 §3.2 (priorità 1, «Sul sito di prenotazione diretta»; crescita di CasaZen, «Fondamenta gratuite»).

1. Verificare ciò che emettono già `src/site/site-head.ts` e i sitemap del backend e completare solo i vuoti: dati strutturati degli alloggi, crawler di ricerca di OpenAI (OAI-SearchBot) consentito, IndexNow.
2. Nessuna iscrizione a servizi esterni: Search Console e Bing Webmaster Tools sono attività del PO.

### 4.8 Routine

#### Q1-26 — Routine QUANDO → SE → ALLORA · must

Fonte: v10 §3.6 («Routine»), D15, WP8.

1. L'agenzia, e l'host B2C entro i limiti del piano, crea routine: un evento, condizioni (Q1-07), una o più azioni anche con attese e passaggi successivi.
2. **Eventi** di fase 1: prenotazione nuova, confermata, modificata, cancellata (diretta, manuale, soggiorno OTA da iCal); X giorni o ore prima o dopo check-in e check-out; check-in o check-out avvenuto; check-in online completato; identificazione completata; ricevuta Alloggiati arrivata; messaggio dell'ospite ricevuto (thread esistenti); recensione ricevuta (recensioni del sito); pagamento riuscito o fallito; attività completata; ricorrenza settimanale. Gli eventi delle OTA via API sono esclusi.
3. **Azioni**: messaggio all'ospite (email; WhatsApp e SMS sulla porta finta); notifica al proprietario, allo staff o al team; creazione di un'attività assegnata allo staff, a un fornitore proprio o al marketplace; **invio del codice di accesso solo a identificazione completata**; richiesta di recensione (invito del sito esistente); tag; attesa. Il webhook verso sistemi esterni è fuori dalla fase 1 (default, domanda D-12).
4. **Guardrail** non aggirabili: codice solo dopo l'identificazione; nessun contatto né link prima della prenotazione su Airbnb; campagne solo con consenso.
5. **Modalità di prova** prima dell'attivazione (simulazione sugli eventi recenti, senza effetti) e **registro** di ogni esecuzione (evento, condizioni valutate, azioni, esito).
6. **Libreria** di routine pronte attivabili con un clic: pulizia al check-out, istruzioni due giorni prima, richiesta di recensione dopo la partenza, promemoria per i documenti mancanti.
7. Ogni evento produce **al massimo un'esecuzione** per routine (idempotenza); attese con Hangfire; ripresa dopo un riavvio. Oggi **non esiste un bus di eventi**: la sorgente degli eventi (per esempio una tabella scritta nella stessa transazione dei servizi e un dispatcher) è una decisione ADR.

Prove: un evento produce una sola esecuzione per routine; il codice di accesso non parte senza identificazione; un'azione fuori dal perimetro dell'autore è rifiutata.

### 4.9 Trasversali

#### Q1-27 — Demo «Agenzia DEMO» · must

Fonte: regola `.claude/rules/demo-seed.md`; v10 §7 («ogni ondata chiude con un percorso completo»).

1. Scenari nuovi in `DemoShowcaseCatalog`: agenzia con circa 10 immobili tra Italia e Svizzera (anche in Ticino), proprietari con quote e mandati, staff, un fornitore proprio, prenotazioni dirette, manuali e da iCal, check-in in tutti gli stati della verifica, consensi, comunicazioni Alloggiati nei nuovi stati con ricevuta finta, rendiconti, ritenute, F24, un lotto di bonifico, fatture generate, campagne in bozza, routine attive e in prova.
2. Dati inventati e riconoscibili secondo `demo-seed.md`; **nessun volto reale**: per la biometria solo esiti registrati, nessuna immagine.
3. Ogni adattatore finto è etichettato «simulato».

#### Q1-28 — Lingue · must

Fonte: v10 §3.2 (bozze IT/EN/DE/FR), D15 (tedesco rimandato).

1. Interfaccia dell'agenzia e del proprietario in IT ed EN; tedesco rimandato.
2. Pagine e messaggi per l'ospite in IT ed EN; DE e FR secondo la domanda D-09.
3. Testi nuovi in `it.json` ed `en.json`, `SharedResources(.en).resx`, `EmailTexts(.en).resx`.

#### Q1-29 — Flag, registro e sicurezza · must

1. Flag nuovi spenti per default (almeno livello Agenzia, verifica del volto, Alloggiati automatico, canali WhatsApp e SMS finti, routine, campagne), documentati in `docs/runbooks/feature-flags.md` ed esposti al frontend con `GET /api/public/features` (`src/config/feature-flags.ts`).
2. Eventi nuovi nel registro attività dell'org: pubblicazione del rendiconto, rilascio del codice, decisione di verifica, invio Alloggiati, lotto di bonifico.
3. Ogni endpoint nuovo con policy, perimetro `HostScope`, isolamento per `OrgId` e limiti di frequenza sulle pagine pubbliche (check-in, conferma dell'email, disiscrizione, QR).
4. Documentazione nella stessa PR del comportamento: runbook nuovi o aggiornati e rimando in `docs/runbooks/index.md`. La guida del prodotto nel frontend descrive una funzione solo quando è consegnata (regola di `docs/product/README.md` del frontend: gli obiettivi del pilota non sono funzioni disponibili).

## 5. Temi per la fase ADR

1. Rappresentazione del livello Agenzia e delle capacità per piano (Q1-01).
2. Proprietario: identità indipendente dall'org, nomi di contesto e ruolo, modello di accesso (Q1-03, Q1-04).
3. Paese e valuta dell'immobile e regole di compliance per paese (Q1-02).
4. Pipeline del documento e della biometria: dove gira (API .NET con ONNX Runtime, servizio separato, browser), carico della CPU, pesi e licenze, cancellazione garantita, caso Ticino (Q1-10, Q1-11).
5. Client Alloggiati: profili, stati, ricevute, contenuto di `ConfirmationNumber`, idempotenza (Q1-14).
6. Sorgente degli eventi per le routine (Q1-26).
7. Libro dei rendiconti: immutabilità, rettifiche, arrotondamenti, più valute, fonte degli importi OTA (Q1-17, Q1-18).
8. Emittente generalizzato delle fatture elettroniche e numerazione (Q1-20).
9. Canali e consensi: transazionale e marketing, soppressione, registro dei canali finti (Q1-05, Q1-09).
10. Attività dello staff rispetto a `ServiceRequest` (Q1-22).
11. Politica dei dati verso l'AI e coda di approvazione (Q1-06, Q1-08).

## 6. Domande aperte per il PO, con default

| ID | Domanda | Default proposto | Blocca |
|---|---|---|---|
| D-01 | Come si diventa Agenzia in fase 1 e cosa succede togliendo il livello? | Lo assegna l'admin di piattaforma; i dati restano in sola lettura | No |
| D-02 | Prenotazione diretta per gli immobili svizzeri (CHF)? | Non pubblicata in fase 1 | No |
| D-03 | Da dove arrivano gli importi delle prenotazioni OTA per i rendiconti? | Inserimento manuale (lordo, commissione OTA, netto) e import CSV dei report delle OTA | **Sì**, per Q1-17 |
| D-04 | Competenza del rendiconto mensile | Data d'incasso dell'agenzia | No |
| D-05 | Profilo Alloggiati dell'agenzia: struttura singola o «Gestione Appartamenti»? | Supportare entrambi | No |
| D-06 | Se l'ospite rifiuta la biometria? | Verifica dell'operatore, nessuna biometria | No |
| D-07 | Conservazione delle immagini del documento | Fino alla ricevuta Alloggiati, poi cancellate; in Ticino mai conservate | No |
| D-08 | Il collaboratore (staff) può decidere le verifiche e trasmettere ad Alloggiati? | No, come oggi per `alloggiati.submit` (AM-D8) | No |
| D-09 | Lingue per gli ospiti oltre IT ed EN | Solo IT ed EN in fase 1; DE e FR solo per le bozze AI | No |
| D-10 | «Soft spam» (art. 130 c. 4) per gli ospiti diretti? | No: solo consenso esplicito | No |
| D-11 | Dicitura AI verso l'ospite per testi approvati da una persona | Solo l'avviso nell'interfaccia dell'agenzia, finché il legale non decide (⚖) | No |
| D-12 | Azione webhook nelle routine | Fuori dalla fase 1 | No |
| D-13 | Quokka è un soggetto italiano? Quale banca e formato del file di bonifico? | Agenzia italiana; SEPA pain.001 | No |
| D-14 | Il proprietario usa anche l'app mobile? | Solo web | No |

## 7. Ordine suggerito (indicativo per la scomposizione)

1. Fondamenta: Q1-01, Q1-02, Q1-03, Q1-04 e il nucleo Q1-05…Q1-09.
2. In parallelo, senza dipendenze dal punto 1: client Alloggiati con il server finto (Q1-14), lettura del documento e confronto del volto come componenti (Q1-10, Q1-11), generatori di F24, pain.001 e FatturaPA (parti di Q1-18…Q1-20), file per Ticino e OTR (Q1-15).
3. Flussi: verifica dell'operatore, QR e codice (Q1-12, Q1-13); portale e rendiconti (Q1-16, Q1-17); fornitori propri e staff (Q1-21, Q1-22).
4. CRM e campagne (Q1-23…Q1-25); routine (Q1-26).

Q1-27…Q1-29 accompagnano ogni pacchetto. Ogni ondata chiude un percorso completo, dal dato alla schermata.

## 8. Accettazione e consegna

| Prova | Risultato atteso |
|---|---|
| Demo dell'agenzia | Percorso completo con l'Agenzia DEMO: dal proprietario e dal mandato al check-in verificato, al codice, ad Alloggiati con ricevuta finta, al rendiconto pubblicato, alla ritenuta con F24, al lotto di bonifico, alla campagna approvata e alle routine; nessun dato reale; ogni simulazione dichiarata |
| Separazione dei dati | Agenzie, proprietari, staff e ospiti vedono solo ciò che il loro ruolo e il loro perimetro consentono |
| Biometria | Consenso separato e rifiutabile; immagini e vettori cancellati subito; registro senza immagini; minori esclusi |
| Codice di accesso | Mai rilasciato senza verifica superata, da nessun percorso (check-in, QR, routine, azione manuale) |
| Alloggiati | `Test` prima di `Send`; mai `Inviato` senza ricevuta; nessun reinvio automatico dopo un esito ambiguo; entrambi i profili |
| Denaro | Rendiconto, ritenute, F24 e file di bonifico coerenti al centesimo; nessun doppio pagamento; nessun flusso di denaro attraverso CasaZen |
| Marketing | Nessun invio senza consenso confermato o con indirizzo in soppressione; disiscrizione con un clic; nessun pixel |

Verifiche secondo il regime del processo Helix: in locale solo build, format, lint e typecheck; per ogni regola di denaro, dati, permessi o privacy una prova minima scritta, che gira in CI con le suite dei repository. Nessuna chiamata a servizi reali come prova.

Consegna: PR verso `develop` con i riferimenti `Q1-nn`, il delta effettivo e le configurazioni ancora necessarie. Non dichiarare attivo in produzione ciò che è soltanto integrato. Prima del lancio, fuori da questa fase: primo invio Alloggiati supervisionato su una struttura reale; conferma scritta di fal sui dati di addestramento di AuraFace, DPIA e calibrazione delle soglie su dati reali; testi legali (consensi, mandato, informative) ⚖; provider SDI; decisione D8 sull'LLM.

## 9. Fuori perimetro

- **Channel management** (WP2) e tutto ciò che presuppone dati delle OTA via API: contenuti, disponibilità e tariffe, prenotazioni, inbox e risposte AI, recensioni delle OTA.
- Google Vacation Rentals, Google Ads, Meta, post organici; server MCP e strumento diagnostico CIN.
- Invii reali: WhatsApp, SMS, SDI, Alloggiati in produzione, ISTAT via web service, feratel, pagoPA.
- Migrazione a Stripe Accounts v2, EBICS, aggregatori, Stripe Treasury; listino per unità su Stripe Billing.
- CU e 770; file annuali per l'AdE (comunicazione dei dati delle locazioni brevi, DAC7, dichiarazione dell'imposta di soggiorno), con prima scadenza nel 2027.
- Interfaccia in tedesco, creazione delle routine da una frase, ChatGPT Ads, SEO automatico, sincronizzazione con HubSpot.
- Integrazione con le serrature (D9).

## 10. Tracciabilità v10 → requisiti

| v10 | Requisiti |
|---|---|
| §0, R1, R2, R8, WP1 | Q1-01, Q1-02, Q1-03, Q1-04 |
| D15, WP0 | Q1-05, Q1-06, Q1-07, Q1-08, Q1-09 |
| §3.2, R17, D14, WP3a | Q1-05, Q1-23, Q1-24, Q1-25 |
| §3.3, D2, D3, WP4 | Q1-16, Q1-17, Q1-19, Q1-20 |
| §3.4, R10, D7, WP5 | Q1-21, Q1-22 |
| §3.5, R12, D6, WP6b | Q1-15 |
| §3.6 verifica e accesso, R6/R9, R13, D5, D9, D13, WP7 | Q1-10, Q1-11, Q1-12, Q1-13 |
| §3.6 Alloggiati, R16, D12, WP6a | Q1-14 |
| §3.6 routine, WP8 | Q1-26 |
| §3.7, R14, D11 | Q1-18 |
| §3.1, R5, D4, WP2 | **escluso** (punto aperto del PO) |
| WP3b, WP3c, WP3d, WP3e | **esclusi** |
