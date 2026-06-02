---
name: council-platform-devops-validator
description: DevOps and GitHub Flow gate validation for CasaZen AI-SDLC — CI/CD pipeline, Docker build, EF Core migration, post-deploy health.
---

# Council domain — Platform DevOps Validation

## Context to load before acting

1. Read `council/domain-context.md` sections: `tech-stack`, `docker-infrastructure`, `services`
2. Read `.claude/rules/github-flow-mandatory.md`
3. Read `.claude/rules/git-workflow.md`

## CasaZen CI/CD infrastructure

| Component | Location | Purpose |
|---|---|---|
| Main CI/CD | `.github/workflows/ci-cd.yml` | Build + test on push; deploy on release tag |
| PR Code Review | `.github/workflows/claude-code-review.yml` | Automated code review on PR open/update |
| Docker backend | `Dockerfile` | Multi-stage: SDK build → runtime image |
| Docker compose | `docker-compose.yml` | api + sqlserver services |
| Health endpoint | `GET /api/health` | Anonymous health check (backend) |

## GitHub Flow gate definitions

Every stage that produces code MUST enforce:
1. Branch: `feature/<name>` or `fix/<name>` — never work on `main`
2. Commit: Conventional Commits format (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`)
3. PR: opened via `gh pr create --base main` — STOP for review
4. No "Co-Authored-By: Claude" signature in commits
5. No merge to main without PR approval + CI pass

## Executable gate commands per stage

| Stage | Command | Expected result |
|---|---|---|
| 03-development | `dotnet test` | All tests pass |
| 03-development | `dotnet format --verify-no-changes` | Exit 0 (no format violations) |
| 03-development | `npm test` | All Vitest tests pass |
| 03-development | `tsc -b --noEmit` | No TypeScript errors |
| 03-development | `npm run lint` | No ESLint errors |
| 03-development | `npm run build` | Vite build succeeds |
| 03-development | `dotnet ef migrations script` | Migration script compiles without errors |
| 04-review | `gh pr view --json reviews` | At least 1 approval, no requested changes |
| 05-release | `gh pr checks` | All CI checks pass |
| 05-release | `docker build -t casazen-api .` | Build exits 0 |
| 05-release | `curl -f https://localhost:5001/api/health` | HTTP 200 |

## Artifact handoffs (what each stage produces)

| Stage | Exit artifact |
|---|---|
| 01-planning | GitHub Issue(s) with acceptance criteria |
| 02-design | `Sessions/design-<issue-id>.md` spec file |
| 03-development | Feature branch with open PR (`gh pr create`) |
| 04-review | PR with approval (review notes addressed) |
| 05-release | Merged PR, deployed version tag |
| 06-operations | Operations report + compliance audit file |

## Output shape

Per-stage DevOps gate assessment table + artifact handoff verification.
