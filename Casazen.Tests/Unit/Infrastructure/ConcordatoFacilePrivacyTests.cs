using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-BE, the PROOF of the privacy rule: the signed link to the guided contract form is never made before the privacy notice is accepted
/// for the lease. Refused with 409 <c>concordatofacile_privacy_not_accepted</c> (nothing signed, no URL, no event of the link), also for the
/// platform admin, whose bypass is about the payment; granted after the acceptance; the acceptance is one event (idempotent) that holds for
/// the version of the notice that was current and asks again when the notice changes; and another org's lease cannot be accepted (404).
/// </summary>
public class ConcordatoFacilePrivacyTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private static readonly Guid LeaseId = Guid.NewGuid();

    private string _privacyVersion = "2026-10-v2";

    [Theory]
    [InlineData(CfAccessSource.Payment)]
    [InlineData(CfAccessSource.Admin)]
    public async Task Link_WithoutTheAcceptance_Is409WithNoUrlAndNoEvent_AndAfterTheAcceptanceItIsGranted(CfAccessSource source)
    {
        await using var db = await NewDbWithLeaseAsync();
        var (privacy, link) = Services(db, source);
        var user = new ClaimsPrincipal();

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => link.GenerateSignedLinkAsync(LeaseId, user));

        Assert.Equal("concordatofacile_privacy_not_accepted", refused.Code);
        Assert.Empty(await db.LeaseEvents.Where(e => e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated).ToListAsync());

        var accepted = await privacy.AcceptAsync(LeaseId, OrgId);
        var granted = await link.GenerateSignedLinkAsync(LeaseId, user);

        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.AcceptedAt);
        Assert.StartsWith("https://app.concordatofacile.it/?partner=casazen&leaseId=", granted.Url);
        Assert.Single(await db.LeaseEvents.Where(e => e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated).ToListAsync());
        var state = await privacy.GetStateAsync(LeaseId, OrgId);
        Assert.True(state.Accepted);
        Assert.NotNull(state.LinkOpenedAt);
    }

    [Fact]
    public async Task Acceptance_IsOneEventWithTheVersionOnly_AndAsksAgainWhenTheNoticeChanges()
    {
        await using var db = await NewDbWithLeaseAsync();
        var (privacy, link) = Services(db, CfAccessSource.Payment);
        var user = new ClaimsPrincipal();

        await privacy.AcceptAsync(LeaseId, OrgId);
        await privacy.AcceptAsync(LeaseId, OrgId);

        // Idempotent: a second acceptance of the same version writes nothing, and the event holds the version and no person.
        var events = await db.LeaseEvents.Where(e => e.EventType == LeaseEventType.ConcordatoFacilePrivacyAccepted).ToListAsync();
        var only = Assert.Single(events);
        Assert.Equal("2026-10-v2", JsonDocument.Parse(only.Payload!).RootElement.GetProperty("policyVersion").GetString());
        Assert.Single(JsonDocument.Parse(only.Payload!).RootElement.EnumerateObject());

        // The notice changes: the acceptance of the old version is not the acceptance of the current one.
        _privacyVersion = "2026-12-v1";
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => link.GenerateSignedLinkAsync(LeaseId, user));
        Assert.Equal("concordatofacile_privacy_not_accepted", refused.Code);
        Assert.False((await privacy.GetStateAsync(LeaseId, OrgId)).Accepted);

        await privacy.AcceptAsync(LeaseId, OrgId);

        Assert.Equal(2, await db.LeaseEvents.CountAsync(e => e.EventType == LeaseEventType.ConcordatoFacilePrivacyAccepted));
        Assert.StartsWith("https://app.concordatofacile.it/", (await link.GenerateSignedLinkAsync(LeaseId, user)).Url);
    }

    [Fact]
    public async Task Acceptance_LeaseOfAnotherOrgOrMissing_Is404_NothingIsWritten_AndNothingIsRevealed()
    {
        await using var db = await NewDbWithLeaseAsync();
        var (privacy, _) = Services(db, CfAccessSource.Payment);
        await privacy.AcceptAsync(LeaseId, OrgId);
        var eventsBefore = await db.LeaseEvents.CountAsync();

        var otherOrg = await Assert.ThrowsAsync<NotFoundException>(() => privacy.AcceptAsync(LeaseId, OtherOrgId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => privacy.AcceptAsync(Guid.NewGuid(), OrgId));

        Assert.Equal("lease_not_found", otherOrg.Code);
        Assert.Equal("lease_not_found", missing.Code);
        Assert.Equal(eventsBefore, await db.LeaseEvents.CountAsync());

        // The state of a lease that is another org's shows nothing, although its own org accepted.
        var seenByTheOther = await privacy.GetStateAsync(LeaseId, OtherOrgId);
        Assert.False(seenByTheOther.Accepted);
        Assert.Null(seenByTheOther.AcceptedAt);
        Assert.Null(seenByTheOther.LinkOpenedAt);
    }

    private (ConcordatoFacilePrivacyService Privacy, ConcordatoFacileLinkService Link) Services(AppDbContext db, CfAccessSource source)
    {
        var legal = new Mock<ILegalDocumentService>();
        legal.Setup(l => l.GetPrivacy()).Returns(() => new LegalDocumentMeta(_privacyVersion, null, "Informativa Privacy", "", null));

        var access = new Mock<IConcordatoFacileAccessService>();
        access.Setup(a => a.CheckAccessAsync(LeaseId, It.IsAny<ClaimsPrincipal>(), default))
            .ReturnsAsync(new CfAccessResult(true, source, false, false, DateTime.UtcNow, null));

        var privacy = new ConcordatoFacilePrivacyService(db, legal.Object, NullLogger<ConcordatoFacilePrivacyService>.Instance);
        var link = new ConcordatoFacileLinkService(
            db,
            access.Object,
            privacy,
            Options.Create(new ConcordatoFacileOptions { LinkSecret = "test_link_secret_0123456789abcdef0123456789abcdef" }),
            NullLogger<ConcordatoFacileLinkService>.Instance);
        return (privacy, link);
    }

    private static async Task<AppDbContext> NewDbWithLeaseAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var propertyId = Guid.NewGuid();
        db.Comuni.Add(new Comune
        {
            IstatCode = "108044",
            Name = "Seveso",
            DisplayName = "Seveso",
            NormalizedName = "seveso",
            SearchText = "seveso",
            ProvinceCode = "MB",
            RegionIstatCode = "03",
            RegionName = "Lombardia",
            IsActive = true,
            SourceImportId = Guid.NewGuid(),
            SourceImport = new ComuneImport
            {
                Origin = ComuneImportOrigin.StartupSeed,
                SourceFileName = "comuni-test.csv",
                SourceVersion = "Test",
                ReferenceDate = new DateOnly(2026, 1, 1),
                Sha256 = new string('0', 64),
                ImportedBy = "system"
            }
        });
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = OrgId,
            OwnerId = "test-user",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Seveso",
            PostalCode = "20822",
            ComuneIstatCode = "108044",
            RegionCode = "LOM"
        });
        db.LeaseContracts.Add(new LeaseContract { Id = LeaseId, OrgId = OrgId, PropertyId = propertyId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return db;
    }
}
