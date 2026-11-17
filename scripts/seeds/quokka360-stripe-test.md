# Quokka360 Stripe test subscription

Configured 2026-11-17. All Stripe objects have livemode=false; production was not changed.

- Org: ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8
- Customer: cus_Ve9ugR1M6L8uDQ
- Product: prod_Ve9wB8RbUYKCOQ (CasaZen Partner)
- Price: price_1UcrcpRPwlDgw3NT0BR04mfO, EUR 12.00/property/month, tax_behavior=exclusive
- Subscription: sub_1Ucri0RPwlDgw3NTdPJ9iIhX, active, quantity=3, collection_method=send_invoice, days_until_due=30
- First invoice: in_1Ucri0RPwlDgw3NTPxrcpLpd; EUR 36.00 net + EUR 7.92 VAT = EUR 43.92. Created as draft, not paid.
- Tax registration: taxreg_1UcrhNRPwlDgw3NTic6s9uqk, IT standard, TEST ONLY.
- Customer tax simulation country: IT. This is a test scenario, not verified legal billing information for Quokka.
- Existing account product tax code reused: txcd_10000000.
- Tax calculation: taxcalc_1UcrhaRPwlDgw3NTTMRcMC3C, taxability_reason=standard_rated, 22%.
- automatic_tax.enabled=true.
- Railway test Billing__Prices__Agency set to the Partner price ID.

The existing subscription lifecycle webhook automatically populated Orgs.SubscriptionId, SubscriptionStatus=Active and CurrentPeriodEnd in casazen_test. The previous simulated Trialing state was replaced by the received Stripe state. No manual paid status was written.

Quantities are initially 3. No new automatic property-count synchronization was implemented in this task; reconcile quantity when properties change.

Do not run the earlier trial fixture again after provisioning this subscription. The tax registration/customer location are test data and must not be copied to production without validating the real billing facts.
