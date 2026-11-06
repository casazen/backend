using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>POST api/properties/{id}/publish</c> (E02/F03/US01/T02, NF-27): a list of channels that is empty, or missing, is not a mistake of the body for the
/// model (that would be a 400) but a request the service refuses with the 422 the contract names, and it is never read as «all the channels»; a property
/// the caller does not reach is a 404 and queues nothing.
/// </summary>
public class ListingPublishControllerTests
{
    private const string Host = "auth0|host";

    private static readonly Guid OrgId = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a51");
    private static readonly Guid PropertyId = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c52");

    private readonly Mock<IContentPushService> _push = new();
    private readonly Mock<IHostResourceLookup> _resources = new();
    private readonly Mock<IAuthorizationService> _authorization = new();

    public ListingPublishControllerTests()
    {
        _resources.Setup(r => r.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, Host, PropertyId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WithNoChannels_IsNotAModelError_ItReachesTheServiceWhichRefusesItWith422(bool missing)
    {
        var request = new ListingPublishRequest { Channels = missing ? null : [] };
        Assert.True(
            Validator.TryValidateObject(request, new ValidationContext(request), [], validateAllProperties: true),
            "a body without channels must reach the service, which answers 422 listing_channels_required, and not stop at a 400 of the model");
        Allow();
        _push
            .Setup(p => p.PublishAsync(PropertyId, It.Is<IReadOnlyList<string>>(c => c.Count == 0), false, Host, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()))
            .ThrowsAsync(new DomainRuleException("listing_channels_required", "ListingChannelsRequired"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Controller().Publish(PropertyId, request, CancellationToken.None));

        Assert.Equal("listing_channels_required", ex.Code);
        _push.Verify(
            p => p.PublishAsync(PropertyId, It.Is<IReadOnlyList<string>>(c => c.Count == 0), false, Host, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Once);
    }

    [Fact]
    public async Task Publish_ForAPropertyTheCallerDoesNotReach_Answers404_AndQueuesNothing()
    {
        Deny();

        var result = await Controller().Publish(PropertyId, new ListingPublishRequest { Channels = ["booking_xml"] }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _push.Verify(
            p => p.PublishAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task Publish_ForAReadyProperty_Answers202WithTheChannelsSending()
    {
        Allow();
        _push
            .Setup(p => p.PublishAsync(PropertyId, It.IsAny<IReadOnlyList<string>>(), true, Host, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()))
            .ReturnsAsync([new ListingChannelResult("booking_xml", ChannelPublishStatus.Sending, null)]);

        var result = await Controller().Publish(PropertyId, new ListingPublishRequest { Channels = ["booking_xml"], Overwrite = true }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var body = Assert.IsType<ListingPublishResponse>(accepted.Value);
        Assert.Equal(PropertyId, body.PropertyId);
        var channel = Assert.Single(body.Channels);
        Assert.Equal("booking_xml", channel.Channel);
        Assert.Equal(ChannelPublishStatus.Sending, channel.Status);
    }

    private ListingPublishController Controller() =>
        new(_push.Object, _resources.Object, _authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Host)], "test")) },
            },
        };

    private void Allow() => SetUpAuthorization(AuthorizationResult.Success());

    private void Deny() => SetUpAuthorization(AuthorizationResult.Failed());

    private void SetUpAuthorization(AuthorizationResult result) =>
        _authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(result);
}
