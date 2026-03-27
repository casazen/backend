# Gap Analysis Report - 27 Marzo 2026

## Riepilogo Esecutivo

Analisi completa della conformità normativa del sistema CasaZen rispetto agli obblighi vigenti per affitti brevi in Italia.

**Data analisi**: 2026-03-27
**Versione codebase**: commit 98dbd15
**Analyst**: Analyzer Agent (regulatory intelligence)

---

## Riepilogo Gap per Priorità

| Priorità | Gap Identificati | Stato Implementazione |
|----------|------------------|----------------------|
| **CRITICAL** | 1 | MISSING |
| **HIGH** | 2 | MISSING |
| **MEDIUM** | 3 | MISSING |
| **LOW** | 2 | MISSING |
| **COMPLIANT** | 0 | N/A |

**Totale Gap**: 8
**Totale Funzionalità Compliant**: 0

---

## Dettaglio Gap

### CRITICAL - Comunicazione Alloggiati Web

#### Requisito Normativo
Obbligo di comunicare alla Questura i dati di tutti gli ospiti entro **24 ore dall'arrivo** (art. 109 TULPS).

**Riferimento**: Art. 109 TULPS, D.M. 07/01/2013
**Scadenza**: Obbligo permanente
**Sanzioni**: Sanzioni **penali** (contravvenzione)

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Entità `Guest` manca campi obbligatori:
  - Data e luogo di nascita
  - Cittadinanza
  - Documento identità (tipo, numero, scadenza)
  - Scansione/foto documento
- ✗ Nessun connettore per Alloggiati Web
- ✗ Nessun portale regionale integrato
- ✗ Nessun tracking comunicazioni effettuate
- ✗ Nessun alert scadenza 24h

#### Cosa Manca
1. **Guest Entity Enhancement**
   - Aggiungere campi anagrafici completi
   - Campo documento identità con validazione
   - Upload e storage sicuro scan documento
   - Campo cittadinanza e residenza completa

2. **Alloggiati Integration Service**
   - Connettore API Alloggiati Web (Polizia di Stato)
   - Connettori portali regionali (Toscana, Veneto, Puglia)
   - Mapping dati Guest → formato Alloggiati
   - Gestione credenziali Questura per proprietario

3. **Check-In Digitale**
   - Workflow self check-in ospiti
   - Form compilazione dati + upload documento
   - Validazione completezza dati pre-arrivo
   - OCR documento identità (opzionale)

4. **Compliance Monitoring**
   - Tracking comunicazioni effettuate vs. prenotazioni
   - Alert urgente per scadenza 24h
   - Report comunicazioni mancanti/in ritardo
   - Storico comunicazioni con numero protocollo

#### Impatto
🚨 **CRITICO** - Sanzioni penali personali a carico del gestore. Responsabilità non delegabile. Il proprietario rischia contravvenzione per ogni comunicazione omessa o tardiva.

#### Suggerimento Implementazione
**Priorità 1**: Implementare raccolta dati completi Guest + alert pre-arrivo
**Priorità 2**: Integrare API Alloggiati Web con fallback manuale
**Priorità 3**: Self check-in digitale per ospiti

**Stima complessità**: ALTA (integrazione API pubblica, gestione documenti, compliance GDPR)

---

### HIGH - Gestione Codice CIN

#### Requisito Normativo
Codice Identificativo Nazionale (CIN) obbligatorio per tutte le locazioni turistiche e strutture ricettive. Esposizione fisica all'esterno e digitale negli annunci.

**Riferimento**: D.L. 145/2023 conv. L. 191/2023 (art. 13-ter), D.M. 03/09/2024
**Scadenza**: **01/03/2026** per operatori esistenti (IMMINENTE!)
**Sanzioni**: €800 - €8.000 per immobile

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Entità `Property` manca campo `CINCode`
- ✗ Nessuna validazione formato CIN
- ✗ Nessun controllo presenza CIN prima pubblicazione annunci
- ✗ Nessuna sincronizzazione CIN verso OTA
- ✗ Nessun alert scadenza per proprietà senza CIN

