#!/usr/bin/env bash
# Apply EF Core migrations to Supabase (test or prod schema).
set -euo pipefail

TARGET="${1:-test}"
if [[ "$TARGET" != "test" && "$TARGET" != "prod" ]]; then
  echo "Usage: ./scripts/migrate.sh [test|prod]"
  exit 1
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT/secrets/supabase.local.env"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing $ENV_FILE"
  echo "  cp secrets/supabase.local.env.example secrets/supabase.local.env"
  echo "  # edit host + password, then: ./scripts/setup-supabase.sh"
  exit 1
fi

# shellcheck source=/dev/null
source "$ENV_FILE"

if [[ -z "${SUPABASE_HOST:-}" || -z "${SUPABASE_PASSWORD:-}" ]]; then
  echo "SUPABASE_HOST and SUPABASE_PASSWORD required in $ENV_FILE"
  exit 1
fi

HOST="$SUPABASE_HOST"
HOST="${HOST#https://}"
HOST="${HOST#http://}"
if [[ "$HOST" == *".pooler.supabase.com" ]] || [[ "$HOST" == db.*.supabase.co ]]; then
  :
elif [[ "$HOST" =~ ^([a-z0-9]+)\.supabase\.co$ ]]; then
  HOST="db.${BASH_REMATCH[1]}.supabase.co"
elif [[ "$HOST" != *.* ]]; then
  HOST="db.${HOST}.supabase.co"
fi

PORT="${SUPABASE_PORT:-5432}"
DATABASE="${SUPABASE_DATABASE:-postgres}"
USERNAME="${SUPABASE_USERNAME:-postgres}"
SCHEMA="casazen_test"
[[ "$TARGET" == "prod" ]] && SCHEMA="casazen_prod"

export ConnectionStrings__DefaultConnection="Host=${HOST};Port=${PORT};Database=${DATABASE};Username=${USERNAME};Password=${SUPABASE_PASSWORD};SearchPath=${SCHEMA};SSL Mode=Require;Trust Server Certificate=true"
export CASAZEN_MIGRATION_TARGET="$TARGET"

echo "Applying migrations to schema: $SCHEMA"
dotnet ef database update \
  --project "$ROOT/Casazen.Infrastructure" \
  --startup-project "$ROOT/Casazen.Web"
