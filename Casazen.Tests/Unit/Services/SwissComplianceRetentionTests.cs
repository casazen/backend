using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-128 (casazen/backend#979, decisions D10, D11): the receipts of the Ticino police notifications, the sheets of the groups and the written authorizations
/// of the parents are kept five years from their date, then the daily job deletes their file and their row; an OTR report is never deleted, and nothing of
/// the last five years is touched, whatever the org.
/// </summary>
public class SwissComplianceRetentionTests
{
    // Today in Zurich is 20 November 2031: the cut day is 20 November 2026. A document dated the 19th goes, one dated the 20th stays.
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2031, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private static readonly DateTime CutDay = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Retention_DeletesOnlyOldSwissReceipts_Test()
    {
        await using var db = NewDb();
        var orgA = NewProperty(db);
        var orgB = NewProperty(db);
        var storage = new InMemoryFileStorage();
        var oldPolice = Receipt(db, storage, orgA, SwissComplianceFileType.Police, CutDay.AddDays(-1));
        var onTheCutDay = Receipt(db, storage, orgA, SwissComplianceFileType.Police, CutDay);
        var oldSheet = Receipt(db, storage, orgA, SwissComplianceFileType.GroupSheet, CutDay.AddYears(-1));
        var oldOtr = Receipt(db, storage, orgA, SwissComplianceFileType.Otr, CutDay.AddYears(-2));
        var oldAuthorization = Receipt(db, storage, orgB, SwissComplianceFileType.ParentalAuthorization, CutDay.AddDays(-30));
        var youngAuthorization = Receipt(db, storage, orgB, SwissComplianceFileType.ParentalAuthorization, CutDay.AddYears(3));
        await db.SaveChangesAsync();

        await Job(db, storage).ExecuteAsync(CancellationToken.None);

        // Gone: the police documents dated before the cut day, of both orgs, file and row. Kept: the one dated on the cut day (five years ago today, never
        // early), the young one and the OTR report (tax evidence, no retention decided), each with its own file.
        var kept = await db.SwissComplianceReceipts.AsNoTracking().Select(r => r.Id).ToListAsync();
        Assert.Equal(new[] { onTheCutDay.Id, oldOtr.Id, youngAuthorization.Id }.Order(), kept.Order());
        foreach (var gone in new[] { oldPolice, oldSheet, oldAuthorization })
            Assert.False(await storage.ExistsAsync(StorageBucket.Private, KeyOf(gone)));
        foreach (var stays in new[] { onTheCutDay, oldOtr, youngAuthorization })
            Assert.True(await storage.ExistsAsync(StorageBucket.Private, KeyOf(stays)));
    }

    [Fact]
    public async Task Retention_AFileThatCannotBeDeleted_KeepsItsRow_AndFailsTheRun()
    {
        await using var db = NewDb();
        var property = NewProperty(db);
        var storage = new InMemoryFileStorage();
        var stuck = Receipt(db, storage, property, SwissComplianceFileType.Police, CutDay.AddDays(-1));
        var deletable = Receipt(db, storage, property, SwissComplianceFileType.GroupSheet, CutDay.AddDays(-2));
        await db.SaveChangesAsync();
        var failing = new Mock<IFileStorage>();
        failing
            .Setup(s => s.DeleteAsync(StorageBucket.Private, KeyOf(stuck), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("TEST storage unavailable"));
        failing
            .Setup(s => s.DeleteAsync(StorageBucket.Private, KeyOf(deletable), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(db, failing.Object).ExecuteAsync(CancellationToken.None));

        // Nothing past its time is kept in silence: the run fails (Hangfire repeats it) and only the row whose file is still there stays.
        Assert.Equal([stuck.Id], await db.SwissComplianceReceipts.AsNoTracking().Select(r => r.Id).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configure_TheRetentionIsRegisteredDailyInUtc_WhateverTheFlagsSay(bool flagsOn)
    {
        var scheduled = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(
            manager.Object,
            RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: flagsOn, rliProvider: flagsOn, eSignProvider: flagsOn));

        Assert.Equal("swiss-compliance-retention", SwissComplianceRetentionJob.RecurringJobId);
        var registered = scheduled[SwissComplianceRetentionJob.RecurringJobId];
        Assert.Equal("55 3 * * *", registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(SwissComplianceRetentionJob), registered.Job.Type);
        manager.Verify(m => m.RemoveIfExists(SwissComplianceRetentionJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void AddSwissComplianceRetention_RegistersTheJobScoped_AndItResolvesWithValidatedScopes()
    {
        var registration = Assert.Single(new ServiceCollection().AddSwissComplianceRetention());
        Assert.Equal((typeof(SwissComplianceRetentionJob), ServiceLifetime.Scoped), (registration.ServiceType, registration.Lifetime));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Mock.Of<IFileStorage>());
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSwissComplianceRetention();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SwissComplianceRetentionJob>());
    }

    [Fact]
    public void ReceiptTypes_AreAppended_TheUploadAcceptsEachName_AndEachStoredNameFitsTheColumn()
    {
        // The values are appended, never renumbered; the stored text of each fits the column, which NF-128 widened to 40 («ParentalAuthorization» has 21).
        Assert.Equal(
            [(0, "Police"), (1, "OTR"), (2, "ParentalAuthorization"), (3, "GroupSheet")],
            Enum.GetValues<SwissComplianceFileType>().Select(t => ((int)t, SwissComplianceReceipts.StoredType(t))));
        Assert.Equal(
            SwissComplianceReceipt.TypeMaxLength,
            typeof(SwissComplianceReceipt).GetProperty(nameof(SwissComplianceReceipt.Type))!.GetCustomAttribute<MaxLengthAttribute>()!.Length);
        Assert.All(
            Enum.GetValues<SwissComplianceFileType>(),
            t => Assert.InRange(SwissComplianceReceipts.StoredType(t).Length, 1, SwissComplianceReceipt.TypeMaxLength));
        foreach (var name in new[] { "police", "OTR", "ParentalAuthorization", "groupsheet" })
            Assert.True(EnumNames.TryParseDefined<SwissComplianceFileType>(name, out _), name);

        // The five years apply to the documents of the police rules, never to the OTR report.
        Assert.Equal(["Police", "ParentalAuthorization", "GroupSheet"], SwissComplianceReceipts.RetainedFiveYears);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SwissComplianceRetentionJob Job(AppDbContext db, IFileStorage storage) =>
        new(db, storage, Clock, NullLogger<SwissComplianceRetentionJob>.Instance);

    /// <summary>A property of a new org of its own.</summary>
    private static Property NewProperty(AppDbContext db)
    {
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        db.Orgs.Add(org);
        var property = new Property { OrgId = org.Id, Name = "TEST", Country = "CH" };
        db.Properties.Add(property);
        return property;
    }

    /// <summary>A document of the archive, with its file in the private bucket under the key the real code builds.</summary>
    private static SwissComplianceReceipt Receipt(
        AppDbContext db,
        InMemoryFileStorage storage,
        Property property,
        SwissComplianceFileType type,
        DateTime reportDate)
    {
        var receipt = new SwissComplianceReceipt
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Type = SwissComplianceReceipts.StoredType(type),
            ReportDate = reportDate,
            Reference = "TEST",
            UploadedFileHash = new string('0', 64),
        };
        db.SwissComplianceReceipts.Add(receipt);
        storage.Add(KeyOf(receipt), "TEST document"u8.ToArray());
        return receipt;
    }

    private static string KeyOf(SwissComplianceReceipt receipt) =>
        StorageKeys.SwissComplianceReceipt(receipt.OrgId, receipt.PropertyId, receipt.Id);
}
