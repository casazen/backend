#!/usr/bin/env bash
# NuGet vulnerability gate (FD-02, A9-26). Used by ci-cd.yml; runs the same way locally.
#
# usage: scripts/check-vulnerable-packages.sh [solution-or-project]   (default: Casazen.sln)
#
# Lists every vulnerable package, direct and transitive, of the restored projects
# (`dotnet list package --vulnerable --include-transitive`) and fails when at least one advisory is High or
# Critical. Moderate and Low advisories are reported as warnings and do not fail the gate.
# `dotnet list package` ignores <NuGetAuditSuppress>: this script applies it itself, per project, so the gate and
# restore (Directory.Build.props) accept exactly the same temporary suppressions.
# Run `dotnet restore` first: the command reads obj/project.assets.json without restoring again. NuGet writes that
# file even when restore fails on NU1903/NU1904, so the full report is still available.
# Runbook: docs/runbooks/ci-backend.md.
set -uo pipefail

TARGET="${1:-Casazen.sln}"
REPORT="$(mktemp)"
ERRORS="$(mktemp)"
trap 'rm -f "$REPORT" "$ERRORS"' EXIT

if ! command -v jq >/dev/null 2>&1; then
  echo "::error::jq is required to parse the vulnerability report."
  exit 1
fi

if ! dotnet list "$TARGET" package --vulnerable --include-transitive --no-restore --format json >"$REPORT" 2>"$ERRORS"; then
  echo "::error::dotnet list package --vulnerable failed (run dotnet restore first)."
  cat "$ERRORS" "$REPORT"
  exit 1
fi

if ! jq -e '.projects' "$REPORT" >/dev/null 2>&1; then
  echo "::error::Unexpected output from dotnet list package --vulnerable."
  cat "$ERRORS" "$REPORT"
  exit 1
fi

if jq -e '(.problems // []) | length > 0' "$REPORT" >/dev/null 2>&1; then
  echo "::error::dotnet list package reported problems:"
  jq -r '.problems[] | "  \(.level // "error"): \(.text // .)"' "$REPORT"
  exit 1
fi

# One line per (project, package, advisory): "<severity>\t<project path>\t<package> <version>\t<kind>\t<advisory>"
FINDINGS="$(jq -r '
  .projects[]
  | .path as $project
  | (.frameworks // [])[]
  | ((.topLevelPackages // []) | map(. + {kind: "direct"})) + ((.transitivePackages // []) | map(. + {kind: "transitive"}))
  | .[]
  | . as $pkg
  | (.vulnerabilities // [])[]
  | [.severity, $project, "\($pkg.id) \($pkg.resolvedVersion)", $pkg.kind, .advisoryurl]
  | @tsv' "$REPORT" | sort -u)"

if [ -z "$FINDINGS" ]; then
  echo "No vulnerable NuGet packages (direct or transitive) in $TARGET."
  exit 0
fi

# Advisories suppressed with <NuGetAuditSuppress Include="<advisory url>" /> in each affected project.
declare -A SUPPRESSED=()
while IFS= read -r project; do
  SUPPRESSED["$project"]="$(dotnet msbuild "$project" -getItem:NuGetAuditSuppress </dev/null 2>/dev/null \
    | jq -r '.Items.NuGetAuditSuppress[]?.Identity' 2>/dev/null)"
done < <(printf '%s\n' "$FINDINGS" | cut -f2 | sort -u)

BLOCKING=0
echo "Vulnerable NuGet packages in $TARGET:"
while IFS=$'\t' read -r severity project package kind advisory; do
  name="$(basename "$project")"
  if printf '%s\n' "${SUPPRESSED[$project]:-}" | grep -Fxq "$advisory"; then
    status="suppressed"
    echo "::warning title=Vulnerable package ($severity, suppressed)::$package ($kind, $name): $advisory is suppressed with NuGetAuditSuppress. Remove the suppression as soon as a patched version exists."
  elif [ "$(printf '%s' "$severity" | tr 'A-Z' 'a-z')" = "high" ] || [ "$(printf '%s' "$severity" | tr 'A-Z' 'a-z')" = "critical" ]; then
    status="BLOCKING"
    BLOCKING=$((BLOCKING + 1))
    echo "::error title=Vulnerable package ($severity)::$package ($kind, $name): $advisory"
  else
    status="warning"
    echo "::warning title=Vulnerable package ($severity)::$package ($kind, $name): $advisory"
  fi
  printf '  %-10s %-9s %-28s %-45s %-10s %s\n' "$status" "$severity" "$name" "$package" "$kind" "$advisory"
done <<<"$FINDINGS"

if [ "$BLOCKING" -gt 0 ]; then
  echo "$BLOCKING High/Critical advisories: update the package (or the direct dependency that brings it in) to a patched version. See docs/runbooks/ci-backend.md."
  exit 1
fi
echo "No High/Critical advisory left unsuppressed: the gate passes. Plan the remaining updates anyway."
exit 0