#### Cosa Manca
1. **Property Entity Enhancement**
   - Campo `CINCode` (string, nullable inizialmente)
   - Campo `CINRequestedDate` e `CINApprovedDate`
   - Validazione formato CIN
   - Flag `HasValidCIN` per controlli rapidi

2. **CIN Validation Service**
   - Validazione formato CIN (da definire standard BDSR)
   - Verifica univocità CIN nel sistema
   - API call a BDSR per verifica validità (se disponibile)

3. **OTA Integration Enhancement**
   - Sincronizzazione automatica CIN verso tutte le piattaforme
   - Verifica presenza CIN in listing esistenti
   - Blocco pubblicazione annunci senza CIN valido

4. **Compliance Dashboard**
   - Lista proprietà senza CIN
   - Alert scadenza 01/03/2026 per immobili non conformi
   - Workflow guidato richiesta CIN (link BDSR, checklist documenti)
   - Report non-conformità per proprietario

#### Impatto
🔴 **ALTO** - Scadenza **imminente** (01/03/2026). Sanzioni significative (€800-€8.000 per immobile). OTA potrebbero rimuovere annunci non conformi.

#### Suggerimento Implementazione
**Priorità 1**: Aggiungere campo CIN a Property + validazione
**Priorità 2**: Dashboard conformità + alert scadenza
**Priorità 3**: Sincronizzazione automatica CIN verso OTA

**Stima complessità**: MEDIA (campo DB + validazione + sync OTA)

---

### HIGH - Regime Fiscale e Cedolare Secca

#### Requisito Normativo
Dal 01/01/2026, Partita IVA obbligatoria dal **3° immobile** (riduzione da 5°). Ritenuta fiscale 21% applicata da OTA. Cedolare secca 21% (primo immobile) / 26% (secondo immobile).

**Riferimento**: L. 199/2025 (Legge di Bilancio 2026), D.L. 50/2017
**Scadenza**: In vigore dal 01/01/2026
**Sanzioni**: Sanzioni fiscali per omessa dichiarazione redditi

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Entità `Property` manca campo `FiscalRegime`
- ✗ Nessun tracking numero immobili per proprietario
- ✗ Nessun alert superamento soglia 2 immobili
- ✗ Entità `Payment` manca tracking ritenuta 21% OTA
- ✗ Nessun report fiscale per dichiarazione redditi

#### Cosa Manca
1. **Property Entity Enhancement**
   - Enum `FiscalRegime`: CedolareSecca21 | CedolareSecca26 | PartitaIVA | RegimeOrdinario
   - Campo `TaxYear` per tracking anno fiscale

2. **Owner/User Entity Enhancement**
   - Campo `PropertiesCount` per anno fiscale
   - Campo `HasPartitaIVA` (bool)
   - Campo `PartitaIVANumber` (string, nullable)

3. **Payment Enhancement**
   - Campo `OtaWithholdingTax` (ritenuta 21%)
   - Campo `WithholdingTaxApplied` (bool)
   - Separazione canone vs. ritenuta

4. **Fiscal Service**
   - Calcolo automatico regime applicabile per proprietario
   - Alert superamento soglia 2 immobili
   - Tracking ritenute subite da OTA per recupero in dichiarazione

5. **Reporting Fiscale**
   - Report annuale redditi per cedolare secca
   - Report ritenute subite (per F24 e dichiarazione)
   - Export dati per commercialista

#### Impatto
🔴 **ALTO** - Obbligo già in vigore (01/01/2026). Rischio sanzioni fiscali per proprietari. Necessario per corretta gestione fiscale e dichiarazione redditi.

#### Suggerimento Implementazione
**Priorità 1**: Tracking regime fiscale + conteggio immobili per proprietario
**Priorità 2**: Gestione ritenuta 21% OTA in Payment
**Priorità 3**: Report fiscale per dichiarazione redditi

**Stima complessità**: MEDIA (logica fiscale, tracking multi-property)

---

### MEDIUM - Imposta di Soggiorno

#### Requisito Normativo
Tassa comunale applicata ai pernottamenti, riscossa dal gestore e versata al Comune. 1.409 comuni italiani applicano l'imposta con tariffe variabili.

