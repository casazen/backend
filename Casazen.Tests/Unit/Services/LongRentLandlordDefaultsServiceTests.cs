using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12: the landlord the wizard suggests — the first landlord of the most recent lease the caller reaches, else the fiscal data of the
/// org and the name and e-mail of the caller; every field may be empty, nothing anonymized is offered, and a member limited to some
/// properties never gets the landlord of a lease of another one.
/// </summary>
public class LongRentLandlordDefaultsServiceTests
{
    private const string Caller = "auth0|caller";

    private static LongRentLandlordDefaultsService Service(AppDbContext db) => new(db);

    private static async Task<(LeaseDraftHarness H, Guid OrgId)> NewAsync()
    {
        var h = await new LeaseDraftHarness().SeedAsync();
        return (h, h.OrgId);
    }

    private static async Task SeedLeaseAsync(
        LeaseDraftHarness h,
        Guid propertyId,
        string landlordFirstName,
        DateTime createdAt,
        string? residence = "Bari, via Sparano 1",
        string? email = "landlord@example.com",
        bool anonymized = false,
        string cf = "RSSMRA80A01H501U")
    {
        await using var db = h.SecondContext();
        var lease = new LeaseContract
        {
            PropertyId = propertyId,
            OrgId = h.OrgId,
            Status = LeaseStatus.Signed,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var landlord = new Party
        {
            Role = PartyRole.Landlord,
            FirstName = landlordFirstName,
            LastName = "Rossi",
            FiscalCode = cf,
            Citizenship = "IT",
            ContactEmail = email ?? string.Empty,
            Residence = residence,
        };
        if (anonymized)
            LeasePartyPrivacyService.AnonymizeParty(landlord, createdAt);
        lease.Parties.Add(landlord);
        lease.Parties.Add(new Party { Role = PartyRole.Tenant, FirstName = "Inquilino", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = "t@example.com" });
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
    }

    private static async Task SeedCallerAsync(LeaseDraftHarness h, string first = "Anna", string last = "Bianchi", string email = "anna@example.com")
    {
        await using var db = h.SecondContext();
        db.Users.Add(new User { Id = Caller, Email = email, FirstName = first, LastName = last, OrgId = h.OrgId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WithNoLease_TheFiscalDataOfTheOrg_AndTheNameAndEmailOfTheCaller()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedCallerAsync(h);
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(orgId), Caller);

        // The org of the harness declares a fiscal code; nothing is known of the residence.
        Assert.Equal(new LandlordDefaultsDto("Anna", "Bianchi", "RSSMRA80A01H501U", null, "anna@example.com"), defaults);
    }

    [Fact]
    public async Task WithNoLeaseAndNoFiscalCodeOfTheOrg_TheVatNumberIsTheFallback_AndEveryFieldMayBeEmpty()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await using (var db = h.SecondContext())
        {
            var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
            org.FiscalCode = null;
            org.PartitaIvaNumber = "12345678903";
            await db.SaveChangesAsync();
        }

        await using var read = h.SecondContext();
        var withVat = await Service(read).GetAsync(new HostScope(orgId), "auth0|nobody");

        Assert.Equal(new LandlordDefaultsDto(null, null, "12345678903", null, null), withVat);
    }

    [Fact]
    public async Task WithAnEmptyOrgAndNoUser_EveryFieldIsNull_NoError()
    {
        var (h, _) = await NewAsync();
        using var _ = h;
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(Guid.NewGuid()), "auth0|nobody");

        Assert.Equal(new LandlordDefaultsDto(null, null, null, null, null), defaults);
    }

    [Fact]
    public async Task TheLandlordOfTheMostRecentLease_IsTheOneSuggested_NotMasked_WithTheResidence()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedCallerAsync(h);
        await SeedLeaseAsync(h, h.PropertyId, "Giovanni", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), residence: "Lecce, via Vecchia 1");
        await SeedLeaseAsync(h, h.PropertyId, "Mario", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), residence: "Bari, via Sparano 1", email: "mario@example.com");
        await SeedLeaseAsync(h, h.PropertyId, "Luigi", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(orgId), Caller);

        Assert.Equal(new LandlordDefaultsDto("Mario", "Rossi", "RSSMRA80A01H501U", "Bari, via Sparano 1", "mario@example.com"), defaults);
    }

    [Fact]
    public async Task ALeaseWhosePartiesWereAnonymized_IsSkipped_TheNextOneIsUsed()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedLeaseAsync(h, h.PropertyId, "Giovanni", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedLeaseAsync(h, h.PropertyId, "Mario", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), anonymized: true);
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(orgId), Caller);

        Assert.Equal("Giovanni", defaults.FirstName);
        Assert.DoesNotContain("ANON", defaults.ContactEmail ?? string.Empty);
    }

    [Fact]
    public async Task FieldsThatAreEmptyInTheLease_ComeBackNull()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedLeaseAsync(h, h.PropertyId, "Mario", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), residence: null, email: null);
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(orgId), Caller);

        Assert.Equal(new LandlordDefaultsDto("Mario", "Rossi", "RSSMRA80A01H501U", null, null), defaults);
    }

    [Fact]
    public async Task AMemberLimitedToTheirOwnProperties_NeverGetsTheLandlordOfALeaseOfAnotherOne()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedCallerAsync(h);
        // The property of the harness is owned by LeaseDraftHarness.OwnerId; another one is owned by somebody else.
        var other = await h.SeedPropertyAsync();
        await using (var db = h.SecondContext())
        {
            (await db.Properties.SingleAsync(p => p.Id == other.Id)).OwnerId = "auth0|someone-else";
            await db.SaveChangesAsync();
        }

        await SeedLeaseAsync(h, h.PropertyId, "Mario", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedLeaseAsync(h, other.Id, "Segreto", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), cf: "BNCMRC90C15F205" + Casazen.Core.Regulatory.ItalianFiscalCode.CheckCharacter("BNCMRC90C15F205"));

        await using var read = h.SecondContext();
        var owner = await Service(read).GetAsync(new HostScope(orgId, OwnerId: LeaseDraftHarness.OwnerId), Caller);
        var stranger = await Service(read).GetAsync(new HostScope(orgId, OwnerId: "auth0|someone-else"), Caller);
        var nobody = await Service(read).GetAsync(new HostScope(orgId, OwnerId: "auth0|nobody-owns-anything"), Caller);
        var wholeOrg = await Service(read).GetAsync(new HostScope(orgId), Caller);

        Assert.Equal("Mario", owner.FirstName);
        Assert.Equal("Segreto", stranger.FirstName);
        // Whoever reaches no lease gets the fallback, never the landlord of somebody else's.
        Assert.Equal("Anna", nobody.FirstName);
        Assert.Equal("Segreto", wholeOrg.FirstName); // the most recent of the org
    }

    [Fact]
    public async Task AnotherOrgsLease_IsNeverOffered()
    {
        var (h, orgId) = await NewAsync();
        using var _ = h;
        await SeedCallerAsync(h);
        await SeedLeaseAsync(h, h.PropertyId, "Mario", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var db = h.SecondContext();

        var defaults = await Service(db).GetAsync(new HostScope(Guid.NewGuid()), Caller);

        Assert.NotEqual("Mario", defaults.FirstName);
        Assert.Equal("Anna", defaults.FirstName);
        Assert.Equal(orgId, h.OrgId);
    }
}
