# Skill: Classify Topic - Regulatory Classification by Macro-Topic

## Description
This skill describes how to classify a regulatory measure or legislative update within the macro-topics defined for short-term rentals monitoring.

## When to Use It
- When you have extracted a new regulatory measure and need to catalog it
- When you need to decide which context file to insert information into
- When a measure affects multiple topics and you need to determine the primary classification

## Taxonomy

### Macro-Topics
| ID | Topic | Keywords | Context File |
|----|-------|----------|--------------|
| 1 | Codice CIN | CIN, identification code, BDSR, registration | `regulations/cin.md` |
| 2 | Alloggiati Communication | alloggiati, police headquarters, PS, registration form, check-in | `regulations/alloggiati.md` |
| 3 | Tourist Tax | imposta soggiorno, tourist tax, local tribute | `regulations/imposta_soggiorno.md` |
| 4 | Tax Regime | cedolare secca, 21% withholding, income, IRPEF, declaration | `regulations/fiscale.md` |
| 5 | OTA Regulations | platform, intermediary, DAC7, reporting obligations | `regulations/ota_normativa.md` |
| 6 | GDPR | privacy, personal data, consent, GDPR, processing | `regulations/gdpr.md` |
| 7 | Safety | safety, fire extinguisher, detector, structural requirements | `regulations/sicurezza.md` |
| 8 | Regional Regulations | region, regional, municipal, local | `regulations/regionale.md` |

## Classification Procedure

### Step 1: Keyword Analysis
Analyze the measure text looking for keywords in the table above.

### Step 2: Primary Classification
Assign the macro-topic that best represents the main subject of the measure.

**Precedence Rules** (in case of ambiguity):
1. If the measure specifically concerns CIN -> topic 1
2. If it concerns reporting obligations to Police Headquarters -> topic 2
3. If it concerns local taxes -> topic 3
4. If it concerns national taxation -> topic 4
5. If it concerns online platform obligations -> topic 5
6. If it concerns data processing -> topic 6
7. If it concerns physical property requirements -> topic 7
8. If it is specific to a region -> topic 8

### Step 3: Secondary Classification
If the measure affects multiple topics:
- Insert the content into the primary topic file
- Add a cross-reference in the secondary topic files

Cross-reference format:
```markdown
> See also: `regulations/[primary_file].md` - [brief connection description]
```

### Step 4: Tags
Assign the following tags to the measure:
- **scope**: `national` | `regional` | `european`
- **status**: `in_force` | `pending` | `repealed`
- **urgency**: `immediate` | `upcoming_deadline` | `informational`

## Example

**Input**: "D.L. 145/2023 introduces the obligation of the National Identification Code (CIN) for accommodation facilities and properties intended for short-term rentals. The CIN must be displayed in the listing and on the outside of the property. Penalties from 800 to 8,000 euros foreseen."

**Output**:
- **Primary classification**: 1 - Codice CIN
- **Secondary classification**: 5 - OTA Regulations (display obligation on platforms)
- **Tags**: scope=national, status=in_force, urgency=immediate
- **Destination file**: `regulations/cin.md`
- **Cross-reference**: in `regulations/ota_normativa.md` add CIN reference