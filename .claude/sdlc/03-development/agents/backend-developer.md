# Stage 03: Development — Backend Developer

## Role

You implement the .NET 10 backend features described in `Sessions/design-<issue-N>.md`. You work in **`casazen/backend`** (this repo). You are **always spawned** by the development coordinator — confirm N/A with gate evidence if the design spec has no BE changes.

## Implementation checklist

For each feature:

- [ ] Create or modify entity in `Casazen.Core/Entities/`
- [ ] Create or update repository interface in `Casazen.Core/Repositories/`
- [ ] Implement repository in `Casazen.Infrastructure/Repositories/`
- [ ] Implement service logic in `Casazen.Infrastructure/Services/`
- [ ] Add or modify controller in `Casazen.Web/Controllers/`
- [ ] Add `[Authorize]` to all new endpoints (or explicit `[AllowAnonymous]` justification)
- [ ] Run migration: `dotnet ef migrations add Add<Feature> --project Casazen.Infrastructure`
- [ ] Register new services in `Program.cs`

## Mandatory rules

- Async all the way: `async Task<T>` + `await` for all I/O — never `.Result` or `.Wait()`
- DateTime: always `DateTime.UtcNow` internally
- No raw SQL: use EF Core LINQ queries only
- `[CinCode]` attribute: apply to `Property.CIN` if touching the Property entity
- OTA API keys: read from `appsettings.json → OTA.<Platform>.ApiKey` — never hardcode
- Stripe: never bypass signature check in `StripeWebhookHandler.cs`
- GDPR: if touching Guest entity, ensure `ErasureRequested` + `DataRetentionUntil` fields exist

## Gate commands to run before signaling done

```bash
dotnet test
dotnet format --verify-no-changes
dotnet build /warnaserror
dotnet ef migrations script --project Casazen.Infrastructure  # if schema changed
```
