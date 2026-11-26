using System.Collections.Concurrent;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class CampaignDeliveryConcurrencyPostgresTests
{
    [PostgresFact]
    public async Task CampaignsAndRetriesShareTheGuestsLastMonthlySlot()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync();
        var org = new OrgEntity { Name = "Marketing", DisplayName = "Marketing", Slug = $"marketing-{Guid.NewGuid():N}",
            ContactEmail = "host@example.test", IsActive = true };
        var segment = new GuestSegment { OrgId = org.Id, Name = "All", ConditionJson = """{"Version":"1.0","Groups":[]}""" };
        var campaigns = Enumerable.Range(0, 3).Select(i => new Campaign { OrgId = org.Id, SegmentId = segment.Id,
            Subject = $"Issue {i}", Body = "<p>Issue</p>", Status = CampaignStatus.Approved }).ToArray();
        await using (var db = database.CreateContext())
        {
            db.Orgs.Add(org);
            db.GuestSegments.Add(segment);
            db.Campaigns.AddRange(campaigns);
            db.Set<MarketingStudioSettings>().Add(new() { OrgId = org.Id, MonthlyEmailLimit = 1 });
            CampaignServiceTests.AddContact(db, org.Id, "subscriber@example.test");
            await db.SaveChangesAsync();
        }
        var calls = new ConcurrentBag<Guid>();
        var sender = new Mock<IEmailService>();
        sender.Setup(s => s.SendCampaignEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, string, Guid>((_, _, _, _, _, key) => calls.Add(key))
            .ReturnsAsync(EmailSendResult.Sent());
        async Task Run(Guid id)
        {
            await using var db = database.CreateContext();
            await CampaignServiceTests.NewService(db, sender.Object, CampaignServiceTests.Links()).SendCampaignAsync(id);
        }
        await Task.WhenAll(campaigns.Select(c => Run(c.Id)));
        await Task.WhenAll(campaigns.Select(c => Run(c.Id)));
        Assert.Single(calls);
        await using var verify = database.CreateContext();
        var delivery = await verify.CampaignDeliveries.SingleAsync(d => d.OrgId == org.Id);
        Assert.Equal("Sent", delivery.Status);
        Assert.Equal(delivery.Id, calls.Single());
    }
}
