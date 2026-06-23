# PROJECT.md — AI Context for CasaZen Backend

## What this project does
CasaZen is a vacation-rental property management platform targeting the Italian short-term
rental market. It enables property managers to list properties, manage bookings, collect
payments, sync inventory across OTA channels (Airbnb, Booking.com, Expedia, VRBO,
TripAdvisor, Agoda), and comply with Italian regulations (CIN D.L. 145/2023, Alloggiati
Web reporting, tourist tax, GDPR).

## Stack snapshot
- **Language**: C# 13 / .NET 10 (global.json sdk `10.0.0`)
- **Framework**: ASP.NET Core Web API with Swagger/OpenAPI
- **Database**: SQL Server (EF Core 10, code-first migrations)
- **Auth**: Auth0 — JWT Bearer validated on every `/api` endpoint
- **Background jobs**: Hangfire (OTA sync hourly, booking pull every 15 min, GDPR retention, pricing)
- **Payments**: Stripe (webhook signature verification required)
- **Email**: MailKit SMTP (transactional; supplier invite uses `SupplierInviteEmailBuilder` HTML; supports any SMTP server or SendGrid relay)
- **OTA resilience**: Polly (retry + circuit-breaker + rate-limit per platform)
- **Tests**: xUnit (unit + integration), AAA pattern
- **CI/CD**: GitHub Actions (`.github/workflows/ci-cd.yml`, `step-transitions.yml`)

## Repo layout
```
Casazen.sln
├── Casazen.Core/              # Domain layer — entities, interfaces, validation
│   ├── Entities/              # All EF Core entity classes (14 entities)
│   ├── Repositories/          # Repository interfaces
│   ├── Services/              # Service interfaces
│   ├── Validation/            # CinCodeAttribute, BookingValidator
│   ├── Utilities/             # TimezoneHelper (Europe/Rome default)
│   └── Enums/                 # PropertyAmenity enum
├── Casazen.Infrastructure/    # Data + external services layer
│   ├── Data/                  # AppDbContext, AppDbContextFactory
│   ├── Migrations/            # EF Core migration files
│   ├── Repositories/          # Concrete repository implementations
│   ├── Services/              # Concrete service implementations
│   ├── External/              # AlloggiatiWebService, SmtpEmailService, StripeService, StripeWebhookHandler
│   ├── OTA/                   # Channel adapters: Airbnb, BookingCom, Expedia, VRBO, TripAdvisor, Agoda
│   │   └── Resilience/        # Polly policy factories per platform
│   └── Payments/              # Stripe payment processing
├── Casazen.Web/               # Presentation layer — ASP.NET Core host
│   ├── Controllers/           # 12 API controllers
│   ├── DTOs/                  # Request/response transfer objects
│   ├── BackgroundJobs/        # Hangfire job classes (7 jobs)
│   ├── Middleware/            # ErrorHandlingMiddleware
│   ├── Extensions/            # DI registration helpers
│   └── Program.cs             # App entry point, DI, middleware pipeline
├── Casazen.Tests/
│   ├── Unit/                  # Services, controllers, entities, OTA, repositories
│   └── Integration/           # API, migration, resilience, controller integration tests
├── docs/                      # Project documentation (this folder)
├── Dockerfile                 # Multi-stage build
├── docker-compose.yml         # API + SQL Server services
└── .github/workflows/         # CI/CD pipelines
```

