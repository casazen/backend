---
id: US-ISTAT-01
slug: istat-flussi-regionali
title: Flussi turistici regionali — export verificati e invio diretto
phase: post-MVP
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

**Obiettivo della prima delivery:** dominio comune, calendario e completezza dati, resolver territoriale, audit delle
trasmissioni ed export ufficialmente documentati. Gli invii diretti entrano solo quando CasaZen dispone di ambiente test,
credenziali fittizie e collaudo dell'ente.

## Decisioni vincolanti

1. Nessun adapter chiamato `ISTAT`: gli adapter rappresentano il portale destinatario e una versione di protocollo.
2. `Accepted` significa ricevuta o esito applicativo persistito. Un file generato è `Exported`; HTTP 2xx da solo non basta.
3. Nessuna automazione del browser, SPID, CIE o CNS. Dove manca una API si offre export o assistenza manuale.
4. Non riusare `AlloggiatiWebReport`: scopo, destinatario, granularità e prova di invio sono differenti.
5. Riutilizzare `StayGuest`, `AlloggiatiCodeEntry`, `Property.ComuneIstatCode`, `RegionCode` e la cifratura esistente.
6. Portale e autorità devono essere risolti per comune/provincia e validità temporale, non dalla sola Regione.
7. Funzionalità spenta per default e attivata per singolo adapter/ente dopo conformance test.
8. Mai mostrare una sanzione nazionale unica; testi, scadenze e importi sono contenuti territoriali versionati.

## Perimetro per capacità

| Famiglia | Prima delivery | Invio diretto successivo | Blocco esterno |
|---|---|---|---|
| ROSS1000/Turismo5 | export XML v2.4, validazione e audit | SOAP su un ente pilota, poi rollout per ente | WSDL/endpoint/utenza test e collaudo |
| Sinfonia Campania | contratto adapter e secret API key | REST con CUSR + API key | account software house e ambiente test |
| Sicilia | contratto adapter e fixture XML | API HTTPS/token/XML | credenziali PMS test e XSD aggiornati |
| SPOT Puglia strutture | export XML UTF-8 con XSD 0.6 | non previsto dalla documentazione pubblica | nessuno per export |
| SPOT Easy locazioni | guida/stato manuale | non documentato | protocollo PMS ufficiale |
| Turismatica Umbria | porta solo-export | da valutare | tracciato TXT ufficiale corrente |
| STU Trento | porta file/WS | dopo test di congruenza ISPAT | specifica e accreditamento |
| DTU Trento | manuale | non documentato | protocollo PMS ufficiale |
| Toscana/Ricestat | resolver territoriale | adapter separato | contratti per Provincia |
| WebTur FVG | manuale/porta disabilitata | da valutare | specifica Insiel ufficiale |
| ABIT Sondrio | manuale/porta disabilitata | da valutare | specifica Provincia |
| Molise, Valle d'Aosta, Bolzano | `unverified/manual` | da valutare | verifica amministrativa e tecnica |

## User stories e acceptance criteria

### NF-01 — Profilo territoriale e resolver del portale · must

Come host voglio che CasaZen individui il percorso applicabile al mio immobile senza assumere che una Regione abbia un
solo portale.

- **AC1.1**: dato un immobile con comune ISTAT e una data, il resolver restituisce autorità, portale, versione protocollo
  e capacità (`ManualEntry`, `FileExport`, `DirectSubmit`) della regola temporalmente valida.
- **AC1.2**: per Provincia di Sondrio il resolver restituisce ABIT e non il canale host ROSS1000.
- **AC1.3**: per la Toscana il risultato dipende almeno dalla Provincia; una regola assente o ambigua produce
  `portal_configuration_unverified`, mai un fallback silenzioso.
- **AC1.4**: Molise, Valle d'Aosta e Bolzano restano manuali/non verificati finché non viene caricata una fonte approvata.
- **AC1.5**: ogni regola conserva URL fonte, data di verifica, validità e versione; l'admin può disattivarla senza deploy.

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

### NF-08 — Adapter diretti certificabili · should, gated

Come host voglio inviare direttamente solo verso integrazioni collaudate e ricevere l'esito reale.

- **AC8.1**: un adapter dichiara portale, versione e capacità; `SubmitAsync` non è esposto quando supporta solo export.
- **AC8.2**: Sinfonia usa CUSR/API key; Sicilia usa credenziale PMS per struttura, token breve e XML; ROSS1000 SOAP è
  configurato per singolo ente, non globalmente.
- **AC8.3**: test di contratto girano contro ambiente finto basato sugli artefatti ufficiali; attivazione produzione
  richiede un conformance test registrato per ente/versione.
