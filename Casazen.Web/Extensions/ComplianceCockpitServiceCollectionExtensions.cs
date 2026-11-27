using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The compliance cockpit: the traffic light, the archive of the proofs and the log of what was sent, the retention jobs, the guest document purge,
/// the wizard, the autopilot and the passage to the Octorate compliance cabinet.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class ComplianceCockpitServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenComplianceCockpitServices(this IServiceCollection services)
    {
        // The compliance traffic light of the cockpit "am I compliant?" and the feed of its red and yellow items into the unified To do
        // (E03/F02/US01/T03, NF-52): the status reads facts and hands them to ComplianceLightRules, the one rule class, which returns green
        // only with an archived proof; the feeder keeps one To do per item and closes it when the item turns green. Both are read-side of the
        // cockpit (ComplianceWizardService.GetSummaryAsync); nothing is sent to an authority or to Octorate.
        services.AddScoped<IComplianceStatusService, ComplianceStatusService>();
        services.AddScoped<IComplianceSendLog, ComplianceSendLog>();
        services.AddScoped<IComplianceArchiveService, ComplianceArchiveService>();
        services.AddScoped<Casazen.Infrastructure.Jobs.ComplianceProofRetentionJob>();
        services.AddSwissComplianceRetention();
        // E20/F02/US01/T02 (NF-121, NF-16, decision O4): the photos of the identity documents and the legacy scan of a stay covered by an archived Alloggiati receipt are deleted by the
        // daily job (guest-document-purge) and by a run queued after each receipt upload; behind Features:PurgeDocumentsAfterReceipt, off by default (the deletion cannot be undone).
        services.AddScoped<IGuestDocumentPurgeService, GuestDocumentPurgeService>();
        services.AddScoped<Casazen.Infrastructure.Jobs.GuestDocumentPurgeJob>();
        services.AddScoped<IComplianceActivityFeeder, ComplianceActivityFeeder>();
        services.AddScoped<IComplianceWizardService, ComplianceWizardService>();
        services.AddScoped<IAutopilotMessageWriter, AutopilotMessageWriter>();
        services.AddScoped<IComplianceAutopilotService, ComplianceAutopilotService>();
        // CHK-15 (E03/F05/US01/T01): the controlled passage of a host to the Octorate Cassetto Compliance. It prepares the allowlisted address
        // and the guide and writes one line of the activity log; it never signs in to Octorate, reads an outcome or changes a compliance state.
        // Addresses come from Octorate:FrontendBaseUrl and Octorate:ComplianceHandoff, nothing is required (the home of the panel is the default).
        services.AddOptions<OctorateComplianceHandoffOptions>()
            .BindConfiguration(OctorateComplianceHandoffOptions.SectionName);
        services.AddScoped<IOctorateComplianceHandoffService, OctorateComplianceHandoffService>();

        return services;
    }
}
