using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The default <see cref="IAutoSendGate"/> (E05/F05/US02/T01): it never allows a draft to be sent by itself (<see cref="AutoSendVerdict.NotEnabledCode"/>), so every draft of the assistant
/// goes to the approval queue and no AI text reaches a guest unapproved. E05/F05/US03 registers the real gate in its place.
/// </summary>
public sealed class NeverAutoSendGate : IAutoSendGate
{
    public Task<AutoSendVerdict> EvaluateAsync(AutoSendContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(AutoSendVerdict.Refused(AutoSendVerdict.NotEnabledCode));
    }
}
