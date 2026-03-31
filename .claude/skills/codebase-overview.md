---
name: codebase-overview
description: Get instant overview of CasaZen backend architecture without exploring files
invocable: true
---

# CasaZen Backend - Architecture Overview

## Project Structure

```
Casazen.Web/
├── Controllers/          # API endpoints
│   ├── BookingsController.cs
│   ├── PropertiesController.cs
│   ├── PaymentsController.cs
│   └── OtaController.cs
├── Middleware/          # Auth, logging, error handling
└── Program.cs          # DI configuration

Casazen.Core/
├── Entities/           # Domain models
│   ├── Booking.cs
│   ├── Property.cs
│   ├── Guest.cs
│   ├── Payment.cs
│   └── OtaIntegration.cs
├── Repositories/       # Repository interfaces
│   └── IRepository<T>
└── Services/          # Business logic interfaces
    ├── IBookingService.cs
    ├── IPropertyService.cs
    └── IOtaService.cs

Casazen.Infrastructure/
├── Data/
│   ├── AppDbContext.cs        # EF Core context
│   └── Migrations/            # Database migrations
├── Repositories/              # Repository implementations
├── Services/                  # Business logic implementations
├── External/                  # Third-party integrations
│   ├── Auth0Service.cs
│   ├── StripeService.cs
│   ├── StripeWebhookHandler.cs
│   └── SendGridService.cs
└── OTA/                      # OTA platform adapters
    ├── IOtaAdapter.cs
    ├── AirbnbAdapter.cs
    ├── BookingAdapter.cs
    ├── ExpediaAdapter.cs
    ├── VrboAdapter.cs
    ├── TripAdvisorAdapter.cs
    └── AgodaAdapter.cs

Casazen.Tests/
├── Unit/              # Unit tests
└── Integration/       # Integration tests (API)
```

## Key Design Patterns

1. **Repository Pattern**: All database access via `IRepository<T>`
2. **Dependency Injection**: Services registered in `Program.cs`
3. **Adapter Pattern**: Each OTA platform has dedicated adapter implementing `IOtaAdapter`

## Entity Relationships

- **User** (1) → (*) **Property**
- **Property** (1) → (*) **Booking**
- **Booking** (1) → (1) **Payment**
- **Booking** (1) → (*) **Guest**
- **Property** (1) → (*) **OtaIntegration**

## Important Conventions

- **Async methods**: Always suffix with `Async`
- **Database changes**: Require EF Core migration
- **Testing**: AAA pattern (Arrange-Act-Assert)
- **API routes**: `/api/{resource}` (e.g., `/api/properties`)

## Common File Locations

- **DB Context**: `Casazen.Infrastructure/Data/AppDbContext.cs`
- **Migrations**: `Casazen.Infrastructure/Data/Migrations/`
- **Dependency Injection**: `Casazen.Web/Program.cs`
- **Config**: `Casazen.Web/appsettings.json` (secrets in `.Development.json`)

## Tech Stack

- **.NET 10** + **C# 13**
- **EF Core** for ORM
- **SQL Server** database
- **Auth0** authentication
- **Stripe** payments
- **SendGrid** emails
- **xUnit** + **Moq** testing

This overview eliminates the need to explore files for basic navigation.
