using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class SupplierIdentityReviewIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task MatchedPrivateContact_OnlyStaffCanInspectAndConfirm()
    {
        var owner = $"auth0|host-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        var phone = $"+39333{Random.Shared.Next(1000000, 9999999)}";
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        var alias = $"alias-{Guid.NewGuid():N}@example.test";
        var supplierId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Orgs.Add(new OrgEntity { Id = supplierId, Slug = $"supplier-{Guid.NewGuid():N}"[..28], Name = "Private supplier", OrgType = OrgType.Supplier });
            db.SupplierProfiles.Add(new SupplierProfile { OrgId = supplierId, Email = email, Phone = phone,
                LegalName = "Private supplier", Status = SupplierStatus.Active });
            var selected = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id);
            selected.City = "Roma";
            await db.SaveChangesAsync();
        }

        using var host = factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        using var stranger = factory.CreateAuthenticatedClient($"auth0|other-{Guid.NewGuid():N}", "PropertyOwner");
        using var staff = factory.CreateAuthenticatedClient($"auth0|staff-{Guid.NewGuid():N}", "Admin");
        var sent = await host.PostAsJsonAsync("/api/trusted-suppliers/invite", new { email = alias, phone, propertyIds = new[] { property.Id } });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var receipt = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "expiresAt", "inviteId" }, receipt.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var id = receipt.GetProperty("inviteId").GetGuid();
        const string queue = "/api/admin/suppliers/identity-reviews";
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(queue)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync($"{queue}/{id}/decision", new { action = "existing", supplierOrgId = supplierId })).StatusCode);
        var reviews = await staff.GetFromJsonAsync<JsonElement>(queue);
        var candidate = reviews.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id)
            .GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal(email, candidate.GetProperty("email").GetString());
        Assert.Equal("phone", candidate.GetProperty("matchedBy")[0].GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsJsonAsync($"{queue}/{id}/decision", new { action = "existing", supplierOrgId = supplierId })).StatusCode);

        using var verification = factory.Services.CreateScope();
        var data = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var invite = await data.SupplierInviteRecords.AsNoTracking().SingleAsync(i => i.Id == id);
        Assert.Equal(email, invite.Email);
        Assert.Equal(supplierId, invite.IdentifiedSupplierOrgId);
        Assert.NotNull(invite.TokenHash);
        Assert.Null(invite.IdentityReviewJson);
        Assert.False(await data.TrustedSuppliers.IgnoreQueryFilters().AnyAsync(t => t.SupplierOrgId == supplierId));
    }
}
