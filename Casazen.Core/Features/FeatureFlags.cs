namespace Casazen.Core.Features;

/// <summary>
/// Feature flags: one boolean per flag in the <c>Features</c> configuration section
/// (<c>Features:OtaPartnerApi</c>, environment variable <c>Features__OtaPartnerApi</c>).
/// A missing or non-boolean value means <b>off</b>: a flag is on only when explicitly set to <c>true</c>.
/// </summary>
/// <remarks>
/// Adding a flag (docs/runbooks/feature-flags.md): a constant here, listed in <see cref="All"/> (the frontend reads it
/// from <c>GET /api/public/features</c> as a camelCase key), <c>[FeatureGate(FeatureFlags.X)]</c> on the endpoints,
/// <see cref="IFeatureFlags"/> in background jobs and services.
/// </remarks>
public static class FeatureFlags
{
    public const string SectionName = "Features";

    /// <summary>
    /// D10: Airbnb / Booking.com partner API integrations (#31-35, in freeze): <c>api/ota</c>,
    /// <c>api/properties/{id}/ota-integrations</c>, <c>webhooks/ota/{platform}</c>, the OTA adapters and the
    /// <c>ota-sync-all</c> / <c>booking-pull-all</c> recurring jobs. iCal import/export is not behind this flag.
    /// </summary>
    public const string OtaPartnerApi = "OtaPartnerApi";

    /// <summary>
    /// D11: AI supplier discovery (US-014, in freeze): <c>POST api/service-requests/match-supplier</c>, the external
    /// web search and the LLM extraction of "nearby businesses" (<c>IAiSupplierDiscoveryService</c>) and the AI match
    /// reason. Off: the endpoint answers 404 and no call reaches the AI provider. The manual service request
    /// (<c>POST api/service-requests</c> with a chosen supplier) is not behind this flag.
    /// </summary>
    public const string AiSupplierDiscovery = "AiSupplierDiscovery";

    /// <summary>
    /// D15 / LT-01: RLI filing through an external provider (Openapi DocuEngine, docs/integrations/rli-esign.md), off
    /// until the legal opinion on the provider's filing professional and a real provider client exist. Off:
    /// <c>POST api/leases/{id}/registration</c> answers 404, the <c>lease-registration-status-poll</c> job is not
    /// registered and no call reaches the provider. The manual registration (the landlord files on the official
    /// channel and records number, date and receipt) is not behind this flag and is the default path.
    /// </summary>
    public const string RliProvider = "RliProvider";

    /// <summary>
    /// D15 / LT-02: contract signature through an external e-signature provider (docs/integrations/rli-esign.md §3), off
    /// until the budget and the legal opinion on the FEA exist and a real provider client is written. Off:
    /// <c>POST api/leases/{id}/signing</c> and <c>POST webhooks/esign</c> answer 404, the <c>lease-sign-status-poll</c>
    /// job is not registered and no call reaches a provider. The offline signature (download the contract, have it
    /// signed, upload the signed PDF with the stipula date) is not behind this flag and is the default path. On:
    /// <c>ESign:WebhookSecret</c> is required at startup.
    /// </summary>
    public const string ESignProvider = "ESignProvider";

    /// <summary>
    /// SP-02 (redesign wave, decision D34): booking from the public showcase of a supplier (<c>/fornitori/{slug}</c>), off
    /// until the product owner turns it on. It gates the public reads of the showcase (services, slots, estimate: SP-09) and
    /// the booking endpoints (hold and e-mail check: SP-10) and the customer's own area of a booking (find it again, cancel it,
    /// move it, answer a proposed time: SP-11, <c>api/public/supplier-bookings/*</c>), 404 when off. <b>SP-10 and SP-11 both have
    /// to be deployed before it is turned on</b>: a customer who can book and cannot manage the booking is a promise the e-mails
    /// make and the product does not keep. <b>On, the application needs
    /// <c>Suppliers:Showcase:PrivacyNoticeVersion</c> and, outside Development and Testing, <c>Suppliers:CustomerIndexKey</c></b>
    /// or it does not start (<see cref="Casazen.Core.Options.ShowcaseBookingOptionsValidator"/>). The upkeep jobs of the
    /// bookings are registered whatever the flag says. The service catalog (<c>api/supplier/services</c>) does not depend on
    /// it: every supplier has one.
    /// </summary>
    public const string SupplierShowcaseBooking = "SupplierShowcaseBooking";

