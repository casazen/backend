using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;

namespace Casazen.Web.Extensions;

/// <summary>
/// The copy of the OTA conversations (epic E05, feature F01, ADR-032): the sync service and, with the next task, its triggers (the webhook handler
/// and the reconciliation jobs) and the retention of the copy. An extension class of its own, so this epic never collides with the shared
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. The chat reads are methods of the client E02 already registers, and the
/// mapping of the listings is the service of <c>AddCasazenOctorate</c>. Nothing here is conditional: what is not ready sits behind
/// <c>Features:OctorateConnector</c> where it is used.
/// </summary>
public static class MessageSyncServiceCollectionExtensions
{
    public static IServiceCollection AddMessageSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F01/US01/T02 (NF-62, NF-64): the threads and messages of the channels, copied from the channel manager and kept up to date.
        services.AddScoped<IMessageSyncService, MessageSyncService>();

        return services;
    }
}
