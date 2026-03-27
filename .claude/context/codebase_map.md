# CasaZen - Mappa della Codebase

## Struttura Progetti

### Casazen.Web (Presentation Layer)
- `Controllers/` - API endpoints REST
- `Program.cs` - configurazione app, DI, middleware
- `appsettings.json` - configurazione base

### Casazen.Core (Business Logic)
- `Entities/` - modelli di dominio
  - `Property` - proprieta' con relazione owner
  - `Booking` - prenotazioni con ospite, proprieta', date
  - `Payment` - transazioni collegate a booking
  - `Guest` - informazioni cliente
  - `OtaIntegration` - configurazioni sync per piattaforma
- `Interfaces/` - contratti per repository e servizi
- `Services/` - logica di business

### Casazen.Infrastructure (Data Access & External)
- `Data/` - DbContext, migrations, repository implementations
- `External/` - webhook handlers (Stripe, etc.)
- `OTA/` - adapter per piattaforme OTA (Airbnb, Booking.com, etc.)

### Casazen.Tests
- Unit tests per servizi
- Integration tests per API

## Funzionalita' Implementate
- [x] Entita' core (Property, Booking, Payment, Guest, OtaIntegration)
- [x] Repository pattern per accesso dati
- [x] API RESTful con autenticazione JWT (Auth0)
- [x] Integrazione Stripe per pagamenti
- [x] Integrazione SendGrid per email
- [x] Adapter OTA (Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda)
- [x] CI/CD con GitHub Actions

## Funzionalita' NON Ancora Implementate (gap potenziali)
- [ ] Gestione Codice CIN (campo, validazione, esposizione)
- [ ] Comunicazione alloggiati web (integrazione Questura)
- [ ] Calcolo e versamento imposta di soggiorno
- [ ] Gestione cedolare secca / ritenuta 21% OTA
- [ ] Reportistica fiscale automatizzata
- [ ] Consent management GDPR per ospiti
- [ ] Gestione documenti identita' ospiti
- [ ] Scadenzario obblighi normativi
- [ ] Dashboard compliance

## Pattern Architetturali
- **Repository Pattern** - `IRepository<T>` in Core, implementazioni in Infrastructure
- **Adapter Pattern** - per integrazioni OTA
- **Dependency Injection** - registrazione servizi in Program.cs
- **JWT Bearer Auth** - tramite Auth0
