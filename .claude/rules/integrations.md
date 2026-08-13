# External Integrations

- **Auth0**: JWT validated on every request; config in `appsettings.json → Auth0`
- **Stripe**: webhook handler `Casazen.Infrastructure/External/StripeWebhookHandler.cs` — MUST verify signatures
- **SendGrid**: use template IDs (managed in dashboard), not inline HTML
- **OTA Adapters**: `Casazen.Infrastructure/OTA/` — implement `IChannelAdapter`; respect rate limits with exponential backoff
