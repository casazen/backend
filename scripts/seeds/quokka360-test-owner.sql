-- Quokka360 TEST only: temporary owner requested by Luca.
-- No Auth0 roles removed, no invitation accepted or email sent.
BEGIN;
DO $$
DECLARE
  agency uuid := 'ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8';
  owner_id uuid;
  target_ids uuid[] := ARRAY[
    '36e556c2-943e-44d7-86db-dea0567ea8a1'::uuid,
    '36e556c2-943e-44d7-86db-dea0567ea8a2'::uuid,
    '36e556c2-943e-44d7-86db-dea0567ea8a3'::uuid];
BEGIN
  IF (SELECT count(*) FROM casazen_test."Properties"
      WHERE "OrgId"=agency AND "Id"=ANY(target_ids) AND "IsTestData" AND NOT "IsDeleted") <> 3 THEN
    RAISE EXCEPTION 'Expected exactly three Quokka test properties';
  END IF;
  SELECT "Id" INTO owner_id FROM casazen_test."PropertyOwners"
    WHERE "OrgId"=agency AND "ExternalKey"='QUOKKA-TEST-LUCA-OWNER';
  IF owner_id IS NULL THEN
    owner_id := gen_random_uuid();
    INSERT INTO casazen_test."PropertyOwners"
      ("Id","OrgId","FirstName","LastName","Email","TaxRegime","CreatedAt","ExternalKey","LegalName","ExternalShortLetUnits")
    VALUES (owner_id,agency,'Luca','La Malfa','luca.lamal@hotmail.it',0,now(),'QUOKKA-TEST-LUCA-OWNER','Luca La Malfa',0);
  END IF;
  IF EXISTS (SELECT 1 FROM casazen_test."PropertyOwnerships"
      WHERE "OrgId"=agency AND "PropertyId"=ANY(target_ids)
        AND "PropertyOwnerId"<>owner_id AND ("OwnershipEnd" IS NULL OR "OwnershipEnd">=now())) THEN
    RAISE EXCEPTION 'An existing ownership requires review';
  END IF;
  INSERT INTO casazen_test."PropertyOwnerships"
    ("Id","OrgId","PropertyId","PropertyOwnerId","SharePercent","OwnershipStart","OwnershipEnd","OwnerUserId")
  SELECT gen_random_uuid(),agency,p."Id",owner_id,100.00,now(),NULL,NULL
    FROM casazen_test."Properties" p
    WHERE p."OrgId"=agency AND p."Id"=ANY(target_ids)
      AND NOT EXISTS (SELECT 1 FROM casazen_test."PropertyOwnerships" o
        WHERE o."PropertyId"=p."Id" AND o."OrgId"=agency
          AND o."PropertyOwnerId"=owner_id AND (o."OwnershipEnd" IS NULL OR o."OwnershipEnd">=now()));
END $$;
COMMIT;