**Riferimento**: D.Lgs. 23/2011, L. 199/2025
**Scadenza**: Versamenti mensili/trimestrali (varia per Comune)
**Sanzioni**: Sanzioni amministrative comunali

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Entità `Booking` manca campo imposta soggiorno
- ✗ Entità `Payment` non separa canone vs. imposta
- ✗ Nessun database tariffe comunali
- ✗ Nessun calcolo automatico imposta
- ✗ Nessun tracking versamenti al Comune

#### Cosa Manca
1. **Booking Enhancement**
   - Campo `TouristTaxAmount` (decimal)
   - Campo `TouristTaxExemptionApplied` (bool)
   - Campo `TouristTaxExemptionReason` (string)

2. **TouristTax Entity (nuova)**
   - Tabella tariffe per Comune
   - Configurazione esenzioni (minori età, residenti)
   - Scadenze versamenti per Comune
   - Modalità versamento (F24, bonifico, portale)

3. **TouristTax Service**
   - Calcolo automatico imposta per prenotazione
   - Logica esenzioni configurabile
   - Generazione report mensile/trimestrale
   - Preparazione dati per Modello 21 (dichiarazione annuale)

4. **Payment Enhancement**
   - Separazione contabile: `AccommodationAmount` + `TouristTaxAmount`
   - Campo `TouristTaxCollected` (data riscossione)
   - Campo `TouristTaxPaidToMunicipality` (data versamento)

5. **Compliance Dashboard**
   - Monitor imposta riscossa vs. versata
   - Alert scadenze versamenti per Comune
   - Report anomalie (mancati versamenti)

#### Impatto
🟡 **MEDIO** - Obbligo permanente con sanzioni amministrative. Rischio moderato ma diffuso (1.409 comuni). Necessario per trasparenza verso ospiti e corretto versamento.

#### Suggerimento Implementazione
**Priorità 1**: Database tariffe comunali + calcolo automatico
**Priorità 2**: Separazione contabile in Payment
**Priorità 3**: Report versamenti + Modello 21

**Stima complessità**: ALTA (1.409+ comuni, tariffe variabili, esenzioni complesse)

---

### MEDIUM - GDPR e Consent Management

#### Requisito Normativo
Il GDPR si applica a tutte le strutture ricettive. Obbligo di informativa, consensi, sicurezza dati, diritti interessati, data retention.

**Riferimento**: Reg. UE 2016/679, D.Lgs. 196/2003
**Scadenza**: Obbligo permanente (dal 25/05/2018)
**Sanzioni**: Fino a €20M o 4% fatturato globale

#### Stato Codebase
**PARTIAL** - Infrastruttura base presente, manca compliance GDPR

**Analisi**:
- ✓ Autenticazione JWT (Auth0) presente
- ✓ HTTPS (presumibile in produzione)
- ✗ Nessuna informativa privacy strutturata
- ✗ Nessun consent management per marketing
- ✗ Nessuna cifratura dati sensibili (scan documenti)
- ✗ Nessuna data retention policy
- ✗ Nessun portal diritti interessati (accesso, cancellazione)
- ✗ Nessun audit log accessi ai dati

#### Cosa Manca
1. **Guest/User Entity Enhancement**
   - Campo `PrivacyConsentDate` (datetime)
   - Campo `MarketingConsentDate` (datetime, nullable)
   - Campo `DataRetentionExpiryDate` (datetime)

2. **GdprConsent Entity (nuova)**
   - Tracking consensi per tipologia
   - Versione informativa accettata
   - Data consenso / revoca
   - IP address e user agent

3. **Document Storage Enhancement**
   - Cifratura scan documenti identità
   - Cifratura dati pagamento (se trattati)
   - Accesso controllato con audit log

4. **Data Retention Service**
   - Politiche conservazione automatiche:
     - Dati fiscali: 10 anni
     - Dati ospiti: 5 anni (alloggiati) poi cancellazione
     - Scan documenti: configurabile
   - Job schedulato cancellazione dati scaduti
   - Anonimizzazione per statistiche

