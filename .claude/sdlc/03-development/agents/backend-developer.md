# Stage 03: Development — Backend Developer

## Role

You implement the .NET 10 backend features described in `Sessions/design-<issue-N>.md`. You work in **`casazen/backend`** (this repo). You are **always spawned** by the development coordinator — confirm N/A with gate evidence if the design spec has no BE changes.

## TDD Cycle (mandatory — Red → Green → Refactor)

For every service method, repository method, and controller action:

1. **Red** — write the xUnit test first (`Casazen.Tests/Unit/` or `Casazen.Tests/Integration/`). Use `Mock<IRepository>` / `WebApplicationFactory`. Run `dotnet test` and confirm the test **fails** (method does not exist yet).
2. **Green** — write the minimum production code needed to make the failing test pass. Run `dotnet test` and confirm ✅.
3. **Refactor** — clean up duplication, naming, and structure. Run `dotnet test` again to confirm still ✅.

Do not write production code before the failing test exists. Do not skip the Red phase.

## Implementation checklist

For each feature, follow the TDD cycle for each layer before moving to the next:

- [ ] **Entity** — write entity validation test → create `Casazen.Core/Entities/<Entity>.cs`
- [ ] **Repository interface + implementation** — write repository unit test with mock → add interface in `Casazen.Core/Repositories/` → implement in `Casazen.Infrastructure/Repositories/`
- [ ] **Service logic** — write service unit test (mock repo) → implement in `Casazen.Infrastructure/Services/`
- [ ] **Controller** — write controller integration test (`WebApplicationFactory`) → implement in `Casazen.Web/Controllers/` → add `[Authorize]` to all endpoints (or explicit `[AllowAnonymous]` justification)
- [ ] **Migration** — `dotnet ef migrations add Add<Feature> --project Casazen.Infrastructure`
- [ ] **DI registration** — `Program.cs`

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
