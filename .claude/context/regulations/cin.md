# Codice CIN (Codice Identificativo Nazionale)

## Riferimento Normativo
- **Fonte primaria**: D.L. 18/10/2023 n. 145, convertito in L. 15/12/2023 n. 191 (art. 13-ter)
- **Decreto attuativo**: D.M. Ministero Turismo prot. n. 0016726/24 del 03/09/2024
- **Entrata in vigore**: 01/01/2025 (scadenza richiesta: 01/03/2026 per operatori esistenti)

## Sintesi dell'Obbligo

Il Codice Identificativo Nazionale (CIN) è un codice univoco obbligatorio per:
- Tutte le strutture ricettive turistiche
- Tutti gli immobili destinati a locazione breve o turistica

Ogni unità immobiliare deve essere registrata nella **Banca Dati delle Strutture Ricettive (BDSR)** del Ministero del Turismo.

## Scadenze
- **01/01/2025**: Obbligo in vigore
- **01/03/2026**: Scadenza per operatori già attivi
- **60 giorni** dall'inizio attività per nuovi operatori

## Modalità di Richiesta
1. Accesso al portale BDSR: https://www.ministeroturismo.gov.it/banca-dati-strutture-ricettive/
2. Autenticazione tramite SPID o CIE
3. Inserimento dati struttura e documentazione:
   - **Strutture ricettive**: SCIA presentata al SUAP
   - **Locazioni turistiche non imprenditoriali**: comunicazione comunale (ante 02/11/2024)
   - **Locazioni turistiche imprenditoriali**: comunicazione comunale (post 02/11/2024)
4. Rilascio CIN da parte del Ministero

## Obblighi di Esposizione

### Esposizione Fisica
Il CIN deve essere esposto all'esterno dell'immobile in modo **visibile e leggibile**:
- Sulla targa esterna
- Sul citofono
- All'ingresso della proprietà

### Esposizione Digitale
Il CIN deve essere **obbligatoriamente** inserito in:
- Tutti gli annunci online
- Tutte le piattaforme OTA (Airbnb, Booking.com, Expedia, VRBO, etc.)
- Qualsiasi materiale promozionale o pubblicitario

## Sanzioni
- **Assenza CIN o mancata esposizione**: da €800 a €8.000 per immobile
- **CIN non esposto negli annunci online**: sanzioni amministrative aggiuntive
- Le sanzioni si applicano per ogni singola violazione

## Impatto su CasaZen

### Funzionalità Coinvolte
1. **Property Management**
   - Campo obbligatorio `CINCode` nell'entità Property
   - Validazione formato CIN
   - Controllo presenza CIN prima di pubblicazione annunci

2. **OTA Integration**
   - Sincronizzazione automatica CIN verso piattaforme OTA
   - Verifica presenza CIN in listing esistenti
   - Alert per immobili senza CIN valido

3. **Compliance Dashboard**
   - Monitor scadenze CIN per proprietà
   - Alert per immobili vicini alla scadenza 01/03/2026
   - Report proprietà non conformi

4. **Onboarding Nuove Proprietà**
   - Workflow guidato per richiesta CIN
   - Checklist documentazione necessaria
   - Link diretto al portale BDSR

### Criticità Tecniche
- **Validazione formato**: il formato del CIN non è standardizzato pubblicamente (da verificare)
- **Aggiornamenti**: necessità di mantenere allineamento con BDSR
- **Scadenza imminente**: molti proprietari potrebbero non aver ancora richiesto il CIN

## Sorgenti
- [Regione Toscana - CIN](https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche)
- [Lodgify - Guida CIN 2026](https://www.lodgify.com/blog/it/codice-cin-affitti-brevi/)
- [PraticheCasa - CIN 2026](https://www.pratichecasa.it/parere-di-un-esperto/cin-alloggi-turistici-2026/)

**Data consultazione**: 2026-03-27
