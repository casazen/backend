using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

internal sealed class RecordingSupplierDisputePenalties : ISupplierDisputePenalties
{
    private readonly List<SupplierDisputePenalty> _recorded = [];
    private Exception? _failure;

    public IReadOnlyList<SupplierDisputePenalty> Recorded
    {
        get
        {
            lock (_recorded)
                return _recorded.ToList();
        }
    }

    public void FailNext(Exception exception) => _failure = exception;

    public Task RecordAsync(SupplierDisputePenalty penalty, CancellationToken cancellationToken = default)
    {
        if (_failure is { } failure)
        {
            _failure = null;
            throw failure;
        }

        lock (_recorded)
            _recorded.Add(penalty);

        return Task.CompletedTask;
    }
}
