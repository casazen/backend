# External Integrations

## Authentication (Auth0)
- JWT tokens validated on every request
- Config in `appsettings.json` → `Auth0` section
- Domain: your-domain.auth0.com (set in appsettings.Development.json)

## Payments (Stripe)
- Webhook handler: `Casazen.Infrastructure/External/StripeWebhookHandler.cs`
- **MUST** verify webhook signatures (prevent spoofing)
- Test with Stripe CLI: `stripe listen --forward-to localhost:5001/webhooks/stripe`

## Email (SendGrid)
- Templates managed in SendGrid dashboard (not in code)
- Use template IDs, not inline HTML
- Test with SendGrid sandbox mode

## OTA Platforms
Each platform has adapter in `Casazen.Infrastructure/OTA/`:
- `AirbnbAdapter.cs`, `BookingAdapter.cs`, `ExpediaAdapter.cs`, etc.
- Adapters implement `IOtaAdapter` interface
- Rate limits vary by platform - respect them (implement retry with exponential backoff)

### OTA Sync Timing
- Webhooks must respond within 3 seconds (OTA timeout)
- Use background jobs for long-running sync operations
- Queue pattern: Webhook → Queue message → Background worker processes
