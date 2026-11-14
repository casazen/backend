using Casazen.Core.Documents;
using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the fiscal documents of epic E06 are registered (NF-81, NF-82): the invoice data of a booking, the issuer of the
/// documents and what follows from them. Every backend task of the epic adds what it registers here, none in
/// <c>ServiceCollectionExtensions</c>, which most tasks of the plan share; <c>Program.cs</c> calls it once, next to the e-mail.
/// </summary>
public static class FiscalDocumentExtensions
{
    public static IServiceCollection AddCasazenFiscalDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Who an invoice of a booking is made out to, with the precedence of its sources (E06/F02/US01/T02).
        services.AddScoped<IInvoiceDataService, InvoiceDataService>();

        // The switches of the section FiscalDocuments (the answer to D34, the import of the Octorate documents).
        services.AddOptions<FiscalDocumentRulesOptions>().Bind(configuration.GetSection(FiscalDocumentRulesOptions.SectionName));

        // The effective-dated catalog of the tax rules, FiscalDocuments:TaxRules (E11/F01/US01/T02), validated at startup: overlapping ranges, a line type
        // without a rate or an Italian 0% without a nature code stop the application with a clear message. An empty catalog is valid (nothing is issued).
        services.AddOptions<FiscalTaxRulesOptions>().Bind(configuration.GetSection(FiscalTaxRulesOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FiscalTaxRulesOptions>, FiscalTaxRulesOptionsValidator>();
        services.AddSingleton<IFiscalTaxRuleResolver, FiscalTaxRuleResolver>();

        // Who issues the documents of an organization and of each booking (E06/F02/US02/T02).
        services.AddScoped<IDocumentIssuerService, DocumentIssuerService>();

        // The billing data and Octorate (E06/F02/US01/T04), both behind Features:OctorateConnector and Features:OctorateBillingSync: the invoice data of a
        // booking imported from Octorate is pre-filled from its customer record (BookingCreated), and the data saved in CasaZen is written to Octorate where
        // Octorate is the issuer (InvoiceDataSaved). Run by the outbox dispatcher, so neither can fail an import or a host's save.
        services.AddScoped<IEventHandler<DomainEvent>, InvoiceDataFromOctorateHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBillingWriteBackHandler>();

        // The labels of the generated documents, read from SharedResources in the language the caller names (E06/F01/US01/T02).
        services.AddSingleton<IDocumentTexts, ResxDocumentTexts>();

        // The PDF and CSV of an owner statement and the PDF of the F24 data, built from what is stored (E06/F01/US01/T02, NF-25).
        services.AddScoped<IStatementExportService, StatementExportService>();

        // What Stripe kept and credited for a settled card payment, read by the job payment-provider-fees (E06/F04/US01/T02, NF-85).
        services.AddScoped<IPaymentProviderFeeService, PaymentProviderFeeService>();

        // The gap-free numbers of the fiscal documents, per org and series, in the transaction that saves the document (E06/F01/US02/T02, NF-83).
        services.AddScoped<IFiscalNumberingService, FiscalNumberingService>();

        // The FatturaPA XML of a guest document, validated against the official schema embedded in the build (E06/F01/US02/T03, NF-83, NF-87):
        // the schema set is loaded once; without the files nothing is produced (fatturapa_schema_unavailable).
        services.AddSingleton<FatturaPaSchemaProvider>();
        services.AddSingleton<IFatturaPaXmlBuilder, FatturaPaXmlBuilder>();

        // The import of the documents Octorate issued, read-only, behind Features:OctorateConnector and Features:OctorateDocumentImport
        // (E06/F03/US02/T01, NF-84); run by the job octorate-document-import.
        services.AddScoped<IOctorateDocumentImportService, OctorateDocumentImportService>();

        // The fiscal documents CasaZen issues for its direct bookings and the reading of every document (E06/F01/US01/T01, NF-83); the changes are
        // behind Features:FiscalDocumentIssuance.
        services.AddScoped<IFiscalDocumentService, FiscalDocumentService>();

        // The payment ledger of a booking and of a period: card, manual and channel payments with the document of the booking (E06/F04/US01/T03, NF-85).
        services.AddScoped<IBookingPaymentLedgerService, BookingPaymentLedgerService>();

        // The owner accounting of the agencies (E06/F05/US02): the approved rule sets and the effective-dated fiscal profiles of the owners (T01).
        services.AddScoped<IOwnerFiscalProfileService, OwnerFiscalProfileService>();

        // The owner ledger, the statement versions and the month-end close (E06/F05/US02/T02, NF-110).
        services.AddScoped<IOwnerAccountingService, OwnerAccountingService>();

        // The payouts to the owners with their withholding evidence, the annual tax data and the filing states (E06/F05/US02/T03).
        services.AddScoped<IOwnerPayoutService, OwnerPayoutService>();

        return services;
    }
}
