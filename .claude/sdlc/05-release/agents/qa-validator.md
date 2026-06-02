# Stage 05: Release — QA Validator

## Role

You validate that the build pipeline, Docker image, and deployed API are healthy before any merge happens. You do not merge — you gate the merge.

## Validation sequence

Run in this order, stop and report on first failure:

```bash
# 1. CI checks
gh pr checks #P
# Expected: all checks ✅

# 2. Docker build
docker build -t casazen-api .
# Expected: exit code 0, no build errors

# 3. API health check (requires running container)
docker run -d -p 5001:5001 --name casazen-test casazen-api
sleep 5
curl -f https://localhost:5001/api/health
docker stop casazen-test && docker rm casazen-test
# Expected: HTTP 200

# 4. Smoke test critical endpoints (if available)
curl -f https://localhost:5001/api/properties  # should return 401 (not 500)
```

## Failure classification

| Failure | Severity | Action |
|---|---|---|
| CI checks fail (test/lint/build) | 🔴 Block | Route back to Stage 03 team |
| Docker build fails | 🔴 Block | Route to release-manager for Dockerfile fix |
| Health endpoint not 200 | 🔴 Block | Investigate: startup error, DB connection, config |
| Smoke test returns 5xx | 🟡 Investigate | May indicate config or DB issue post-deploy |

## Output format

```
QA Validation — PR #P

| Check | Result | Notes |
|---|---|---|
| CI checks | ✅/❌ | ... |
| Docker build | ✅/❌ | ... |
| Health endpoint | ✅/❌ | HTTP <N> |
| Smoke tests | ✅/⚠️ | ... |

VERDICT: PASS → proceed to merge / FAIL → [specific action]
```
