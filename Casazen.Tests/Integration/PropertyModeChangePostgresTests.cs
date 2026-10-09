using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PM-02 on real PostgreSQL, where the guarantees live: at most one change waiting per property (partial unique index), the
/// creation and the application of a change under the dates lock of the property (<c>BookingRepository.LockPropertyDatesAsync</c>)
/// so a change and a booking never take the same nights, a run that cannot overlap another (session advisory lock) and an
/// application that happens once whatever the number of runs. The clock is a fixed <see cref="FakeTimeProvider"/>; the
/// e-mails are recorded.
/// </summary>
/// <remarks>
/// Written to run in the CI: they need PostgreSQL (<see cref="PostgresFactAttribute"/>) and are skipped on a machine without
/// one. The same logic is covered on EF InMemory by the unit tests; what only PostgreSQL proves is here.
/// </remarks>
public class PropertyModeChangePostgresTests : IClassFixture<PropertyModeChangePostgresTests.Factory>
{
    // "Today" for the host: 2 October of next year at 10:00 in Rome (FD-06, fixed clock).
    private static readonly DateTime Today = PublicAvailabilityPostgresTests.NextYear(10, 2);
    private static readonly DateTimeOffset Now = new(Today.AddHours(8), TimeSpan.Zero);

    private readonly Factory _factory;

    public PropertyModeChangePostgresTests(Factory factory) => _factory = factory;

    // ─── The index ──────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Table_AndItsIndexes_AreInTheDatabase()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT indexdef::text AS "Value" FROM pg_indexes WHERE tablename = 'PropertyModeChanges'""")
            .ToListAsync();

