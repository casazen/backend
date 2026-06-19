# Design — #284 Public booking prod deploy

## API Contract

N/A — deploy/routing verification.

## Frontend Flow

| Check | Change |
|---|---|
| `vercel.json` | SPA rewrite already present — verify only |
| `e2e/vercel-deploy-smoke.spec.ts` | Assert `/book/{slug}` serves `#root`, no env placeholder leak |

## Security Notes

Public `/book/*` routes remain anonymous.

## Migration Plan

N/A

## GDPR Scope

N/A
