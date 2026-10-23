# Affitti lunghi: elenco contratti, registro canoni, scadenze e panoramica (LR-01)

Task LR-01 della wave di redesign (gap report `redesign/docs/gap/04-long-rent.md`, B1-B3). Le schermate nuove dell'area leggono dati
che esistevano solo dentro il singolo contratto: questo task li aggrega. È **solo backend, additivo** (nessun endpoint esistente
cambia forma o comportamento), **senza flag** (sono letture; l'unico effetto è l'email di sollecito, come già avviene per il link di
pagamento). Codice: `LeasesController`, `LongRentRentsController`, `LongRentAgendaController`; servizi `RentRegisterService`,
`LongRentAgendaService`, `RentBillingService.Reminder.cs`; regole in `Casazen.Core/Leases/` (`LeaseListViews`, `RentInstallmentRules`,
`LongRentDeadlineRules`); impostazioni in `Casazen.Core/Services/RentCharges.cs`.

## 1. Endpoint

| Endpoint | Permesso | Cosa fa |
|---|---|---|
| `GET /api/leases?view=&q=&propertyId=` | `lease.read` | Elenco contratti (sintesi) con vista, ricerca, primo conduttore e stato dei canoni (§ 2) |
| `GET /api/long-rent/rents?month=&status=&page=&pageSize=` | `lease.read` | Registro canoni del mese con i numeri del mese (§ 3) |
| `POST /api/long-rent/rents/{id}/reminder` | `lease.create` | Sollecito e-mail di un canone, nota facoltativa (§ 4) |
| `POST /api/long-rent/rents/reminders` | `lease.create` | Sollecito di massa (da 1 a 50 canoni, stessa nota) |
| `GET /api/long-rent/deadlines?from=&to=&type=` | `lease.read` | Scadenze in ordine di data (§ 5) |
| `GET /api/long-rent/overview` | `lease.read` | Numeri d'area, checklist e prossima scadenza (§ 5) |

Tutte le letture sono limitate **in SQL** all'org del chiamante e agli immobili che raggiunge: `HostScope` letto dal database
(`IHostScopeResolver`, AM-03) e applicato con `InScope`, la stessa regola di `GET /api/leases` e di ogni altro elenco dell'host (tutta
l'org per titolare, amministratori, property manager, commercialista e collaboratore con tutti gli immobili; solo gli immobili assegnati
per il collaboratore «Solo alcuni»; gli immobili creati per un account senza team). `LongRentScopeQueries.WithinScope` aggiunge solo il
filtro d'org e il percorso canone, contratto, immobile (che `HostScopeQueryExtensions` non ha); non decide chi vede cosa. I solleciti
autorizzano ogni canone come `HostResource` del suo immobile (`LeaseOperations.Create`): altra org = 404, immobile non gestibile = 403
(nel sollecito di massa: saltato come «non trovato», senza rivelare che esiste). Oggi il ruolo Collaboratore del lungo periodo ha solo
`property.read` (`OrgRoleCatalog`): non supera la policy `lease.read` di questi endpoint (403); lo scope resta applicato dai servizi come
difesa in profondità e se un domani quel ruolo ricevesse `lease.read` non vedrebbe altri immobili che i suoi
(`LongRentHostScopeTests`, `LongRentSqlShapeTests`, `LongRentAggregatesPostgresTests`).

## 2. Elenco contratti (B3)

