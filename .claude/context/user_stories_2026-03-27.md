# User Stories - Gap Analysis 2026-03-27

> User story pronte per essere convertite in issue GitHub dal `github_agent`

---

## Story 1: Comunicazione Alloggiati Web [CRITICAL]

### User Story
Come **proprietario**, voglio **comunicare automaticamente i dati degli ospiti alla Questura entro 24 ore dall'arrivo**, in modo da **adempiere all'obbligo di legge ed evitare sanzioni penali**.

### Contesto
L'art. 109 TULPS impone a tutti i gestori di strutture ricettive (inclusi affitti brevi) di comunicare alla Questura i dati di tutti gli ospiti entro 24 ore dall'arrivo. L'omessa o tardiva comunicazione comporta sanzioni **penali** (contravvenzione) a carico personale del gestore.

La comunicazione avviene tramite:
- Portale nazionale **Alloggiati Web** (Polizia di Stato)
- Portali regionali alternativi (Toscana, Veneto, Puglia, etc.)

**Riferimento**: Art. 109 TULPS, D.M. 07/01/2013
**Priorità**: CRITICAL
**Scadenza**: Obbligo permanente

### Requisiti Funzionali

#### 1. Guest Entity Enhancement
- [ ] Campo `DateOfBirth` (DateTime)
- [ ] Campo `PlaceOfBirth` (string, città e nazione)
- [ ] Campo `Citizenship` (string, ISO 3166-1 alpha-2)
- [ ] Campo `ResidenceAddress` (string, completo)
- [ ] Campo `ResidenceCity` (string)
- [ ] Campo `ResidenceCountry` (string)
- [ ] Campo `DocumentType` (enum: IDCard | Passport | DrivingLicense)
- [ ] Campo `DocumentNumber` (string)
- [ ] Campo `DocumentIssuedBy` (string, ente rilascio)
- [ ] Campo `DocumentExpiryDate` (DateTime)
- [ ] Campo `DocumentScanUrl` (string, path cifrato storage)

#### 2. Alloggiati Integration Service
- [ ] Connettore API Alloggiati Web (Polizia di Stato)
- [ ] Connettori portali regionali:
  - [ ] Toscana
  - [ ] Veneto
  - [ ] Puglia
  - [ ] Altri (estendibile)
- [ ] Mapping dati `Guest` → formato Alloggiati Web
- [ ] Gestione credenziali Questura per proprietario (cifratura)
- [ ] Retry logic per fallimenti temporanei
- [ ] Fallback manuale se API non disponibile

#### 3. Communication Tracking Entity (nuova)
- [ ] Campo `BookingId` (FK verso Booking)
- [ ] Campo `CommunicationType` (enum: AlloggiatiWeb | RegionalPortal)
- [ ] Campo `Status` (enum: Pending | Sent | Failed | ManuallyCompleted)
- [ ] Campo `SentDate` (DateTime)
- [ ] Campo `ProtocolNumber` (string, se disponibile)
- [ ] Campo `ErrorMessage` (string, per debugging)

#### 4. Self Check-In Digitale
- [ ] Guest portal: form compilazione dati + upload documento
- [ ] Validazione completezza dati lato client e server
- [ ] Upload sicuro scan documento (cifratura at-rest)
- [ ] OCR documento identità (opzionale, nice-to-have)
- [ ] Conferma ricezione dati completi

#### 5. Pre-Arrival Workflow
- [ ] Email/SMS automatica ospite 3-7 giorni prima arrivo con link check-in
- [ ] Reminder se dati non completi (1 giorno prima)
- [ ] Alert proprietario se dati mancanti (1 giorno prima)
- [ ] Dashboard ospiti con dati incompleti

#### 6. Auto-Send on Check-In
- [ ] Trigger automatico invio dati ad Alloggiati Web al check-in
- [ ] Conferma invio e salvataggio numero protocollo
- [ ] Alert fallimento con fallback manuale

