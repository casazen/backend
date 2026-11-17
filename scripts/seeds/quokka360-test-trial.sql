-- Pilot fixture only: simulated trial, no Stripe subscription or charge.
BEGIN;
UPDATE casazen_test."Orgs"
SET "IsAgency"=true,"PlanTier"=3,"SubscriptionStatus"=1,
"CurrentPeriodEnd"='2026-12-17T23:00:00Z',"IsAgencyTierDowngraded"=false,"UpdatedAt"=now()
WHERE "Id"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND "Name"='Quokka360'
AND "SubscriptionId" IS NULL AND "StripeCustomerId" IS NULL
AND EXISTS (SELECT 1 FROM casazen_test."OrgMembers" m WHERE m."OrgId"=casazen_test."Orgs"."Id" AND m."UserId"='auth0|6afb6c7bb2c0f85f4e97f136' AND m."Role"=1 AND m."Status"=1);
COMMIT;