    /// <summary>
    /// SP-02 (redesign wave, decision D2): payment of a supplier's work inside CasaZen (direct charge on the supplier's
    /// Stripe account with the platform commission), off until the product owner turns it on. SP-14 puts the supplier's Stripe
    /// Connect account behind it (<c>api/supplier/payments/*</c>: onboarding, state, Express Dashboard link; 404 while it is
    /// off); SP-15a also gates the creation of the payments (a request is taken as an online one only with it on, and the supplier's
    /// payment request is a 404 without it). The processing of the Stripe webhooks (<c>account.updated</c> and, later, the payments in
    /// flight), the payer's page and sessions and the supplier's offline record are not behind it. Without it the existing manual flow
    /// ("Segna pagato" by the host) stays the only one.
    /// </summary>
    public const string SupplierOnlinePayments = "SupplierOnlinePayments";

    /// <summary>
    /// SP-04 (redesign wave, decision D8): automatic cancellation of the service requests nobody answered. Every request now
    /// gets a deadline (<c>ResponseDueAt</c>, 120 minutes after its creation for a host's request); with the flag on, the
    /// recurring job <c>service-request-auto-cancel</c> (every 10 minutes) moves the new requests past their deadline to
    /// <c>Annullato</c> (reason <c>NoResponse</c>) and tells the host and the supplier. <b>Off by default</b>: it changes what
    /// happens to the requests that exist, so the product owner turns it on when the console and the apps show the new
    /// status. Off: the job is not scheduled (an earlier schedule is removed) and nothing is ever cancelled by time.
    /// </summary>
    public const string SupplierRequestAutoCancel = "SupplierRequestAutoCancel";

    /// <summary>
    /// SP-BKP (product owner, 2026-10-29/30): backup suppliers for a host's request. With the flag on, a request a host sends to a
    /// supplier (the primary) is sent at the same moment to up to three backups, declared as such: the primary has one hour
    /// (<c>Suppliers:ServiceRequests:PrimaryResponseMinutes</c>) to take the job, and the first backup that accepts has it if the
    /// primary does not. <b>Off by default</b>: it changes what the suppliers receive and what the host sees. The flag only gates the
    /// <b>creation</b> of a group: a group that exists is followed to the end whatever the flag says (the recurring job
    /// <c>service-request-groups</c> is always registered, and the suppliers' answers are not gated), so turning the flag off never
    /// leaves a request hanging. A host can still turn the rule off for its org (<c>Org.SupplierBackupsEnabled</c>) or for one request.
    /// </summary>
    public const string SupplierRequestBackups = "SupplierRequestBackups";

    /// <summary>
    /// SEO-INVITE (product owner, 2026-10-13): CasaZen is on private invitation, prices and the onboarding of new customers are
    /// not defined yet. Off (the default): the public SEO pages carry no call to action to sign up ("Pubblica la tua casa"):
    /// <c>cta</c> is <c>null</c> in <c>GET api/public/content/…</c> and the crawler page has no link to <c>/signup</c>. The
    /// registration itself is closed outside the code: Auth0 "Disable Sign Ups" on every connection, and
    /// <c>Suppliers__AnyComune=false</c> with no pilot comuni (docs/runbooks/feature-flags.md).
    /// </summary>
    public const string SeoSignupCta = "SeoSignupCta";

    /// E01/F01/US01 (ADR-010): B2B agency tier features including property owner registry, owner portal access,
    /// monthly statements, withholding tax management, wire transfers, invoicing, internal staff activities,
    /// and trusted supplier management. Off (default): <c>agency.*</c> policies return 403 feature_requires_agency_tier,
    /// agency endpoints return 404, and frontend B2B routes are hidden. Gates all features requiring
    /// <c>Org.PlanTier == PlanTier.Agency</c>.
    /// </summary>
    public const string AgencyTier = "AgencyTier";