#### 7. Compliance Monitoring
- [ ] Dashboard comunicazioni effettuate vs. prenotazioni
- [ ] Alert urgente per scadenza 24h imminente
- [ ] Report comunicazioni mancanti/in ritardo
- [ ] Storico comunicazioni con tracking completo

### Requisiti Non Funzionali
- [ ] Cifratura scan documenti at-rest (AES-256)
- [ ] Cifratura credenziali Questura (HSM o Azure Key Vault)
- [ ] HTTPS per tutte le comunicazioni
- [ ] GDPR compliance: informativa, consenso, data retention
- [ ] Audit log per accessi ai documenti
- [ ] Performance: invio asincrono (non bloccare check-in)
- [ ] Resilienza: retry automatico, fallback manuale

### Acceptance Criteria
- [ ] **GIVEN** una prenotazione confermata, **WHEN** l'ospite compila il form check-in con dati completi, **THEN** i dati sono salvati cifrati e validati
- [ ] **GIVEN** dati ospite completi, **WHEN** avviene il check-in, **THEN** i dati sono inviati automaticamente ad Alloggiati Web entro 1 ora
- [ ] **GIVEN** invio riuscito, **WHEN** verifico lo stato comunicazione, **THEN** vedo conferma invio + numero protocollo
- [ ] **GIVEN** invio fallito, **WHEN** verifico lo stato, **THEN** vedo errore + possibilità invio manuale
- [ ] **GIVEN** prenotazione con dati incompleti, **WHEN** mancano meno di 24h al check-in, **THEN** proprietario riceve alert urgente
- [ ] **GIVEN** comunicazione non inviata entro 24h, **WHEN** accedo alla dashboard, **THEN** vedo alert CRITICO

### Note Tecniche

#### Entità da Modificare/Creare
- `Casazen.Core/Entities/Guest.cs` - aggiungere campi alloggiati
- `Casazen.Core/Entities/GuestCommunication.cs` - nuova entità tracking
- Migration EF Core per nuovi campi

#### Servizi da Creare
- `Casazen.Core/Services/IAlloggiatiService.cs` - interfaccia
- `Casazen.Infrastructure/Services/AlloggiatiService.cs` - implementazione
- `Casazen.Infrastructure/External/AlloggiatiWebConnector.cs` - connettore API
- `Casazen.Infrastructure/External/RegionalPortalConnectors/` - connettori regionali

#### Controller/API
- `Casazen.Web/Controllers/GuestCheckInController.cs` - endpoint check-in digitale
- Endpoint: `POST /api/checkin/guest-data`
- Endpoint: `POST /api/checkin/document-upload`
- Endpoint: `POST /api/alloggiati/send` (manuale fallback)
- Endpoint: `GET /api/alloggiati/status/{bookingId}`

#### Background Jobs
- Job schedulato: verificare comunicazioni pendenti ogni ora
- Job: inviare alert pre-arrivo (3 giorni, 1 giorno)

#### Storage
- Azure Blob Storage (o S3) per scan documenti cifrati
- Azure Key Vault per credenziali Questura

#### Pattern
- Repository pattern per GuestCommunication
- Adapter pattern per portali regionali (interfaccia comune)
- Strategy pattern per selezione portale (nazionale vs. regionale)

### Stima Complessità
**ALTA** - 15-20 giorni sviluppo
- Modifiche entità + migrations (2 giorni)
- Connettore Alloggiati Web (3-5 giorni, dipende da disponibilità API)
- Connettori regionali (2 giorni per portale)
- Self check-in portal (3-5 giorni)
- Cifratura + sicurezza (2 giorni)
- Compliance dashboard (2 giorni)
- Testing + fallback manuale (2 giorni)

### Epic
Questa story può essere suddivisa in un epic con 3 sub-stories:
1. **Guest Data Collection** (entità + self check-in)
2. **Alloggiati Integration** (connettori + invio automatico)
3. **Compliance Monitoring** (dashboard + alert)

