using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.Storage;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-20 on a real Hangfire storage: the OTA partner recurring jobs registered by an earlier deploy disappear on the
/// first startup with <c>Features:OtaPartnerApi</c> off, while the iCal sync keeps its schedule.
/// </summary>
public class OtaRecurringJobsPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public void Configure_OtaPartnerApiTurnedOff_DeletesOtaRecurringJobsFromStorage()
    {
        var storage = CreateStorage();
        var manager = new RecurringJobManager(storage);

        RecurringJobsRegistration.Configure(manager, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: true));
        Assert.Contains(RecurringJobsRegistration.OtaSyncAllJobId, RecurringJobIds(storage));
        Assert.Contains(RecurringJobsRegistration.BookingPullAllJobId, RecurringJobIds(storage));

        RecurringJobsRegistration.Configure(manager, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var ids = RecurringJobIds(storage);
        Assert.DoesNotContain(RecurringJobsRegistration.OtaSyncAllJobId, ids);
        Assert.DoesNotContain(RecurringJobsRegistration.BookingPullAllJobId, ids);
        Assert.Contains("property-ical-sync", ids);
    }

    private PostgreSqlStorage CreateStorage()
    {
        var settings = new HangfireStorageSettings("hangfire_fd20", HangfireStorageSettings.DefaultDistributedLockTimeout);
        var options = settings.CreateStorageOptions();
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(_database!.ConnectionString, options, null), options);
    }

    private static List<string> RecurringJobIds(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs().Select(job => job.Id).ToList();
    }
}
