using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.GuestPortal;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.GuestPortal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalGuideGenerationTests
{
    [Fact]
    public async Task FastDraftCompletesEverySectionWithoutCallingTheProvider()
    {
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        var result = await Build(ai.Object).GenerateAsync(Property(), "guide", CancellationToken.None, fast: true);
        var guide = Assert.IsType<GuestHouseGuide>(result?.Guide);
        Assert.Equal("property", result!.GuideSource);
        Assert.Equal("Dalle 15:00 alle 20:30, ora locale.", guide.CheckInHours);
        AssertComplete(guide);
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProviderDraftFillsAllSectionsWithoutSendingPrivatePropertyDetails()
    {
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        ai.Setup(provider => provider.GenerateAsync(It.Is<AiRequest>(request =>
                request.Prompt.Contains("Wi-Fi") &&
                !request.Prompt.Contains("Test property private name") &&
                !request.Prompt.Contains("Private Street") &&
                !request.Prompt.Contains("private-guide-content")),
            AiModelTier.Standard, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(
                """{"guide":{"house":"L’alloggio dispone di Wi-Fi.","wifi":"invented-password","access":"invented-code"}}""",
                12, 12, AiModelTier.Standard, FromCache: false));

        var result = await Build(ai.Object).GenerateAsync(Property(), "guide", CancellationToken.None);
        var guide = Assert.IsType<GuestHouseGuide>(result?.Guide);
        Assert.Equal("ai", result!.GuideSource);
        Assert.Equal("L’alloggio dispone di Wi-Fi.", guide.House);
        Assert.Equal("Dalle 15:00 alle 20:30, ora locale.", guide.CheckInHours);
        Assert.Equal("Entro le 10:00, ora locale.", guide.CheckOutHours);
        Assert.Contains("Parcheggio gratuito", guide.Parking);
        Assert.Contains("Wi-Fi", guide.Wifi);
        Assert.DoesNotContain("invented-password", guide.Wifi);
        Assert.DoesNotContain("invented-code", guide.Access);
        AssertComplete(guide);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "invalid response")]
    [InlineData(true, "{\"guide\":{\"house\":\"  \"}}")]
    [InlineData(true, "{\"guide\":invalid}")]
    public async Task UnavailableOrUnusableProviderLeavesACompleteDraft(bool configured, string content)
    {
        var ai = new Mock<IAiProvider>();
        ai.Setup(provider => provider.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Standard,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(content, 0, 0, AiModelTier.Standard,
                FromCache: false, ProviderConfigured: configured));

        var result = await Build(ai.Object).GenerateAsync(new Property(), "guide", CancellationToken.None);
        var guide = Assert.IsType<GuestHouseGuide>(result?.Guide);
        Assert.Equal("property", result!.GuideSource);
        Assert.Contains("non è indicata", guide.Parking);
        Assert.Contains("non è indicata", guide.Wifi);
        AssertComplete(guide);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryProviderFailureUsesThePropertyAndCanRecoverOnTheNextAttempt(bool timedOut)
    {
        var ai = new Mock<IAiProvider>();
        ai.SetupSequence(provider => provider.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Standard,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(timedOut ? new OperationCanceledException() : new HttpRequestException())
            .ReturnsAsync(new AiGenerationResult(
                """{"guide":{"house":"L’alloggio dispone di Wi-Fi."}}""",
                12, 12, AiModelTier.Standard, FromCache: false));
        var service = Build(ai.Object);
        var property = Property();

        var fallback = await service.GenerateAsync(property, "guide", CancellationToken.None);
        var recovered = await service.GenerateAsync(property, "guide", CancellationToken.None);

        Assert.Equal("property", fallback?.GuideSource);
        Assert.Equal("ai", recovered?.GuideSource);
    }

    private static Property Property() => new()
    {
        Name = "Test property private name",
        Address = "Private Street",
        GuestHouseGuideJson = "private-guide-content",
        CheckInFrom = new TimeOnly(15, 0),
        CheckInTo = new TimeOnly(20, 30),
        CheckOutBy = new TimeOnly(10, 0),
        Amenities = [PropertyAmenity.WiFi, PropertyAmenity.FreeParking],
    };

    private static GuestPortalSetupSuggestions Build(IAiProvider ai) => new(ai,
        Mock.Of<IWebSearchClient>(), new AiResponseCache(Options.Create(new AiCacheOptions())),
        Mock.Of<IComuneDirectory>(), NullLogger<GuestPortalSetupSuggestions>.Instance);

    private static void AssertComplete(GuestHouseGuide guide) =>
        Assert.All(new[] { guide.Arrival, guide.CheckInHours, guide.House, guide.Departure, guide.CheckOutHours,
                guide.Contacts, guide.LocalTips, guide.Access, guide.Wifi, guide.Parking },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
}
