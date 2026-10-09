# Contratto di locazione ad uso abitativo per studenti universitari

> **Bozza non approvata: nessuna pipeline di prodotto.** Bozza redatta da un agente AI su incarico del PO (versione 0.9 · bozza del 8 ottobre 2026; documento `contratto-studenti` di `redesign/assets/legal-contracts.js`) e salvata qui solo come riferimento per il PO e per il legale. Richiede la revisione di un legale prima di qualunque uso: non è un modello del prodotto, non si approva e non si attiva.

- Tipo di contratto nel prodotto: nessuno (non modellato).
- Base normativa dichiarata dalla bozza: L. 9 dicembre 1998, n. 431, art. 5, commi 2 e 3; D.M. 16 gennaio 2017, Allegato C.
- Destinatari: Locatore e studente universitario (conduttore).
- Versione della bozza: 0.9 · bozza del 8 ottobre 2026.

## Perché non c’è un modello nel prodotto

Il contratto per studenti universitari non è modellato nel prodotto: non esiste un tipo di contratto «Studenti» (i tipi sono Libero, Concordato e Transitorio), quindi non ci sono regole di durata, struttura delle sezioni o modello. I modelli dei tre regimi non vanno usati per uno studente.

Per dare una pipeline a questo tipo servono, in un task a parte: il tipo di contratto con le sue regole di durata (da 6 a 36 mesi secondo la bozza), la struttura delle sezioni obbligatorie, il modello, i test e il parere del legale. Fino ad allora il testo resta in questa cartella.

## In sintesi (come mostrato all’utente nella demo)

- È riservato a chi è iscritto a un corso universitario o post-laurea in un comune diverso da quello di residenza.
- Dura da 6 a 36 mesi; alla prima scadenza si rinnova da solo per lo stesso periodo, se lo studente non dà disdetta da 1 a 3 mesi prima.
- Il canone deve stare nelle fasce dell’accordo territoriale; con l’attestazione di un’organizzazione firmataria si accede alle agevolazioni fiscali.
- Più studenti possono firmare lo stesso contratto: in quel caso rispondono insieme dell’intero canone e delle spese.
- Lo studente può recedere per gravi motivi con un preavviso da 1 a 3 mesi.
- Va registrato entro 30 giorni; il locatore può scegliere la cedolare secca.

## Punti che il legale deve verificare per primi

1. **Non è il contratto tipo ufficiale.** Il testo è una riformulazione del tipo dell’Allegato C del D.M. 16 gennaio 2017 e non lo riproduce: le differenze possono mettere a rischio l’attestazione di rispondenza e i benefici fiscali. Il confronto con il tipo ufficiale va fatto per primo su durata e rinnovo (art. 4) e recesso (art. 5).
2. **Requisiti dello studente.** Iscrizione a un corso universitario o post-laurea in un comune diverso da quello di residenza (art. 3), documentata con certificato o dichiarazione sostitutiva; al rinnovo la bozza chiede la documentazione aggiornata: verificare i requisiti e i documenti.
3. **Durata e rinnovo.** Da 6 a 36 mesi (art. 3 del D.M., fonte non ufficiale nel repository); rinnovo automatico per un uguale periodo salvo disdetta del conduttore da uno a tre mesi prima; alla fine del rinnovo il contratto cessa. Da verificare sull’art. 5, commi 2 e 3, della L. 431/1998.
4. **Recesso.** La bozza lo consente per gravi motivi con preavviso da uno a tre mesi; l’accordo di Monza e Brianza (pagina 11 del testo pubblicato) prevede il recesso del conduttore con due mesi di preavviso se interrompe gli studi: decidere quale formula usare.
5. **Più studenti sullo stesso contratto.** L’art. 7 è una clausola facoltativa (responsabilità solidale, recesso del singolo, subentro) con un testo alternativo per un conduttore solo: il legale decide se e come tenerla.
6. **Fisco.** Aliquota della cedolare secca per i contratti per studenti (la bozza non scrive aliquote; da confermare con il commercialista); rinuncia agli aggiornamenti con la cedolare secca e suo effetto dopo la revoca, come per gli altri contratti; attestazione di rispondenza: la bozza presume un contratto non assistito.
7. **Cessione di fabbricato e clausole da approvare per iscritto.** Come nei tre modelli per la cessione di fabbricato (art. 12 del D.L. 59/1978; art. 7 del D.Lgs. 286/1998 per i cittadini extra UE). Elenco delle clausole da approvare: durata e rinnovo, recesso, pluralità di conduttori, deposito, uso, manutenzione e innovazioni, accesso (artt. 1341-1342 c.c.).
8. **Dati che il prodotto non ha** (se un giorno nascerà la pipeline): anno accademico, corso di studi, università, durata, estremi dell’accordo, fascia di canone, giorno di pagamento, IBAN, acconto spese, regime fiscale, organizzazione che attesta, email, residenza, cittadinanza, piano, composizione, superficie, arredo, luogo e data.

