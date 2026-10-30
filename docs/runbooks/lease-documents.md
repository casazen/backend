# Allegati del contratto

Le relazioni sono distinte: casa → documenti della casa; casa → contratti; contratto → documenti del contratto. Gli allegati caricati dalla pagina partner non sono PropertyDocuments.

La pagina `/app/long-rent/documents/upload?leaseId=<uuid>` usa il contratto salvato e mantiene il riferimento dopo login; con `?practice=P-…` (il pulsante di ConcordatoFacile) il contratto lo ricava il server dalla pratica, o lo sceglie l'utente. I documenti sono consultabili dalla sezione Documenti del contratto, raggiungibile anche aprendo il contratto dalla casa.

## API

- `GET /api/leases/{leaseId}/documents`: elenco metadati; richiede lettura del contratto.
- `POST /api/leases/{leaseId}/documents`: multipart `file` e `kind` (`Contract`, `Attestation`, `Registration`, `Other`); richiede scrittura del contratto (`lease.create`), oltre a lettura. PDF non vuoto fino a 20 MB, estensione e intestazione controllate. Restituisce 201 con documentId e metadati.
- `GET /api/leases/{leaseId}/documents/{documentId}/download`: originale PDF con controllo di appartenenza al contratto e accesso in lettura; cache privata disabilitata. Nessuna chiave storage o URL pubblico nel DTO.

## Persistenza e rilascio

`LeaseDocuments` conserva leaseId, OrgId ereditato dal contratto, tipo, nome, dimensione, data e autore; i byte originali vanno nello storage privato esistente, sotto `orgs/{orgId}/leases/{leaseId}/documents/`. Il salvataggio non modifica firma, dati fiscali o stato di registrazione. Se il salvataggio DB fallisce, il servizio tenta di rimuovere il file appena caricato.

La migrazione `AddLeaseDocuments` è additiva e applicata all'avvio dal meccanismo esistente. La tabella usa il filtro tenant EF e abilita RLS senza policy Data API: il normale backend proprietario della tabella accede attraverso le proprie autorizzazioni, anon/authenticated non leggono direttamente gli allegati. Pubblicare il backend e verificare salute/migrazione prima del frontend. Un ritorno a codice precedente non richiede cancellazione della tabella o dei documenti salvati.

La lettura del contratto caricato e il riempimento della bozza «importato» (NL-DOCS) sono in [`lease-document-extraction.md`](lease-document-extraction.md); la bozza minima del concordato, la pagina del servizio con pagamento e omaggio, il link firmato, lo stato della pratica e il ritorno da ConcordatoFacile con `?practice=` sono descritti in `docs/integrations/concordatofacile/README.md`. Una singola foto JPEG/PNG inviata a questo endpoint diventa un PDF di una pagina (come per i documenti della casa).

**Contratto con attestazione e scheda in un solo file (CF-DOCS-REAL).** Il servizio guidato consegna il contratto firmato, l'attestazione di rispondenza e la scheda di calcolo del canone in UN documento scansionato: si carica come `Contract`. La lettura ne riconosce le sezioni (§4 di `lease-document-extraction.md`) e `GET /api/leases/{leaseId}/documents` dà, per ogni documento `Contract` letto, `sections` (`{ kind: contract|attestation|calculationSheet, firstPage, lastPage }`), `containsAttestation` e `containsCalculationSheet`: se il file contiene l'attestazione la persona non è invitata a caricarla di nuovo (resta il documento di tipo `Attestation` per chi la riceve a parte). Gli avvisi di coerenza stanno in `GET /api/leases/{id}/draft/reading` (`documents[].warnings`, codici stabili).

**La ricevuta dell'Agenzia delle Entrate** (documento di tipo `Registration`, PDF digitale, mod. RLI12) si carica a parte e si legge senza OCR: `GET /api/leases/{leaseId}/documents/{documentId}/reading` (permesso di lettura del contratto, `no-store`) ne dà esito (registrato o scartato), codice identificativo, numero e serie, data di registrazione, ufficio, canone, durata, data di stipula, tipologia, cedolare secca, comune e provincia, e i codici di ciò che non coincide con il contratto (`receipt_*`); un documento che non è una ricevuta di QUESTO contratto è 404 `document_not_found` e il file non viene aperto. La lettura non dichiara nulla: la dichiarazione della registrazione resta quella di `POST /api/leases/{leaseId}/registration/manual`, con numero e data scritti o confermati dalla persona.

La ricevuta di registrazione caricata qui come documento di tipo `Registration` serve a dichiarare la registrazione senza ricaricare il file (CF-END): `POST /api/leases/{leaseId}/registration/manual` accetta `receiptDocumentId` al posto di `receipt`, solo per un documento `Registration` di questo contratto e della sua organizzazione (ogni altro è 404 `document_not_found`, senza leggere nulla). Il documento resta nella sezione Documenti con il suo file e la registrazione ne tiene uno suo (due chiavi, un proprietario ciascuna): vedi [`long-rent-lease-import.md`](long-rent-lease-import.md) § 12.

## Verifica

Test del servizio: originali conservati, nessun PropertyDocument creato, separazione tra leaseId, PDF non validi e limiti, nessun avanzamento fiscale automatico. Test controller: accesso negato non raggiunge lo storage. Test PostgreSQL/API: upload multipart, elenco, download originale, riapertura del DB e rifiuto di anonimi/altra organizzazione.


Le selezioni fotografiche possono essere riunite in un solo PDF prima dell’invio: vedere [Documenti multipagina e OCR APE](documenti-multipagina.md). La funzione è condivisa con i documenti della casa e non cambia la titolarità degli allegati.
