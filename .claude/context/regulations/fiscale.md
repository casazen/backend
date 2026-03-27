# Regime Fiscale Affitti Brevi - Cedolare Secca

## Riferimento Normativo
- **Fonte primaria**: L. 30/12/2025 n. 199 (Legge di Bilancio 2026)
- **Normativa base**: D.L. 50/2017 (cedolare secca affitti brevi)
- **Entrata in vigore modifiche**: 01/01/2026

## Sintesi dell'Obbligo

Dal 1° gennaio 2026 il regime fiscale per gli affitti brevi è stato modificato con una **riduzione della soglia** per l'applicabilità della cedolare secca.

### Novità Principale 2026
**Partita IVA obbligatoria dal 3° immobile** (in precedenza dal 5° immobile)

## Regime Fiscale Applicabile

### Opzione 1: Cedolare Secca (fino a 2 immobili)
Applicabile solo se si affittano **massimo 2 immobili** nell'anno fiscale.

**Aliquote:**
- **21%** per il primo immobile scelto dal contribuente
- **26%** per il secondo immobile in locazione breve

**Caratteristiche:**
- Imposta sostitutiva di IRPEF e addizionali
- Elimina imposta di registro e bollo sul contratto
- Impedisce aggiornamenti annuali del canone
- Applicazione opzionale (si può optare per tassazione ordinaria)

**Definizione "locazione breve":**
- Durata massima: **30 giorni**
- Immobili a uso residenziale
- Possibilità di fornire servizi accessori (biancheria, pulizie)

### Opzione 2: Attività d'Impresa (da 3 immobili)
Dal **terzo immobile** in affitto breve, l'attività è considerata **imprenditoriale**.

**Obblighi:**
- Apertura **Partita IVA** obbligatoria
- Tassazione come reddito d'impresa
- Regime fiscale: ordinario o forfettario (se requisiti soddisfatti)
- Obbligo fatturazione elettronica
- Contabilità ordinata

**Aliquote imposta (regime ordinario):**
- Aliquote IRPEF progressive (23% - 43%)
- Addizionali regionali e comunali
- IRAP se superata soglia ricavi

**Regime forfettario (se ammissibile):**
- Imposta sostitutiva 15% (5% primi 5 anni start-up)
- Soglia ricavi: €85.000/anno
- Esenzione IVA
- Contabilità semplificata

### Ritenuta Fiscale OTA
Dal 2024 le piattaforme OTA (Airbnb, Booking.com, etc.) applicano una **ritenuta fiscale del 21%** sui compensi erogati ai proprietari italiani, a titolo di acconto.

Questa ritenuta:
- È un acconto sull'imposta dovuta
- Viene recuperata in dichiarazione dei redditi
- Non si applica a chi ha partita IVA e fattura direttamente

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **Property Management**
   - Campo `FiscalRegime` nell'entità Property:
     - `CedolareSecca21` (primo immobile)
     - `CedolareSecca26` (secondo immobile)
     - `PartitaIVA` (3+ immobili o scelta imprenditoriale)
   - Tracking numero immobili per proprietario
   - Alert automatico al superamento soglia 2 immobili

2. **Payment Processing**
   - Calcolo ritenuta 21% per pagamenti OTA
   - Gestione acconti fiscali
   - Registrazione ritenute subite per dichiarazione redditi

3. **Reporting Fiscale**
   - Report annuale redditi per cedolare secca
   - Report ritenute subite da OTA
   - Certificazione Unica (CU) per ospiti/intermediari
   - Preparazione dati per dichiarazione redditi

4. **Compliance Dashboard**
   - Monitor numero immobili per proprietario
   - Alert superamento soglia cedolare secca
   - Suggerimento apertura partita IVA
   - Calcolo imposte dovute (simulazione)

5. **Owner Portal**
   - Wizard selezione regime fiscale ottimale
   - Simulatore impatto fiscale (cedolare vs. impresa)
   - Documentazione e guide fiscali
   - Link a commercialisti/consulenti partner

### Criticità Tecniche
- **Conteggio immobili**: necessità di contare immobili per anno fiscale, non totali
- **Cambio regime infrannuale**: gestire transizione se proprietario supera soglia mid-year
- **Ritenute OTA**: alcune piattaforme potrebbero non applicare correttamente la ritenuta
- **Dichiarazione redditi**: CasaZen non può sostituire commercialista, ma può preparare dati

### Dati da Tracciare
Per ogni proprietario:
- Numero immobili attivi per anno fiscale
- Regime fiscale scelto per ciascun immobile
- Ritenute subite da OTA (per recupero in dichiarazione)
- Redditi lordi per immobile
- Spese deducibili (se regime impresa)

## Sorgenti
- [FISCOeTASSE - Cedolare secca 2026](https://www.fiscoetasse.com/new-rassegna-stampa/2970-cedolare-secca-affitti-brevi-le-novita-dal-2026.html)
- [Facile.it - Cedolare secca affitti brevi 2026](https://www.facile.it/mutui/news/cedolare-secca-affitti-brevi-2026.html)
- [Studio Mazzocchi - Affitti brevi 2026](https://studiomazzocchi.it/affitti-brevi-2026-normativa-cedolare-secca-e-partita-iva/)

**Data consultazione**: 2026-03-27