    /// <summary>
    /// CF-FIX (product owner, 2026-10-25): the ConcordatoFacile service (ADR-005 to ADR-009), on in the test environment and off
    /// in production until CasaZen confirms to ConcordatoFacile that the trial run and the privacy framework (v1.3 R24) are done.
    /// Off: every endpoint of <c>ConcordatoFacileController</c> (checkout, access, gift, signed link, status, the practice
    /// lookup of the upload page) and <c>POST api/leases/drafts/concordatofacile</c> answer 404, and the frontend does not show the
    /// service. <b>Not behind it</b>: the processing of the Stripe webhooks (a payment already made is still recorded), the status
    /// client, the startup validation of the <c>ConcordatoFacile</c> options and the gifts of the admin controller.
    /// </summary>
    public const string ConcordatoFacile = "ConcordatoFacile";

    /// <summary>
    /// DEB-01 (2026-10-31): the reading of the identity document (MRZ, OCR, NF-10) and the selfie check (liveness, face match, NF-11,
    /// ADR-013) of the guest's check-in page, <c>POST api/public/checkin/{token}/document</c> and <c>POST …/selfie</c>. <b>Off by
    /// default</b>: the services behind them are placeholders (MRZ and OCR never read anything, the liveness is always true, the face
    /// similarity is a fixed 0.75), so a public endpoint would hand out made-up results. Off, both answer 404 before anything of the
    /// request is read, so no image reaches the application. The guest's data is entered by hand (<c>POST api/public/checkin/{token}</c>,
    /// not behind this flag). Turn it on only once real models replace the placeholders and the assessment of the biometric data
    /// (art. 9 GDPR) is done.
    /// </summary>
    public const string CheckInIdentityVerification = "CheckInIdentityVerification";

    /// <summary>
    /// E02/F01/US02/T01 (ADR-023): the Octorate connector, <b>off by default</b> until the sandbox is validated. Off, every endpoint of
    /// the connector answers 404 before authentication: the connection and the consent (<c>api/octorate/connection</c>,
    /// <c>api/octorate/connect/*</c>), the mapping of the listings (<c>api/octorate/properties</c>, <c>api/octorate/mapping/*</c>) and
    /// the webhook (<c>api/webhooks/octorate/{secret}</c>). On, outside Development and Testing, the <c>Octorate</c> options of the consent
    /// are required at startup (<c>OctorateOptionsValidator</c>). This task owns the flag: the other Octorate tasks only read it.
    /// </summary>
    public const string OctorateConnector = "OctorateConnector";

    /// <summary>
    /// E07/F06/US01/T01 (NF-95, CRM-08, Q1-29): the unfinished marketing module, <b>off by default</b> until it is safe to send. It closes the
    /// CRM profiles (<c>api/crm/*</c>), the segments and the campaigns (<c>api/campaigns/*</c>), and the menu entries of the web app that open
    /// them (the frontend reads the key <c>crmMarketing</c>). Off, every endpoint of <c>CrmController</c> and <c>CampaignsController</c> answers
    /// 404 like a route that does not exist, before authentication, and the Hangfire job that sends a campaign scheduled before
    /// (<c>CampaignSendJob</c>) sends nothing: it logs a warning, ends without a retry and puts the campaign back to <c>Approved</c>.
    /// <b>Not behind it, on purpose</b>: the public consent, confirmation, unsubscribe and welcome endpoints, the GDPR endpoints, and the
    /// collection of contacts and consents at the check-in and at the checkout: a legal record is made whatever the flag says, also without
    /// the key (the withdrawal the host records is saved and its suppression is skipped, with an error in the log). On, outside Development and
    /// Testing, <c>Crm:ContactIndexKey</c> is required at startup (<c>CrmOptionsValidator</c>), and only then. Runbook
    /// <c>docs/runbooks/crm-marketing.md</c>.
    /// </summary>
    public const string CrmMarketing = "CrmMarketing";

    /// <summary>
    /// E10/F01/US01/T02 (NF-104..NF-107): the activation of CasaZen with Octorate, <b>off by default</b> until the plan, the flows and the
    /// panel steps are ready to be shown. Off, every endpoint of epic E10 answers 404 before authentication
    /// (<c>api/onboarding/flows/*</c> and the ones the later tasks of E10 add) <b>except</b> <c>GET api/onboarding/status</c>, which stays
    /// reachable and answers exactly as before (its <c>flows</c> member is empty). On, that endpoint also gives the summary of the six
    /// flows to a caller who passes <c>property.read</c>. This task owns the flag: the other E10 tasks only read it. It needs no variable
    /// to start: nothing in the application is configured by it.
    /// </summary>
    public const string OctorateOnboarding = "OctorateOnboarding";

