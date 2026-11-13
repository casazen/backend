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
    ];
}
