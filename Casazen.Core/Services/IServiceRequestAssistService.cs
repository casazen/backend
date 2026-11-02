using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// «Descrivi di cosa hai bisogno» in the guided request of a short-rent host (WIZ-AI-REQUEST): the host tells in a few words what the job is and
/// the AI proposes the kind of service, how urgent it is, a sentence for the supplier and who to ask. It only proposes: the wizard fills in its
/// answers with it, and the host reads, changes and sends the request (or does not use it at all: the wizard works without an AI).
/// </summary>
public interface IServiceRequestAssistService
{
    /// <summary>
    /// Whether an AI could answer now: an external provider is in effect (not the stub) and has a key. A cheap read, no call to a provider: the
    /// wizard asks it before it offers the box, so a platform without an AI shows nothing new.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What the AI makes of <paramref name="text"/> for <paramref name="property"/>. Not an error when it cannot be used (no provider, budget used up,
    /// no answer, an answer that is not a service, a job it cannot tell): the result says why (<see cref="ServiceRequestAssist.Reason"/>) and the
    /// host chooses as before. Only the host's own words reach the provider, scrubbed of e-mails, phones, fiscal codes and street addresses: never
    /// the property, its address or a guest. The channel is not the AI's: CasaZen suppliers come first whenever one covers the comune of the
    /// property for the category.
    /// </summary>
    /// <param name="property">The property the request is for (authorized by the caller).</param>
    /// <param name="text">What the host wrote, 8 to 600 characters.</param>
    /// <param name="language"><c>it</c> or <c>en</c>: the language of the sentence for the host (the summary for the supplier is Italian).</param>
    Task<ServiceRequestAssist> AssistAsync(
        Property property,
        string text,
        string language,
        CancellationToken cancellationToken = default);
}
