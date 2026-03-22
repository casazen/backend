# CASAZEN Backend - Project Context

## Project Overview
CASAZEN is a vacation rental property management system built with .NET 10. It provides integrated booking management, payment processing, and synchronization with multiple OTA (Online Travel Agency) platforms.

## Architecture
**Layered Architecture Pattern:**
- **Casazen.Web** - ASP.NET Core Web API (presentation layer)
- **Casazen.Core** - Domain entities, repository/service interfaces (business logic)
- **Casazen.Infrastructure** - Data access, external service implementations
- **Casazen.Tests** - Unit and integration tests

## Technology Stack
- **.NET 10** - Primary framework
- **SQL Server 2022+** - Database
- **Entity Framework Core** - ORM
- **Auth0** - Authentication/authorization
- **Stripe** - Payment processing
- **SendGrid** - Email notifications

## External Integrations
**OTA Platforms:** Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda

**Third-party Services:**
- Auth0 for JWT authentication
- Stripe for payments and webhooks
- SendGrid for email notifications

## Key Entities
- **Property** - Rental properties with owner relationships
- **Booking** - Reservations with guest, property, and date details
- **Payment** - Transaction records linked to bookings
- **Guest** - Customer information
- **OtaIntegration** - Platform-specific sync configurations

## Development Guidelines

### Code Style
- Follow Microsoft C# coding conventions
- Use async/await for I/O operations
- Implement repository pattern for data access
- Use dependency injection throughout

### Database
- Entity Framework migrations for schema changes
- Connection string in appsettings.Development.json (not committed)
- SQL Server LocalDB for local development

### Testing
- Write unit tests for services
- Integration tests for API endpoints
- Use xUnit framework

### API Conventions
- RESTful endpoints under /api
- JWT authentication required for protected endpoints
- Swagger documentation at /swagger

## Common Commands
```bash
# Run application
dotnet run --project Casazen.Web

# Run tests
dotnet test

# Create migration
dotnet ef migrations add MigrationName --project Casazen.Infrastructure

# Update database
dotnet ef database update --project Casazen.Infrastructure

# Restore packages
dotnet restore
```

## Important Notes
- Never commit API keys or secrets to version control
- Use appsettings.Development.json for local configuration (gitignored)
- Webhook handlers are in Casazen.Infrastructure/External/
- OTA adapters follow the adapter pattern in Casazen.Infrastructure/OTA/
- All services should have corresponding interfaces in Casazen.Core

## CI/CD
GitHub Actions workflow configured for:
- Build and test on push
- Deployment on release tags

## Current Status
The project is in active development with core infrastructure recently implemented. Recent commits show:
- Core entities and services defined
- Infrastructure implementations added
- Initial test suite created
- CI/CD pipeline configured
