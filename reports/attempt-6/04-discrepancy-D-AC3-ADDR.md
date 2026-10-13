# D-AC3-ADDR

Active properties are unique on `(Address, City, PostalCode)`. L3 sent a fixed `Via Roma 10` and `POST /api/properties` returned 500 instead of 4xx.

Harness: `address: Via Roma ${run}` in `golden-journey-web.spec.ts`. Product still needs a 409 mapping for real duplicate addresses.
