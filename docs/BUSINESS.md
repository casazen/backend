# CasaZen — Business Documentation

> CasaZen is a vacation rental management platform built for the Italian short-term rental market. It enables property owners to manage bookings, guests, and pricing across multiple Online Travel Agency (OTA) channels while staying fully compliant with Italian law — including CIN registration (D.L. 145/2023), police guest reporting (Alloggiati Web), tourist tax collection, and GDPR data protection.

---

## Overview

### Purpose
CasaZen gives Italian vacation rental owners a single platform to manage their properties, accept bookings from guests and OTA platforms, process payments, and meet all mandatory Italian regulatory obligations. Owners no longer need to juggle spreadsheets, manual police reports, or per-platform dashboards.

### Target users
- **Property owners** — individuals or small businesses renting short-term in Italy
- **Property managers** — professionals managing multiple properties on behalf of owners
- **Guests** — travellers who book directly or via OTA platforms (Airbnb, Booking.com, etc.)

### Core value proposition
CasaZen automates the most burdensome parts of Italian vacation rental management: real-time multi-channel synchronisation, compliant tourist tax calculation, mandatory police reporting (Alloggiati Web), and AI-driven dynamic pricing — all in one API-first platform.

---

## Domain Entities

> The core objects the system works with. Each entity represents a real-world concept in the business domain.

### Property
- **Purpose**: A vacation rental property listed by an owner.
- **Key attributes**: Name, address, city, bedrooms, bathrooms, max guests, nightly rate, cleaning fee, damage deposit, amenities, house rules, CIN code, timezone.
- **Relationships**: Owned by a user (`OwnerId`); has many `Booking`s, `OtaIntegration`s, an optional `CancellationPolicy`, and one `PricingAdapterConfig`.
- **Business significance**: The central asset. Every booking, payment, OTA sync, and pricing decision is attached to a property. Italian law (D.L. 145/2023) requires each property to carry a CIN code in the format `IT-XXXXX-XXXXXXXXXX`.

### Booking
- **Purpose**: A confirmed or pending stay at a property.
- **Key attributes**: Check-in date, check-out date, number of guests (adults / children), status, source platform, base price, tourist tax amount, total price, special requests.
- **Relationships**: Belongs to a `Property` and a `Guest`; has many `Payment`s and `AlloggiatiWebReport`s.
- **Business significance**: The core transactional record. Tourist tax is calculated per booking based on city rates. Status follows a strict lifecycle: Pending → Confirmed → CheckedIn → CheckedOut (or Cancelled). Check-in triggers a mandatory police report.

### Guest
- **Purpose**: A person who makes or participates in a booking.
- **Key attributes**: Name, email, phone, address, date of birth, nationality, document type and number, GDPR consent dates, data retention expiry.
- **Relationships**: Has many `Booking`s and `AlloggiatiWebReport`s.
- **Business significance**: Italian law requires full identity verification for police reporting. GDPR rules govern how long data may be retained (default 7 years) and mandate erasure on request (Article 17).

### Payment
- **Purpose**: A financial transaction linked to a booking.
- **Key attributes**: Amount, refunded amount, status, payment method, Stripe payment intent ID, transaction ID, processed timestamp.
- **Relationships**: Belongs to a `Booking`.
- **Business significance**: Payments flow through Stripe. Partial refunds are tracked separately via `RefundedAmount`. Status lifecycle: Pending → Processing → Completed / Failed / Refunded / PartiallyRefunded.

### TouristTaxRate
- **Purpose**: City-specific tourist tax rates mandated by Italian municipalities.
- **Key attributes**: Region, city, rate per night, maximum nights subject to tax, effective date range.
- **Relationships**: Referenced by the tax calculation service at booking creation time.
- **Business significance**: Tourist tax rates vary by city and change over time. They are stored in the database and must never be hardcoded. The system applies the rate valid at the time of the booking's check-in date.

