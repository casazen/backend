BEGIN;
WITH made AS (
INSERT INTO casazen_test."ManualComplianceProofs" ("Id","OrgId","PropertyId","Kind","Day","FileKey","ContentType","UploadedByUserId","UploadedAt")
SELECT gen_random_uuid(),b."OrgId",b."PropertyId",1,b."CheckInDate"::date,
 'quokka-test-scenario/'||b."OrgId"||'/'||b."Id"||'/TEST-police.pdf',
 'application/pdf','auth0|6afb6c7bb2c0f85f4e97f136',b."CheckInDate"+interval '16 hours'
FROM casazen_test."Bookings" b JOIN casazen_test."Properties" p ON p."Id"=b."PropertyId" and p."OrgId"=b."OrgId"
WHERE b."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND p."Country"='CH' AND b."Status" in(2,3)
AND EXISTS(select 1 from casazen_test."SwissComplianceReceipts" s where s."OrgId"=b."OrgId" and s."PropertyId"=b."PropertyId" and s."Type"='Police' and s."ReportDate"::date=b."CheckInDate"::date)
AND NOT EXISTS(select 1 from casazen_test."ManualComplianceProofs" x where x."OrgId"=b."OrgId" and x."PropertyId"=b."PropertyId" and x."Kind"=1 and x."Day"=b."CheckInDate"::date)
RETURNING *)
INSERT INTO casazen_test."TestDataMarkers" ("Id","OrgId","EntityType","EntityId","Origin","TemplateKey","CreatedAt")
SELECT gen_random_uuid(),"OrgId",'ManualComplianceProof',"Id",'quokka-happy-path-test','swiss-police-proof-legacy-cockpit',now() FROM made;
INSERT INTO casazen_test."ManualComplianceProofProperties" ("Id","OrgId","ProofId","PropertyId")
SELECT gen_random_uuid(),p."OrgId",p."Id",p."PropertyId" FROM casazen_test."ManualComplianceProofs" p
WHERE p."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'
AND EXISTS(select 1 from casazen_test."TestDataMarkers" m where m."EntityId"=p."Id" and m."Origin"='quokka-happy-path-test')
AND NOT EXISTS(select 1 from casazen_test."ManualComplianceProofProperties" x where x."ProofId"=p."Id" and x."PropertyId"=p."PropertyId");
UPDATE casazen_test."InternalActivities" a SET "Status"='Completed',"CompletedAt"=now(),"ClosedBy"='system',
"Notes"=coalesce(a."Notes",'')||' TEST - prova polizia Ticino archiviata; nessun invio Alloggiati italiano applicabile.'
FROM casazen_test."Bookings" b JOIN casazen_test."Properties" p on p."Id"=b."PropertyId" and p."OrgId"=b."OrgId"
WHERE a."EntityId"=b."Id" and a."OrgId"=b."OrgId" and b."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'
and p."Country"='CH' and a."ActivityType"='AlloggiatiDeadline' and a."Status"='Pending';
COMMIT;