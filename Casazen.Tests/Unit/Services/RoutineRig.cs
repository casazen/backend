using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What the tests of the routine engine share (E01/F04/US01): one in-memory database, the engine built over it with the real condition evaluator and a
/// Hangfire client that records what is scheduled, and the seeding of routines, bookings and guests. The engine is built in one place, so a new
/// dependency of it changes this file and no test.
/// </summary>
internal sealed class RoutineRig
{
    public static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    public static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");

    public static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"routine-rig-{Guid.NewGuid()}";
    private readonly List<(Job Job, IState State)> _scheduled = [];

    private readonly EphemeralDataProtectionProvider _keys = TestDataProtection.Shared;

    public RoutineRig()
    {
        Jobs = new Mock<IBackgroundJobClient>();
        Jobs
            .Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, state) => _scheduled.Add((job, state)))
            .Returns(() => $"job-{_scheduled.Count}");
    }

    public Mock<IBackgroundJobClient> Jobs { get; }

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.Routines);

    public IAiProvider Ai { get; set; } = SelectAllOperations();

    public IScheduledMessageService? ScheduledMessages { get; set; }

    internal static IAiProvider SelectAllOperations()
    {
        var provider = new Mock<IAiProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest request, AiModelTier tier, string _, CancellationToken _) =>
            {
                using var input = JsonDocument.Parse(request.Prompt);
                var indices = input.RootElement.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("index").GetInt32());
                return Task.FromResult(new AiGenerationResult(JsonSerializer.Serialize(indices), 1, 1, tier, false));
            });
        return provider.Object;
    }

    public ICleaningAutomationService? Cleaning { get; set; }

    public IReadOnlyList<(Job Job, IState State)> Scheduled => _scheduled;

    public RecordingEmailQueue Emails { get; } = new();

    public ReviewLinkTokens Tokens => new(_keys);

    public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    public RoutineService Service(AppDbContext db, TimeProvider? clock = null)
    {
        var time = clock ?? new FixedTimeProvider(Now);
        return new RoutineService(
            db,
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time),
            Jobs.Object,
            Flags,
            time,
            NullLogger<RoutineService>.Instance,
            Cleaning,
            Reviews(db, time),
            Ai,
            scheduledMessages: ScheduledMessages);
    }

    public HostReviewService Reviews(AppDbContext db, TimeProvider? clock = null) =>
        new(db, Tokens, Emails, EmailTestHelpers.Links(), NullLogger<HostReviewService>.Instance, clock ?? new FixedTimeProvider(Now));

    public RoutineManagementService Management(AppDbContext db) =>
        new(db, new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance), Flags, NullLogger<RoutineManagementService>.Instance);

    public async Task TriggerAsync(Guid orgId, RoutineEventType eventType, object payload, string? eventId = null, TimeProvider? clock = null)
    {
        await using var db = NewDb();
        await Service(db, clock).TriggerAsync(orgId, eventType, eventId ?? $"event-{Guid.NewGuid()}", payload);
    }

    public async Task ResumeAsync(Guid executionId, TimeProvider? clock = null)
    {
        await using var db = NewDb();
        await Service(db, clock).ResumeAsync(executionId);
    }

    public async Task<Routine> SeedRoutineAsync(
        Guid orgId,
        RoutineEventType eventType,
        string actionsJson,
        string conditionJson = "{}",
        bool testMode = false,
        bool active = true,
        string eventConfig = "{}")
    {
        await using var db = NewDb();
        if (!await db.Orgs.AnyAsync(o => o.Id == orgId))
            db.Orgs.Add(new OrgEntity { Id = orgId, Slug = orgId.ToString("N") });
        var routine = new Routine
        {
            OrgId = orgId,
            Name = $"Routine {Guid.NewGuid():N}"[..20],
            EventType = eventType,
            EventConfig = eventConfig,
            ConditionJson = conditionJson,
            ActionsJson = actionsJson,
            TestMode = testMode,
            Active = active,
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync();
        return routine;
    }

    public async Task<Guest> SeedGuestAsync(
        Guid orgId,
        bool marketingConsent = false,
        bool deleted = false,
        bool erasureRequested = false,
        bool anonymized = false)
    {
        await using var db = NewDb();
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"guest.{Guid.NewGuid():N}@example.com",
            MarketingConsent = marketingConsent,
            IsDeleted = deleted,
            ErasureRequested = erasureRequested,
            DataAnonymizedDate = anonymized ? Now.UtcDateTime : null,
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest;
    }

    public async Task<Booking> SeedBookingAsync(
        Guid orgId,
        BookingSource source = BookingSource.Direct,
        BookingStatus status = BookingStatus.Confirmed,
        Guid? guestId = null,
        int guests = 2,
        DateTime? checkIn = null,
        DateTime? checkOut = null,
        bool withProperty = true)
    {
        await using var db = NewDb();
        var arrival = checkIn ?? new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var property = new Property { OrgId = orgId, OwnerId = "auth0|rig-owner", Name = "Casa", Address = "Via Roma 1", City = "Roma" };
        if (withProperty)
            db.Properties.Add(property);

        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guestId ?? Guid.NewGuid(),
            Source = source,
            Status = status,
            CheckInDate = arrival,
            CheckOutDate = checkOut ?? arrival.AddDays(3),
            NumberOfGuests = guests,
            CreatedAt = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    public async Task ChangeBookingAsync(Guid bookingId, Action<Booking> change)
    {
        await using var db = NewDb();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        await db.SaveChangesAsync();
    }

    public async Task SeedVerificationAsync(Guid orgId, Booking booking, BiometricOutcome outcome, DateTime createdAt)
    {
        await using var db = NewDb();
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = orgId,
            BookingId = booking.Id,
            GuestId = booking.GuestId,
            Outcome = outcome,
            ModelName = "test",
            ModelVersion = "1",
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<RoutineExecution>> ExecutionsAsync()
    {
        await using var db = NewDb();
        return await db.RoutineExecutions.AsNoTracking().OrderBy(e => e.StartedAt).ThenBy(e => e.Id).ToListAsync();
    }

    public async Task<RoutineExecution> SingleExecutionAsync()
    {
        var executions = await ExecutionsAsync();
        return Assert.Single(executions);
    }

    public async Task<RoutineExecution> ExecutionAsync(Guid executionId)
    {
        await using var db = NewDb();
        return await db.RoutineExecutions.AsNoTracking().SingleAsync(e => e.Id == executionId);
    }

    public async Task SetActiveAsync(Guid routineId, bool active)
    {
        await using var db = NewDb();
        var routine = await db.Routines.SingleAsync(r => r.Id == routineId);
        routine.Active = active;
        await db.SaveChangesAsync();
    }

    public static string Actions(params (string Type, string? ConfigJson)[] actions) =>
        "[" + string.Join(",", actions.Select(a => $"{{\"type\":\"{a.Type}\"" + (a.ConfigJson is null ? string.Empty : $",\"config\":{a.ConfigJson}") + "}")) + "]";

    public static RoutineExecutionResult ResultOf(RoutineExecution execution) => RoutineExecutionResult.Parse(execution.ResultJson);

    public static object PayloadOf(Booking booking) => new { BookingId = booking.Id, PropertyId = booking.PropertyId };

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
