BEGIN;
UPDATE casazen_test."Payments" p SET "Status"=2,"ProcessedAt"=now(),"UpdatedAt"=now(),"Description"='PAGAMENTO ANTICIPATO SIMULATO TEST - nessun movimento reale'
FROM casazen_test."TestDataMarkers" m WHERE p."Id"=m."EntityId" AND p."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND m."OrgId"=p."OrgId" AND m."Origin"='quokka-scenario-test' AND m."EntityType"='Payment' AND p."Status"=0;
WITH numbered AS (
 SELECT d."Id",row_number() over(partition by d."IssuerProfileId",d."Series" order by d."ServicePeriodFrom",d."Id") +
 coalesce((select max(x."SequenceNumber") from casazen_test."FiscalDocuments" x where x."OrgId"=d."OrgId" and x."IssuerProfileId"=d."IssuerProfileId" and x."Series"=d."Series" and x."Status"=2),0) seq,
 (select x."IssuerSnapshot" from casazen_test."FiscalDocuments" x where x."OrgId"=d."OrgId" and x."IssuerProfileId"=d."IssuerProfileId" and x."IssuerSnapshot" is not null order by x."IssueDate" desc limit 1) snapshot
 FROM casazen_test."FiscalDocuments" d WHERE d."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and d."Purpose"=1 and d."Status"=1
 AND EXISTS(select 1 from casazen_test."TestDataMarkers" m where m."OrgId"=d."OrgId" and m."EntityId"=d."Id" and m."Origin"='quokka-scenario-test' and m."EntityType"='FiscalDocument'))
 UPDATE casazen_test."FiscalDocuments" d SET "Status"=2,"SequenceNumber"=n.seq::int,"Number"=d."Series"||'/'||n.seq,"IssueDate"=current_date,"PaymentState"=2,"IssuerSnapshot"=n.snapshot,"UpdatedAt"=now()
 FROM numbered n WHERE n."Id"=d."Id";
INSERT INTO casazen_test."OwnerFxRates" ("Id","OrgId","BaseCurrency","QuoteCurrency","RateDate","Rate","Source","CreatedAt","CreatedByUserId")
SELECT gen_random_uuid(),'ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8','CHF','EUR',day::date,1.04,'TEST-SIMULATED-CHF-EUR-1.04',now(),'auth0|6afb6c7bb2c0f85f4e97f136'
FROM generate_series('2026-10-01'::date,'2026-12-31'::date,'1 day') day
WHERE NOT EXISTS(select 1 from casazen_test."OwnerFxRates" r where r."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and r."BaseCurrency"='CHF' and r."QuoteCurrency"='EUR' and r."RateDate"=day::date);
COMMIT;