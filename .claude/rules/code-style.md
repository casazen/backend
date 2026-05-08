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
