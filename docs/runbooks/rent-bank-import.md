# Affitti lunghi: import dell'estratto conto sui canoni (RENT-IMPORT)

Richiesta del product owner: «queste scadenze devono essere registrate a mano? non si possono collegare al conto o a un sistema per il
pagamento degli affitti?». Le scadenze non si scrivono a mano (le genera `RentSchedule`), ma un canone diventava `Paid` solo in due
modi: il pagamento online sul conto Stripe del locatore, o il locatore che lo dichiara pagato a mano (`MarkPaidOffline`). Questa è la
**fase 1**, senza nuovi fornitori: il locatore carica il file dei movimenti che scarica dalla sua banca, il server abbina gli
accrediti ai canoni ancora da incassare e il locatore **conferma**. In questa versione **niente è segnato come pagato senza una
conferma**, e la conferma passa dal percorso «offline» già esistente, così ogni regola del registro resta in un posto solo.

Codice: `LongRentBankImportController`, `RentBankImportService`, `RentBankMovement` (tabella `RentBankMovements`, migrazione
`AddRentBankMovements`), e in `Casazen.Core/BankStatements/` i lettori (`BankStatementParser`, `TabularStatementReader`, `XlsxTableReader`,
`Camt053StatementReader`, `Mt940StatementReader`), l'abbinatore (`RentBankMatcher`) e le impronte (`BankMovementFingerprint`,
`BankPayerHash`). Frontend: «Importa estratto conto» nella pagina Canoni.

## 1. Endpoint

| Endpoint | Permesso | Cosa fa |
|---|---|---|
| `POST /api/long-rent/rents/bank-import/preview` | `lease.create` | Legge il file e propone, per ogni accredito, il canone che paga. **Non scrive nulla** e non conserva il file |
| `POST /api/long-rent/rents/bank-import/confirm` | `lease.create` | Riceve **di nuovo il file** con le scelte del locatore e dichiara pagati i canoni scelti |

Entrambi sono `multipart/form-data`. Campi: `file`; `mapping` (JSON, facoltativo, § 2); solo nella conferma `selections` (JSON, da 1 a 500
`{ "fingerprint": "…", "installmentId": "…" }`). Limite di frequenza `RentBankImport`: 30 chiamate ogni 10 minuti per persona.

La conferma rimanda il file perché **importo, data e pagatore li legge il server dal file** e non si fida del client: il client dice solo
quale accredito (impronta) paga quale canone. Così non c'è nulla da salvare tra anteprima e conferma.

## 2. Formati e limiti

Il formato lo decide il **contenuto**, non il nome del file. Quattro formati:

- **CSV** (anche `.txt`): separatore `;`, `,`, tabulazione o `|` (vince quello che divide più righe nello stesso numero di celle);
  UTF-8 (con o senza BOM), UTF-16 con BOM, oppure Windows-1252; righe di preambolo prima dei titoli; virgolette e a capo dentro le celle.
- **Excel** (`.xlsx`, solo il primo foglio): stringhe condivise o inline, numeri veri, date come seriali, celle vuote. Letto senza
  librerie (è un zip di XML). Un `.xls` vecchio non si legge: 415, il locatore lo salva come `.xlsx` o CSV.
- **CAMT.053** (XML ISO 20022, qualsiasi versione): solo movimenti `BOOK`, `CdtDbtInd` (la direzione sul conto), più righe `TxDtls` con
  importi propri, pagatore (`Dbtr`/`Pty`/`UltmtDbtr`) e IBAN. **Gli storni** (`RvslInd = true`) si contano e **non si offrono mai** come
  accrediti, qualunque sia `CdtDbtInd`: uno storno è la banca che riprende o restituisce una scrittura, non un conduttore che paga (un
  bonifico restituito arriva come addebito con `RvslInd`, e non deve diventare un accredito). DTD ed entità esterne **vietati**.
- **MT940**: righe `:61:` con `:86:` in tutti e tre i modi (sottocampi `?20…?33`, tag `/NAME/…/REMI/…`, testo libero). Solo `C` è un
  accredito: `D`, `RC` e `RD` si contano e si scartano. La divisa è quella del saldo iniziale (`:60F:`/`:60M:`), euro se manca.

