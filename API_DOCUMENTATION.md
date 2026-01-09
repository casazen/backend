```markdown

CASAZEN API Documentation
Base URL
Development: `https://localhost:5001/api\`

Production: `https://api.casazen.app/api\`

Authentication
All endpoints (except login) require Auth0 JWT token in Authorization header:

```
Authorization: Bearer <JWT_TOKEN>
```

Properties API
List Properties
```http
GET /properties
Authorization: Bearer {token}
```

Response:
```json
[
{
"id": "550e8400-e29b-41d4-a716-446655440000",
"name": "Luxury Villa",
"city": "Rome",
"nightlyRate": 150.00,
"bedrooms": 3,
"maxGuests": 6
}
]
```

Create Property
```http
POST /properties
Authorization: Bearer {token}
Content-Type: application/json

{
"name": "New Villa",
"address": "Via Roma 123",
"city": "Rome",
"bedrooms": 3,
"bathrooms": 2,
"maxGuests": 6,
"nightlyRate": 150.00
}
```

Bookings API
Get Calendar
```http
GET /bookings/calendar?propertyId=xxx&startDate=2024-01-01&endDate=2024-12-31
Authorization: Bearer {token}
```

Create Booking
```http
POST /bookings
Authorization: Bearer {token}
Content-Type: application/json

{
"propertyId": "550e8400-e29b-41d4-a716-446655440000",
"guestId": "550e8400-e29b-41d4-a716-446655440001",
"checkInDate": "2024-02-01",
"checkOutDate": "2024-02-05",
"numberOfGuests": 4,
"totalPrice": 600.00
}
```

Payments API
Process Payment
```http
POST /payments/{id}/process
Authorization: Bearer {token}
```

Refund Payment
```http
POST /payments/{id}/refund
Authorization: Bearer {token}
```

OTA API
Sync All Platforms
```http
POST /ota/sync?propertyId=xxx
Authorization: Bearer {token}
``\

Get Sync Status
```http
GET /ota/status?propertyId=xxx
Authorization: Bearer {token}
``\

Error Responses
400 Bad Request
```json
{
"message": "Validation failed",
"errors": {
"name": ["Name is required"]
}
}
``\

401 Unauthorized
```json
{
"message": "Unauthorized",
"error": "Invalid or expired token"
}
``\

500 Internal Server Error
```json
{
"message": "An error occurred processing your request",
"error": "Internal server error",
"timestamp": "2024-01-15T10:30:00Z"
}
``\

Rate Limiting
1000 requests/hour per user

100 requests/minute per endpoint

Pagination
```http
GET /bookings?page=1&pageSize=20
``\

Filtering
```http
GET /properties/search?city=Rome&bedrooms=3&maxPrice=200
``\

Webhook Events
Booking Created
```json
{
"event": "booking.created",
"data": { "bookingId": "xxx" }
}
``\

Payment Completed
```json
{
"event": "payment.completed",
"data": { "paymentId": "xxx" }
}
``\

```