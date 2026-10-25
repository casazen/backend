using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof test for owner invitation rate limiting (ADR-011-R4).
/// Enforces 100 invitations per day per organization.
/// </summary>
public class OwnerInvitationRateLimitTests
{
    private const string ActorUserId = "auth0|admin";

    private AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"OwnerInvitationTests_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private IOwnerInvitationService NewService(AppDbContext db)
    {
        var emailQueue = new FakeEmailQueue();
        return new OwnerInvitationService(db, emailQueue, NullLogger<OwnerInvitationService>.Instance);
    }

    private async Task<(Guid OrgId, Guid PropertyOwnerId)> SeedOrgAndOwnerAsync()
    {
        await using var db = NewDb();

        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Agency",
            Slug = "test-agency",
            PlanTier = PlanTier.Agency,
            SubscriptionStatus = SubscriptionStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        var propertyOwner = new PropertyOwner
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario.rossi@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        db.Orgs.Add(org);
        db.PropertyOwners.Add(propertyOwner);
        await db.SaveChangesAsync();

        return (org.Id, propertyOwner.Id);
    }

    /// <summary>
    /// OwnerInvitation_RateLimit_Enforced - proves rate limiting (ADR-011-R4).
    /// After 100 invitations in 24 hours, the 101st is refused with 429.
    /// </summary>
    [Fact]
    public async Task OwnerInvitation_RateLimit_Enforced()
    {
        var (orgId, propertyOwnerId) = await SeedOrgAndOwnerAsync();

        // Create 100 invitations (at the limit)
        for (var i = 0; i < 100; i++)
        {
            await using var db = NewDb();
            var service = NewService(db);

            // Create a new PropertyOwner for each invitation to avoid duplicates
            var owner = new PropertyOwner
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                FirstName = $"Owner{i}",
                LastName = "Test",
                Email = $"owner{i}@example.com",
                TaxRegime = OwnerTaxRegime.Private,
                CreatedAt = DateTime.UtcNow
            };
            db.PropertyOwners.Add(owner);
            await db.SaveChangesAsync();

            await service.CreateInvitationAsync(orgId, owner.Id, ActorUserId);
        }

        // The 101st invitation should be refused
        await using var dbFinal = NewDb();
        var finalService = NewService(dbFinal);

        var finalOwner = new PropertyOwner
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = "FinalOwner",
            LastName = "Test",
            Email = "final@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };
        dbFinal.PropertyOwners.Add(finalOwner);
        await dbFinal.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(
            () => finalService.CreateInvitationAsync(orgId, finalOwner.Id, ActorUserId));

        Assert.Equal(OwnerInvitationErrors.DailyLimitReached, error.Code);
    }

    /// <summary>Fake email queue for testing - does not actually send emails.</summary>
    private class FakeEmailQueue : IEmailQueue
    {
        public bool Enqueue(string? to, EmailContent content, string template) => true;
    }
}
