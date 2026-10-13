---
title: Risposte tecniche alle domande di ConcordatoFacile
version: 1.0-bozza
date: 2026-10-13
---

# Risposte tecniche alle domande di ConcordatoFacile

**Versione 1.0 (bozza)** · 13 ottobre 2026 · Allegato: `02-openapi.yaml` (contratto tecnico degli endpoint, con gli stessi campi di questo documento).

Questo documento risponde, nel dettaglio e solo, alle domande che ci avete posto. *Piattaforma* = la nostra applicazione; *CF* = ConcordatoFacile. Convenzioni: JSON UTF-8; date ISO 8601 UTC (`2026-10-13T09:30:00Z`) o `YYYY-MM-DD`; importi come interi in centesimi (`currency` `EUR`); comune = codice ISTAT a 6 cifre.

| N. | Vostra domanda | Sezione |
|---|---|---|
| 1 | Che cosa vi invierebbe la nostra applicazione per ogni richiesta (dati e calcolo, oppure anche contratto e documenti)? | 1 |
| 2 | Che cosa ci aspettiamo in risposta, e in quali tempi? | 2 |
| 3 | A chi si rivolge la nostra soluzione e in quali Comuni? | 3 |
| 4 | Come vorremmo il link al modulo? | 4 |
| 5 | La forma proposta (a pratica conclusa, invio al nostro endpoint di zip, ricevuta e dati) ci torna? | 5 |
| 6 | Come passano dal nostro endpoint i dati o documenti mancanti e la bozza da approvare e far firmare? | 6 |
| 7 | Come tornano a voi le risposte e i documenti firmati? | 7 |

---

## 1. Che cosa inviamo per ogni richiesta

Per ogni richiesta la nostra applicazione dispone dei dati sotto e li mette a disposizione di CF. **La consegna** avviene in uno di due modi, a vostra scelta: (a) l'utente compila il modulo CF e la nostra applicazione precompila ciò che è possibile tramite i parametri del link (sezione 4); (b) se esponete un endpoint di creazione della pratica, vi inviamo lo schema qui sotto in un'unica chiamata e l'utente non riscrive nulla.

**Dati strutturati**

