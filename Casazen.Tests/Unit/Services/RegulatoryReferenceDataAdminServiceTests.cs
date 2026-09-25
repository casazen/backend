using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Admin CRUD of the LTR regulatory reference data (LT-13, A7-22): status, expiry, every rule and band of a
/// territorial agreement, and the comune IMU channels, all editable without a deploy, every write audited.
/// </summary>
public class RegulatoryReferenceDataAdminServiceTests
{
    private const string AdminUserId = "auth0|admin";
    private static readonly DateTimeOffset Today = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAgreementsAsync_ReturnsZoneNamesAndBandCount()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        SeedBand(db, agreement, "Unica", 0, 50);
        SeedBand(db, agreement, "Unica", 50, 74);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var rows = await sut.GetAgreementsAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Seveso", row.Comune);
        Assert.Equal(2, row.BandCount);
        Assert.Equal(["Unica"], row.ZoneNames);
    }

    [Fact]
    public async Task GetAgreementAsync_UnknownId_ReturnsNull()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db);

        Assert.Null(await sut.GetAgreementAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateAgreementAsync_ChangesFieldsSetsUpdatedAtAndLogsDiff()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);
        var input = FullRulesInput(agreement) with
        {
            DataCompleteness = DataCompleteness.Complete,
            SourceUrl = "https://example.org/accordo.pdf",
            RequiredTypeACount = 3,
        };

        var result = await sut.UpdateAgreementAsync(agreement.Id, input, AdminUserId);

        Assert.NotNull(result);
        Assert.Equal(DataCompleteness.Complete, result.Summary.DataCompleteness);
        Assert.Equal(3, result.Rules.RequiredTypeACount);
        Assert.Equal(Today.UtcDateTime, result.Summary.UpdatedAt);

        var audit = await sut.GetAuditAsync(agreement.Id);
        var entry = Assert.Single(audit);
        Assert.Equal(RegulatoryAuditAction.Updated, entry.Action);
        Assert.Equal(AdminUserId, entry.ChangedByUserId);
        Assert.Contains("RequiredTypeACount: 2 -> 3", entry.Changes, StringComparison.Ordinal);
        Assert.Contains("DataCompleteness: Missing -> Complete", entry.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAgreementAsync_UnknownId_ReturnsNull()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db);

        var result = await sut.UpdateAgreementAsync(Guid.NewGuid(), FullRulesInput(null), AdminUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAgreementAsync_NoActualChange_LogsNoModificationSummary()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        await sut.UpdateAgreementAsync(agreement.Id, FullRulesInput(agreement), AdminUserId);

        var entry = Assert.Single(await sut.GetAuditAsync(agreement.Id));
        Assert.Contains("nessuna modifica", entry.Changes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateBandAsync_ValidRange_UpdatesAndLogsUnderTheAgreement()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        var band = SeedBand(db, agreement, "Unica", 0, 50);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);
        var input = new UpdateRentBandInput("Unica", null, 0, 55, 20, 60, 20, 90, 20, 110);

        var result = await sut.UpdateBandAsync(agreement.Id, band.Id, input, AdminUserId);

        Assert.NotNull(result);
        var updatedBand = Assert.Single(result.Bands, b => b.Id == band.Id);
        Assert.Equal(55, updatedBand.MaxSqm);
        Assert.Equal(60m, updatedBand.SubFascia1MaxEurSqmYear);

        var entry = Assert.Single(await sut.GetAuditAsync(agreement.Id));
        Assert.Equal(RegulatoryAuditAction.Updated, entry.Action);
    }

    [Fact]
    public async Task UpdateBandAsync_MaxNotGreaterThanMin_ThrowsDomainRuleException()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        var band = SeedBand(db, agreement, "Unica", 0, 50);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);
        var input = new UpdateRentBandInput("Unica", null, 50, 50, 20, 60, 20, 90, 20, 110);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => sut.UpdateBandAsync(agreement.Id, band.Id, input, AdminUserId));
        Assert.Equal(RegulatoryReferenceDataErrorCodes.InvalidBandRange, ex.Code);
    }

    [Fact]
    public async Task UpdateBandAsync_UnknownBand_ReturnsNull()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.UpdateBandAsync(
            agreement.Id, Guid.NewGuid(), new UpdateRentBandInput("Unica", null, 0, 50, 20, 60, 20, 90, 20, 110), AdminUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task MarkAgreementVerifiedAsync_SetsDateAndSourceAndLogs()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.MarkAgreementVerifiedAsync(
            agreement.Id, new MarkVerifiedInput(new DateOnly(2026, 9, 20), "PDF ufficiale dell'accordo"), AdminUserId);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), result.Summary.LastVerifiedAt);
        Assert.Equal("PDF ufficiale dell'accordo", result.Summary.VerificationSource);
        var entry = Assert.Single(await sut.GetAuditAsync(agreement.Id));
        Assert.Equal(RegulatoryAuditAction.MarkedVerified, entry.Action);
    }

    [Fact]
    public async Task MarkAgreementVerifiedAsync_DateInTheFuture_ThrowsDomainRuleException()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => sut.MarkAgreementVerifiedAsync(
            agreement.Id, new MarkVerifiedInput(new DateOnly(2026, 9, 26), "domani"), AdminUserId));
        Assert.Equal(RegulatoryReferenceDataErrorCodes.VerifiedAtInFuture, ex.Code);
    }

    [Fact]
    public async Task UpdateImuChannelAsync_ChangesFieldsAndLogsDiff()
    {
        await using var db = CreateDb();
        var channel = SeedChannel(db, "Seveso");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);
        var before = new UpdateImuChannelInput(
            channel.RecipientOffice, channel.Email, channel.Pec, channel.PostalAddress, channel.Instructions,
            channel.RatePercent, channel.EffectiveRatePercent, channel.RateYear, channel.RateKind,
            channel.RateNotes, channel.RateSourceUrl, channel.SourceUrl, channel.DataCompleteness);
        var input = before with { RecipientOffice = "Nuovo Ufficio Tributi", DataCompleteness = DataCompleteness.Complete };

        var result = await sut.UpdateImuChannelAsync(channel.Id, input, AdminUserId);

        Assert.NotNull(result);
        Assert.Equal("Nuovo Ufficio Tributi", result.RecipientOffice);
        Assert.Equal(DataCompleteness.Complete, result.DataCompleteness);
        Assert.NotNull(result.UpdatedAt);
        var entry = Assert.Single(await sut.GetAuditAsync(channel.Id));
        Assert.Contains("RecipientOffice:", entry.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateImuChannelAsync_UnknownId_ReturnsNull()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db);

        var result = await sut.UpdateImuChannelAsync(
            Guid.NewGuid(),
            new UpdateImuChannelInput(
                RecipientOffice: "Ufficio", Email: null, Pec: null, PostalAddress: null, Instructions: null,
                RatePercent: null, EffectiveRatePercent: null, RateYear: null, RateKind: null,
                RateNotes: null, RateSourceUrl: null, SourceUrl: null, DataCompleteness: DataCompleteness.Missing),
            AdminUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task MarkImuChannelVerifiedAsync_SetsDateAndSource()
    {
        await using var db = CreateDb();
        var channel = SeedChannel(db, "Cesano Maderno");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.MarkImuChannelVerifiedAsync(
            channel.Id, new MarkVerifiedInput(new DateOnly(2026, 9, 1), "Delibera comunale"), AdminUserId);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), result.LastVerifiedAt);
        Assert.Equal("Delibera comunale", result.VerificationSource);
    }

    [Fact]
    public async Task GetAuditAsync_ReturnsNewestFirst()
    {
        await using var db = CreateDb();
        var agreement = SeedAgreement(db, "Seveso");
        await db.SaveChangesAsync();
        var clock = new Casazen.Tests.Unit.FakeTimeProvider(Today);
        var sut = CreateSut(db, clock);

        await sut.MarkAgreementVerifiedAsync(agreement.Id, new MarkVerifiedInput(new DateOnly(2026, 1, 1), "prima"), AdminUserId);
        // Two verifications logged in the same instant would tie on OccurredAt: move the clock so "newest first" is unambiguous.
        clock.Advance(TimeSpan.FromMinutes(1));
        await sut.MarkAgreementVerifiedAsync(agreement.Id, new MarkVerifiedInput(new DateOnly(2026, 2, 1), "seconda"), AdminUserId);

        var audit = await sut.GetAuditAsync(agreement.Id);

        Assert.Equal(2, audit.Count);
        Assert.Equal("seconda", ExtractSource(audit[0].Changes));
        Assert.Equal("prima", ExtractSource(audit[1].Changes));
    }

    private static string ExtractSource(string changes) => changes.Split("source: ")[1];

    private static IRegulatoryReferenceDataAdminService CreateSut(AppDbContext db, TimeProvider? clock = null) =>
        new RegulatoryReferenceDataAdminService(
            new TerritorialRentAgreementRepository(db),
            new ComuneImuChannelRepository(db),
            new RegulatoryDataAuditLogRepository(db),
            clock ?? new Casazen.Tests.Unit.FakeTimeProvider(Today));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            NullTenantContext.Instance);

    private static TerritorialRentAgreement SeedAgreement(AppDbContext db, string comune)
    {
        var agreement = new TerritorialRentAgreement
        {
            Comune = comune,
            Region = "Lombardia",
            AgreementName = "Accordo locale Quadro — Provincia di Monza e della Brianza",
            SignedDate = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            EffectiveDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            SourceUrl = "https://example.org/accordo.pdf",
            DataCompleteness = DataCompleteness.Missing,
            RequiredTypeACount = 2,
            SubFascia2MinTypeBCount = 3,
        };
        db.TerritorialRentAgreements.Add(agreement);
        return agreement;
    }

    private static ConcordatoRentBand SeedBand(AppDbContext db, TerritorialRentAgreement agreement, string zone, int min, int? max)
    {
        var band = new ConcordatoRentBand
        {
            TerritorialRentAgreementId = agreement.Id,
            ZoneName = zone,
            MinSqm = min,
            MaxSqm = max,
            SubFascia1MinEurSqmYear = 20m,
            SubFascia1MaxEurSqmYear = 57m,
            SubFascia2MinEurSqmYear = 58m,
            SubFascia2MaxEurSqmYear = 91m,
            SubFascia3MinEurSqmYear = 92m,
            SubFascia3MaxEurSqmYear = 109m,
        };
        db.ConcordatoRentBands.Add(band);
        return band;
    }

    private static ComuneImuChannel SeedChannel(AppDbContext db, string comune)
    {
        var channel = new ComuneImuChannel
        {
            Comune = comune,
            Region = "Lombardia",
            RecipientOffice = "Ufficio Tributi",
            Email = "tributi@example.it",
            DataCompleteness = DataCompleteness.Partial,
        };
        db.ComuneImuChannels.Add(channel);
        return channel;
    }

    /// <summary>Every rule field left as the seed's default (2 A-elements, 3 B-elements, everything else 0), so a test
    /// can override just the field it cares about (named arguments: positional count never has to match by hand).</summary>
    private static UpdateAgreementInput FullRulesInput(TerritorialRentAgreement? agreement) => new(
        DataCompleteness: agreement?.DataCompleteness ?? DataCompleteness.Missing,
        SourceUrl: agreement?.SourceUrl,
        ExpiresAt: null,
        ExpiryNote: null,
        RemainsInForceUntilReplaced: false,
        RequiredTypeACount: agreement?.RequiredTypeACount ?? 2,
        SubFascia2MinTypeBCount: agreement?.SubFascia2MinTypeBCount ?? 3,
        SubFascia3MinTypeCCount: 0,
        SubFascia3MinQualifyingTypeDCount: 0,
        SubFascia3QualifyingTypeDElements: null,
        SubFascia3MaxMinTypeDCount: 0,
        StoveHeatingMinTypeBCount: 0,
        CoefficientCombination: CoefficientCombination.Additive,
        FurnishedUpliftPercent: 0,
        AirConditioningUpliftPercent: 0,
        SmallSqmMax: 0,
        SmallSqmUpliftPercent: 0,
        MidSqmMin: 0,
        MidSqmMax: 0,
        MidSqmUpliftPercent: 0,
        LargeSqmMin: 0,
        LargeSqmReductionPercent: 0,
        GarageAppurtenancePercent: 0,
        BalconyAppurtenancePercent: 0,
        OtherAppurtenancePercent: 0,
        GreenAreaAppurtenancePercent: 0,
        Duration4UpliftPercent: 0,
        Duration5UpliftPercent: 0,
        Duration6UpliftPercent: 0);
}
