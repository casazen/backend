using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The hub of messages (epic E05, feature F02, ADR-032, NF-61, NF-64): the one list and the one thread model over the conversations of the channels
/// and the direct ones, and the proxy of the attachments. An extension class of its own, so this feature never collides with the shared
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once and the next tasks of the feature (reply, templates, notifications,
/// assistant) add their registrations here. Nothing here is conditional: the hub reads what the copy of the conversations (<c>AddMessageSync</c>)
/// holds, and answers an empty OTA part while the connector is off.
/// </summary>
public static class MessagesHubServiceCollectionExtensions
{
    public static IServiceCollection AddMessagesHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F02/US01/T01 (NF-61, NF-64): the list, the thread detail, the read mark, the badges and the attachment of the hub.
        services.AddScoped<IMessageHubService, MessageHubService>();

        // The proxy of the attachments (ADR-032-R5): its own client, with no automatic redirect and a connection that goes to public addresses only
        // (the address was read from a channel, so it is untrusted); the 30 seconds and the 20 MB are kept by the fetcher itself, which reads the body.
        // RemoveAllLoggers: the logging handlers every client of the factory gets by default write «Sending HTTP request GET https://host/path» (the query
        // is hidden, the host and the path are not) at Information for each file that is opened, and the address may carry a token or the name of a file:
        // the fetcher logs the kind of failure itself and nothing else.
        services.AddHttpClient<IOtaAttachmentFetcher, OtaAttachmentFetcher>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(OtaAttachmentFetcher.CreateHandler)
            .RemoveAllLoggers();

        return services;
    }
}