Elenco dei punti comuni a tutti i modelli: sezione «Bozze 2026-11» di `docs/runbooks/lease-contract-templates.md`.

## Variabili ed esempi

Nel testo le variabili sono scritte `{{chiave}}`. La tabella dà l’etichetta e l’esempio della bozza (dati demo) e il segnaposto del prodotto che oggi coprirebbe la variabile; dove il prodotto non ha il dato la colonna dice che è un campo da completare a mano.

| Variabile | Descrizione | Esempio | Segnaposto del prodotto |
|---|---|---|---|
| `locatore_nome` | Nome e cognome del locatore | Giulia Rinaldi | `locatori` (nome e cognome, con il codice fiscale) |
| `locatore_cf` | Codice fiscale del locatore | RNLGLI84C54E506N | `locatori` (nome e cognome, con il codice fiscale) |
| `locatore_residenza` | Residenza del locatore (comune e indirizzo) | Lecce (LE), Viale Gallipoli 28 | nessuno: campo da completare a mano |
| `conduttore_nome` | Nome e cognome del conduttore | Giada Ruggiero | `conduttori` (nome e cognome, con il codice fiscale) |
| `conduttore_cf` | Codice fiscale del conduttore | RGGGDI03H58F839N | `conduttori` (nome e cognome, con il codice fiscale) |
| `conduttore_cittadinanza` | Cittadinanza del conduttore | italiana | nessuno: campo da completare a mano |
| `conduttore_residenza` | Residenza attuale del conduttore (comune e indirizzo) | Napoli (NA), Via Toledo 256 | nessuno: campo da completare a mano |
| `accordo_comune` | Comune dell’accordo territoriale | Bari | `immobile_comune` (solo il Comune dell’immobile; i dati dell’accordo non ci sono) |
| `accordo_data_deposito` | Data di deposito dell’accordo territoriale | 12 marzo 2024 | nessuno: campo da completare a mano |
| `immobile_comune` | Comune dell’immobile | Bari | `immobile_comune` |
| `immobile_indirizzo` | Indirizzo dell’immobile (via e numero civico) | Via Orabona 4 | `immobile_indirizzo` (il valore del prodotto comprende CAP e comune) |
| `immobile_piano` | Piano | primo | nessuno: campo da completare a mano |
| `immobile_vani` | Composizione dell’immobile | un vano con angolo cottura e bagno | nessuno: campo da completare a mano |
| `immobile_superficie` | Superficie in metri quadrati | 34 | nessuno: campo da completare a mano |
| `catasto_foglio` | Foglio catastale | 40 | `dati_catastali` |
| `catasto_particella` | Particella catastale | 210 | `dati_catastali` |
| `catasto_subalterno` | Subalterno catastale | 31 | `dati_catastali` |
| `catasto_categoria` | Categoria catastale | A/2 | `dati_catastali` |
| `catasto_rendita` | Rendita catastale in euro | 387,34 | `dati_catastali` |
| `immobile_arredo` | Arredamento (arredato o non arredato) | arredato, come da inventario allegato | nessuno: campo da completare a mano |
| `ape_codice` | Codice identificativo dell’APE | 072006-2024-0012096 | `ape_estremi` |
| `ape_classe` | Classe energetica (APE) | E | `ape_estremi` |
| `anno_accademico` | Anno accademico | 2026/2027 | nessuno: campo da completare a mano |
| `corso_di_studi` | Corso di studi | Laurea magistrale in Informatica | nessuno: campo da completare a mano |
| `universita` | Università | Università degli Studi di Bari Aldo Moro | nessuno: campo da completare a mano |
| `durata_mesi` | Durata in mesi | 12 | `durata` (calcolata dalle date: «10 mesi», «1 anno») |
| `data_inizio` | Data di inizio della locazione | 1° novembre 2026 | `data_decorrenza` |
| `data_fine` | Data di scadenza | 31 ottobre 2027 | `data_scadenza` |
| `fascia_min` | Canone minimo della fascia dell’accordo (euro al mese) | 360,00 | nessuno: campo da completare a mano |
| `fascia_max` | Canone massimo della fascia dell’accordo (euro al mese) | 510,00 | nessuno: campo da completare a mano |
| `canone_mensile` | Canone mensile in euro | 480,00 | `canone_mensile` (senza valuta; c’è anche `canone_annuo`) |
| `canone_lettere` | Canone mensile in lettere | quattrocentottanta/00 | nessuno: campo da completare a mano |
| `giorno_pagamento` | Giorno del mese entro cui pagare il canone | 5 | nessuno: campo da completare a mano |
| `iban_locatore` | IBAN del locatore | IT60 X054 2811 1010 0000 0123 456 | nessuno: campo da completare a mano |
| `deposito_importo` | Deposito cauzionale in euro (massimo tre mensilità) | 960,00 | `deposito_cauzionale` (senza valuta) |
| `spese_mensili` | Acconto mensile sugli oneri accessori in euro | 30,00 | nessuno: campo da completare a mano |
| `regime_fiscale` | Regime fiscale scelto dal locatore | cedolare secca con aliquota del 10% | nessuno: campo da completare a mano |
| `attestazione_ente` | Organizzazione firmataria che rilascia l’attestazione | Associazione della Proprietà Edilizia di Bari | nessuno: campo da completare a mano |
| `locatore_email` | Email del locatore | giulia@zenstays.it | nessuno: campo da completare a mano |
| `conduttore_email` | Email del conduttore | giada.ruggiero@example.com | nessuno: campo da completare a mano |
| `luogo_firma` | Luogo della firma | Bari | nessuno: campo da completare a mano |
| `data_firma` | Data della firma | 15 ottobre 2026 | nessuno: campo da completare a mano |

