---
id: US-ISTAT-01
slug: istat-flussi-regionali
title: Flussi turistici regionali — export verificati e invio diretto
phase: implementation
type: feature
priority: P1
status: ready-for-helix-intake
issue: null
depends_on: [alloggiati-code-tables, comuni-istat]
blocks: []
exit_contributes_to: Compliance regionale automatizzata e verificabile
last_reviewed: 2026-10-26
---

# Spec Helix — flussi turistici regionali (ISTAT)

## Overview

CasaZen deve aiutare host e property manager a comunicare i movimenti turistici agli organi intermedi territoriali.
Non esiste un singolo endpoint o «formato ISTAT» nazionale per le strutture: Regione, Provincia autonoma o Provincia
raccolgono i dati attraverso portali diversi e trasmettono successivamente gli aggregati a ISTAT.

La ricerca ufficiale, la matrice territoriale e i riferimenti tecnici sono in
[`docs/runbooks/istat-flussi-regionali.md`](../../docs/runbooks/istat-flussi-regionali.md). Questo documento traduce
quella ricerca in un input eseguibile dal processo Helix.

**Obiettivo della delivery:** rendere gestibile da CasaZen l'adempimento per qualunque appartamento italiano, in tutte
le 20 Regioni e nelle Province autonome di Trento e Bolzano. La delivery comprende dominio, UI, calendario, resolver,
audit, tutti gli export documentabili, adapter diretti documentati e un percorso assistito verificabile dove l'ente non
pubblica un contratto machine-to-machine. Non sono ammesse Regioni escluse né righe `unverified` senza istruzioni.

“Supportato” non significa artificiosamente “API”: significa che l'host può completare l'adempimento partendo da
CasaZen. Il livello migliore disponibile è scelto in quest'ordine: `DirectSubmit`, `FileExport`, `AssistedManual`.

## Decisioni vincolanti

1. Nessun adapter chiamato `ISTAT`: gli adapter rappresentano il portale destinatario e una versione di protocollo.
2. `Accepted` significa ricevuta o esito applicativo persistito. Un file generato è `Exported`; HTTP 2xx da solo non basta.
3. Nessuna automazione del browser, SPID, CIE o CNS. Dove manca una API si offre export o assistenza manuale.
4. Non riusare `AlloggiatiWebReport`: scopo, destinatario, granularità e prova di invio sono differenti.
5. Riutilizzare `StayGuest`, `AlloggiatiCodeEntry`, `Property.ComuneIstatCode`, `RegionCode` e la cifratura esistente.
6. Portale e autorità devono essere risolti per comune/provincia e validità temporale, non dalla sola Regione.
7. Funzionalità spenta per default e attivata per singolo adapter/ente dopo conformance test.
8. Mai mostrare una sanzione nazionale unica; testi, scadenze e importi sono contenuti territoriali versionati.
9. Tutti i requisiti NF-01–NF-10 sono `must`. Le dipendenze esterne impediscono soltanto l'attivazione production del
   singolo adapter diretto, mai dominio, UI, export disponibile o percorso assistito.
10. La classificazione giuridica dell'immobile è input obbligatorio: “appartamento” può essere locazione turistica,
    CAV/struttura ricettiva o altra categoria e può cambiare portale nello stesso territorio.

## Copertura nazionale vincolante

Ogni riga deve avere fixture di routing e una journey E2E. `FileExport` è attivabile solo con formato ufficiale; negli
altri casi `AssistedManual` apre il portale ufficiale, mostra dati aggregati/copincollabili, checklist, scadenza e consente
di allegare ricevuta o dichiarare l'esito. Non si automatizzano identità digitali o pagine web.

