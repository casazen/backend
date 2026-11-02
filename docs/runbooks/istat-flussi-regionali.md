# Analisi tecnica: comunicazione dei flussi turistici regionali (ISTAT)

> Ricerca aggiornata al **26 ottobre 2026**. Documento di analisi, non parere legale.
> Le fonti commerciali (Lodgify, Chekin e altri PMS) sono state usate solo per individuare i sistemi da verificare;
> le decisioni sotto si basano su documentazione pubblica degli enti o dei portali istituzionali.

## Esito operativo

CasaZen non deve implementare un generico «invio a ISTAT». La struttura ricettiva comunica a un **organo
intermedio territoriale** (Regione, Provincia autonoma o Provincia); è quest'ultimo a produrre i flussi aggregati
per ISTAT. Per il PMS esistono invece più protocolli, con credenziali, identificativi, calendari e ricevute diversi.

La soluzione corretta è un dominio unico `TouristFlow` e adapter versionati per famiglia di portale:

1. **ROSS1000 / Turismo5**: XML/TXT per upload e SOAP Web Service; è la famiglia con la copertura più ampia.
2. **Sinfonia Turismo SMART (Campania)**: API REST con CUSR e API key; documentazione e ambiente di test ufficiali.
3. **Osservatorio turistico Sicilia**: API HTTPS con login/token e body XML; specifica ufficiale completa.
4. **SPOT Online (Puglia)**: XML UTF-8 validato da XSD e caricato dal gestore; non risulta una API pubblica.
5. **Turismatica (Umbria)**: file giornaliero TXT; specifica da acquisire dal link ufficiale prima di scrivere il serializer.
6. **STU (Trento)**: file o web service per strutture ricettive; **DTU** per alloggi privati non espone nelle fonti
   consultate un protocollo PMS pubblico equivalente.
7. **Toscana**: routing provinciale fra Turismo5/ROSS1000, Ricestat e sistemi locali; non è un adapter unico.
8. **WebTur (Friuli-Venezia Giulia)** e **ABIT (Sondrio)**: WebTur documenta ufficialmente il caricamento da file, ma
   non pubblica il tracciato; per ABIT non è pubblica una specifica PMS. Servono contratto dell'ente o percorso assistito.