## Testo

Testo semplice, con le variabili tra doppie parentesi graffe. Gli articoli sono numerati come nella bozza.

### Parti

Locatore: {{locatore_nome}}, codice fiscale {{locatore_cf}}, residente in {{locatore_residenza}}.

Conduttore: {{conduttore_nome}}, codice fiscale {{conduttore_cf}}, di cittadinanza {{conduttore_cittadinanza}}, residente in {{conduttore_residenza}}.

### Premesse

Le parti intendono stipulare un contratto di locazione per studenti universitari ai sensi dell’art. 5, commi 2 e 3, della L. 9 dicembre 1998, n. 431, secondo il tipo di contratto di cui all’Allegato C del D.M. 16 gennaio 2017.

Il canone e le altre condizioni del contratto sono definiti sulla base dell’accordo territoriale per il Comune di {{accordo_comune}}, depositato il {{accordo_data_deposito}} (di seguito «accordo territoriale»), che le parti dichiarano di conoscere.

Tutto ciò premesso, le parti convengono e stipulano quanto segue; le premesse e gli allegati fanno parte integrante del contratto.

### Art. 1 · Oggetto della locazione

Il locatore concede in locazione al conduttore, che accetta, l’unità immobiliare ad uso abitativo sita nel Comune di {{immobile_comune}}, {{immobile_indirizzo}}, piano {{immobile_piano}}, composta da {{immobile_vani}}, della superficie di circa {{immobile_superficie}} metri quadrati.

L’unità immobiliare è censita al Catasto Fabbricati del Comune di {{immobile_comune}} al foglio {{catasto_foglio}}, particella {{catasto_particella}}, subalterno {{catasto_subalterno}}, categoria {{catasto_categoria}}, rendita catastale € {{catasto_rendita}}.

L’immobile è locato {{immobile_arredo}}, con le pertinenze e le dotazioni indicate nel verbale di consegna.

Il locatore dichiara di essere proprietario dell’immobile o di averne comunque la piena disponibilità.

### Art. 2 · Prestazione energetica e impianti

Il conduttore dichiara di aver ricevuto le informazioni e la documentazione, comprensiva dell’attestato di prestazione energetica (APE), relative alla prestazione energetica dell’immobile, ai sensi del D.Lgs. 19 agosto 2005, n. 192.

L’attestato, identificato dal codice {{ape_codice}}, attribuisce all’immobile la classe energetica {{ape_classe}}. Copia dell’attestato è consegnata al conduttore e allegata al contratto.