| Territorio / categoria | Destinazione | Capacità minima obbligatoria | Capacità diretta da implementare |
|---|---|---|---|
| Abruzzo | SITRA/ROSS1000 | XML ROSS1000 + assistenza upload | SOAP quando accreditato |
| Basilicata | SIST/ROSS1000 | XML ROSS1000 + assistenza upload | WS quando accreditato |
| Calabria | ROSS1000 | XML ROSS1000 + assistenza upload | WS quando accreditato |
| Campania | Sinfonia Turismo SMART | riepilogo assistito | REST CUSR/API key |
| Emilia-Romagna | ROSS1000 | XML v2.4 | SOAP/WS v2.4 |
| Friuli-Venezia Giulia | WebTur | file solo dopo consegna tracciato; fino ad allora assistito | adapter upload/WS dopo contratto Insiel |
| Lazio | ROSS1000-Lazio | XML import | WS quando accreditato |
| Liguria AAUT | ROSS1000 | XML import + assistenza | WS quando abilitato |
| Liguria strutture | RIMOVCLI finché vigente, poi regola datata ROSS1000 | assistito/export ufficiale vigente | adapter solo con contratto ufficiale |
| Lombardia eccetto Sondrio | ROSS1000 | XML ROSS1000 + assistenza upload | WS dopo collaudo ARIA |
| Sondrio | ABIT | assistito | adapter dopo contratto Provincia |
| Marche | ISTRICE/ROSS1000 | XML ROSS1000 + assistenza upload | WS quando accreditato |
| Molise | servizio indicato dall'ente competente | assistito configurabile, senza inventare formato | adapter dopo identificazione formale |
| Piemonte | Piemonte Dati Turismo/ROSS1000 | TXT/XML import | WS quando accreditato |
| Puglia strutture | DMS/SPOT Online | XML XSD 0.6 | nessuna API pubblica |
| Puglia locazioni | DMS/SPOT Easy | assistito; eventuale import PS resta distinto | adapter solo con protocollo ISTAT ufficiale |
| Sardegna | SIRED/ROSS1000 | XML ROSS1000 + assistenza upload | WS quando accreditato |
| Sicilia | Osservatorio Turistico | riepilogo assistito | HTTPS token + XML rev. 1.0.7 |
| Toscana Arezzo | Unicom | assistito/export dopo contratto | adapter separato |
| Toscana Firenze e Prato/Pistoia secondo regola vigente | Turismo5 | XML ROSS1000 | WS quando accreditato |
| Toscana GR/LI/LU/MS/PI/SI | Ricestat/Motourist | assistito/export dopo contratto provinciale | adapter Ricestat separato |
| Trento strutture | STU | file ufficiale | WS dopo test ISPAT |
| Trento alloggi privati | DTU Alloggi | assistito SPID/CPS | nessuna API pubblica |
| Umbria | Turismatica | TXT ufficiale versionato | nessuna API pubblica verificata |
| Valle d'Aosta | VIT Albergatori | assistito e import PMS dopo tracciato ufficiale | adapter dopo contratto regionale |
| Veneto | ROSS1000 | XML/TXT ufficiale | WS quando accreditato |
| Bolzano | ASTAT IST-00139/TUR-031 | assistito secondo guida ASTAT corrente | adapter solo con contratto ASTAT |

## User stories e acceptance criteria

### NF-01 — Profilo territoriale e resolver del portale · must

Come host voglio che CasaZen individui il percorso applicabile al mio immobile senza assumere che una Regione abbia un
solo portale.

- **AC1.1**: dato un immobile con comune ISTAT e una data, il resolver restituisce autorità, portale, versione protocollo
  e capacità (`ManualEntry`, `FileExport`, `DirectSubmit`) della regola temporalmente valida.
- **AC1.2**: per Provincia di Sondrio il resolver restituisce ABIT e non il canale host ROSS1000.
- **AC1.3**: per la Toscana il risultato dipende almeno dalla Provincia; una regola assente o ambigua produce
  `portal_configuration_unverified`, mai un fallback silenzioso.
- **AC1.4**: Molise, Valle d'Aosta e Bolzano restituiscono sempre una journey assistita con ente, URL, contatto,
  scadenza e checklist versionati; mai `unsupported` o `unverified` come stato visibile all'host.
- **AC1.5**: ogni regola conserva URL fonte, data di verifica, validità e versione; l'admin può disattivarla senza deploy.
- **AC1.6**: la seed copre tutti i comuni italiani e le due Province autonome; un test fallisce se un comune e una
  categoria attiva non risolvono esattamente una regola.

### NF-02 — Profilo e credenziali della struttura · must

Come host voglio associare all'immobile i codici richiesti dal portale senza sostituirli impropriamente con il CIN.

- **AC2.1**: CIR, CUSR, hotel/structure code e identificatore dell'autorità sono campi distinti, tenant-scoped e
  versionabili; il CIN esistente non viene usato automaticamente al loro posto.