Per le tabelle i titoli sono riconosciuti in italiano e in inglese («Data contabile», «Data valuta», «Descrizione», «Causale», «Importo»,
«Dare»/«Avere», «Entrate»/«Uscite», «Segno», «Tipo movimento», «Ordinante», «IBAN»…). Importi `1.234,56`, `1,234.56`, `-12,50`, `12,50-`,
`(12,50)`, `€ 12,50`, `12,50 DR`; date `gg/mm/aaaa`, `aaaa-mm-gg`, con ora, con fuso (il giorno resta quello scritto, mai spostato in UTC).
Una colonna del segno può dire `D`/`A`, «Dare»/«Avere», «Entrata»/«Uscita» o solo `+`/`-` (anche i trattini lunghi di un foglio di calcolo).
Se il file non dice in nessun modo la direzione di un importo senza segno, ogni riga è letta come accredito: l'abbinamento chiede comunque
l'importo uguale a un canone e, per essere sicuro, il nome del conduttore, e la conferma è sempre del locatore.

Se i titoli non si riconoscono: **422 `bank_statement_columns_unrecognized`** con `headerRow` (la riga che sembra di titoli, -1 se nessuna),
`columnCount` e `rows` (le prime righe, sempre fino alla prima riga sotto i titoli, per far scegliere le colonne). Il locatore dice quale colonna è data, descrizione e importo e il
client rimanda il file con `mapping = {"headerRow":0,"date":0,"description":1,"amount":2}` (righe e colonne contate da 0 sulle righe non
vuote; `headerRow` -1 se il file non ha titoli; al posto di `amount` si può dare `credit` e `debit`). Un mapping che non combacia: 422
`bank_statement_mapping_invalid`.

Limiti (`BankStatementLimits`): file **8 MB** (413 `bank_statement_too_large`), **5.000 accrediti** e 50.000 righe (422
`bank_statement_too_many_rows`), 100 colonne, 1.000 caratteri per cella, 48 MB per ogni parte di un `.xlsx` una volta decompressa (il
limite si applica ai byte letti, non all'intestazione dello zip). Altri errori: 400 `bank_statement_file_missing`, 415
`bank_statement_format_unsupported` (PDF, `.xls`, immagini, zip che non è un xlsx), 422 `bank_statement_unreadable`, 422
`bank_statement_empty`. Si tengono solo gli **accrediti in euro**; le uscite si contano e si scartano.

## 3. Regole di abbinamento (`RentBankMatcher`, una funzione pura)

**Candidati.** Canoni dell'org che il chiamante raggiunge (`LongRentScopeQueries.WithinScope`, in SQL), in stato `Scheduled` o `Failed`
(non `Paid`, `Processing`, `Cancelled`), con **importo uguale al centesimo** a quello dell'accredito, e con la data dell'accredito tra 45
giorni prima e 400 giorni dopo la scadenza (la data non va oltre oggi: un accredito datato avanti conta come di oggi).

**Segnali** (punti): cognome di un conduttore nel testo o nel pagatore +40 (+10 anche il nome); IBAN già visto su un abbinamento
confermato dello stesso contratto +50; nome del pagatore già visto +40; nome o indirizzo dell'immobile +15 (poco, da solo non basta);
il mese del canone nominato nel testo +25, un altro mese −30; vicinanza alla scadenza da 0 a +15. Cognomi sotto 3 lettere non contano.
I mesi si leggono come «ottobre 2026», «10/2026», «2026-10», «mese di set»; la **data del bonifico** («del 03/10/2026», «3 ottobre
2026») non è un mese di riferimento.

**Fiducia.**

| Livello | Quando | Cosa fa la schermata |
|---|---|---|
| `High` | c'è un'identità forte (cognome, o pagatore già visto), un solo contratto è plausibile (un altro deve stare almeno 25 punti sotto), un solo canone di quel contratto è possibile (uno solo, o uno solo col mese giusto), nessun mese in conflitto, non oltre 120 giorni dopo la scadenza e **non più di 10 giorni prima**, a meno che il testo nomini il mese | già spuntato: **un clic li conferma tutti** |
| `Medium` | qualche indizio (un nome, l'immobile, il mese) ma non basta a essere sicuri del contratto o del canone | proposto, da spuntare a mano |
| `Low` | solo l'importo | nessuna proposta: il locatore sceglie il canone tra quelli possibili |

Un canone è proposto a **un solo accredito**: i più sicuri scelgono per primi, chi perde il suo canone passa al successivo e non è più
`High`. Cosa **non** fa: importi diversi (nemmeno di un centesimo), pagamenti cumulativi di più mesi, pagamenti parziali (il registro non
li conosce: un canone è pagato o no).

**Perché il limite dei 10 giorni prima.** I canoni già dichiarati a mano non sono candidati: se il locatore ha segnato ottobre a mano e poi
importa l'estratto di ottobre, l'accredito del 3 ottobre («Bonifico Rossi», senza mese) troverebbe come unico candidato novembre, a 33
giorni dalla scadenza, e sarebbe `High` e già spuntato. Con il limite diventa `Medium`: lo si vede nella sezione «Da controllare» e non
si conferma per distrazione. Se il testo nomina il mese («canone novembre») resta `High` anche con più giorni di anticipo.

## 4. Conferma

Per ogni scelta, nell'ordine: l'accredito deve essere nel file (`bank_movement_not_found`) e non già importato
(`bank_movement_already_imported`); il canone deve essere nello scope (altrimenti `rent_installment_not_found`, uguale a «non esiste»,
anche per un immobile che il chiamante non può gestire: `HostResource` + `LeaseOperations.Create`); l'importo deve essere quello del
canone **e la divisa l'euro** (`bank_movement_amount_mismatch`, anche per una scelta fatta a mano su un accredito in un'altra valuta);
poi `IRentBillingService.MarkPaidOfflineAsync` con `PaidOn` = data valuta dell'accredito, nota
**«Da estratto conto»**, `PaidVia = Offline`, `MarkedPaidByUserId` = chi conferma. È **lo stesso codice** della registrazione a mano:
lock del contratto, PaymentIntent Stripe cancellato se il conduttore poteva ancora pagare online, canone già pagato (409
`rent_installment_not_payable`) o con un pagamento online in corso (409 `rent_installment_in_flight`), data non futura e non più di un
anno prima dell'inizio del contratto (`rent_paid_on_invalid`). Una scelta rifiutata finisce in `skipped` col suo codice e **non ferma le altre**; la
risposta è sempre 200.

**L'accredito si prenota prima di pagare il canone.** La riga di `RentBankMovements` si salva *prima* di chiamare il registro, e l'indice
univoco dell'impronta lascia passare una sola di due richieste che corrono per lo stesso accredito (due schede, il proprietario e un
gestore): l'altra riceve `bank_movement_already_imported` e non paga niente, quindi un accredito non può pagare due canoni. Poi:

