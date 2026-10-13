---
title: Specifica di integrazione Piattaforma – ConcordatoFacile
version: 1.0-bozza
date: 2026-10-13
---

# Specifica di integrazione Piattaforma – ConcordatoFacile

> **Riferimento interno.** Questo documento usa nomi tecnici in italiano ed è superato su questo punto: i nomi validi di endpoint, campi e valori (in inglese) sono quelli di `da-inviare/01-technical-specification.md`. Superata anche la fase 3 (creazione della pratica via API con dati precompilati) e, dalla v1.3 di `da-inviare/`, anche le richieste di integrazione, la bozza e le chiamate di ritorno: l'utente svolge tutta la pratica nel modulo CF (link firmato con `leaseId` al posto di `ref`) e CasaZen riceve solo avvisi di stato (`case_status_changed`) ed esito finale (`case_completed`, `case_rejected`, `case_cancelled`), senza rispondere a CF. La registrazione non si fa con CF, ma con Openapi: il livello `completa` non è usato. Il resto resta come riferimento (fasi, credito, prove, punti da confermare).

**Versione 1.0 (bozza per approvazione)** · 13 ottobre 2026
Documenti collegati: `02-openapi.yaml` (contratto tecnico di endpoint e schemi).

Scopo: definire per iscritto **che cosa si scambiano** la Piattaforma (l'applicazione del partner, di seguito *Piattaforma*) e ConcordatoFacile (di seguito *CF*), **come** e **quando**, così che le due parti possano costruire senza ulteriori rimbalzi. Ogni punto che dipende da una vostra decisione è nella sezione 13: rispondete lì, e questa specifica diventa la v1.0 definitiva.

---

## 1. Attori e termini

| Termine | Significato |
|---|---|
| **Utente** | Il locatore che usa la Piattaforma. |
| **Pratica** | Una richiesta di attestazione (e, nel livello completo, di registrazione) per un contratto. Identificata da `praticaId` (assegnato da CF). |
| **reference** | Identificativo della pratica lato Piattaforma (UUID). CF lo riceve nel link o nella creazione e lo **restituisce in ogni evento**. |
| **livello** | `base` oppure `completa`, come da listino CF (contenuti da confermare, sezione 13, punto 1). |
| **Evento** | Messaggio che CF invia alla Piattaforma (sezione 4 e 5). |

Convenzioni: JSON UTF-8; date e orari in ISO 8601 UTC (`2026-10-13T09:30:00Z`); date senza ora `YYYY-MM-DD`; importi come **interi in centesimi** con `currency` `EUR`; codice del comune = **codice ISTAT a 6 cifre**; nomi dei campi in `camelCase` italiano come in questo documento.

## 2. Fasi di realizzazione

| Fase | Contenuto | Chi costruisce |
|---|---|---|
| **F1 (minima)** | Link al modulo CF con `reference` (sez. 8) + eventi `pratica.creata`, `pratica.stato_aggiornato`, `pratica.attestata`, `pratica.completata`, `pratica.respinta`, `pratica.annullata`, `pratica.fallita` verso la Piattaforma (sez. 4–6). | CF: emissione eventi. Piattaforma: ricezione. |
| **F2** | Scambio nei due sensi: `pratica.informazioni_richieste` e `pratica.bozza_pronta` verso la Piattaforma; **API di ritorno** di CF per risposte, decisione sulla bozza e documenti firmati (sez. 7). | CF: API di ritorno. Piattaforma: schermate e client. |
| **F3** | Creazione della pratica via API con dati già compilati (nessuna riscrittura da parte dell'utente), annullamento, estratto credito (sez. 7). | CF e Piattaforma. |

F1 deve essere utilizzabile da sola. Le fasi successive non cambiano F1.

## 3. Flusso e stati della pratica

Flusso (livello completo): (1) l'utente apre il modulo CF dal link della Piattaforma → (2) CF crea la pratica ed emette `pratica.creata` → (3) CF verifica il canone sull'accordo territoriale → (4) se mancano dati: `pratica.informazioni_richieste`; l'utente risponde dalla Piattaforma → (5) esito: `pratica.respinta` oppure `pratica.attestata` con il documento → (6) `pratica.bozza_pronta` (se prevista) → l'utente approva e le parti firmano; il contratto firmato torna a CF → (7) CF registra → (8) `pratica.completata` con zip finale e ricevute.

| Stato | Significato | Terminale |
|---|---|---|
| `ricevuta` | Pratica creata | no |
| `in_verifica` | Verifica del canone sull'accordo in corso | no |
| `informazioni_richieste` | CF attende dati o documenti dall'utente | no |
| `attestata` | Attestazione rilasciata, documento disponibile | no |
| `bozza_pronta` | Bozza di contratto disponibile per approvazione | no |
| `in_firma` | Bozza approvata, CF attende il contratto firmato | no |
| `in_registrazione` | Registrazione in corso (solo livello completo) | no |
| `completata` | Tutto consegnato | **sì** |
| `respinta` | Attestazione non rilasciabile (es. canone fuori limiti) | **sì** |
| `annullata` | Annullata dall'utente o da CF | **sì** |
| `fallita` | Errore che CF non può risolvere | **sì** |

Transizioni ammesse: `ricevuta → in_verifica ⇄ informazioni_richieste → attestata | respinta`; `attestata → bozza_pronta → in_firma → in_registrazione → completata`; qualsiasi stato non terminale → `annullata | fallita`. Per il livello `base` la pratica termina in `completata` dopo `attestata` (e `bozza_pronta` se inclusa): contenuti da confermare (sez. 13).

## 4. Interfaccia A: eventi da CF alla Piattaforma

**Endpoint della Piattaforma:** `POST {PIATTAFORMA_BASE_URL}/webhooks/concordatofacile` (URL di test e di produzione comunicati separatamente). HTTPS obbligatorio (TLS 1.2 o superiore). Corpo JSON, massimo **256 KB**. **I file non viaggiano nel corpo** (sez. 6).

### 4.1 Intestazioni

| Intestazione | Valore |
|---|---|
| `Content-Type` | `application/json` |
| `X-CF-Event-Id` | UUID dell'evento (uguale a `eventId` nel corpo). Chiave di **idempotenza** |
| `X-CF-Timestamp` | Secondi Unix UTC dell'invio (intero) |
| `X-CF-Key-Id` | Identificativo del segreto usato (per la rotazione) |
| `X-CF-Signature` | `sha256=` seguito da HMAC-SHA256 in esadecimale minuscolo |
| `X-CF-Api-Version` | `1` |

### 4.2 Firma (formulazione esatta)

```
messaggio = X-CF-Timestamp + "." + corpoGrezzo      (corpoGrezzo = i byte esatti inviati, UTF-8)
firma     = HMAC-SHA256(segreto, messaggio)         (esadecimale minuscolo)
X-CF-Signature = "sha256=" + firma
```

La Piattaforma rifiuta (401) un evento se: la firma non coincide (confronto a tempo costante); `|ora_attuale − X-CF-Timestamp| > 300 s`; `X-CF-Key-Id` è sconosciuto. Il segreto è una stringa casuale di almeno 32 byte, **diverso per test e produzione**, consegnato fuori banda. Rotazione: due segreti validi in parallelo per 7 giorni, distinti da `X-CF-Key-Id`.

**Vettore di prova** (segreto `test_secret_0123456789abcdef0123456789abcdef`, `X-CF-Timestamp: 1760347800`). Il corpo è **su una sola riga, senza spazi aggiunti**: copiarlo dal file `.md` e non dal PDF, dove va a capo solo per la stampa:

```
corpo = {"eventId":"7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e","eventType":"pratica.creata","apiVersion":"1","occurredAt":"2026-10-13T09:30:00Z","sequence":1,"praticaId":"CF-TEST-000001","reference":"0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11","data":{"livello":"completa","comuneIstat":"108039"}}
X-CF-Signature = sha256=b08ad161f64df5b840bf22d550ad0866db342649334266a8103276edff6e07bd
```

### 4.3 Risposte della Piattaforma e ritentativi

| Risposta | Quando | CF deve |
|---|---|---|
| `200 {"received":true}` | Accettato, anche se già ricevuto (idempotenza) | considerare consegnato |
| `401 {"code":"invalid_signature"}` / `{"code":"timestamp_out_of_range"}` | Firma o orario non validi | **non ritentare**, correggere |
| `422 {"code":"unknown_reference"}` | `reference` sconosciuto | non ritentare, segnalare |
| `400` | Corpo non valido | non ritentare |
| `408`, `429`, `5xx`, errore di rete | Problema temporaneo | **ritentare** con backoff: 1 min, 5 min, 30 min, 2 h, 6 h, 12 h (poi alert a CF) |

La Piattaforma risponde **entro 3 secondi** e lavora in coda. Un evento ritentato mantiene lo stesso `eventId`, `occurredAt` e `sequence`; cambia solo `X-CF-Timestamp` e la firma.

### 4.4 Ordine

L'ordine di arrivo non è garantito. Ogni evento ha `sequence` (intero crescente per pratica, parte da 1). La Piattaforma applica uno **stato** solo se la sequenza è maggiore dell'ultima applicata; un evento con sequenza minore viene registrato e ignorato.

### 4.5 Busta dell'evento

```json
{
  "eventId": "uuid",
  "eventType": "pratica.completata",
  "apiVersion": "1",
  "occurredAt": "2026-10-13T09:30:00Z",
  "sequence": 7,
  "praticaId": "CF-2026-000123",
  "reference": "uuid della pratica lato Piattaforma",
  "data": { }
}
```

## 5. Eventi e contenuto di `data`

| `eventType` | Fase | `data` (campi) |
|---|---|---|
| `pratica.creata` | F1 | `livello`, `comuneIstat` |
| `pratica.stato_aggiornato` | F1 | `stato` (`in_verifica`, `in_firma`, `in_registrazione`), `dettaglio?` |
| `pratica.informazioni_richieste` | F2 | `richiestaId`, `scadenzaRisposta?`, `domande[]` (`campo`, `etichetta`, `tipo`: `testo`/`numero`/`data`/`scelta`/`file`, `obbligatorio`, `opzioni?`, `aiuto?`) |
| `pratica.attestata` | F1 | `attestazione` (vedi sotto), `files[]` |
| `pratica.bozza_pronta` | F2 | `bozzaId`, `versione`, `files[]`, `azioniPossibili` (`approva`, `richiedi_modifiche`), `note?`, `scadenza?` |
| `pratica.completata` | F1 | `attestazione`, `registrazione?`, `addebito`, `files[]` (inclusi `zip_completo` e ricevute) |
| `pratica.respinta` | F1 | `motivo`, `dettaglio`, `limiti?` (`minimoMensileCent`, `massimoMensileCent`), `addebito` |
| `pratica.annullata` | F1 | `motivo`, `addebito` |
| `pratica.fallita` | F1 | `codice`, `dettaglio`, `addebito` |

**`attestazione`:** `attestazioneId`, `emessaIl` (data), `accordo` {`nome`, `comuneIstat`, `inVigoreDal`, `depositatoIl?`}, `organizzazioni[]` {`nome`, `ruolo` (`proprieta`/`inquilini`), `firmatarioNome`, `firmatoIl`}, `canone` {`zona`, `sottofascia` (1–3), `superficieMq` (decimale con un solo decimale al massimo), `minimoMensileCent`, `massimoMensileCent`, `concordatoMensileCent`}, `documentoFileId`. Le organizzazioni indicate devono essere **firmatarie dell'accordo territoriale** del comune; la Piattaforma lo verifica sul proprio archivio degli accordi.

**`registrazione`** (livello completo): `tipologia` (`L1`/`L2`), `ufficioTerritoriale`, `dataRegistrazione`, `protocolloTelematico`, `codiceIdentificativoContratto`, `cedolareSecca` (booleano), `importoRegistroCent`, `importoBolloCent`, `ricevuteFileId[]`.

**`addebito`:** `livello`, `importoCent`, `ivaCent?`, `currency`, `descrizione`. Nelle pratiche `respinta` senza costo, `importoCent` = 0.

**`motivo` di `pratica.respinta`:** `canone_fuori_limiti`, `dati_incongruenti`, `accordo_non_vigente`, `comune_non_coperto`, `documenti_non_validi`, `altro`.

**Esempio completo (`pratica.completata`, livello completo):**

```json
{
  "eventId": "9d0f6c3a-8e44-4a7b-a1f3-2c5b7e9d1a42",
  "eventType": "pratica.completata",
  "apiVersion": "1",
  "occurredAt": "2026-10-20T15:02:11Z",
  "sequence": 9,
  "praticaId": "CF-TEST-000001",
  "reference": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "data": {
    "attestazione": {
      "attestazioneId": "ATT-2026-0456",
      "emessaIl": "2026-10-15",
      "accordo": { "nome": "Accordo locale Quadro Monza e Brianza", "comuneIstat": "108039", "inVigoreDal": "2024-05-01" },
      "organizzazioni": [
        { "nome": "Organizzazione proprietà (esempio)", "ruolo": "proprieta", "firmatarioNome": "Mario Rossi", "firmatoIl": "2026-10-15" },
        { "nome": "Organizzazione inquilini (esempio)", "ruolo": "inquilini", "firmatarioNome": "Anna Bianchi", "firmatoIl": "2026-10-15" }
      ],
      "canone": { "zona": "1", "sottofascia": 3, "superficieMq": 70.0, "minimoMensileCent": 11667, "massimoMensileCent": 78925, "concordatoMensileCent": 78000 },
      "documentoFileId": "f-001"
    },
    "registrazione": {
      "tipologia": "L2", "ufficioTerritoriale": "Desio", "dataRegistrazione": "2026-10-19",
      "protocolloTelematico": "TEST-PROTO-0001", "codiceIdentificativoContratto": "TEST-CODE-0001",
      "cedolareSecca": true, "importoRegistroCent": 0, "importoBolloCent": 0, "ricevuteFileId": ["f-003", "f-004"]
    },
    "addebito": { "livello": "completa", "importoCent": 9900, "currency": "EUR", "descrizione": "Pratica completa" },
    "files": [
      { "fileId": "f-001", "kind": "attestazione", "fileName": "attestazione.pdf", "mimeType": "application/pdf", "sizeBytes": 412345, "sha256": "…64 caratteri esadecimali…", "downloadUrl": "https://…", "expiresAt": "2026-10-28T15:02:11Z" },
      { "fileId": "f-002", "kind": "zip_completo", "fileName": "pratica-CF-TEST-000001.zip", "mimeType": "application/zip", "sizeBytes": 2345678, "sha256": "…", "downloadUrl": "https://…", "expiresAt": "2026-10-28T15:02:11Z" }
    ]
  }
}
```

(I valori dell'esempio sono fittizi.)

## 6. File

I file **non** sono nel corpo dell'evento: sono elencati in `files[]` e la Piattaforma li scarica.

| Campo | Regola |
|---|---|
| `fileId` | Stringa unica per pratica |
| `kind` | `attestazione`, `scheda_calcolo`, `bozza_contratto`, `contratto_firmato`, `ricevuta_trasmissione`, `ricevuta_registrazione`, `ricevuta_pagamento`, `zip_completo`, `altro` |
| `mimeType` | `application/pdf` o `application/zip` (altri formati da concordare) |
| `sizeBytes`, `sha256` | Dimensione e SHA-256 del file, in esadecimale minuscolo. La Piattaforma **scarta** il file se non coincidono |
| `downloadUrl` | HTTPS, accessibile senza altre credenziali, **valido almeno 7 giorni** (`expiresAt`). Se scade, la Piattaforma lo richiede a CF (sez. 7, `files/{fileId}/link`) |

Limiti: PDF fino a **20 MB**; zip fino a **40 MB** compresso e **120 MB** non compresso; al massimo **20 voci**, nessuno zip annidato, nomi dei file semplici (senza percorsi, caratteri di controllo o `..`), estensione coerente con il contenuto. Un file che supera un limite genera, da parte della Piattaforma, un errore verso CF in fase di scarico (nessun evento di rifiuto: se ne occupa la correzione).

Le ricevute dell'Agenzia delle Entrate vanno consegnate **come ricevute separate in PDF** (oltre che nello zip): la Piattaforma le conserva singolarmente.

## 7. Interfaccia B: API di CF utilizzate dalla Piattaforma (F2 e F3)

Base: `{CF_BASE_URL}/v1`. Autenticazione: `Authorization: Bearer <chiave API>`, chiave diversa per test e produzione. Ogni richiesta che modifica dati porta `Idempotency-Key` (UUID); una ripetizione con la stessa chiave restituisce la stessa risposta. Errori nel formato `{"code": "...", "message": "..."}`. Dettagli degli schemi in `02-openapi.yaml`.

| Fase | Metodo e percorso | Scopo |
|---|---|---|
| F2 | `POST /pratiche/{praticaId}/risposte` | Risposte dell'utente a `informazioni_richieste` (`richiestaId`, `risposte[]`, `fileId` caricati) |
| F2 | `POST /pratiche/{praticaId}/documenti` | Caricamento di un documento (multipart: `kind`, `sha256`, file), es. `contratto_firmato` |
| F2 | `POST /pratiche/{praticaId}/bozza/decisione` | `decisione`: `approvata` o `modifiche`, `bozzaId`, `note?` |
| F2 | `GET /pratiche/{praticaId}/files/{fileId}/link` | Nuovo `downloadUrl` per un file con link scaduto |
| F2 | `GET /pratiche/{praticaId}` | Stato corrente e ultimo `sequence` (verifica e recupero in caso di eventi persi) |
| F3 | `POST /pratiche` | Creazione con dati e calcolo già compilati: risposta con `praticaId` e `moduloUrl` per i dati ancora mancanti |
| F3 | `POST /pratiche/{praticaId}/annullamento` | Annullamento da parte dell'utente |
| F3 | `GET /credito/estratto?da=&a=` | Elenco degli addebiti del periodo e saldo (sez. 10) |

Se CF non può fornire una funzione di F2 entro la data concordata, il ripiego provvisorio è un **link di ritorno al modulo CF** con la stessa `reference`: l'utente completa il passaggio nel modulo e CF emette lo stesso evento di prosecuzione.

## 8. Link al modulo (F1)

`{CF_MODULO_URL}?partner=<codice>&ref=<reference>&livello=<base|completa>&istat=<codice ISTAT a 6 cifre>&return_url=<URL codificato>&theme=<identificativo veste>`

| Parametro | Obbligatorio | Note |
|---|---|---|
| `partner` | sì | Codice assegnato da CF |
| `ref` | sì | `reference`: restituito in ogni evento |
| `livello` | sì | `base` o `completa` |
| `istat` | no | Precompila il comune |
| `return_url` | no | A fine compilazione CF reindirizza qui aggiungendo `?ref=<reference>&praticaId=<id>` (nessun dato personale nell'URL) |
| `theme` | no | Veste personalizzata |

Il link si usa **una sola volta** per `ref`: un secondo ingresso con lo stesso `ref` riapre la stessa pratica, non ne crea un'altra.

## 9. Dati personali

- Minimizzazione: la Piattaforma invia (o l'utente inserisce nel modulo) solo i dati necessari alla verifica, all'attestazione e alla registrazione. Nessun documento d'identità se non richiesto dalla registrazione.
- Ruoli: CF e Piattaforma definiscono per iscritto il ruolo di ciascuno (titolare o responsabile del trattamento) per ogni trattamento; dove CF agisce per conto della Piattaforma, accordo ai sensi dell'art. 28 GDPR (sez. 13, punto 6).
- Conservazione: CF indica per quanto tempo conserva i documenti dopo `completata`; la Piattaforma conserva la propria copia nell'archivio del cliente.
- Nessun dato personale negli URL, nei log condivisi o nei messaggi di errore.

## 10. Credito e addebiti (richiesta della Piattaforma)

Finché la Piattaforma non è costituita e operativa, **non è possibile pagare pratica per pratica in anticipo**. Si chiede:
1. **Credito a consuntivo**: i servizi sono erogati subito; il pagamento avviene **dopo l'erogazione**, con **carta o bonifico**, su un saldo periodico (frequenza da concordare, proposta: mensile).
2. **Estratto** consultabile (area riservata o `GET /credito/estratto`) con, per pratica: `praticaId`, `reference`, livello, data, importo, stato.
3. Nessun costo di installazione né minimo mensile; periodo di avvio a prezzi agevolati come proposto da CF (durata e prezzi da scrivere).
4. Le pratiche `respinta` (canone fuori limiti) e `annullata` prima dell'attestazione: trattamento dell'addebito da confermare (proposta: nessun addebito).
5. Documento di addebito: da concordare, tenuto conto che la Piattaforma è in fase di costituzione.

Listino indicato da CF: base 79 €, completa 99 € (IVA e inclusioni da confermare, sez. 13).

## 11. Tempi attesi

| Passaggio | Tempo atteso (proposta) |
|---|---|
| Risposta della Piattaforma a un evento | entro 3 secondi |
| `in_verifica` → `attestata` / `respinta` | entro 2 giorni lavorativi dal completamento dei dati |
| `informazioni_richieste`: emissione | contestuale alla verifica |
| `completata` dopo `in_firma` (contratto firmato ricevuto) | entro 2 giorni lavorativi (livello completo) |
| Disponibilità del servizio CF | indicare l'impegno (es. 99% mensile) |

## 12. Ambienti e prove

CF fornisce un **ambiente di prova** con comuni e pratiche fittizi; la Piattaforma fornisce l'URL di prova. Si considera completata l'integrazione F1 quando tutti i casi sono superati:

| # | Caso | Atteso |
|---|---|---|
| 1 | Evento valido `pratica.creata` | 200 |
| 2 | Firma errata | 401 `invalid_signature`, nessun ritentativo |
| 3 | Orario oltre 300 s | 401 `timestamp_out_of_range` |
| 4 | Stesso `eventId` due volte | 200 entrambe, un solo effetto |
| 5 | `sequence` minore dell'ultima | 200, stato non modificato |
| 6 | `reference` sconosciuto | 422 `unknown_reference` |
| 7 | `pratica.attestata` con file e `sha256` corretto | file archiviato |
| 8 | `sha256` errato | file scartato, errore registrato |
| 9 | `pratica.respinta` con limiti | motivo e limiti mostrati all'utente |
| 10 | `pratica.completata` con zip e ricevute | archivio completo |
| 11 | Corpo oltre 256 KB | 413 |
| 12 | Risposta 503 simulata dalla Piattaforma | ritentativi come in 4.3 |

## 13. Punti da confermare da parte di CF

Ogni riga ha la proposta della Piattaforma: rispondere "ok" oppure indicare la modifica.

| # | Punto | Proposta della Piattaforma |
|---|---|---|
| 1 | Cosa include la pratica **base** e la **completa** (calcolo, attestazione, bozza contratto, registrazione, imposte e bollo) | Base = verifica del calcolo + attestazione; completa = base + bozza contratto + registrazione. **Da confermare** |
| 2 | Prezzi: IVA inclusa o esclusa; imposte e bollo a carico dell'utente | Prezzi IVA esclusa; imposte e bollo addebitati direttamente all'utente, non nella pratica |
| 3 | **Organizzazioni firmatarie** per i comuni coperti, in particolare per l'accordo di Monza e Brianza (attestazione congiunta, art. 12) | Elenco delle organizzazioni che firmano per accordo; per MB una per la proprietà e una per i conduttori, firmatarie dell'accordo |
| 4 | **Chi registra** all'Agenzia delle Entrate (nome e qualifica) e quale delega serve dall'utente | Delega raccolta nella Piattaforma con testo fornito da CF |
| 5 | Tempi della sez. 11 | Come proposti |
| 6 | Ruoli privacy e accordo ex art. 28 GDPR | CF titolare per l'attestazione; accordo per i servizi svolti per conto della Piattaforma |
| 7 | Date di disponibilità: F1, F2, F3, ambiente di prova | F1 entro 2 settimane dall'approvazione di questa specifica; F2 e F3 da concordare |
| 8 | Validità dei link ai file | Almeno 7 giorni + rinnovo |
| 9 | Credito a consuntivo, frequenza e metodo di saldo, estratto | Sez. 10 |
| 10 | Trattamento dell'addebito per `respinta` e `annullata` | Nessun addebito |
| 11 | Versionamento: modifiche non compatibili solo con nuova versione (`/v2`) e preavviso di 90 giorni | Come proposto |
| 12 | Contatto tecnico e di assistenza (nome, email, orari) e canale per segnalare eventi persi | Un riferimento per parte |