- **AC2.2**: una configurazione segnala i campi obbligatori mancanti prima della generazione o dell'invio.
- **AC2.3**: API key, password PMS o password di trasmissione sono write-only, cifrate con purpose dedicato e mai
  restituite o registrate nei log.
- **AC2.4**: SPID/CIE/CNS non possono essere salvati come credenziale del portale.

### NF-03 — Residenza e relazioni fra ospiti · must

Come gestore voglio raccogliere una volta i dati codificati necessari sia ad Alloggiati sia ai flussi regionali.

- **AC3.1**: ogni `StayGuest` può registrare residenza italiana (codice comune Alloggiati) o estera (codice Stato), più
  la label mostrata all'utente.
- **AC3.2**: familiare e membro gruppo referenziano esplicitamente il relativo capo; la relazione è validata nello stesso
  booking e tenant.
- **AC3.3**: è registrabile se l'ospite occupa un posto letto; l'età è calcolata dalla data di nascita alla data di arrivo.
- **AC3.4**: bambini di ogni età sono rappresentabili; nessuna regola globale li esclude dalla rilevazione.
- **AC3.5**: i codici provengono dalle tabelle ufficiali importate; il testo libero non viene convertito se il match non è
  unico.

### NF-04 — Calendario giornaliero della struttura · must

Come host voglio classificare ogni giorno del mese per evitare giorni mancanti o presunti dal sistema.

- **AC4.1**: per immobile/data locale è registrabile `Open`, `Closed` o `OpenNoMovement`, con camere/unità disponibili,
  camere occupate e letti disponibili.
- **AC4.2**: prenotazioni e capacità anagrafica propongono valori, ma chiusure, lavori e capacità vendibile possono essere
  corretti con motivazione e audit.
- **AC4.3**: la dashboard mensile evidenzia ogni giorno non classificato; un mese non è pronto se rimane un giorno bianco.
- **AC4.4**: i calcoli usano `Property.Timezone` e gestiscono soggiorni a cavallo di mese e cambio ora.

### NF-05 — Submission audit e stati onesti · must

Come host voglio sapere cosa CasaZen ha preparato, esportato o realmente trasmesso.

- **AC5.1**: ogni submission conserva tenant, immobile, autorità, periodo, adapter/versione, hash canonico, revisione,
  stato, tentativi e storage key privata del payload/risposta.
- **AC5.2**: stati minimi: `Draft`, `Invalid`, `Ready`, `Exported`, `Queued`, `Accepted`, `PartiallyAccepted`, `Rejected`,
  `TechnicalFailure`, `ManuallyDeclaredSent`.
- **AC5.3**: `Accepted` richiede ricevuta o risposta applicativa positiva; il database impedisce lo stato senza prova.
- **AC5.4**: due retry dello stesso contenuto hanno la stessa idempotency key; una rettifica crea nuova revisione e non
  sovrascrive la prova precedente.
- **AC5.5**: esiti per giorno/record usano identificatori CasaZen privi di PII; payload XML e token non appaiono nei log.

### NF-06 — Export ROSS1000 XML · must

Come host di un territorio ROSS1000 abilitato voglio scaricare un file conforme da caricare sul portale.

- **AC6.1**: il serializer produce XML v2.4 deterministico con codice struttura, prodotto, movimenti ordinati per giorno,
  struttura, arrivi, partenze e le sezioni applicabili.
- **AC6.2**: comuni, Stati e tipi ospite usano i codici Alloggiati ufficiali; `idswh` è stabile, massimo 20 caratteri e
  privo di dati personali.
- **AC6.3**: apertura/chiusura e disponibilità derivano dal calendario NF-04, inclusi giorni senza ospiti.
- **AC6.4**: file non valido non è scaricabile; l'utente vede errori per campo/giorno senza dati personali nei log.
- **AC6.5**: il download crea stato `Exported`, checksum e istruzioni; non crea `Accepted`.

### NF-07 — Export SPOT Online Puglia · must

Come gestore di struttura ricettiva pugliese voglio scaricare l'XML da caricare nel DMS/SPOT.

- **AC7.1**: XML 1.0 UTF-8 conforme agli XSD `movimentogiornaliero-0.6` e `datatype-0.6`, radice `movimenti` e vendor
  CasaZen.
