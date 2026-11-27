using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E08/F01/US02/T01 (NF-74, NF-80, D7): Octorate's availability check of the room a property books into, with the quota of the org in mind.
/// Only a mirrored property is asked, an answer is remembered 60 seconds for the property, its room and the dates (and what went wrong is not),
/// and an Octorate that cannot be asked never refuses: the question is advice, the answer "not available" is the only one that stops a booking.
/// </summary>
public class ChannelManagerAvailabilityServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 11, 10);
    private static readonly DateOnly To = new(2026, 11, 13);

    private readonly Mock<IOctorateBookingTargets> _targets = new();
    private readonly Mock<IOctorateApiClient> _client = new();
    private readonly Mock<IOctorateOrgScope> _scope = new();
    private readonly TestFeatureFlags _flags = new(FeatureFlags.OctorateConnector);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public ChannelManagerAvailabilityServiceTests()
    {
        _targets
            .Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("acc-1", "room-1")));
        _scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
    }

    [Fact]
    public async Task CheckAsync_OctorateSaysNotAvailable_AnswersItForTheRoomOfTheMappingAndTheOrgOfTheProperty()
    {
        ClientAnswers(new OctorateAvailabilityResult(false, "Not sellable"));

        var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.True(answer.IsNotAvailable);
        Assert.True(answer.Checked);
        Assert.Equal("Not sellable", answer.Reason);
        _scope.Verify(s => s.Use(OrgId), Times.Once);
        _client.Verify(
            c => c.CheckAvailabilityAsync(
                It.Is<OctorateBookingTarget>(t => t.AccommodationId == "acc-1" && t.ProductId == "room-1"),
                From,
                To,
                2,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAsync_OctorateSaysAvailable_AnswersItAndDoesNotRefuse()
    {
        ClientAnswers(new OctorateAvailabilityResult(true, null));

        var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 1, Ct);

        Assert.Equal(ChannelManagerAnswer.Available, answer.Answer);
        Assert.False(answer.IsNotAvailable);
        Assert.True(answer.Checked);
    }

    [Fact]
    public async Task CheckAsync_TheSameQuestionAgain_CostsOneCall_AndOtherDatesAreAnotherQuestion()
    {
        ClientAnswers(new OctorateAvailabilityResult(false, null));

        // The same property, room and dates: the guest who tries again, the host who goes back and forth, whoever is the one asking.
        await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);
        await Service().CheckAsync(PropertyId, OrgId, From, To, 4, Ct);
        var again = await Service().CheckAsync(PropertyId, OrgId, From, To, 1, Ct);
        VerifyClientCalls(Times.Once());
        Assert.True(again.IsNotAvailable);

        await Service().CheckAsync(PropertyId, OrgId, From, To.AddDays(1), 2, Ct);
        await Service().CheckAsync(PropertyId, OrgId, From.AddDays(1), To, 2, Ct);
        VerifyClientCalls(Times.Exactly(3));
    }

    [Fact]
    public async Task CheckAsync_WhatWentWrong_IsNotRemembered()
    {
        _client
            .SetupSequence(c => c.CheckAvailabilityAsync(
                It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503))
            .ReturnsAsync(new OctorateAvailabilityResult(true, null));

        var failed = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);
        var answered = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.Unanswered, failed.Answer);
        Assert.False(failed.Checked);
        Assert.Equal(ChannelManagerAnswer.Available, answered.Answer);
        Assert.True(answered.Checked);
        VerifyClientCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task CheckAsync_AnOctorateThatCannotBeAsked_NeverRefuses()
    {
        var errors = new OctorateException[]
        {
            new OctorateServerException(null),
            new OctorateServerException(500),
            new OctorateRateLimitException(429),
            new OctorateRateLimitException(null),
            new OctorateNotFoundException(),
            new OctorateConflictException(),
            OctorateAuthException.Refused(403),
            OctorateAuthException.NotConnected(),
            new OctorateRequestRejectedException(400),
        };

        foreach (var error in errors)
        {
            _client
                .Setup(c => c.CheckAvailabilityAsync(
                    It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(error);

            var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

            Assert.Equal(ChannelManagerAnswer.Unanswered, answer.Answer);
            Assert.False(answer.IsNotAvailable);
            Assert.False(answer.Checked);
        }
    }

    [Fact]
    public async Task CheckAsync_TheLookupOfTheTargetFailsOnOctorate_NeverRefuses()
    {
        // The lookup reads the rooms of the accommodation on Octorate when the mapping holds none.
        _targets
            .Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(null));

        var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.Unanswered, answer.Answer);
        VerifyClientCalls(Times.Never());
    }

    [Fact]
    public async Task CheckAsync_APropertyThatIsNotMirrored_AsksNothingAndSaysNothing()
    {
        _targets
            .Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OctorateTargetLookup.NotMirrored);

        var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.NotMirrored, answer.Answer);
        Assert.Null(answer.Checked);
        Assert.False(answer.IsNotAvailable);
        VerifyClientCalls(Times.Never());
    }

    [Fact]
    public async Task CheckAsync_AMappingWithNoRoomChosen_CannotBeAsked_AndNothingIsCalled()
    {
        _targets
            .Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OctorateTargetLookup.RoomNotChosen);

        var answer = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.Unanswered, answer.Answer);
        Assert.False(answer.Checked);
        VerifyClientCalls(Times.Never());
    }

    [Fact]
    public async Task CheckAsync_TheConnectorOff_OrDatesThatAreNotAStay_AskNothing()
    {
        _flags.Set(FeatureFlags.OctorateConnector, false);

        var off = await Service().CheckAsync(PropertyId, OrgId, From, To, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.NotMirrored, off.Answer);
        _targets.Verify(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        _flags.Set(FeatureFlags.OctorateConnector, true);
        var noNight = await Service().CheckAsync(PropertyId, OrgId, From, From, 2, Ct);

        Assert.Equal(ChannelManagerAnswer.NotMirrored, noNight.Answer);
        _targets.Verify(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyClientCalls(Times.Never());
    }

    [Fact]
    public async Task CheckAsync_ARequestThatIsCancelled_IsNotAnswered()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _client
            .Setup(c => c.CheckAvailabilityAsync(
                It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Service().CheckAsync(PropertyId, OrgId, From, To, 2, cancelled.Token));
    }

    private ChannelManagerAvailabilityService Service() =>
        new(_targets.Object, _client.Object, _scope.Object, _flags, _cache, NullLogger<ChannelManagerAvailabilityService>.Instance);

    private void ClientAnswers(OctorateAvailabilityResult answer) =>
        _client
            .Setup(c => c.CheckAvailabilityAsync(
                It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);

    private void VerifyClientCalls(Times times) =>
        _client.Verify(
            c => c.CheckAvailabilityAsync(
                It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            times);
}
