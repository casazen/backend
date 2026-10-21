# Documenti multipagina e OCR APE

Funzione generale di gestione documentale, indipendente dall’integrazione ConcordatoFacile.

## Caricamento e conservazione

Nei documenti della casa e del contratto si può selezionare un PDF esistente oppure fino a 12 foto JPEG/PNG dello stesso documento. Le foto possono essere aggiunte in più selezioni; anteprima, rotazione a passi di 90°, ordine e rimozione sono disponibili prima dell’invio. Ogni modifica annulla la conferma «Ho caricato tutte le pagine». PDF e foto non sono mescolati: per sostituire la selezione rimuovere i file precedenti. La conferma dell’utente non dimostra automaticamente la completezza legale del documento.

Le foto vengono decodificate nel browser rispettando l’orientamento EXIF, ridimensionate al massimo a 2400 pixel sul lato maggiore e riunite in ordine in un PDF con immagini JPEG di qualità 90%. La conversione è caricata dinamicamente. Non avviene un caricamento delle singole fotografie: al server arriva un solo PDF. Le foto restano solo in memoria nel browser e le URL di anteprima sono revocate alla rimozione/chiusura. Dopo il salvataggio non sono recuperabili separatamente.

Limiti: 20 MB per foto, 40 MB complessivi di input; PDF finale massimo 10 MB per documenti casa, 20 MB per allegati del contratto. Se la conversione supera il limite, non viene salvato un documento parziale. Un PDF già esistente viene inviato senza ricompressione, per preservare anche eventuali firme digitali. I client precedenti che inviano una singola immagine APE all’API documenti casa ottengono comunque un PDF, normalizzato sul server prima della validazione e prima dello storage. Gli altri caricamenti delle API preesistenti conservano il loro formato; il nuovo componente multipagina invia sempre il solo PDF composto.

Lo storage privato contiene soltanto il PDF risultante, con un solo record documentale. Il download autenticato restituisce quel PDF. Restano distinti casa → documenti casa (APE), casa → contratti e contratto → documenti contratto. Gli altri formati singoli già supportati dai documenti casa rimangono nel flusso preesistente.

## Controlli APE

Si applicano prima di qualsiasi scrittura nello storage, tanto ai PDF digitali quanto alle scansioni/foto riunite. Si estrae il testo incorporato con Poppler; se non basta a verificare i dati, tutte le pagine vengono renderizzate e lette da Tesseract in italiano e inglese. Le pagine non vengono validate come attestati separati: i campi sono cercati nel testo complessivo.

Si controllano riconoscibilità del titolo APE, codice identificativo non vuoto con cifra, classe energetica, valore EPgl, indirizzo, nome del certificatore, data di emissione leggibile/non futura e scadenza leggibile/successiva all’emissione e non trascorsa. Dati mancanti o illeggibili sono elencati nella risposta localizzata `APE_INVALID_CONTENT`; l’utente può aggiungere le pagine mancanti e riprovare mantenendo la selezione. Una sola prima pagina senza codice, certificatore o date non passa. Date impossibili e APE scaduti non passano.

È un controllo automatico di riconoscimento, completezza minima e date del testo letto, non una verifica di autenticità, della firma del tecnico o del registro regionale, né della manutenzione impianti o corrispondenza dell’indirizzo alla casa. I modelli con diciture non riconosciute possono richiedere una scansione migliore o un adeguamento del parser; non sono previsti bypass che accettino una foto senza leggerla. Non viene introdotta qui l’estrazione dei dati dei contratti: quella rimane nel lavoro Helix.

## Runtime e sicurezza operativa

Il Dockerfile installa `poppler-utils`, `tesseract-ocr`, lingue `ita`/`eng` e `util-linux`. Servono anche nell’esecuzione locale dei controlli OCR; il workflow CI li installa per i test reali. Nessun provider esterno riceve i documenti e non servono nuove chiavi API.

Cartelle temporanee casuali locali, eliminate in `finally` dopo esito positivo o negativo. Nessun originale fotografico viene persistito nello storage. Massimo 12 pagine APE, 10 MB in ingresso, rendering a 2400 pixel, due elaborazioni concorrenti per processo API, scadenza complessiva di 90 secondi comprensiva della coda. I sottoprocessi usano argomenti separati senza shell e limiti `prlimit` di 768 MiB e 60 secondi CPU. Il client attende fino a 120 secondi. L’assenza dei tool/lingue o il timeout restituiscono `503 APE_OCR_UNAVAILABLE`, senza salvare il file: non sono classificati come APE contraffatto.

Pubblicare prima il backend con le dipendenze native e poi il frontend. Nessuna migrazione aggiuntiva richiesta per l’assemblaggio/OCR; la migrazione LeaseDocuments appartiene alla distinta funzione allegati del contratto.

## Verifica

Test browser: aggiunta foto, riordino, rotazione, conferma obbligatoria, PDF con due pagine nell’orientamento previsto, nessuna foto nel multipart, elenco dopo reload e download degli stessi byte PDF. Test API/PostgreSQL: una foto completa produce solo un PDF scaricabile. Test OCR reali su facsimili sintetici: prima pagina incompleta respinta con campi mancanti, due pagine con dati distribuiti accettate, foto completa accettata. Test del parser per date impossibili/scadute, titolo non APE e campi mancanti.
