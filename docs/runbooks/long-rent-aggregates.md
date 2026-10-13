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
derivano da stato e data di fine (`LeaseListViews.Predicate`, una sola espressione SQL) perché il modello reale non ha «in scadenza»
né «terminato» (gap 04 § 4.1).

| Vista | Regola (oggi = giorno di Roma) |
|---|---|
| `Active` (Attivi) | `Registered` e `EndDate >= oggi` (include i contratti in scadenza) |
| `InPreparation` (In preparazione) | ogni stato diverso da `Registered` e `Rejected`: bozza, in firma, firmato in attesa di registrazione, registrazione in corso. **Qualunque sia la data**: una bozza o un contratto firmato da registrare è lavoro da finire, e non c'è ancora l'eliminazione della bozza (B4) |
| `Expiring` (In scadenza) | `Registered`, non terminato e `EndDate <= oggi + 6 mesi` (sottoinsieme di `Active`; il transitorio, che non ha rinnovo, è «in scadenza» per gli ultimi 6 mesi) |
| `Ended` (Terminati) | `Registered` e `EndDate < oggi`, oppure `Rejected` (un contratto che non avrà effetto) |

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
| `LeaseEnd` | ultimo giorno del contratto registrato | non è passato |
| `Notice` | ultimo giorno per la disdetta: **fine contratto − 6 mesi**, solo per libero (4+4) e concordato (3+2); il transitorio non ne ha | non è passato |
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
- D8: nessuna disdetta/rinnovo/ISTAT in questa versione; le date si calcolano dalla data di fine inserita (i rinnovi non sono modellati).
- Da decidere: soglia di «in scadenza» e di disdetta (6 mesi), intervallo dei solleciti (24 ore), tetto della nota (300 caratteri), scadenza
  IMU quando il dato per Comune ne avrà una.
