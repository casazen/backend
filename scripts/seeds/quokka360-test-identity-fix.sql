-- Identity verified through Auth0 Management API on 2026-11-17.
-- Current user exists with the requested email; the old seeded user is absent from Auth0.
-- Only casazen_test is changed. Preserve the current org and its existing test property.
BEGIN;
DO $guard$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM casazen_test."Users" u JOIN casazen_test."OrgMembers" m ON m."UserId"=u."Id" AND m."OrgId"=u."OrgId" WHERE u."Id"='auth0|6afb6c7bb2c0f85f4e97f136' AND u."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND m."Role"=1 AND m."Status"=1) THEN RAISE EXCEPTION 'Current owner or membership changed'; END IF;
 IF (SELECT count(*) FROM casazen_test."Properties" WHERE "Id" IN ('36e556c2-943e-44d7-86db-dea0567ea8a1','36e556c2-943e-44d7-86db-dea0567ea8a2','36e556c2-943e-44d7-86db-dea0567ea8a3') AND "IsTestData" AND "OrgId" IN ('d948604e-7d26-4505-9542-68e889bd4026','ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'))<>3 THEN RAISE EXCEPTION 'Pilot properties changed'; END IF;
END $guard$;
UPDATE casazen_test."Orgs"
SET "Name"='Quokka360 (seed archiviato)',"DisplayName"='Quokka360 (seed archiviato)',"Slug"='quokka360-seed-archiviato',"IsActive"=false,"UpdatedAt"=now()
WHERE "Id"='d948604e-7d26-4505-9542-68e889bd4026';
UPDATE casazen_test."Orgs"
SET "Name"='Quokka360',"DisplayName"='Quokka360',"Slug"='quokka360',"IsAgency"=true,"PlanTier"=3,"ContactEmail"='luca.lamalfa91@gmail.com',"BillingCountry"='CH',"IsActive"=true,"UpdatedAt"=now()
WHERE "Id"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8';
UPDATE casazen_test."Users" SET "Email"='luca.lamalfa91@gmail.com',"UpdatedAt"=now()
WHERE "Id"='auth0|6afb6c7bb2c0f85f4e97f136';
UPDATE casazen_test."Properties"
SET "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8',"OwnerId"='auth0|6afb6c7bb2c0f85f4e97f136',"UpdatedAt"=now()
WHERE "Id" IN ('36e556c2-943e-44d7-86db-dea0567ea8a1','36e556c2-943e-44d7-86db-dea0567ea8a2','36e556c2-943e-44d7-86db-dea0567ea8a3')
AND "OrgId"='d948604e-7d26-4505-9542-68e889bd4026' AND "IsTestData";
UPDATE casazen_test."TestDataMarkers" SET "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'
WHERE "OrgId"='d948604e-7d26-4505-9542-68e889bd4026'
AND "EntityId" IN ('36e556c2-943e-44d7-86db-dea0567ea8a1','36e556c2-943e-44d7-86db-dea0567ea8a2','36e556c2-943e-44d7-86db-dea0567ea8a3') AND "Origin"='quokka-pilot';
COMMIT;