### OtaIntegration
- **Purpose**: A connection between a property and an OTA platform.
- **Key attributes**: Platform name, external property ID, API key, last sync timestamp.
- **Relationships**: Belongs to a `Property`.
- **Business significance**: Enables bidirectional sync — pulling bookings from OTAs and pushing pricing and availability updates. Six platforms supported: Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda.

### AlloggiatiWebReport
- **Purpose**: A record of a guest data submission to the Italian police database (Alloggiati Web).
- **Relationships**: Linked to a `Booking` and a `Guest`.
- **Business significance**: Italian law (D.L. 286/1998, Art. 7) requires accommodation providers to report guest identities to the police within 24 hours of check-in. This entity tracks submission status.

### PricingAdapterConfig / PricingHistory
- **Purpose**: Configuration and audit trail for AI-driven dynamic pricing.
- **Key attributes**: Enabled flag, adaptation frequency, seasonality and public holiday flags, next scheduled run, last adapted timestamp, AI confidence score.
- **Business significance**: Allows owners to opt into automated price adjustments. Each price change is logged in `PricingHistory` with an AI confidence score for transparency.

---

## Business Processes

### New Booking Flow

**Trigger**: A property owner or guest submits a booking request (direct or OTA-sourced).

**Steps**:
1. System checks property availability for the requested dates.
2. If unavailable, the request is rejected with a clear error message.
3. Tourist tax is calculated using the city rate valid for the check-in date.
4. Total price = base price + tourist tax.
5. Booking is saved with status `Pending`.
6. Owner or OTA confirms the booking; status moves to `Confirmed`.

**Outcome**: A `Booking` record exists with a confirmed status and a correct, legally compliant total price.

**Business rules applied**: Property availability check; tourist tax calculation from `TouristTaxRate` entity; status lifecycle enforcement.

---

### Guest Check-In and Police Reporting

**Trigger**: A property owner marks a booking as checked-in via the API.

**Steps**:
1. System validates the booking is in `Confirmed` status.
2. System validates today's date is on or after the check-in date.
3. Booking status is updated to `CheckedIn`.
4. A background job is immediately enqueued to submit guest identity data to Alloggiati Web (Italian police system).

**Outcome**: Booking is active; Italian police registration obligation is fulfilled within 24 hours.

**Business rules applied**: Status must be `Confirmed`; check-in date must not be in the future; Alloggiati Web submission is mandatory and asynchronous to keep the API response fast.

---

### Payment Processing and Refund

**Trigger**: A payment record is created and then explicitly processed for a confirmed booking.

**Steps**:
1. A `Payment` record is created (status `Pending`) linked to the booking.
2. Owner or system triggers payment processing via Stripe.
3. On success, status becomes `Completed` and the processed timestamp is recorded.
4. If a refund is needed, a refund action initiates a Stripe refund (full or partial).
5. `RefundedAmount` is updated; status becomes `Refunded` or `PartiallyRefunded`.

**Outcome**: Payment is settled; revenue is attributable to a property and date range.

**Business rules applied**: Stripe webhook signatures must be verified before updating state; partial refund tracking via `RefundedAmount`.

---

### OTA Synchronisation

**Trigger**: Scheduled automatically (hourly full sync, every 15 minutes booking pull) or triggered manually.

**Steps**:
1. For each active `OtaIntegration`, the system connects to the platform API using stored credentials.
2. New bookings on the OTA are pulled and imported as local `Booking` records with the appropriate `BookingSource`.
3. Local availability and pricing updates are pushed to the OTA calendar.
4. Sync status and timestamp are recorded.
5. On transient failure, the system retries with exponential backoff (2s, 4s, 8s). After 5 consecutive failures the circuit breaker opens for 60 seconds.

**Outcome**: All connected OTA platforms reflect the latest availability and pricing; new OTA bookings appear in CasaZen.

**Business rules applied**: Rate limits respected per platform; OTA webhooks must respond within 3 seconds (long work offloaded to background jobs).

---

## Business Rules

