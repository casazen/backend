using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E24/F01/US01/T02 (NF-120, decision O11): the cleaning at check-out. The stay of <see cref="ServiceRequestScenario"/> ends on Monday 12 October 2026 (check-out day, Europe/Rome
/// summer time, so 10:00 is 08:00 UTC); its property is in comune H501 and its supplier covers it. The real <see cref="ServiceRequestService"/> makes the request (every check it has),
/// the alerts go to a recording router, and the clock is the scenario's (Thursday 8 October 2026).
/// </summary>
public class CleaningAutomationTests
{
    private static readonly DateTime CheckOutStart = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOutEnd = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        private Rig(ServiceRequestScenario scenario) => Scenario = scenario;

        public ServiceRequestScenario Scenario { get; }

        public Mock<IAlertRouter> Alerts { get; } = new();

        public CleaningAutomationService Automation { get; private set; } = null!;

        public InternalActivityService Activities { get; private set; } = null!;

        public Guid OrgId => Scenario.HostOrgId;

        public Guid BookingId => Scenario.BookingId;

        public static async Task<Rig> CreateAsync(bool routines = true)
        {
            var scenario = await ServiceRequestScenario.CreateAsync(recordEvents: true);
            scenario.Flags.Set(FeatureFlags.Routines, routines);
            var rig = new Rig(scenario);
            rig.Activities = new InternalActivityService(scenario.Db, NullLogger<InternalActivityService>.Instance, scenario.Clock);
            rig.Automation = new CleaningAutomationService(
                scenario.Db,
                scenario.Service,
                rig.Activities,
                rig.Alerts.Object,
                scenario.Flags,
                scenario.Clock,
                NullLogger<CleaningAutomationService>.Instance);
            return rig;
        }

        public async Task<PropertyCleaningSetup> SupplierSetupAsync(int price = 7500, int? duration = null, TimeOnly? start = null, bool active = true)
        {
            var setup = new PropertyCleaningSetup
            {
                OrgId = OrgId,
                PropertyId = Scenario.PropertyId,
                CleanerKind = PropertyCleanerKind.Supplier,
                SupplierOrgId = Scenario.SupplierOrgId,
                PriceCents = price,
                DurationMinutes = duration,
                StartTime = start,
                Active = active,
            };
            Scenario.Db.PropertyCleaningSetups.Add(setup);
            await Scenario.Db.SaveChangesAsync();
            Scenario.Db.ChangeTracker.Clear();
            return setup;
        }

        /// <summary>A staff setup for the host itself, an owner of its org who reaches every property (<paramref name="staffUserId"/> otherwise: a user the test adds).</summary>
        public async Task<PropertyCleaningSetup> StaffSetupAsync(string? staffUserId = null, int? price = 4000, bool memberOfTheOrg = true)
        {
            staffUserId ??= ServiceRequestScenario.HostUserId;
            if (memberOfTheOrg && !await Scenario.Db.OrgMembers.AnyAsync(m => m.UserId == staffUserId))
                Scenario.Db.OrgMembers.Add(new OrgMember { OrgId = OrgId, UserId = staffUserId, Role = OrgRole.Owner });

            var setup = new PropertyCleaningSetup
            {
                OrgId = OrgId,
                PropertyId = Scenario.PropertyId,
                CleanerKind = PropertyCleanerKind.Staff,
                StaffUserId = staffUserId,
                PriceCents = price,
            };
            Scenario.Db.PropertyCleaningSetups.Add(setup);
            await Scenario.Db.SaveChangesAsync();
            Scenario.Db.ChangeTracker.Clear();
            return setup;
        }

        /// <summary>The automation over other services than the scenario's real ones (a service that fails with something that is not a rule); everything else is the rig's.</summary>
        public CleaningAutomationService AutomationWith(IServiceRequestService requests, IInternalActivityService? staffWork = null) =>
            new(Scenario.Db, requests, staffWork ?? Activities, Alerts.Object, Scenario.Flags, Scenario.Clock, NullLogger<CleaningAutomationService>.Instance);

        public async Task ChangeStayAsync(Action<Booking> change)
        {
            Scenario.Db.ChangeTracker.Clear();
            change(await Scenario.Db.Bookings.SingleAsync(b => b.Id == BookingId));
            await Scenario.Db.SaveChangesAsync();
            Scenario.Db.ChangeTracker.Clear();
        }

        public async Task ChangePropertyAsync(Action<Property> change)
        {
            Scenario.Db.ChangeTracker.Clear();
            change(await Scenario.Db.Properties.SingleAsync(p => p.Id == Scenario.PropertyId));
            await Scenario.Db.SaveChangesAsync();
            Scenario.Db.ChangeTracker.Clear();
        }

        /// <summary>Points the key of the stay at another request, as a row written by hand could.</summary>
        public async Task PointKeyAtAsync(Guid serviceRequestId)
        {
            Scenario.Db.ChangeTracker.Clear();
            (await Scenario.Db.StayCleanings.SingleAsync(c => c.BookingId == BookingId)).ServiceRequestId = serviceRequestId;
            await Scenario.Db.SaveChangesAsync();
            Scenario.Db.ChangeTracker.Clear();
        }

        /// <summary>
        /// SP-BKP: a backup supplier took the job in place of the primary the key points at, as <c>CloseGroupAfterTake</c> leaves a group: the primary is cancelled for no response and
        /// replaced, the backup (another supplier, same stay, same time) is the request the host follows. Returns the backup.
        /// </summary>
        public async Task<ServiceRequest> PromoteBackupAsync(Guid primaryId)
        {
            var groupId = Guid.NewGuid();
            await Scenario.ChangeRequestAsync(
                primaryId,
                r =>
                {
                    r.GroupId = groupId;
                    r.GroupRole = ServiceRequestGroupRole.ReplacedPrimary;
                    r.Status = ServiceRequestStatus.Annullato;
                    r.CancelledBy = ServiceRequestActorParty.System;
                    r.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
                });
            var primary = await Scenario.ReadAsync(primaryId);
            var backupSupplierOrgId = await Scenario.AddOtherSupplierAsync();
            return await Scenario.SeedAsync(
                ServiceRequestStatus.PresoInCarico,
                r =>
                {
                    r.GroupId = groupId;
                    r.GroupRole = ServiceRequestGroupRole.PromotedBackup;
                    r.ScheduledStartUtc = primary.ScheduledStartUtc;
                    r.ScheduledEndUtc = primary.ScheduledEndUtc;
                    r.EstimatedAmountCents = primary.EstimatedAmountCents;
                },
                backupSupplierOrgId);
        }

        public async Task<List<ServiceRequest>> RequestsAsync() =>
            await Scenario.Db.ServiceRequests.AsNoTracking().Where(r => r.BookingId == BookingId).ToListAsync();

        public async Task<List<InternalActivity>> StaffWorkAsync() =>
            await Scenario.Db.InternalActivities.AsNoTracking().Where(a => a.Action == null && a.BookingId == BookingId).ToListAsync();

        public async Task<List<InternalActivity>> TodosAsync(string type) =>
            await Scenario.Db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == BookingId && a.ActivityType == type).ToListAsync();

        public async Task<StayCleaning> KeyAsync() => await Scenario.Db.StayCleanings.AsNoTracking().SingleAsync(c => c.BookingId == BookingId);

        public void VerifyAlerts(Times times) =>
            Alerts.Verify(
                a => a.RouteAlertAsync(
                    AlertType.BookingNeedsReview,
                    BookingId,
                    PopupAlert.BookingEntityType,
                    ServiceRequestScenario.HostUserId,
                    AlertUrgency.High,
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()),
                times);

        public void Dispose() => Scenario.Dispose();
    }

    // ─── Proofs: the rules of the brief that cost money or permission if wrong ──────────────────────────────────────────────────────

    /// <summary>
    /// Proof (money): a stay gets exactly one automatic cleaning, whatever the number of triggers (the check-out time, the departure the host registered, a second routine, an event
    /// delivered again): one request, at the agreed price, at the check-out day and time of the property, and one key; the second call finds the first and creates nothing.
    /// </summary>
    [Fact]
    public async Task CleaningAutomation_OncePerStay_Test()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync(price: 7500);

        var first = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var again = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var third = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(CleaningAutomationOutcome.Created, first.Outcome);
        Assert.All([again, third], result => Assert.Equal(CleaningAutomationOutcome.AlreadyCreated, result.Outcome));
        Assert.All([again, third], result => Assert.Equal(first.ServiceRequestId, result.ServiceRequestId));
        var request = Assert.Single(await rig.RequestsAsync());
        Assert.Equal(first.ServiceRequestId, request.Id);
        Assert.Equal((ServiceCategories.Cleaning, ServiceRequestStatus.Richiesto, ServiceRequestRentalContext.ShortRent), (request.Category, request.Status, request.RentalContext));
        Assert.Equal(7500, request.EstimatedAmountCents);
        Assert.Null(request.QuotedAmountCents);
        Assert.Equal((rig.OrgId, rig.Scenario.PropertyId, rig.Scenario.SupplierOrgId), (request.OrgId, request.PropertyId, request.SupplierOrgId));
        Assert.Equal((CheckOutStart, CheckOutEnd), (request.ScheduledStartUtc, request.ScheduledEndUtc));
        Assert.Equal("Pulizia automatica al check-out. Prezzo concordato.", request.Notes);
        var key = await rig.KeyAsync();
        Assert.Equal((rig.OrgId, rig.Scenario.PropertyId, PropertyCleanerKind.Supplier, request.Id, CheckOutStart), (key.OrgId, key.PropertyId, key.CleanerKind, key.ServiceRequestId, key.ScheduledStartUtc));
        Assert.Null(key.InternalActivityId);
        // The supplier is told as of any new request, once.
        Assert.Single(rig.Scenario.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestCreated);
    }

    /// <summary>
    /// Proof (money): when the booking is cancelled the automatic cleaning is cancelled with it, with the reason «prenotazione annullata», while the work has not started (a request not
    /// taken or taken, a staff activity not started); a cleaning that is in progress or done keeps its status, and so does one the host cancelled already.
    /// </summary>
    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, false)]
    [InlineData(ServiceRequestStatus.Completato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public async Task CleaningAutomation_CancelledBookingCancelsNotStarted_Test(ServiceRequestStatus status, bool cancelled)
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.Scenario.ChangeRequestAsync(created.ServiceRequestId!.Value, r => r.Status = status);
        await rig.ChangeStayAsync(b => b.Status = BookingStatus.Cancelled);

        var result = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        var request = await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value);
        Assert.Equal(cancelled ? CleaningAutomationOutcome.Cancelled : CleaningAutomationOutcome.Unchanged, result.Outcome);
        if (cancelled)
        {
            Assert.Equal((ServiceRequestStatus.Annullato, ServiceRequestActorParty.Host, "Prenotazione annullata"), (request.Status, request.CancelledBy, request.CancellationReason));
            Assert.Equal(1, rig.Scenario.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier));
        }
        else
        {
            Assert.Equal(status, request.Status);
            Assert.Null(request.CancellationReason);
        }
    }

    [Theory]
    [InlineData(InternalActivity.StatusPending, true)]
    [InlineData(InternalActivity.StatusInProgress, false)]
    [InlineData(InternalActivity.StatusCompleted, false)]
    public async Task CleaningAutomation_CancelledBookingCancelsAStaffActivityNotStarted_Test(string status, bool cancelled)
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync();
        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var activity = Assert.Single(await rig.StaffWorkAsync());
        await rig.Activities.UpdateStatusAsync(activity.Id, status);
        await rig.ChangeStayAsync(b => b.Status = BookingStatus.Cancelled);

        var result = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        var after = Assert.Single(await rig.StaffWorkAsync());
        Assert.Equal(cancelled ? CleaningAutomationOutcome.Cancelled : CleaningAutomationOutcome.Unchanged, result.Outcome);
        Assert.Equal(cancelled ? InternalActivity.StatusCancelled : status, after.Status);
        // A cancelled activity is never completed: no cost is read from it.
        if (cancelled)
            Assert.Null(after.CompletedAt);
    }

    /// <summary>
    /// Proof (money): the cancellation reads the stay as it is now. The event says the stay does not happen, but events come late and out of order: a cancellation that was undone since (the
    /// stay is due again: confirmed, checked in or checked out) cancels nothing, while a stay that is still cancelled, or is marked as a no-show, cancels its cleaning, a request and a
    /// staff activity alike.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, false, false)]
    [InlineData(BookingStatus.CheckedIn, false, false)]
    [InlineData(BookingStatus.CheckedOut, false, false)]
    [InlineData(BookingStatus.Cancelled, false, true)]
    [InlineData(BookingStatus.Confirmed, true, true)]
    public async Task CleaningAutomation_LateCancellationEventCancelsOnlyAStayThatDoesNotHappen_Test(BookingStatus status, bool noShow, bool cancelled)
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.ChangeStayAsync(b =>
        {
            b.Status = status;
            b.NoShowAt = noShow ? new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc) : null;
        });

        var result = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        var request = await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value);
        Assert.Equal(cancelled ? CleaningAutomationOutcome.Cancelled : CleaningAutomationOutcome.Unchanged, result.Outcome);
        Assert.Equal(cancelled ? ServiceRequestStatus.Annullato : ServiceRequestStatus.Richiesto, request.Status);
        Assert.Equal(cancelled ? "Prenotazione annullata" : null, request.CancellationReason);
    }

    /// <summary>The same for the staff activity: a late cancellation of a stay that is due again leaves it alone; a stay that is cancelled or a no-show cancels it.</summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, false, false)]
    [InlineData(BookingStatus.Cancelled, false, true)]
    [InlineData(BookingStatus.Confirmed, true, true)]
    public async Task ALateCancellationEvent_CancelsTheStaffActivityOnlyOfAStayThatDoesNotHappen(BookingStatus status, bool noShow, bool cancelled)
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync();
        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.ChangeStayAsync(b =>
        {
            b.Status = status;
            b.NoShowAt = noShow ? new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc) : null;
        });

        var result = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(cancelled ? CleaningAutomationOutcome.Cancelled : CleaningAutomationOutcome.Unchanged, result.Outcome);
        Assert.Equal(cancelled ? InternalActivity.StatusCancelled : InternalActivity.StatusPending, Assert.Single(await rig.StaffWorkAsync()).Status);
    }

    /// <summary>
    /// Proof (permission): the automation works in the org it is given and nowhere else. A booking of another org is not found (nothing created), the changes of the stay asked in the
    /// name of another org touch nothing, and a key that points at a request of another org never moves or cancels it.
    /// </summary>
    [Fact]
    public async Task CleaningAutomation_OtherOrganizationNeverTouched_Test()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var otherOrg = rig.Scenario.SupplierOrgId;

        var foreign = await rig.Automation.CreateForStayAsync(otherOrg, rig.BookingId);
        Assert.Equal((CleaningAutomationOutcome.Failed, CleaningAutomationCodes.StayNotFound), (foreign.Outcome, foreign.Code));
        Assert.Empty(await rig.RequestsAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);

        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
        var moved = await rig.Automation.FollowStayDatesAsync(otherOrg, rig.BookingId);
        var cancelled = await rig.Automation.FollowStayCancellationAsync(otherOrg, rig.BookingId);
        var request = await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value);

        Assert.All([moved, cancelled], result => Assert.Equal(CleaningAutomationOutcome.Unchanged, result.Outcome));
        Assert.Equal((ServiceRequestStatus.Richiesto, CheckOutStart), (request.Status, request.ScheduledStartUtc));

        // A key that was tampered to point at a request of another org: the org of the request is read from the request, so nothing of it is touched.
        var strangerRequest = await rig.Scenario.SeedAsync(ServiceRequestStatus.Richiesto, r => r.OrgId = otherOrg);
        await rig.PointKeyAtAsync(strangerRequest.Id);
        await rig.ChangeStayAsync(b => b.Status = BookingStatus.Cancelled);
        var viaKey = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);
        Assert.Equal(CleaningAutomationOutcome.Unchanged, viaKey.Outcome);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await rig.Scenario.ReadAsync(strangerRequest.Id)).Status);
    }

    // ─── Which stays, which properties ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APropertyWithNoSetup_OrWithAnInactiveOne_IsNotAutomated()
    {
        using var rig = await Rig.CreateAsync();

        var none = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.SupplierSetupAsync(active: false);
        var inactive = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(CleaningAutomationOutcome.NotConfigured, none.Outcome);
        Assert.Equal(CleaningAutomationOutcome.NotConfigured, inactive.Outcome);
        Assert.Empty(await rig.RequestsAsync());
        Assert.Empty(await rig.StaffWorkAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        // The "To do" of a stay that ended without a cleaning is the event handler's, as it always was: the automation raises nothing here.
        Assert.Empty(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        rig.VerifyAlerts(Times.Never());
    }

    /// <summary>A request, a hold, a cancelled stay and a no-show get no cleaning: the one definition of a stay that is due.</summary>
    [Theory]
    [InlineData(BookingStatus.Pending, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.Confirmed, false)]
    [InlineData(BookingStatus.CheckedIn, false)]
    [InlineData(BookingStatus.CheckedOut, false)]
    public async Task OnlyAStayThatHappens_GetsACleaning(BookingStatus status, bool noShow)
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        await rig.ChangeStayAsync(b =>
        {
            b.Status = status;
            b.NoShowAt = noShow ? new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc) : null;
        });

        var result = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        var happens = !noShow && (status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut);
        Assert.Equal(happens ? CleaningAutomationOutcome.Created : CleaningAutomationOutcome.StayNotActive, result.Outcome);
        Assert.Equal(happens ? 1 : 0, (await rig.RequestsAsync()).Count);
    }

    /// <summary>A cleaning someone asked for is the cleaning of the stay: the automation adds none (and a cancelled or refused one does not count).</summary>
    [Fact]
    public async Task ACleaningAskedForByHand_IsNotDoubled_ButACancelledOneIsNotACleaning()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var byHand = await rig.Scenario.SeedAsync(ServiceRequestStatus.PresoInCarico);

        var planned = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.Scenario.ChangeRequestAsync(byHand.Id, r => r.Status = ServiceRequestStatus.Annullato);
        var afterCancel = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(CleaningAutomationOutcome.AlreadyPlanned, planned.Outcome);
        Assert.Equal(CleaningAutomationOutcome.Created, afterCancel.Outcome);
        Assert.Equal(2, (await rig.RequestsAsync()).Count);
    }

    [Fact]
    public async Task TheRoutinesAreOff_NothingIsReadOrWritten()
    {
        using var rig = await Rig.CreateAsync(routines: false);
        await rig.SupplierSetupAsync();

        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var moved = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);
        var cancelled = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        Assert.All([created, moved, cancelled], result => Assert.Equal(CleaningAutomationOutcome.ModuleOff, result.Outcome));
        Assert.Empty(await rig.RequestsAsync());
    }

    // ─── The supplier and the staff ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The time and the length of the setup win over the defaults; the time of the property is the one that applies when the setup has none.</summary>
    [Fact]
    public async Task TheRequest_TakesTheTimeAndTheLengthOfTheSetup_OrOfTheProperty()
    {
        using var rig = await Rig.CreateAsync();
        await rig.ChangePropertyAsync(p => p.CheckOutBy = new TimeOnly(11, 30));
        await rig.SupplierSetupAsync();

        var result = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        // 11:30 on the clock of Rome in summer time is 09:30 UTC; two hours long.
        var request = await rig.Scenario.ReadAsync(result.ServiceRequestId!.Value);
        Assert.Equal((new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 11, 30, 0, DateTimeKind.Utc)), (request.ScheduledStartUtc, request.ScheduledEndUtc));
    }

    [Fact]
    public async Task TheRequest_OfASetupWithATimeAndALength_StartsAndEndsAsTheSetupSays()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync(duration: 90, start: new TimeOnly(12, 0));

        var result = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        var request = await rig.Scenario.ReadAsync(result.ServiceRequestId!.Value);
        Assert.Equal((new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 11, 30, 0, DateTimeKind.Utc)), (request.ScheduledStartUtc, request.ScheduledEndUtc));
    }

    /// <summary>
    /// A supplier that cannot take the job (suspended since the setup was saved) makes no request and no key: the outcome is a failure with the code of the rule, and the host is told
    /// at once: the «To do» of a stay without a cleaning (once, also when the call is made again) and an alert to the people of the property.
    /// </summary>
    [Fact]
    public async Task ASupplierThatCannotTakeTheJob_FailsWithItsCode_AndTheHostIsTold()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        await rig.Scenario.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var failed = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var again = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.All([failed, again], result => Assert.Equal((CleaningAutomationOutcome.Failed, ServiceRequestErrorCodes.SupplierInactive), (result.Outcome, result.Code)));
        Assert.Empty(await rig.RequestsAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        var todo = Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        Assert.Equal((ActivityAction.CompleteCheckOut, InternalActivity.BookingEntityType, rig.Scenario.PropertyId), (todo.Action, todo.EntityType, todo.PropertyId));
        Assert.Equal("Check-out completato senza una pulizia richiesta", todo.Reason);
        rig.VerifyAlerts(Times.Exactly(2));
    }

    [Fact]
    public async Task ASupplierBusyAtThatTime_FailsWithItsCode_AndLeavesNothingBehind()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        // The supplier took another job that overlaps the cleaning (a request it already took at the same time).
        await rig.Scenario.SeedAsync(ServiceRequestStatus.PresoInCarico, r =>
        {
            r.BookingId = null;
            r.ScheduledStartUtc = CheckOutStart.AddMinutes(30);
            r.ScheduledEndUtc = CheckOutStart.AddMinutes(150);
        });

        var result = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.Failed, ServiceRequestErrorCodes.CommitmentConflict), (result.Outcome, result.Code));
        Assert.Empty(await rig.RequestsAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
    }

    /// <summary>
    /// Proof (money): an unexpected error while the request is made (the service fails with something that is not a rule: a database error, a timeout) is a failure of the cleaning like any
    /// other, and it never waits for the bus to deliver the event again (the engine runs a routine once for each event, so a second delivery would run nothing): the outcome is Failed with
    /// the generic code, what the failed attempt left in the unit of work is forgotten (not saved with the «To do»), the host is told at once with the «To do» of a stay without a cleaning
    /// (once) and an alert, and no key is left behind.
    /// </summary>
    [Fact]
    public async Task CleaningAutomation_UnexpectedErrorFailsTheCleaningAndTheHostIsTold_Test()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var requests = new Mock<IServiceRequestService>(MockBehavior.Strict);
        requests
            .Setup(s => s.CreateAsync(It.IsAny<CreateServiceRequestCommand>(), It.IsAny<CancellationToken>()))
            .Returns<CreateServiceRequestCommand, CancellationToken>((command, _) =>
            {
                // What a save that failed halfway leaves in the unit of work: a request that was added and never written.
                rig.Scenario.Db.ServiceRequests.Add(new ServiceRequest
                {
                    OrgId = command.OrgId,
                    PropertyId = command.PropertyId,
                    BookingId = command.BookingId,
                    SupplierOrgId = command.SupplierOrgId,
                    Category = ServiceCategories.Cleaning,
                    RentalContext = ServiceRequestRentalContext.ShortRent,
                });
                throw new InvalidOperationException("the database does not answer");
            });
        var automation = rig.AutomationWith(requests.Object);

        var failed = await automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var again = await automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.All([failed, again], result => Assert.Equal((CleaningAutomationOutcome.Failed, CleaningAutomationCodes.CreationFailed), (result.Outcome, result.Code)));
        Assert.Empty(await rig.RequestsAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        rig.VerifyAlerts(Times.Exactly(2));
    }

    /// <summary>The same for the staff path: an unexpected error while the activity of the member is made fails the cleaning, tells the host, and leaves nothing.</summary>
    [Fact]
    public async Task AnUnexpectedErrorWhileTheStaffActivityIsMade_FailsTheCleaningToo()
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync();
        var staffWork = new Mock<IInternalActivityService>(MockBehavior.Strict);
        staffWork
            .Setup(a => a.CreateActivityAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database does not answer"));
        staffWork
            .Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CreateTodoRequest, CancellationToken>((request, token) => rig.Activities.CreateAsync(request, token));

        var result = await rig.AutomationWith(rig.Scenario.Service, staffWork.Object).CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.Failed, CleaningAutomationCodes.CreationFailed), (result.Outcome, result.Code));
        Assert.Empty(await rig.StaffWorkAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        rig.VerifyAlerts(Times.Once());
    }

    /// <summary>A call that is cancelled is the cancellation it is, never a failure of the cleaning: it is thrown, and the host is told nothing.</summary>
    [Fact]
    public async Task ACancelledCall_IsNotAFailureOfTheCleaning()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var requests = new Mock<IServiceRequestService>(MockBehavior.Strict);
        requests
            .Setup(s => s.CreateAsync(It.IsAny<CreateServiceRequestCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.AutomationWith(requests.Object).CreateForStayAsync(rig.OrgId, rig.BookingId));

        Assert.Empty(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        rig.VerifyAlerts(Times.Never());
    }

    /// <summary>The staff path: an activity of type Cleaning for the member, at the same time, with its cost as the rate and the recharge to the owner, and the key.</summary>
    [Fact]
    public async Task TheStaffMember_GetsAnActivityWithItsCostAndTheRecharge()
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync(price: 4050);

        var first = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var again = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(CleaningAutomationOutcome.Created, first.Outcome);
        Assert.Equal((CleaningAutomationOutcome.AlreadyCreated, first.InternalActivityId), (again.Outcome, again.InternalActivityId));
        var activity = Assert.Single(await rig.StaffWorkAsync());
        Assert.Equal((InternalActivity.CleaningActivityType, ServiceRequestScenario.HostUserId, InternalActivity.StatusPending), (activity.ActivityType, activity.StaffUserId, activity.Status));
        Assert.Equal((rig.OrgId, rig.Scenario.PropertyId, CheckOutStart), (activity.OrgId, activity.PropertyId, activity.ScheduledAt));
        Assert.Equal((40.50m, true), (activity.InternalRate, activity.RechargeToOwner));
        Assert.Equal("Pulizia automatica al check-out.", activity.Notes);
        Assert.Null(activity.CreatedByUserId);
        var key = await rig.KeyAsync();
        Assert.Equal((PropertyCleanerKind.Staff, activity.Id, CheckOutStart), (key.CleanerKind, key.InternalActivityId, key.ScheduledStartUtc));
        Assert.Null(key.ServiceRequestId);
        Assert.Empty(await rig.RequestsAsync());
    }

    /// <summary>A member of the staff whose setup has no price has an activity with no rate, still to be recharged to the owner (the cost entered later is recharged too).</summary>
    [Fact]
    public async Task TheStaffMember_WithNoPrice_GetsNoRateAndStillTheRecharge()
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync(price: null);

        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        var activity = Assert.Single(await rig.StaffWorkAsync());
        Assert.Null(activity.InternalRate);
        Assert.True(activity.RechargeToOwner);
    }

    /// <summary>A member deactivated since the setup was saved gets no work: the automation fails, the host is told, and nothing is created.</summary>
    [Fact]
    public async Task AStaffMemberWhoIsNotActiveAnyMore_GetsNoWork()
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync();
        rig.Scenario.Db.ChangeTracker.Clear();
        (await rig.Scenario.Db.OrgMembers.SingleAsync()).Status = OrgMemberStatus.Deactivated;
        await rig.Scenario.Db.SaveChangesAsync();

        var result = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.Failed, CleaningAutomationCodes.StaffUnavailable), (result.Outcome, result.Code));
        Assert.Empty(await rig.StaffWorkAsync());
        Assert.Empty(rig.Scenario.Db.StayCleanings);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
    }

    /// <summary>The «To do» of a stay that ended without a cleaning, raised before the cleaning was made (a departure registered before the check-out time), is closed by the system.</summary>
    [Fact]
    public async Task TheToDoOfAStayWithoutACleaning_IsClosedWhenTheCleaningIsMade()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        await rig.Activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.CleaningDue,
                Action = ActivityAction.CompleteCheckOut,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = rig.BookingId,
                Urgency = ActivityUrgency.Low,
                PropertyId = rig.Scenario.PropertyId,
            },
            CancellationToken.None);

        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);

        var todo = Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDue)));
        Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (todo.Status, todo.ClosedBy));
    }

    // ─── The changes of the stay ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dates of the stay change and nobody answered the request: it moves to the new check-out day, with the same length, and the key moves with it.</summary>
    [Fact]
    public async Task ARequestNobodyAnswered_MovesWithTheCheckOutDay()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);
        var again = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.Moved, created.ServiceRequestId), (result.Outcome, result.ServiceRequestId));
        Assert.Equal(CleaningAutomationOutcome.Unchanged, again.Outcome);
        var request = await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value);
        Assert.Equal((new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), ServiceRequestStatus.Richiesto), (request.ScheduledStartUtc, request.ScheduledEndUtc, request.Status));
        Assert.Equal(new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), (await rig.KeyAsync()).ScheduledStartUtc);
        Assert.Empty(await rig.TodosAsync(nameof(ActivityType.CleaningDateChanged)));
    }

    /// <summary>A change of the stay that does not move the check-out day moves nothing; a stay with no automatic cleaning has nothing to follow.</summary>
    [Fact]
    public async Task AChangeThatLeavesTheCheckOutDay_MovesNothing()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var withoutKey = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);
        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.ChangeStayAsync(b => b.NumberOfGuests = 3);

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        Assert.Equal(CleaningAutomationOutcome.Unchanged, withoutKey.Outcome);
        Assert.Equal(CleaningAutomationOutcome.Unchanged, result.Outcome);
        Assert.Equal(CheckOutStart, Assert.Single(await rig.RequestsAsync()).ScheduledStartUtc);
    }

    /// <summary>
    /// The supplier took the job (or has an offer waiting, or the request has a backup): it stays on the old day, the host is asked to decide with a «To do» (one while it is open)
    /// and an alert, and the cleaning keeps the start it was planned for.
    /// </summary>
    [Theory]
    [InlineData("taken")]
    [InlineData("proposed")]
    [InlineData("quote")]
    [InlineData("group")]
    public async Task ARequestWithSomethingAgreed_IsLeftToTheHost(string state)
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.Scenario.ChangeRequestAsync(created.ServiceRequestId!.Value, r =>
        {
            switch (state)
            {
                case "taken":
                    r.Status = ServiceRequestStatus.PresoInCarico;
                    break;
                case "proposed":
                    r.ProposedStartUtc = CheckOutStart.AddHours(2);
                    r.ProposedEndUtc = CheckOutStart.AddHours(4);
                    r.ProposedAt = CheckOutStart.AddDays(-1);
                    break;
                case "quote":
                    r.QuoteSentAt = CheckOutStart.AddDays(-1);
                    r.QuoteLinesJson = """[{"label":"Pulizia","amountCents":8000}]""";
                    r.QuoteTotalCents = 8000;
                    break;
                default:
                    r.GroupId = Guid.NewGuid();
                    r.GroupRole = ServiceRequestGroupRole.Primary;
                    break;
            }
        });
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);
        var again = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        Assert.All([result, again], r => Assert.Equal(CleaningAutomationOutcome.HostToDecide, r.Outcome));
        var request = await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value);
        Assert.Equal(CheckOutStart, request.ScheduledStartUtc);
        Assert.Equal(CheckOutStart, (await rig.KeyAsync()).ScheduledStartUtc);
        var todo = Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDateChanged)));
        Assert.Equal((ActivityAction.ReviewBooking, ActivityUrgency.High, CheckOutStart), (todo.Action, todo.Urgency, todo.Deadline));
        Assert.Equal("Le date del soggiorno sono cambiate: la pulizia è già stata presa in carico, decidi se spostarla o annullarla", todo.Reason);
        rig.VerifyAlerts(Times.Exactly(2));
    }

    /// <summary>A supplier busy at the new time: the request cannot be moved, so the host decides instead of the change failing.</summary>
    [Fact]
    public async Task ARequestThatCannotBeMoved_IsLeftToTheHost()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        await rig.Scenario.SeedAsync(ServiceRequestStatus.PresoInCarico, r =>
        {
            r.BookingId = null;
            r.ScheduledStartUtc = new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc);
            r.ScheduledEndUtc = new DateTime(2026, 10, 14, 11, 0, 0, DateTimeKind.Utc);
        });
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.HostToDecide, ServiceRequestErrorCodes.CommitmentConflict), (result.Outcome, result.Code));
        Assert.Equal(CheckOutStart, (await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value)).ScheduledStartUtc);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDateChanged)));
    }

    /// <summary>
    /// SP-BKP, proof (money): a backup took the job in place of the primary the key points at (the primary is cancelled and replaced, the backup is the request the host follows). The
    /// cancellation of the stay cancels the backup's job, with the reason of a cancelled stay and the notice to its supplier, and never touches the primary that was replaced.
    /// </summary>
    [Fact]
    public async Task CleaningAutomation_CancelledBookingCancelsThePromotedBackup_Test()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var primaryId = created.ServiceRequestId!.Value;
        var backup = await rig.PromoteBackupAsync(primaryId);
        await rig.ChangeStayAsync(b => b.Status = BookingStatus.Cancelled);

        var result = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.Cancelled, backup.Id), (result.Outcome, result.ServiceRequestId));
        var cancelled = await rig.Scenario.ReadAsync(backup.Id);
        Assert.Equal((ServiceRequestStatus.Annullato, ServiceRequestActorParty.Host, "Prenotazione annullata"), (cancelled.Status, cancelled.CancelledBy, cancelled.CancellationReason));
        var replaced = await rig.Scenario.ReadAsync(primaryId);
        Assert.Equal((ServiceRequestStatus.Annullato, ServiceRequestActorParty.System, ServiceRequestCancellationReasons.NoResponse), (replaced.Status, replaced.CancelledBy, replaced.CancellationReason));
        Assert.Equal(1, rig.Scenario.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier));
    }

    /// <summary>
    /// SP-BKP: the dates of the stay change after a backup took the job: the change acts on the request the host follows (the backup's job, which the supplier took: the host decides with a
    /// «To do» and an alert), not on the primary that was replaced, which is left as it is.
    /// </summary>
    [Fact]
    public async Task APromotedBackup_IsTheJobTheChangeOfDatesLeavesToTheHost()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var created = await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var backup = await rig.PromoteBackupAsync(created.ServiceRequestId!.Value);
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        Assert.Equal((CleaningAutomationOutcome.HostToDecide, backup.Id), (result.Outcome, result.ServiceRequestId));
        Assert.Equal(CheckOutStart, (await rig.Scenario.ReadAsync(backup.Id)).ScheduledStartUtc);
        Assert.Equal(ServiceRequestStatus.Annullato, (await rig.Scenario.ReadAsync(created.ServiceRequestId!.Value)).Status);
        Assert.Single(await rig.TodosAsync(nameof(ActivityType.CleaningDateChanged)));
        rig.VerifyAlerts(Times.Once());
    }

    /// <summary>A staff activity not started moves with the stay; one that started, or is done, is left alone.</summary>
    [Theory]
    [InlineData(InternalActivity.StatusPending, true)]
    [InlineData(InternalActivity.StatusInProgress, false)]
    [InlineData(InternalActivity.StatusCompleted, false)]
    public async Task AStaffActivity_MovesWithTheStayUnlessItStarted(string status, bool moves)
    {
        using var rig = await Rig.CreateAsync();
        await rig.StaffSetupAsync();
        await rig.Automation.CreateForStayAsync(rig.OrgId, rig.BookingId);
        var activity = Assert.Single(await rig.StaffWorkAsync());
        await rig.Activities.UpdateStatusAsync(activity.Id, status);
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);

        var expected = moves ? new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc) : CheckOutStart;
        Assert.Equal(moves ? CleaningAutomationOutcome.Moved : CleaningAutomationOutcome.Unchanged, result.Outcome);
        Assert.Equal(expected, Assert.Single(await rig.StaffWorkAsync()).ScheduledAt);
        Assert.Equal(moves ? expected : CheckOutStart, (await rig.KeyAsync()).ScheduledStartUtc);
    }

    // ─── The key that tells the automatic cleaning from the one of the host ───────────────────────────────────────────────

    /// <summary>A cleaning the host asked for by hand is not followed: only the one the automation made (the key) moves or is cancelled with the stay.</summary>
    [Fact]
    public async Task ACleaningTheHostAskedForByHand_IsNeverMovedNorCancelled()
    {
        using var rig = await Rig.CreateAsync();
        await rig.SupplierSetupAsync();
        var byHand = await rig.Scenario.SeedAsync(ServiceRequestStatus.Richiesto, r =>
        {
            r.ScheduledStartUtc = CheckOutStart;
            r.ScheduledEndUtc = CheckOutEnd;
        });
        await rig.ChangeStayAsync(b => b.CheckOutDate = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var moved = await rig.Automation.FollowStayDatesAsync(rig.OrgId, rig.BookingId);
        var cancelled = await rig.Automation.FollowStayCancellationAsync(rig.OrgId, rig.BookingId);

        Assert.All([moved, cancelled], result => Assert.Equal(CleaningAutomationOutcome.Unchanged, result.Outcome));
        var request = await rig.Scenario.ReadAsync(byHand.Id);
        Assert.Equal((ServiceRequestStatus.Richiesto, CheckOutStart), (request.Status, request.ScheduledStartUtc));
    }
}
