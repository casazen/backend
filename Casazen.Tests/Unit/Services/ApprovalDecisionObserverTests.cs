using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F05/US02/T01: the approval queue tells every <see cref="IApprovalDecisionObserver"/> (the lessons and the trust measure of the assistant) after each approval, rejection and edit, once
/// it is saved, with the kind of decision and who decided; an observer that fails never undoes the decision nor keeps the others from being told.
/// </summary>
public sealed class ApprovalDecisionObserverTests : IDisposable
{
    private readonly ApprovalQueueRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task EveryApprovalRejectionAndEdit_TellsTheObservers_AndAFailingObserverNeverUndoesTheDecision()
    {
        await _rig.SeedAsync();
        var recording = new RecordingObserver();
        var service = new ApprovalQueueService(
            _rig.Db, _rig.Clock, _rig.Registry(), _rig.Activities(), _rig.Alerts(), NullLogger<ApprovalQueueService>.Instance, [new FailingObserver(), recording]);
        var approved = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var rejected = await _rig.SubmitDraftAsync(service, _rig.PropertyA, text: "Un altro testo del draft");

        await service.EditAsync(_rig.OrgWide, approved.Id, "Testo corretto dall'host", ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, approved.Id, ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, approved.Id, ApprovalQueueRig.Owner);
        await service.RejectAsync(_rig.OrgWide, rejected.Id, ApprovalQueueRig.Owner, "Non va bene");

        // A second approval of an item already approved tells nobody again.
        Assert.Equal(
            [
                (approved.Id, ApprovalDecisionKind.Edited, ApprovalStatus.Pending, ApprovalQueueRig.Owner),
                (approved.Id, ApprovalDecisionKind.Approved, ApprovalStatus.Approved, ApprovalQueueRig.Owner),
                (rejected.Id, ApprovalDecisionKind.Rejected, ApprovalStatus.Rejected, ApprovalQueueRig.Owner),
            ],
            recording.Seen);
        Assert.Equal(["Sent"], recording.ActionStates);

        // The decisions stand, whatever the failing observer did.
        Assert.Equal(ApprovalStatus.Approved, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == approved.Id)).Status);
        Assert.Equal(ApprovalStatus.Rejected, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == rejected.Id)).Status);
    }

    private sealed class RecordingObserver : IApprovalDecisionObserver
    {
        public List<(Guid ItemId, ApprovalDecisionKind Kind, ApprovalStatus Status, string? UserId)> Seen { get; } = [];

        public List<string> ActionStates { get; } = [];

        public Task OnDecidedAsync(ApprovalDecision decision, CancellationToken ct)
        {
            Seen.Add((decision.Item.Id, decision.Kind, decision.Item.Status, decision.UserId));
            if (decision.ActionState is { } state)
                ActionStates.Add(state);

            return Task.CompletedTask;
        }
    }

    private sealed class FailingObserver : IApprovalDecisionObserver
    {
        public Task OnDecidedAsync(ApprovalDecision decision, CancellationToken ct) => throw new InvalidOperationException("The observer failed.");
    }
}