`view` (assente o `All` = tutti, com'è sempre stato; valore sconosciuto = 400 `validation_error`). Le viste **non sono salvate**:
derivano da stato e fine del periodo in corso (`LeaseListViews.Predicate`, una sola espressione SQL) perché il modello reale non ha
«in scadenza» né «terminato» (gap 04 § 4.1). Dal task LR-14 la data letta è `CurrentTermEndDate` (la fine del periodo in cui il
contratto si trova, che il rinnovo tacito sposta in avanti e il termine anticipato riporta indietro, § 10), non più la `EndDate` che
il contratto dichiara.

| Vista | Regola (oggi = giorno di Roma) |
|---|---|
| `Active` (Attivi) | `Registered` e `CurrentTermEndDate >= oggi` (include i contratti in scadenza) |
| `InPreparation` (In preparazione) | ogni stato diverso da `Registered` e `Rejected`: bozza, in firma, firmato in attesa di registrazione, registrazione in corso. **Qualunque sia la data**: una bozza o un contratto firmato da registrare è lavoro da finire (la bozza si elimina con `DELETE /api/leases/{id}`, LR-12) |
| `Expiring` (In scadenza) | `Registered`, non terminato e `CurrentTermEndDate <= oggi + 6 mesi`, **oppure** con una disdetta o un termine anticipato registrati (finisce e non si rinnova), qualunque sia la fine (sottoinsieme di `Active`; il transitorio, che non ha rinnovo, è «in scadenza» per gli ultimi 6 mesi) |
| `Ended` (Terminati) | `Registered` e `CurrentTermEndDate < oggi`, oppure `Rejected` (un contratto che non avrà effetto). Un contratto terminato prima della fine ha `CurrentTermEndDate` = il suo ultimo giorno: è terminato dal giorno dopo, come ogni contratto |

Un contratto è **terminato dal giorno dopo** il suo ultimo giorno (come la retention dei dati dei conduttori,
`LeasePartyPrivacyService.HasEnded`). Ogni contratto sta in una sola tra `InPreparation`, `Active`, `Ended`.

`q`: testo cercato senza distinguere maiuscole nel nome, città e indirizzo dell'immobile e nel nome e cognome (in entrambi gli
ordini) di **qualunque conduttore non anonimizzato** (al massimo 100 caratteri, il resto è tagliato; `%` e `_` sono caratteri,
non jolly).

**Il parametro `status`** che i client vecchi mandano non ha mai filtrato questo elenco e **continua a essere ignorato**: non è un
alias di `view` (valori diversi), così chi lo manda continua a ricevere l'elenco intero come prima. Coperto da
`LongRentLeaseListIntegrationTests.List_TheLegacyStatusParameter_IsStillIgnored_ItNeverBecameAFilter`.

