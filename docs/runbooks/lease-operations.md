# Affitti lunghi: le operazioni del contratto — lettere in bozza, disdetta e recesso (LR-OPS-1)

Task LR-OPS-1 (richiesta dell'utente del 25/10/2026: la scheda del contratto deve essere «non solo un contenitore di dati, ma una guida per il cliente», con le sezioni per «preparare la disdetta da mandare all'inquilino, formalizzare i rinnovi e altre operazioni utili»). La «Guida» (LR-GUIDE-1) dice cosa fare; le **operazioni** sono il «come»: ogni operazione dice **quando** serve (la scadenza calcolata dal contratto), prepara **una bozza di lettera** con i dati del contratto, scaricabile in PDF, e dice **cosa fare dopo**. Questo documento descrive il pezzo 1a (disdetta del locatore, recesso del conduttore) con l'infrastruttura delle lettere, e il pezzo 1b (rinnovo e conferma dell'esigenza del transitorio); i pezzi seguenti aggiungono qui le loro sezioni. **Nessun flag, nessuna migrazione** (valori in coda a `LeaseEventType`, che è un intero in colonna).

## Principio

- **CasaZen prepara, la persona firma e invia.** Nessuna lettera parte da qui: niente PEC, raccomandata o e-mail al conduttore, da nessun evento. Il PDF si scarica, si firma, si spedisce; poi, se vuole, «Segna come inviata» (giorno e mezzo) scrive un evento nella cronologia e **nient'altro** (il contratto non cambia).
- **Le lettere sono bozze non approvate** finché il PO non le approva con variabili d'ambiente, come i modelli di contratto ([`lease-contract-templates.md`](lease-contract-templates.md)). Finché non lo sono il PDF ha la filigrana «BOZZA — testo non ancora approvato» su ogni pagina e una riga in fondo che lo ripete (resta se il testo viene copiato), e l'interfaccia lo dice in una frase.
- **Nessuna cifra di legge nel codice dell'interfaccia né nelle lettere**: i mesi di preavviso e i giorni per comunicare vengono da `GET /api/long-rent/rules` (`noticeMonths`, `registrationDays`; valori di prodotto ancora da confermare, LR-D29). Ciò che il contratto non ha è `[da completare]` nel PDF.
- **Privacy.** Le residenze delle parti (cifrate a riposo) sono lette **sul server** e finiscono solo nel PDF che la persona scarica: mai in un DTO, in un log o in un evento. Nella lettera non c'è mai il codice fiscale né l'e-mail di una parte. Gli eventi portano nomi di codici e giorni, nessun dato di una persona.

## 1. Endpoint

Tutti richiedono `lease.create` (anche la lettura: le lettere portano le residenze) e autorizzano il contratto come riga del suo immobile (`HostResource`, TN-3, AM-03): altra org = 404 `lease_not_found`, immobile non gestibile = 403.

