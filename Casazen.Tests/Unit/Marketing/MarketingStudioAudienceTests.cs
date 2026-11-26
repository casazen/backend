using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketing;

public class MarketingStudioAudienceTests
{
    [Fact]
    public async Task StudioUsesOnlyPropertyAndEditorialInputsAndKeepsAssetsInScope()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = new Org { Name = "Host", Slug = "host" };
        var property = new Property { OrgId = org.Id, OwnerId = "host", Name = "Casa", City = "Lugano", Country = "CH" };
        var segment = new GuestSegment { OrgId = org.Id, Name = "Privato", ConditionJson = "{}" };
        db.AddRange(org, property, segment, new MarketingStudioSettings { OrgId = org.Id, Voice = "Informale",
            PropertyUrlsJson = JsonSerializer.Serialize(new Dictionary<Guid, string> { [property.Id] = "https://villa.example.org" }) });
        await db.SaveChangesAsync();
        AiRequest? sent = null;
        var provider = new Mock<IAiProvider>();
        provider.Setup(s => s.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Standard, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, AiModelTier, string, CancellationToken>((request, _, _, _) => sent = request)
            .ReturnsAsync(new AiGenerationResult("""{"subject":"Musica","body":"La aspettiamo","keywords":"","audience":"","budget":""}""", 1, 1, AiModelTier.Standard, false));
        var config = new ConfigurationBuilder().Build();
        var studio = new MarketingStudio(db, provider.Object, Mock.Of<IWebSearchClient>(), null!,
            new MarketingAiQuota(db, config, TimeProvider.System), config, Mock.Of<ICampaignService>(), TimeProvider.System, null!);
        var brief = new StudioBrief(property.Id, segment.Id, "email", "Invito al festival");
        await Assert.ThrowsAsync<DomainRuleException>(() => studio.GenerateAsync(new HostScope(Guid.NewGuid()), brief, default));
        var asset = await studio.GenerateAsync(new HostScope(org.Id), brief, default);
        Assert.Null(asset.ApprovedAt);
        Assert.Null(asset.CampaignId);
        Assert.True(asset.ExternalDestination);
        Assert.DoesNotContain(segment.Name, sent!.Prompt);
        Assert.Contains(property.City, sent.Prompt);
        Assert.Empty(await studio.Assets(new HostScope(Guid.NewGuid())).ToListAsync());
        Assert.Empty(await studio.Assets(new HostScope(org.Id, OwnerId: "other")).ToListAsync());
    }
}