- il registro rifiuta (canone già pagato, in corso, non trovato…): la prenotazione si **rilascia** e la scelta finisce in `skipped`;
- esito ignoto (la richiesta è annullata, cade il database, Stripe): il **registro dice** se il canone è stato pagato dall'import (pagato
  offline con la nota «Da estratto conto») e la prenotazione resta solo in quel caso; l'errore non si nasconde (499/5xx) e i canoni già
  confermati prima restano registrati, per cui rifare l'import li mostra come «già importati»;
- il processo muore proprio tra le due scritture: resta un accredito nascosto come importato e un canone ancora da incassare, che il
  locatore **vede** in «Canoni» e dichiara a mano. È il lato sicuro: mai un accredito che paga due canoni.

Per i dettagli in `RentBankImportService` (`TryClaimAsync`, `ReleaseAsync`, `WasPaidByTheImportAsync`): il rilascio scarta prima quello che
un pagamento fallito ha lasciato a metà nel contesto, per non salvarlo insieme.

## 5. Idempotenza e privacy

- **Impronta.** SHA-256 di date (contabile e valuta), importo, divisa, testo normalizzato (minuscolo, senza accenti), nome e IBAN del
  pagatore, riferimento della banca, **org** e ordine tra righe identiche dello stesso file. Lo stesso movimento in due esportazioni che si
  sovrappongono, o lo stesso file due volte, ha la stessa impronta e si importa **una volta** (indice univoco `(OrgId, Fingerprint)`);
  due bonifici uguali lo stesso giorno si distinguono per l'ordine. Limiti noti: lo **stesso** movimento esportato in due formati diversi
  (CSV e CAMT, con colonne di descrizione diverse) ha due impronte e si può importare due volte, la seconda sul canone successivo; e
  un'esportazione che comincia a metà di una serie di righe *identiche* (stessa data, importo, testo e pagatore) numera la prima riga
  come la prima della serie dell'esportazione precedente, e quell'accredito risulta «già importato». Sono casi di una riga uguale a
  un'altra, con la stessa data e lo stesso importo; il locatore vede comunque il canone ancora da incassare e lo dichiara a mano.
- **Cosa si salva** (`RentBankMovements`, `ITenantOwned`): impronta, canone e contratto, importo e data valuta, chi ha confermato e quando,
  e due **hash** del pagatore (IBAN e nome) per riconoscerlo il mese dopo. **Mai** il file, il testo del bonifico, un nome o un IBAN.