    /// <summary>
    /// E06/F02/US02/T02 (NF-82, FIS-02, PO-05): the issuance of fiscal documents by CasaZen, <b>off by default</b> until the accountant has approved the
    /// rules of the documents (<c>FiscalDocuments</c> options) and the issuance (epic E06/F01) is delivered. It gates only two things: the issuance
    /// itself, and the choice of the mode «CasaZen issues, direct bookings only» of the issuer setting (<c>PUT api/fiscal/documents/settings</c> answers
    /// 422 <c>document_issuance_not_available</c> for it while the flag is off; a booking cannot be locked to CasaZen as its issuer either). Not behind
    /// it: reading and saving the setting for the other two modes (Octorate, the accountant), the invoice data of a booking and the reading of the
    /// documents Octorate issued. It needs no variable to start. This task owns the flag: the other tasks of epic E06 only read it.
    /// </summary>
    public const string FiscalDocumentIssuance = "FiscalDocumentIssuance";

    /// <summary>
    /// E06/F02/US01/T04 (NF-81, NF-82, FIS-01, FIS-02): the sync of the billing data with Octorate, <b>off by default</b> until the customer (address
    /// book) endpoints are proven in the Octorate sandbox and CasaZen's outbound IPs are whitelisted (Octorate refuses the guests' sensitive data
    /// before that). It gates two handlers and nothing else: the pre-fill of the invoice data of a booking imported from Octorate (<c>BookingCreated</c>)
    /// and the write of the billing data saved in CasaZen to Octorate, only where Octorate is the issuer (<c>InvoiceDataSaved</c>). Both also need
    /// <c>Features:OctorateConnector</c>. It needs no variable to start. This task owns the flag: the other tasks of epic E06 only read it.
    /// </summary>
    public const string OctorateBillingSync = "OctorateBillingSync";

    /// <summary>
    /// E03/F02/US01/T03 (NF-52, CHK-12): the compliance traffic light of the cockpit, <b>off by default</b> until the proofs it asks for can be
    /// archived. It gates <c>GET api/compliance/summary</c> and the two other readers of the same summary (the Home of the day,
    /// <c>GET api/dashboard/today</c>, and «Gestisci con AI»). Off, the summary is exactly what it was before: the items carry no light, reason or
    /// evidence, the five new sections (<c>identityPending</c>, <c>receiptsToUpload</c>, <c>manualSendsRequired</c>, <c>connectionsToFix</c>,
    /// <c>periodItems</c>) are empty, the section of the stays to send by hand lists every stay as before, the status is not even evaluated and the
    /// feed of the red and yellow items into the «Da fare» list (<c>ComplianceActivityFeeder</c>) writes nothing. On, every item says whether the host
    /// is compliant and the «Da fare» list is kept in step with the cockpit. Why it is off: nothing in the application archives the proofs that turn
    /// an item green yet (the receipts and tax proofs of <c>ManualComplianceProof</c>, the receipt a report points to), so on, every stay of the last 90
    /// days would get a red To do nobody can close. Also behind it (E03/F02/US03/T01): the coverage report of the proof archive (<c>GET api/compliance/report</c> and
    /// <c>report/export</c>, 404 while off) and the coverage CSV of the ZIP of the proofs. Not behind it: <c>GET api/properties/{propertyId}/compliance/connection-health</c>,
    /// which writes nothing, and the archive of the proofs itself (list, download, export, upload, the log of a stay). It needs no variable to start. This task owns the flag: the other tasks of epic E03 only read it.
    /// </summary>
    public const string ComplianceTrafficLight = "ComplianceTrafficLight";

