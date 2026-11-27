using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The supplier marketplace: the supplier service, its catalog, agenda, showcase content and media, the public showcase read side, the host view of
/// the suppliers, the admin service and the host invites.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class SupplierServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierServices(this IServiceCollection services)
    {
        services.AddScoped<ISupplierService, Casazen.Infrastructure.Services.SupplierService>();
        // The supplier's catalog of services with prices (SP-02): rows keyed by the supplier org, not tenant-filtered.
        services.AddScoped<ISupplierServiceCatalogService, SupplierServiceCatalogService>();
        // The supplier's agenda (SP-03): hours, time off, blocks, rules, calendar and the input of the slot planner. Rows keyed
        // by the supplier org, not tenant-filtered; every write under the SupplierCalendarSync lock.
        services.AddScoped<ISupplierAgendaService, SupplierAgendaService>();
        // The content of the supplier's showcase (SP-13a): the draft, the published version, and the changes between them. Rows keyed
        // by the supplier org, not tenant-filtered; every change under the SupplierShowcaseContent lock.
        services.AddScoped<ISupplierShowcaseContentService, SupplierShowcaseContentService>();
        // The AI help of the showcase editor: drafts of the sentence, the introduction, "chi siamo" and the tips, written from what is true of the
        // supplier and checked against the rules of the static starter. Reads the supplier's own data, calls the AI provider, writes nothing.
        services.AddScoped<IShowcaseTextSuggestionService, ShowcaseTextSuggestionService>();
        // The images of the supplier's showcase (SP-13b): the logo, the cover and the photos of the jobs it uploads from the editor, and the daily
        // cleanup of the ones nothing points to. Rows keyed by the supplier org, not tenant-filtered; every change under the content lock.
        services.AddScoped<ISupplierShowcaseMediaService, SupplierShowcaseMediaService>();
        // The anonymous read side of the supplier showcase (SP-09): services, free slots and price estimate, behind the flag
        // SupplierShowcaseBooking. The slot plans are kept 30 seconds per replica (a singleton: not tied to a request).
        services.AddSingleton<PublicSupplierSlotCache>();
        services.AddScoped<IPublicSupplierShowcaseService, PublicSupplierShowcaseService>();
        // What the Portal's agenda reads on top of the planner (SP-03b): the conflicts of the jobs to come with a time to propose
        // instead, and the supplier's own preview of the slots of a service (no cache, no flag, draft and paused services too).
        services.AddScoped<ISupplierConflictService, SupplierConflictService>();
        services.AddScoped<ISupplierSlotPreviewService, SupplierSlotPreviewService>();
        // What the host reads of a supplier to ask it for an intervention: its published services and their free slots (SR-08a).
        services.AddScoped<IHostSupplierCatalogService, HostSupplierCatalogService>();
        // Admin list, suspension and invites of the suppliers (SU-12, A4-29).
        services.AddScoped<ISupplierAdminService, SupplierAdminService>();
        // Invitation of new independent hosts by admins and hosts: limits are configuration (HostInvites), validated at startup.
        services.AddOptions<Casazen.Core.Options.HostInviteOptions>()
            .BindConfiguration(Casazen.Core.Options.HostInviteOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.HostInviteOptions>, Casazen.Core.Options.HostInviteOptionsValidator>();
        services.AddScoped<IHostInviteService, HostInviteService>();
        services.AddScoped<IHostInviteRetentionService, HostInviteRetentionService>();

        return services;
    }
}
