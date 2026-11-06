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

        return services;
    }
}
