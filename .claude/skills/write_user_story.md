# Skill: Write User Story - Well-Structured User Story Generation

## Description
This skill describes how to write well-structured user stories starting from regulatory requirements or gap analysis. The generated user stories are ready to be turned into GitHub issues.

> **Cross-project reusable**: this skill is generic and can be used in any project.

## When to Use It
- When the `analyzer_agent` has identified a regulatory gap
- When a technical requirement needs to be transformed into a clear task
- When creating GitHub issues via the `github_agent`

## User Story Format

### Base Template

    As a [ROLE],
    I want [ACTION/FEATURE],
    so that [BENEFIT/VALUE].

### Common Roles (CasaZen)
- **owner** - manages properties and rentals
- **guest** - books and stays
- **administrator** - manages the system
- **system** - automated operations

### Full Template for Issue

    ## User Story
    As a **[role]**, I want **[action]**, so that **[benefit]**.

    ## Context
    [Why this feature is needed. Regulatory reference if applicable.]

    ## Functional Requirements
    - [ ] [requirement 1]
    - [ ] [requirement 2]
    - [ ] [requirement N]

    ## Non-Functional Requirements
    - [ ] [performance, security, etc.]

    ## Acceptance Criteria
    - [ ] GIVEN [precondition], WHEN [action], THEN [expected result]
    - [ ] GIVEN [precondition], WHEN [action], THEN [expected result]

    ## Technical Notes
    [implementation hints, files to modify, patterns to follow]

    ## References
    - [regulatory link]
    - [documentation link]

## Writing Rules

### DO
- Write from the user's perspective, not the system
- Use clear and unambiguous language
- Always include verifiable acceptance criteria
- Specify regulatory context when applicable
- Indicate suggested priority

### DON'T
- Do not use technical jargon in the user story (reserve it for technical notes)
- Do not combine multiple features into a single story (INVEST principle - Independent)
- Do not write vague stories ("improve compliance")
- Do not omit acceptance criteria

## Complete Example

**Input**: CRITICAL gap - Missing CIN code management in properties

**Output**:

    ## User Story
    As an **owner**, I want to **register and manage the CIN code of my properties**, so that **I comply with regulatory requirements and avoid penalties ranging from €800 to €8,000**.

    ## Context
    Decree Law 145/2023 (art. 13-ter) introduced the obligation of the National Identification Code (CIN)
    for all properties used for short-term rentals. The CIN must:
    - Be obtained through the BDSR (Accommodation Facilities Database)
    - Be displayed in listings across all OTA platforms
    - Be physically displayed outside the property

    ## Functional Requirements
    - [ ] CIN field in the Property entity (format: IT-XXXXX-XXXXXXXXXX)
    - [ ] CIN format validation
    - [ ] Display CIN in the property details page
    - [ ] Include CIN in data synchronized with OTAs
    - [ ] Alert for properties without a registered CIN

    ## Non-Functional Requirements
    - [ ] CIN must be encrypted at rest in the database
    - [ ] Audit log for CIN changes

    ## Acceptance Criteria
    - [ ] GIVEN a property without CIN, WHEN I access the dashboard, THEN I see a non-compliance alert
    - [ ] GIVEN a valid CIN, WHEN I enter it in the property, THEN it is saved and displayed
    - [ ] GIVEN a property with CIN, WHEN I sync with an OTA, THEN the CIN is included in the data

    ## Technical Notes
    - Add `CinCode` field to `Property` entity in `Casazen.Core/Entities/`
    - Create EF Core migration for the column
    - Update OTA adapters in `Casazen.Infrastructure/OTA/` to include CIN
    - Add regex validation for CIN format

    ## References
    - Decree Law 145/2023, art. 13-ter
    - https://bdsr.ministeroturismo.it

## Sizing
- A user story should be completable within 1–5 days of development
- If larger, split it into multiple stories (epic → stories)
- Indicate if the story belongs to a broader epic  