- **Gli hash hanno una chiave.** Un IBAN o un nome si indovinano, e un SHA-256 semplice li darebbe a chi legge la tabella: sono HMAC-SHA256
  (`BankPayerHash`) con una chiave ricavata dal segreto dei link firmati (`SIGNED_LINK_SECRET`, lo stesso del link di disiscrizione, ADR-007-R20),
  che non è nel database, e con l'org nel messaggio. **Senza quel segreto non si salva nessun hash**: il pagatore non viene riconosciuto
  e l'abbinamento va avanti con cognome, mese, immobile e importo. Cambiare il segreto fa dimenticare i pagatori già visti (si
  reimparano alla conferma successiva), non rompe nient'altro; l'impronta del movimento **non** dipende dal segreto, perché deve restare
  stabile per non importare due volte.
- **Il file non tocca il disco.** ASP.NET Core appoggia su un file temporaneo i campi `multipart` sopra 64 KB: le due azioni alzano
  `MemoryBufferThreshold` alla dimensione massima della richiesta (come il check-in ospiti, ADR-013-R2), per cui l'estratto resta in memoria
  per la durata della richiesta, e un test lo verifica per le due azioni.
- **Cosa non si salva né si logga.** I log portano conteggi e id (mai nomi, IBAN, causali, nome del file). L'anteprima restituisce
  causale e pagatore **solo a chi ha caricato il file** (sono nel suo file), troncati.
- **Cancellazione.** Quando si anonimizzano i conduttori di un contratto (`LeasePartyPrivacyService`, richiesta di cancellazione o
  retention) gli hash del pagatore di quel contratto diventano `NULL`; la riga resta e dice solo che un accredito ha pagato quel canone.
  Su un contratto con la cancellazione richiesta o già anonimizzato un accredito tardivo (la finestra è di 400 giorni) **non riscrive**
  gli hash.

## 6. Scope e permessi

Come il registro dei canoni: org e, se non ha tutta l'org, solo gli immobili che raggiunge (`HostScope`, `InScope`). L'anteprima propone
**solo** canoni dentro lo scope (un collaboratore «Solo alcuni» non vede mai conduttori di altri immobili, nemmeno come alternativa); la
conferma rilegge i canoni nello scope e autorizza ogni immobile. Serve `lease.create` (lo stesso di «Registra un incasso»): chi può solo
leggere i contratti non vede il pulsante.

## 7. Migrazione e rollback

`AddRentBankMovements`: una tabella nuova con i suoi indici, **additiva** (non tocca righe esistenti). Rollback: `dotnet ef migrations
remove` prima del rilascio, oppure `DROP TABLE "RentBankMovements"` dopo (i canoni già segnati restano pagati: il percorso è quello offline).
Non c'è un flag: finché il locatore non conferma non cambia nulla. Per spegnere la funzione basta togliere il pulsante dal frontend.

## 8. Test

`Casazen.Tests/Unit/BankStatements/*` (valori, quattro formati, storni, divisa MT940, colonna del segno, impronte, hash con chiave, abbinatore
con il limite dei giorni prima, `.xlsx` con `<is/>` vuoto e stringhe condivise a nodi), `Unit/Services/RentBankImportServiceTests` (scope,
conferma attraverso il percorso offline, doppio import, file sovrapposti, due richieste per lo stesso accredito, rilascio della
prenotazione dopo un errore, annullamento dopo il pagamento, divisa, pagatore riconosciuto, cancellazione degli hash, nessun hash dopo
l'anonimizzazione né senza segreto, log senza dati personali), `Integration/LongRentBankImportIntegrationTests` (HTTP: proprietario,
collaboratore con un solo immobile, gestore, altra org, permessi, errori, mappatura delle colonne, file solo in memoria). Il registro e le regole di sicurezza di sempre (`EndpointAuthorizationArchitectureTests`,
`SharedResourcesLocalizationTests`, `TenantQueryFilterArchitectureTests`) valgono anche per questi endpoint.

## 9. Dopo la fase 1 (decisioni del PO)

- **Addebito SEPA con Stripe** (mandato firmato alla firma del contratto): riusa Stripe Connect e i webhook, nessun fornitore nuovo;
  `RentSchedule.MandateReference` esiste già e non è usato.
- **Open banking** (collegamento al conto tramite un AISP autorizzato): fornitore nuovo, costo per collegamento, consenso da rinnovare
  ogni 90-180 giorni, lavoro GDPR. Riusa l'abbinatore di questo task: cambia solo da dove arrivano i movimenti.
