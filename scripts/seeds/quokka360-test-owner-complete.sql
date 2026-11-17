-- Complete synthetic Quokka owner fixture. TEST schema only.
-- No real tax approval, signature, invoice, payout, email, or Auth0 role change.
-- Fiscal policy remains a draft; qualified accountant approval is not invented.
-- Restart TEST backend after seeding so its native EF startup encryption rewrites the synthetic values.
BEGIN;
DO $$
DECLARE
 agency uuid := 'ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8';
 owner_id uuid := 'fa985716-6d5a-4c5a-b554-140a6ea00e9e';
 policy_id uuid;
BEGIN
 IF NOT EXISTS (SELECT 1 FROM casazen_test."PropertyOwners"
   WHERE "Id"=owner_id AND "OrgId"=agency AND "ExternalKey"='QUOKKA-TEST-LUCA-OWNER')
 OR (SELECT count(*) FROM casazen_test."PropertyOwnerships" o
   JOIN casazen_test."Properties" p ON p."Id"=o."PropertyId" AND p."OrgId"=o."OrgId"
   WHERE o."PropertyOwnerId"=owner_id AND o."OrgId"=agency AND p."IsTestData" AND NOT p."IsDeleted")<>3
 THEN RAISE EXCEPTION 'Expected Quokka synthetic owner with three test properties'; END IF;

 UPDATE casazen_test."PropertyOwners" SET "Bic"='TESTIT00XXX'
 WHERE "Id"=owner_id AND "OrgId"=agency AND "Bic" IS NULL;

 SELECT "Id" INTO policy_id FROM casazen_test."OwnerAccountingPolicies"
 WHERE "OrgId"=agency AND "RuleCode"='QUOKKA-SYNTHETIC-TEST' AND "Version"='2026-11-17-v1';
 IF policy_id IS NULL THEN
   policy_id := gen_random_uuid();
   INSERT INTO casazen_test."OwnerAccountingPolicies"
     ("Id","OrgId","Jurisdiction","RuleCode","Version","ValidFrom","ValidTo","SourceUrl","SourceSha256","ParametersJson","ApprovedAt","ApprovedByUserId","ApprovedByName","CreatedAt","CreatedByUserId")
   VALUES (policy_id,agency,'IT','QUOKKA-SYNTHETIC-TEST','2026-11-17-v1','2026-11-17','2027-11-16',
     'https://github.com/casazen/backend/blob/chore/quokka360-test-seed-20261117/scripts/seeds/fixtures/quokka360-mandato-simulato.pdf','bfce9971ee0d59479cc01f59a704ed6b30c87d9d2109b71c43786ed30786ca29',
     '{"withholdingApplies":true,"withholdingRatePercent":21,"withholdingTaxCode":"1919","requiresAgencyCollection":true,"requiresQualifyingShortLet":true,"grossBaseComponents":["stay_gross"],"rounding":"half_up","retentionYears":10,"portalsWithholding":["Airbnb"]}',
     NULL,NULL,NULL,now(),'auth0|6afb6c7bb2c0f85f4e97f136');
 END IF;

 IF NOT EXISTS (SELECT 1 FROM casazen_test."OwnerFiscalProfiles" WHERE "OrgId"=agency AND "PropertyOwnerId"=owner_id) THEN
 INSERT INTO casazen_test."OwnerFiscalProfiles"
   ("Id","OrgId","PropertyOwnerId","ValidFrom","ValidTo","EntityKind","LegalName","TaxResidenceCountry","LegalAddress","FiscalCode","VatNumber","ForeignTaxId","SettlementCurrency","BankAccountReference","MandateRole","MandateFrom","MandateTo","RevenueRecognitionRule","ExpenseCommissionModel","WorkingCapitalRule","TaxJurisdiction","AccountingPolicyId","CreatedAt","CreatedByUserId")
 VALUES (gen_random_uuid(),agency,owner_id,'2026-11-17','2027-11-16',1,
   'Luca La Malfa [TEST - dati sintetici]','IT','Via dei Collaudi TEST 1, 22100 Como (CO), Italia',
   'LMLLCU80A01H501S',NULL,NULL,'EUR','IT83A0000000000000000000000',
   'owner','2026-11-17','2027-11-16','stay','net_of_commission','none','IT',policy_id,now(),'auth0|6afb6c7bb2c0f85f4e97f136');
 END IF;

 -- Current domain model: one owner-level mandate covers all three ownerships.
 IF NOT EXISTS (SELECT 1 FROM casazen_test."OwnerMandates" WHERE "OrgId"=agency AND "PropertyOwnerId"=owner_id AND "PropertyOwnershipId" IS NULL) THEN
 INSERT INTO casazen_test."OwnerMandates"
   ("Id","OrgId","PropertyOwnerId","PropertyOwnershipId","CommissionPercent","CommissionBase","CommissionIncludesVat","CleaningFlatRate","LinenFlatRate","CleaningRechargeMode","PassthroughPaymentFees","WithholdingTreatment","CoOwnerTaxReporting","MonthlyFrequency","MandatePdfUrl","ValidFrom","ValidTo","CreatedAt","CreatedByUserId")
 VALUES (gen_random_uuid(),agency,owner_id,NULL,20,1,false,70,15,1,false,1,0,1,
   'https://github.com/casazen/backend/blob/chore/quokka360-test-seed-20261117/scripts/seeds/fixtures/quokka360-mandato-simulato.pdf','2026-11-17','2027-11-16',now(),'auth0|6afb6c7bb2c0f85f4e97f136');
 END IF;
END $$;
COMMIT;
