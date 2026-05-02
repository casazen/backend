---
name: codebase-overview
description: Instant architecture reference for the CasaZen backend. Replaces exploring 10-20 files. Use at the start of any implementation task to orient quickly.
---

# CasaZen Backend — Architecture Reference

## Project Layers

```
Casazen.Web/                     Presentation (HTTP)
  Controllers/                   API endpoints — thin, delegate to services
    AuthController.cs
    BookingController.cs
    GdprController.cs
    GuestsController.cs
    OtaController.cs / OtaIntegrationsController.cs
    PaymentsController.cs
    PropertiesController.cs
    TouristTaxRatesController.cs
    WebhooksController.cs
  Middleware/                    Auth, logging, error handling
  Program.cs                     DI registration (all services registered here)

Casazen.Core/                    Domain (business logic + contracts)
  Entities/                      Domain models
    Property.cs
    Booking.cs
    Guest.cs
    Payment.cs
    OtaIntegration.cs
    OtaSyncLog.cs
    AlloggiatiWebReport.cs       Italian police check-in reporting
    TouristTaxRate.cs            Regional tourist tax rates
    TaxRate.cs
    CancellationPolicy.cs
    User.cs
  Repositories/                  IRepository<T> interfaces (no implementation here)
  Services/                      Business logic interfaces

Casazen.Infrastructure/          Data access + external integrations
  Data/
    AppDbContext.cs              EF Core context
    Migrations/                  EF Core migration files
  Repositories/                  IRepository<T> implementations
  Services/                      Business logic implementations
  External/
    AlloggiatiWebService.cs      Italian police portal integration
    StripeService.cs
    StripeWebhookHandler.cs
    SendGridService.cs
  OTA/
    IOtaAdapter.cs / IChannelAdapter.cs
    AirbnbAdapter.cs             Reference implementation
    BookingComAdapter.cs         Stub — not fully implemented
    ExpediaAdapter.cs
    VrboAdapter.cs
    TripAdvisorAdapter.cs
    AgodaAdapter.cs
    ChannelFactory.cs
    Resilience/                  Polly: retry, circuit-breaker, timeout, rate-limit

Casazen.Tests/
  Unit/                          Service unit tests (Moq)
  Integration/                   API integration tests (WebApplicationFactory)
```

## Key Design Patterns

| Pattern | Implementation |
|---|---|
| Repository | All DB access via `IRepository<T>` — never DbContext directly in controllers |
| Adapter | Each OTA platform implements `IOtaAdapter` — add new platforms here |
| Dependency Injection | All services registered in `Program.cs` — inject via constructor |

## Entity Relationships

```
User (1) ──── (*) Property
Property (1) ── (*) Booking
Booking (1) ─── (1) Payment
Booking (1) ─── (*) Guest
Property (1) ── (*) OtaIntegration
```

## Key Conventions

- **Async**: always `async/await` + suffix method with `Async`; NEVER `.Result` or `.Wait()`
- **DB changes**: always create EF Core migration (`dotnet ef migrations add <Name> --project Casazen.Infrastructure`)
- **Testing**: AAA pattern (Arrange/Act/Assert), Moq for dependencies
- **API routes**: `/api/{resource}` (e.g., `/api/properties`)
- **Compliance comments**: tag code touching Italian regulations with XML comment referencing the specific law

## Common File Locations

| What | Where |
|---|---|
| DI registration | `Casazen.Web/Program.cs` |
| DB Context | `Casazen.Infrastructure/Data/AppDbContext.cs` |
| Migrations | `Casazen.Infrastructure/Data/Migrations/` |
| OTA adapters | `Casazen.Infrastructure/OTA/` |
| Configuration | `Casazen.Web/appsettings.json` |

## Tech Stack

**.NET 10** + **C# 13** · **EF Core** · **SQL Server** · **Auth0** (JWT) · **Stripe** · **SendGrid** · **xUnit** + **Moq** · **Polly**
