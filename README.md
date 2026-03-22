# 🏠🧘 CASAZEN - Backend (.NET 10)

## 🚀 Quick Start

### Prerequisites
- .NET 10 SDK
- SQL Server 2022+
- Docker (optional)

### Local Setup

```bash
# Clone repository
git clone https://github.com/casazen/casazen-backend.git
cd casazen-backend

# Restore packages
dotnet restore

# Configure database
cp appsettings.json appsettings.Development.json
# Edit connection string in appsettings.Development.json

# Create database
dotnet ef database update

# Run
dotnet run --project Casazen.Web
```

**API Swagger:** https://localhost:5001/swagger

### Docker Setup

```bash
docker-compose up -d

# View logs
docker-compose logs -f api

# Stop
docker-compose down
```

---

## 📋 Architecture

### Layered Architecture
```
Casazen.Web (Presentation)
├── Controllers
└── Middleware

Casazen.Core (Business Logic)
├── Entities
├── Repositories (Interfaces)
└── Services (Interfaces)

Casazen.Infrastructure (Data Access)
├── Data (DbContext)
├── Repositories (Implementations)
├── Services (Implementations)
└── OTA (External Adapters)

Casazen.Tests (Quality)
├── Unit Tests
└── Integration Tests
```

## 🔐 Authentication
**Auth0 Integration:**

1. Create Auth0 tenant
2. Configure application
3. Set in appsettings.json:

```json
{
  "Auth0": {
    "Domain": "your-tenant.auth0.com",
    "Audience": "https://casazen-api",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  }
}
```

JWT tokens automatically validated on /api endpoints

## 💳 Stripe Integration
**Payment Processing:**

1. Create Stripe account
2. Get API keys
3. Configure in `appsettings.json`:

```json
{
  "Stripe": {
    "PublishableKey": "pk_test_...",
    "SecretKey": "sk_test_...",
    "WebhookSecret": "whsec_..."
  }
}
```

Webhooks handled automatically

## 📮 Email (SendGrid)
**Email Notifications:**

1. Register SendGrid
2. Get API key
3. Configure in `appsettings.json`:

```json
{
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@casazen.app"
  }
}
```

## 🌐 OTA Integrations
**Supported Platforms:**

✅ Airbnb
✅ Booking.com
✅ Expedia
✅ VRBO
✅ TripAdvisor
✅ Agoda

**Setup:**

```bash
POST /api/ota/validate
{
  "platform": "airbnb",
  "apiKey": "YOUR_AIRBNB_API_KEY"
}
```

## 📊 Database
**SQL Server Schema:**

```
Users
├── Id (PK)
├── Email
├── FirstName
├── LastName
└── Role

Properties
├── Id (PK)
├── OwnerId (FK)
├── Name
├── City
├── NightlyRate
└── ...

Bookings
├── Id (PK)
├── PropertyId (FK)
├── GuestId (FK)
├── CheckInDate
├── CheckOutDate
├── TotalPrice
└── Status

Payments
├── Id (PK)
├── BookingId (FK)
├── Amount
├── Status
├── Method
└── TransactionId

OtaIntegrations
├── Id (PK)
├── PropertyId (FK)
├── Platform
├── ExternalPropertyId
├── ApiKey
└── LastSyncAt
```

## 🧪 Testing
```bash
# Run all tests
dotnet test

# Run specific test
dotnet test --filter "PropertyServiceTests"

# With coverage
dotnet test /p:CollectCoverage=true
```

## 📚 API Endpoints

### Properties
- `GET /api/properties` - List owner properties
- `GET /api/properties/{id}` - Get property
- `POST /api/properties` - Create
- `PUT /api/properties/{id}` - Update
- `DELETE /api/properties/{id}` - Delete

### Bookings
- `GET /api/bookings` - List bookings
- `GET /api/bookings/{id}` - Get booking
- `POST /api/bookings` - Create
- `PUT /api/bookings/{id}` - Update
- `DELETE /api/bookings/{id}` - Cancel

### Payments
- `GET /api/payments` - List payments
- `POST /api/payments` - Create
- `POST /api/payments/{id}/process` - Process
- `POST /api/payments/{id}/refund` - Refund

### OTA
- `POST /api/ota/sync` - Sync all platforms
- `GET /api/ota/status` - Get sync status
- `PUT /api/ota/pricing` - Update pricing

## 🔄 CI/CD
GitHub Actions workflow included:

- Build & Test on push
- Deploy on release tag

## 📞 Support
- **GitHub Issues:** https://github.com/casazen/issues
- **Email:** support@casazen.app
- **Docs:** https://casazen.app/docs

---

**🏠🧘 CASAZEN - Vacation rentals without stress!**
