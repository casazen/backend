#!/usr/bin/env bash
# One-time: secrets/supabase.local.env → dotnet user-secrets
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXAMPLE="$ROOT/secrets/supabase.local.env.example"
ENV_FILE="$ROOT/secrets/supabase.local.env"
WEB="$ROOT/Casazen.Web"

if [[ ! -f "$ENV_FILE" ]]; then
  cp "$EXAMPLE" "$ENV_FILE"
  echo "Created $ENV_FILE — edit SUPABASE_HOST and SUPABASE_PASSWORD, then re-run."
  exit 1
fi

# shellcheck source=/dev/null
source "$ENV_FILE"
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

build_conn() {
  local schema="$1"
  echo "Host=${HOST};Port=${PORT};Database=${DATABASE};Username=${USERNAME};Password=${SUPABASE_PASSWORD};SearchPath=${schema};SSL Mode=Require;Trust Server Certificate=true"
}

TEST_CONN="$(build_conn casazen_test)"
PROD_CONN="$(build_conn casazen_prod)"

dotnet user-secrets set "ConnectionStrings:DefaultConnection" "$TEST_CONN" --project "$WEB"
dotnet user-secrets set "ConnectionStrings:SupabaseTest" "$TEST_CONN" --project "$WEB"
dotnet user-secrets set "ConnectionStrings:SupabaseProd" "$PROD_CONN" --project "$WEB"

echo "User secrets updated. Run: ./scripts/migrate.sh test"
