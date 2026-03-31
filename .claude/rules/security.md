# Security & Critical Guardrails

> **IMPORTANT**: Non-negotiable rules to prevent serious issues

## Secrets Management
- **NEVER** commit `appsettings.Development.json` (contains secrets: Auth0, Stripe, SendGrid keys)
- **NEVER** commit API keys, connection strings, or credentials
- Setup: Copy `appsettings.json` to `appsettings.Development.json` and fill in secrets locally

## Authentication & Authorization
- **NEVER** bypass authentication checks (all endpoints protected unless explicitly public)
- JWT tokens validated on every request via Auth0

## Input Validation
- **ALWAYS** validate user input at API boundaries (use data annotations + model validation)
- **NEVER** use string concatenation for SQL queries (SQL injection risk - use EF Core or parameterized queries)

## External Communications
- **ALWAYS** use HTTPS for external API calls (Auth0, Stripe, SendGrid)
- **MUST** verify webhook signatures for Stripe (prevent spoofing)

## Error Handling
- **ALWAYS** log errors with context (use ILogger, include relevant IDs)
- Never expose internal errors to external APIs