| Endpoint | Fa | Errori |
|---|---|---|
| `GET /api/leases/{id}/operations` | Cosa si può fare ora, calcolato sul server: `disdetta { available, firstPeriod, periodEnd, lastDayToGiveNotice, late, reasons[] }`, `withdrawal { available, minReceivedOn, maxReceivedOn, maxContractualNoticeMonths, recorded? }`, `letters[] { kind, approved, sent? }`, `renewal { available, opensOn, startsOn, standardEnd, standardMonths, latestAgreedEnd, conflict?, recorded? }`, `transitoryConfirmation { applies, available, holder?, confirmBy }`. Nessun dato di una persona | 404 |
| `POST /api/leases/{id}/letters/disdetta` `{ reason? }` | PDF della bozza della disdetta del locatore. Alla prima scadenza `reason` è obbligatorio e la lettera lo scrive | 409 `lease_not_in_force`, `lease_already_terminated`; 422 `lease_type_has_no_renewal` (transitorio), `lease_disdetta_reason_required`, `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/presa-atto-recesso` `{ receivedOn, reason, noticeMonths? }` | PDF della bozza di presa d'atto del recesso del conduttore. Scrive la data di cessazione come la calcolerebbe `POST …/withdrawal` | 409 `lease_not_in_force`; 422 `lease_withdrawal_date_invalid`, `lease_withdrawal_notice_invalid`, `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/rinnovo` `{ startsOn, endsOn, agreed? }` | PDF della bozza dell'**accordo di rinnovo o proroga** (lo firmano le due parti, non si spedisce): il periodo che aggiunge, dal giorno dopo la fine del periodo in corso. `agreed`: proroga a un giorno concordato; altrimenti rinnovo standard. Si costruisce dai giorni dati, quindi si può scaricare prima e dopo la registrazione | 409 `lease_not_in_force`; 422 `lease_letter_not_applicable` (un tipo che non si rinnova), `lease_renewal_end_invalid`, `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/conferma-esigenza` | PDF della bozza della conferma, dal locatore al conduttore, che l'esigenza del contratto transitorio permane | 409 `lease_not_in_force`; 422 `lease_letter_not_applicable` (non è un transitorio in vigore, o l'esigenza è del conduttore), `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/{kind}/sent` `{ sentOn, means }` | «Segna come inviata»: evento `LetterSent`. `kind` = `disdetta`, `presa-atto-recesso` o `conferma-esigenza`; `means` = `RegisteredMail`, `Pec`, `ByHand`, `Other`. Risponde con le operazioni | 404 (lettera sconosciuta); 409 `lease_not_in_force`; 422 `lease_letter_date_invalid` (prima dell'inizio o nel futuro) |
| `POST /api/leases/{id}/renewal` `{ newEnd? }` | **Formalizza il rinnovo**: senza `newEnd` il rinnovo standard del tipo (48 mesi il libero, 24 il concordato, `LongRentRules`), con `newEnd` una proroga fino a quel giorno concordato. Risponde con il dettaglio del contratto | 409 `lease_not_in_force`, `lease_already_terminated`, `lease_renewal_blocked_by_notice`, `lease_renewal_period_over`, `lease_period_overlaps`, `lease_busy`; 422 `lease_type_has_no_renewal`, `lease_renewal_not_yet`, `lease_renewal_end_invalid` |
| `POST /api/leases/{id}/withdrawal` `{ receivedOn, reason, noticeMonths? }` | Registra il recesso del conduttore e **termina il contratto** nel giorno in cui il preavviso scade. Risponde con il dettaglio del contratto | 409 `lease_not_in_force`, `lease_already_terminated`, `lease_busy`; 422 `lease_withdrawal_date_invalid`, `lease_withdrawal_notice_invalid`, `lease_termination_date_invalid` (con `min` e `max`) |

I PDF hanno `Cache-Control: private, no-store`.

## 2. Le regole

### 2.1 Disdetta del locatore

Codice: `LeaseOperationRules.Disdetta` (`Casazen.Core/Leases`), pura.

- **Disponibile** se il contratto è in vigore (firmato da tutte le parti), di un tipo che si rinnova (libero 4+4, concordato 3+2: il transitorio finisce da solo e non ha disdetta), non terminato, il suo periodo non è finito e l'inquilino non ha già registrato una sua disdetta.
- **Ultimo giorno utile** (`lastDayToGiveNotice`): la fine del periodo in corso meno i mesi di preavviso (`LongRentDeadlineRules.NoticeDate`, la stessa data di Scadenze e Panoramica e del promemoria `lease-notice-reminder`, § 10.8 di [`long-rent-aggregates.md`](long-rent-aggregates.md)). `late`: oggi è dopo quel giorno; la bozza si prepara lo stesso e la persona è avvertita che potrebbe non bastare, CasaZen non giudica la validità (LR-D29).
- **Prima scadenza** (`firstPeriod`: `CurrentTermEndDate = EndDate`, cioè il contratto non si è ancora rinnovato da solo): la legge ammette il diniego solo per i motivi di un elenco chiuso e vuole il motivo scritto nella comunicazione «a pena di nullità» (art. 3, commi 1 e 2, L. 431/1998). I `reasons` sono le sette ipotesi, per codice: `OwnUse` (a), `LegalEntityUse` (b), `TenantOtherHome` (c), `DamagedBuilding` (d), `Renovation` (e), `TenantAbsent` (f), `Sale` (g). La bozza stampa **solo** il motivo scelto, con la riga «Dettagli: [da completare]» da riempire a mano. Alle scadenze successive nessun motivo: la bozza è una rinuncia al rinnovo.
- La disdetta **non si registra da qui**: «Segna come inviata» è solo un evento. Per fermare il rinnovo del contratto resta l'azione di sempre, «Registra una disdetta» (`POST /api/leases/{id}/notice`, § 10.2 di `long-rent-aggregates.md`): la Guida la propone dopo l'invio.

### 2.2 Recesso del conduttore

Codice: `LeaseOperationRules.Withdrawal`, `LeaseTermService.RecordWithdrawalAsync`.

- Il locatore **riceve** il recesso e lo registra con il giorno di ricezione (dall'inizio del contratto a oggi), il motivo (`GraveMotivi`: per legge, art. 3, comma 6, L. 431/1998, con il preavviso delle regole; `ContractualFaculty`: la facoltà che il contratto dà al conduttore, con il preavviso che il contratto scrive, da 1 a 12 mesi, `noticeMonths`; per i gravi motivi il preavviso è sempre quello delle regole) e, per la facoltà, i mesi.
- Il server calcola il **giorno di cessazione** = ricezione + preavviso (`LeaseOperationRules.WithdrawalEffectiveOn`: il giorno del mese corrispondente, o l'ultimo del mese se non esiste; es. ricevuto il 31/8 con 6 mesi finisce il 28/2) e **termina il contratto** con la cessazione anticipata di LR-TERM (§ 10.2.1 di `long-rent-aggregates.md`): stesse regole (`inizio < giorno <= fine del periodo in corso`, contratto già iniziato), stessi effetti (`TerminatedOn` e `CurrentTermEndDate` = quel giorno, rate non pagate dopo quel giorno annullate, ultimo periodo parziale come sempre), stessa transazione e stesso lock. **Irreversibile**, come la cessazione. Se il preavviso scade **dopo** la fine del periodo in corso il server risponde 422 `lease_termination_date_invalid` con `min` e `max` e non scrive nulla: il recesso va registrato quando il contratto si è rinnovato.
- Scrive due eventi: `WithdrawalRecorded` (payload `{motivo}:{ricezione}:{cessazione}:{mesi}`, es. `GraveMotivi:2026-10-01:2027-04-01:6`) e `Terminated` (la cessazione). Le operazioni leggono da `WithdrawalRecorded` il recesso già registrato.
- **Dopo il recesso** (la Guida lo dice): inviare la presa d'atto (la bozza), concordare la riconsegna e comunicare la risoluzione all'Agenzia delle Entrate entro `registrationDays` giorni (modello RLI; con la cedolare secca non c'è imposta ma la comunicazione resta: fatti verificati il 25/10/2026, § 10.2.1). Il giorno da cui decorre il termine lo conferma il commercialista (LR-TERM-FU1).

### 2.3 Rinnovo del contratto (1b)

Codice: `LeaseOperationRules.Renewal`, `LeaseTermService.RecordRenewalAsync`. Il rinnovo tacito automatico (job `lease-term-renewal`, § 10.4 di [`long-rent-aggregates.md`](long-rent-aggregates.md)) **resta com'è**: questa è l'azione *esplicita* con cui il locatore formalizza un rinnovo o una proroga prima della fine del periodo.

- **Disponibile** per un contratto in vigore di un tipo che si rinnova (libero, concordato), non terminato, **senza disdetta registrata** (va tolta prima: 409 `lease_renewal_blocked_by_notice`) e con il periodo non ancora finito (dopo, il contratto si rinnova da solo e il rinnovo lo scrive il job: 409 `lease_renewal_period_over`, per non aggiungere due volte lo stesso periodo). Solo **nell'ultimo tratto prima della fine**: dall'ultimo giorno per la disdetta (`renewal.opensOn`, la stessa data di Scadenze e Panoramica), quando le parti decidono; prima, 422 `lease_renewal_not_yet`. Registrare un rinnovo sposta la fine in avanti e chiude la finestra fino a quella del periodo dopo: non si può rinnovare due volte di seguito. `renewal.recorded` dice il rinnovo del periodo in cui il contratto si trova adesso.
- **Il nuovo periodo** va dal giorno dopo la fine del periodo in corso a: la fine standard (`LeaseRenewal.NextEnd`, i mesi del tipo) oppure il giorno concordato `newEnd` (dopo la fine del periodo e non oltre 20 anni, un limite di buon senso contro un refuso: 422 `lease_renewal_end_invalid`).
- **Sempre** `LeasePeriodGuard.EnsureFreeAsync` sul nuovo periodo, dentro la transazione, dopo il lock della riga del contratto e sotto il lock dell'immobile (`PostgresAdvisoryLocks.Scope.LeasePeriod`): se un altro contratto dello stesso immobile copre qualche giorno, 409 `lease_period_overlaps` (membri `conflictingLeaseId`, `conflictingFrom`, `conflictingTo`) e nulla è scritto. È il debito LR-OVERLAP-FU3 (§ 18 di [`long-rent-lease-draft.md`](long-rent-lease-draft.md)). Le operazioni dicono già prima, in `renewal.conflict`, se il rinnovo standard sarebbe rifiutato.
- **Effetti** (una transazione, lo stesso ordine dei lock di terminazione e rinnovo tacito: piano dei canoni, riga del contratto, immobile): `CurrentTermEndDate` avanza (`EndDate` non si muove mai), un piano dei canoni **attivo** si estende alla nuova fine (`ExtendScheduleToCurrentTermAsync`: le rate nuove, mai quelle che esistono), evento `RenewalRecorded` (payload `{Standard|Agreed}:{dal}:{al}`; il rinnovo tacito scrive `TermRenewed`). Dopo, il contratto è nel periodo successivo: le sue scadenze, la disdetta e la Panoramica leggono la nuova fine.
- **Dopo il rinnovo** (la Guida lo dice): comunicare il rinnovo o la proroga all'Agenzia delle Entrate entro `registrationDays` giorni (modello RLI; con la cedolare secca senza imposta) e ricordare che il piano dei canoni è già esteso. L'accordo (`Rinnovo`) è una bozza che le due parti firmano; CasaZen non invia nulla.
- **Il rinnovo tacito e un contratto che segue** (LR-OVERLAP-FU1, **non fatto**): il job rinnova per legge senza guardare gli altri contratti dell'immobile. Saltare quel rinnovo falsificherebbe la legge (il contratto continua comunque) e segnalarlo chiede un canale di avviso verso il locatore (e-mail o notifica) e un dato in più sul contratto: non è un pezzo piccolo, resta debito. Chi affitta deve registrare la disdetta o la cessazione del primo contratto.

### 2.4 Conferma dell'esigenza del contratto transitorio (1b)

Codice: `LeaseOperationRules.TransitoryConfirmation`. Un contratto transitorio finisce da solo e non si rinnova; se l'esigenza che lo giustifica continua, la parte che l'ha dichiarata la conferma per iscritto all'altra **prima della scadenza** (così dicono la bozza di riferimento `docs/legal-drafts/leases/contratto-transitorio.md`, art. 3, e il consiglio «Se l'esigenza continua, confermala per iscritto» della Guida; senza conferma, o se l'esigenza viene meno, il contratto può essere trattato come un 4+4. **Il modello di contratto distribuito** — l'Allegato B trascritto, [`lease-contract-templates.md`](lease-contract-templates.md) — **non ha questa clausola**: è un punto per il legale).

- `applies`: un transitorio in vigore, non terminato, con il periodo non finito. `confirmBy`: l'ultimo giorno del contratto. **Nessun anticipo in giorni è scritto**: i file del progetto dicono solo «prima della scadenza»; se la legge ne chiede uno, il legale o il commercialista lo confermano e si aggiunge alle regole dell'area (`LongRentRules`), un solo punto da cambiare (**da confermare**, LR-D29).
- `available` (e la bozza `ConfermaEsigenza`): solo se l'esigenza è del **locatore** (`TransitoryParty`); se è del conduttore la conferma la manda lui e la Guida lo dice senza lettera. La bozza riprende l'esigenza com'è scritta nel contratto (cifrata a riposo, letta sul server, solo nel PDF).
- Non cambia lo stato del contratto: «Segna come inviata» scrive l'evento `LetterSent` e basta; la conversione in 4+4 non è modellata.

## 3. Le lettere: file, formato, stato

`Casazen.Web/LeaseLetters/<Tipo>/<VersionId>.md` (`Disdetta`, `PresaAttoRecesso`, `Rinnovo`, `ConfermaEsigenza`), distribuiti con l'API (`Casazen.Web.csproj`) e letti **una volta per processo**: dopo una modifica serve un riavvio. Il formato è quello dei modelli di contratto (`# titolo`, `## id | Titolo`, `{{segnaposto}}`, righe `<!-- … -->` di commento per il legale); cambiano l'elenco dei segnaposto e delle sezioni.

- **Segnaposto** (`LeaseLetterPlaceholders`): `locatori`, `locatori_dettaglio` (nome e residenza, una riga per persona), `conduttori`, `conduttori_dettaglio`, `immobile_indirizzo`, `tipo_contratto`, `data_decorrenza`, `data_scadenza` (fine del periodo in corso), `data_ricezione`, `data_cessazione`, `preavviso_mesi`, `data_inizio_periodo`, `data_fine_periodo` (il periodo che aggiunge un rinnovo), `esigenza_motivo` (l'esigenza del transitorio, come scritta nel contratto). I giorni sono scritti «31 ottobre 2030». Un dato che il contratto non ha è `[da completare]`; le persone anonimizzate non compaiono.
- **Sezioni** (`LeaseLetterStructure`): tutte obbligatorie nel file; alcune sono **condizionali** e ne viene stampata solo una (i sette `motivo_…` della disdetta, solo alla prima scadenza; `rinuncia`, solo alle successive; `ricezione_gravi_motivi` o `ricezione_facolta`; `rinnovo` o `proroga`). Una sezione che non è nell'elenco rende il file incompleto.
- **Versione**: ogni tipo ha una bozza distribuita (`2026-11-bozza-v1`). Un testo cambiato è **un file nuovo con una versione nuova**: l'approvazione si riferisce a una versione esatta; non si modifica mai un file approvato.

| Stato (per tipo) | PDF |
|---|---|
| **Missing**: il file della versione non c'è | 422 `lease_letter_unavailable` (problema del deploy, scritto nel log) |
| **Incomplete**: sezione senza testo, segnaposto sconosciuto, titolo mancante | 422 `lease_letter_unavailable` |
| **NotApproved** (il predefinito): completo, non approvato | PDF con filigrana «BOZZA — testo non ancora approvato» e riga finale |
| **Approved** | PDF senza filigrana |

## 4. Come il PO approva una lettera

1. Far rivedere a un legale il file della versione (`Casazen.Web/LeaseLetters/<Tipo>/<VersionId>.md` e i commenti `LEGALE:`), provando prima la bozza: `POST /api/leases/{id}/letters/…` su un contratto di prova (test).
2. Sull'host dell'ambiente (test, poi produzione), per ogni tipo approvato:
   - `LeaseLetters__Variants__<Tipo>__VersionId=<VersionId>` (la versione che il legale ha rivisto; mai `dev-stub`)
   - `LeaseLetters__Variants__<Tipo>__Approved=true`
   - `LeaseLetters__Variants__<Tipo>__ApprovalReference=<chi ha approvato: parere o numero di protocollo>` e/o `LeaseLetters__Variants__<Tipo>__ApprovedAt=<AAAA-MM-GG>`
3. Riavviare. Il PDF di quel tipo non ha più la filigrana.
4. **Il repository non approva mai una lettera**: `LeaseLetterTests` fallisce se un `appsettings*.json` nomina una versione o imposta `Approved=true`.
5. **Rollback**: `LeaseLetters__Variants__<Tipo>__Approved=false` e riavvio.
6. **Controllo all'avvio** (come i modelli, § «Startup validation» di [`lease-contract-templates.md`](lease-contract-templates.md)): fuori da Development e Testing l'API **non parte** se un tipo è `Approved=true` ma non c'è la versione (o è `dev-stub`), mancano riferimento e data, il nome non è una lettera o il file manca o è incompleto; Railway tiene il deploy precedente. Un tipo non approvato non ferma mai nulla.

La riga delle variabili è nella tabella di [`deploy-checklist.md`](deploy-checklist.md) (§ 2.10).

## 5. Cronologia

Tre valori in coda a `LeaseEventType` (gli interi degli altri non cambiano): `WithdrawalRecorded`, `LetterSent` e `RenewalRecorded` (1b) (payload `{tipo}:{mezzo}:{giorno}`, es. `Disdetta:RegisteredMail:2026-11-03`). La cronologia del contratto li mostra come «Recesso dell'inquilino registrato» e «Lettera segnata come inviata»; il payload non esce nei DTO del contratto, solo nelle operazioni (`letters[].sent`, `withdrawal.recorded`).

## 6. Decisioni applicate, punti per il legale, rimandato

- **Default** (brief LR-OPS-1): lettere = bozze non approvate; testi prudenti e brevi, in italiano formale; nessun invio automatico; nessuna cifra di legge nel codice o nelle lettere; l'area «Operazioni» sta nella Guida, non in una pagina nuova.
- **Il motivo della disdetta è una scelta, non un testo libero**: l'elenco è quello dell'art. 3 comma 1 (letto il 29/10/2026 su due fonti, Brocardi e Legalars; sintesi, non il testo della norma). I dettagli di ogni motivo (chi userà l'immobile, i lavori, le condizioni di vendita) restano `[da completare]` a mano: non sono dati del contratto.
- **Per il legale, per primi**: (1) la formulazione dei sette motivi e se servono allegati (titolo edilizio per i casi d ed e; la denuncia di vendita per la prelazione, caso g, è un atto a parte); (2) alle scadenze successive il testo parla di «rinuncia al rinnovo» senza termini di risposta, che sono nel contratto: verificare la coerenza con il rinnovo tacito deciso dal prodotto (D5); (3) la presa d'atto «ne prende atto» senza riserve; (4) il preavviso della facoltà di recesso, che è quello scritto nel contratto (nei modelli di contratto è uno spazio a mano).
- **Rimandato / debiti**: LR-OPS-1-FU1 salvare la lettera scaricata fra i documenti del contratto (oggi si rigenera ogni volta); FU2 un «promemoria» di invio; FU3 la disdetta dell'inquilino (alla scadenza) come lettera di risposta; FU4 la lettera in inglese. Il pezzo 1c (ISTAT, opzione cedolare, riconsegna, restituzione della cauzione) aggiunge qui le sue sezioni. Debiti del 1b: LR-OVERLAP-FU1 (§ 2.3); LR-OPS-1b-FU1 un avviso al locatore quando il rinnovo tacito si sovrappone al contratto che segue; FU2 la conferma dell'esigenza del conduttore registrata dal locatore; FU3 il giorno di anticipo della conferma quando il legale lo conferma.

## 7. Prove

`LeaseOperationRulesTests` (scadenza della disdetta per tipo, giorno di cessazione), `LeaseWithdrawalTests` (il recesso rispetta le regole di cessazione e non scrive nulla quando le viola), `LeaseRenewalTests` (il rinnovo sposta la fine, estende il piano e scrive l'evento; un periodo che copre un altro contratto dell'immobile è rifiutato con 409 `lease_period_overlaps` e non scrive nulla; disdetta registrata, contratto terminato, periodo finito, transitorio rifiutati), `LeaseOperationRulesTests` (anche rinnovo e conferma dell'esigenza), `LeaseLetterTests` (bozze complete e non approvate, nessuna configurazione le approva, filigrana e riga finale, un solo motivo stampato, `[da completare]`, nessun codice fiscale né e-mail nel PDF).