        Assert.Contains(indexes, i => i.Contains("UIX_PropertyModeChanges_PropertyId_Scheduled") && i.Contains("UNIQUE") && i.Contains("\"Status\" = 0"));
        Assert.Contains(indexes, i => i.Contains("(\"Status\", \"EffectiveDate\")"));
        Assert.Contains(indexes, i => i.Contains("(\"PropertyId\", \"CreatedAt\")"));
    }

    [PostgresFact]
    public async Task Index_TwoWaitingChangesOfTheSameProperty_IsRefusedByTheDatabase()
    {
        var property = await SeedPropertyAsync("pm02-index");
        await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today.AddDays(10));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today.AddDays(20)));

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(PropertyModeChange.OneScheduledIndexName, postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task Index_OnlyTheWaitingOnesCount_TheHistoryAndOtherPropertiesDoNot()
    {
        var property = await SeedPropertyAsync("pm02-history");
        var other = await SeedPropertyAsync("pm02-history-other");

        await InsertChangeAsync(property, PropertyModeChangeStatus.Applied, Today.AddDays(-30));
        await InsertChangeAsync(property, PropertyModeChangeStatus.Cancelled, Today.AddDays(-20));
        await InsertChangeAsync(property, PropertyModeChangeStatus.Failed, Today.AddDays(-10));
        await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today.AddDays(10));
        await InsertChangeAsync(other, PropertyModeChangeStatus.Scheduled, Today.AddDays(10));

        await WithDbAsync(async db =>
            Assert.Equal(4, await db.PropertyModeChanges.IgnoreQueryFilters().CountAsync(c => c.PropertyId == property.Id)));
    }

    // ─── The creation and the bookings ──────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Schedule_ToLong_AndABookingOnTheSameNightsAtTheSameTime_OnlyOneOfThemHappens()
    {
        for (var round = 0; round < 8; round++)
        {
            var property = await SeedPropertyAsync($"pm02-race-{round}");
            var day = Today.AddDays(10);
            using var start = new ManualResetEventSlim(false);

            var scheduleTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var modes = scope.ServiceProvider.GetRequiredService<IPropertyModeService>();
                try
                {
                    await modes.ScheduleAsync(property.Id, RentalMode.Long, day, property.OwnerId);
                    return true;
                }
                catch (DomainConflictException error) when (error.Code == PropertyModeErrorCodes.BlockedByBookings)
                {
                    return false;
                }
            });
            var bookingTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
                try
                {
                    await bookings.AddAsync(NewBooking(property, day.AddDays(1), day.AddDays(3), BookingSource.Direct));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });

            start.Set();
            var outcomes = await Task.WhenAll(scheduleTask, bookingTask);

            // Exactly one: either the change is programmed (and the calendar closed, so the booking was refused), or the booking
            // is there (and the change was refused). Never both on the same nights.
            Assert.Equal(1, outcomes.Count(won => won));
            await WithDbAsync(async db =>
            {
                var scheduled = await db.PropertyModeChanges.IgnoreQueryFilters()
                    .AnyAsync(c => c.PropertyId == property.Id && c.Status == PropertyModeChangeStatus.Scheduled);
                var booked = await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id);
                Assert.NotEqual(scheduled, booked);
            });
        }
    }

    [PostgresFact]
    public async Task Schedule_TwoChangesOfTheSamePropertyAtTheSameTime_OnlyOneIsProgrammed()
    {
        var property = await SeedPropertyAsync("pm02-double");
        using var start = new ManualResetEventSlim(false);

        Task<bool> Attempt(int offset) => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = _factory.Services.CreateAsyncScope();
            var modes = scope.ServiceProvider.GetRequiredService<IPropertyModeService>();
            try
            {
                await modes.ScheduleAsync(property.Id, RentalMode.Long, Today.AddDays(10 + offset), property.OwnerId);
                return true;
            }
            catch (DomainConflictException error) when (error.Code == PropertyModeErrorCodes.ChangeExists)
            {
                return false;
            }
        });

        var attempts = Enumerable.Range(0, 4).Select(Attempt).ToList();
        start.Set();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(won => won));
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.PropertyModeChanges.IgnoreQueryFilters().CountAsync(c => c.PropertyId == property.Id));
            Assert.Equal(1, await db.CalendarBlocks.IgnoreQueryFilters()
                .CountAsync(b => b.PropertyId == property.Id && b.ManualReason == CalendarBlockReason.ModeChange));
        });
    }

    [PostgresFact]
    public async Task Schedule_ToLong_TheClosedNightsRefuseTheBookingsOfTheRepositoryUnderTheLock()
    {
        var property = await SeedPropertyAsync("pm02-closed");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPropertyModeService>()
                .ScheduleAsync(property.Id, RentalMode.Long, Today.AddDays(10), property.OwnerId);
        }

        await using var other = _factory.Services.CreateAsyncScope();
        var bookings = other.ServiceProvider.GetRequiredService<IBookingRepository>();

        // The final check of the repository (under the property lock) reads the block of the change too.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bookings.AddAsync(NewBooking(property, Today.AddDays(12), Today.AddDays(14), BookingSource.Manual)));
        Assert.Contains("Property not available", error.Message);
        // The nights before the day of the change stay bookable.
        await bookings.AddAsync(NewBooking(property, Today.AddDays(5), Today.AddDays(9), BookingSource.Manual));
    }

    // ─── The application ────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task ApplyDue_AChangeAndABookingOnTheSameNightsAtTheSameTime_NeverEndWithALongPropertyAndAStay()
    {
        for (var round = 0; round < 8; round++)
        {
            var property = await SeedPropertyAsync($"pm02-apply-race-{round}");
            // The change is due today; it was programmed earlier, so no calendar block closes the nights yet.
            await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today, RentalMode.Long);
            using var start = new ManualResetEventSlim(false);

            var applyTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();
                return result;
            });
            var bookingTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
                try
                {
                    await bookings.AddAsync(NewBooking(property, Today.AddDays(1), Today.AddDays(3), BookingSource.Direct));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });

            start.Set();
            await Task.WhenAll(applyTask, bookingTask);

            await WithDbAsync(async db =>
            {
                var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
                var booked = await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id);
                var change = await db.PropertyModeChanges.IgnoreQueryFilters().SingleAsync(c => c.PropertyId == property.Id);
                // Either the stay got there first (the change failed, the property is still short), or the property became
                // long-term first (the stay was refused by the block). Never a long-term property with a stay to come.
                Assert.False(stored.RentalMode == RentalMode.Long && booked);
                Assert.Equal(
                    stored.RentalMode == RentalMode.Long ? PropertyModeChangeStatus.Applied : PropertyModeChangeStatus.Failed,
                    change.Status);
            });
        }
    }

    [PostgresFact]
    public async Task ApplyDue_TwoRunsAtTheSameTime_ApplyEveryChangeOnceAndTellTheHostOnce()
    {
        var properties = new List<Property>();
        for (var i = 0; i < 6; i++)
        {
            var property = await SeedPropertyAsync($"pm02-runs-{i}");
            await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today, RentalMode.Long);
            properties.Add(property);
        }

        _factory.Emails.Queued.Clear();
        using var start = new ManualResetEventSlim(false);
        Task<PropertyModeRunResult> Run() => Task.Run(async () =>
        {
            start.Wait();
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();
        });

        var runs = new[] { Run(), Run(), Run() };
        start.Set();
        var results = await Task.WhenAll(runs);

        var ids = properties.Select(p => p.Id).ToList();
        Assert.Equal(properties.Count, results.Sum(r => r.Applied));
        await WithDbAsync(async db =>
        {
            Assert.All(
                await db.PropertyModeChanges.IgnoreQueryFilters().Where(c => ids.Contains(c.PropertyId)).ToListAsync(),
                c => Assert.Equal(PropertyModeChangeStatus.Applied, c.Status));
            Assert.All(
                await db.Properties.IgnoreQueryFilters().Where(p => ids.Contains(p.Id)).ToListAsync(),
                p => Assert.Equal(RentalMode.Long, p.RentalMode));
            // One calendar block each, whatever the number of runs.
            var blocks = await db.CalendarBlocks.IgnoreQueryFilters()
                .Where(b => ids.Contains(b.PropertyId) && b.ManualReason == CalendarBlockReason.ModeChange)
                .GroupBy(b => b.PropertyId)
                .Select(g => g.Count())
                .ToListAsync();
            Assert.Equal(properties.Count, blocks.Count);
            Assert.All(blocks, count => Assert.Equal(1, count));
        });
        // One "applied" e-mail per change, to the contact address of its org.
        var applied = _factory.Emails.Snapshot().Where(e => e.Template == "property-mode-change-applied").ToList();
        Assert.Equal(properties.Count, applied.Count);
    }

    [PostgresFact]
    public async Task ApplyDue_AnotherRunHoldsTheLock_IsSkippedAndDoesNothing_ThenRunsWhenItIsReleased()
    {
        var property = await SeedPropertyAsync("pm02-lock");
        await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today, RentalMode.Long);
        string connectionString;
        await using (var scope = _factory.Services.CreateAsyncScope())
            connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;

        await using (var other = new NpgsqlConnection(connectionString))
        {
            await other.OpenAsync();
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@scope, @key)", other))
            {
                lockCommand.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.PropertyModeChangeRun);
                lockCommand.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash("property-mode-change"));
                await lockCommand.ExecuteNonQueryAsync();
            }

            await using var runScope = _factory.Services.CreateAsyncScope();
            var blocked = await runScope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();

            Assert.True(blocked.Skipped);
            Assert.Equal(0, blocked.Applied);
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", other);
            await unlock.ExecuteNonQueryAsync();
        }

        await using var afterScope = _factory.Services.CreateAsyncScope();
        var afterRelease = await afterScope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();

        Assert.False(afterRelease.Skipped);
        Assert.True(afterRelease.Applied >= 1);
        await WithDbAsync(async db =>
            Assert.Equal(RentalMode.Long, (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id)).RentalMode));
    }

    [PostgresFact]
    public async Task Cancel_AndApply_AtTheSameTime_TheChangeEndsEitherCancelledOrAppliedNeverBoth()
    {
        for (var round = 0; round < 6; round++)
        {
            var property = await SeedPropertyAsync($"pm02-cancel-race-{round}");
            var change = await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today, RentalMode.Long);
            using var start = new ManualResetEventSlim(false);

            var cancelTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IPropertyModeService>()
                        .CancelAsync(property.Id, change.Id, property.OwnerId);
                    return true;
                }
                catch (DomainConflictException error) when (error.Code == PropertyModeErrorCodes.ChangeNotScheduled)
                {
                    return false;
                }
            });
            var applyTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();
            });

            start.Set();
            var cancelled = await cancelTask;
            var run = await applyTask;

            await WithDbAsync(async db =>
            {
                var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
                var row = await db.PropertyModeChanges.IgnoreQueryFilters().SingleAsync(c => c.Id == change.Id);
                if (cancelled)
                {
                    Assert.Equal(PropertyModeChangeStatus.Cancelled, row.Status);
                    Assert.Equal(RentalMode.Short, stored.RentalMode);
                    Assert.Equal(0, run.Applied);
                }
                else
                {
                    Assert.Equal(PropertyModeChangeStatus.Applied, row.Status);
                    Assert.Equal(RentalMode.Long, stored.RentalMode);
                }
            });
        }
    }

    [PostgresFact]
    public async Task ApplyDue_ToShort_RemovesTheBlockAndEvaluatesTheCompliance_OnTheRealDatabase()
    {
        var property = await SeedPropertyAsync("pm02-back", RentalMode.Long);
        await WithDbAsync(async db =>
        {
            db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Source = CalendarBlockSource.Manual,
                StartUtc = Today.AddDays(-20),
                EndUtc = Today.AddDays(700),
                ManualReason = CalendarBlockReason.ModeChange,
            });
            await db.SaveChangesAsync();
        });
        await InsertChangeAsync(property, PropertyModeChangeStatus.Scheduled, Today, RentalMode.Short);

        await using var scope = _factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();

        Assert.True(result.Applied >= 1);
        await WithDbAsync(async db =>
        {
            var stored = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id);
            Assert.Equal(RentalMode.Short, stored.RentalMode);
            Assert.False(await db.CalendarBlocks.IgnoreQueryFilters()
                .AnyAsync(b => b.PropertyId == property.Id && b.ManualReason == CalendarBlockReason.ModeChange));
            // The seeded property has no CIN certificate nor checklist: the evaluation after the return suspends it.
            Assert.Equal(PropertyComplianceStatus.Suspended, stored.ComplianceStatus);
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<Property> SeedPropertyAsync(string label, RentalMode mode = RentalMode.Short)
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, label);
        await WithDbAsync(async db =>
        {
            var org = await db.Orgs.SingleAsync(o => o.Id == property.OrgId);
            org.ContactEmail = $"{label}-{Guid.NewGuid():N}@example.com";
            if (mode != RentalMode.Short)
                (await db.Properties.SingleAsync(p => p.Id == property.Id)).RentalMode = mode;
            await db.SaveChangesAsync();
        });
        return property;
    }

    private async Task<PropertyModeChange> InsertChangeAsync(
        Property property, PropertyModeChangeStatus status, DateTime effectiveDate, RentalMode to = RentalMode.Long)
    {
        var change = new PropertyModeChange
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            FromMode = PropertyModeRules.Opposite(to),
            ToMode = to,
            EffectiveDate = effectiveDate,
            Status = status,
            CreatedByUserId = property.OwnerId,
            CreatedAt = Now.UtcDateTime.AddDays(-2),
        };
        await WithDbAsync(async db =>
        {
            db.PropertyModeChanges.Add(change);
            await db.SaveChangesAsync();
        });
        return change;
    }

    private static Booking NewBooking(Property property, DateTime checkIn, DateTime checkOut, BookingSource source)
    {
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = $"giulia.{Guid.NewGuid():N}@example.com",
        };
        return new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Guest = guest,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            Status = BookingStatus.Confirmed,
            Source = source,
        };
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The integration host with <c>Features:PropertyModeChange</c> on, a fixed clock and the e-mails recorded.</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:PropertyModeChange"] = "true" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }
    }
}