| Rule | Description | Where enforced |
|---|---|---|
| CIN format | Property CIN code must match `IT-XXXXX-XXXXXXXXXX` | `CinCodeAttribute` validator on `Property` |
| Tourist tax source | Rates must come from the `TouristTaxRate` entity — never hardcoded | `TaxCalculationService` |
| Booking status machine | Only valid transitions: Pending→Confirmed→CheckedIn→CheckedOut; any state→Cancelled | `BookingsController` check-in/check-out actions |
| Check-in date validation | Cannot check in before the booking's check-in date | `BookingsController.CheckIn` |
| Availability check | A property cannot have overlapping confirmed bookings | `IBookingService.IsPropertyAvailableAsync` |
| GDPR data retention | Guest data retained for 7 years by default; erasure on request (Article 17) | `GdprService`, `GdprDataRetentionJob` |
| Police reporting | Guest data must be submitted to Alloggiati Web within 24 hours of check-in | `AlloggiatiWebReportJob` (background job) |
| Ownership authorisation | Only the property owner may modify or access data for their own property | `PropertiesController`, `BookingsController`, `PricingAdapterController` |
| Image limit | Maximum 20 photos per property | `PropertiesController.UploadImages` |
| OTA webhook response time | Must respond within 3 seconds; long operations use background jobs | Architecture convention |
| Payment refund tracking | Partial refund amount tracked in `RefundedAmount`; status reflects actual state | `PaymentService` / `PaymentsController` |

---

## Glossary

| Term | Definition |
|---|---|
| CIN | Codice Identificativo Nazionale — a unique national identification code assigned to each short-term rental property in Italy under D.L. 145/2023. Format: `IT-XXXXX-XXXXXXXXXX`. |
| Alloggiati Web | The Italian Police (Polizia di Stato) online portal for accommodation providers to register guest identities within 24 hours of check-in, as required by D.L. 286/1998, Art. 7. |
| Tourist tax (tassa di soggiorno) | A per-night fee collected by accommodation providers on behalf of Italian municipalities. Rates vary by city and are capped at a maximum number of nights. |
| OTA | Online Travel Agency — third-party booking platforms such as Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, and Agoda. |
| Cedolare secca | A flat-rate tax regime for individual property owners in Italy covering rental income. Relevant to how owners report revenue. |
| BookingSource | The channel through which a booking originated: Direct, Airbnb, BookingCom, Expedia, Vrbo, TripAdvisor, Agoda, or Local. |
| BookingStatus | The lifecycle state of a booking: Pending, Confirmed, CheckedIn, CheckedOut, Cancelled. |
| Circuit breaker | A resilience pattern that halts calls to an external service after repeated failures, preventing cascading errors. Used for all OTA integrations. |
| GDPR | General Data Protection Regulation — EU regulation governing the collection, storage, and erasure of personal data. Applies to all guest personal data. |
| Dynamic pricing | AI-driven automatic adjustment of nightly rates based on seasonality, demand signals, and public holidays. Managed via `PricingAdapterConfig`. |

---

## External Integrations (Business Perspective)

| Integration | Business purpose | Data exchanged |
|---|---|---|
| Airbnb | Sync bookings and availability; push pricing | Booking details in; availability and nightly prices out |
| Booking.com | Sync bookings and availability; push pricing | Same as Airbnb |
| Expedia | Sync bookings and availability; push pricing | Same as Airbnb |
| VRBO | Sync bookings and availability; push pricing | Same as Airbnb |
| TripAdvisor | Sync bookings and availability; push pricing | Same as Airbnb |
| Agoda | Sync bookings and availability; push pricing | Same as Airbnb |
| Stripe | Payment processing and refunds | Charge amounts, refund amounts, webhook payment events |
| Alloggiati Web | Mandatory police guest registration | Guest identity data (name, DOB, document details, nationality) |
| SendGrid | Transactional email notifications | Booking confirmations, receipts, reminders |
| Auth0 | User identity and authentication | JWT tokens validated on every API request |
