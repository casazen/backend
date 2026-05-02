---
name: write-user-story
description: Generate a well-structured user story from a regulatory gap or feature requirement. Produces GitHub-ready issue body with user story, regulatory context, acceptance criteria, and technical notes.
---

# Write User Story

Transforms a regulatory gap or feature requirement into a GitHub-ready issue body.

## Format

```
As a [ROLE], I want [ACTION], so that [BENEFIT].
```

CasaZen roles: `owner` (manages properties) | `guest` (books/stays) | `administrator` (system management) | `system` (automated operations)

## Full Issue Template

```markdown
## User Story
As a **[role]**, I want **[action]**, so that **[benefit]**.

## Context
[Why this feature is needed. Regulatory reference if applicable.]

## Functional Requirements
- [ ] [requirement 1]
- [ ] [requirement 2]

## Non-Functional Requirements
- [ ] [performance, security, compliance constraint]

## Acceptance Criteria
- [ ] GIVEN [precondition], WHEN [action], THEN [expected result]
- [ ] GIVEN [precondition], WHEN [action], THEN [expected result]

## Technical Notes
[Files to modify, patterns to follow, migration needed, OTA impact]

## References
- [Regulatory source URL]
- [Relevant codebase file]
```

## Rules

**DO**:
- Write from the user's perspective, not the system's
- Include verifiable acceptance criteria (GIVEN/WHEN/THEN)
- Specify regulatory deadline and penalties when applicable
- Note if a DB migration is needed in Technical Notes

**DON'T**:
- Mix multiple features in one story (INVEST: Independent)
- Use technical jargon in the story sentence (reserve for Technical Notes)
- Write vague stories like "improve compliance"
- Omit acceptance criteria

## Example

**Input**: CRITICAL gap — CIN code missing from Property entity

```markdown
## User Story
As an **owner**, I want to **register and display the CIN code for my properties**,
so that **I comply with D.L. 145/2023 and avoid fines of €800–€8,000**.

## Context
D.L. 145/2023 (art. 13-ter) requires all short-term rental properties to obtain
a National Identification Code (CIN) from BDSR and display it on all listings.
Deadline: 01/03/2026 for existing operators.

## Functional Requirements
- [ ] CIN field on Property entity (format: IT-XXXXX-XXXXXXXXXX)
- [ ] Format validation (regex)
- [ ] CIN displayed on property detail page
- [ ] CIN included in OTA sync data
- [ ] Dashboard alert for properties without CIN

## Non-Functional Requirements
- [ ] CIN encrypted at rest
- [ ] Audit log for CIN changes

## Acceptance Criteria
- [ ] GIVEN a property without CIN, WHEN I open the dashboard, THEN I see a compliance alert
- [ ] GIVEN a valid CIN entered, WHEN I save the property, THEN it is persisted and displayed
- [ ] GIVEN a property with CIN, WHEN OTA sync runs, THEN CIN is included in the payload

## Technical Notes
- Add `CinCode` field to `Casazen.Core/Entities/Property.cs`
- Create EF Core migration: `AddCinCodeToProperty`
- Add regex validation `IT-[A-Z0-9]{5}-[A-Z0-9]{10}`
- Update OTA adapters in `Casazen.Infrastructure/OTA/` to include CIN in sync

## References
- D.L. 145/2023, art. 13-ter
- https://bdsr.ministeroturismo.it
```

## Sizing

- 1-5 days per story
- If larger → split into epic + stories
- Note the parent epic in the issue body