    /// <summary>
    /// E05/F02/US02/T02 (NF-63, AI-06, FD-21): the translation of a message by CasaZen's AI, <b>off by default</b>. What a guest wrote is personal data,
    /// and the AI provider is an external sub-processor (DeepSeek, with its seat and its archives in China, the basis of the transfer being a decision
    /// of the product owner: <c>docs/runbooks/ai.md</c>). Off, <c>POST api/messages/translate</c> never asks the AI provider anything, not even for the text
    /// the host is writing: it asks the channel manager to translate the message it holds (the id of its message, no word of CasaZen's leaves) and,
    /// when that cannot be done either, answers 422 <c>translation_unavailable</c>. The endpoint itself, the templates and the reply are not behind it.
    /// On, the text goes to the provider after the scrub of <c>MessageTranslationPrompt</c> (e-mail addresses, phone numbers, fiscal codes and the names of
    /// the conversation; documents and addresses of a person are not recognized). It needs no variable to start; it works only with an AI provider in
    /// effect (<c>Ai__Provider</c> and <c>Ai__ApiKey</c>). Turn it on only after the product owner has decided the transfer.
    /// </summary>
    public const string MessageTranslationAi = "MessageTranslationAi";

    /// <summary>
    /// E06/F03/US02/T01 (NF-84, FIS-04, D19): the import of the documents Octorate issued, <b>off by default</b> until the documents list is proven on a
    /// structure with the invoice module: the operations are documented in the OpenAPI (S24) but the sandbox answers an empty list (S25), so the
    /// names of the members are not verified. It gates the recurring job <c>octorate-document-import</c> (removed while off), the import service and
    /// <c>GET api/fiscal/documents/{id}/octorate-pdf</c> (404 while off). It also needs <c>Features:OctorateConnector</c> and an org whose issuer is
    /// Octorate with a connected connection. It needs no variable to start. This task owns the flag.
    /// </summary>
    public const string OctorateDocumentImport = "OctorateDocumentImport";

    /// <summary>
    /// E05/F05/US01/T01 (NF-66, NF-67, PO-08, PO-09): the assistant that answers guests' messages, <b>off by default</b>. What a guest wrote is personal data
    /// and the AI provider is an external sub-processor: the flag stays off until the privacy notice, the processing agreement and the <c>Ai:Subprocessor</c>
    /// details of PO-09 exist. Off, every endpoint of the assistant (<c>api/assistant/*</c>: the knowledge base, the rules, the situations and, with the later
    /// tasks of the feature, the drafts and the lessons) answers 404 before authentication, and the assistant processes no message. The frontend reads the key
    /// <c>messageAssistant</c> from <c>GET api/public/features</c>. This task owns the flag: the other tasks of feature E05/F05 only read it. It needs no
    /// variable to start: nothing in the application is configured by it. Even with the flag on, no organization is served until it turns its own switch
    /// on (<c>OrgAssistantRules.Enabled</c>, off by default).
    /// </summary>
    public const string MessageAssistant = "MessageAssistant";

    /// <summary>
    /// E14/F01/US01/T01 (NF-114, D9, D15): the invoices of the property manager to the owners, made by CasaZen from a version of the owner statement, <b>off by
    /// default</b> and turned on only in the test environment until the accountant confirms the ⚖ choices (R4, I10, I11). Off, every endpoint of
    /// <c>api/owner-invoices</c> (the list, the draft, the issue, the credit note, the discard of a draft), <c>POST api/agency-invoices/generate</c> (the drafts of a
    /// period) and who holds the portal accounts (<c>PUT api/billing-setup/channels</c>, <c>api/billing-setup/channel-account-holders</c>, a part of the billing setup
    /// that needs <see cref="FiscalDocumentIssuance"/> too) answer 404 before authentication, and the section <c>channels</c> of <c>GET api/billing-setup</c> shows no
    /// values. <b>Not behind it</b>: the reading of the legacy agency invoices; the PDF and the XML of an owner invoice (reads of <c>api/fiscal/documents</c>: a
    /// document issued stays a document). The endpoints of the guest documents refuse to change an owner invoice (422 <c>document_owner_invoice</c>). It needs no
    /// variable to start. The frontend reads the key <c>ownerInvoicing</c>.
    /// </summary>
    public const string OwnerInvoicing = "OwnerInvoicing";

    /// <summary>
    /// E15/F01/US01/T01 (NF-101, NF-87, NF-115, D11, D15): the accountant package, one ZIP of a month or a year in a fixed structure and the agency summary, <b>off by
    /// default</b> and turned on only in the test environment until the accountant confirms the ⚖ choices that shape the files (the Swiss purchase tax on the portal
    /// commissions, the Italian register of the sales). Off, <c>GET api/accountant-package</c> and <c>GET api/accountant-package/agency-summary</c> answer 404 before
    /// authentication. It needs no variable to start; the caps of the ZIP are the ones of the document archive (<c>DocumentArchive:MaxExportFiles</c>,
    /// <c>DocumentArchive:MaxExportBytes</c>). The frontend reads the key <c>accountantPackage</c>.
    /// </summary>
    public const string AccountantPackage = "AccountantPackage";

