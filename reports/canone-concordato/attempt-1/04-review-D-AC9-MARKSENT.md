# Review — D-AC9-MARKSENT

STATO: APPROVED

`POST /api/leases/{id}/canone-concordato/imu-notification/mark-sent` exists, gated `lease.register`. `ImuNotificationExported` fires only on export; `ImuNotificationMarkedSent` only on mark-sent; neither fires before `Registered`. Controller 404/409 covered.
