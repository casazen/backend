# Nightly quality report — YYYY-MM-DD

## Commands

```powershell
# Frontend L2
cd ../frontend; npm run test:e2e

# Frontend L3 local
cd ../backend; .\scripts\quality\run-l3-local.ps1

# Staging (secrets required)
cd ../frontend; npm run test:e2e:staging; npm run test:e2e:staging-gj

# Anti-stub
cd ../backend; .\scripts\quality\check-no-shipped-stubs.ps1

# Mobile Maestro (device)
cd ../mobile; maestro test e2e/
```

## Results

| Suite | Pass | Fail | Notes |
|---|---|---|---|
| L2 demo | | | |
| L3 local | | | |
| Staging GJ | | | |
| Anti-stub | | | |
| Maestro | | | |

## Matrix deltas

- Rows flipped to `pass`:
- New `fail` / freeze impact:

## Freeze

- [ ] P0 fail present → freeze active
- [ ] Freeze cleared