    /// <summary>
    /// E25/F01/US01/T01 (NF-119, decision O12): the tourist tax paid online in the guest check-in, on the organization's own connected account, <b>off by
    /// default</b> and turned on only in the test environment after the merge. Off, the endpoints of the guest (<c>GET api/public/checkin/{token}/tourist-tax</c>,
    /// <c>POST …/tourist-tax/payment-intent</c>), the host's «Pagata in struttura» (<c>POST api/bookings/{id}/tourist-tax/paid-at-property</c>) and the setting of
    /// the organization (<c>api/orgs/me/tourist-tax-settings</c>) answer 404 before authentication, <c>GET api/bookings/{id}/tourist-tax</c> has no
    /// <c>collection</c> block and the consumer of the booking events (the refund of a cancelled stay, the «To do» of a difference) does nothing. On, a guest
    /// pays only where the organization turned its own setting on and its account takes payments (409 <c>tourist_tax_payment_not_available</c> otherwise).
    /// <b>Not behind it</b>: the Stripe webhooks of a payment already made (it is recorded). It needs no variable to start. The frontend reads the key
    /// <c>touristTaxOnlinePayment</c>. The lock code is never held back unless the organization turns on <c>TouristTaxRequiredBeforeAccessCode</c>: then the server
    /// holds it back (<c>IAccessCodeService.ReleaseToGuestAsync</c>, the gate of every release to a guest) while the stay owes a tax the guest can pay online and has
    /// not paid; the selfie step answers <c>lockCodeHeld: tourist_tax_due</c> instead of the code.
    /// </summary>
    public const string TouristTaxOnlinePayment = "TouristTaxOnlinePayment";

    /// <summary>
    /// E01/F04/US01/T03 (NF-21, DEB-06, ADR-022): the automation routines, <b>off by default</b> until the actions a host needs are real (today the To do of
    /// <c>CreateActivity</c> and the wait; the others are listed by <c>RoutineActionCatalog</c> as not available yet). It closes the whole module. Off, every
    /// endpoint of <c>RoutinesController</c> (<c>api/routines</c>: the list, one routine, its changes, its log, the simulation and the catalog of the actions)
    /// answers 404 like a route that does not exist, before authentication; no routine runs (the engine ignores the events of the bus, and a wait that
    /// comes due changes nothing: the job reads and writes nothing and the execution stays waiting); and <c>TimeEventEmitterJob</c> writes no time trigger.
    /// The routines already saved are kept. It needs no variable to start. The frontend reads the key <c>routines</c> (the routines page).
    /// </summary>
    public const string Routines = "Routines";

    /// <summary>
    /// E23/F01/US01/T01 (NF-122, NF-63, decision O10 of the operations plan of 2026-11-14): the check-in link of a booking of a portal that has no e-mail address goes to the guest
    /// in the reservation thread of the portal chat, once, as an automatic message, and the booking shows a waiting state («in attesa del permesso chat», «in attesa della
    /// conversazione») with a «To do» whose «Copia link» lets the host carry the link itself. <b>Off by default</b> and turned on only in the test environment: the chat of the
    /// channel manager needs a permission of the application that Octorate has not given in the sandbox. Off, the daily job <c>guest-checkin-send</c> behaves as before (it
    /// writes the delivery record of the booking with the state <c>WaitingContact</c>, so the host can see why, and raises no «To do»), the hourly job
    /// <c>checkin-link-delivery-retry</c> is not scheduled and <c>GET api/bookings/{id}/checkin/link-delivery</c> only reads what was recorded. It needs no variable to start,
    /// and works only while <see cref="OctorateConnector"/> is on too. The frontend reads the key <c>portalChatCheckInLink</c>.
    /// </summary>
    public const string PortalChatCheckInLink = "PortalChatCheckInLink";

