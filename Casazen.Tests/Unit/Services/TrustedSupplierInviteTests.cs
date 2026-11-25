using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Bug #977: a host invites one of its trusted suppliers with the supplier invite (hashed token, email, registration page), and
/// accepting it creates the relation with the host, for a new supplier and for one already registered.
/// </summary>
public class TrustedSupplierInviteTests
{
    private static readonly Guid HostOrgId = Guid.NewGuid();
    private const string SupplierEmail = "supplier@test.com";
    private const string SupplierUserId = "auth0|supplier-test";
    private const string HostUserId = "auth0|host-test";

    [Fact]
    public async Task InviteSupplierAsync_StoresAHostInviteAndQueuesTheSupplierInviteEmail()
    {
        await using var db = CreateDbContext();
        var property = AddProperty(db, HostOrgId, "Roma", "058091");
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();

        var result = await CreateTrustedSupplierService(db, queue)
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, [property.Id]), HostUserId);

        var stored = Assert.Single(db.SupplierInviteRecords);
        Assert.Equal(result.InviteId, stored.Id);
        Assert.Equal(HostOrgId, stored.InvitedByOrgId);
        Assert.Equal(new[] { property.Id }, stored.AssignedPropertyIds!);
        Assert.Equal("058091", stored.ComuneCode);
        var (to, content, template) = Assert.Single(queue.Queued);
        Assert.Equal(SupplierEmail, to);
        Assert.Equal(Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierInvite, template);
        // The same link as an admin invite; only the hash of its token is stored.
        Assert.Equal(SupplierInviteTokens.Hash(InviteToken(content.HtmlBody)), stored.TokenHash);
    }

    [Fact]
    public async Task InviteSupplierAsync_PropertyAbroad_KeepsItsCityAsWritten()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Lugano", istatCode: null);
        await db.SaveChangesAsync();

        await CreateTrustedSupplierService(db, new RecordingEmailQueue())
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId);

        Assert.Equal("Lugano", Assert.Single(db.SupplierInviteRecords).ComuneCode);
    }

    [Fact]
    public async Task InviteSupplierAsync_OrgWithoutProperties_Throws422ComuneMissing()
    {
        await using var db = CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateTrustedSupplierService(db, new RecordingEmailQueue())
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId));

        Assert.Equal("trusted_supplier_invite_comune_missing", ex.Code);
        Assert.Empty(db.SupplierInviteRecords);
    }

    [Fact]
    public async Task InviteSupplierAsync_PropertyOfAnotherOrg_Throws422AndInvitesNobody()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        var foreign = AddProperty(db, Guid.NewGuid(), "Milano", "015146");
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateTrustedSupplierService(db, queue)
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, [foreign.Id]), HostUserId));

        Assert.Equal("trusted_supplier_property_invalid", ex.Code);
        Assert.Empty(db.SupplierInviteRecords);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task InviteSupplierAsync_InvalidEmail_Throws422()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateTrustedSupplierService(db, new RecordingEmailQueue())
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest("not-an-email", []), HostUserId));

        Assert.Equal("trusted_supplier_invite_email_invalid", ex.Code);
    }

    [Fact]
    public async Task InviteSupplierAsync_SupplierAlreadyTrusted_Throws409()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        var supplierOrgId = AddRegisteredSupplier(db, SupplierEmail);
        db.TrustedSuppliers.Add(new TrustedSupplier { OrgId = HostOrgId, SupplierOrgId = supplierOrgId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => CreateTrustedSupplierService(db, new RecordingEmailQueue())
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest("Supplier@Test.com", []), HostUserId));

        Assert.Equal("trusted_supplier_already_added", ex.Code);
    }

    [Fact]
    public async Task InviteSupplierAsync_SecondPendingInviteOfTheSameOrg_Throws409ButAnAdminInviteDoesNotBlockIt()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();
        await CreateSupplierService(db, queue).CreateInviteAsync(SupplierEmail, "H501", null, null);
        var service = CreateTrustedSupplierService(db, queue);

        await service.InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId);
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId));

        Assert.Equal(SupplierAdminErrorCodes.DuplicateInvite, ex.Code);
        Assert.Equal(2, db.SupplierInviteRecords.Count());
    }

    [Fact]
    public async Task RegisterAsync_HostInvite_TheNewSupplierBecomesATrustedSupplierOfTheInvitingOrg()
    {
        await using var db = CreateDbContext();
        var property = AddProperty(db, HostOrgId, "Roma", "058091");
        db.Users.Add(new User { Id = SupplierUserId, Email = SupplierEmail });
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();
        await CreateTrustedSupplierService(db, queue)
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, [property.Id]), HostUserId);
        var token = InviteToken(Assert.Single(queue.Queued).Content.HtmlBody);

        var result = await CreateSupplierService(db, queue).RegisterAsync(
            new SupplierRegistration(SupplierEmail, "Pulizie Test", "+390000000000", "058091", token, SupplierUserId, SupplierEmail));

        var relation = Assert.Single(db.TrustedSuppliers);
        Assert.Equal(HostOrgId, relation.OrgId);
        Assert.Equal(result.Org.Id, relation.SupplierOrgId);
        Assert.Equal(new[] { property.Id }, relation.AssignedPropertyIds);
        Assert.True(Assert.Single(db.SupplierInviteRecords).IsUsed);
        Assert.Equal(result.Org.Id, (await db.Users.SingleAsync(u => u.Id == SupplierUserId)).SupplierOrgId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterAsync_MarketplaceChangesWhileInviteIsPending_KeepsOriginalBeneficiary(bool publishedAtInvite)
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        var supplierOrgId = AddRegisteredSupplier(db, SupplierEmail, linkedUserId: SupplierUserId);
        var profile = db.SupplierProfiles.Local.Single(p => p.OrgId == supplierOrgId);
        profile.GuestPortalMarketplaceEnabled = publishedAtInvite;
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();
        await CreateTrustedSupplierService(db, queue)
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId);
        var token = InviteToken(Assert.Single(queue.Queued).Content.HtmlBody);

        profile.GuestPortalMarketplaceEnabled = !publishedAtInvite;
        await db.SaveChangesAsync();
        var result = await CreateSupplierService(db, queue).RegisterAsync(
            new SupplierRegistration(SupplierEmail, "Pulizie Test", "+390000000000", "058091", token, SupplierUserId, SupplierEmail));

        Assert.Equal(supplierOrgId, result.Org.Id);
        var relation = Assert.Single(db.TrustedSuppliers);
        Assert.Equal(HostOrgId, relation.OrgId);
        Assert.Equal(supplierOrgId, relation.SupplierOrgId);
        Assert.Equal(!publishedAtInvite, relation.HostOrigin);
        Assert.True(Assert.Single(db.SupplierInviteRecords).IsUsed);
        Assert.Single(db.SupplierProfiles);
    }

    [Fact]
    public async Task RegisterAsync_HostInviteOpenedByAnotherRegisteredSupplier_Throws422AndAddsNothing()
    {
        await using var db = CreateDbContext();
        AddProperty(db, HostOrgId, "Roma", "058091");
        AddRegisteredSupplier(db, "other@test.com", linkedUserId: SupplierUserId);
        await db.SaveChangesAsync();
        var queue = new RecordingEmailQueue();
        await CreateTrustedSupplierService(db, queue)
            .InviteSupplierAsync(HostOrgId, new InviteSupplierRequest(SupplierEmail, []), HostUserId);
        var token = InviteToken(Assert.Single(queue.Queued).Content.HtmlBody);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSupplierService(db, queue).RegisterAsync(
            new SupplierRegistration(SupplierEmail, "Pulizie Test", "+390000000000", "058091", token, SupplierUserId, "other@test.com")));

        Assert.Equal("supplier_invite_email_mismatch", ex.Code);
        Assert.Empty(db.TrustedSuppliers);
        Assert.False(Assert.Single(db.SupplierInviteRecords).IsUsed);
    }

    [Theory]
    [InlineData("vat")]
    [InlineData("fiscal")]
    [InlineData("phone")]
    public async Task AlternativeIdentifier_RequiresStaffConfirmationAndTheExistingAccount(string field)
    {
        await using var db = CreateDbContext();
        db.Orgs.Add(new Casazen.Core.Entities.Org { Id = HostOrgId, Name = "Host", Slug = "host" });
        AddProperty(db, HostOrgId, "Roma", "058091");
        var id = AddRegisteredSupplier(db, SupplierEmail, SupplierUserId);
        var profile = db.SupplierProfiles.Local.Single(p => p.OrgId == id);
        profile.Phone = "(+39) 333-1234567";
        profile.VatNumber = "IT12345678901";
        db.Orgs.Local.Single(o => o.Id == id).BillingFiscalCode = "RSSMRA80A01H501U";
        await db.SaveChangesAsync();
        var q = new RecordingEmailQueue();
        var svc = CreateSupplierService(db, q);
        var request = new InviteSupplierRequest("other-address@example.test", [],
            field == "vat" ? "12345678901" : null, field == "fiscal" ? "rssmra80a01h501u" : null,
            field == "phone" ? "0039 333 1234567" : null);
        var receipt = await CreateTrustedSupplierService(db, q).InviteSupplierAsync(HostOrgId, request, HostUserId);
        Assert.Empty(q.Queued);
        Assert.Empty(db.TrustedSuppliers);
        var review = Assert.Single(await svc.ListIdentityReviewsAsync(1, default));
        Assert.Equal(receipt.InviteId, review.Id);
        Assert.Equal(field, Assert.Single(Assert.Single(review.Candidates).MatchedBy));
        await Assert.ThrowsAsync<DomainRuleException>(() => svc.ResolveIdentityReviewAsync(review.Id, "existing", Guid.NewGuid(), "staff", default));
        await svc.ResolveIdentityReviewAsync(review.Id, "existing", id, "staff", default);
        Assert.Empty(await svc.ListIdentityReviewsAsync(1, default));
        Assert.Equal(SupplierEmail, (await db.SupplierInviteRecords.SingleAsync()).Email);
        var token = InviteToken(Assert.Single(q.Queued).Content.HtmlBody);
        await Assert.ThrowsAsync<DomainConflictException>(() => svc.ResolveIdentityReviewAsync(review.Id, "reject", null, "staff", default));
        Assert.Empty(db.TrustedSuppliers);
        await svc.RegisterAsync(new SupplierRegistration(SupplierEmail, "Pulizie Test", "+390000000000", "058091",
            token, SupplierUserId, SupplierEmail));
        Assert.Equal(id, Assert.Single(db.TrustedSuppliers).SupplierOrgId);
        Assert.Single(db.SupplierProfiles);
    }

    [Fact]
    public async Task ConflictingEmailAndPhone_AreHeldUntilStaffRejects_WithoutSendingEitherAddress()
    {
        await using var db = CreateDbContext();
        db.Orgs.Add(new Casazen.Core.Entities.Org { Id = HostOrgId, Name = "Host", Slug = "host" });
        AddProperty(db, HostOrgId, "Roma", "058091");
        AddRegisteredSupplier(db, SupplierEmail);
        var otherId = AddRegisteredSupplier(db, "other@test.com");
        db.SupplierProfiles.Local.Single(p => p.OrgId == otherId).Phone = "+39 333 1234567";
        await db.SaveChangesAsync();
        var q = new RecordingEmailQueue();
        var svc = CreateSupplierService(db, q);
        var request = new InviteSupplierRequest(SupplierEmail, [], Phone: "+39 333 1234567");
        var receipt = await CreateTrustedSupplierService(db, q).InviteSupplierAsync(HostOrgId, request, HostUserId);
        Assert.Equal(2, Assert.Single(await svc.ListIdentityReviewsAsync(1, default)).Candidates.Count);
        await Assert.ThrowsAsync<DomainConflictException>(() => CreateTrustedSupplierService(db, q).InviteSupplierAsync(HostOrgId, request, HostUserId));
        await Assert.ThrowsAsync<DomainRuleException>(() => svc.ResolveIdentityReviewAsync(receipt.InviteId, "distinct", null, "staff", default));
        await svc.ResolveIdentityReviewAsync(receipt.InviteId, "reject", null, "staff", default);
        Assert.Empty(q.Queued);
        Assert.Empty(db.TrustedSuppliers);
        Assert.NotNull((await db.SupplierInviteRecords.SingleAsync()).RevokedAt);
    }

    private static Property AddProperty(AppDbContext db, Guid orgId, string city, string? istatCode)
    {
        var property = new Property { OrgId = orgId, Name = $"Casa {city}", City = city, ComuneIstatCode = istatCode };
        db.Properties.Add(property);
        return property;
    }

    /// <summary>A supplier org with its profile; with <paramref name="linkedUserId"/>, the account linked to it.</summary>
    private static Guid AddRegisteredSupplier(AppDbContext db, string email, string? linkedUserId = null)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Fornitore Test", Slug = $"supplier-{Guid.NewGuid():N}"[..30], OrgType = OrgType.Supplier };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile { OrgId = org.Id, Email = email, LegalName = "Fornitore Test", Phone = "+390000000000" });
        if (linkedUserId is not null)
            db.Users.Add(new User { Id = linkedUserId, Email = email, SupplierOrgId = org.Id });
        return org.Id;
    }

    private static string InviteToken(string html)
    {
        var match = Regex.Match(html, "inviteToken=([0-9a-f]+)");
        Assert.True(match.Success, "The email has no invite link.");
        return match.Groups[1].Value;
    }

    private static TrustedSupplierService CreateTrustedSupplierService(AppDbContext db, RecordingEmailQueue queue) =>
        new(db, CreateSupplierService(db, queue), new ActivityLog(db), NullLogger<TrustedSupplierService>.Instance);

    private static SupplierService CreateSupplierService(AppDbContext db, RecordingEmailQueue queue) =>
        new(
            db,
            queue,
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            ComuneTestServices.Pilots(db),
            ComuneTestServices.Directory(db),
            ComuneTestServices.Matcher(db),
            LegalTestServices.Legal(),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