5. **GDPR Portal**
   - Form richiesta accesso dati (export JSON/CSV)
   - Form richiesta cancellazione dati
   - Gestione consensi marketing
   - Tracking richieste e risposte (scadenza 1 mese)

6. **Audit Log**
   - Logging accessi ai dati personali
   - Tracciamento modifiche dati sensibili
   - Report per data breach (se necessario)

7. **Template & Documenti**
   - Informativa Privacy (italiano + inglese)
   - Modulo consenso marketing
   - Registro Trattamenti (art. 30)
   - Procedura data breach

#### Impatto
🟡 **MEDIO** - Obbligo permanente con sanzioni potenzialmente elevate. Rischio moderato per attività piccole (sanzioni proporzionate) ma essenziale per tutelare dati ospiti e proprietari.

#### Suggerimento Implementazione
**Priorità 1**: Informativa privacy + consent management
**Priorità 2**: Cifratura dati sensibili + data retention
**Priorità 3**: GDPR portal diritti interessati

**Stima complessità**: ALTA (cifratura, audit log, compliance legale)

---

### MEDIUM - Reportistica ISTAT

#### Requisito Normativo
Comunicazione mensile aggregata ad ISTAT: numero arrivi, presenze, nazionalità ospiti.

**Riferimento**: Normativa ISTAT turismo
**Scadenza**: Mensile (varia per regione)
**Sanzioni**: Sanzioni amministrative

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Nessun tracking nazionalità ospiti
- ✗ Nessun report aggregato arrivi/presenze
- ✗ Nessuna integrazione portali ISTAT

#### Cosa Manca
1. **Guest Enhancement**
   - Campo `Nationality` (string, ISO 3166-1 alpha-2)

2. **ISTAT Reporting Service**
   - Aggregazione mensile arrivi/presenze per nazionalità
   - Export formato ISTAT (CSV, XML)
   - Storico comunicazioni effettuate

3. **Compliance Dashboard**
   - Alert scadenza comunicazione ISTAT
   - Report mensile pronto per invio

#### Impatto
🟡 **MEDIO** - Obbligo permanente con sanzioni amministrative. Rischio moderato ma necessario per statistiche turistiche nazionali.

#### Suggerimento Implementazione
**Priorità 1**: Campo nazionalità Guest + report aggregato
**Priorità 2**: Export formato ISTAT
**Priorità 3**: Integrazione portali regionali (se disponibile)

**Stima complessità**: BASSA (report aggregato, export CSV)

---

### LOW - Sicurezza e Requisiti Strutturali

#### Requisito Normativo
Obblighi regionali/comunali per sicurezza: rilevatori fumo, estintori, segnaletica emergenza.

**Riferimento**: D.L. 145/2023, normative regionali
**Scadenza**: Varia per regione
**Sanzioni**: Sanzioni amministrative regionali

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Nessun campo per tracking requisiti sicurezza
- ✗ Nessuna checklist conformità strutturale

#### Cosa Manca
1. **Property Enhancement**
   - Campo `SafetyComplianceChecklist` (JSON)
   - Flag `HasSmokeDetector`, `HasFireExtinguisher`, etc.
   - Data ultima verifica sicurezza

2. **Compliance Service**
   - Checklist conformità per regione
   - Alert manutenzioni periodiche
   - Report non-conformità

#### Impatto
🟢 **BASSO** - Obbligo permanente ma verifica fisica non gestibile via software. CasaZen può solo tracciare la conformità autodichiarata.

#### Suggerimento Implementazione
**Priorità 3**: Checklist conformità + tracking verifica

**Stima complessità**: BASSA (campi boolean, checklist)

---

### LOW - Normativa Regionale

#### Requisito Normativo
Leggi regionali specifiche per affitti brevi (es. Emilia-Romagna, Toscana, Veneto).

**Riferimento**: Leggi regionali turismo
**Scadenza**: Varia per regione
**Sanzioni**: Sanzioni amministrative regionali

#### Stato Codebase
**MISSING** - Funzionalità completamente assente

