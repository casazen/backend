# Review — D-AC13-I18N-EXPORT

STATO: APPROVED

`leases.canoneConcordato.*` now in `it.json` (Italian primary). Export uses authenticated axios (`responseType: 'blob'`) on `/leases/{id}/canone-concordato/imu-notification/export` — same token interceptor as other private APIs; no raw unauthenticated href.
