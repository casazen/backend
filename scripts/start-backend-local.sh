#!/usr/bin/env bash
# Start the .NET backend locally with InMemory database (no Supabase needed).
# Use this for local E2E testing or frontend development without a real database.
#
# Usage: ./scripts/start-backend-local.sh
#        ./scripts/start-backend-local.sh -Port 5000
set -euo pipefail

PORT="${1:-5000}"

# Clear connection string to trigger EF Core InMemory fallback
export ConnectionStrings__DefaultConnection=""
export ASPNETCORE_ENVIRONMENT="Development"

echo "============================================================"
echo "Starting CasaZen backend with InMemory database"
echo "Port: http://localhost:${PORT}"
echo "Swagger: http://localhost:${PORT}/swagger"
echo "Health: http://localhost:${PORT}/api/health"
echo "============================================================"

dotnet run --project Casazen.Web --urls "http://localhost:${PORT}"
