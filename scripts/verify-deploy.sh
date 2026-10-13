#!/usr/bin/env bash
# Verifies a Railway deployment after a push (FD-12, A9-19). Used by ci-cd.yml (verify-test / verify-prod).
#
# usage: scripts/verify-deploy.sh <api-base-url> <expected-commit-sha> [timeout-seconds]
#
# Polls <api-base-url>/api/health/ready until the API serving it runs <expected-commit-sha> (the commit Railway
# exposes as RAILWAY_GIT_COMMIT_SHA) and answers 200 (healthy or degraded). A newer commit of the same branch that
# already contains the expected one is accepted too (Railway deploys the head of the branch). Fails when the URL is
# not configured, when the deployment never shows up or never becomes ready before the timeout, or when the new
# deployment stays unhealthy (503). Runbook: docs/runbooks/health-checks.md.
set -uo pipefail

BASE_URL="${1:-}"
EXPECTED="$(printf '%s' "${2:-}" | tr 'A-F' 'a-f')"
TIMEOUT_SECONDS="${3:-900}"
INTERVAL_SECONDS="${VERIFY_INTERVAL_SECONDS:-15}"
# Consecutive 503 answers of the expected commit before giving up (the Hangfire server needs a few seconds to start).
MAX_UNHEALTHY_POLLS="${VERIFY_MAX_UNHEALTHY_POLLS:-8}"

if [ -z "$BASE_URL" ]; then
  echo "::error::API URL not configured: set the GitHub Actions variable (RAILWAY_TEST_URL / RAILWAY_PROD_URL) to the public Railway URL. See docs/runbooks/health-checks.md."
  exit 1
fi
if [ -z "$EXPECTED" ]; then
  echo "::error::Expected commit SHA missing."
  exit 1
fi

BASE_URL="${BASE_URL%/}"
READY_URL="$BASE_URL/api/health/ready"
BODY="$(mktemp)"
trap 'rm -f "$BODY"' EXIT

# True when the served commit is the expected one, or a later commit that contains it.
is_expected_or_newer() {
  local served="$1"
  [ -n "$served" ] || return 1
  [ "$served" = "$EXPECTED" ] && return 0
  git cat-file -e "${served}^{commit}" 2>/dev/null || git fetch --quiet origin "$served" 2>/dev/null || return 1
  if git merge-base --is-ancestor "$EXPECTED" "$served" 2>/dev/null; then
    echo "::notice::The API already runs $served, a later commit that contains $EXPECTED."
    return 0
  fi
  return 1
}

print_checks() {
  jq -r '.checks[]? | "  \(.name): \(.status)"' "$BODY" 2>/dev/null || true
}

deadline=$((SECONDS + TIMEOUT_SECONDS))
unhealthy_polls=0
code="000"
served=""
status=""

echo "Waiting for $EXPECTED on $READY_URL (timeout ${TIMEOUT_SECONDS}s)"
while :; do
  code="$(curl -sS -o "$BODY" -w '%{http_code}' --max-time 20 "$READY_URL" 2>/dev/null)" || code="000"
  served="$(jq -r '.commit // empty' "$BODY" 2>/dev/null | tr 'A-F' 'a-f')"
  status="$(jq -r '.status // empty' "$BODY" 2>/dev/null)"

  if is_expected_or_newer "$served"; then
    if [ "$code" = "200" ]; then
      echo "Deployment $served is ready ($status):"
      print_checks
      jq -r '.checks[]? | select(.status != "healthy") | "::warning::Readiness check \(.name) is \(.status) (optional configuration missing): see docs/runbooks/health-checks.md"' "$BODY" 2>/dev/null || true
      exit 0
    fi

    unhealthy_polls=$((unhealthy_polls + 1))
    if [ "$unhealthy_polls" -ge "$MAX_UNHEALTHY_POLLS" ]; then
      echo "::error::Deployment $served is running but not ready (HTTP $code, status ${status:-unknown}). Checks:"
      print_checks
      echo "Unhealthy database = unreachable or EF migrations not applied; hangfire = no server running. Details in the Railway logs (docs/runbooks/health-checks.md)."
      exit 1
    fi
  else
    unhealthy_polls=0
  fi

  if [ "$SECONDS" -ge "$deadline" ]; then
    if [ -z "$served" ]; then
      echo "::error::$READY_URL (HTTP $code) does not expose the commit of the running build. Either the deployment still runs a build older than FD-12, or RAILWAY_GIT_COMMIT_SHA is not set (deployment not triggered from GitHub): see docs/runbooks/health-checks.md."
    else
      echo "::error::Railway still serves commit $served instead of $EXPECTED after ${TIMEOUT_SECONDS}s: the deployment failed, was skipped or is still building (Railway dashboard > Deployments)."
    fi
    exit 1
  fi

  echo "  HTTP $code, commit ${served:-not exposed}, status ${status:-unknown}: retrying in ${INTERVAL_SECONDS}s"
  sleep "$INTERVAL_SECONDS"
done
