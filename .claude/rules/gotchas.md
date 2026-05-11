# Common Gotchas

- **DateTime**: always `DateTime.UtcNow` internally; convert to local only for display
- **DbContext**: scoped per request — no static fields, always dispose
- **OTA Webhooks**: must respond within 3s → use background jobs for long ops (Webhook → Queue → Worker)
- **Tax rates**: vary by region/city, stored in `TaxRate` entity — never hardcode
