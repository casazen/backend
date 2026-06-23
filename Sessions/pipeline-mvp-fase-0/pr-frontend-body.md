## Summary
- Golden Journey E2E skeleton: steps 1-4 runnable in demo mode
- Steps 5-12 marked test.fixme for Fase 1 (#301)

## Backend PR
https://github.com/casazen/backend/pull/307

## Test Plan
- [x] npm run test:e2e -- golden-journey-web (4 passed, 8 fixme)
- [x] npm test (176 passed)
- [x] npm run build
- [ ] npm run lint (pre-existing develop errors unrelated to this PR)

## Acceptance criteria coverage
| AC | Test |
|---|---|
| GJ skeleton steps 1-4 | e2e/golden-journey-web.spec.ts |
| No API 500 on step 4 | GJ-4 test |

Closes casazen/backend#286
