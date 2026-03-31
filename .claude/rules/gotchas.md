# Common Gotchas

## Date/Time Handling
- Always use UTC internally: `DateTime.UtcNow`
- Convert to local timezone only for display
- Bookings use UTC to avoid DST issues

## Database Connections
- DbContext is scoped per request (don't store in static fields)
- Always dispose or use `using` statements
- Connection pooling handled automatically by EF Core

## OTA Sync Timing
- Webhooks must respond within 3 seconds (OTA timeout)
- Use background jobs for long-running sync operations
- Queue pattern: Webhook → Queue message → Background worker processes

## Regional Tax Rates
- Tourist tax varies by region/city in Italy
- Rates stored in database, not hardcoded
- Check `TaxRate` entity for current rates