    /// <summary>
    /// E23/F01/US01/T02 (NF-122, NF-63, S18, D24): for a booking of a portal that has no e-mail address, CasaZen reads the e-mail address of the guest from the channel manager
    /// (<c>POST /rest/v2/reservation/{acc}/{refer}/contactInfo</c>), <b>once for the booking, for the check-in link only</b>, never in bulk and never into the CRM, and sends the link
    /// by e-mail; a phone number that comes with it is kept for the host's view only. <b>Off by default</b>: Octorate releases the contact only to the static outbound IP
    /// addresses registered with it, and the Railway plan that gives them (Pro) is not active. Off, nothing is asked. On without the registration, the read is refused (401 or
    /// 403): the booking waits in the state <c>WaitingStaticIps</c> and the read is repeated at most once a day. It needs no variable to start, and works only while
    /// <see cref="OctorateConnector"/> is on too. The frontend reads the key <c>guestContactInfo</c>.
    /// </summary>
    public const string GuestContactInfo = "GuestContactInfo";

    /// <summary>
    /// E17/F01/US02/T01 (NF-116, NF-86, D13, D15): the submission of the Italian electronic invoices to the SDI through the accredited channel (the provider adapter) and
    /// their correction after a rejection, <b>off by default</b> and turned on only in the test environment until the accountant confirms the ⚖ choices (the provider, the
    /// reuse of the number of a rejected document). Off, the write endpoints of <c>api/fiscal/documents/{id}/sdi</c> (<c>submit</c>, <c>declare</c>, <c>correct</c>) and the webhook
    /// of the provider (<c>api/webhooks/sdi/{secret}</c>) answer 404 before authentication, and the recurring job <c>fiscal-sdi-status</c> is removed; the state of a document
    /// (<c>GET api/fiscal/documents/{id}/sdi</c>) is a read and stays. They are behind <see cref="FiscalDocumentIssuance"/> too. With no provider configured
    /// (<c>Sdi:Provider:*</c>) a submission answers <c>NotConfigured</c> and changes nothing, and the manual declaration works all the same. The frontend reads the key
    /// <c>sdiSubmission</c>.
    /// </summary>
    public const string SdiSubmission = "SdiSubmission";

    /// <summary>
    /// E18/F01/US03/T01 and E18/F01/US02 (NF-117, D15): the Italian tax reporting of the owners (the checks of the limit of units and of the company tenant, and the exports of the
    /// Certificazione Unica, the 770 and the communication of the short-let contracts, the owner's view of its withholding, and the year-end exports of the tourist tax), <b>off by default</b>
    /// and turned on only in the test environment until the accountant confirms the ⚖ choices (I6, I7, I8, T2). Off, every endpoint of <c>OwnerTaxDecisionsController</c>
    /// (<c>api/owner-tax-decisions</c>, <c>api/owners/{id}/tax-checks</c>, <c>api/owners/{id}/external-short-let-units</c>) answers 404 before authentication, and the warnings the UI shows are
    /// not made: no <c>warnings</c> in the owner readiness, no non-blocking issue on the statement versions, no «Da fare» <c>company_tenant_decision</c>. The same 404 for the new exports of
    /// <c>WithholdingController</c> (<c>api/withholding/annual/{year}/export</c>, <c>api/withholding/annual/{year}/owners/{id}/pdf</c>, <c>api/withholding/770/{year}/export</c>,
    /// <c>api/withholding/contracts-communication/{year}/export</c>) and of <c>TouristTaxSummaryController</c> (<c>api/tourist-tax/year-end/{year}/declaration|agent-account|ticino</c>);
    /// with it off <c>GET api/owner-portal/withholding</c> answers the empty summary it was. <b>Not behind it, on purpose</b>: the facts of the withholding
    /// (<c>IOwnerTaxChecks.TreatmentOfStaysAsync</c>: an owner past the limit is a business whatever the screens show; a decision already recorded is followed), the field
    /// <c>coOwnerTaxReporting</c> of the mandate, the annual data and its filing states (<c>api/withholding/annual</c>) and the F24 data. It needs no variable to start.
    /// The frontend reads the key <c>taxReportingExports</c>.
    /// </summary>
    public const string TaxReportingExports = "TaxReportingExports";

