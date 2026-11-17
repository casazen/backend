-- Quokka360 TEST: owner as issuer of stay documents.
-- Synthetic identity/address match the test owner fixture. No invoices issued,
-- no tax submissions, automatic issuance disabled, email delivery disabled.
-- Restart the test backend after this seed: native EF startup encryption handles
-- these synthetic legacy-format values using Casazen.OrgFiscalIssuerProfile.
BEGIN;
DO $$
DECLARE
 agency uuid := 'ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8';
 owner_id uuid := 'fa985716-6d5a-4c5a-b554-140a6ea00e9e';
BEGIN
 IF NOT EXISTS (SELECT 1 FROM casazen_test."PropertyOwners" WHERE "Id"=owner_id AND "OrgId"=agency
   AND "ExternalKey"='QUOKKA-TEST-LUCA-OWNER' AND "TaxResidenceCountry"='IT' AND "EntityKind"=1)
 THEN RAISE EXCEPTION 'Expected synthetic Quokka owner'; END IF;
 IF EXISTS (SELECT 1 FROM casazen_test."OrgFiscalIssuerProfiles" WHERE "PropertyOwnerId"=owner_id)
 THEN RAISE EXCEPTION 'Owner issuer profile already exists: review before replacing'; END IF;
 INSERT INTO casazen_test."OrgFiscalIssuerProfiles"
 ("Id","OrgId","PropertyOwnerId","IssuerMode","DocumentRegime","LegalName","FiscalCode","VatNumber",
 "AddressStreet","AddressCivic","PostalCode","City","Province","CountryCode",
 "FatturaPaRegimeCode","IssuerSdiRecipientCode","IssuerPec","GuestDocumentSeries","StampDutyReference",
 "StayVatTreatment","CleaningFeeTreatment","VatRegistered","StayIssuedByOwner",
 "AutoIssueGuestDocuments","EmailGuestDocuments","ConservationAgreementConfirmed","VirtualStampConfirmed",
 "ConfirmedAt","UpdatedAt","UpdatedByUserId")
 VALUES (gen_random_uuid(),agency,owner_id,2,1,
 'Luca La Malfa [TEST - dati sintetici]','LMLLCU80A01H501S',NULL,
 'Via dei Collaudi TEST','1','22100','Como','CO','IT',
 NULL,'0000000','luca.lamalfa@pec.example.test','LLM{year}','TEST-BOLLO-NON-REALE',
 1,1,false,false,false,false,false,false,now(),now(),'auth0|6afb6c7bb2c0f85f4e97f136');
 -- VAT number, FatturaPA regime, Swiss UID and bank accounts do not belong to this
 -- private Italian owner issuer request; do not create fabricated VAT registration.
END $$;
COMMIT;
