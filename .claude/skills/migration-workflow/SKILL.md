---
name: migration-workflow
description: Complete EF Core migration workflow for CasaZen. Covers create, review, dry-run, apply, and verify steps. Use whenever a schema change is needed.
---

# EF Core Migration Workflow

Required for **any** schema change. Never modify the database schema without a migration.

## Step 1 — Create

```bash
dotnet ef migrations add <MigrationName> --project Casazen.Infrastructure
```

Naming convention:
- `Add<Feature>`: new table or column — e.g., `AddCinCodeToProperty`
- `Update<Feature>`: modify existing — e.g., `UpdateBookingAddCheckInStatus`
- `Remove<Feature>`: drop table or column — e.g., `RemoveDeprecatedOtaField`

## Step 2 — Review

Check generated file in `Casazen.Infrastructure/Data/Migrations/<timestamp>_<Name>.cs`:
- `Up()` method: does it do exactly what you expect?
- `Down()` method: can the migration be safely rolled back?
- No unintended table drops or data loss

## Step 3 — Dry run (SQL preview)

```bash
dotnet ef migrations script --project Casazen.Infrastructure
```

Review the SQL before applying. Look for any destructive operations.

## Step 4 — Apply

```bash
dotnet ef database update --project Casazen.Infrastructure
```

## Step 5 — Verify

```bash
dotnet ef dbcontext info --project Casazen.Infrastructure
```

Then run the full test suite to confirm nothing broke:

```bash
dotnet test
```

## Rollback

```bash
# Roll back to a specific previous migration
dotnet ef database update <PreviousMigrationName> --project Casazen.Infrastructure

# Remove the last unapplied migration
dotnet ef migrations remove --project Casazen.Infrastructure
```

## Common Issues

- **Missing `DbSet<>`**: add entity to `AppDbContext.cs` before creating migration
- **Navigation property errors**: ensure foreign key relationships are configured in `OnModelCreating`
- **Missing index**: add explicit `migrationBuilder.CreateIndex()` in `Up()` if EF doesn't generate it
- **Migration conflict**: if two branches created migrations simultaneously, resolve by removing one and recreating with both changes combined

## Include in PR

Always include the migration file in the same PR as the code that requires it.
The CI pipeline runs `dotnet ef migrations script` to detect schema drift.