    /// <summary>
    /// E20/F02/US01/T02 (NF-121, NF-16, decision O4): the deletion of the guests' documents once the stay is covered by an archived Alloggiati receipt, <b>off by default</b>: the
    /// deletion cannot be undone, so it is turned on in the test environment after the merge and in production only after the test has shown it. Off, the daily job
    /// <c>guest-document-purge</c> and the run queued after a receipt upload read nothing and delete nothing. On, the photos of identity documents a verification kept (the objects
    /// and the rows) and the legacy scan of the booker of every stay that a receipt covers are deleted; the document data of the guests of the stay (<c>StayGuest</c>) are never
    /// touched. No endpoint is behind it and it needs no variable to start. The frontend reads the key <c>purgeDocumentsAfterReceipt</c>.
    /// </summary>
    public const string PurgeDocumentsAfterReceipt = "PurgeDocumentsAfterReceipt";

    /// <summary>
    /// E05/F07/US01/T01 (NF-71, MSG-11, ADR-022-R11): the scheduled messages of the routines, <b>off by default</b> and turned on only in the test environment until the product owner
    /// confirms the send window and the retry times (<c>Messaging:ScheduledMessages</c>). Off, nothing is planned (the hourly job <c>scheduled-message-plan</c> and the handler of the booking
    /// events do nothing), the sweep <c>scheduled-message-sweep</c> is not scheduled, the three endpoints of <c>ScheduledMessagesController</c> answer 404 before authentication, and the
    /// action <c>MessageToGuest</c> of a routine does what it did before (it writes to the conversation of the booking with the template it names, no row of the plan, no variable of the
    /// check-in link or of the access code). On, the action goes through the plan: the message is planned for each booking, edited or skipped by the host, and sent once, whichever of the
    /// routine, the sweep and «Send now» reaches it first. It works on the routines, so it also needs <see cref="Routines"/> on: with the module of the routines closed nothing is planned or
    /// sent, whatever this flag says. It needs no variable to start. The frontend reads the key <c>scheduledMessages</c>.
    /// </summary>
    public const string ScheduledMessages = "ScheduledMessages";

    /// <summary>
    /// E30/F02/US01/T01 (NF-127, D9, casazen/backend#979): the staged alerts about the 24 hours of the notification of the guests of a Ticino property to the
    /// cantonal police, <b>off by default</b> and turned on only in the test environment until the product owner has seen them on a real arrival. Off, the hourly
    /// <c>stay-alerts</c> job reads no Ticino day and writes no alert state, and nothing is queued: no e-mail, no push, no entry of the bell. On, every
    /// arrival day of a Ticino property that is not closed (no receipt of the portal, and the declaration when it exists) gets three stages, each sent once:
    /// the arrival day («prepare the file»), six hours before the deadline, and overdue followed by at most two reminders, to the contact address of the organization
    /// (the one e-mail) and to the person in charge of the property and the administrators of the organization (push, bell and the live update of the open pages, the
    /// types <c>ticino-notification-due</c> and <c>ticino-notification-overdue</c>). The cockpit section of the days to notify (<c>ticinoNotifications</c>) is not behind it. Independent of
    /// <see cref="ComplianceTrafficLight"/>. It needs no variable to start. The thresholds are <c>Compliance__Ticino*</c>.
    /// </summary>
    public const string TicinoNotificationAlerts = "TicinoNotificationAlerts";

    /// <summary>Every flag, in the order exposed to the frontend.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        OtaPartnerApi,
        AiSupplierDiscovery,
        RliProvider,
        ESignProvider,
        SupplierShowcaseBooking,
        SupplierOnlinePayments,
        SupplierRequestAutoCancel,
        SeoSignupCta,
        AgencyTier,
        ConcordatoFacile,
        SupplierRequestBackups,
        CheckInIdentityVerification,
        OctorateConnector,
        CrmMarketing,
        OctorateOnboarding,
        FiscalDocumentIssuance,
        OctorateBillingSync,
        ComplianceTrafficLight,
        MessageTranslationAi,
        OctorateDocumentImport,
        MessageAssistant,
        OwnerInvoicing,
        AccountantPackage,
        TouristTaxOnlinePayment,
        Routines,
        PortalChatCheckInLink,
        GuestContactInfo,
        SdiSubmission,
        TaxReportingExports,
        PurgeDocumentsAfterReceipt,
        ScheduledMessages,
        TicinoNotificationAlerts,
    ];
}
