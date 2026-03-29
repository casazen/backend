# Error Learning Log - CasaZen Backend

> Track errors, mistakes, and lessons learned to prevent future issues and continuously improve.

## Format

```markdown
## YYYY-MM-DD - [Error Type]

**What Happened**: [Description]
**Context**: [What you were doing]
**Root Cause**: [Why it happened]
**Solution**: [How fixed]
**Lesson**: [Key takeaway]
**Prevention**: [How to avoid]
```

## Categories
Build, Tests, Database, Integration, Compliance, Security, Performance, Configuration, Logic

---

## Example Entry

## 2024-03-15 - Database Migration

**What Happened**: Migration failed in production with constraint violation
**Context**: Adding NOT NULL CIN column to Property table with existing data
**Root Cause**: Can't add NOT NULL to non-empty table
**Solution**: Two-phase migration: (1) add nullable, (2) populate, (3) add constraint
**Lesson**: Always consider existing data when adding constraints
**Prevention**:
- Test migrations with production-like data
- Use two-phase approach for constraints
- Add migration testing to CI with seed data

---

## Your Entries Start Here

_(Add new entries above this line, most recent first)_