`LeaseSummaryDto` guadagna (campi in coda, nessun campo cambia): `tenantFirstName`, `tenantLastName` (primo conduttore per ordine
di inserimento, **solo nome e cognome**, decisione D29; null se anonimizzato dalla retention o dalla richiesta di cancellazione,
LT-12, con `tenantAnonymized: true`), `nextRentDueDate` (scadenza della prima rata ancora da incassare, quella in ritardo se c'è),
`overdueRentCount`, `overdueRentAmount`, `overdueDays` (giorni dalla più vecchia in ritardo). Una rata in elaborazione
(`Processing`) è da incassare ma non in ritardo; pagate e annullate non contano.

**Una sola istruzione SQL** qualunque sia il numero di contratti: il registro dei canoni è un `LEFT JOIN` di una sottoquery raggruppata
per contratto, il primo conduttore tre sottoquery scalari sull'indice univoco delle parti (contratto, ruolo, posizione). Provato in
locale dalla traduzione Npgsql (`LongRentSqlShapeTests`) e su PostgreSQL dal conteggio dei comandi
(`LongRentAggregatesPostgresTests.Calls_TheNumberOfCommands_DoesNotGrowWith…`).

## 3. Registro canoni (B1)

`month` (`yyyy-MM`, default il mese corrente a Roma, tra il 2000 e il 2100: altro = 400 `rent_register_month_invalid`), `status`
(`All`, `Paid`, `Pending`, `Overdue`; altro = 400 `rent_register_status_unknown`), `page` (da 1) e `pageSize` (default 25, al
massimo 100). Elenca le rate con **scadenza nel mese**, non annullate, ordinate per scadenza e poi per id: l'id è unico, quindi
l'ordine è totale e una pagina non ripete né salta righe.

Stati (`RentInstallmentRules`, la stessa regola del registro del singolo contratto, `RentInstallmentView.IsOverdue`):
`Paid` = pagata; `Overdue` = `Scheduled` o `Failed` con scadenza passata; `Pending` = il resto che si attende (non ancora scaduta,
oppure un pagamento in elaborazione). Ogni rata non annullata sta in uno solo dei tre.

`counters` sono i numeri **del mese intero**, indipendenti da filtro e pagina: `expected` (tutto ciò che scade nel mese),
`collected`, `pending`, `overdue`, ognuno `{count, amount}`. Il totale della pagina viene dagli stessi numeri (un'istruzione sola per
i numeri, una per la pagina, una per lo stato Stripe dell'org: tre comandi sempre).

Ogni riga porta immobile, primo conduttore (solo nome), periodo, scadenza, importo, stato, `isOverdue`, `daysOverdue`, come e quando è
stata pagata, `lastReminderAt`, `reminderCount` e `canRemind`. L'indice `IX_RentLedgerEntries_OrgId_DueDate` serve il mese di
un'org come un intervallo già in ordine di scadenza.

## 4. Solleciti (B1, decisione D30)

Solo e-mail, **a mano**: nessun invio automatico, nessun SMS né WhatsApp.

- **Singolo e di massa** usano `RentBillingService.SendReminderAsync` sotto lo stesso lock advisory del contratto
  (`PostgresAdvisoryLocks.Scope.RentLease`) del pagamento, del webhook e dell'incasso offline: due solleciti dello stesso canone nello
  stesso istante ne mandano uno (l'altro riceve 422 `rent_reminder_too_soon`), e un sollecito che incontra un incasso non sollecita mai
  un canone già pagato (409 `rent_installment_not_payable`).
- **Chi riceve**: ogni conduttore con indirizzo non anonimizzato (indirizzi uguali contati una volta). Nessuno: 422
  `rent_no_tenant_email`, nulla registrato.
- **Quando**: canone `Scheduled` o `Failed`, anche non ancora scaduto (il testo dice «è da pagare entro il…» o «era in scadenza il…»).
  Pagato, annullato o schedule disattivo: 409 `rent_installment_not_payable`; pagamento in corso: 409 `rent_installment_in_flight`.
- **Limite di frequenza**: un sollecito per canone ogni `RentBilling__ReminderIntervalHours` ore (default **24**, da 1 a 720; un valore
  non numerico è il default). Il momento registrato è troncato al microsecondo che PostgreSQL conserva, così il confronto dopo l'andata e
  ritorno è esatto.
- **Registrato** sul canone: `LastReminderAt`, `ReminderCount`. Solo se l'e-mail è stata accodata: se la coda la rifiuta (servizio
  e-mail non configurato) il sollecito **non è avvenuto**, viene ripristinato tutto (anche il link precedente) e la risposta è 422
  `rent_reminder_not_sent`.
- **Testo**: modello `RentReminder` in `EmailTexts*.resx` (IT/EN, valori codificati HTML), con la nota (al massimo 300 caratteri,
  citata con l'etichetta «Messaggio del locatore», mai markup). Con Stripe Connect attivo l'e-mail porta un **nuovo link personale**
  (il precedente smette di funzionare, come per `payment-request`); senza Connect il conduttore paga «col metodo concordato col
  locatore».
- **Log e risposta senza dati personali**: solo id e conteggi; la risposta dice quanti destinatari, non chi.
- **Di massa**: `{ installmentIds: [...], note }`, 200 con `sent` e `skipped` (ognuno col codice dell'errore che avrebbe dato); un
  canone saltato non ferma gli altri. 400 `rent_reminder_batch_invalid` per nessun id, più di 50 o un id vuoto.

## 5. Scadenze e panoramica (B2)

`GET /api/long-rent/deadlines`: `from` e `to` (`yyyy-MM-dd`, estremi inclusi; default oggi e oggi + 90 giorni; al massimo 366 giorni,
altro = 400 `long_rent_deadlines_range_invalid`), `type` facoltativo (`RliRegistration`, `Questura`, `LeaseEnd`, `Notice`, `Rent`; altro =
400 `long_rent_deadlines_type_unknown`).

| Tipo | Data | Compare finché |
|---|---|---|
| `RliRegistration` | `min(stipula, inizio) + 30` (`RliRegistrationDeadline`, LT-04) | il contratto non è registrato (né rifiutato) e la scadenza è determinabile |
| `Questura` | 48 ore dalla consegna (`QuesturaCommunicationDeadline`, LT-07), solo con un conduttore extra-UE | il locatore non ha dichiarato la comunicazione |
| `LeaseEnd` | ultimo giorno del **periodo in corso** del contratto registrato (`CurrentTermEndDate`, LR-14: dopo un rinnovo tacito è la nuova fine) | non è passato |
| `Notice` | ultimo giorno per la disdetta: **fine del periodo in corso − 6 mesi**, solo per libero (4+4) e concordato (3+2); il transitorio non ne ha | non è passato, e **finché non c'è una disdetta né un termine anticipato registrati** (LR-14: non resta nulla da disdire) |
| `Rent` | scadenza di una rata `Scheduled` o `Failed` | non è pagata (il pagamento in corso non è una scadenza) |

Una finestra che contiene oggi porta anche ciò che è **già passato e ancora aperto** (registrazione, Questura, rate), con
`isOverdue: true`, prima di tutto; una finestra solo futura no. Si leggono solo i contratti non rifiutati e non terminati. Le rate sono
al massimo 500 (le prime per scadenza), `truncated: true` se erano di più. **IMU non è elencata** (il dato per Comune,
`ComuneImuChannel`, non ha una scadenza) e **ISTAT è fuori dalla prima versione** (D8). I **6 mesi di disdetta** sono il valore del
task e della demo, non una cifra verificata in `.claude/context/regulations`: da confermare con il legale insieme ai testi dei
contratti (D1).

`GET /api/long-rent/overview`: `leases` (contati come le viste dell'elenco: `active`, `expiring`, `inPreparation` = `toSign` +
`toRegister`, `ended`), `rents` (i numeri del registro per il mese corrente, `rentMonth`), `checklist` (in ordine di urgenza, solo
voci con qualcosa: canoni in ritardo di tutti i mesi, contratti da registrare, comunicazioni Questura, contratti da firmare; ognuna con
`count`, la data più urgente e il contratto da aprire) e `nextDeadline` (la prima scadenza non passata nei prossimi 90 giorni). Cinque
istruzioni SQL, qualunque sia il numero di contratti.

## 6. Database

Migrazione additiva `AddRentRegisterAndReminders` (una sola): indice `IX_RentLedgerEntries_OrgId_DueDate` e due colonne di
`RentLedgerEntries` (`LastReminderAt` nullable, `ReminderCount` default 0). Le righe esistenti restano com'erano e risultano «mai
sollecitate». Rollback: `Down` toglie colonne e indice (si perde solo la cronologia dei solleciti). L'indice più vecchio `(OrgId)` resta
(ridondante ma innocuo; toglierlo è un task a parte). Provata con righe storiche su PostgreSQL da
`AddRentRegisterAndRemindersMigrationPostgresTests` (CI).

## 7. Configurazione

| Variabile | Default | Significato |
|---|---|---|
| `RentBilling__ReminderIntervalHours` | `24` | ore tra due solleciti dello stesso canone (da 1 a 720; fuori intervallo viene riportato nell'intervallo, non numerico = 24) |

## 8. Cosa controllare in esercizio

- Un sollecito che risponde 422 `rent_reminder_not_sent`: l'e-mail non è configurata (`Email__ApiKey`, `Email__FromAddress`, vedi
  [`email.md`](email.md)); nulla è stato registrato, si può riprovare subito.
- «Non ricevo i solleciti»: il canone è in `Failed`/`Scheduled`? c'è un conduttore con indirizzo non anonimizzato? (`canRemind` nel
  registro dice se si può). Il link nell'e-mail è quello dell'ultimo sollecito: i precedenti non funzionano più.
- L'elenco `GET /api/leases` è lento: deve essere **un solo comando** (log di EF a livello `Information` con
  `Microsoft.EntityFrameworkCore.Database.Command`); il raggruppamento delle rate è per org.

## 9. Decisioni applicate e punti aperti

- D29: nome del conduttore sì, mai codice fiscale, e-mail o cittadinanza. D30: sollecito = e-mail, con link se c'è Connect, niente SMS.
- D8: nessuna ISTAT in questa versione. Disdetta, rinnovo tacito e termine anticipato non c'erano e sono arrivati con LR-14 (§ 10): le date
  di fine che questa pagina descrive sono ora quelle del periodo in corso.
- Da decidere: soglia di «in scadenza» e di disdetta (6 mesi), intervallo dei solleciti (24 ore), tetto della nota (300 caratteri), scadenza
  IMU quando il dato per Comune ne avrà una.

## 10. Fine effettiva del contratto: rinnovo tacito, disdetta, termine anticipato (LR-14)

Task LR-14 della wave di redesign (analisi `redesign/docs/wave/analysis/LR/`, brief `tasks/LR-14.md` nella **versione ridotta** della revisione di perimetro; decisione D5 dell'intervista, LR-D29). **Solo backend, una migrazione (`AddLeaseTermLifecycle`), nessun flag (sempre acceso).** Un contratto libero (4+4) o concordato (3+2) che nessuno disdice **si rinnova da solo** e resta valido: la `EndDate` è quindi solo la fine del primo periodo che il contratto dichiara (modello, registrazione RLI, primo piano dei canoni) e **la data che dice se il contratto è finito è `CurrentTermEndDate`**, la fine del periodo in cui si trova. Codice: regole pure `LeaseRenewal` (`Casazen.Core/Leases`); `LeaseTermService` e `RentBillingService.Term.cs` (Infrastructure); `LeaseTermController`; job `LeaseTermRenewalJob`.

### 10.1 I dati

| Colonna di `LeaseContracts` | Cosa è |
|---|---|
| `CurrentTermEndDate` | ultimo giorno (compreso) del periodo in corso, mezzanotte UTC del giorno di Roma come ogni data di contratto. Uguale a `EndDate` finché il contratto non si rinnova o non finisce prima. Indice `(OrgId, CurrentTermEndDate)` |
| `NoticeOn`, `NoticeFrom` (`Landlord` 0, `Tenant` 1) | il giorno della disdetta registrata e chi l'ha data; ferma il rinnovo |
| `TerminatedOn` | l'ultimo giorno di un contratto terminato prima della fine (e allora è anche `CurrentTermEndDate`); ferma il rinnovo per sempre. Segna anche come concluso un contratto già scaduto alla migrazione (§ 10.5) |

`EndDate` **non si muove mai**. Nell'entità, assegnare `EndDate` finché `CurrentTermEndDate` è ancora uguale (o non scritta) porta con sé anche questa: chi conosce solo la fine del contratto (la bozza guidata, la creazione completa) lascia il contratto coerente; dopo un rinnovo o un termine anticipato i due valori differiscono e l'assegnazione non tocca più il corrente. EF carica i campi, non passa da quel setter.

### 10.2 Endpoint

Tutti richiedono `lease.create` (come le scritture dei canoni) e autorizzano il contratto come riga del suo immobile (`HostResource`, TN-3, AM-03): altra org = 404 `lease_not_found`, immobile non gestibile = 403. Rispondono **200 con il dettaglio del contratto** (come `GET /api/leases/{id}`), dove `currentTermEndDate`, `noticeOn`, `noticeFrom`, `terminatedOn`, `renewalMonths` (derivato: 48 libero, 24 concordato, 0 transitorio) e `noticeWarning` dicono a che punto è; gli stessi campi (tranne l'avviso) sono in coda alle righe dell'elenco. Nessuno scrive a un terzo: nessuna e-mail al conduttore.

| Endpoint | Fa | Errori |
|---|---|---|
| `POST /api/leases/{id}/notice` `{ from, on }` | Registra la disdetta data da `Landlord` o `Tenant` il giorno `on` (dall'inizio del contratto a oggi): il contratto finisce col periodo in corso e non si rinnova. `noticeWarning` = `notice_possibly_late` se `on` è dopo «fine del periodo − 6 mesi» (CasaZen lo dice e non giudica la validità legale, LR-D29). Evento `NoticeRecorded` | 422 `lease_type_has_no_renewal` (transitorio), `lease_notice_date_invalid`; 409 `lease_not_in_force`, `lease_notice_already_recorded`, `lease_already_terminated`, `lease_busy` |
| `DELETE /api/leases/{id}/notice` | Toglie la disdetta, **solo finché `CurrentTermEndDate >= oggi`**. Se non c'è, non cambia nulla (200). Evento `NoticeRemoved` | 409 `lease_term_already_ended`, `lease_busy` |
| `POST /api/leases/{id}/terminate` `{ on }` | Termina il contratto il giorno `on`, il suo ultimo giorno (dall'inizio alla fine del periodo in corso; anche un giorno passato): `CurrentTermEndDate` e `TerminatedOn` diventano `on`, il rinnovo si ferma per sempre e **le rate del piano dei canoni il cui periodo inizia dopo `on` e non sono pagate vengono annullate** (§ 10.3). **Irreversibile**: nessun `DELETE`, l'interfaccia chiede una conferma. Evento `Terminated` | 422 `lease_termination_date_invalid`; 409 `lease_not_in_force`, `lease_already_terminated`, `lease_busy` |

«In vigore» vuol dire firmato da tutte le parti e non rifiutato: `Signed`, `RegistrationPending`, `SentToProvider`, `Registered` (`LeaseRenewal.InForceStatuses`; la registrazione è un obbligo fiscale, non una condizione del contratto). `lease_busy` è la riga del contratto bloccata da un'altra operazione per più di 5 secondi: nulla è stato scritto, si riprova. Un'azione e il job dello stesso contratto si eseguono uno dopo l'altro, ognuno su ciò che l'altro ha confermato.

### 10.3 Il piano dei canoni segue il contratto

Nella stessa transazione del contratto (il lock della riga del contratto e il lock `RentLease` del piano si uniscono): il contratto e il suo piano cambiano insieme o per nulla. Il lock del piano si prende **prima** della riga del contratto, come fanno tutti gli altri scrittori delle rate (inserire una rata chiede la riga del contratto per la chiave esterna: con l'ordine inverso una modifica qui e la configurazione del piano si aspetterebbero a vicenda).

- **Termine anticipato**: `RentBillingService.CancelInstallmentsAfterAsync` annulla (`Cancelled`) le rate con `PeriodStart > on` ancora da incassare (`Scheduled` o `Failed`), dopo aver annullato su Stripe il PaymentIntent che il conduttore potrebbe ancora pagare. Una rata **già pagata o in corso di pagamento non si tocca mai**, e quella il cui periodo contiene `on` resta com'è (nessuna regola pro rata, come per l'ultimo periodo parziale). Se una rata risulta pagata su Stripe nel frattempo, resta e il locatore riceve l'avviso di pagamento come sempre. Se Stripe fallisce la transazione torna indietro: il contratto non è terminato e si riprova.
- **Rinnovo**: `RentBillingService.ExtendScheduleToCurrentTermAsync` aggiunge a un piano **attivo** le rate dei periodi interi dopo l'ultima (stessa cadenza, giorno di scadenza e importo del piano; niente di ciò che esiste viene toccato). Le richieste di pagamento delle rate nuove sono quelle del job giornaliero `rent-collection`, come per ogni rata del piano: il rinnovo non manda nulla a nessuno. Un piano disattivato o assente non cambia.

### 10.4 Il job `lease-term-renewal`

Ogni ora al minuto 15 (UTC), sempre registrato. Una corsa prende una lock di sessione (due corse non si sovrappongono nemmeno fuori da Hangfire) e rinnova **un contratto alla volta**, ognuno in una transazione sotto il lock della sua riga, rileggendo le condizioni dopo il lock:

- **si rinnova** un contratto in vigore (§ 10.2), di tipo libero o concordato, senza disdetta (`NoticeOn`) e non terminato (`TerminatedOn`), il cui `CurrentTermEndDate` è **prima di oggi** (giorno di Roma): `CurrentTermEndDate` avanza di un periodo (`LongRentRules`: 48 mesi il libero, 24 il concordato; `LeaseRenewal.NextEnd`: il giorno prima dello stesso giorno un periodo dopo, es. 31/10/2030 → 31/10/2034) e c'è un evento `TermRenewed` (payload: il nuovo ultimo giorno) per ogni periodo; al massimo 20 periodi per contratto per corsa, la corsa dopo prosegue;
- **non si tocca mai**: un transitorio, una bozza o un contratto non firmato da tutte le parti, un rifiutato, uno con disdetta, uno terminato (anche prima del rinnovo), uno già rinnovato oltre oggi;
- **idempotente**: un contratto rinnovato non è più scaduto, quindi una seconda corsa, una corsa sovrapposta, un nuovo tentativo o un avvio manuale non trovano nulla e non aggiungono mai un periodo due volte. Un contratto occupato (lock oltre 5 secondi) o che fallisce (il piano, il database) è lasciato alla corsa dopo, senza fermare gli altri;
- all'inizio di ogni corsa **ripara** i contratti scritti da un server che ancora non conosceva la colonna (un deploy in corso): hanno il valore di partenza della colonna, che il database tiene come `-infinity`, cioè una fine corrente prima della fine del loro contratto senza un termine anticipato registrato; ricevono la fine del loro contratto, il valore che avrebbero avuto (`Lease term renewal: N leases written without the end of their period were given the end of their contract`, un avviso nel log). Nient'altro mette la fine corrente prima della fine del contratto senza `TerminatedOn`;
- scrive nel log solo id e conteggi, mai nomi: `Lease term renewal: N leases renewed by M periods, K left for the next run`.

Fino alla prima corsa dopo la mezzanotte di Roma (al massimo un'ora) un contratto che si rinnova da solo risulta «terminato» nelle viste e nelle scadenze, e le richieste di cancellazione dei dati del conduttore lo trattano come finito: si ricompone alla corsa. Il cambio di modalità (PM-02) invece lo considera già in corso (§ 10.6).

### 10.5 Migrazione, backfill e rollback

Una migrazione additiva, `AddLeaseTermLifecycle` (generata con `dotnet ef`, dopo l'ultima di `develop`): quattro colonne su `LeaseContracts` e l'indice `IX_LeaseContracts_OrgId_CurrentTermEndDate`; `LeaseEventType` guadagna in coda `NoticeRecorded` 20, `NoticeRemoved` 21, `Terminated` 22, `TermRenewed` 23. `AddLeaseTermLifecycle.BackfillSql`:

1. `CurrentTermEndDate = EndDate` per ogni riga (solo quelle ancora al valore di partenza della colonna, che per una `timestamptz` non nulla aggiunta da EF il database tiene come `-infinity`: tutto ciò che precede l'anno 2; quindi rieseguirlo non disfa un rinnovo);
2. **nessun rinnovo retroattivo** (decisione del PO, 17/10): un contratto di tipo libero o concordato, firmato (stati 3, 4, 5, 6), il cui ultimo giorno era già passato il giorno della migrazione (Roma) **resta «concluso»** come il sistema l'ha sempre mostrato. È registrato come terminato il suo ultimo giorno (`TerminatedOn = EndDate`), ed è questo che tiene il job lontano: non si inventa nessun periodo e nessun evento. Se un locatore dice che uno di questi contratti è andato avanti, l'assistenza può azzerare `TerminatedOn` con una `UPDATE`: la corsa dopo lo porta avanti periodo per periodo. Un contratto che finisce **dopo** la migrazione, senza disdetta, si rinnova.

**Rollback**: `Down` toglie colonne e indice; si perdono le date di disdetta, di termine anticipato e di fine corrente dei contratti rinnovati (le `EndDate` restano). Se si torna anche al codice precedente, che non conosce gli eventi nuovi, vanno cancellati prima: `DELETE FROM "LeaseEvents" WHERE "EventType" IN (20, 21, 22, 23)` (i numeri di `LeaseEventType` alla fusione; controllare l'enum). Le rate annullate da un termine anticipato restano `Cancelled`. La regola più rischiosa (rinnovo una volta sola, contratto già scaduto alla migrazione mai rinnovato) è provata su PostgreSQL, con righe scritte allo schema precedente, da `LeaseTermRenewalPostgresTests` (CI).

### 10.6 Cosa legge la fine corrente

| Lettore | Come |
|---|---|
| Viste dell'elenco, conteggi della panoramica, scadenze `LeaseEnd` e `Notice` (§ 2, § 5) | `CurrentTermEndDate`; `Notice` sparisce con una disdetta o un termine registrati |
| Occupazione dell'immobile: cambio modalità (PM-02) e cancellazione dell'immobile (`LeaseOccupancy`) | un contratto rinnovato tiene l'immobile fino alla nuova fine + 1; con una disdetta o un termine anticipato lo libera dal giorno dopo l'ultimo giorno. La bozza resta un blocco. Un contratto che si rinnova da solo e aspetta la corsa del job tiene l'immobile anche se la sua fine è già passata (nessuna data lo libera finché il job non ha girato): un passaggio ad affitto breve dovuto quella mattina fallisce con `blocked_by_lease` invece di precedere il rinnovo |
| Privacy dei conduttori (`LeasePartyPrivacyService`): richiesta di cancellazione, retention, «altro contratto in corso» | un contratto rinnovato non si anonimizza finché dura |
| Promemoria Questura (`rli-deadline-reminder`), piano dei canoni (`ConfigureScheduleAsync`, periodo finale parziale), riepilogo fiscale (`FiscalService`: immobile «locato» nell'anno), avviso di sovrapposizione della revisione della bozza | la fine del periodo in corso |
| Contratto stampato, registrazione RLI, IMU, consulenza cedolare, controllo del termine alla firma, data di consegna per la Questura | restano sulla `EndDate`: sono il primo periodo che il contratto dichiara |

### 10.7 Decisioni applicate e punti aperti

- **Versione ridotta** (revisione di perimetro del 17/10): fuori la restituzione del deposito, «duplica come bozza» (LR-D31), `DryRun` e configurazione del job, il punto di aggancio `ILeaseTermRenewedHandler` (il job chiama direttamente l'estensione del piano) e gli eventi in più; anche le note libere su disdetta e termine, che sarebbero dati personali da cancellare con l'anonimizzazione. Seguiti proposti: LR-14-FU1 (deposito), LR-14-FU2 («duplica come bozza»), LR-14-FU3 (note).
- **Valori di legge da far confermare dal legale (LR-D29)**: preavviso 6 mesi, periodi di rinnovo 48/24 mesi (`LongRentRules`, nessuna cifra nell'interfaccia: `GET /api/long-rent/rules`) e l'ipotesi che i rinnovi successivi ripetano lo stesso periodo.
- Nessuna e-mail automatica ai conduttori, da nessuno dei tre flussi (il rinnovo estende il piano; le richieste di pagamento restano quelle del job `rent-collection`).
- Il giorno di una disdetta o di un termine è un giorno di calendario (Roma); il termine anticipato fissa l'**ultimo** giorno del contratto, che è terminato dal giorno dopo (come ogni contratto: `Ended` legge `CurrentTermEndDate < oggi`).
- **Limite noto, PM-02** (LR-14-FU4): per un contratto che si rinnova da solo l'anteprima del cambio modalità indica come primo giorno libero la fine del periodo + 1 anche senza una disdetta registrata; chi programma il passaggio ad affitto breve per quel giorno, senza disdetta, trova alla mezzanotte il contratto che si rinnova e il passaggio fallisce (`blocked_by_lease`, l'host è avvisato). Dovrebbe essere l'interfaccia a chiedere prima la disdetta.
