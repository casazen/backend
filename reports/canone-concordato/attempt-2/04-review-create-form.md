STATO: APPROVED

Slice: calculator on lease create (AC12 create path)

- `/leases/new` shows the canone concordato calculator when regime is `CanoneConcordato` and a property is selected.
- Nested `<form>` avoided: calculator calculate control is `type="button"`.
- Submit is blocked unless monthly rent is inside the calculated min/max (`isRentInConcordatoRange`).
- Italian copy via `t()` (`concordatoRangeRequired`, `concordatoRentOutOfRange`, `concordatoRangeHint`).
- L2: `lease-create-form.test.tsx` (calculator visible) + `concordato-rent-range.test.ts` (in/out of range).
