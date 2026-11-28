using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The compliance alerts and the monthly summary, the approval queue, the CIN deadline, the tourist tax, the GDPR data rights and retention, and the
/// drafts of the wizards.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class ComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenComplianceServices(this IServiceCollection services)
    {
        // The alerts about a property that are not tied to one stay (E03/F02/US02/T01, NF-52, CHK-12): the ISTAT and tourist tax periods and the
        // connection to the channel manager, run by the same hourly job after the stay alerts, behind Features:ComplianceTrafficLight. Who is told is
        // a port (the audience of the preferences of NF-65, narrowed to who reaches the property); the tourist tax deadline day has no source yet (open
        // point), so that alert stays silent; the follow-up opens the To do of a stage and raises the popup of a legal deadline within 24 hours.
        services.AddScoped<IComplianceAlertRecipientResolver, ComplianceAlertRecipientResolver>();
        services.AddScoped<ITouristTaxDeadlineSource, NoTouristTaxDeadlineSource>();
        services.AddScoped<IComplianceAlertFollowUp, ComplianceAlertFollowUp>();
        services.AddScoped<IComplianceAlertService, ComplianceAlertService>();
        // The monthly compliance summary email of every active short-rent org (E03/F02/US02/T02, NF-52): the previous month through the coverage report of the
        // archive, once per org and month (ComplianceMonthlySummaryLogs), to the address of the org and its administrators through the recipient port.
        services.AddScoped<IComplianceMonthlySummaryService, ComplianceMonthlySummaryService>();
        services.AddScoped<ComplianceMonthlySummaryJob>();
        // Approval queue for items requiring human decision before taking effect (NF-06)
        services.AddScoped<IApprovalQueueService, ApprovalQueueService>();
        // CIN deadline (CO-20): optional date, validated at startup; daily host alert.
        services.AddOptions<Casazen.Core.Options.CinOptions>()
            .BindConfiguration(Casazen.Core.Options.CinOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.CinOptions>, Casazen.Core.Options.CinOptionsValidator>();
        services.AddSingleton<Casazen.Core.Regulatory.CinDeadlineCalendar>();
        services.AddScoped<ICinDeadlineAlertService, CinDeadlineAlertService>();
        services.AddScoped<ITouristTaxService, TouristTaxService>();
        services.AddScoped<ITouristTaxQuoteService, TouristTaxQuoteService>();
        // NF-46, ADR-037: the tourist tax of a stay guest by guest, the exemptions, the collector and the rate an org declares.
        services.AddScoped<IStayTouristTaxService, StayTouristTaxService>();
        services.AddScoped<IGdprService, GdprService>();
        // CO-15: guest data rights and retention per category; no period has a default.
        services.AddScoped<GuestDataEraser>();
        services.AddScoped<IGuestDataRetentionService, GuestDataRetentionService>();
        services.AddScoped<ILeasePartyPrivacyService, LeasePartyPrivacyService>();
        // ADR-024: the drafts of the wizards, saved on the server so that a person resumes them on any device (E01/F02/US01). Their personal
        // half is an encrypted column (EncryptedColumns); the nightly retention (gdpr-data-retention) anonymizes the drafts past their 30 days.
        services.AddScoped<IWizardDraftService, WizardDraftService>();
        services.AddScoped<IWizardDraftRetentionService, WizardDraftRetentionService>();

        return services;
    }
}