- **AC7.2**: ogni giorno è `MP`, `NM` o `EC`; la sequenza è continua e contiene `datistruttura` quando richiesto.
- **AC7.3**: gli arrivi rappresentano singolo/capofamiglia/capogruppo e componenti con identificatori PMS non-PII.
- **AC7.4**: il flusso delle locazioni SPOT Easy non usa questo XML e rimane esplicitamente manuale.

### NF-08 — Adapter diretti certificabili · must

Come host voglio inviare direttamente solo verso integrazioni collaudate e ricevere l'esito reale.

- **AC8.1**: un adapter dichiara portale, versione e capacità; `SubmitAsync` non è esposto quando supporta solo export.
- **AC8.2**: Sinfonia usa CUSR/API key; Sicilia usa credenziale PMS per struttura, token breve e XML; ROSS1000 SOAP è
  configurato per singolo ente, non globalmente.
- **AC8.3**: test di contratto girano contro ambiente finto basato sugli artefatti ufficiali; attivazione produzione
  richiede un conformance test registrato per ente/versione.
- **AC8.4**: retry solo per errori transienti, timeout e circuit breaker; rifiuti applicativi richiedono correzione.
- **AC8.5**: redirect fuori allowlist, TLS non valido o host non ufficiale bloccano l'invio.
- **AC8.6**: sono implementati nello stesso programma Sinfonia REST, Sicilia API, ROSS1000 SOAP v2.4 e la porta STU;
  la produzione per ente resta feature-flagged finché manca il relativo collaudo.

### NF-09 — Esperienza operativa, scadenze e correzioni · must

Come host voglio completare il mese e correggere i rifiuti senza confondere l'adempimento con Alloggiati Web.

- **AC9.1**: pagina per immobile/mese mostra completezza giornaliera, submission, errori, ricevute e azione disponibile.
- **AC9.2**: etichette distinguono “Scaricato”, “Dichiarato inviato”, “Accettato dal portale” e “Rifiutato”.
- **AC9.3**: promemoria e scadenze provengono da configurazione territoriale versionata, in italiano e inglese.
- **AC9.4**: importi sanzionatori sono mostrati soltanto con fonte e territorio; in assenza si usa testo neutro.
- **AC9.5**: la UI non promette invio automatico per adapter manuali o non certificati.

### NF-10 — Percorso assistito universale · must

Come host di un territorio senza API pubblica voglio comunque completare e provare l'adempimento da CasaZen.

- **AC10.1**: CasaZen prepara per giorno/mese gli stessi totali e dettagli richiesti dal portale, con copia per campo,
  deep link ufficiale e istruzioni versionate per categoria di immobile.
- **AC10.2**: prima dell'apertura del portale valida completezza, codici, giorni a zero/chiusi e identifica i dati mancanti.
- **AC10.3**: al termine l'host registra data, esito e identificativo oppure allega la ricevuta; lo stato diventa
  `ManuallyDeclaredSent`, mai `Accepted`, salvo ricevuta verificabile.
- **AC10.4**: se l'amministrazione cambia modalità, una regola datata sostituisce la precedente senza alterare lo storico.
- **AC10.5**: nessun territorio mostra “non supportato”; se una fonte non è pubblica, mostra contatto dell'ente e crea
  un evento operativo `AuthoritySpecificationRequired` visibile all'amministrazione CasaZen.

## Modello e componenti attesi

- `TouristFlowPortalRule` o catalogo equivalente, versionato e riferito a territorio/autorità.
- `PropertyTouristFlowProfile`, separato dai dati Questura e dal CIN.
- estensione `StayGuest` per residenza, capo e occupazione letto.
- `PropertyDailyCapacity` per giorno locale.
- `TouristFlowSubmission` e `TouristFlowRecordResult`, entrambi `ITenantOwned`.
- `ITouristFlowAdapter`, resolver di capacità e serializer puri/versionati.
- controller/API host, schermata web mensile ed eventuali promemoria Hangfire.

I nomi finali sono decisione ADR; le proprietà semantiche e i vincoli sopra non sono opzionali.

## Piano di esecuzione obbligatorio per Helix

Le wave sono merge incrementali dello stesso programma, non scope opzionale. Helix può lavorare con server finti e
feature flag mentre attende accreditamenti; non può chiudere la feature finché NF-01–NF-10 e la matrice sono coperte.

### Wave 0 — ADR, catalogo nazionale e prerequisiti esterni

