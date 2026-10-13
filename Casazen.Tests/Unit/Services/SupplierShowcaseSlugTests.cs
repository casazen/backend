using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The address of a supplier's public showcase (SU-13, A4-16).</summary>
public class SupplierShowcaseSlugTests
{
    [Theory]
    [InlineData("Pulizie Roma Srl", "pulizie-roma-srl")]
    [InlineData("Pulizie Città & Co.", "pulizie-citta-co")]
    [InlineData("  L'Idraulico   di Giù  ", "l-idraulico-di-giu")]
    [InlineData("Ñandú Servizi 24/7", "nandu-servizi-24-7")]
    [InlineData("---", "fornitore")]
    [InlineData("日本語", "fornitore")]
    [InlineData("", "fornitore")]
    [InlineData(null, "fornitore")]
    public void FromName_Name_ReturnsLowercaseAsciiWithSingleHyphens(string? name, string expected)
    {
        Assert.Equal(expected, SupplierShowcaseSlug.FromName(name));
    }

    [Fact]
    public void FromName_LongName_IsCutAtTheLimitWithoutATrailingHyphen()
    {
        var slug = SupplierShowcaseSlug.FromName(new string('a', 59) + " " + new string('b', 40));

        Assert.True(slug.Length <= SupplierShowcaseSlug.MaxBaseLength);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal(new string('a', 59), slug);
    }

    [Theory]
    [InlineData("  Pulizie-Roma ", "pulizie-roma")]
    [InlineData(null, "")]
    public void Normalize_SlugFromTheAddressBar_IsTrimmedAndLowercase(string? slug, string expected)
    {
        Assert.Equal(expected, SupplierShowcaseSlug.Normalize(slug));
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_ActiveSupplier_GeneratesItFromTheNameAndKeepsIt()
    {
        await using var db = CreateDb();
        var orgId = await SeedAsync(db, "Pulizie Città Srl", SupplierStatus.Active);
        var service = CreateService(db);

        var first = await service.EnsureShowcaseSlugAsync(orgId);
        var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
        profile.LegalName = "Un altro nome";
        await db.SaveChangesAsync();
        var second = await service.EnsureShowcaseSlugAsync(orgId);

        Assert.Equal("pulizie-citta-srl", first);
        // A link already shared keeps working when the supplier renames the business.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_SameNameAsAnotherSupplier_GetsADifferentSlug()
    {
        await using var db = CreateDb();
        var a = await SeedAsync(db, "Pulizie Roma", SupplierStatus.Active);
        var b = await SeedAsync(db, "Pulizie Roma", SupplierStatus.Active);
        var service = CreateService(db);

        var slugA = await service.EnsureShowcaseSlugAsync(a);
        var slugB = await service.EnsureShowcaseSlugAsync(b);

        Assert.Equal("pulizie-roma", slugA);
        Assert.Equal("pulizie-roma-2", slugB);
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_ManyWithTheSameName_FallsBackToARandomSuffix()
    {
        await using var db = CreateDb();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
            ids.Add(await SeedAsync(db, "Idraulico Mario", SupplierStatus.Active));
        var service = CreateService(db);

        var slugs = new List<string?>();
        foreach (var id in ids)
            slugs.Add(await service.EnsureShowcaseSlugAsync(id));

        Assert.Equal(6, slugs.Distinct().Count());
        Assert.All(slugs, s => Assert.StartsWith("idraulico-mario", s));
        Assert.Matches("^idraulico-mario-[0-9a-f]{6}$", slugs[5]!);
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_SupplierNeverActivated_GeneratesNothing()
    {
        await using var db = CreateDb();
        var orgId = await SeedAsync(db, "Pulizie Roma", SupplierStatus.Pending);

        var slug = await CreateService(db).EnsureShowcaseSlugAsync(orgId);

        Assert.Null(slug);
        Assert.Null((await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId)).ShowcaseSlug);
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_SuspendedSupplierWithASlug_KeepsIt()
    {
        await using var db = CreateDb();
        var orgId = await SeedAsync(db, "Pulizie Roma", SupplierStatus.Suspended, "pulizie-roma");

        Assert.Equal("pulizie-roma", await CreateService(db).EnsureShowcaseSlugAsync(orgId));
    }

    [Fact]
    public async Task EnsureShowcaseSlugAsync_OrgWithoutProfile_ReturnsNull()
    {
        await using var db = CreateDb();

        Assert.Null(await CreateService(db).EnsureShowcaseSlugAsync(Guid.NewGuid()));
    }

    private static async Task<Guid> SeedAsync(AppDbContext db, string legalName, SupplierStatus status, string? slug = null)
    {
        var org = new OrgEntity
        {
            Name = legalName,
            Slug = $"sup-{Guid.NewGuid():N}"[..30],
            DisplayName = legalName,
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = legalName,
            Phone = "+39 06 333333",
            Status = status,
            ShowcaseSlug = slug,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static SupplierService CreateService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            ComuneTestServices.Pilots(db),
            ComuneTestServices.Directory(db),
            ComuneTestServices.Matcher(db),
            LegalTestServices.Legal(),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
