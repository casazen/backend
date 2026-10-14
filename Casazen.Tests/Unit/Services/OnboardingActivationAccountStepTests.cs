using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The "Account e consensi" step of the activation checklist and the host onboarding gate answer the same question. The
/// gate is what sends a user to /onboarding and what the wizard reads (via /users/me) to choose between a plain update
/// and the consents step: if the checklist computed the rule on its own, a step shown as "todo" could be one the wizard
/// considers finished, and the host would loop between the dashboard and the wizard forever.
/// </summary>
public class OnboardingActivationAccountStepTests : IDisposable
{
    private const string UserId = "auth0|host-1";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly Mock<IHostOnboardingGate> _gate = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task GetActivationStatusAsync_UserWithOrg_AccountStepFollowsTheGate(
        bool gateOnboardingCompleted, bool gateConsentsAccepted, bool expectedDone)
    {
        // Arrange: the user row alone says "onboarded, with an org"; only the gate decides.
        SeedUserWithOrg(onboardingCompletedAt: DateTime.UtcNow);
        GateReturns(gateOnboardingCompleted, gateConsentsAccepted);

        // Act
        var status = await NewService().GetActivationStatusAsync(UserId, CancellationToken.None);

        // Assert
        var account = AccountStep(status);
        Assert.Equal(expectedDone ? ActivationStepStates.Done : ActivationStepStates.Todo, account.State);
        Assert.Equal(gateConsentsAccepted, status.ConsentsAccepted);
    }

    [Fact]
    public async Task GetActivationStatusAsync_UserRowCompletedButGateIncomplete_AccountStepIsTodo()
    {
        // The documents changed after the host accepted them: the gate asks for the consents again, the checklist must too.
        SeedUserWithOrg(onboardingCompletedAt: DateTime.UtcNow.AddMonths(-3));
        GateReturns(onboardingCompleted: true, consentsAccepted: false);

        var status = await NewService().GetActivationStatusAsync(UserId, CancellationToken.None);

        var account = AccountStep(status);
        Assert.Equal(ActivationStepStates.Todo, account.State);
        Assert.Equal(ActivationStepReasons.OnboardingIncomplete, account.Reason);
    }

    [Fact]
    public async Task GetActivationStatusAsync_GateCompleteButNoOrg_AccountStepIsTodo()
    {
        _db.Users.Add(new User { Id = UserId, Email = "host@example.com", OrgId = null, OnboardingCompletedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        GateReturns(onboardingCompleted: true, consentsAccepted: true);

        var status = await NewService().GetActivationStatusAsync(UserId, CancellationToken.None);

        Assert.Equal(ActivationStepStates.Todo, AccountStep(status).State);
    }

    [Fact]
    public async Task GetActivationStatusAsync_UnknownUser_AccountStepIsTodoAndGateIsNotAsked()
    {
        var status = await NewService().GetActivationStatusAsync("auth0|nobody", CancellationToken.None);

        Assert.Equal(ActivationStepStates.Todo, AccountStep(status).State);
        _gate.Verify(g => g.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SeedUserWithOrg(DateTime? onboardingCompletedAt)
    {
        var org = new OrgEntity
        {
            Name = "casa-lago",
            Slug = "casa-lago",
            DisplayName = "Casa Lago",
            ContactEmail = "host@casa-lago.test",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        _db.Orgs.Add(org);
        _db.Users.Add(new User
        {
            Id = UserId,
            Email = "host@example.com",
            OrgId = org.Id,
            RentalType = RentalType.ShortTerm,
            OnboardingCompletedAt = onboardingCompletedAt,
        });
        _db.SaveChanges();
    }

    private void GateReturns(bool onboardingCompleted, bool consentsAccepted) =>
        _gate.Setup(g => g.GetStatusAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostOnboardingStatus(onboardingCompleted, consentsAccepted));

    private OnboardingService NewService() =>
        new(
            _db,
            Mock.Of<ILegalDocumentService>(),
            Mock.Of<IUserAuthorizationCache>(),
            _gate.Object,
            EmailTestHelpers.Links());

    private static ActivationChecklistStep AccountStep(OnboardingActivationStatus status) =>
        status.Steps.Single(s => s.Key == ActivationStepKeys.Account);
}