Il locatore dichiara che gli impianti a servizio dell’immobile sono funzionanti e, per quanto a sua conoscenza, conformi alla normativa vigente, e consegna al conduttore la documentazione disponibile, compreso il libretto dell’impianto termico, ove presente.

### Art. 3 · Requisiti del conduttore

Il conduttore dichiara di essere iscritto, per l’anno accademico {{anno_accademico}}, al corso «{{corso_di_studi}}» presso l’ateneo «{{universita}}» e di avere la residenza in un comune diverso da quello in cui ha sede il corso.

Il contratto è riservato a studenti iscritti a un corso di laurea o di formazione post-laurea, come master, dottorato, specializzazione o perfezionamento, secondo la normativa vigente.

L’iscrizione è documentata dal certificato di iscrizione o da una dichiarazione sostitutiva, allegati al contratto. In caso di rinnovo il conduttore consegna al locatore, su richiesta, la documentazione aggiornata.

### Art. 4 · Durata e rinnovo

La locazione ha la durata di mesi {{durata_mesi}}, dal {{data_inizio}} al {{data_fine}}. La durata non può essere inferiore a sei mesi né superiore a trentasei mesi.

Alla prima scadenza il contratto si rinnova automaticamente per un uguale periodo, salvo disdetta del conduttore comunicata al locatore con lettera raccomandata con avviso di ricevimento o PEC almeno un mese e non oltre tre mesi prima della scadenza.

Alla scadenza del periodo di rinnovo il contratto cessa, salvo diverso accordo scritto tra le parti, secondo la normativa vigente.

### Art. 5 · Recesso del conduttore

Il conduttore può recedere dal contratto in qualsiasi momento, qualora ricorrano gravi motivi, dandone comunicazione al locatore con lettera raccomandata con avviso di ricevimento o PEC almeno un mese e non oltre tre mesi prima della data di rilascio indicata nella comunicazione.

Fino alla data di rilascio il conduttore resta tenuto al pagamento del canone e degli oneri accessori.

### Art. 6 · Canone e pagamento

Il canone è determinato dalle parti sulla base dell’accordo territoriale, tenendo conto della zona in cui si trova l’immobile, della sua superficie e delle sue dotazioni. Per l’immobile l’accordo prevede un canone compreso tra € {{fascia_min}} e € {{fascia_max}} al mese, come risulta dall’allegato di calcolo sottoscritto dalle parti.

Il canone di locazione è convenuto in € {{canone_mensile}} (euro {{canone_lettere}}) al mese. Il conduttore paga il canone in rate mensili anticipate, entro il giorno {{giorno_pagamento}} di ciascun mese, mediante bonifico bancario sul conto intestato al locatore, IBAN {{iban_locatore}}; la contabile del bonifico vale come quietanza.

Se l’accordo territoriale lo prevede e la durata supera i dodici mesi, a partire dal secondo anno il canone è aggiornato, su richiesta scritta del locatore, nella misura del 75% della variazione in aumento dell’indice ISTAT dei prezzi al consumo per le famiglie di operai e impiegati (FOI) registrata nei dodici mesi precedenti, salvo l’opzione del locatore per la cedolare secca.

Il mancato pagamento, anche parziale, di una rata del canone decorsi venti giorni dalla scadenza, oppure degli oneri accessori per un importo superiore a due mensilità del canone, costituisce grave inadempimento e consente al locatore di chiedere la risoluzione del contratto, secondo la normativa vigente.

### Art. 7 · Pluralità di conduttori (clausola facoltativa)

Se il contratto è sottoscritto da più studenti, ciascuno di essi è obbligato in solido con gli altri al pagamento dell’intero canone e degli oneri accessori e all’adempimento di tutte le obbligazioni del contratto.

Ciascun conduttore può recedere con le modalità indicate all’art. 5. In tal caso il contratto prosegue con gli altri conduttori e il conduttore receduto resta obbligato in solido per i canoni e gli oneri maturati fino alla data del rilascio.

Con il consenso scritto del locatore, al conduttore receduto può subentrare un altro studente in possesso dei requisiti indicati all’art. 3, che assume tutte le obbligazioni del contratto dalla data del subentro.

Testo alternativo, se il conduttore è uno solo: «Il conduttore è l’unico titolare del contratto; l’ingresso di altri conduttori richiede il consenso scritto del locatore e un’integrazione scritta del contratto.»

