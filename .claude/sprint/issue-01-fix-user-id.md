## Problem

**Current Issue**: Type mismatch between User.Id and Property.OwnerId causes authentication failures.

- **User.Id** is `string` (Auth0 sub claim format: "auth0|123abc")
- **Property.OwnerId** is `Guid` (database primary key)

This causes runtime errors when trying to match property owners to authenticated users.

## User Story

As a **developer**, I want to **change Property.OwnerId from Guid to string**, so that **it matches Auth0's user ID format and eliminates authentication errors**.

## Technical Details

### Files to Modify

1. **Casazen.Core/Entities/Property.cs**
   - Change `OwnerId` from `Guid` to `string`
   - Add `[MaxLength(255)]` attribute

2. **Migration**
   - Create migration: `dotnet ef migrations add FixOwnerIdType --project Casazen.Infrastructure`
   - Migration steps:
     1. Add new column `OwnerIdString`
     2. Migrate data: `UPDATE Properties SET OwnerIdString = CONVERT(NVARCHAR(255), OwnerId)`
     3. Drop old `OwnerId` column
     4. Rename `OwnerIdString` to `OwnerId`
     5. Make NOT NULL

3. **Casazen.Infrastructure/Services/PropertyService.cs**
   - Update all methods using `OwnerId`

4. **Casazen.Web/Controllers/PropertiesController.cs**
   - Update authorization checks
   - Use `User.FindFirst(ClaimTypes.NameIdentifier)?.Value` for owner ID

## Acceptance Criteria

- [ ] Property.OwnerId is string type
- [ ] Migration created and tested locally
- [ ] PropertyService methods updated
- [ ] PropertiesController authorization works with Auth0 sub
- [ ] All existing tests pass
- [ ] Integration test: authenticated user can only access their properties

## Definition of Done

- [ ] Code changes completed
- [ ] Database migration created and applied locally
- [ ] Unit tests updated and passing
- [ ] Integration test added for authorization
- [ ] Code reviewed
- [ ] Documentation updated (if needed)

## Estimated Effort

**1 day**

## Priority

⚠️ **CRITICAL** - Must be fixed before any other property-related features

## Dependencies

None (should be done first)
