using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12, decision LR-D33: the drafts nobody changed for <c>LongRentLease:DraftRetentionDays</c> days are deleted with their parties and
/// files; one with a signed copy, a rent plan, or that is not a draft any more never is; a second run finds nothing.
/// </summary>
public class LeaseDraftRetentionServiceTests
{
    private static LeaseDraftRetentionService NewRetention(LeaseDraftHarness h, int days = 180, AppDbContext? db = null)
    {
        db ??= h.SecondContext();
        return new LeaseDraftRetentionService(
            db,
            new LeaseDraftDeleter(db, h.Storage.Object, NullLogger<LeaseDraftDeleter>.Instance),
            Options.Create(new LongRentLeaseOptions { DraftRetentionDays = days }),
            NullLogger<LeaseDraftRetentionService>.Instance,
            h.Clock);
    }

    /// <summary>A complete draft, whose last change was <paramref name="idleDays"/> days before "now".</summary>
    private static async Task<Guid> IdleDraftAsync(LeaseDraftHarness h, double idleDays, Action<LeaseContract>? change = null)
    {
        var (created, _) = await h.CompleteAsync();
        await using var db = h.SecondContext();
        var lease = await db.LeaseContracts.SingleAsync(l => l.Id == created.Id);
        lease.UpdatedAt = LeaseDraftHarness.Now.UtcDateTime.AddDays(-idleDays);
        change?.Invoke(lease);
        await db.SaveChangesAsync();
        return created.Id;
    }

    private static async Task<bool> ExistsAsync(LeaseDraftHarness h, Guid id)
    {
        await using var db = h.SecondContext();
        return await db.LeaseContracts.AnyAsync(l => l.Id == id);
    }

    [Fact]
    public async Task Run_ADraftIdleForMoreThanTheRetention_IsDeletedWithItsPartiesAndEvents()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var old = await IdleDraftAsync(h, 181);
        var recent = await IdleDraftAsync(h, 10);

        var result = await NewRetention(h).RunAsync();