| Gruppo | Campi |
|---|---|
| Pratica | `reference` (UUID nostro), `livello` (`base` o `completa`) |
| Immobile | `comuneIstat`, `indirizzo`, `cap`, `foglio`, `particella`, `subalterno`, `categoriaCatastale`, `superficieMq` |
| Parti | per ciascuna: `ruolo` (`locatore`/`conduttore`), `nome`, `cognome`, `codiceFiscale`, `quotaPossessoPercento` (locatori) |
| Contratto | `decorrenza`, `durataAnni`, `canoneMensileCent`, `arredato`, `ariaCondizionata`, `elementi` A, B, C, D (codici dell'accordo dichiarati dalle parti) |
| Nostro calcolo | `zona`, `sottofascia`, `minimoMensileCent`, `massimoMensileCent`: serve a confrontarlo con la vostra verifica |

**Documenti**: l'**Allegato 9 precompilato** (PDF) con gli stessi dati, sempre; **su richiesta**, e solo se servono alla verifica, contratto, visura, planimetria e APE (PDF, fino a 20 MB ciascuno).

**Esempio** (valori fittizi):

```json
{
  "reference": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "livello": "completa",
  "immobile": { "comuneIstat": "108040", "indirizzo": "Via Esempio 1", "cap": "20822", "foglio": "12", "particella": "345", "subalterno": "6", "categoriaCatastale": "A/2", "superficieMq": 65.0 },
  "parti": [
    { "ruolo": "locatore", "nome": "Mario", "cognome": "Rossi", "codiceFiscale": "RSSMRA80A01H501U", "quotaPossessoPercento": 100 },
    { "ruolo": "conduttore", "nome": "Anna", "cognome": "Bianchi", "codiceFiscale": "BNCNNA85C41F205Z" }
  ],
  "contratto": { "decorrenza": "2026-11-01", "durataAnni": 3, "canoneMensileCent": 40000, "arredato": false, "ariaCondizionata": false,
                 "elementi": { "A": ["A1", "A2"], "B": ["B2", "B3", "B4", "B5"], "C": ["C4", "C6"], "D": ["D1", "D5"] } },
  "calcoloPiattaforma": { "zona": "1", "sottofascia": 2, "minimoMensileCent": 10833, "massimoMensileCent": 46042 }
}
```

## 2. Che cosa ci aspettiamo in risposta e in quali tempi

Una serie di **eventi** inviati al nostro endpoint (sezione 5), con questi contenuti:

| Evento | Quando | Contenuto |
|---|---|---|
| `pratica.creata` | Pratica aperta | `livello`, `comuneIstat` |
| `pratica.stato_aggiornato` | Cambio di fase | `stato`: `in_verifica`, `attestata`, `in_firma`, `in_registrazione` |
| `pratica.informazioni_richieste` | Manca un dato o un documento | domande (sezione 6) |
| `pratica.bozza_pronta` | Bozza da approvare e firmare | file e azioni possibili (sezione 6) |
| `pratica.completata` | Tutto consegnato | attestazione, registrazione se prevista, file (sezione 5.4) |
| `pratica.respinta` | Attestazione non rilasciabile | `motivo`, `dettaglio`, `limiti` (minimo e massimo mensili dell'accordo) |
| `pratica.annullata` | Pratica chiusa senza esito | `motivo` |

**Tempi.** Ci basiamo sui tempi dichiarati sul vostro sito: **24–48 ore dalla ricezione dei documenti firmati completi** per la pratica completa. In dettaglio: verifica del canone e `attestata` o `respinta` entro **2 giorni lavorativi** dal momento in cui i dati sono completi; `completata` entro **2 giorni lavorativi** dalla ricezione del contratto firmato. Una richiesta di informazioni parte non appena la verifica trova la lacuna, non alla fine.

## 3. A chi ci rivolgiamo e in quali Comuni

**A chi:** proprietari privati con una o poche unità abitative in affitto di lungo periodo. **Dove:** partiamo dalla **provincia di Monza e Brianza** (comuni dell'accordo territoriale quadro) e ci estendiamo poi ad altri territori. Per ogni comune che ci occorre, ci serve sapere se lo coprite: se un comune non è coperto, rispondete con `pratica.respinta` e `motivo` `comune_non_coperto` (o `pratica.annullata`) senza ritardo, e non attendete l'utente.

## 4. Il link al modulo

Il modulo resta sul vostro dominio con la nostra veste. Il link:

`{CF_MODULO_URL}?partner=<codice>&ref=<reference>&livello=<base|completa>&istat=<codice ISTAT>&return_url=<URL codificato>&theme=<veste>`

| Parametro | Obbl. | Note |
|---|---|---|
| `partner` | sì | Codice che ci assegnate |
| `ref` | sì | Il nostro `reference` (UUID). **Lo restituite in ogni evento** |
| `livello` | sì | `base` o `completa` |
| `istat` | no | Precompila il comune |
| `return_url` | no | A fine compilazione reindirizzate qui aggiungendo `?ref=<reference>&praticaId=<id>` (nessun dato personale nell'URL) |
| `theme` | no | Nome, logo e colori della nostra veste |

Un secondo ingresso con lo stesso `ref` riapre la stessa pratica: non ne crea un'altra.

## 5. La forma proposta: invio al nostro endpoint

**Sì, ci torna**, con le precisazioni seguenti.

### 5.1 Endpoint e intestazioni

`POST {PIATTAFORMA_BASE_URL}/webhooks/concordatofacile` (URL di prova e di produzione comunicati separatamente; HTTPS, TLS 1.2 o superiore). Corpo JSON, al massimo **256 KB**. **I file non viaggiano nel corpo** (sezione 5.5).

| Intestazione | Valore |
|---|---|
| `Content-Type` | `application/json` |
| `X-CF-Event-Id` | UUID dell'evento (uguale a `eventId` nel corpo): chiave di idempotenza |
| `X-CF-Timestamp` | Secondi Unix UTC dell'invio |
| `X-CF-Key-Id` | Identificativo del segreto usato |
| `X-CF-Signature` | `sha256=` seguito da HMAC-SHA256 in esadecimale minuscolo |
| `X-CF-Api-Version` | `1` |

### 5.2 Firma

```
messaggio = X-CF-Timestamp + "." + corpoGrezzo      (i byte esatti inviati, UTF-8)
firma     = HMAC-SHA256(segreto, messaggio)         (esadecimale minuscolo)
X-CF-Signature = "sha256=" + firma
```

Rifiutiamo (401) se la firma non coincide, se `|ora − X-CF-Timestamp| > 300 s` o se `X-CF-Key-Id` è sconosciuto. Il segreto è casuale, di almeno 32 byte, **diverso per prova e produzione**, consegnato fuori banda; alla rotazione restano validi due segreti per 7 giorni.

**Vettore di prova** (segreto `test_secret_0123456789abcdef0123456789abcdef`, `X-CF-Timestamp: 1760347800`). Il corpo è **su una sola riga, senza spazi aggiunti**: copiarlo dal file `.md`, non dal PDF, dove va a capo solo per la stampa:

```
corpo = {"eventId":"7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e","eventType":"pratica.creata","apiVersion":"1","occurredAt":"2026-10-13T09:30:00Z","sequence":1,"praticaId":"CF-TEST-000001","reference":"0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11","data":{"livello":"completa","comuneIstat":"108039"}}
X-CF-Signature = sha256=b08ad161f64df5b840bf22d550ad0866db342649334266a8103276edff6e07bd
```

### 5.3 Risposte e ritentativi

Rispondiamo **entro 3 secondi** e lavoriamo in coda.

| Risposta | Significato | Voi |
|---|---|---|
| `200 {"received":true}` | Accettato (anche se già ricevuto) | consegnato |
| `401` (`invalid_signature`, `timestamp_out_of_range`) | Firma o orario non validi | non ritentare |
| `422` (`unknown_reference`) | `reference` sconosciuto | non ritentare |
| `400`, `413` | Corpo non valido o troppo grande | non ritentare |
| `408`, `429`, `5xx`, errore di rete | Problema temporaneo | ritentare: 1 min, 5 min, 30 min, 2 h, 6 h, 12 h |

Un evento ritentato mantiene `eventId`, `occurredAt` e `sequence`; cambiano solo `X-CF-Timestamp` e firma. L'ordine di arrivo non è garantito: `sequence` è un intero crescente per pratica (da 1) e applichiamo lo stato solo se è maggiore dell'ultimo applicato.

Busta di ogni evento:

```json
{ "eventId": "uuid", "eventType": "pratica.completata", "apiVersion": "1", "occurredAt": "2026-10-20T15:02:11Z",
  "sequence": 9, "praticaId": "CF-2026-000123", "reference": "il nostro UUID", "data": { } }
```

### 5.4 `pratica.completata`

`data` contiene:

- **`attestazione`**: `attestazioneId`, `emessaIl`, `accordo` (`nome`, `comuneIstat`, `inVigoreDal`), `organizzazioni[]` (`nome`, `ruolo` `proprieta`/`inquilini`, `firmatarioNome`, `firmatoIl`), `canone` (`zona`, `sottofascia`, `superficieMq`, `minimoMensileCent`, `massimoMensileCent`, `concordatoMensileCent`), `documentoFileId`. Le organizzazioni devono essere **firmatarie dell'accordo territoriale** del comune: lo verifichiamo sul nostro archivio.
- **`registrazione`** (se prevista): `tipologia` (`L1`/`L2`), `ufficioTerritoriale`, `dataRegistrazione`, `protocolloTelematico`, `codiceIdentificativoContratto`, `cedolareSecca`, `importoRegistroCent`, `importoBolloCent`, `ricevuteFileId[]`.
- **`files[]`**: l'attestazione timbrata, le ricevute (ciascuna come PDF separato) e lo zip completo.

**`pratica.respinta`**: `motivo` (`canone_fuori_limiti`, `dati_incongruenti`, `accordo_non_vigente`, `comune_non_coperto`, `documenti_non_validi`, `altro`), `dettaglio`, `limiti` (`minimoMensileCent`, `massimoMensileCent`).

### 5.5 File

I file sono in `files[]` e li scarichiamo noi.

| Campo | Regola |
|---|---|
| `fileId`, `kind` | `kind`: `attestazione`, `scheda_calcolo`, `bozza_contratto`, `contratto_firmato`, `ricevuta_trasmissione`, `ricevuta_registrazione`, `ricevuta_pagamento`, `zip_completo`, `altro` |
| `fileName`, `mimeType` | `application/pdf` o `application/zip` |
| `sizeBytes`, `sha256` | Dimensione e SHA-256 esadecimale minuscolo: scartiamo il file se non coincidono |
| `downloadUrl`, `expiresAt` | HTTPS, accessibile senza altre credenziali, **valido almeno 7 giorni** |

Limiti: PDF fino a 20 MB; zip fino a 40 MB compresso e 120 MB non compresso, al massimo 20 voci, nessuno zip annidato, nomi dei file senza percorsi né `..`.

## 6. Dati o documenti mancanti e bozza da approvare e firmare

Passano dal nostro endpoint (sezione 5) come due eventi, e l'utente li gestisce nella nostra applicazione.

**`pratica.informazioni_richieste`** — `data`: `richiestaId`, `scadenzaRisposta` (opzionale), `domande[]` con `campo`, `etichetta`, `tipo` (`testo`, `numero`, `data`, `scelta`, `file`), `obbligatorio`, `opzioni` (per `scelta`), `aiuto` (opzionale).

**`pratica.bozza_pronta`** — `data`: `bozzaId`, `versione`, `files[]` (la bozza in PDF), `azioniPossibili` (`approva`, `richiedi_modifiche`), `note` e `scadenza` (opzionali). Se chiedete modifiche, emettete una nuova bozza con `versione` maggiore.

## 7. Come tornano a voi risposte e documenti firmati

Proponiamo **due chiamate che dovreste esporre**, con la stessa autenticazione e lo stesso formato. Base: `{CF_BASE_URL}/v1`, `Authorization: Bearer <chiave API>` (una per prova e una per produzione), `Idempotency-Key` (UUID) su ogni richiesta: una ripetizione con la stessa chiave restituisce la stessa risposta. Errori: `{"code": "...", "message": "..."}`.

| Chiamata | Scopo |
|---|---|
| `POST /pratiche/{praticaId}/risposte` | Risposte a `informazioni_richieste` (`richiestaId`, `risposte[]` con `campo` e `valore` o `fileId`) **oppure** decisione sulla bozza (`bozzaId`, `decisione` `approvata`/`modifiche`, `note`) |
| `POST /pratiche/{praticaId}/documenti` | Caricamento di un file in `multipart/form-data`: `kind` (`contratto_firmato`, `documento_richiesto`, `altro`), `richiestaId` (se risponde a una richiesta), `sha256`, `file` (PDF fino a 20 MB). Risponde `201` con `fileId` |

Il contratto firmato lo carichiamo per primo con `documenti`, e con `fileId` rispondiamo poi a eventuali domande.

**Finché queste due chiamate non esistono**, il ripiego è il **link di ritorno al modulo**: l'utente completa il passaggio nel modulo CF con lo stesso `ref` e voi emettete lo stesso evento di prosecuzione.