### Art. 8 · Deposito cauzionale

A garanzia delle obbligazioni assunte con il contratto, il conduttore versa al locatore, alla sottoscrizione, un deposito cauzionale di € {{deposito_importo}}, che non può superare tre mensilità del canone, ai sensi dell’art. 11 della L. 27 luglio 1978, n. 392.

Il deposito è produttivo di interessi legali, che il locatore corrisponde al conduttore al termine di ogni anno di locazione e, per l’ultimo periodo, alla restituzione del deposito. Il conduttore non può imputare il deposito al pagamento dei canoni o degli oneri accessori.

Il deposito è restituito entro trenta giorni dalla riconsegna dell’immobile, previa verifica del suo stato e dell’adempimento di tutte le obbligazioni del conduttore. Dalla somma da restituire il locatore può detrarre, indicandole per iscritto e documentandole, le somme ancora dovute per canoni, oneri accessori e danni eccedenti il normale deterioramento d’uso.

### Art. 9 · Oneri accessori e utenze

Gli oneri accessori sono ripartiti tra locatore e conduttore secondo la tabella di ripartizione allegata al D.M. 16 gennaio 2017 e secondo l’accordo territoriale; per quanto non previsto si applica l’art. 9 della L. 27 luglio 1978, n. 392.

Il conduttore versa, insieme al canone, un acconto mensile sugli oneri accessori di € {{spese_mensili}}. Il conguaglio è calcolato ogni anno sulla base del rendiconto condominiale ed è pagato entro due mesi dalla richiesta; prima del pagamento il conduttore ha diritto di ottenere l’indicazione specifica delle spese e dei criteri di ripartizione e di prendere visione dei documenti giustificativi.

Le utenze individuali (energia elettrica, gas, acqua ove autonoma, telefonia e internet) sono intestate al conduttore, che ne sostiene i costi e ne chiede la voltura entro trenta giorni dalla consegna. La tassa sui rifiuti è a carico del conduttore nei casi previsti dalla normativa vigente.

### Art. 10 · Uso, ospitalità e divieto di sublocazione

L’immobile è destinato esclusivamente ad abitazione del conduttore e delle persone con lui conviventi. È vietato ogni mutamento di destinazione, anche parziale.

Il conduttore può ospitare persone, anche non appartenenti al proprio nucleo familiare, senza che ciò costituisca sublocazione. Se l’ospitalità si protrae per più di trenta giorni consecutivi, il conduttore ne informa per iscritto il locatore, fermi gli obblighi di comunicazione all’autorità di pubblica sicurezza che la normativa vigente pone a carico di chi ospita.

Il conduttore non può sublocare l’immobile, in tutto o in parte, né cedere il contratto o concedere a terzi il godimento dell’immobile, anche a titolo gratuito o per brevi periodi, comprese le locazioni turistiche tramite piattaforme online, senza il preventivo consenso scritto del locatore.

Il conduttore usa l’immobile con la diligenza del buon padre di famiglia, rispetta il regolamento di condominio, ove esistente, di cui dichiara di aver ricevuto copia, e risponde dei danni causati all’immobile da lui stesso, dai conviventi e dagli ospiti.

### Art. 11 · Manutenzione, riparazioni e innovazioni

Il locatore consegna l’immobile in buono stato di manutenzione e lo mantiene in stato da servire all’uso convenuto, ai sensi dell’art. 1575 c.c.; durante la locazione esegue tutte le riparazioni necessarie, eccettuate quelle di piccola manutenzione, ai sensi dell’art. 1576 c.c.

Sono a carico del conduttore le riparazioni di piccola manutenzione dipendenti da deterioramenti prodotti dall’uso, e non quelle dipendenti da vetustà o da caso fortuito, ai sensi dell’art. 1609 c.c., nonché la manutenzione ordinaria e i controlli periodici dell’impianto termico autonomo, ove presente, secondo la normativa vigente. Per la ripartizione delle spese di manutenzione si applica anche la tabella richiamata all’art. 9.

Il conduttore segnala tempestivamente al locatore, anche per email, i guasti e le riparazioni che non sono a suo carico. In caso di urgenza può eseguire direttamente le riparazioni indispensabili, dandone contemporaneo avviso al locatore, che rimborsa le spese documentate, secondo la normativa vigente.

