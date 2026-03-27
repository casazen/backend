# Normativa OTA e Intermediari - DAC7

## Riferimento Normativo
- **Fonte europea**: Direttiva UE 2021/514 (DAC7 - Directive on Administrative Cooperation)
- **Recepimento italiano**: D.Lgs. 01/03/2023 n. 32
- **Regolamento UE affitti brevi**: Regolamento UE 2024/1028
- **Entrata in vigore piena**: 20/05/2026

## Sintesi dell'Obbligo

### DAC7 - Scambio Automatico Informazioni

La direttiva DAC7 introduce l'**obbligo per le piattaforme digitali** (OTA) di comunicare dati dettagliati sulle transazioni economiche facilitate alle autorità fiscali.

**Obiettivo**: aumentare la trasparenza fiscale nell'economia digitale e contrastare l'evasione.

## Ambito di Applicazione DAC7

### Piattaforme Coinvolte
Gestori di piattaforme online che facilitano transazioni in:
- **Locazione immobili** (inclusi affitti brevi)
- Prestazione di servizi personali
- Vendita di beni
- Noleggio mezzi di trasporto

**Esempi**: Airbnb, Booking.com, Vrbo, Amazon, eBay, Uber, etc.

### Venditori Soggetti a Comunicazione
Venditori/host che:
- Sono residenti in Italia o altri Stati membri UE
- Forniscono servizi di locazione per immobili situati in Italia/UE
- **Non soggetti** se sotto soglie minime (vedere Soglie)

## Soglie di Esclusione
Le piattaforme **non devono comunicare** dati di venditori che nell'anno effettuano:
- **Meno di 30 transazioni**, E
- **Compensi complessivi < €2.000**

Se si superano entrambe le soglie, scatta l'obbligo di comunicazione.

## Dati Comunicati dalle OTA
Per ogni venditore/host soggetto, le piattaforme devono comunicare:
- Dati identificativi (nome, cognome/ragione sociale, indirizzo, partita IVA/CF)
- Numero di transazioni
- Compensi complessivi erogati
- Commissioni trattenute dalla piattaforma
- Dati immobile (indirizzo, codice catastale per locazioni)
- Numero giorni locazione (per affitti brevi)

## Scadenze Comunicazione
- **31 gennaio** dell'anno successivo: comunicazione dati anno precedente all'Agenzia delle Entrate italiana
- **Scambio automatico** con altri Stati UE entro **2 mesi** dalla scadenza comunicazione

**Esempio**: dati 2025 → comunicazione entro 31/01/2026 → scambio UE entro 31/03/2026

**Eccezione 2026**: la scadenza per i dati 2025 slitta al **02/02/2026** (31/01 cade di sabato)

## Regolamento UE 2024/1028 - Affitti Brevi

### Novità Principale
Il Regolamento UE 2024/1028 introduce un **sistema armonizzato europeo** per la raccolta e condivisione dei dati sugli affitti brevi.

**Entrata in vigore piena**: **20 maggio 2026**

### Obiettivi
- Trasparenza e tracciabilità affitti brevi
- Coordinamento tra piattaforme, autorità nazionali e locali
- Contrasto evasione fiscale e abusivismo
- Monitoraggio impatto turistico

### Obblighi Piattaforme
- Registrazione presso autorità nazionali
- Comunicazione dati host e annunci
- Verifica codici identificativi (es. CIN in Italia)
- **Rimozione annunci** senza codice identificativo valido

### Coordinamento con CIN
A partire dal 20/05/2026, le piattaforme OTA dovranno:
1. Verificare presenza **CIN valido** in ogni annuncio
2. **Non pubblicare** annunci senza CIN
3. Comunicare automaticamente dati annunci alle autorità

Questo rafforza l'obbligo nazionale del CIN italiano.

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **OTA Integration**
   - **Verifica compliance piattaforme**: assicurare che OTA partner applichino correttamente DAC7
   - **Sincronizzazione CIN**: garantire invio CIN valido a tutte le piattaforme
   - **Monitoraggio pubblicazione**: alert se annuncio rimosso per CIN mancante (post 20/05/2026)

2. **Reporting Fiscale**
   - **Ricezione Certificazioni OTA**: importare dati comunicati da OTA (compensi, ritenute, transazioni)
   - **Riconciliazione**: verificare coerenza tra dati CasaZen e dati OTA
   - **Preparazione dichiarazione redditi**: fornire dati completi a proprietario/commercialista

