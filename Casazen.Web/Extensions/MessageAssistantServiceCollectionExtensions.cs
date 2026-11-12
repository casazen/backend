using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The AI assistant that answers the guests' messages (epic E05, feature F05, ADR-032, NF-66, NF-67): the one place where its services are registered. An extension class of its
/// own, so this feature never collides with the shared <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once and the other tasks of the feature (the drafts, the lessons,
/// the auto-send gate) add their registrations here. Nothing here is conditional: what is not ready sits behind <c>Features:MessageAssistant</c> where it is used (the controllers, and
/// the handlers of the later tasks), not by being left out of DI.
/// </summary>
public static class MessageAssistantServiceCollectionExtensions
{
    public static IServiceCollection AddMessageAssistant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F05/US01/T02 (NF-66): the rules of the organization with the exceptions of a property (and the version of the instructions every change grows), and the knowledge base
        // of a property: the facts of its listing, the entries of the property and of the organization. Scoped: they hold the database of the request.
        services.AddScoped<IAssistantRulesService, AssistantRulesService>();
        services.AddScoped<IAssistantKnowledgeService, AssistantKnowledgeService>();

        return services;
    }
}
