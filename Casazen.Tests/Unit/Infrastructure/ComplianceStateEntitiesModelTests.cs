using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The shape of the four tables of the compliance dashboard (E03/F02/US01/T01, NF-52, NF-47): the unique indexes that make an alert stage
/// and the monthly email once-only, the index of the log, the stored numbers of the enums (never renumbered) and the tenant ownership of
/// every table. The unique indexes themselves are exercised on PostgreSQL by the later tasks that write the rows.
/// </summary>
public class ComplianceStateEntitiesModelTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void ComplianceAlertState_PropertyTypeAndReference_AreUnique()
    {
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(ComplianceAlertState))!;

        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal(
            [nameof(ComplianceAlertState.PropertyId), nameof(ComplianceAlertState.Type), nameof(ComplianceAlertState.ReferenceKey)],
            unique.Properties.Select(p => p.Name));
        Assert.Equal("ComplianceAlertStates", typeof(ComplianceAlertState).GetCustomAttribute<TableAttribute>()!.Name);
        Assert.Equal(ComplianceAlertState.ReferenceKeyMaxLength, entity.FindProperty(nameof(ComplianceAlertState.ReferenceKey))!.GetMaxLength());
    }

    [Fact]
    public void ComplianceMonthlySummaryLog_OrgAndMonth_AreUniqueSoTheEmailIsIdempotent()
    {
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(ComplianceMonthlySummaryLog))!;

        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal([nameof(ComplianceMonthlySummaryLog.OrgId), nameof(ComplianceMonthlySummaryLog.YearMonth)], unique.Properties.Select(p => p.Name));
        Assert.Equal(ComplianceMonthlySummaryLog.OrgMonthIndexName, unique.GetDatabaseName());
        Assert.Equal(ComplianceMonthlySummaryLog.YearMonthLength, entity.FindProperty(nameof(ComplianceMonthlySummaryLog.YearMonth))!.GetMaxLength());
    }

    [Fact]
    public void ComplianceSendLogEntry_IsIndexedByOrgPropertyAndTime_AndHoldsNoGuestData()
    {
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(ComplianceSendLogEntry))!;

        Assert.Contains(
            entity.GetIndexes(),
            i => !i.IsUnique
                 && i.Properties.Select(p => p.Name).SequenceEqual(
                     [nameof(ComplianceSendLogEntry.OrgId), nameof(ComplianceSendLogEntry.PropertyId), nameof(ComplianceSendLogEntry.SentAt)]));
        Assert.True(entity.FindProperty(nameof(ComplianceSendLogEntry.BookingId))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(ComplianceSendLogEntry.ByUserId))!.IsNullable);

        // The reference is a receipt or confirmation id, never a name: the columns of the row say it.
        Assert.Equal(
            [
                nameof(ComplianceSendLogEntry.BookingId), nameof(ComplianceSendLogEntry.ByUserId), nameof(ComplianceSendLogEntry.Id),
                nameof(ComplianceSendLogEntry.Kind), nameof(ComplianceSendLogEntry.OrgId), nameof(ComplianceSendLogEntry.PropertyId),
                nameof(ComplianceSendLogEntry.Reference), nameof(ComplianceSendLogEntry.Sender), nameof(ComplianceSendLogEntry.SentAt),
            ],
            entity.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ManualComplianceProof_IsIndexedByOrgPropertyKindAndDay_AndRetentionIsComputed()
    {
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(ManualComplianceProof))!;

        Assert.Contains(
            entity.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(ManualComplianceProof.OrgId), nameof(ManualComplianceProof.PropertyId), nameof(ManualComplianceProof.Kind), nameof(ManualComplianceProof.Day)]));
        Assert.Null(entity.FindProperty(nameof(ManualComplianceProof.RetainUntil)));

        var uploadedAt = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        var proof = new ManualComplianceProof { UploadedAt = uploadedAt };
        Assert.Equal(uploadedAt.AddYears(5), proof.RetainUntil);
    }

    [Fact]
    public void ComplianceTables_AreTenantOwned_AndTheStoredNumbersOfTheEnumsNeverMove()
    {
        Assert.All(
            new[] { typeof(ComplianceAlertState), typeof(ComplianceMonthlySummaryLog), typeof(ComplianceSendLogEntry), typeof(ManualComplianceProof) },
            type => Assert.True(typeof(ITenantOwned).IsAssignableFrom(type), type.Name));

        Assert.Equal([1, 2, 3], new[] { (int)ComplianceAlertType.IstatPeriod, (int)ComplianceAlertType.TouristTaxPeriod, (int)ComplianceAlertType.ConnectionInactive });
        Assert.Equal([1, 2, 3], new[] { (int)ComplianceSendKind.AlloggiatiGuests, (int)ComplianceSendKind.IstatPeriod, (int)ComplianceSendKind.TouristTaxPeriod });
        Assert.Equal([1, 2, 3], new[] { (int)ComplianceSender.CasaZen, (int)ComplianceSender.ChannelManager, (int)ComplianceSender.Manual });
        Assert.Equal(
            [1, 2, 3, 4],
            new[]
            {
                (int)ManualProofKind.AlloggiatiReceipt, (int)ManualProofKind.TouristTaxDeclaration,
                (int)ManualProofKind.TouristTaxPayment, (int)ManualProofKind.IstatProof,
            });
    }
}
