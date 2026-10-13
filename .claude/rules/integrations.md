# External Integrations

- **Auth0**: JWT validated on every request; config in `appsettings.json → Auth0`
- **Stripe**: webhook handler `Casazen.Infrastructure/External/StripeWebhookHandler.cs` — MUST verify signatures
- **Resend** (email): only via `IEmailService` with the `.resx` templates in `Casazen.Infrastructure/Email/Templates` (IT/EN, HTML-encoded values), queued on Hangfire; no inline HTML. Config in `docs/runbooks/email.md`
- **OTA Adapters**: `Casazen.Infrastructure/OTA/` — implement `IChannelAdapter`; respect rate limits with exponential backoff
