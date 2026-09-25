# source /home/user/wt/bin/env.sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$PATH:$HOME/.dotnet/tools"
export INT_BRANCH=claude/app-analysis-fixes-plan-p0mx0a
# Base connection for integration tests on real PostgreSQL (each test creates its own database)
export TEST_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=dev;Include Error Detail=true"
pg_isready -h localhost -q 2>/dev/null || pg_ctlcluster 16 main start >/dev/null 2>&1 || true
