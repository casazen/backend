# Code Style

## Async
- Use async/await for all I/O; suffix methods with `Async` (e.g. `GetUserAsync`)
- NEVER `.Result` or `.Wait()` — deadlock risk

## Database
- EF Core migration required for every schema change: `dotnet ef migrations add Add<Feature> --project Casazen.Infrastructure`
- Test migration locally before committing

## Testing
- Naming: `MethodName_Scenario_ExpectedBehavior` | Pattern: AAA | Mocking: `Mock<IRepository>`
- Coverage: critical 100%, services 80%, controllers 70%

## Before Committing
`dotnet test` · `dotnet format --verify-no-changes` · no compiler warnings

## i18n / Localization
- Frontend: every user-visible string uses `t()` via `useTranslation()`. Add keys to both `src/i18n/locales/it.json` and `en.json`. No hardcoded text, no `defaultValue` fallback.
- Backend: all API error messages and validation attributes use `IStringLocalizer<T>` with `.resx` resource files (Italian default + `en.resx`). No inline `ErrorMessage` strings.
- Both: when adding new features, budget time for i18n key creation in both languages — it's part of the definition of done, not an afterthought.