9. **VIT Albergatori (Valle d'Aosta)** e **ASTAT (Bolzano)**: i canali ufficiali sono identificati, ma il contratto
   machine-to-machine corrente non è pubblico.

La delivery nazionale deve offrire per ogni immobile almeno un percorso completo: invio diretto certificato, file
ufficiale oppure compilazione assistita con prova dell'esito. L'automazione via browser/SPID non è una API e non va
implementata. La mancanza di un contratto pubblico impedisce di dichiarare un invio diretto, non di supportare il territorio.

## Correzioni ai documenti di partenza

- Il **C/59** è il modello concettuale/storico della rilevazione, non un tracciato nazionale che un PMS possa inviare a
  tutte le Regioni. Il file nazionale **MOV/C** è prodotto dagli organi intermedi, non dalla struttura.
- «Tutti gli ospiti sopra i tre anni» è errato come regola generale: la specifica ufficiale SPOT include esplicitamente
  anche i bambini sotto i tre anni. CasaZen deve quindi registrare **ogni ospite**, lasciando agli adapter solo le regole
  territoriali documentate.
- La periodicità statistica è mensile, ma molti sistemi richiedono o raccomandano registrazioni giornaliere e la
  valorizzazione anche dei giorni senza movimento/di chiusura.
- Le sanzioni non sono uniformi. Per esempio Lombardia €250–2.500 per mese; Lazio €1.000–2.000; Umbria €500–2.000.
  Non mostrare nell'app un importo nazionale unico.
- **ABIT** è il portale della Provincia di Sondrio, non un secondo portale valido per tutta la Lombardia.
- In Puglia **DMS** è il punto di accesso; i flussi passano da **SPOT** (strutture) o **SPOT Easy** (locazioni).
- La Toscana è territorialmente frammentata: la fonte regionale elenca Unicom per Arezzo; Turismo5 per Firenze e
  relativi ambiti indicati, Pistoia e Prato; Ricestat per Grosseto, Livorno, Lucca, Massa-Carrara, Pisa e Siena.
- Il Molise non è classificabile con affidabilità come ROSS1000 sulla sola base di liste commerciali: va mantenuto
  su percorso assistito configurabile finché la Regione pubblica o consegna il protocollo applicabile.

## Matrice territoriale

`Diretto` significa che esiste una documentazione istituzionale di integrazione PMS; non significa che CasaZen sia già
accreditata. `Export` indica un file documentato da caricare nel portale. `Manuale` significa che non è stata trovata una
specifica pubblica sufficiente.

| Territorio | Sistema verificato | Canale PMS documentato | Strategia CasaZen |
|---|---|---|---|
| Abruzzo | SITRA su ROSS1000 | famiglia XML/TXT/WS ROSS1000; confermare endpoint e credenziali locali | adapter ROSS1000, feature flag territoriale |
| Basilicata | SIST, basato ufficialmente su ROSS1000 | output PMS compatibili; WS dichiarato dal sistema | XML/WS ROSS1000 dopo conformance test |
| Calabria | ROSS1000 (`sirdat.regione.calabria.it`) | protocollo di famiglia; endpoint locale non pubblicato nella pagina | adapter ROSS1000 dopo accreditamento |
| Campania | Sinfonia Turismo SMART | **REST API**, CUSR + API key, ambiente test richiedibile | invio diretto, priorità alta |
| Emilia-Romagna | ROSS1000 | XML v2.4, TXT v4, Web Service, demo ufficiale | implementazione di riferimento ROSS1000 |
| Friuli-Venezia Giulia | WebTur | portale ufficiale dichiara caricamento da file anche per unità abitative; tracciato non pubblico | assistito, poi export/WS quando Insiel consegna contratto |
| Lazio | ROSS1000-Lazio dal 21/05/2025 | pagina ufficiale dichiara import XML | adapter ROSS1000; WS da abilitare solo dopo conferma Regione |
| Liguria | ROSS1000 e residui percorsi locali/RIMOVCLI | non assumere copertura uniforme dal solo nome regione | routing per codice struttura/ente competente |
| Lombardia (eccetto Sondrio) | ROSS1000 | XML/TXT e nota WS richiamati dal portale; collaudo ARIA necessario | adapter ROSS1000 |
| Lombardia, Provincia di Sondrio | ABIT | nessuna specifica API pubblica trovata | manuale/accordo Provincia; non instradare a ROSS1000 dall'host |
| Marche | ROSS1000/ISTRICE | protocollo di famiglia, configurazione locale da acquisire | adapter ROSS1000 dopo collaudo |
| Molise | servizio regionale non identificato nelle fonti pubbliche consultate | nessun contratto PMS pubblico | assistito con contatto ente; ticket di acquisizione specifica |
| Piemonte | ROSS1000 | protocollo di famiglia, configurazione locale da acquisire | adapter ROSS1000 dopo collaudo |
| Puglia, strutture ricettive | DMS → SPOT Online/Completo | XML UTF-8, XSD `movimentogiornaliero-0.6.xsd` e `datatype-0.6.xsd`; upload SPID | generazione XML + download, niente scraping |
| Puglia, locazioni turistiche | DMS → SPOT Easy | inserimento portale; può importare file PS, che non equivale al flusso ISTAT PMS | manuale; non confondere file Alloggiati con file SPOT |
| Sardegna | SIRED/ROSS1000 | protocollo di famiglia, configurazione regionale da acquisire | adapter ROSS1000 dopo collaudo |
| Sicilia | Osservatorio Turistico regionale | API HTTPS + bearer token + XML | invio diretto, priorità alta |
| Toscana | Turismo5, Ricestat e sistemi territoriali | Turismo5 usa famiglia ROSS1000; Ricestat richiede contratto separato | resolver provinciale + adapter distinti |
| Trento, strutture ricettive | STU | file e web service; gestionali devono superare test di congruenza | partnership/collaudo ISPAT |
| Trento, alloggi privati | DTU Alloggi | inserimento online SPID/CPS; nessuna API pubblica verificata | manuale finché PAT abilita PMS |
| Umbria | Turismatica | file giornaliero TXT da gestionale | export TXT dopo acquisizione specifica ufficiale |
| Valle d'Aosta | VIT Albergatori | manuale ufficiale conferma inserimento e integrazione PMS; contratto non pubblico | assistito; adapter dopo contratto regionale |
| Veneto | ROSS1000 | protocollo di famiglia | adapter ROSS1000 dopo collaudo |
| Bolzano | ASTAT IST-00139 + TUR-031 | guide/circolari ufficiali; contratto PMS non pubblico | assistito; adapter dopo contratto ASTAT |

## Contratti tecnici verificati

### ROSS1000 / Turismo5

La Regione Emilia-Romagna pubblica il set più completo e utilizzabile come baseline:

- upload di file **XML** o **TXT**;
- XML/WS versione pubblicata **2.4** e TXT **4.0**;
- Web Service per invio automatico;
- file composto da uno o più `movimento`, ciascuno riferito a un giorno, con `data`, `struttura`, `arrivi`,
  `partenze`, `prenotazioni` e `rettifiche`;
- calendario giornaliero: apertura, camere occupate/disponibili e letti disponibili;
- arrivi nominativi/codificati e identificatore PMS stabile (`idswh`); comuni, Stati e tipi alloggiato riusano le
  tabelle Alloggiati Web;
- lo storico distingue file caricati e invii Web Service; il semplice HTTP 200 non è prova di acquisizione.

La specifica pubblica è comune al prodotto, ma **endpoint, codice struttura, credenziali, campi opzionali e abilitazione
WS sono configurazioni dell'ente**. Non è corretto attivare automaticamente il WS in tredici regioni solo perché usano
lo stesso software.

Per CasaZen:

- serializer puro e versionato (`Ross1000XmlV24`), validazione XSD/contratto prima dell'invio;
- `idswh` non deve contenere PII: usare un UUID stabile CasaZen;
- un batch giornaliero, ordinato e idempotente per `(property, localDate, protocolVersion)`;
- salvare l'esito per giorno e per record; “inviato” solo con risposta positiva del portale;
- supportare prima XML download, poi SOAP, senza implementare il TXT salvo necessità di retrocompatibilità;
- ottenere per ogni ente: base URL produzione/test, WSDL effettivo, regola di autenticazione, codice struttura,
  password di trasmissione e contatto di collaudo.

Fonti: [pagina e manuali ROSS1000 Emilia-Romagna](https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo),
[XML/WS 2.4](https://statistica.regione.emilia-romagna.it/documentazione/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo/tracciatoxml-webservice.pdf/@@download/file/Tracciato_XML-WEBSERVICE-2.4.pdf),
[TXT v4](https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo/tracciatotxt.pdf/@@download/file),
[FAQ ufficiali](https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo/faq-rilevazioni-turismo),
[Lombardia](https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/informazioni-importanti-per-avvio-della-struttura-ricettiva),
[Lazio](https://regione.lazio.it/cittadini/turismo/comunicazione-flussi-turistici-e-cir),
[Calabria](https://www.regione.calabria.it/cultura-turismo-ed-eventi/strutture-ricettive/informazioni-servizi-e-modulistica/documenti/),
[Abruzzo/SITRA](https://www.regione.abruzzo.it/novita/procedura-telematica-ottenere-il-cir),
[Veneto](https://elezioni2020.regione.veneto.it/web/turismo/rilevazione-flussi-turistici-ross1000).

### Sinfonia Turismo SMART — Campania

Dal 2025 il portale espone una integrazione **REST API** ufficiale. La struttura comunica al PMS:

- `CUSR`;
- `API KEY`, visibile in “Anagrafica → Gestione Anagrafica Struttura Ricettiva”.

La Regione distribuisce la documentazione tecnica come archivio `.7z` e concede utenze di test alle software house su
richiesta. L'invio automatico va quindi sviluppato contro l'ambiente test e certificato, non dedotto dalle schermate.

Passi necessari: acquisire e versionare localmente OpenAPI/schema/esempi contenuti nell'archivio ufficiale, richiedere
l'utenza CasaZen, implementare una API key cifrata per struttura, mapping CUSR, idempotency/retry secondo il contratto e
salvataggio della risposta applicativa.

Fonti: [pagina ufficiale Sinfonia](https://www.regione.campania.it/aree-tematiche/cultura-turismo/strutture-ricettive-locazioni-brevi/sinfonia-turismo-smart-sistema-monitoraggio-accoglienza-ricettivita-turistica),
[istruzioni API](https://www.regione.campania.it/assets/documents/modalita-operative-per-la-trasmissione-della-movimentazione-turistica-via-web-api-tramite-software-gestionali.pdf),
[archivio tecnico ufficiale](https://www.regione.campania.it/assets/documents/documentazione-tecnica-per-trasmissione-movimentazione-via-api-web.7z).

### Osservatorio Turistico — Sicilia

Il protocollo ufficiale rev. **1.0.7 del 03/04/2025** documenta API HTTPS real-time:

- `GET /webapi/api/auth/login`, header `UserId` e `Password`, restituisce bearer token;
- `POST /webapi/api/auth/logout`;
- `POST /webapi/api/stay/addfrompms` per nuovi soggiorni;
- `POST /webapi/api/stay/updatefrompms` per reinviare il soggiorno completo aggiornato;
- ulteriori operazioni per check-out, occupazione camere e chiusura giornaliera;
- payload XML e XSD; credenziali PMS dedicate a **una sola struttura**, diverse dall'accesso web.

Campi principali: `StayId`, `HotelCode`, ospiti con `GuestId`, età, cittadinanza, nascita e residenza con codici
Alloggiati Web a 9 cifre, tipo ospite 16–20, genere, date UTC, occupazione letto e camere/intervalli. Il documento indica
tutti i campi obbligatori tranne email: questa scelta è più invasiva di alcuni tracciati aggregati e richiede minimizzazione
e retention specifica.

Nota di sicurezza: il login con credenziali in header su un `GET` è parte del protocollo ufficiale, ma CasaZen deve
comunque evitare logging di URL/header, usare TLS, secret cifrati, token solo in memoria/cache breve e allowlist host.

Fonte: [protocollo PMS ufficiale Sicilia](https://osservatorioturistico.regione.sicilia.it/public/public/docs/Sistema_Informativo_Osservatorio_Turistico_ProtocolloPMS.pdf),
[portale e FAQ](https://osservatorioturistico.regione.sicilia.it/public/info).

### SPOT / SPOT Easy — Puglia

Per le strutture, SPOT Online importa un XML 1.0 UTF-8 conforme a
`movimentogiornaliero-0.6.xsd` e `datatype-0.6.xsd`. Radice `movimenti` con attributo `vendor`; ogni `movimento` è
giornaliero e usa:

- `MP` con arrivi/partenze;
- `NM` per aperto senza movimento;
- `EC` per esercizio chiuso;
- `datistruttura`: camere disponibili, posti letto disponibili e camere occupate;
- identificatori cliente PMS privi di dati anagrafici;
- codici Alloggiati Web per tipologia, cittadinanza e residenza;
- sequenza continua delle date: un salto interrompe l'elaborazione.

L'upload avviene nell'area SPOT del DMS dopo accesso SPID. La documentazione pubblica descrive generazione e upload,
non una API server-to-server: CasaZen deve quindi offrire file e istruzioni, senza custodire SPID né automatizzare il browser.

SPOT Easy è riservato alle locazioni. Il “CARICA FILE PS” importa il file di Pubblica Sicurezza per evitare il reinserimento,
ma non prova l'esistenza di un protocollo PMS ISTAT equivalente a SPOT Online. Va trattato come percorso distinto.

Fonti: [specifica XML SPOT](https://info.ect.regione.puglia.it/wp-content/uploads/2025/10/Spot-online-specifiche.pdf),
[manuali e XSD](https://info.ect.regione.puglia.it/come-fare-per/manualistica/),
[FAQ DMS/SPOT](https://dms.ect.regione.puglia.it/comefareper?pagina=faqprivateSpot),
[servizio regionale](https://pr2127.regione.puglia.it/web/turismo-e-cultura/come-fare-per?origin=at&pagina=1&serviceId=PublicService%2F0082-r_puglia&vista=ricerca).

### Turismatica — Umbria

La fonte ufficiale conferma inserimento manuale o invio di un **file giornaliero `.txt`** prodotto dal gestionale,
registrazione quotidiana di arrivo/partenza e camere occupate e chiusura mensile entro il giorno 5, anche a zero.
La specifica “Istruzioni per interfacciamento” deve essere acquisita e congelata prima di implementare il formato; non
va sostituita con un XML sulla base di guide di PMS terzi.

Fonte: [servizio Turismatica](https://accessounico.regione.umbria.it/it/enti-partecipanti/enti/regione-umbria/turismo-sport-caccia-pesca/trasmettere-i-dati-del-movimento-ospiti-del-proprio-esercizio-ricettivo/),
[modalità e scadenze](https://www.regione.umbria.it/turismo-attivita-sportive/modalita-di-rilevazione-del-movimento-dei-clienti-negli-esercizi-ricettivi1).

### STU / DTU — Provincia autonoma di Trento

STU ammette inserimento manuale, file da gestionale abilitato e Web Service. La software house deve effettuare test di
congruenza; quindi l'integrazione richiede onboarding con ISPAT/PAT. DTU Alloggi, usato per alloggi privati, è descritto
come inserimento online con SPID/CPS: non si può assumere che il Web Service STU copra anche DTU.

Fonte: [servizio ufficiale PAT](https://www.provincia.tn.it/Servizi/Comunicare-online-le-presenze-degli-ospiti-a-ISTAT-e-Pubblica-Sicurezza),
[istruzioni STU/ISPAT](https://www.provincia.tn.it/Amministrazione/Documenti/Istruzioni-per-l-invio-dati-di-movimentazione-turistica-via-Sistema-Informativo-Turistico-STU-a-ISPAT).

### Toscana, WebTur e ABIT

- **Toscana**: il portale è scelto in base alla Provincia/Città metropolitana, non solo a `RegionCode`. Il resolver deve
  usare almeno comune ISTAT → provincia → autorità e avere configurazione temporalmente valida. Fonte:
  [elenco regionale](https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche).
- **WebTur FVG**: il portale istituzionale e l'assistenza Insiel sono verificati, ma le specifiche di upload/WS trovate
  pubblicamente sono guide di PMS terzi. Non bastano per codice regolatorio. Fonte: [WebTur](https://servizistatistici.regione.fvg.it/webtur/jsp/wt/login.jsp).
- **ABIT**: la Provincia di Sondrio conferma uso, accesso SPID e scadenza del giorno 5; un atto provinciale del 2026
  conferma che ABIT e ROSS1000 sono due sistemi distinti e che ABIT alimenta il flusso ufficiale. Non è stata trovata
  documentazione API pubblica. Fonti: [procedimento Provincia di Sondrio](https://www.provinciasondrio.it/servizio-turismo-cultura-istruzione/procedimenti/comunicazione-dei-flussi-turistici-da-parte-strutture-ricettive),
  [portale ABIT](https://abit.so.it/).

### Basilicata, Valle d'Aosta, Bolzano e Molise

- **Basilicata**: APT Basilicata dichiara che SIST è basato su Turismo5/ROSS1000, acquisisce output compatibili dei
  gestionali e può trasmettere via web service. La rilevazione include le locazioni turistiche, assimilate operativamente
  alle case e appartamenti per vacanze, e i dati mensili vanno comunicati entro il giorno 10. Fonti:
  [SIST APT Basilicata](https://www.aptbasilicata.it/sistema-informativo-statistico-turistico/),
  [dati e obblighi 2026](https://www.aptbasilicata.it/dati-statistici-2026/).
- **Valle d'Aosta**: il manuale ufficiale VIT Albergatori conferma obbligatorietà, inserimento manuale, integrazione con
  PMS e chiusura mensile C59-M entro il giorno 5, inclusi i periodi chiusi. Non pubblica però il contratto tecnico PMS.
  Fonte: [manuale VIT Albergatori](https://www.regione.vda.it/gestione/vit_albergatori/Docs/VIT%20Albergatori%20Manuale%20utente.pdf).
- **Bolzano**: ASTAT gestisce la rilevazione mensile IST-00139 e, da novembre 2023, la giornaliera TUR-031. La pagina
  ufficiale pubblica guida e circolare correnti e il contatto `astat.tur@provinz.bz.it`, ma non un contratto PMS.
  Fonte: [ASTAT Turismo](https://astat.provinz.bz.it/it/turismo).
- **Molise**: le pagine regionali reperibili documentano anagrafe/CIN, ma non identificano in modo verificabile il
  canale dei flussi né un formato PMS. CasaZen deve configurare il percorso consegnato dall'ufficio competente e, fino
  ad allora, presentare riepilogo assistito e contatto regionale; non deve etichettarlo ROSS1000 per analogia commerciale.
  Fonte: [servizio regionale strutture e CIN](https://www.regione.molise.it/flex/cm/pages/ServeBLOB.php/L/IT/IDPagina/20744).

## Modello dati CasaZen

Il modello esistente è una buona base: `StayGuest` possiede ruolo ospite, nascita, cittadinanza e codici delle tabelle
Alloggiati; `Property` ha comune ISTAT e regione. Mancano però dati essenziali per i flussi regionali.

### Dati da aggiungere

**Property / profilo territoriale**

- identificatori separati e temporalmente versionati: CIR/CUSR/codice portale/hotel code; il CIN non li sostituisce;
- autorità/portale risolto (`portalKind`, `authorityCode`, `validFrom`, `validTo`, versione protocollo);
- capacità autorizzata e giornaliera: camere/unità, letti, piazzole;
- credenziale per adapter, cifrata e write-only, con tipo e data ultima verifica;
- timezone locale (già presente) obbligatoria per calcolare il giorno di rilevazione.

**StayGuest**

- residenza: Italia/estero, codice comune Alloggiati o codice Stato, più label;
- relazione esplicita al capofamiglia/capogruppo (`HeadStayGuestId`) invece di inferirla solo dall'ordine;
- flag occupazione posto letto;
- età derivata alla data di arrivo, mai memorizzata come fonte primaria;
- eventuali campi qualitativi solo se richiesti e con provenienza/consenso chiaramente separati dai dati obbligatori.

**Calendario giornaliero**

Nuova entità per `(PropertyId, LocalDate)` con stato `Open`, `Closed`, `OpenNoMovement`, camere/unità disponibili,
camere occupate e letti disponibili. Non dedurre sempre “aperto” dalle prenotazioni: lavori, chiusure stagionali e
capacità vendibile possono differire dall'anagrafica.

**Trasmissione**

- `TouristFlowSubmission`: tenant, property, authority, giorno/intervallo, adapter e versione, hash canonico payload,
  stato, tentativi, timestamp, idempotency key, ricevuta e storage key del payload cifrato;
- `TouristFlowRecordResult`: identificatore CasaZen non-PII, esito/codice portale per singolo giorno/record;
- stati append-only: `Draft`, `Invalid`, `Ready`, `Exported`, `Queued`, `Accepted`, `PartiallyAccepted`, `Rejected`,
  `TechnicalFailure`, `ManuallyDeclaredSent`;
- vincolo: `Accepted` richiede ricevuta/esito applicativo; HTTP 2xx da solo non basta.

Non riusare `AlloggiatiWebReport`: finalità, scadenza, destinatario, ricevuta e granularità sono diversi. Si riusano invece
le **tabelle di codifica ufficiali** e la raccolta ospiti, con mapping espliciti e versionati.

## Architettura proposta

```text
Booking + StayGuests + DailyCapacity + PortalProfile
                         |
                         v
               TouristFlowCanonicalizer
                         |
                 validation snapshot
                         |
            +------------+-------------+
            |            |             |
       Ross1000      Sinfonia      Sicilia / altri
      XML + SOAP     REST API       API o file
            |            |             |
            +------ receipt/result ----+
                         |
             TouristFlowSubmission audit
```

Interfaccia suggerita:

```csharp
public interface ITouristFlowAdapter
{
    PortalKind Portal { get; }
    ProtocolVersion Version { get; }
    ValidationResult Validate(TouristFlowBatch batch, PortalProfile profile);
    Task<PreparedPayload> PrepareAsync(TouristFlowBatch batch, CancellationToken ct);
    Task<SubmissionResult> SubmitAsync(PreparedPayload payload, PortalCredential credential, CancellationToken ct);
}
```

`SubmitAsync` non deve esistere/essere abilitato per un adapter solo-export. Il resolver restituisce capacità esplicite:
`ManualEntry`, `FileExport`, `DirectSubmit`, `SupportsCorrection`, `SupportsZeroMovement`, `RequiresDailyClose`.

## Sicurezza, privacy e affidabilità

- CasaZen opera verosimilmente come responsabile del trattamento per l'host: DPA, registro trattamenti e informativa
  devono includere questa finalità prima dell'invio diretto.
- Minimizzare: alcuni portali vogliono dati individuali pseudonimi/codificati, altri aggregati. Non inviare nome,
  documento o email se lo schema non li richiede.
- Segreti per struttura cifrati con purpose separato da Alloggiati; mai SPID/CIE/CNS, mai header/token nei log.
- Payload e risposte in storage privato cifrato, accesso tenant-scoped, retention separata dai dati Alloggiati.
- Log applicativi con property/report ID e codici errore, senza XML né dati ospite.
- Retry soltanto su errori tecnici transienti; rifiuti di validazione richiedono correzione. Idempotency key deterministica.
- Allowlist degli host ufficiali, TLS obbligatorio, timeout/circuit breaker e controllo della destinazione dopo redirect.
- Ogni modifica o annullamento deve produrre una nuova revisione; non sovrascrivere la prova di ciò che è stato inviato.
- Dashboard mensile che riconcilia ogni giorno: movimento, aperto senza movimento o chiuso. Nessun giorno “bianco”.

## Piano di implementazione nazionale

Le fasi sono incrementi dello stesso programma obbligatorio. Un blocco di accreditamento esterno mantiene spento solo
l'invio production interessato: dominio, UI, file disponibili e percorso assistito devono comunque essere consegnati.

### Fase 0 — acquisizione e accordi

1. Registrare CasaZen come software house presso Campania, Sicilia e un ente ROSS1000 pilota.
2. Ottenere ambienti test, credenziali fittizie, endpoint, schemi, esempi di risposta e procedura di certificazione.
3. Archiviare gli artefatti ufficiali con URL, data, SHA-256 e versione; non affidarsi a link “latest”.
4. Chiedere specifiche a Umbria, PAT, Insiel FVG, Provincia di Sondrio e gestori Ricestat.
5. Validazione legale/privacy su ruolo CasaZen, retention e custodia credenziali.

### Fase 1 — dominio ed export

1. Migrazioni per profili portale, residenza ospite, calendario giornaliero e submission audit.
2. Resolver territoriale versionato; onboarding che non consente l'invio senza codice struttura e autorità confermati.
3. Motore di completezza comune e dashboard “giorni mancanti”.
4. Export ROSS1000 XML e SPOT XML, validati contro schema e test golden-file.
5. Download con checksum e guida di caricamento; stato onesto `Exported`, non `Accepted`.

### Fase 2 — invio diretto

1. Sinfonia REST in ambiente test, poi Sicilia API.
2. ROSS1000 SOAP su un solo ente pilota; generalizzazione soltanto dopo prove di compatibilità su endpoint diversi.
3. Polling/storico o ricevute dove previsti; riconciliazione e correzioni.
4. Alert prima della scadenza e su rifiuti, senza citare una sanzione generica nazionale.

### Fase 3 — copertura territoriale e conformance

Consegnare il percorso assistito per ogni territorio sin dalla prima attivazione. Acquisire poi i contratti Turismatica,
STU, Ricestat, Unicom, WebTur, ABIT, VIT e ASTAT e promuovere la relativa capacità a file/diretta dopo test di
conformance. Ogni nuova configurazione con protocollo identico richiede comunque collaudo per ente; una differenza di
schema o semantica crea un adapter/versione separata.

## Criteri di accettazione minimi

- Un soggiorno con famiglia/gruppo produce codici e legami corretti senza PII negli identificatori PMS.
- Giorni con ospiti, senza movimento e chiusi sono tutti rappresentabili e riconciliati.
- Cambio camera, partenza anticipata, rettifica, cancellazione e soggiorno a cavallo del mese hanno test dedicati.
- Serializer verificati contro XSD/schema ufficiale e golden files dell'ente.
- Due retry dello stesso batch non duplicano arrivi; un batch modificato crea una revisione distinta.
- Un 200 con errore applicativo non marca `Accepted`; accettazione richiede prova persistita.
- Tenant isolation, secret redaction, retention e cancellazione sono coperti da test.
- Se il portale/adapter non è certificato, l'interfaccia mostra solo export/manuale e non promette invio automatico.
- Ogni comune ISTAT e categoria di immobile attiva risolve esattamente una regola temporalmente valida.
- Ogni Regione e Provincia autonoma ha almeno una journey E2E: diretta, export ufficiale o assistita.

## Questioni ancora da chiudere con gli enti

1. Quali regioni ROSS1000 accettano oggi il WS e quali solo upload; endpoint test/produzione e WSDL effettivi.
2. Se il tracciato ROSS1000 2.4 sia ancora la versione prescritta in ogni ente nel 2026.
3. Contenuto dell'archivio Sinfonia `.7z`, politica di idempotenza, limiti e codici risposta; va importato nel repository
   solo se la licenza/documentazione lo consente.
4. XSD aggiornati e procedura di abilitazione account PMS Sicilia.
5. Tracciato TXT ufficiale Turismatica e WebTur; contratto WS WebTur eventualmente disponibile a software house.
6. Specifiche STU e perimetro separato DTU; requisiti dei test di congruenza PAT.
7. API/import Ricestat per ciascuna provincia toscana e futuro consolidamento regionale.
8. API/export ABIT e piano di evoluzione/migrazione della Provincia di Sondrio.
9. Identificazione formale del servizio e del referente flussi del Molise.
10. Contratti PMS non pubblici di VIT Valle d'Aosta e ASTAT Bolzano.

Questi punti non sono domande di prodotto: richiedono documenti, endpoint, credenziali o collaudi rilasciati da soggetti
esterni. Il comportamento CasaZen in loro assenza è già deciso: percorso assistito tracciato, senza scraping né falsa
dichiarazione di invio automatico.
