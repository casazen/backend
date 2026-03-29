# CasaZen Workflows

This directory contains workflow documentation for common development processes in CasaZen.

## Available Workflows

### 1. Feature Implementation (Cross-Repo)
**File**: `feature_implementation.md`

Complete workflow from PO request to production deployment for features that span both backend and frontend.

**Agents involved**:
- product_owner (global)
- architect (global)
- scrum_master (project-specific)
- feature_developer (global)
- test_engineer (global)
- code_reviewer (global)
- release_manager (global)

### 2. Regulatory Compliance
**File**: `regulatory_compliance.md`

Automated regulatory intelligence workflow for Italian vacation rental compliance.

**Agents involved**:
- regulatory_agent (project-specific)
- analyzer_agent (project-specific)
- github_agent (project-specific)

### 3. Critical Issue Response
**File**: `critical_issue_response.md`

Emergency response workflow for critical issues affecting production.

**Agents involved**:
- scrum_master (if affects both repos)
- issue_planner (global, for single-repo criticals)
- feature_developer (for hotfixes)

## Workflow Types

### Automated Workflows
Run on schedule without human intervention:
- Regulatory compliance monitoring (monthly)

### Triggered Workflows
Initiated by user request or event:
- Feature implementation (PO request)
- Critical issue response (incident detection)

### Hybrid Workflows
Combination of automated and manual steps:
- OTA integration (automated sync + manual testing)

## Agent Categories

### Global Agents (in ~/.claude/agents/)
Reusable across all projects:
- `product_owner` - Requirements gathering
- `architect` - Architecture design
- `issue_planner` - Issue planning
- `feature_developer` - Implementation
- `test_engineer` - Testing
- `code_reviewer` - Code review
- `release_manager` - Deployment
- `doc_writer` - Documentation

### Project-Specific Agents (in .claude/agents/)
CasaZen-specific logic:
- `scrum_master` - Cross-repo coordination (backend ↔️ frontend)
- `regulatory_agent` - Italian compliance monitoring
- `analyzer_agent` - Compliance gap analysis
- `github_agent` - Automated issue creation

## Usage

Each workflow document contains:
- **Overview**: What the workflow does
- **Trigger**: When/how it starts
- **Steps**: Detailed step-by-step process
- **Agents**: Which agents are involved at each step
- **Output**: What gets produced
- **Examples**: Real-world usage examples

Read the specific workflow file for detailed instructions.
