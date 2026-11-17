-- SYNTHETIC TEST FIXTURE ONLY. Not Luca's real fiscal/bank/address details.
-- Run only against casazen_test, then restart the existing test backend:
-- its EncryptLegacyPlaintextAsync startup pass rewrites the synthetic fixture
-- through the application's EF converters. Verify CfDJ8 prefixes afterwards.
BEGIN;
DO $$
BEGIN
 IF NOT EXISTS (
   SELECT 1 FROM casazen_test."PropertyOwners" o
   JOIN casazen_test."Orgs" g ON g."Id"=o."OrgId"
   WHERE o."Id"='fa985716-6d5a-4c5a-b554-140a6ea00e9e'
     AND o."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'
     AND o."ExternalKey"='QUOKKA-TEST-LUCA-OWNER'
     AND o."Email"='luca.lamal@hotmail.it'
     AND g."Slug"='quokka360'
     AND (SELECT count(*) FROM casazen_test."PropertyOwnerships" own
          JOIN casazen_test."Properties" p ON p."Id"=own."PropertyId" AND p."OrgId"=own."OrgId"
          WHERE own."PropertyOwnerId"=o."Id" AND own."OrgId"=o."OrgId" AND p."IsTestData" AND NOT p."IsDeleted")=3
 ) THEN RAISE EXCEPTION 'Quokka360 test owner guard failed'; END IF;

 UPDATE casazen_test."PropertyOwners"
 SET "EntityKind"=1, "TaxRegime"=0,
     "LegalName"='Luca La Malfa [TEST - dati sintetici]',
     "Phone"='+390000000000',
     "TaxResidenceCountry"='IT', "SettlementCurrency"='EUR',
     "FiscalCode"='LMLLCU80A01H501S',
     "Iban"='IT83A0000000000000000000000', -- all-zero bank/branch/account: synthetic, not a payment destination
     "BillingStreet"='Via dei Collaudi TEST', "BillingCivic"='1',
     "BillingPostalCode"='22100', "BillingCity"='Como',
     "BillingProvince"='CO', "BillingCountry"='IT',
     "ExternalShortLetUnits"=0
 WHERE "Id"='fa985716-6d5a-4c5a-b554-140a6ea00e9e'
   AND "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8';
END $$;
COMMIT;