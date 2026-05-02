---
name: compliance-feature
description: Regulatory-driven feature creation (updates + gap analysis + competitive research + backlog)
invocable: true
---

# Compliance-Driven Feature Creation

Execute full compliance workflow: regulatory updates → gap analysis → competitive research → feature backlog → GitHub issues.

## Workflow

Load full workflow definition:

```
Read .claude/hooks/compliance-feature-creation.md
```

Then execute the workflow as documented, orchestrating:
- `@regulatory_agent` (update Italian regulations)
- `@analyzer_agent` (gap analysis vs codebase)
- `@scrum_master_casazen` (issue creation)

## Quick Summary

The workflow will:
0. ✅ **Verify planning & epics** - if missing, trigger strategic refinement meeting
   - Involves: `@product_owner` (vision), `@architect` (feasibility), `@scrum_master_casazen` (coordination)
   - Creates: Product roadmap + Epic issues on GitHub
1. ✅ Update Italian regulations (CIN, Alloggiati Web, Tourist Tax, GDPR)
2. ✅ Analyze compliance gaps in codebase
3. ✅ Research competitor features (Lodgify, Guesty, Hostaway)
4. ✅ Check existing features in codebase
5. ✅ Create prioritized backlog (P0 critical → P3 nice-to-have)
6. ✅ Open GitHub Issues for implementation (under relevant epics)

**Output**:
- `.claude/context/planning/product-roadmap.md` (consolidated: vision + feasibility + roadmap)
- Epic issues on GitHub (if didn't exist)
- `.claude/context/regulations/` updated
- `.claude/context/gap-analysis-YYYY-MM-DD.md` created
- N GitHub Issues created (prioritized backlog, linked to epics)

**Note**: Refinement meeting discussion happens in-memory (no intermediate files)

**Next step**: Use `/feature-implementation` to implement P0/P1 features

**Cadence**: Monthly (or ad-hoc when new regulation published)
