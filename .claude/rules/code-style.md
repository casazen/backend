# Code Style Rules

## Async Operations
- **ALWAYS** use async/await for I/O (database, HTTP, file operations)
- **ALWAYS** suffix async methods with "Async": `GetUserAsync()`, `SaveBookingAsync()`
- **NEVER** use `.Result` or `.Wait()` (causes deadlocks)

## Database
- **MUST** create EF Core migration for any schema change (entities, relationships, indexes)
- **MUST** test migrations locally before committing
- Migration naming: `Add{Feature}` (e.g., `AddCinCodeToProperty`)
- Commands:
  ```bash
  dotnet ef migrations add MigrationName --project Casazen.Infrastructure
  dotnet ef database update --project Casazen.Infrastructure
  ```

## Testing
- Test naming: `MethodName_Scenario_ExpectedBehavior`
- Pattern: Arrange-Act-Assert (AAA)
- Use Moq for mocking: `Mock<IRepository>`, not manual mocks
- Coverage targets: Critical logic 100%, Services 80%, Controllers 70%

## Before Committing
- [ ] Tests pass: `dotnet test`
- [ ] Code formatted: `dotnet format --verify-no-changes`
- [ ] No compiler warnings
