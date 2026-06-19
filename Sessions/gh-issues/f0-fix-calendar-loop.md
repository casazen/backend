## Summary

Pre-Fase 0 blocking fix from prod E2E audit (`spec-production-e2e-flow-verification.md`, 2026-06-09). Host calendar page triggers `GET /api/bookings/calendar` without `propertyId`, causing an infinite request loop and unusable calendar.

**Planning:** `Sessions/PLANNING.md` § Debito noto  
**Blocks:** Golden Journey step 5, MVP Fase 0 exit

## Acceptance criteria

- [ ] Calendar route always passes `propertyId` (or sensible default when single property)
- [ ] Empty state when no property selected — no repeated API calls
- [ ] Playwright smoke: open calendar → max 1 calendar fetch per property change
- [ ] Italian empty-state copy

## Spec / deps

- Prerequisite for `spec-golden-journey-e2e` (GJ-001)
- Related: #271 (org context)
