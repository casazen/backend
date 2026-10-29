# Affitti lunghi: le operazioni del contratto — lettere in bozza, disdetta e recesso (LR-OPS-1)

Task LR-OPS-1 (richiesta dell'utente del 25/10/2026: la scheda del contratto deve essere «non solo un contenitore di dati, ma una guida per il cliente», con le sezioni per «preparare la disdetta da mandare all'inquilino, formalizzare i rinnovi e altre operazioni utili»). La «Guida» (LR-GUIDE-1) dice cosa fare; le **operazioni** sono il «come»: ogni operazione dice **quando** serve (la scadenza calcolata dal contratto), prepara **una bozza di lettera** con i dati del contratto, scaricabile in PDF, e dice **cosa fare dopo**. Questo documento descrive il pezzo 1a (disdetta del locatore, recesso del conduttore) e l'infrastruttura delle lettere che anche i pezzi seguenti usano. **Nessun flag, nessuna migrazione** (due valori in coda a `LeaseEventType`, che è un intero in colonna).

## Principio

- **CasaZen prepara, la persona firma e invia.** Nessuna lettera parte da qui: niente PEC, raccomandata o e-mail al conduttore, da nessun evento. Il PDF si scarica, si firma, si spedisce; poi, se vuole, «Segna come inviata» (giorno e mezzo) scrive un evento nella cronologia e **nient'altro** (il contratto non cambia).
- **Le lettere sono bozze non approvate** finché il PO non le approva con variabili d'ambiente, come i modelli di contratto ([`lease-contract-templates.md`](lease-contract-templates.md)). Finché non lo sono il PDF ha la filigrana «BOZZA — testo non ancora approvato» su ogni pagina e una riga in fondo che lo ripete (resta se il testo viene copiato), e l'interfaccia lo dice in una frase.
- **Nessuna cifra di legge nel codice dell'interfaccia né nelle lettere**: i mesi di preavviso e i giorni per comunicare vengono da `GET /api/long-rent/rules` (`noticeMonths`, `registrationDays`; valori di prodotto ancora da confermare, LR-D29). Ciò che il contratto non ha è `[da completare]` nel PDF.
- **Privacy.** Le residenze delle parti (cifrate a riposo) sono lette **sul server** e finiscono solo nel PDF che la persona scarica: mai in un DTO, in un log o in un evento. Nella lettera non c'è mai il codice fiscale né l'e-mail di una parte. Gli eventi portano nomi di codici e giorni, nessun dato di una persona.

## 1. Endpoint

Tutti richiedono `lease.create` (anche la lettura: le lettere portano le residenze) e autorizzano il contratto come riga del suo immobile (`HostResource`, TN-3, AM-03): altra org = 404 `lease_not_found`, immobile non gestibile = 403.