- ADR: dominio canonico, granularità della submission, correzioni e retention.
- ADR: credenziali per struttura, cifratura, allowlist e prova di accettazione.
- acquisire e versionare XSD/schema/esempi con URL, data e SHA-256.
- richiedere account test Campania, Sicilia e un ente ROSS1000 pilota.

### Wave 1 — dominio backend

- NF-01, NF-02, NF-03, NF-04 e NF-05;
- migrazioni generate solo con `dotnet ef`;
- API e audit tenant-scoped;
- prove minime su isolamento, cifratura e vincolo `Accepted`.

### Wave 2 — export e console

- NF-06 e NF-07 con golden files e validazione schema;
- NF-09 per completezza, download e stati;
- nessuna chiamata reale a enti.

### Wave 3 — invio diretto e fallback nazionale

- NF-08 Sinfonia e Sicilia in test;
- ROSS1000 SOAP implementato contro il contratto ufficiale e provato sul pilota;
- NF-10 e fixture E2E per tutti i territori;
- rollout per singolo ente/versione con feature flag spento fino al collaudo.

### Wave 4 — conformance nazionale

- ottenere e versionare tracciati non pubblici per WebTur, ABIT, Ricestat, Unicom, Turismatica, VIT, STU/DTU e ASTAT;
- promuovere ogni journey da assistita a file/diretta senza cambiare il dominio;
- report automatico che elenca copertura per comune, categoria, capacità, fonte, versione e data ultima verifica.

## Test expectations

- Unit test dei resolver, calendario, canonical hash e mapping codici.
- Integration test PostgreSQL per tenant isolation, cifratura write-only e constraint degli stati.
- Golden-file test per ogni serializer; validazione contro XSD/schema ufficiale.
- Contract test su server finto per timeout, retry, rifiuto parziale e risposta 2xx con errore applicativo.
- L2 frontend per mese completo/incompleto, export, errore di validazione e adapter manuale.
- L3 soltanto su ambiente test ufficiale, mai produzione, prima dell'abilitazione dell'invio diretto.
- Test parametrico sui codici ISTAT di tutti i comuni: nessun risultato nullo, ambiguo o fuori validità.
- E2E di almeno una locazione e una struttura ricettiva per ciascuna regola territoriale distinta.

## Regulatory, privacy and security gates

- confermare ruolo CasaZen come responsabile del trattamento, DPA, informativa e registro prima dell'invio diretto;
- payload nominativi cifrati: cancellazione automatica 90 giorni dopo esito definitivo; ricevute e audit minimizzati
  senza PII: 24 mesi; valori sovrascrivibili per obbligo territoriale documentato;
- vietare PII nei log e negli identificatori PMS;
- cifrare payload/risposte in storage privato e credenziali nel database;
- documentare minimizzazione campo per campo per ogni adapter;
- nessuna credenziale reale nei fixture, nelle demo o nelle CI.

## Attività esterne obbligatorie (non decisioni Helix)

- Accreditare CasaZen come software house presso Campania, Sicilia, gli enti ROSS1000 e ISPAT.
- Ottenere test account, endpoint, WSDL/XSD/OpenAPI, esempi di risposta e procedura di certificazione.
- Richiedere specifiche correnti a Umbria, PAT/ISPAT, Insiel FVG, Provincia di Sondrio, ASTAT, Valle d'Aosta,
  amministrazione Molise e gestori Ricestat/Unicom.
- Validazione legale/privacy prima di abilitare l'invio diretto.

## Out of scope

- generazione MOV/C o invio diretto a ISTAT/INDATA;
- automazione di SPID, CIE, CNS o scraping dei portali;
- invio Alloggiati Web e imposta di soggiorno, pur riusando i dati compatibili;
- adapter costruiti da reverse engineering o sole guide commerciali;
- attivazione production senza test per ente;
- custodia di credenziali personali di identità digitale.

## Decisioni chiuse per Helix

- La UI completa NF-09 e il percorso assistito NF-10 fanno parte della delivery.
- Emilia-Romagna è il baseline tecnico ROSS1000; l'attivazione resta configurata per singolo ente.
- La retention di default è quella definita nel gate privacy sopra e deve essere implementata con job verificabile.
- L'invio diretto è nello stesso programma; gli account esterni governano solo la feature flag production.
- La feature è completata soltanto quando ogni comune/categoria risolve una journey eseguibile e verificata.
