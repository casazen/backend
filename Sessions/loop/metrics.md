# SDLC Reliability Loop — Metrics

**Updated:** 2026-08-12

| Metric | Value | Notes |
|---|---|---|
| Open P0 gaps | 10 | From `check-spec-coverage.ps1` |
| Tick count | 3 | Dry-run ticks only |
| Tick-to-close (avg) | n/a | No gap closed yet |
| Escalations | 0 | |
| P0 pass rate (matrix heuristic) | low | Many fail/missing-test rows remain |
| Post-merge regressions tracked | 0 | Start counting after first real close |

## How to refresh

1. Run `.\scripts\quality\extract-requirements.ps1`
2. Run `.\scripts\quality\check-spec-coverage.ps1`
3. Update this table from `Sessions/loop/state.md` + evidence folders
4. Nightly CI hard-fails on P0 `` `fail` `` rows (see `.github/workflows/quality-nightly.yml`)
