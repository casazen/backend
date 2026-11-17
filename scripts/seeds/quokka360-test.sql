-- Quokka360 pilot seed: test schema only. No Auth0, Octorate, payments or email calls.
BEGIN;
SET LOCAL search_path = casazen_test, pg_catalog;
DO $guard$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM casazen_test."Users" WHERE "Id"='auth0|6a393129c0492ca8e95cebdc' AND lower("Email")='luca.lamalfa91@gmail.com' AND "IsActive" AND ("OrgId" IS NULL OR "OrgId"='d948604e-7d26-4505-9542-68e889bd4026')) THEN
 RAISE EXCEPTION 'Pilot account missing, inactive, or already assigned elsewhere';
 END IF;
 IF EXISTS (SELECT 1 FROM casazen_test."Orgs" WHERE "Slug"='quokka360' AND "Id"<>'d948604e-7d26-4505-9542-68e889bd4026') THEN RAISE EXCEPTION 'Slug already in use'; END IF;
 IF EXISTS (SELECT 1 FROM casazen_test."OrgMembers" WHERE "UserId"='auth0|6a393129c0492ca8e95cebdc' AND "OrgId"<>'d948604e-7d26-4505-9542-68e889bd4026') THEN RAISE EXCEPTION 'Conflicting membership'; END IF;
END $guard$;
INSERT INTO casazen_test."Orgs" ("Id","Name","Slug","PlanTier","DisplayName","ContactEmail","IsActive","CreatedAt","UpdatedAt","IsAgency","OrgType","BillingCountry")
VALUES ('d948604e-7d26-4505-9542-68e889bd4026','Quokka360','quokka360',3,'Quokka360','luca.lamalfa91@gmail.com',true,now(),now(),true,0,'CH')
ON CONFLICT ("Id") DO NOTHING;
UPDATE casazen_test."Users" SET "OrgId"='d948604e-7d26-4505-9542-68e889bd4026',"UpdatedAt"=now()
WHERE "Id"='auth0|6a393129c0492ca8e95cebdc' AND "OrgId" IS NULL;
INSERT INTO casazen_test."OrgMembers" ("Id","OrgId","UserId","Role","Status","PropertyScope","CreatedAt")
SELECT 'effa053a-731b-4eab-a60f-21ef94688f67','d948604e-7d26-4505-9542-68e889bd4026','auth0|6a393129c0492ca8e95cebdc',1,1,1,now()
WHERE NOT EXISTS (SELECT 1 FROM casazen_test."OrgMembers" WHERE "UserId"='auth0|6a393129c0492ca8e95cebdc');
INSERT INTO casazen_test."UserContextMemberships" ("Id","UserId","ContextKey","RoleId")
SELECT gen_random_uuid(),'auth0|6a393129c0492ca8e95cebdc',r."ContextKey",r."Id"
FROM casazen_test."Roles" r WHERE r."Id" IN (1,4)
AND NOT EXISTS (SELECT 1 FROM casazen_test."UserContextMemberships" m WHERE m."UserId"='auth0|6a393129c0492ca8e95cebdc' AND m."ContextKey"=r."ContextKey");
INSERT INTO casazen_test."Properties"
("Id","OrgId","OwnerId","Name","Slug","Description","Address","City","PostalCode","Latitude","Longitude","Bedrooms","Bathrooms","MaxGuests","NightlyRate","CleaningFee","DamageDeposit","Amenities","PhotoUrls","HouseRules","Timezone","Country","Currency","IsActive","IsTestData","PublishedOnSite","CreatedAt","UpdatedAt","AreaSqm","FloorLevel","SwissMunicipalityCode","CheckInFrom","CheckOutBy","Unit")
VALUES
('36e556c2-943e-44d7-86db-dea0567ea8a1','d948604e-7d26-4505-9542-68e889bd4026','auth0|6a393129c0492ca8e95cebdc','LARIO SUITE BRIENNO','lario-suite-brienno',
'BOZZA DI TEST. Bilocale a Como con camera matrimoniale, divano letto e bagno con doccia. Balcone, cucina e garage; secondo piano con ascensore. Fonte: https://quokka.kross.travel/lario-suite-brienno . Prezzi da configurare.',
'Via Bellinzona 52','Como','',0,0,1,1,4,0,0,0,ARRAY[1,10,20,30,44,52,71,80,100],ARRAY['https://cdn.krossbooking.com/quokka360/images/3/626/17627875987684.jpg'],'Vietate le attività commerciali. Self check-in.','Europe/Rome','IT','EUR',true,true,false,now(),now(),56,2,null,null,null,null),
('36e556c2-943e-44d7-86db-dea0567ea8a2','d948604e-7d26-4505-9542-68e889bd4026','auth0|6a393129c0492ca8e95cebdc','URBAN RESIDENCE 06','urban-residence-06',
'BOZZA DI TEST. Bilocale a Viganello, Lugano, con camera matrimoniale, divano letto, cucina attrezzata e bagno con doccia. Balcone privato e lavatrice. Fonte: https://quokka.kross.travel/urban-residence-06 . Prezzi da configurare.',
'Via Emilio Rava 14','Lugano','6962',0,0,1,1,4,0,0,0,ARRAY[1,2,20,23,30,31,44,71,80,100],ARRAY['https://cdn.krossbooking.com/quokka360/images/3/692/17482463957843.jpg'],'Vietato fumare e svolgere attività commerciali. Nessun parcheggio privato. Self check-in.','Europe/Zurich','CH','CHF',true,true,false,now(),now(),null,1,'5113','15:00','10:00','06'),
('36e556c2-943e-44d7-86db-dea0567ea8a3','d948604e-7d26-4505-9542-68e889bd4026','auth0|6a393129c0492ca8e95cebdc','FLORIDA ASCONA Room 10','florida-ascona-room-10',
'BOZZA DI TEST. Camera ad Ascona con letto matrimoniale, bagno dedicato, scrivania, balcone privato e posto auto esterno. Frigorifero non presente. Fonte richiesta: https://quokka.kross.travel/florida-ascona-room-10 ; riscontro: https://www.airbnb.com/rooms/1578282631029758166 . Foto, capienza e prezzi da confermare.',
'Via Locarno 12','Ascona','6612',0,0,1,1,0,0,0,0,ARRAY[1,4,44,50,100],ARRAY[]::text[],'Vietato fumare e svolgere attività commerciali. Self check-in.','Europe/Zurich','CH','CHF',true,true,false,now(),now(),null,null,'5237',null,null,'10')
ON CONFLICT ("Id") DO NOTHING;
INSERT INTO casazen_test."TestDataMarkers" ("Id","OrgId","EntityType","EntityId","Origin","TemplateKey","CreatedAt")
SELECT gen_random_uuid(),p."OrgId",'Property',p."Id",'quokka-pilot','quokka360-r06-20261117',now()
FROM casazen_test."Properties" p WHERE p."Id" IN ('36e556c2-943e-44d7-86db-dea0567ea8a1','36e556c2-943e-44d7-86db-dea0567ea8a2','36e556c2-943e-44d7-86db-dea0567ea8a3')
AND NOT EXISTS (SELECT 1 FROM casazen_test."TestDataMarkers" t WHERE t."EntityId"=p."Id" AND t."Origin"='quokka-pilot');
COMMIT;
