---
name: migration-workflow
description: Execute complete EF Core migration workflow (create + test + apply)
invocable: true
---

# EF Core Migration Workflow

This skill provides the complete workflow for database migrations.

## Workflow Steps

1. **Create Migration**
   ```bash
   dotnet ef migrations add {MigrationName} --project Casazen.Infrastructure
   ```

2. **Review Migration**
   - Check generated files in `Casazen.Infrastructure/Data/Migrations/`
   - Verify `Up()` and `Down()` methods

3. **Test Migration (Dry Run)**
   ```bash
   dotnet ef migrations script --project Casazen.Infrastructure
   ```

4. **Apply Migration**
   ```bash
   dotnet ef database update --project Casazen.Infrastructure
   ```

5. **Verify Schema**
   ```bash
   dotnet ef dbcontext info --project Casazen.Infrastructure
   ```

## Migration Naming Convention

- `Add{Feature}`: Adding new table/column (e.g., `AddCinCodeToProperty`)
- `Update{Feature}`: Modifying existing structure
- `Remove{Feature}`: Removing table/column

## Common Issues

- **Missing DbContext**: Check `AppDbContext.cs` has the entity as `DbSet<>`
- **Navigation properties**: Ensure proper relationships configured
- **Index creation**: Add explicit index in migration if needed

## Rollback

```bash
dotnet ef database update {PreviousMigrationName} --project Casazen.Infrastructure
dotnet ef migrations remove --project Casazen.Infrastructure
```

Invoke this skill when performing database schema changes.