| Endpoint | Fa | Errori |
|---|---|---|
| `GET /api/leases/{id}/operations` | Cosa si può fare ora, calcolato sul server: `disdetta { available, firstPeriod, periodEnd, lastDayToGiveNotice, late, reasons[] }`, `withdrawal { available, minReceivedOn, maxReceivedOn, maxContractualNoticeMonths, recorded? }`, `letters[] { kind, approved, sent? }`. Nessun dato di una persona | 404 |
| `POST /api/leases/{id}/letters/disdetta` `{ reason? }` | PDF della bozza della disdetta del locatore. Alla prima scadenza `reason` è obbligatorio e la lettera lo scrive | 409 `lease_not_in_force`, `lease_already_terminated`; 422 `lease_type_has_no_renewal` (transitorio), `lease_disdetta_reason_required`, `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/presa-atto-recesso` `{ receivedOn, reason, noticeMonths? }` | PDF della bozza di presa d'atto del recesso del conduttore. Scrive la data di cessazione come la calcolerebbe `POST …/withdrawal` | 409 `lease_not_in_force`; 422 `lease_withdrawal_date_invalid`, `lease_withdrawal_notice_invalid`, `lease_letter_unavailable` |
| `POST /api/leases/{id}/letters/{kind}/sent` `{ sentOn, means }` | «Segna come inviata»: evento `LetterSent`. `kind` = `disdetta` o `presa-atto-recesso`; `means` = `RegisteredMail`, `Pec`, `ByHand`, `Other`. Risponde con le operazioni | 404 (lettera sconosciuta); 409 `lease_not_in_force`; 422 `lease_letter_date_invalid` (prima dell'inizio o nel futuro) |
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

## 3. Le lettere: file, formato, stato

`Casazen.Web/LeaseLetters/<Tipo>/<VersionId>.md` (`Disdetta`, `PresaAttoRecesso`), distribuiti con l'API (`Casazen.Web.csproj`) e letti **una volta per processo**: dopo una modifica serve un riavvio. Il formato è quello dei modelli di contratto (`# titolo`, `## id | Titolo`, `{{segnaposto}}`, righe `<!-- … -->` di commento per il legale); cambiano l'elenco dei segnaposto e delle sezioni.

- **Segnaposto** (`LeaseLetterPlaceholders`): `locatori`, `locatori_dettaglio` (nome e residenza, una riga per persona), `conduttori`, `conduttori_dettaglio`, `immobile_indirizzo`, `tipo_contratto`, `data_decorrenza`, `data_scadenza` (fine del periodo in corso), `data_ricezione`, `data_cessazione`, `preavviso_mesi`. I giorni sono scritti «31 ottobre 2030». Un dato che il contratto non ha è `[da completare]`; le persone anonimizzate non compaiono.
- **Sezioni** (`LeaseLetterStructure`): tutte obbligatorie nel file; alcune sono **condizionali** e ne viene stampata solo una (i sette `motivo_…` della disdetta, solo alla prima scadenza; `rinuncia`, solo alle successive; `ricezione_gravi_motivi` o `ricezione_facolta`). Una sezione che non è nell'elenco rende il file incompleto.
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

Due valori in coda a `LeaseEventType` (gli interi degli altri non cambiano): `WithdrawalRecorded` e `LetterSent` (payload `{tipo}:{mezzo}:{giorno}`, es. `Disdetta:RegisteredMail:2026-11-03`). La cronologia del contratto li mostra come «Recesso dell'inquilino registrato» e «Lettera segnata come inviata»; il payload non esce nei DTO del contratto, solo nelle operazioni (`letters[].sent`, `withdrawal.recorded`).

## 6. Decisioni applicate, punti per il legale, rimandato

- **Default** (brief LR-OPS-1): lettere = bozze non approvate; testi prudenti e brevi, in italiano formale; nessun invio automatico; nessuna cifra di legge nel codice o nelle lettere; l'area «Operazioni» sta nella Guida, non in una pagina nuova.
- **Il motivo della disdetta è una scelta, non un testo libero**: l'elenco è quello dell'art. 3 comma 1 (letto il 29/10/2026 su due fonti, Brocardi e Legalars; sintesi, non il testo della norma). I dettagli di ogni motivo (chi userà l'immobile, i lavori, le condizioni di vendita) restano `[da completare]` a mano: non sono dati del contratto.
- **Per il legale, per primi**: (1) la formulazione dei sette motivi e se servono allegati (titolo edilizio per i casi d ed e; la denuncia di vendita per la prelazione, caso g, è un atto a parte); (2) alle scadenze successive il testo parla di «rinuncia al rinnovo» senza termini di risposta, che sono nel contratto: verificare la coerenza con il rinnovo tacito deciso dal prodotto (D5); (3) la presa d'atto «ne prende atto» senza riserve; (4) il preavviso della facoltà di recesso, che è quello scritto nel contratto (nei modelli di contratto è uno spazio a mano).
- **Rimandato / debiti**: LR-OPS-1-FU1 salvare la lettera scaricata fra i documenti del contratto (oggi si rigenera ogni volta); FU2 un «promemoria» di invio; FU3 la disdetta dell'inquilino (alla scadenza) come lettera di risposta; FU4 la lettera in inglese. I pezzi 1b (rinnovo e conferma dell'esigenza) e 1c (ISTAT, opzione cedolare, riconsegna, restituzione della cauzione) aggiungono qui le loro sezioni.

## 7. Prove

`LeaseOperationRulesTests` (scadenza della disdetta per tipo, giorno di cessazione), `LeaseWithdrawalTests` (il recesso rispetta le regole di cessazione e non scrive nulla quando le viola), `LeaseLetterTests` (bozze complete e non approvate, nessuna configurazione le approva, filigrana e riga finale, un solo motivo stampato, `[da completare]`, nessun codice fiscale né e-mail nel PDF).
