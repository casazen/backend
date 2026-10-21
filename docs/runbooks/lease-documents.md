# Allegati del contratto

Le relazioni sono distinte: casa → documenti della casa; casa → contratti; contratto → documenti del contratto. Gli allegati caricati dalla pagina partner non sono PropertyDocuments.

La pagina `/app/long-rent/documents/upload?leaseId=<uuid>` usa il contratto salvato e mantiene il riferimento dopo login. I documenti sono consultabili dalla sezione Documenti del contratto, raggiungibile anche aprendo il contratto dalla casa.

## API

- `GET /api/leases/{leaseId}/documents`: elenco metadati; richiede lettura del contratto.
- `POST /api/leases/{leaseId}/documents`: multipart `file` e `kind` (`Contract`, `Attestation`, `Registration`, `Other`); richiede scrittura del contratto (`lease.create`), oltre a lettura. PDF non vuoto fino a 20 MB, estensione e intestazione controllate. Restituisce 201 con documentId e metadati.
- `GET /api/leases/{leaseId}/documents/{documentId}/download`: originale PDF con controllo di appartenenza al contratto e accesso in lettura; cache privata disabilitata. Nessuna chiave storage o URL pubblico nel DTO.

## Persistenza e rilascio

`LeaseDocuments` conserva leaseId, OrgId ereditato dal contratto, tipo, nome, dimensione, data e autore; i byte originali vanno nello storage privato esistente, sotto `orgs/{orgId}/leases/{leaseId}/documents/`. Il salvataggio non modifica firma, dati fiscali o stato di registrazione. Se il salvataggio DB fallisce, il servizio tenta di rimuovere il file appena caricato.

La migrazione `AddLeaseDocuments` è additiva e applicata all'avvio dal meccanismo esistente. La tabella usa il filtro tenant EF e abilita RLS senza policy Data API: il normale backend proprietario della tabella accede attraverso le proprie autorizzazioni, anon/authenticated non leggono direttamente gli allegati. Pubblicare il backend e verificare salute/migrazione prima del frontend. Un ritorno a codice precedente non richiede cancellazione della tabella o dei documenti salvati.

Estrazione automatica dei dati, creazione minima della bozza CF, pagamento/offerta e popolamento delle sezioni da documenti sono lavoro Helix successivo; non sono implementati da questa pagina di conservazione.

## Verifica

Test del servizio: originali conservati, nessun PropertyDocument creato, separazione tra leaseId, PDF non validi e limiti, nessun avanzamento fiscale automatico. Test controller: accesso negato non raggiunge lo storage. Test PostgreSQL/API: upload multipart, elenco, download originale, riapertura del DB e rifiuto di anonimi/altra organizzazione.
