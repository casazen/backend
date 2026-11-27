using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F01/US01/T02 (NF-194, NF-195), without a database (the PostgreSQL side is <c>AddSelfHostedIdentityVerificationMigrationPostgresTests</c>): the two new tables
/// of the self-hosted identity verification are tenant data (<see cref="ITenantOwned"/>, the tenant query filter on every read, a non-null <c>OrgId</c>, a
/// restrict foreign key to the org); the keys are the filtered unique indexes of the plan; the defaults keep every existing row on the legacy path; and no
/// column of the identity schema can hold an image or a vector.
/// </summary>
public class IdentitySchemaTenantTests
{
    private static readonly Guid CallerOrgId = Guid.Parse("3f0c8a5e-6b1d-4f27-9e44-8d2a7c1b5e90");

    /// <summary>Data: a caller of one org never reads the segments or the evidence of another (TN-2).</summary>
    [Fact]
    public void NewEntitiesAreTenantOwned()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId));

        foreach (var type in new[] { typeof(IdentitySegmentSetting), typeof(IdentityReviewEvidence) })
        {
            Assert.True(typeof(ITenantOwned).IsAssignableFrom(type), $"{type.Name} is not ITenantOwned");
            var entityType = db.Model.FindEntityType(type)!;
            var filter = entityType.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter);
            Assert.True(filter?.Expression is not null, $"{type.Name} has no tenant query filter");
            Assert.Contains(nameof(ITenantOwned.OrgId), filter!.Expression!.ToString(), StringComparison.Ordinal);
            Assert.False(entityType.FindProperty(nameof(ITenantOwned.OrgId))!.IsNullable);
            var toOrg = Assert.Single(entityType.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(OrgEntity));
            Assert.Equal(DeleteBehavior.Restrict, toOrg.DeleteBehavior);
        }

        Assert.Matches("\"OrgId\" = @", db.IdentitySegmentSettings.ToQueryString());
        Assert.Matches("\"OrgId\" = @", db.IdentityReviewEvidences.ToQueryString());
    }

    [Fact]
    public void IdentityKeys_AreTheFilteredIndexesOfThePlan()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(null));

        var log = db.Model.FindEntityType(typeof(BiometricVerificationLog))!;
        var idempotency = Index(log, BiometricVerificationLog.IdempotencyIndexName);
        Assert.Equal((true, "\"IdempotencyKey\" IS NOT NULL"), (idempotency.IsUnique, idempotency.GetFilter()));
        Assert.Equal(["BookingId", "IdempotencyKey"], idempotency.Properties.Select(p => p.Name));
        // The readers of the last row of a booking keep their index on the booking alone.
        Assert.Contains(log.GetIndexes(), index => Columns(index) == "BookingId" && !index.IsUnique && index.GetFilter() is null);

        var segment = db.Model.FindEntityType(typeof(IdentitySegmentSetting))!;
        var orgWide = Index(segment, IdentitySegmentSetting.OrgWideIndexName);
        Assert.Equal((true, "\"PropertyId\" IS NULL", "OrgId,FormatId,Channel"), (orgWide.IsUnique, orgWide.GetFilter(), Columns(orgWide)));
        var perProperty = Index(segment, IdentitySegmentSetting.PropertyIndexName);
        Assert.Equal(
            (true, "\"PropertyId\" IS NOT NULL", "OrgId,PropertyId,FormatId,Channel"),
            (perProperty.IsUnique, perProperty.GetFilter(), Columns(perProperty)));
        var toProperty = Assert.Single(segment.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(Property));
        Assert.Equal(DeleteBehavior.Cascade, toProperty.DeleteBehavior);

        var evidence = db.Model.FindEntityType(typeof(IdentityReviewEvidence))!;
        var live = Index(evidence, IdentityReviewEvidence.LiveExpiryIndexName);
        Assert.Equal((false, "\"DeletedAt\" IS NULL", "ExpiresAt"), (live.IsUnique, live.GetFilter(), Columns(live)));
        Assert.Contains(evidence.GetIndexes(), index => Columns(index) == "OrgId,BookingId" && index.GetFilter() is null);
        // On purpose, only the org: a row outlives a hard delete of its booking or session until the purge deleted its object.
        Assert.Equal([typeof(OrgEntity)], evidence.GetForeignKeys().Select(key => key.PrincipalEntityType.ClrType));

        var session = db.Model.FindEntityType(typeof(GuestCheckInSession))!;
        Assert.Contains(session.GetIndexes(), index => Columns(index) == "OrgId,IdentityOpenedAt");
    }

    [Fact]
    public void ExistingRows_StayOnTheLegacyPath_ThroughTheDefaultsOfTheColumns()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(null));

        Assert.Equal(0, DefaultOf(db, typeof(GuestCheckInSession), nameof(GuestCheckInSession.IdentityGuidedRetries)));
        Assert.Equal(IdentityDecisionMode.Legacy, DefaultOf(db, typeof(BiometricVerificationLog), nameof(BiometricVerificationLog.DecisionMode)));
        Assert.Equal(IdentitySegmentMode.Observation, DefaultOf(db, typeof(IdentitySegmentSetting), nameof(IdentitySegmentSetting.Mode)));
        Assert.Equal("consistency", DefaultOf(db, typeof(IdentitySegmentSetting), nameof(IdentitySegmentSetting.RequiredAssurance)));

        // A row made by the code starts the same way.
        Assert.Equal(0, new GuestCheckInSession().IdentityGuidedRetries);
        Assert.Equal(IdentityDecisionMode.Legacy, new BiometricVerificationLog().DecisionMode);
        Assert.Equal((IdentitySegmentMode.Observation, "consistency"), (new IdentitySegmentSetting().Mode, new IdentitySegmentSetting().RequiredAssurance));
    }

    /// <summary>
    /// Privacy (ADR-013-R2, R20): the columns of the self-hosted verification are ids, hashes, codes, counters and times. None is binary, an array or JSON, and
    /// every text is bounded, so no column can hold an image, a frame or a face representation.
    /// </summary>
    [Fact]
    public void IdentitySchema_NoColumnCanHoldAnImageOrAVector()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(null));
        string[] newLogColumns =
        [
            nameof(BiometricVerificationLog.PolicyVersion), nameof(BiometricVerificationLog.PolicyHash), nameof(BiometricVerificationLog.DecisionMode),
            nameof(BiometricVerificationLog.ShadowOutcome), nameof(BiometricVerificationLog.FormatId), nameof(BiometricVerificationLog.Channel),
            nameof(BiometricVerificationLog.IdempotencyKey),
        ];
        var columns = db.Model.FindEntityType(typeof(IdentitySegmentSetting))!.GetProperties()
            .Concat(db.Model.FindEntityType(typeof(IdentityReviewEvidence))!.GetProperties())
            .Concat(db.Model.FindEntityType(typeof(GuestCheckInSession))!.GetProperties().Where(p => p.Name.StartsWith("Identity", StringComparison.Ordinal)))
            .Concat(db.Model.FindEntityType(typeof(BiometricVerificationLog))!.GetProperties().Where(p => newLogColumns.Contains(p.Name)))
            .ToList();

        Assert.Equal(17 + 14 + 13 + 7, columns.Count);
        Assert.All(columns, column =>
        {
            var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
            Assert.True(
                type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(int) || type.IsEnum,
                $"{column.DeclaringType.ClrType.Name}.{column.Name} is a {type.Name}");
            if (type == typeof(string))
            {
                Assert.True(column.GetMaxLength() is > 0 and <= IdentityReviewEvidence.StorageKeyMaxLength, $"{column.Name} is not bounded");
                Assert.StartsWith("character varying", column.GetColumnType(), StringComparison.Ordinal);
            }
        });
    }

    private static IReadOnlyIndex Index(IReadOnlyEntityType entityType, string name) =>
        Assert.Single(entityType.GetIndexes(), index => index.GetDatabaseName() == name);

    private static string Columns(IReadOnlyIndex index) => string.Join(",", index.Properties.Select(p => p.Name));

    private static object? DefaultOf(AppDbContext db, Type entity, string property) =>
        db.Model.FindEntityType(entity)!.FindProperty(property)!.GetDefaultValue();

    private static AppDbContext NewNpgsqlContext(ITenantContext tenant) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = orgId is not null;
    }
}
