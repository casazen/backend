---
name: contract-audit
description: FE/BE sync gap analysis - identifies misalignments between frontend and backend
invocable: true
---

# Contract Audit: FE/BE Sync Gap Analysis

Execute comprehensive audit to identify contract misalignments between frontend and backend.

## Workflow

Load full workflow definition:

```
Read .claude/hooks/contract-audit.md
```

Then execute the workflow as documented, invoking `@scrum_master_casazen` with GitHub MCP access.

## Quick Summary

The audit will:
1. ✅ Read backend API, DTOs, controllers
2. ✅ Read frontend types, API client, queries
3. ✅ Identify gaps (missing types, wrong endpoints, outdated docs)
4. ✅ Open GitHub Issues for each gap (categorized by severity)
5. ✅ Create summary issue on backend repo

**Output**: N GitHub Issues created (ready for implementation)

**Next step**: Use `/feature-implementation` to fix identified gaps
