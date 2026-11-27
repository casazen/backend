using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public sealed class NeverAutoSendGate : IAutoSendGate
{
    public Task<AutoSendVerdict> EvaluateAsync(AutoSendContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(AutoSendVerdict.Refused(AutoSendVerdict.NotEnabledCode));
    }
}