3. **Owner Portal**
   - **Dashboard DAC7**: visualizzazione dati comunicati da OTA all'Agenzia Entrate
   - **Alert soglie**: notifica se si stanno avvicinando alle soglie (30 transazioni / €2.000)
   - **Documentazione**: guide su impatti fiscali e obblighi

4. **Compliance Dashboard**
   - **Verifica CIN pre-pubblicazione**: blocco pubblicazione su OTA se CIN mancante (post 20/05/2026)
   - **Monitor comunicazioni OTA**: tracking dati inviati da piattaforme
   - **Segnalazione anomalie**: discrepanze tra booking CasaZen e dati OTA

5. **Guest & Transaction Tracking**
   - **Tracking provenienza booking**: distinguere booking diretti vs. OTA
   - **Conteggio transazioni OTA**: monitor soglia 30 transazioni
   - **Calcolo compensi OTA**: monitor soglia €2.000

### Criticità Tecniche
- **Ricezione dati OTA**: non tutte le piattaforme forniscono API per scaricare certificazioni fiscali
- **Formati eterogenei**: ogni OTA potrebbe avere formato dati diverso
- **Riconciliazione complessa**: difficile mappare transazione OTA → booking CasaZen (codici diversi)
- **Tempistiche**: dati OTA arrivano a gennaio anno successivo, utili solo per dichiarazione redditi
- **CIN enforcement**: dal 20/05/2026 OTA bloccheranno annunci senza CIN, impatto immediato

### Dati da Tracciare
Per ogni proprietario:
- Numero transazioni OTA nell'anno fiscale (per soglia 30)
- Compensi totali OTA nell'anno (per soglia €2.000)
- Ritenute fiscali applicate da OTA
- Commissioni OTA (deducibili fiscalmente in alcuni regimi)

Per ogni booking da OTA:
- Piattaforma di provenienza
- Codice transazione OTA
- Importo lordo e netto (al netto commissioni)
- Ritenuta fiscale applicata

Per ogni proprietà:
- CIN presente e valido (obbligatorio per pubblicazione OTA post 20/05/2026)
- Piattaforme su cui è pubblicata
- Stato sincronizzazione CIN per piattaforma

## Workflow Ottimale

### Pre-pubblicazione (dal 20/05/2026)
1. **Verifica CIN**: bloccare pubblicazione su OTA se CIN mancante
2. **Sincronizzazione**: inviare CIN a tutte le piattaforme collegate
3. **Conferma**: verificare che OTA abbiano accettato il CIN

### Durante l'anno
1. **Tracking transazioni**: conteggiare transazioni e compensi OTA per proprietario
2. **Alert soglie**: notificare se si avvicina a 30 transazioni o €2.000 (prossimità comunicazione DAC7)

### Fine anno (gennaio anno successivo)
1. **Ricezione certificazioni OTA**: importare dati da Airbnb, Booking.com, etc.
2. **Riconciliazione**: verificare coerenza
3. **Generazione report fiscale**: preparare documento per dichiarazione redditi

## Standard OCSE
La DAC7 si basa sui "Model Rules for Reporting by Platform Operators" dell'OCSE (2020-2021), che stabiliscono standard internazionali comuni per il reporting dell'economia digitale.

Questo garantisce coerenza tra diversi Paesi e facilita lo scambio automatico di informazioni fiscali.

## Sorgenti
- [Agenzia delle Entrate - DAC7](https://www.agenziaentrate.gov.it/portale/scambio-automatico-di-informazioni-comunicate-dai-gestori-di-piattaforme-dac7/infogen-scambio-automatico-di-informazioni-comunicate-dai-gestori-di-piattaforme-dac7-imprese)
- [FidoCommercialista - DAC7 2026](https://fidocommercialista.it/dac7)
- [FISCOeTASSE - DAC7 economia digitale](https://www.fiscoetasse.com/approfondimenti/16928-dac7-la-nuova-frontiera-della-trasparenza-fiscale-nelleconomia-digitale.html)
- [Tiburzi Bardelli - DAC7 scadenza 2026](https://www.tiburzibardelli.it/dac7-comunicazione-dei-dati-in-scadenza-il-2-febbraio-2026/)

**Data consultazione**: 2026-03-27