        Assert.False(result.Skipped);
        Assert.Equal(1, result.Deleted);
        Assert.False(await ExistsAsync(h, old));
        Assert.True(await ExistsAsync(h, recent));
        await using var db = h.SecondContext();
        Assert.False(await db.Parties.AnyAsync(p => p.LeaseContractId == old));
        Assert.False(await db.LeaseEvents.AnyAsync(e => e.LeaseContractId == old));
        Assert.True(await db.Parties.AnyAsync(p => p.LeaseContractId == recent));
    }

    [Fact]
    public async Task Run_TheCutoffIsStrict_ADraftIdleForExactlyTheRetentionIsKept_OneSecondMoreIsDeleted()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var exact = await IdleDraftAsync(h, 180);
        var over = await IdleDraftAsync(h, 180, l => l.UpdatedAt = l.UpdatedAt.AddSeconds(-1));

        var result = await NewRetention(h).RunAsync();

        Assert.Equal(1, result.Deleted);
        Assert.True(await ExistsAsync(h, exact));
        Assert.False(await ExistsAsync(h, over));
        Assert.Equal(LeaseDraftHarness.Now.UtcDateTime.AddDays(-180), result.Cutoff);
    }

    [Fact]
    public async Task Run_TheRetentionIsTheConfiguredOne()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var id = await IdleDraftAsync(h, 40);

        var kept = await NewRetention(h, days: 180).RunAsync();
        Assert.Equal(0, kept.Deleted);
        Assert.True(await ExistsAsync(h, id));

        var deleted = await NewRetention(h, days: 30).RunAsync();
        Assert.Equal(1, deleted.Deleted);
        Assert.False(await ExistsAsync(h, id));
    }

    [Theory]
    [InlineData("a signed copy")]
    [InlineData("a rent plan")]
    [InlineData("a signed lease")]
    [InlineData("a registered lease")]
    public async Task Run_NeverDeletesAContractThatIsMoreThanADraft(string what)
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        Guid id;
        if (what == "a rent plan")
        {
            id = await IdleDraftAsync(h, 1000);
            await using var db = h.SecondContext();
            db.RentSchedules.Add(new RentSchedule
            {
                OrgId = h.OrgId,
                LeaseContractId = id,
                Cadence = RentCadence.Monthly,
                BillingDayOfMonth = 5,
                Amount = 900m,
                NextRunDate = new DateOnly(2026, 12, 5),
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            id = await IdleDraftAsync(h, 1000, lease =>
            {
                switch (what)
                {
                    case "a signed copy":
                        lease.SignedPdfStoragePath = $"leases/{lease.OrgId}/{lease.Id}/signed-contract/c.pdf";
                        break;
                    case "a signed lease":
                        lease.Status = LeaseStatus.Signed;
                        break;
                    default:
                        lease.Status = LeaseStatus.Registered;
                        break;
                }
            });
        }

        var result = await NewRetention(h).RunAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(await ExistsAsync(h, id));
        h.Storage.Verify(s => s.DeleteAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_IsIdempotent_TheSecondRunFindsNothing()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        await IdleDraftAsync(h, 200);
        await IdleDraftAsync(h, 300);

        var first = await NewRetention(h).RunAsync();
        var second = await NewRetention(h).RunAsync();

        Assert.Equal(2, first.Deleted);
        Assert.Equal(0, second.Deleted);
    }

    [Fact]
    public async Task Run_TheFilesOfADeletedDraft_AreDeleted_AfterTheCommit()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var id = await IdleDraftAsync(h, 200, l => l.QuesturaCommunicationReceiptPath = $"leases/{l.OrgId}/{l.Id}/questura/receipt.pdf");

        await NewRetention(h).RunAsync();

        h.Storage.Verify(
            s => s.DeleteAsync(StorageBucket.Private, $"leases/{h.OrgId}/{id}/questura/receipt.pdf", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_ADraftThatNothingCanDelete_DoesNotMakeTheRunLoop()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        // Old, still a draft, no signed copy: it is a candidate — and a rent plan, added after the list is read, keeps it.
        var id = await IdleDraftAsync(h, 500);
        await using (var db = h.SecondContext())
        {
            db.RentSchedules.Add(new RentSchedule { OrgId = h.OrgId, LeaseContractId = id, Cadence = RentCadence.Monthly, BillingDayOfMonth = 5, Amount = 900m, NextRunDate = new DateOnly(2026, 12, 5), IsActive = true });
            await db.SaveChangesAsync();
        }

        var result = await NewRetention(h).RunAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(await ExistsAsync(h, id));
    }

    [Fact]
    public async Task Run_ADraftSavedBetweenTwoRuns_IsKept_ItsLastChangeIsNow()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var id = await IdleDraftAsync(h, 500);

        await h.NewService().SaveAsync(id, (await h.NewService().GetAsync(id)).Version, 3, LeaseDraftHarness.Json("""{"monthlyRent":950}"""));
        var result = await NewRetention(h).RunAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(await ExistsAsync(h, id));
    }

    [Fact]
    public async Task TryDelete_ADraftThatWasSavedOrSignedSinceTheListWasRead_IsLeftAlone_TheConditionsAreCheckedAgainUnderTheLock()
    {
        using var h = await new LeaseDraftHarness().SeedAsync();
        var saved = await IdleDraftAsync(h, 500);
        var signed = await IdleDraftAsync(h, 500);
        var gone = await IdleDraftAsync(h, 500);
        var cutoff = LeaseDraftHarness.Now.UtcDateTime.AddDays(-180);
        var retention = NewRetention(h);

        // The list was read when all three were idle; since then one was saved, one was signed and one was deleted by its owner.
        await h.NewService().SaveAsync(saved, (await h.NewService().GetAsync(saved)).Version, 3, LeaseDraftHarness.Json("""{"monthlyRent":950}"""));
        await using (var db = h.SecondContext())
        {
            (await db.LeaseContracts.SingleAsync(l => l.Id == signed)).Status = LeaseStatus.Signed;
            await db.SaveChangesAsync();
        }

        await h.NewService().DeleteAsync(gone);

        Assert.False(await retention.TryDeleteAsync(saved, cutoff, CancellationToken.None));
        Assert.False(await retention.TryDeleteAsync(signed, cutoff, CancellationToken.None));
        Assert.False(await retention.TryDeleteAsync(gone, cutoff, CancellationToken.None));
        Assert.True(await ExistsAsync(h, saved));
        Assert.True(await ExistsAsync(h, signed));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(180, true)]
    [InlineData(3650, true)]
    [InlineData(3651, false)]
    [InlineData(-1, false)]
    public void TheOptions_AreValidated_AtStartup(int days, bool valid)
    {
        var options = new LongRentLeaseOptions { DraftRetentionDays = days };

        Assert.Equal(valid, options.Validate().Count == 0);
        Assert.Equal(valid, new LongRentLeaseOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void TheDefault_Is180Days_LRD33()
    {
        Assert.Equal(180, new LongRentLeaseOptions().DraftRetentionDays);
        Assert.Equal(180, LongRentLeaseOptions.DefaultDraftRetentionDays);
        Assert.Equal("LongRentLease", LongRentLeaseOptions.SectionName);
    }
}
