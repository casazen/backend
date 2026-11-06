using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

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

        // The rules of the documents of each regime, from the section FiscalDocuments. Nothing validates them at startup: a rule that is not complete
        // only makes issuance refuse (document_rule_not_configured), it never stops the application.
        services.AddOptions<FiscalDocumentRulesOptions>().Bind(configuration.GetSection(FiscalDocumentRulesOptions.SectionName));

        // Who issues the documents of an organization and of each booking (E06/F02/US02/T02).
        services.AddScoped<IDocumentIssuerService, DocumentIssuerService>();

        return services;
    }
}
