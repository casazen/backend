using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The package of the accountant (summary, ZIP, settings, monthly delivery), the legal documents and the legal texts of the guest check-in.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class AccountantPackageServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenAccountantPackageServices(this IServiceCollection services)
    {
        // NF-101, E15/F01/US01/T01: the agency summary and the ZIP of the accountant (GET /api/accountant-package, behind Features:AccountantPackage), both composed of the
        // services above and of the fiscal documents and the owner accounting; the ZIP is capped like the archive export (DocumentArchive).
        services.AddScoped<IAgencySummaryService, AgencySummaryService>();
        services.AddScoped<IAccountantPackageService, AccountantPackageService>();
        // NF-101, E15/F01/US02/T01: how the package is sent to the accountant (the address, the monthly send, its day and folders): GET and PUT api/accountant-package/settings.
        services.AddScoped<IAccountantPackageSettingsService, AccountantPackageSettingsService>();
        // NF-101, E15/F01/US02/T02: the monthly e-mail of the package to the accountant: the package kept in the private storage, the signed link (AccountantPackageLinks, registered with the e-mail), POST
        // api/accountant-package/send and the public link; the job (accountant-package-monthly) is registered only while Features:AccountantPackage is on.
        services.AddScoped<IAccountantPackageDeliveryService, AccountantPackageDelivery>();
        services.AddScoped<AccountantPackageMonthlyJob>();
        services.AddSingleton<ILegalDocumentService, LegalDocumentService>();
        // E22/F01/US02/T02: the registry of the legal texts of the guest check-in (consent to the biometric verification, notice for Ticino): the files and the manifest under
        // LegalDocuments/checkin, drafts until approved; production serves approved versions only. Read once per process.
        services.AddSingleton<ICheckInLegalTexts, CheckInLegalTexts>();

        return services;
    }
}
