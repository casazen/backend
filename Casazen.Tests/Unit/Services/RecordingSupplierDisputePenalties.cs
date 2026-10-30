using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The sink of the penalty of a lost dispute (SP-15c-c) that remembers what it was told, for the tests of the disputes: the ranking is not connected yet, so
/// nothing else records the penalty. It can be made to fail, to prove that a sink that does never undoes a decision.
/// </summary>
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

    /// <summary>The next call fails with <paramref name="exception"/> (and is not recorded).</summary>
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