## Key conventions
- **Async**: all I/O methods suffixed `Async` (e.g. `GetBookingAsync`). Never `.Result`/`.Wait()`.
- **Naming**: PascalCase classes/methods; `I` prefix for interfaces; `*Controller`, `*Service`, `*Repository` suffixes.
- **Tests**: `MethodName_Scenario_ExpectedBehavior` format; AAA pattern; mock via `Mock<IRepository>`.
- **Coverage targets**: critical paths 100%, services 80%, controllers 70%.
- **DateTime**: always `DateTime.UtcNow` internally; convert to local only for display.
- **Validation**: data annotations on entities + `[ApiController]` automatic model-state validation at API boundary.
- **Commits**: Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`). No "Co-Authored-By: Claude" lines.
- **Branches**: `feature/<name>` / `fix/<name>` / `hotfix/<name>` → PR to `develop`; release PR `develop` → `main` (Stage 05 only). Never push directly to `main` or `develop`.
- **Language**: all code, docs, commit messages in English; UI text for end-users in Italian.

## Where to find things

| Thing | Where |
|---|---|
| Domain entities | `Casazen.Core/Entities/*.cs` (14 files) |
| Repository interfaces | `Casazen.Core/Repositories/I*Repositories.cs` |
| Service interfaces | `Casazen.Core/Services/I*Service.cs` |
| Service implementations | `Casazen.Infrastructure/Services/*.cs` |
| Repository implementations | `Casazen.Infrastructure/Repositories/*.cs` |
| EF Core DbContext | `Casazen.Infrastructure/Data/AppDbContext.cs` |
| Database migrations | `Casazen.Infrastructure/Migrations/` |
| API controllers | `Casazen.Web/Controllers/*Controller.cs` (12 controllers) |
| DTOs | `Casazen.Web/DTOs/` |
| DI / app setup | `Casazen.Web/Program.cs` + `Casazen.Web/Extensions/` |
| Background jobs | `Casazen.Web/BackgroundJobs/` (7 Hangfire jobs) |
| OTA adapters | `Casazen.Infrastructure/OTA/*Adapter.cs` |
| OTA channel factory | `Casazen.Infrastructure/OTA/ChannelFactory.cs` |
| External integrations | `Casazen.Infrastructure/External/` (Alloggiati, email, Stripe) |
| Stripe webhook handler | `Casazen.Infrastructure/External/StripeWebhookHandler.cs` |
| CIN validation | `Casazen.Core/Validation/CinCodeAttribute.cs` |
| Italian tax rates | `Casazen.Core/Entities/TouristTaxRate.cs` |
| App configuration | `Casazen.Web/appsettings.json` (keys: Auth0, Stripe, Email, OTA, Hangfire) |
| Unit tests | `Casazen.Tests/Unit/` |
| Integration tests | `Casazen.Tests/Integration/` |
| CI/CD pipeline | `.github/workflows/ci-cd.yml` |

## Non-obvious rules / gotchas
- **CIN code**: every `Property` must store an Italian CIN (`IT-XXXXX-XXXXXXXXXX`). Validated by `[CinCode]` attribute. Required by D.L. 145/2023.
- **Tourist tax**: stored in `TouristTaxRate` entity — rates vary per city/region. **Never hardcode** a tax amount.
- **OTA webhooks**: must respond within 3 seconds. Long-running work goes through Hangfire queue — do not process inline in the webhook controller.
- **DbContext scope**: `AppDbContext` is scoped per request. Never store it in a static field or singleton; always dispose.
- **Alloggiati Web**: guest data submitted via `AlloggiatiWebService` for Italian police reporting. GDPR retention rules apply.
- **OTA adapters**: implement `IChannelAdapter`; accessed via `ChannelFactory`. Each platform has independent Polly resilience config in `appsettings.json → OTA.Resilience.<Platform>`.
- **Stripe webhook signatures**: `StripeWebhookHandler` must verify the `Stripe-Signature` header — never skip this check.
- **Migrations**: run `dotnet ef migrations add Add<Feature> --project Casazen.Infrastructure` for every schema change; test locally before committing.
- **Property timezone**: stored as IANA string (`Europe/Rome` default) — use `TimezoneHelper` for conversions, never raw `TimeZoneInfo`.
- **Auth**: all `/api` endpoints require Auth0 JWT Bearer token. `HealthController` is the only public endpoint.
- **GDPR**: `GdprDataRetentionJob` handles automatic deletion. `IGdprService` / `GdprController` expose data export/delete for GDPR requests.
- **Before every commit**: `dotnet test` + `dotnet format --verify-no-changes` — no compiler warnings allowed.

## External integrations

| Integration | Purpose | Config location |
|---|---|---|
| Auth0 | JWT authentication for all API endpoints | `appsettings.json → Auth0` |
| Stripe | Payment processing + webhook events | `appsettings.json → Stripe`; handler in `Casazen.Infrastructure/External/StripeWebhookHandler.cs` |
| MailKit SMTP | Transactional email (booking confirmations, notifications, supplier invites) | `appsettings.json → Email:SmtpHost` (or `Email:SendGridApiKey` for SendGrid SMTP relay) |
| Alloggiati Web | Italian police guest reporting (mandatory by law) | `Casazen.Infrastructure/External/AlloggiatiWebService.cs` |
| Airbnb | OTA booking sync + pricing/availability push | `appsettings.json → OTA.Airbnb` |
| Booking.com | OTA booking sync + pricing/availability push | `appsettings.json → OTA.BookingCom` |
| Expedia | OTA booking sync + pricing/availability push | `appsettings.json → OTA.Expedia` |
| VRBO | OTA booking sync + pricing/availability push | `appsettings.json → OTA.Resilience.Vrbo` |
| TripAdvisor | OTA booking sync + pricing/availability push | `appsettings.json → OTA.Resilience.TripAdvisor` |
| Agoda | OTA booking sync + pricing/availability push | `appsettings.json → OTA.Resilience.Agoda` |
| Hangfire | Background job scheduling and processing | `appsettings.json → Hangfire`; dashboard at `/hangfire` |