### Riferimenti
- [Art. 109 TULPS](https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:regio.decreto:1931-06-18;773)
- [D.M. 07/01/2013](https://www.gazzettaufficiale.it/eli/id/2013/01/12/13A00255/sg)
- [Alloggiati Web - Polizia di Stato](https://alloggiatiweb.poliziadistato.it/)
- [CheckInFacile - Multa Alloggiati Web 2026](https://checkinfacile.com/blog/multa-alloggiati-web-sanzioni.html)

---

## Story 2: Gestione Codice CIN [HIGH]

### User Story
Come **proprietario**, voglio **registrare e gestire il Codice CIN delle mie proprietà**, in modo da **essere conforme all'obbligo normativo ed evitare sanzioni da €800 a €8.000 per immobile**.

### Contesto
Il D.L. 145/2023 (art. 13-ter) ha introdotto l'obbligo del Codice Identificativo Nazionale (CIN) per tutte le strutture destinate a locazioni brevi. La **scadenza per operatori esistenti è il 01/03/2026** (IMMINENTE!).

Il CIN deve essere:
- Ottenuto tramite la BDSR (Banca Dati Strutture Ricettive del Ministero Turismo)
- Esposto negli annunci su tutte le piattaforme OTA
- Esposto fisicamente all'esterno dell'immobile

Le OTA potrebbero rimuovere annunci non conformi dopo la scadenza.

**Riferimento**: D.L. 145/2023 conv. L. 191/2023 (art. 13-ter), D.M. 03/09/2024
**Priorità**: HIGH
**Scadenza**: 01/03/2026

### Requisiti Funzionali

#### 1. Property Entity Enhancement
- [ ] Campo `CINCode` (string, nullable, max 50 char)
- [ ] Campo `CINRequestedDate` (DateTime, nullable)
- [ ] Campo `CINApprovedDate` (DateTime, nullable)
- [ ] Campo `CINExpiryDate` (DateTime, nullable - se applicabile)
- [ ] Campo `CINStatus` (enum: NotRequested | Pending | Approved | Rejected | Expired)
- [ ] Validazione formato CIN (regex, da definire standard BDSR)

#### 2. CIN Validation Service
- [ ] Validazione formato CIN (pattern da BDSR)
- [ ] Verifica univocità CIN nel sistema
- [ ] API call a BDSR per verifica validità (se disponibile)
- [ ] Logging validazioni per audit

#### 3. OTA Integration Enhancement
- [ ] Aggiungere campo CIN a payload sync OTA:
  - [ ] Airbnb
  - [ ] Booking.com
  - [ ] Expedia
  - [ ] VRBO
  - [ ] TripAdvisor
  - [ ] Agoda
- [ ] Verifica presenza CIN in listing esistenti (health check)
- [ ] Blocco pubblicazione annunci senza CIN valido (opzionale, configurabile)

#### 4. Owner Portal - CIN Management
- [ ] Form inserimento CIN per proprietà
- [ ] Workflow guidato richiesta CIN:
  - [ ] Link diretto al portale BDSR
  - [ ] Checklist documentazione necessaria (SCIA, comunicazione comunale)
  - [ ] Tutorial step-by-step
- [ ] Visualizzazione stato CIN nella scheda proprietà
- [ ] Possibilità modifica/aggiornamento CIN

#### 5. Compliance Dashboard
- [ ] Lista proprietà senza CIN
- [ ] Alert scadenza 01/03/2026 per immobili non conformi
- [ ] Countdown giorni mancanti alla scadenza
- [ ] Report non-conformità PDF scaricabile
- [ ] Notifica email/SMS proprietari con immobili non conformi

#### 6. Admin Panel
- [ ] Visibilità globale proprietà senza CIN
- [ ] Export CSV proprietà non conformi
- [ ] Statistiche conformità (% proprietà con CIN)

### Requisiti Non Funzionali
- [ ] Performance: validazione CIN < 100ms
- [ ] Audit log per modifiche CIN
- [ ] Alert email automatico proprietari (1 mese prima scadenza, 1 settimana, 1 giorno)
- [ ] Responsive design per mobile (inserimento CIN da smartphone)

### Acceptance Criteria
- [ ] **GIVEN** una proprietà senza CIN, **WHEN** accedo alla dashboard proprietario, **THEN** vedo un alert di non conformità con countdown scadenza
- [ ] **GIVEN** un CIN valido, **WHEN** lo inserisco nella proprietà, **THEN** viene salvato, validato e mostrato nella scheda
- [ ] **GIVEN** un CIN con formato errato, **WHEN** lo inserisco, **THEN** ricevo errore di validazione con messaggio chiaro
- [ ] **GIVEN** una proprietà con CIN, **WHEN** sincronizzo con Airbnb, **THEN** il CIN è incluso nei dati inviati
- [ ] **GIVEN** data odierna < 01/03/2026, **WHEN** accedo alla dashboard, **THEN** vedo alert prominente con giorni mancanti
- [ ] **GIVEN** mancano 7 giorni alla scadenza, **WHEN** un proprietario ha immobili senza CIN, **THEN** riceve email di alert urgente

### Note Tecniche

#### Entità da Modificare
- `Casazen.Core/Entities/Property.cs` - aggiungere campi CIN
- Migration EF Core per nuovi campi

#### Enum da Creare
```csharp
public enum CINStatus {
    NotRequested,
    Pending,
    Approved,
    Rejected,
    Expired
}
```

#### Servizi da Creare
- `Casazen.Core/Services/ICINValidationService.cs` - interfaccia
- `Casazen.Infrastructure/Services/CINValidationService.cs` - implementazione
- Metodi:
  - `bool ValidateFormat(string cinCode)`
  - `Task<bool> IsUnique(string cinCode)`
  - `Task<bool> VerifyWithBDSR(string cinCode)` (nice-to-have)

#### Adapter OTA da Aggiornare
- `Casazen.Infrastructure/OTA/AirbnbAdapter.cs`
- `Casazen.Infrastructure/OTA/BookingComAdapter.cs`
- `Casazen.Infrastructure/OTA/ExpediaAdapter.cs`
- `Casazen.Infrastructure/OTA/VrboAdapter.cs`
- `Casazen.Infrastructure/OTA/TripAdvisorAdapter.cs`
- `Casazen.Infrastructure/OTA/AgodaAdapter.cs`

Aggiungere campo CIN al payload sync (verificare documentazione API OTA per campo corretto).

#### Controller/API
- `Casazen.Web/Controllers/PropertiesController.cs` - aggiungere endpoint CIN:
  - `PUT /api/properties/{id}/cin` - update CIN
  - `POST /api/properties/{id}/cin/validate` - validazione CIN
  - `GET /api/properties/cin-compliance` - report conformità

#### Background Jobs
- Job schedulato: verificare scadenza CIN e inviare alert (daily)
- Job: aggiornamento status CIN verso OTA (weekly health check)

#### Validazione Formato CIN
Formato CIN da verificare su documentazione BDSR. Esempio ipotetico:
- Pattern: `^IT-[0-9]{5}-[A-Z0-9]{10}$`
- Esempio: `IT-12345-ABC1234567`

**TODO**: Verificare formato ufficiale CIN su portale BDSR.

#### UI/UX
- Alert prominente dashboard: banner rosso con countdown
- Icona stato CIN accanto a ciascuna proprietà (✓ conforme, ⚠️ mancante, ⏳ pending)
- Modal wizard richiesta CIN con 3 step:
  1. Informazioni normativa
  2. Checklist documenti
  3. Link BDSR + form inserimento CIN

### Stima Complessità
**MEDIA** - 5-7 giorni sviluppo
- Modifiche entità + migrations (1 giorno)
- Servizio validazione CIN (1 giorno)
- Aggiornamento adapter OTA (2 giorni)
- Dashboard conformità + alert (2 giorni)
- Testing + UI polish (1 giorno)

### Riferimenti
- [D.L. 145/2023, art. 13-ter](https://www.gazzettaufficiale.it/eli/id/2023/10/18/23G00158/sg)
- [BDSR - Ministero Turismo](https://bdsr.ministeroturismo.it)
- [Lodgify - Guida CIN 2026](https://www.lodgify.com/blog/it/codice-cin-affitti-brevi/)
- [Regione Toscana - CIN](https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche)

---

## Story 3: Regime Fiscale e Cedolare Secca [HIGH]

### User Story
Come **proprietario**, voglio **gestire il regime fiscale applicabile ai miei immobili in affitto breve**, in modo da **essere conforme alla normativa fiscale 2026 e calcolare correttamente le imposte dovute**.

### Contesto
Dal 01/01/2026, la Legge di Bilancio 2026 ha introdotto modifiche significative al regime fiscale affitti brevi:
- **Partita IVA obbligatoria dal 3° immobile** (riduzione da 5°)
- **Cedolare secca**: 21% per primo immobile, 26% per secondo immobile
- **Ritenuta fiscale 21%** applicata da OTA sui compensi erogati (acconto recuperabile in dichiarazione)

**Riferimento**: L. 199/2025 (Legge di Bilancio 2026), D.L. 50/2017
**Priorità**: HIGH
**Scadenza**: Obbligo in vigore dal 01/01/2026

### Requisiti Funzionali

#### 1. Property Entity Enhancement
- [ ] Campo `FiscalRegime` (enum: CedolareSecca21 | CedolareSecca26 | PartitaIVA | RegimeOrdinario)
- [ ] Campo `TaxYear` (int, anno fiscale di riferimento)
- [ ] Campo `IsPrimaryPropertyForTax` (bool, se è il primo immobile scelto per cedolare 21%)

#### 2. User/Owner Entity Enhancement
- [ ] Campo `HasPartitaIVA` (bool)
- [ ] Campo `PartitaIVANumber` (string, nullable, max 11 char per P.IVA italiana)
- [ ] Campo `FiscalCode` (string, Codice Fiscale)
- [ ] Campo `PropertiesCountForTaxYear` (int, calcolato dinamicamente)

#### 3. Payment Enhancement
- [ ] Campo `OtaWithholdingTax` (decimal, ritenuta 21% applicata da OTA)
- [ ] Campo `WithholdingTaxApplied` (bool)
- [ ] Campo `NetAmountAfterWithholding` (decimal, importo netto al proprietario)
- [ ] Separazione: `GrossAmount` vs. `NetAmount` vs. `WithholdingTax`

#### 4. Fiscal Regime Calculation Service
- [ ] Calcolo automatico regime applicabile per proprietario:
  - Input: numero immobili in affitto breve per anno fiscale
  - Output: regime raccomandato per ciascun immobile
- [ ] Alert superamento soglia 2 immobili (necessità P.IVA)
- [ ] Suggerimento quale immobile designare come "primo" per cedolare 21%

#### 5. Withholding Tax Management
- [ ] Tracking ritenute subite da OTA per ciascun pagamento
- [ ] Aggregazione ritenute per anno fiscale
- [ ] Export ritenute per F24 e dichiarazione redditi

#### 6. Owner Portal - Fiscal Configuration
- [ ] Wizard selezione regime fiscale ottimale
- [ ] Form inserimento P.IVA (se applicabile)
- [ ] Simulatore impatto fiscale (confronto cedolare vs. impresa)
- [ ] Designazione immobile "primario" per cedolare 21%
- [ ] Dashboard regime fiscale per ciascuna proprietà

#### 7. Reporting Fiscale
- [ ] Report annuale redditi per cedolare secca (per proprietà)
- [ ] Report ritenute subite (per recupero in dichiarazione)
- [ ] Certificazione Unica (CU) simulata
- [ ] Export dati CSV/PDF per commercialista

#### 8. Admin Panel
- [ ] Statistiche regimi fiscali utilizzati
- [ ] Proprietari con P.IVA vs. senza
- [ ] Report aggregato ritenute OTA

### Requisiti Non Funzionali
- [ ] Calcolo regime fiscale deterministico e tracciabile
- [ ] Audit log per modifiche regime fiscale
- [ ] Performance: calcolo < 100ms
- [ ] Export report fiscale < 5s per proprietario

### Acceptance Criteria
- [ ] **GIVEN** un proprietario con 1 immobile, **WHEN** accedo alla configurazione fiscale, **THEN** il sistema raccomanda cedolare secca 21%
- [ ] **GIVEN** un proprietario con 2 immobili, **WHEN** designo il primo per cedolare 21%, **THEN** il secondo è automaticamente assegnato a cedolare 26%
- [ ] **GIVEN** un proprietario con 3 immobili, **WHEN** accedo alla dashboard, **THEN** vedo alert "Partita IVA obbligatoria" con link a wizard
- [ ] **GIVEN** un pagamento da Airbnb, **WHEN** registro il pagamento, **THEN** la ritenuta 21% è calcolata e salvata separatamente
- [ ] **GIVEN** fine anno fiscale, **WHEN** genero il report ritenute, **THEN** vedo l'aggregato di tutte le ritenute subite con dettaglio per OTA
- [ ] **GIVEN** proprietario con P.IVA, **WHEN** modifico il regime di un immobile, **THEN** posso scegliere "Regime Ordinario" o "Regime Forfettario"

### Note Tecniche

#### Entità da Modificare
- `Casazen.Core/Entities/Property.cs` - aggiungere campi fiscali
- `Casazen.Core/Entities/User.cs` - aggiungere campi P.IVA e fiscali
- `Casazen.Core/Entities/Payment.cs` - aggiungere campi ritenuta
- Migration EF Core per nuovi campi

#### Enum da Creare
```csharp
public enum FiscalRegime {
    CedolareSecca21,    // Primo immobile
    CedolareSecca26,    // Secondo immobile
    PartitaIVA,         // 3+ immobili, o scelta volontaria
    RegimeOrdinario,    // P.IVA con tassazione IRPEF
    RegimeForfettario   // P.IVA con tassazione 15%/5%
}
```

#### Servizi da Creare
- `Casazen.Core/Services/IFiscalRegimeService.cs` - interfaccia
- `Casazen.Infrastructure/Services/FiscalRegimeService.cs` - implementazione
- Metodi:
  - `FiscalRegime CalculateRecommendedRegime(Guid ownerId, int taxYear)`
  - `int CountPropertiesForTaxYear(Guid ownerId, int taxYear)`
  - `bool RequiresPartitaIVA(Guid ownerId, int taxYear)`
  - `decimal CalculateWithholdingTax(decimal grossAmount)`

- `Casazen.Core/Services/IFiscalReportingService.cs` - interfaccia
- `Casazen.Infrastructure/Services/FiscalReportingService.cs` - implementazione
- Metodi:
  - `Task<FiscalYearReport> GenerateAnnualReport(Guid ownerId, int taxYear)`
  - `Task<WithholdingTaxReport> GenerateWithholdingTaxReport(Guid ownerId, int taxYear)`
  - `Task<byte[]> ExportReportToPdf(FiscalYearReport report)`

#### Controller/API
- `Casazen.Web/Controllers/FiscalController.cs` - nuovo controller:
  - `GET /api/fiscal/regime/{ownerId}` - get regime consigliato
  - `PUT /api/fiscal/properties/{propertyId}/regime` - set regime per proprietà
  - `GET /api/fiscal/reports/annual/{ownerId}/{taxYear}` - report annuale
  - `GET /api/fiscal/reports/withholding/{ownerId}/{taxYear}` - report ritenute
  - `POST /api/fiscal/simulate` - simulatore fiscale

#### Logica Calcolo Regime
```csharp
public FiscalRegime CalculateRecommendedRegime(Guid ownerId, int taxYear) {
    int propertyCount = CountPropertiesForTaxYear(ownerId, taxYear);

    if (propertyCount == 0) return null;
    if (propertyCount == 1) return FiscalRegime.CedolareSecca21;
    if (propertyCount == 2) return FiscalRegime.CedolareSecca26;
    if (propertyCount >= 3) return FiscalRegime.PartitaIVA;
}
```

#### Calcolo Ritenuta OTA
```csharp
public decimal CalculateWithholdingTax(decimal grossAmount) {
    return grossAmount * 0.21m; // 21%
}

public decimal CalculateNetAmount(decimal grossAmount, decimal withholdingTax) {
    return grossAmount - withholdingTax;
}
```

#### Report Fiscale
Template report annuale:
- Redditi lordi per proprietà
- Regime fiscale applicato
- Ritenute subite da OTA (aggregate)
- Imposta dovuta (simulata)
- Note e disclaimer

**IMPORTANTE**: Il report è indicativo, non sostituisce consulenza fiscale.

#### UI/UX
- Wizard fiscale step-by-step:
  1. Quanti immobili hai in affitto breve?
  2. Hai già una Partita IVA? (se 3+)
  3. Quale immobile vuoi designare come "primario" per cedolare 21%? (se 2)
- Dashboard fiscale con card per ciascuna proprietà:
  - Nome proprietà
  - Regime fiscale corrente
  - Redditi YTD (year-to-date)
  - Ritenute subite YTD
- Simulatore: input redditi stimati, output imposta dovuta per ciascun regime

### Stima Complessità
**MEDIA** - 7-10 giorni sviluppo
- Modifiche entità + migrations (1 giorno)
- Servizio calcolo regime fiscale (2 giorni)
- Gestione ritenuta in Payment (1 giorno)
- Reporting fiscale + export PDF (3 giorni)
- Wizard + dashboard fiscale (2 giorni)
- Testing + edge cases (1 giorno)

### Epic
Questa story può essere suddivisa in 2 sub-stories:
1. **Fiscal Regime Management** (calcolo regime + alert P.IVA)
2. **Withholding Tax & Reporting** (gestione ritenuta + report fiscale)

### Riferimenti
- [L. 199/2025 - Legge di Bilancio 2026](https://www.gazzettaufficiale.it/eli/id/2025/12/30/25G00213/sg)
- [D.L. 50/2017 - Cedolare secca affitti brevi](https://www.gazzettaufficiale.it/eli/id/2017/04/24/17G00064/sg)
- [FISCOeTASSE - Cedolare secca 2026](https://www.fiscoetasse.com/new-rassegna-stampa/2970-cedolare-secca-affitti-brevi-le-novita-dal-2026.html)
- [Studio Mazzocchi - Affitti brevi 2026](https://studiomazzocchi.it/affitti-brevi-2026-normativa-cedolare-secca-e-partita-iva/)

---

## Riepilogo User Stories

| # | Titolo | Priorità | Stima | Epic |
|---|--------|----------|-------|------|
| 1 | Comunicazione Alloggiati Web | CRITICAL | 15-20 giorni | Sì (3 sub-stories) |
| 2 | Gestione Codice CIN | HIGH | 5-7 giorni | No |
| 3 | Regime Fiscale e Cedolare Secca | HIGH | 7-10 giorni | Sì (2 sub-stories) |

**Totale stima**: 27-37 giorni sviluppo

---

**User stories generate da**: Analyzer Agent
**Data**: 2026-03-27
**Template**: write_user_story.md
**Pronte per**: github_agent (creazione issue GitHub)