Il conduttore non può eseguire innovazioni, migliorie, addizioni o modifiche all’immobile e agli impianti senza il preventivo consenso scritto del locatore. Salvo diverso accordo scritto, le migliorie e le addizioni autorizzate restano acquisite all’immobile al termine della locazione senza obbligo di indennità a carico del locatore; per le opere eseguite senza consenso il locatore può chiedere il ripristino dello stato originario a spese del conduttore.

### Art. 12 · Accesso all’immobile

Il conduttore consente l’accesso all’immobile al locatore o a persone da lui incaricate, previo accordo e con un preavviso di almeno quarantotto ore, per verificarne lo stato o eseguire lavori; in caso di urgenza il preavviso è ridotto al tempo strettamente necessario.

In caso di messa in vendita dell’immobile, oppure dopo una comunicazione di disdetta o di recesso e comunque nei due mesi che precedono la scadenza, il conduttore consente le visite di potenziali acquirenti o conduttori, non più di due volte alla settimana e in fasce orarie concordate.

### Art. 13 · Consegna e restituzione dell’immobile

Il conduttore dichiara di aver visitato l’immobile e di averlo trovato adatto all’uso convenuto. La consegna avviene il {{data_inizio}} con la sottoscrizione di un verbale che descrive lo stato dell’immobile, degli impianti e degli eventuali arredi, riporta le letture dei contatori e il numero delle chiavi consegnate ed è corredato da fotografie. Dalla consegna il conduttore è custode dell’immobile.

Entro quindici giorni dalla consegna il conduttore può segnalare per iscritto eventuali difetti non rilevati nel verbale; la segnalazione costituisce integrazione del verbale.

Al termine della locazione il conduttore restituisce l’immobile libero da persone e da cose non di proprietà del locatore, nello stato in cui lo ha ricevuto secondo il verbale di consegna, salvo il deterioramento risultante dall’uso conforme al contratto, ai sensi dell’art. 1590 c.c. La riconsegna risulta da un verbale sottoscritto dalle parti, con le letture dei contatori e la restituzione di tutte le chiavi.

In caso di ritardo nella riconsegna il conduttore corrisponde il canone convenuto fino alla riconsegna effettiva, salvo il risarcimento del maggior danno, secondo la normativa vigente.

### Art. 14 · Registrazione, imposte e agevolazioni

Il locatore registra il contratto presso l’Agenzia delle Entrate entro trenta giorni dalla sottoscrizione o dalla decorrenza, se anteriore, ai sensi del D.P.R. 26 aprile 1986, n. 131, e ne dà documentata comunicazione al conduttore e all’amministratore del condominio, ove presente, nei sessanta giorni successivi, secondo la normativa vigente.

In regime ordinario l’imposta di registro e l’imposta di bollo sono a carico del locatore e del conduttore in parti uguali: il locatore effettua il versamento e il conduttore rimborsa la propria quota con la prima rata di canone successiva alla richiesta. Allo stesso modo sono ripartite le imposte dovute per le annualità successive, per le proroghe e per la risoluzione anticipata.

Il locatore dichiara di applicare il seguente regime fiscale: {{regime_fiscale}}. Se il locatore opta per la cedolare secca, l’imposta di registro e l’imposta di bollo sul contratto, sulle proroghe e sulla risoluzione non sono dovute; l’imposta sostitutiva è a carico esclusivo del locatore.

Se il locatore opta per la cedolare secca, ai sensi dell’art. 3 del D.Lgs. 14 marzo 2011, n. 23, rinuncia per tutta la durata dell’opzione alla facoltà di chiedere l’aggiornamento del canone a qualsiasi titolo, compresa la variazione ISTAT; la presente clausola vale come comunicazione preventiva della rinuncia al conduttore, secondo la normativa vigente.

Il contratto è stipulato dalle parti senza l’assistenza delle rispettive organizzazioni: le parti chiedono a un’organizzazione firmataria dell’accordo territoriale ({{attestazione_ente}}) l’attestazione della rispondenza del contratto all’accordo, ai fini delle agevolazioni fiscali previste dalla normativa vigente per i contratti per studenti universitari, ove ne ricorrano i presupposti.

### Art. 15 · Comunicazione di cessione del fabbricato

