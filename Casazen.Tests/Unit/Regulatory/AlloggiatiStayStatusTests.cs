using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Octorate;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// E20/F01/US01/T02 (NF-43, NF-52, CHK-12): the Alloggiati status of ONE stay knows the sender and where its guests are on Octorate, by the same rules as
/// the cockpit. A stay whose guests are all on Octorate reads «entrusted» (yellow), never «to send manually» and never green without an archived receipt;
/// a guest that could not be written is a failure to resolve, with its error code, and can be retried; another org's stay is not found.
/// </summary>
public class AlloggiatiStayStatusTests
{
    private static ChannelManagerGuestFact Link(
        int position,
        OctorateSyncStatus status = OctorateSyncStatus.Sent,
        bool draft = false,
        string? error = null,
        string? fields = null) =>
        new(position, status, draft, error, fields, Attempts: 1, LastAttemptAt: new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc));

    // ─── The rules ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGuestsAreEntrustedOnlyWhenEveryRegisteredGuestIsOnOctorateForGood()
    {
        Assert.Equal(ChannelManagerWriteState.Entrusted, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], [Link(0), Link(1)]));
        // A link of a position that is no longer a guest is not looked at.
        Assert.Equal(ChannelManagerWriteState.Entrusted, AlloggiatiStatusRules.ChannelManagerStateOf([0], [Link(0), Link(3, OctorateSyncStatus.Failed)]));

        // A draft, a pending write and a missing link are not entrusted.
        Assert.Equal(ChannelManagerWriteState.InProgress, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], [Link(0), Link(1, draft: true)]));
        Assert.Equal(ChannelManagerWriteState.InProgress, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], [Link(0), Link(1, OctorateSyncStatus.Pending)]));
        Assert.Equal(ChannelManagerWriteState.InProgress, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], [Link(0)]));
        Assert.Equal(ChannelManagerWriteState.NothingWritten, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], []));
        Assert.Equal(ChannelManagerWriteState.NothingWritten, AlloggiatiStatusRules.ChannelManagerStateOf([], [Link(0)]));

        // A guest that could not be written makes the stay a failure, whatever the others are.
        Assert.Equal(ChannelManagerWriteState.Failed, AlloggiatiStatusRules.ChannelManagerStateOf([0, 1], [Link(0), Link(1, OctorateSyncStatus.Failed, error: "octorate_server")]));
    }

    [Fact]
    public void TheChannelManagerChangesOnlyAStatusThatIsStillToSend()
    {
        Assert.Equal(
            AlloggiatiWebStatus.EntrustedToChannelManager,
            AlloggiatiStatusRules.WithChannelManager(AlloggiatiWebStatus.DaInviareManualmente, ChannelManagerWriteState.Entrusted));
        Assert.Equal(
            AlloggiatiWebStatus.EntrustedToChannelManager,
            AlloggiatiStatusRules.WithChannelManager(AlloggiatiWebStatus.DaInviare, ChannelManagerWriteState.Entrusted));
        Assert.Equal(
            AlloggiatiWebStatus.ChannelManagerWriteFailed,
            AlloggiatiStatusRules.WithChannelManager(AlloggiatiWebStatus.DaInviareManualmente, ChannelManagerWriteState.Failed));

        // What the stay already is stays as it was: sent, declared sent, a portal error, a no-show, a transmission in progress.
        foreach (var settled in new[]
                 {
                     AlloggiatiWebStatus.Inviato, AlloggiatiWebStatus.InviatoManualmente, AlloggiatiWebStatus.Errore, AlloggiatiWebStatus.Rifiutato,
                     AlloggiatiWebStatus.NotDue, AlloggiatiWebStatus.Sending, AlloggiatiWebStatus.ToVerify, AlloggiatiWebStatus.SentReceiptPending,
                 })
        {
            Assert.Equal(settled, AlloggiatiStatusRules.WithChannelManager(settled, ChannelManagerWriteState.Entrusted));
            Assert.Equal(settled, AlloggiatiStatusRules.WithChannelManager(settled, ChannelManagerWriteState.Failed));
        }

        // Nothing entrusted, nothing changes: the host still sends.
        foreach (var state in new[] { ChannelManagerWriteState.NotApplicable, ChannelManagerWriteState.NothingWritten, ChannelManagerWriteState.InProgress })
            Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, AlloggiatiStatusRules.WithChannelManager(AlloggiatiWebStatus.DaInviareManualmente, state));

        // The two states are derived: a failure for the cockpit, a sent one for nobody.
        Assert.True(AlloggiatiStatusRules.IsFailure(AlloggiatiWebStatus.ChannelManagerWriteFailed));
        Assert.False(AlloggiatiStatusRules.IsFailure(AlloggiatiWebStatus.EntrustedToChannelManager));
        Assert.False(AlloggiatiStatusRules.IsSent(AlloggiatiWebStatus.EntrustedToChannelManager));
        Assert.True(AlloggiatiStatusRules.IsEntrusted(AlloggiatiWebStatus.EntrustedToChannelManager));
    }

    [Fact]
    public void ARetryIsOfferedForAWriteThatAnotherAttemptCanMend_NeverForAPlaceTheHostMustCorrect()
    {
        Assert.True(AlloggiatiStatusRules.CanRetryChannelManagerWrite([Link(0), Link(1, OctorateSyncStatus.Failed, error: "octorate_server")]));
        Assert.True(AlloggiatiStatusRules.CanRetryChannelManagerWrite([Link(0, OctorateSyncStatus.Pending)]));
        Assert.False(AlloggiatiStatusRules.CanRetryChannelManagerWrite([Link(0), Link(1)]));
        Assert.False(AlloggiatiStatusRules.CanRetryChannelManagerWrite(
            [Link(1, OctorateSyncStatus.Failed, error: OctoratePlaceRules.PlaceNotRecognizedCode, fields: "birthComune")]));
    }

    // ─── The proofs ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data, CHK-12): a stay entrusted to the channel manager is yellow, in the per-stay status and in the cockpit alike, and never green without an
    /// archived receipt, whatever the guests say; with its deadline gone it is red, and only the archived receipt turns it green.
    /// </summary>
    [Fact]
    public async Task StayStatus_EntrustedNeverGreenWithoutReceipt_Test()
    {
        var world = await ComplianceStatusServiceTests.World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (entrusted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        var (overdue, _) = await world.AddStayAsync(world.Granted, checkInOffset: -4, nights: 3, guests: 1);
        await world.EntrustAsync(entrusted, positions: [0, 1]);
        await world.EntrustAsync(overdue, positions: [0]);

        await using var db = world.Db();
        var service = world.StatusService(db);
        var stay = await service.GetStayStatusAsync(world.OrgWide, entrusted.Id, CancellationToken.None);
        var late = await service.GetStayStatusAsync(world.OrgWide, overdue.Id, CancellationToken.None);

        // Entrusted: never «to send manually», yellow, no receipt, and the cockpit reads the same.
        Assert.Equal(AlloggiatiWebStatus.EntrustedToChannelManager, stay!.Status);
        Assert.Equal(ChannelManagerWriteState.Entrusted, stay.ChannelManager);
        Assert.Equal(ComplianceLight.Yellow, stay.Verdict!.Verdict.Light);
        Assert.Equal(ComplianceReasonCodes.EntrustedToChannelManager, stay.Verdict.Verdict.Reason);
        Assert.Null(stay.ReceiptProof);
        Assert.Null(stay.Verdict.Verdict.ProofReference);
        Assert.Empty(stay.Actions);
        var cockpit = await service.EvaluateAsync(world.OrgWide, CancellationToken.None);
        var item = cockpit.Items.Single(i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == entrusted.Id);
        Assert.Equal((stay.Verdict.Verdict.Light, stay.Verdict.Verdict.Reason, stay.Verdict.Action), (item.Verdict.Light, item.Verdict.Reason, item.Action));

        // The deadline passed with no receipt: red, never green.
        Assert.Equal(ComplianceLight.Red, late!.Verdict!.Verdict.Light);
        Assert.Equal(ComplianceReasonCodes.ReceiptMissing, late.Verdict.Verdict.Reason);

        // The only thing that makes it green is an archived receipt of the arrival day.
        await world.AddProofAsync(world.Granted.Id, ManualProofKind.AlloggiatiReceipt, RomeCalendar.DateInRome(entrusted.CheckInDate));
        await using var after = world.Db();
        var proven = await world.StatusService(after).GetStayStatusAsync(world.OrgWide, entrusted.Id, CancellationToken.None);
        Assert.Equal(ComplianceLight.Green, proven!.Verdict!.Verdict.Light);
        Assert.NotNull(proven.ReceiptProof);
        Assert.Equal(ComplianceEvidenceSources.ManualUpload, proven.ReceiptProof.Source);
        Assert.Equal(AlloggiatiReceiptCoverageDto.From(proven.ReceiptProof), AlloggiatiChannelManagerDto.From(proven).ReceiptCoverage);
        Assert.True(AlloggiatiChannelManagerDto.From(proven).ReceiptCoverage.Archived);
    }

    /// <summary>
    /// Proof (permission, TN-3): the status of a stay of another org, or of one outside the reach of a collaborator «Solo alcuni», is the answer of a stay
    /// that does not exist: <c>null</c> from the service and a 404 from the endpoints, which read and retry nothing for it.
    /// </summary>
    [Fact]
    public async Task StayStatus_OtherOrganizationBooking404_Test()
    {
        var world = await ComplianceStatusServiceTests.World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (foreign, _) = await world.AddStayAsync(world.Foreign, checkInOffset: 0, nights: 3, guests: 1);
        var (hidden, _) = await world.AddStayAsync(world.Hidden, checkInOffset: 0, nights: 3, guests: 1);
        var (granted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 1);

        await using var db = world.Db();
        var service = world.StatusService(db);

        Assert.Null(await service.GetStayStatusAsync(world.OrgWide, foreign.Id, CancellationToken.None));
        Assert.Null(await service.GetStayStatusAsync(world.Restricted, hidden.Id, CancellationToken.None));
        Assert.Null(await service.GetStayStatusAsync(world.OrgWide, Guid.NewGuid(), CancellationToken.None));
        Assert.NotNull(await service.GetStayStatusAsync(world.Restricted, granted.Id, CancellationToken.None));

        // The endpoints: the booking of another org is not found by the tenant filter, so nothing is read and nothing is retried.
        var bookings = new Mock<IBookingService>();
        bookings.Setup(b => b.GetBookingAsync(foreign.Id)).ReturnsAsync((Booking?)null);
        var status = new Mock<IComplianceStatusService>();
        var sync = new Mock<ICheckInOctorateSyncService>();
        var controller = Controller(bookings, status, sync);

        Assert.IsType<NotFoundResult>((await controller.GetStatus(foreign.Id)).Result);
        Assert.IsType<NotFoundResult>((await controller.RetryOctorateWrite(foreign.Id)).Result);
        status.Verify(s => s.GetStayStatusAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        sync.Verify(s => s.RetryGuestsNowAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>AC2: a failed guest write is on the stay with its code, per position, can be retried, and the cockpit shows it as a failure to resolve on the booking.</summary>
    [Fact]
    public async Task AFailedWrite_IsVisibleOnTheStayWithItsCode_CanBeRetried_AndIsAFailureToResolveInTheCockpit()
    {
        var world = await ComplianceStatusServiceTests.World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (stay, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        await world.EntrustAsync(stay, positions: [0]);
        await world.EntrustAsync(stay, positions: [1], status: OctorateSyncStatus.Failed);

        await using var db = world.Db();
        var service = world.StatusService(db);
        var status = await service.GetStayStatusAsync(world.OrgWide, stay.Id, CancellationToken.None);

        Assert.Equal(AlloggiatiWebStatus.ChannelManagerWriteFailed, status!.Status);
        Assert.Equal(ChannelManagerWriteState.Failed, status.ChannelManager);
        Assert.Equal([AlloggiatiStayActions.RetryOctorateWrite], status.Actions);
        Assert.Equal([0, 1], status.Guests.Select(g => g.Position));
        Assert.Equal(OctorateSyncStatus.Failed, status.Guests.Single(g => g.Position == 1).Status);
        Assert.Equal(ComplianceLight.Red, status.Verdict!.Verdict.Light);

        var dto = AlloggiatiStatusDto.From(
            new AlloggiatiStatusInfo(stay.Id, AlloggiatiWebStatus.DaInviareManualmente, null, null, null, DateTime.UtcNow, false, 0, false, true), status);
        Assert.Equal(AlloggiatiWebStatus.ChannelManagerWriteFailed, dto.Status);
        Assert.Equal([AlloggiatiStayActions.RetryOctorateWrite], dto.ChannelManager!.Actions);

        var cockpit = await service.EvaluateAsync(world.OrgWide, CancellationToken.None);
        var item = cockpit.Items.Single(i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == stay.Id);
        Assert.Equal((ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceLight.Red, stay.Id), (item.Action, item.Verdict.Light, item.TargetId));
    }

    [Fact]
    public async Task ForAStayThatTheChannelManagerDoesNotSend_NothingIsEntrustedAndNoActionIsOffered()
    {
        var world = await ComplianceStatusServiceTests.World.SeedAsync();
        var (stay, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        await world.EntrustAsync(stay, positions: [0, 1]);
        await using var db = world.Db();

        var status = await world.StatusService(db).GetStayStatusAsync(world.OrgWide, stay.Id, CancellationToken.None);

        Assert.Equal((ComplianceSenderType.Manual, ChannelManagerWriteState.NotApplicable), (status!.Sender, status.ChannelManager));
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, status.Status);
        Assert.Empty(status.Guests);
        Assert.Empty(status.Actions);
    }

    private static AlloggiatiController Controller(
        Mock<IBookingService> bookings,
        Mock<IComplianceStatusService> status,
        Mock<ICheckInOctorateSyncService> sync) =>
        new(
            Mock.Of<IAlloggiatiWebService>(),
            bookings.Object,
            Mock.Of<IPropertyAuthorizationService>(),
            Mock.Of<IHostResourceLookup>(),
            Mock.Of<IAuthorizationService>(),
            Mock.Of<IStayGuestService>(),
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IOrgContextResolver>(),
            Mock.Of<IHostScopeResolver>(),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<AlloggiatiController>.Instance,
            status.Object,
            sync.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