- **AC8.4**: retry solo per errori transienti, timeout e circuit breaker; rifiuti applicativi richiedono correzione.
- **AC8.5**: redirect fuori allowlist, TLS non valido o host non ufficiale bloccano l'invio.

### NF-09 — Esperienza operativa, scadenze e correzioni · should

Come host voglio completare il mese e correggere i rifiuti senza confondere l'adempimento con Alloggiati Web.

- **AC9.1**: pagina per immobile/mese mostra completezza giornaliera, submission, errori, ricevute e azione disponibile.
- **AC9.2**: etichette distinguono “Scaricato”, “Dichiarato inviato”, “Accettato dal portale” e “Rifiutato”.
- **AC9.3**: promemoria e scadenze provengono da configurazione territoriale versionata, in italiano e inglese.
- **AC9.4**: importi sanzionatori sono mostrati soltanto con fonte e territorio; in assenza si usa testo neutro.
- **AC9.5**: la UI non promette invio automatico per adapter manuali o non certificati.

## Modello e componenti attesi

- `TouristFlowPortalRule` o catalogo equivalente, versionato e riferito a territorio/autorità.
- `PropertyTouristFlowProfile`, separato dai dati Questura e dal CIN.
- estensione `StayGuest` per residenza, capo e occupazione letto.
- `PropertyDailyCapacity` per giorno locale.
- `TouristFlowSubmission` e `TouristFlowRecordResult`, entrambi `ITenantOwned`.
- `ITouristFlowAdapter`, resolver di capacità e serializer puri/versionati.
- controller/API host, schermata web mensile ed eventuali promemoria Hangfire.

I nomi finali sono decisione ADR; le proprietà semantiche e i vincoli sopra non sono opzionali.

## Sequenza consigliata per Helix

### Wave 0 — ADR e prerequisiti esterni

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

### Wave 3 — invio diretto

- NF-08 Sinfonia e Sicilia in test;
- ROSS1000 SOAP solo dopo il pilota;
- rollout per singolo ente/versione con feature flag spento di default.

## Test expectations

- Unit test dei resolver, calendario, canonical hash e mapping codici.
- Integration test PostgreSQL per tenant isolation, cifratura write-only e constraint degli stati.
- Golden-file test per ogni serializer; validazione contro XSD/schema ufficiale.
- Contract test su server finto per timeout, retry, rifiuto parziale e risposta 2xx con errore applicativo.
- L2 frontend per mese completo/incompleto, export, errore di validazione e adapter manuale.
- L3 soltanto su ambiente test ufficiale, mai produzione, prima dell'abilitazione dell'invio diretto.

## Regulatory, privacy and security gates

- confermare ruolo CasaZen come responsabile del trattamento, DPA, informativa e registro prima dell'invio diretto;
- definire retention di payload, risposte e ricevute separatamente dai dati Alloggiati;
- vietare PII nei log e negli identificatori PMS;
- cifrare payload/risposte in storage privato e credenziali nel database;
- documentare minimizzazione campo per campo per ogni adapter;
- nessuna credenziale reale nei fixture, nelle demo o nelle CI.

## Manual follow-up

- Accreditare CasaZen come software house presso Campania, Sicilia e l'ente ROSS1000 pilota.
- Ottenere test account, endpoint, WSDL/XSD/OpenAPI, esempi di risposta e procedura di certificazione.
- Richiedere specifiche correnti a Umbria, PAT/ISPAT, Insiel FVG, Provincia di Sondrio e gestori Ricestat.
- Validazione legale/privacy prima di abilitare l'invio diretto.

## Out of scope

- generazione MOV/C o invio diretto a ISTAT/INDATA;
- automazione di SPID, CIE, CNS o scraping dei portali;
- invio Alloggiati Web e imposta di soggiorno, pur riusando i dati compatibili;
- adapter costruiti da reverse engineering o sole guide commerciali;
- rollout nazionale simultaneo senza test per ente;
- custodia di credenziali personali di identità digitale.

## Open questions for Helix intake

1. La prima delivery deve includere solo ROSS1000 + SPOT export oppure anche la UI completa NF-09?
   Default proposto: includere la UI minima necessaria a rendere verificabili completezza, download e stati.
2. Quale ente ROSS1000 sarà il pilota contrattuale? Default: Emilia-Romagna per la specifica pubblica; la scelta
   dell'ambiente reale resta bloccante per Wave 3, non per dominio/export.
3. Durata di conservazione di payload e ricevute? Decisione legale/privacy bloccante prima della produzione; per sviluppo
   usare configurazione senza cancellazione automatica e dati esclusivamente fittizi.
4. La fase di invio diretto è autorizzata nello stesso run Helix? Default: no; decomporla come wave gated che resta
   bloccata finché gli account test e la conferma privacy non sono registrati.