Se il conduttore non è cittadino di uno Stato dell’Unione europea, il locatore comunica all’autorità locale di pubblica sicurezza la cessione del godimento dell’immobile entro quarantotto ore dalla consegna, secondo la normativa vigente; a tal fine il conduttore gli consegna, alla sottoscrizione, copia del documento di identità e del titolo di soggiorno.

Negli altri casi la registrazione del contratto assolve all’obbligo di comunicazione, secondo la normativa vigente.

### Art. 16 · Domicilio e comunicazioni

Ai fini del contratto il conduttore elegge domicilio nell’immobile locato e il locatore all’indirizzo di residenza indicato tra le parti. Ciascuna parte comunica per iscritto all’altra ogni variazione.

Le comunicazioni di disdetta, diniego di rinnovo, recesso e messa in mora, e le altre per le quali il contratto prevede la lettera raccomandata, sono effettuate con lettera raccomandata con avviso di ricevimento oppure con posta elettronica certificata (PEC), se il destinatario ne dispone.

Le altre comunicazioni possono essere inviate anche per email, all’indirizzo {{locatore_email}} per il locatore e {{conduttore_email}} per il conduttore, oppure tramite la piattaforma digitale usata dalle parti per gestire il contratto.

### Art. 17 · Trattamento dei dati personali

Ciascuna parte tratta i dati personali dell’altra esclusivamente per le finalità connesse alla conclusione, all’esecuzione e alla registrazione del contratto e per l’adempimento degli obblighi di legge, nel rispetto del Reg. (UE) 2016/679.

Il locatore fornisce al conduttore l’informativa sul trattamento dei dati. I dati possono essere trattati anche con strumenti informatici e comunicati, nei limiti necessari, a professionisti incaricati, all’amministratore del condominio e alle autorità competenti.

### Art. 18 · Norme di rinvio, modifiche e foro competente

Qualsiasi modifica al contratto deve risultare da atto scritto. La tolleranza di una parte verso inadempimenti dell’altra non costituisce rinuncia ai propri diritti.

Per quanto non previsto dal contratto si applicano gli artt. 1571 e seguenti del codice civile, la L. 27 luglio 1978, n. 392, la L. 9 dicembre 1998, n. 431, il D.M. 16 gennaio 2017, l’accordo territoriale e la normativa vigente in materia di locazione.

Per le controversie relative al contratto ciascuna parte può chiedere l’intervento della commissione di negoziazione paritetica e conciliazione stragiudiziale prevista dal D.M. 16 gennaio 2017 e dall’accordo territoriale.

Per ogni controversia relativa al contratto è competente il giudice del luogo in cui si trova l’immobile, previo esperimento del tentativo di mediazione nei casi previsti dalla normativa vigente.

### Allegati

Fanno parte integrante del contratto i seguenti allegati:

- Attestato di prestazione energetica (APE)
- Certificato di iscrizione all’università o dichiarazione sostitutiva
- Allegato di calcolo del canone secondo l’accordo territoriale
- Attestazione di rispondenza all’accordo territoriale, appena rilasciata
- Verbale di consegna con fotografie e, se l’immobile è arredato, inventario degli arredi
- Planimetria catastale dell’immobile
- Copia dei documenti di identità e dei codici fiscali delle parti e, se il conduttore non è cittadino dell’Unione europea, del titolo di soggiorno
- Regolamento di condominio, ove esistente

### Clausole approvate specificamente

Ai sensi e per gli effetti degli artt. 1341 e 1342 del codice civile, le parti, e in particolare il conduttore, dichiarano di aver letto attentamente e di approvare specificamente le clausole contenute nei seguenti articoli:

- Art. 4 · Durata e rinnovo
- Art. 5 · Recesso del conduttore
- Art. 7 · Pluralità di conduttori (clausola facoltativa)
- Art. 8 · Deposito cauzionale
- Art. 10 · Uso, ospitalità e divieto di sublocazione
- Art. 11 · Manutenzione, riparazioni e innovazioni
- Art. 12 · Accesso all’immobile

### Firme

Letto, confermato e sottoscritto.

- Luogo e data: {{luogo_firma}}, {{data_firma}}
- Il locatore ({{locatore_nome}}): ____________________
- Il conduttore ({{conduttore_nome}}): ____________________
- Il locatore, per approvazione specifica delle clausole elencate in «Clausole approvate specificamente»: ____________________
- Il conduttore, per approvazione specifica delle clausole elencate in «Clausole approvate specificamente»: ____________________
