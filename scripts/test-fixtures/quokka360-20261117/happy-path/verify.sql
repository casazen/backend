select json_build_object(
'bookings',(select count(*) from casazen_test."Bookings" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'),
'payments',(select json_agg(x) from(select "Status",count(*) from casazen_test."Payments" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' group by 1)x),
'imbalancedPayments',(select count(*) from casazen_test."Bookings" b left join lateral(select sum("Amount") paid from casazen_test."Payments" p where p."BookingId"=b."Id" and p."Status"=2)p on true where b."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and abs(coalesce(p.paid,0)-b."TotalPrice")>0.01),
'ledger',(select count(*) from casazen_test."OwnerLedgerEntries" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'),
'statements',(select json_agg(json_build_object('month',"PeriodMonth",'status',"Status",'issues',"IssuesJson",'balance',"ClosingBalance")) from casazen_test."OwnerStatementVersions" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'),
'files',(select json_agg(json_build_object('title',d."Title",'size',v."SizeBytes",'exists',s.id is not null)) from casazen_test."OwnerDocuments" d join casazen_test."OwnerDocumentVersions" v on v."OwnerDocumentId"=d."Id" left join storage.objects s on s.name=v."StorageKey" and s.bucket_id='casazen-test-private' where d."OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8'),
'overdueOpen',(select count(*) from casazen_test."InternalActivities" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and "Status"<>'Completed' and "Deadline"<now()),
'documents',(select count(*) from casazen_test."FiscalDocuments" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and "Status"=2),
'unencryptedRecipients',(select count(*) from casazen_test."FiscalDocuments" where "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' and "RecipientName" not like 'CfDJ%')
) data