WITH b AS (select * from casazen_test."Bookings" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'), docs AS (select d.* from casazen_test."FiscalDocuments" d join b on b."Id"=d."BookingId" where d."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8')
select jsonb_build_object(
'bookings',(select count(*) from b),
'status',(select jsonb_object_agg("Status",n) from (select "Status",count(*) n from b group by "Status") s),
'channels',(select jsonb_object_agg("ChannelLabel",n) from (select "ChannelLabel",count(*) n from b group by "ChannelLabel") s),
'overlaps',(select count(*) from b a join b z on a."PropertyId"=z."PropertyId" and a."Id"<z."Id" and a."CheckInDate"<z."CheckOutDate" and z."CheckInDate"<a."CheckOutDate"),
'guestCountErrors',(select count(*) from b where "NumberOfGuests"<>(select count(*) from casazen_test."StayGuests" s where s."BookingId"=b."Id" and s."OrgId"=b."OrgId")),
'bookingTotalErrors',(select count(*) from b where "TotalPrice"<>"BasePrice"+"TouristTaxAmount"),
'docTotalErrors',(select count(*) from docs where "Total"<>"TaxableBase"+"VatAmount"+"StampDuty"),
'docLineErrors',(select count(*) from docs d where "TaxableBase"<>(select sum(l."TotalPrice") from casazen_test."FiscalDocumentLines" l where l."DocumentId"=d."Id" and l."OrgId"=d."OrgId")),
'pastUnpaid',(select count(*) from b where "Status" in(2,3) and "TotalPrice"<>(select sum(p."Amount") from casazen_test."Payments" p where p."BookingId"=b."Id" and p."OrgId"=b."OrgId" and p."Status"=2)),
'pastUnissued',(select count(*) from b where "Status" in(2,3) and not exists(select 1 from docs d where d."BookingId"=b."Id" and d."Status"=2)),
'bookingsForChiara',(select count(*) from b where "CheckInDate">='2026-11-23'),
'externalIds',(select count(*) from b where "OctorateReservationId" is not null or "StripeCustomerId" is not null),
'proofsUploaded',(select count(*) from storage.objects where bucket_id='casazen-test-private' and (name like 'compliance-proofs/ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8/a7112026-%' or name like 'quokka-test-scenario/ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8/%')),
'pastItalianProofMissing',(select count(*) from b where "PropertyId"='36e556c2-943e-44d7-86db-dea0567ea8a1' and "Status" in(2,3) and not exists(select 1 from casazen_test."ManualComplianceProofs" p where p."PropertyId"=b."PropertyId" and p."OrgId"=b."OrgId" and p."Kind"=1 and p."Day"=b."CheckInDate"::date)),
'ownerHistoricGaps',(select count(*) from b where not exists(select 1 from casazen_test."PropertyOwnerships" o where o."PropertyId"=b."PropertyId" and o."OrgId"=b."OrgId" and o."OwnershipStart"<=b."CheckInDate" and (o."OwnershipEnd" is null or o."OwnershipEnd">=b."CheckOutDate"))))