# Italian Regulatory Compliance

## Domain Context
**Domain**: Short-term rental management in Italy
**Regulations**: D.L. 145/2023 (CIN codes), Alloggiati Web, Tourist tax, GDPR, Cedolare secca

## Regulatory Intelligence System
Automated agents monitor compliance:
- **regulatory_agent**: Collects regulatory updates from Italian government sources
- **analyzer_agent**: Analyzes gaps between regulations and codebase
- **github_agent**: Creates GitHub issues for compliance gaps

**Context Location**: @.claude/context/ (domain knowledge, regulations, codebase gaps)

## Compliance Features
When implementing features related to:

### CIN Codes
- Format: IT-XXXXX-XXXXXXXXXX
- Required per D.L. 145/2023
- Must be validated and stored for each property

### Guest Data
- GDPR compliant handling required
- Alloggiati Web reporting integration
- Data retention policies apply

### Tourist Tax
- Regional variations exist (not uniform across Italy)
- Automated calculation based on region/city
- Rates stored in database (check `TaxRate` entity)
- **NEVER** hardcode tax rates

**Always check** @.claude/context/regulations/ for current requirements before implementing compliance features.
