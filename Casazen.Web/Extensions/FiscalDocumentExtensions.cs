using Casazen.Core.Documents;
using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.External.Sdi;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Resources;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class FiscalDocumentExtensions
{
    public static IServiceCollection AddCasazenFiscalDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IInvoiceDataService, InvoiceDataService>();

        services.AddOptions<FiscalDocumentRulesOptions>().Bind(configuration.GetSection(FiscalDocumentRulesOptions.SectionName));

        services.AddOptions<FiscalTaxRulesOptions>().Bind(configuration.GetSection(FiscalTaxRulesOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FiscalTaxRulesOptions>, FiscalTaxRulesOptionsValidator>();
        services.AddSingleton<IFiscalTaxRuleResolver, FiscalTaxRuleResolver>();

        services.AddScoped<IDocumentIssuerService, DocumentIssuerService>();

        // The whole setup of the invoicing in one place, the wizard «Fatturazione» of the onboarding guide (E11/F01/US01/T04): the sections and their missing items,
        // and the save of one section. The missing items come from the contributors, one for each piece of the setup, in the order of the wizard; the epics that own
        // another piece (the owner ledger: channels and mandates; the Italian owner issuers and the SDI: italy) register their own next to these, never in the service.
        services.AddScoped<IBillingSetupService, BillingSetupService>();
        services.AddScoped<IBillingSetupContributor, IssuerSectionContributor>();
        services.AddScoped<IBillingSetupContributor, VatSectionContributor>();
        services.AddScoped<IBillingSetupContributor, NumberingSectionContributor>();
        services.AddScoped<IBillingSetupContributor, TouristTaxSectionContributor>();
        services.AddScoped<IBillingSetupContributor, AutomationSectionContributor>();
        services.AddScoped<IBillingSetupContributor, ItalySectionContributor>();

        services.AddScoped<IBillingSetupContributor, ChannelCommissionsSectionContributor>();
        services.AddScoped<IBillingSetupContributor, OwnerMandatesSectionContributor>();

        services.AddScoped<IBillingSetupContributor, OwnerIssuerSetupContributor>();

        // The billing data and Octorate (E06/F02/US01/T04), both behind Features:OctorateConnector and Features:OctorateBillingSync: the invoice data of a
        // booking imported from Octorate is pre-filled from its customer record (BookingCreated), and the data saved in CasaZen is written to Octorate where
        // Octorate is the issuer (InvoiceDataSaved). Run by the outbox dispatcher, so neither can fail an import or a host's save.
        services.AddScoped<IEventHandler<DomainEvent>, InvoiceDataFromOctorateHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBillingWriteBackHandler>();

        services.AddScoped<IEventHandler<DomainEvent>, FiscalDocumentCheckOutHandler>();

        // The correction of the documents of a stay when its booking changes (E12/F01/US01/T03): the consumer of BookingModified, BookingCancelled, BookingNoShowMarked and PaymentRefunded
        // (a credit note, or the reversal receipt of a receipt, and the new draft), also behind Features:FiscalDocumentIssuance; idempotent per event.
        services.AddScoped<IEventHandler<DomainEvent>, FiscalDocumentBookingChangeHandler>();

        // The document sent to the guest (E12/F01/US02/T01): the signed link (Data Protection, purpose Casazen.FiscalDocument.Link, 30 days, nothing but the document id), the e-mail queued on Hangfire
        // and the two public reads that the link opens; the consumer of FiscalDocumentIssued and FiscalDocumentCreditNoteIssued sends it by itself when the profile has «e-mail to the guest» on (also
        // behind Features:FiscalDocumentIssuance). The e-mail needs the public address of the web app (App:PublicSiteBaseUrl) and the e-mail provider; without them nothing is sent and the send says so.
        services.AddSingleton<FiscalDocumentLinkTokens>();
        services.AddScoped<IFiscalDocumentDeliveryService, FiscalDocumentDeliveryService>();
        services.AddScoped<IEventHandler<DomainEvent>, FiscalDocumentEmailHandler>();

        services.AddSingleton<IDocumentTexts, ResxDocumentTexts>();

        // The PDF and CSV of an owner statement and the PDF of the F24 data, built from what is stored (E06/F01/US01/T02, NF-25).
        services.AddScoped<IStatementExportService, StatementExportService>();

        services.AddScoped<IPaymentProviderFeeService, PaymentProviderFeeService>();

        services.AddScoped<IFiscalNumberingService, FiscalNumberingService>();

        // The FatturaPA XML of a guest document, validated against the official schema embedded in the build (E06/F01/US02/T03, NF-83, NF-87):
        // the schema set is loaded once; without the files nothing is produced (fatturapa_schema_unavailable).
        services.AddSingleton<FatturaPaSchemaProvider>();
        services.AddSingleton<IFatturaPaXmlBuilder, FatturaPaXmlBuilder>();

        services.AddScoped<IOctorateDocumentImportService, OctorateDocumentImportService>();

        services.AddOptions<SwissTouristTaxOptions>().Bind(configuration.GetSection(SwissTouristTaxOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SwissTouristTaxOptions>, SwissTouristTaxOptionsValidator>();

        // Who collects the tourist tax of an Italian portal booking by default (FIX/BE-IT-TOURISTTAX-DEFAULTS, decision 2 of 2026-11-20), ItalianTouristTax: no portal by default, kept apart from the
        // Swiss list so that an Italian organization never inherits it; a channel that is not a portal stops the application with a clear message. Empty is valid.
        services.AddOptions<ItalianTouristTaxOptions>().Bind(configuration.GetSection(ItalianTouristTaxOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ItalianTouristTaxOptions>, ItalianTouristTaxOptionsValidator>();

        services.AddScoped<IGuestDocumentLineBuilder, GuestDocumentLineBuilder>();

        services.AddScoped<IItalianTouristTaxSetup, ItalianTouristTaxSetupService>();

        services.AddScoped<IDocumentPlanService, DocumentPlanService>();

        services.AddScoped<IFiscalDocumentService, FiscalDocumentService>();

        services.AddScoped<IBookingPaymentLedgerService, BookingPaymentLedgerService>();

        services.AddScoped<IOwnerFiscalProfileService, OwnerFiscalProfileService>();

        services.AddScoped<IOwnerAccountingService, OwnerAccountingService>();

        services.AddScoped<IOwnerBulkJobs, HangfireOwnerBulkJobs>();
        services.AddScoped<OwnerBulkCloseJob>();
        services.AddScoped<IOwnerReconciliationService, OwnerReconciliationService>();

        services.AddScoped<IOwnerStatementDocuments, OwnerStatementDocuments>();

        services.AddScoped<IOwnerPayoutService, OwnerPayoutService>();
        services.AddScoped<OwnerPayoutFacts>();

        // The prudent defaults of the owner ledger (E13/F01/US01/T01, ⚖ R2: the channel commissions when a reservation brings none), from the section
        // OwnerAccounting. Not validated at startup: a value that is not a portal or a percent is ignored and the stays of that channel are held back by
        // the readiness (channel_commission_missing), never posted with a guessed commission.
        services.AddOptions<OwnerAccountingOptions>().Bind(configuration.GetSection(OwnerAccountingOptions.SectionName));

        services.AddScoped<IOwnerMandateService, OwnerMandateService>();
        services.AddScoped<Casazen.Web.Infrastructure.OwnerMandateDocuments>();

        services.AddScoped<IOwnerDirectoryService, OwnerDirectoryService>();

        services.AddScoped<IPortalAccountHolderResolver, PortalAccountHolderResolver>();
        services.AddScoped<IPortalAccountSettingsService, PortalAccountSettingsService>();
        services.AddScoped<IOwnerInvoiceService, OwnerInvoiceService>();

        services.AddScoped<IOwnerIssuerProfileService, OwnerIssuerProfileService>();

        // The channel to the SDI (E17/F01/US02, NF-116): the port ISdiEInvoiceProvider is the adapter of the provider (a typed HTTP client, Sdi:Provider) when Sdi__Provider__Name is an adapter that
        // exists and its key is set, and the unconfigured one otherwise: nothing is then ever submitted, a document stays «to send» and its issuer declares it sent by hand. The secret of the address
        // of the provider's webhook is checked in constant time by the verifier. The submission is behind Features:SdiSubmission (and the issuance), the endpoints, the webhook and the job alike.
        services.AddOptions<SdiProviderOptions>().Bind(configuration.GetSection(SdiProviderOptions.SectionName));
        services.AddHttpClient<RestSdiEInvoiceProvider>((sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<SdiProviderOptions>>().Value;
            if (settings.ApiAddress is { } address)
                client.BaseAddress = address;

            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 120));
        });
        services.AddSingleton<SdiWebhookVerifier>();
        services.AddScoped<ISdiEInvoiceProvider>(sp => sp.GetRequiredService<IOptions<SdiProviderOptions>>().Value.IsConfigured
            ? sp.GetRequiredService<RestSdiEInvoiceProvider>()
            : new UnconfiguredSdiEInvoiceProvider());

        // The SDI states of the electronic invoices (submission, outcomes, declaration by hand) and the correction of a rejected one (E17/F01/US02/T01, T03); the consumer of the rejection raises
        // the «To do» «Correggi la fattura» and closes it when the invoice is corrected or credited; the item of the billing setup says which issuers wait for their VAT number to be enabled.
        services.AddScoped<IFiscalDocumentSdiService, FiscalDocumentSdiService>();
        services.AddScoped<IFiscalDocumentCorrectionService, FiscalDocumentCorrectionService>();
        services.AddScoped<IEventHandler<DomainEvent>, FiscalDocumentRejectionHandler>();
        services.AddScoped<IBillingSetupContributor, SdiIssuerRegistrationContributor>();

        services.AddScoped<IOwnerIssuedDocumentsExport, OwnerIssuedDocumentsExport>();

        // The self-billed invoices (TD17) of the commissions of the foreign portals (E17/F01/US03/T01), behind Features:FiscalDocumentIssuance, with the section SelfBilling (the legal
        // entities of the portals, the VAT integrated, the deadline: ⚖ defaults to be confirmed by an accountant). Not validated at startup: a portal entry that is not usable is
        // ignored and its commissions are listed as «portal not configured», an invalid VAT rate makes the generation refuse (422 self_billing_not_configured).
        services.AddOptions<SelfBillingOptions>().Bind(configuration.GetSection(SelfBillingOptions.SectionName));
        services.AddScoped<ISelfBillingService, SelfBillingService>();

        // The checks of the limit of units of an owner and of the company tenant in the property of a private owner (E18/F01/US03/T01, NF-117, ⚖ I7 and I8): the withholding
        // facts read them, the readiness of the owners and the statement versions carry their warnings, and the consumer of InvoiceDataSaved and BookingConfirmed raises the «Da fare» of a company
        // tenant. The warnings and the «Da fare» are behind Features:TaxReportingExports; run by the outbox dispatcher, the consumer can never fail the save of the invoice data.
        services.AddScoped<IOwnerTaxChecks, OwnerTaxChecks>();
        services.AddScoped<IEventHandler<DomainEvent>, CompanyTenantDecisionHandler>();

        return services;
    }
}
