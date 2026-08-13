# 🏠🧘 CASAZEN - Backend (.NET 10)

## 🚀 Quick Start

### Prerequisites
- .NET 10 SDK
- PostgreSQL 16 (local `casazen_dev` or Supabase — see `docs/INFRA.md`)

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

Local PostgreSQL and Railway deploy use the root `Dockerfile` (no `docker-compose.yml`). See `docs/INFRA.md`.

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

## 📮 Email (SMTP via MailKit)

CasaZen uses **MailKit** to send transactional emails via any SMTP server.  
Two configuration modes are supported:

### Mode 1 — Direct SMTP (recommended for zero-cost start)

Use any free SMTP provider. **Gmail SMTP** is the simplest free option:

| Config key | Example value |
|---|---|
| `Email__SmtpHost` | `smtp.gmail.com` |
| `Email__SmtpPort` | `587` |
| `Email__SmtpUsername` | `casazen@gmail.com` |
| `Email__SmtpPassword` | 16-char app password (see below) |
| `Email__FromAddress` | `noreply@casazen.app` |

**Gmail setup (free, 500 emails/day):**
1. Create a Gmail account (or use an existing one)
2. Enable 2-Step Verification at https://myaccount.google.com/security
3. Generate an **App Password** at https://myaccount.google.com/apppasswords
4. Use the 16-character app password as `Email__SmtpPassword`

### Mode 2 — SendGrid SMTP relay (100 emails/day free)

If you already have a SendGrid account, the SMTP relay still works:

| Config key | Example value |
|---|---|
| `Email__SendGridApiKey` | `SG.xxxxxxxxxxxxxx` |

When only `Email__SendGridApiKey` is set (no `Email__SmtpHost`), the service auto‑connects to `smtp.sendgrid.net:587` using `"apikey"` as the username.

### Other free SMTP providers

| Provider | Free tier | SMTP host |
|---|---|---|
| **Brevo** (ex Sendinblue) | 300 emails/day | `smtp-relay.brevo.com` |
| **Mailgun** | 100 emails/day (requires card) | `smtp.mailgun.org` |
| **Ethereal** | Fake SMTP for dev/testing only | `smtp.ethereal.email` |

### Configuration in `appsettings.json`

```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "SmtpUsername": "casazen@gmail.com",
    "SmtpPassword": "",
    "SendGridApiKey": "",
    "FromAddress": "noreply@casazen.app"
  }
}
```

> ⚠️ **Never commit** real credentials. Set them via **Railway environment variables** (`Email__SmtpHost`, etc.) or `appsettings.Development.json` (gitignored).

## 🌐 OTA Integrations
**Supported Platforms:**

✅ Airbnb
✅ Booking.com
✅ Expedia
✅ VRBO
✅ TripAdvisor
✅ Agoda

### Resilience Patterns

All OTA integrations implement production-grade resilience patterns using **Polly**:

**Retry Policy:**
- Exponential backoff: 2s, 4s, 8s
- Retries on transient HTTP errors (500-599, timeout)
- Configurable retry count per platform

**Circuit Breaker:**
- Opens after 5 consecutive failures (default)
- Stays open for 60 seconds (configurable)
- Prevents cascading failures
- Logs circuit state changes

**Timeout Policy:**
- Per-request timeout: 30 seconds (default)
- Prevents long-running requests from blocking
- Works with retry policy for total bounded time

**Rate Limiting:**
- Per-platform request limits
- Sliding window algorithm
- Concurrent request limits
- Prevents API quota exhaustion

**Configuration:**

```json
{
  "OTA": {
    "Resilience": {
      "Airbnb": {
        "RetryCount": 3,
        "CircuitBreakerFailures": 5,
        "CircuitBreakerDurationSeconds": 60,
        "TimeoutSeconds": 30,
        "MaxRequestsPerWindow": 100,
        "WindowDurationSeconds": 60,
        "MaxConcurrentRequests": 10
      }
    }
  }
}
```

**Observability:**
- All resilience events logged (retry, circuit open/close, timeout)
- Structured logging with platform context
- Integrates with application logging infrastructure

### Airbnb Integration Setup

**Prerequisites:**
1. Create an Airbnb partner account at [https://www.airbnb.com/partner](https://www.airbnb.com/partner)
2. Register your application and obtain API credentials
3. Generate an OAuth 2.0 access token

**Configuration:**

Add Airbnb credentials to `appsettings.Development.json`:

```json
{
  "OTA": {
    "Airbnb": {
      "BaseUrl": "https://api.airbnb.com/v2",
      "ApiKey": "YOUR_AIRBNB_OAUTH_TOKEN"
    },
    "Resilience": {
      "Airbnb": {
        "RetryCount": 3,
        "CircuitBreakerFailures": 5,
        "CircuitBreakerDurationSeconds": 60,
        "TimeoutSeconds": 30
      }
    }
  }
}
```

**API Validation:**

```bash
POST /api/ota/validate
{
  "platform": "airbnb",
  "apiKey": "YOUR_AIRBNB_OAUTH_TOKEN"
}
```

**Features:**
- Sync bookings from Airbnb (GET /api/ota/bookings)
- Update calendar availability (PUT /api/ota/availability)
- Update nightly pricing (PUT /api/ota/pricing)
- Automatic retry with exponential backoff
- Circuit breaker pattern for fault tolerance
- Rate limiting (respects Airbnb API limits)

**API Endpoints:**
- `GET /api/ota/bookings?platform=airbnb&propertyId={id}&startDate={date}&endDate={date}` - Fetch bookings
- `PUT /api/ota/availability` - Update availability
- `PUT /api/ota/pricing` - Update pricing

**Testing:**

Use test credentials for development:
```bash
# Validate credentials
curl -X POST https://localhost:5001/api/ota/validate \
  -H "Content-Type: application/json" \
  -d '{"platform":"airbnb","apiKey":"test_token"}'

# Fetch bookings
curl -X GET "https://localhost:5001/api/ota/bookings?platform=airbnb&propertyId=123&startDate=2026-04-01&endDate=2026-04-30" \
  -H "Authorization: Bearer {your_jwt_token}"
```

**Rate Limits:**
- Airbnb API: 5 requests/second per listing
- 200 requests/minute per account
- Automatically handled by built-in rate limiter

**Error Handling:**
- Network errors: Automatic retry up to 3 times
- 5xx errors: Circuit breaker activates after 5 failures
- 4xx errors: Logged and returned without retry
- Timeout: 30 seconds per request

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
