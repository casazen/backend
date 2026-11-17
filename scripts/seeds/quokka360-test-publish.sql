-- Test-fixture pricing and publication, verified source quotes:
-- 2026-11-18 -> 2026-11-19, 2 adults, Standard rate, one night.
-- Como 67.10 EUR; Lugano 77 CHF; Ascona 97 CHF.
-- Documents/safety remain unverified reminders, never fabricated here.
BEGIN;
UPDATE casazen_test."Properties" p
SET "NightlyRate"=v.rate,"ComplianceStatus"=1,"PublishedOnSite"=true,
"ComplianceCompletedAt"=now(),"ComplianceCheckedAt"=now(),
"ComplianceSuspendedAt"=null,"ComplianceSuspensionReasons"=null,"UpdatedAt"=now()
FROM (VALUES ('36e556c2-943e-44d7-86db-dea0567ea8a1'::uuid,67.10,'EUR'),('36e556c2-943e-44d7-86db-dea0567ea8a2'::uuid,77.00,'CHF'),('36e556c2-943e-44d7-86db-dea0567ea8a3'::uuid,97.00,'CHF')) v(id,rate,currency)
WHERE p."Id"=v.id AND p."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND p."IsTestData" AND NOT p."IsDeleted"
AND p."Currency"=v.currency AND p."RentalMode"=0 AND p."MaxGuests">0 AND p."IsActive" AND NOT p."IsPaused"
AND (p."Country"='CH' OR p."CinCode"='IT013075B4GLZD4BX7');
COMMIT;