# ADR-014: Condition Builder Storage and Field Catalog

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-07, NF-24, NF-26

## Context

Marketing segments (NF-24) and automation routines (NF-26) need a condition builder to define guest/booking criteria. Conditions must be stored persistently and evaluated server-side.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `ExtractionResult` jsonb pattern at `LeaseDocument.cs:60` (ADR-009-R5)
- HostScope enforcement at `docs/TECHNICAL.md:136-143`
- No condition builder exists

**Repo scope:** Backend (condition storage, field catalog, evaluator), Frontend (condition builder UI)

## Decision

### JSON Column Storage

Store conditions in jsonb column on Segment and Routine entities:

```csharp
public class Segment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; }
    public string ConditionDefinitionJson { get; set; }  // jsonb
    public DateTime CreatedAt { get; set; }
}
```

JSON structure:
```json
{
  "version": 1,
  "groups": [
    {
      "operator": "AND",
      "conditions": [
        {"field": "totalStays", "operator": "GreaterThan", "value": 2},
        {"field": "propertyIds", "operator": "In", "value": ["prop1", "prop2"]}
      ]
    }
  ]
}
```

### Field Catalog in Code

Typed enum with operators per field type:

```csharp
public enum ConditionFieldType { Text, Number, Date, Boolean, Enum, Set }

public static class FieldCatalog
{
    public static readonly FieldDefinition[] Fields = new[]
    {
        new FieldDefinition("totalStays", ConditionFieldType.Number, new[] { "Equals", "GreaterThan", "LessThan", "Between" }),
        new FieldDefinition("lastStayAt", ConditionFieldType.Date, new[] { "Before", "After", "Between", "InLast" }),
        new FieldDefinition("propertyIds", ConditionFieldType.Set, new[] { "In", "NotIn" }),
        new FieldDefinition("hasMarketingConsent", ConditionFieldType.Boolean, new[] { "Is", "IsNot" }),
        new FieldDefinition("country", ConditionFieldType.Enum, new[] { "In", "NotIn" }),
        // ... more fields
    };
}
```

### Excluded Fields

Per compliance source veto (AC-NF07-5), field catalog MUST exclude:
- Alloggiati submission data (document type, number, issue place)
- Raw document images
- Biometric data

### Server-Side Evaluation Only

`IConditionEvaluationService.EvaluateAsync`:
- Server evaluates conditions against guest/booking data
- Frontend NEVER receives full guest data to evaluate client-side
- Frontend sends condition tree, backend returns count or match result

### HostScope Enforcement

Field value autocomplete (property list, tag list) MUST filter by caller HostScope:
- User with limited perimeter sees only their properties in picker
- Follows `IHostScopeResolver` pattern

### Version Identifier

Condition JSON includes `version` field for migration:
- Old versions readable but deprecated
- New condition saves to latest version
- Migration code transforms old → new on read

### Performance Target

Segment evaluation for 10k guests MUST complete in <5s.

### Rejected Alternatives

- **Separate ConditionDefinition table:** Overkill, single JSON column simpler
- **EAV (entity-attribute-value):** Complex queries, schema changes require code deploy (acceptable)
- **Client-side evaluation:** Cannot verify server-side, security risk

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-014-R1 | P0 | Segment.ConditionDefinitionJson, Routine.ConditionDefinitionJson (jsonb) MUST store `{version, groups:[{operator:AND|OR, conditions:[{field,operator,value}]}]}` |
| ADR-014-R2 | P0 | FieldCatalog enum MUST define field types (Text, Number, Date, Boolean, Enum, Set) and operators per type |
| ADR-014-R3 | P0 | IConditionEvaluationService.EvaluateAsync MUST run server-side, NEVER send full condition tree to client for evaluation |
| ADR-014-R4 | P0 | Field value autocomplete (property list, tag list) MUST filter by caller HostScope per IHostScopeResolver pattern |
| ADR-014-R5 | P0 | FieldCatalog MUST exclude Alloggiati fields (document type, number, issue place) per compliance source veto |
| ADR-014-R6 | P0 | POST /api/segments/preview MUST count matching guests in <5s for 10k guest org (performance target) |
| ADR-014-R7 | P0 | Condition builder component MUST render AND/OR group toggles (default AND, toggle to OR, nested groups allowed) |
| ADR-014-R8 | P0 | Field picker MUST load catalog from GET /api/conditions/fields and show searchable dropdown |
| ADR-014-R9 | P0 | Value input MUST match operator (text for equals/contains; number for >/</=; date picker for before/after; multi-select for in/notIn) |
| ADR-014-R10 | P0 | Segment preview button MUST call GET /api/segments/:id/preview and show count before save |
| ADR-014-R11 | P1 | Condition version migration strategy: old versions readable but deprecated, new condition saves to latest version |
| ADR-014-R12 | P1 | Max nesting depth: 3 levels (root, group, nested group) |
| ADR-014-R13 | P1 | Condition builder add/remove buttons MUST have min 44px touch target |

## Consequences

**Follow-on work:**
- Backend: Condition model + field catalog, segment entity + evaluator, routine entity integration
- Frontend: Condition builder UI (AND/OR groups, field picker, operator/value inputs), segment preview, routine test simulation
- Tests: Server-side evaluation proof test, performance test on 10k guests

**Risks:**
- Performance on complex conditions (10k guests + nested AND/OR)
- Field catalog versioning migration cost
- No PII in condition values (architecture test)

**CI cost:** +10s for performance test on seeded 10k guests
