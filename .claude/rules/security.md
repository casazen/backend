# Security Guardrails

- **NEVER** commit `appsettings.Development.json` or any secrets/API keys
- **NEVER** bypass auth checks — all endpoints require JWT (Auth0) unless explicitly public
- **ALWAYS** validate input at API boundaries (data annotations + model validation)
- **NEVER** string-concatenate SQL — use EF Core or parameterized queries
- **ALWAYS** use HTTPS for external calls; verify Stripe webhook signatures
- **ALWAYS** log errors with context (`ILogger` + relevant IDs); never expose internals externally