**Analisi**:
- ✗ Nessuna configurazione regionale specifica
- ✗ Nessun alert obblighi regionali

#### Cosa Manca
1. **Regional Compliance Service**
   - Database obblighi per regione
   - Alert specifici per ubicazione proprietà
   - Checklist conformità regionale

#### Impatto
🟢 **BASSO** - Eterogeneità elevata, difficile standardizzare. Approccio best-effort con alert informativi.

#### Suggerimento Implementazione
**Priorità 3**: Database obblighi regionali + alert informativi

**Stima complessità**: MEDIA (eterogeneità normativa)

---

## Riepilogo Codebase Map Update

### Funzionalità NON Implementate (Confermate)
- [x] Gestione Codice CIN (campo, validazione, esposizione) - **HIGH**
- [x] Comunicazione alloggiati web (integrazione Questura) - **CRITICAL**
- [x] Calcolo e versamento imposta di soggiorno - **MEDIUM**
- [x] Gestione cedolare secca / ritenuta 21% OTA - **HIGH**
- [x] Reportistica fiscale automatizzata - **MEDIUM**
- [x] Consent management GDPR per ospiti - **MEDIUM**
- [x] Gestione documenti identità ospiti - **CRITICAL** (per alloggiati)
- [x] Scadenzario obblighi normativi - **MEDIUM**
- [x] Dashboard compliance - **MEDIUM**

### Nuove Funzionalità da Aggiungere
- [ ] Reportistica ISTAT mensile - **MEDIUM**
- [ ] Checklist sicurezza strutturale - **LOW**
- [ ] Database obblighi regionali - **LOW**

---

## Azioni Raccomandate

### Immediate (Priorità CRITICAL)
1. ✅ **Comunicazione Alloggiati Web**
   - Estendere entità Guest con campi obbligatori
   - Implementare self check-in digitale
   - Integrare API Alloggiati Web + portali regionali
   - Alert scadenza 24h

### Breve Termine (Priorità HIGH) - Entro 01/03/2026
2. ✅ **Gestione CIN**
   - Aggiungere campo CIN a Property
   - Validazione formato
   - Sincronizzazione OTA
   - Dashboard conformità + alert scadenza

3. ✅ **Regime Fiscale**
   - Tracking regime fiscale per proprietario
   - Gestione ritenuta 21% OTA
   - Report fiscale annuale

### Medio Termine (Priorità MEDIUM)
4. **Imposta di Soggiorno**
5. **GDPR Compliance**
6. **Reportistica ISTAT**

### Lungo Termine (Priorità LOW)
7. Sicurezza strutturale
8. Normativa regionale

---

## Note Tecniche

### Complessità Implementazione
- **ALTA**: Comunicazione Alloggiati (integrazioni API pubblica), Imposta Soggiorno (1.409 comuni), GDPR (cifratura, audit log)
- **MEDIA**: CIN (sync OTA), Regime Fiscale (logica multi-property)
- **BASSA**: ISTAT (report aggregato), Sicurezza (checklist)

### Dipendenze Esterne
- **API Alloggiati Web**: disponibilità limitata, potrebbe richiedere fallback manuale
- **Portali regionali**: eterogeneità tecnologica
- **BDSR (CIN)**: API di verifica validità CIN (da verificare disponibilità)
- **Tariffe comunali**: fonte dati da definire (manuale o API terze parti)

### Rischi
- **Scadenza CIN imminente** (01/03/2026): urgente implementazione
- **Sanzioni penali alloggiati**: priorità massima
- **Complessità GDPR**: necessario supporto legale (DPO)

---

## Conclusioni

Il sistema CasaZen presenta **gap significativi** in tutte le aree di compliance normativa. **Nessuna funzionalità obbligatoria è attualmente implementata**.

**Rischio compliance**: 🔴 ALTO

**Raccomandazione**: Implementare in via prioritaria le funzionalità CRITICAL e HIGH entro la scadenza del 01/03/2026. Pianificare roadmap per funzionalità MEDIUM e LOW.

---

**Report generato da**: Analyzer Agent
**Data**: 2026-03-27
**Versione**: 1.0
