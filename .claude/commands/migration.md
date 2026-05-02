---
description: Run EF Core migration workflow — create, review, dry-run, apply, verify. Pass migration name as argument.
disable-model-invocation: true
allowed-tools: Bash Read
---

Run the EF Core migration workflow for: $ARGUMENTS

Full instructions: @.claude/skills/migration-workflow/SKILL.md

Steps:
1. `dotnet ef migrations add $ARGUMENTS --project Casazen.Infrastructure`
2. Review generated Up()/Down() methods in Casazen.Infrastructure/Data/Migrations/
3. `dotnet ef migrations script --project Casazen.Infrastructure` (dry run)
4. `dotnet ef database update --project Casazen.Infrastructure`
5. `dotnet ef dbcontext info --project Casazen.Infrastructure` + `dotnet test`
