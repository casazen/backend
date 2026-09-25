using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class RliChecklistServiceTests
{
    private const string OwnerId = "auth0|owner";

    [Fact]
    public async Task GetAsync_ExtraEuTenant_IncludesQuesturaItem()
    {
        var lease = BuildLease(extraEu: true);
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        Assert.Contains(result.Items, i => i.Key == RliChecklistKeys.QuesturaExtraEu);
        Assert.Equal("2026-08-rli-delega-bozza", result.TosVersion);
    }

    [Fact]
    public async Task GetAsync_EuOnly_OmitsQuesturaItem()
    {
        var lease = BuildLease(extraEu: false);
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        Assert.DoesNotContain(result.Items, i => i.Key == RliChecklistKeys.QuesturaExtraEu);
        Assert.Null(result.Questura);
    }

    [Fact]
    public async Task GetAsync_ExtraEuTenantAfterReminderEmails_QuesturaItemNotDone()
    {
        // LT-07 (A7-08): the old notice ("extra-eu") and the new reminders are emails CasaZen sent, not the communication.
        var lease = BuildLease(extraEu: true);
        var sut = CreateSut(lease, events:
        [
            new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.DeadlineReminderSent, Payload = "extra-eu" },
            new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.DeadlineReminderSent, Payload = "questura-delivery:2026-09-03" },
        ]);

        var result = await sut.GetAsync(lease);

        Assert.False(result.Items.Single(i => i.Key == RliChecklistKeys.QuesturaExtraEu).Done);
        Assert.Null(result.Questura!.CommunicationDate);
    }

    [Fact]
    public async Task GetAsync_QuesturaCommunicationDeclared_ItemDoneWithDateAndReceipt()
    {
        var lease = BuildLease(extraEu: true);
        lease.QuesturaCommunicationDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        lease.QuesturaCommunicationReceiptPath = $"leases/{Guid.NewGuid()}/{lease.Id}/questura/r.pdf";
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        Assert.True(result.Items.Single(i => i.Key == RliChecklistKeys.QuesturaExtraEu).Done);
        Assert.Equal(lease.QuesturaCommunicationDate, result.Questura!.CommunicationDate);
        Assert.True(result.Questura.HasReceipt);
    }

    [Fact]
    public async Task GetAsync_ExtraEuWithoutDeclaredDelivery_DeadlineIsStartDatePlus48Hours()
    {
        // Start 1/9 = delivery by default: the 48 hours end at the latest on 3/9. Today 2/9 in Rome (22:30 UTC of 1/9).
        var lease = BuildLease(extraEu: true);
        var sut = CreateSut(lease, clock: new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 22, 30, 0, TimeSpan.Zero)));

        var result = await sut.GetAsync(lease);

        var questura = result.Questura!;
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), questura.DeliveryDate);
        Assert.False(questura.DeliveryDateDeclared);
        Assert.Equal(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), questura.Deadline);
        Assert.Equal(1, questura.DaysRemaining);
        Assert.False(questura.HasReceipt);
    }

    [Fact]
    public async Task GetAsync_DeclaredDeliveryDate_DeadlineCountsFromIt()
    {
        var lease = BuildLease(extraEu: true);
        lease.PropertyDeliveryDate = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var sut = CreateSut(lease, clock: new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));

        var result = await sut.GetAsync(lease);

        Assert.True(result.Questura!.DeliveryDateDeclared);
        Assert.Equal(new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc), result.Questura.Deadline);
        Assert.Equal(-2, result.Questura.DaysRemaining);
    }

    [Fact]
    public async Task GetAsync_RejectedLeaseWithExtraEuTenant_NoQuesturaItem()
    {
        var lease = BuildLease(extraEu: true, LeaseStatus.Rejected);
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        Assert.DoesNotContain(result.Items, i => i.Key == RliChecklistKeys.QuesturaExtraEu);
        Assert.Null(result.Questura);
    }

    [Fact]
    public async Task GetAsync_SignedLease_ReturnsOnlyKnownKeysWithoutTexts()
    {
        var lease = BuildLease(extraEu: true);
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        // Labels are localized by the API from the key (A7-26): the service returns no Italian text.
        Assert.All(result.Items, item => Assert.Contains(item.Key, RliChecklistKeys.All));
        Assert.True(result.Items.Single(i => i.Key == RliChecklistKeys.ContractSigned).Done);
        Assert.False(result.Items.Single(i => i.Key == RliChecklistKeys.RliRegistered).Done);
    }

    [Theory]
    [InlineData(LeaseStatus.RegistrationPending, RegistrationStatus.Pending)]
    [InlineData(LeaseStatus.SentToProvider, RegistrationStatus.SentToProvider)]
    public async Task GetAsync_ProviderSubmissionInProgress_RegistrationItemNotTicked(
        LeaseStatus leaseStatus, RegistrationStatus registrationStatus)
    {
        // A7-01: "sent to the filing channel" was ticked as if the contract were registered.
        var lease = BuildLease(extraEu: false, leaseStatus);
        lease.Registration = new LeaseRegistration { LeaseContractId = lease.Id, Status = registrationStatus };
        var sut = CreateSut(lease, providerAvailable: true);

        var result = await sut.GetAsync(lease);

        var registered = result.Items.Single(i => i.Key == RliChecklistKeys.RliRegistered);
        Assert.False(registered.Done);
        Assert.False(registered.Failed);
        Assert.DoesNotContain(result.Items, i => i.Key == "rli_submitted");
    }

    [Fact]
    public async Task GetAsync_RegistrationFailed_ItemFailedAndNotTicked()
    {
        var lease = BuildLease(extraEu: false);
        lease.Registration = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.Failed,
            FailureCode = RliRegistrationFailureCodes.ProviderError,
        };
        var sut = CreateSut(lease, providerAvailable: true);

        var result = await sut.GetAsync(lease);

        var registered = result.Items.Single(i => i.Key == RliChecklistKeys.RliRegistered);
        Assert.False(registered.Done);
        Assert.True(registered.Failed);
    }

    [Fact]
    public async Task GetAsync_RegisteredWithReceipt_ItemTicked()
    {
        var lease = BuildLease(extraEu: false, LeaseStatus.Registered);
        lease.Registration = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.Registered,
            Channel = RegistrationChannel.Manual,
            ReceiptStoragePath = "leases/org/lease/registration/receipt.pdf",
        };
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease);

        Assert.True(result.Items.Single(i => i.Key == RliChecklistKeys.RliRegistered).Done);
    }

    [Fact]
    public async Task GetAsync_ProviderPathUnavailable_NoDelegaItemAndFlagFalse()
    {
        var lease = BuildLease(extraEu: false);
        var sut = CreateSut(lease, providerAvailable: false);

        var result = await sut.GetAsync(lease);

        Assert.False(result.ProviderFilingAvailable);
        Assert.DoesNotContain(result.Items, i => i.Key == RliChecklistKeys.DelegaCaptured);
    }

    [Fact]
    public async Task GetAsync_ProviderPathAvailable_ListsDelegaItem()
    {
        var lease = BuildLease(extraEu: false);
        var sut = CreateSut(lease, providerAvailable: true);

        var result = await sut.GetAsync(lease);

        Assert.True(result.ProviderFilingAvailable);
        Assert.False(result.Items.Single(i => i.Key == RliChecklistKeys.DelegaCaptured).Done);
    }

    [Fact]
    public async Task GetAsync_FlagOnButProviderNotConfigured_ProviderPathUnavailable()
    {
        var lease = BuildLease(extraEu: false);
        var sut = CreateSut(lease, providerAvailable: false, flagOn: true);

        var result = await sut.GetAsync(lease);

        Assert.False(result.ProviderFilingAvailable);
    }

    [Fact]
    public async Task GetAsync_SignedBeforeStart_DeadlineFromStipulaAndDaysOnRomeCalendar()
    {
        // LT-04 (A7-04): signed 1/8, start 1/10 → deadline 31/8; today 24/8 (00:30 in Rome) → 7 days.
        var lease = BuildLease(extraEu: false);
        lease.StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        lease.RecordStipula(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 22, 30, 0, TimeSpan.Zero));
        var sut = CreateSut(lease, clock: clock);

        var result = await sut.GetAsync(lease);

        Assert.Equal(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), result.RegistrationDeadline);
        Assert.Equal(7, result.DaysRemaining);
    }

    [Fact]
    public async Task GetAsync_DeadlinePassed_NegativeDaysRemaining()
    {
        var lease = BuildLease(extraEu: false);
        lease.StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        lease.RecordStipula(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        var sut = CreateSut(lease, clock: new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));

        var result = await sut.GetAsync(lease);

        Assert.Equal(-1, result.DaysRemaining);
    }

    [Theory]
    [InlineData(LeaseStatus.Signed)]
    [InlineData(LeaseStatus.AwaitingSignature)]
    public async Task GetAsync_NoStipulaAndStartAhead_DeadlineToBeDetermined(LeaseStatus status)
    {
        // Signed without a recorded stipula (older lease), or not signed yet with the start date ahead: no deadline.
        var lease = BuildLease(extraEu: false, status);
        lease.StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var sut = CreateSut(lease, clock: new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero)));

        var result = await sut.GetAsync(lease);

        Assert.Null(result.RegistrationDeadline);
        Assert.Null(result.DaysRemaining);
    }

    private static RliChecklistService CreateSut(
        LeaseContract lease,
        bool providerAvailable = false,
        bool? flagOn = null,
        TimeProvider? clock = null,
        IReadOnlyList<LeaseEvent>? events = null)
    {
        var auths = new Mock<ILeaseRegistrationAuthorizationRepository>();
        auths.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync((LeaseRegistrationAuthorization?)null);
        var eventRepository = new Mock<ILeaseEventRepository>();
        eventRepository.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync(events?.ToList() ?? []);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.RliProvider)).Returns(flagOn ?? providerAvailable);
        var provider = new Mock<ILeaseRegistrationProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(providerAvailable);
        return new RliChecklistService(
            auths.Object,
            eventRepository.Object,
            Options.Create(new RliOptions { TosVersion = "2026-08-rli-delega-bozza", AttestationText = "bozza" }),
            flags.Object,
            provider.Object,
            clock);
    }

    private static LeaseContract BuildLease(bool extraEu, LeaseStatus status = LeaseStatus.Signed)
    {
        var property = new Property { OwnerId = OwnerId, City = "Milano", Name = "X" };
        return new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Property = property,
            Parties =
            [
                new Party
                {
                    Role = PartyRole.Tenant,
                    FirstName = "A",
                    LastName = "B",
                    FiscalCode = "XXXXXX00A00A000X",
                    Citizenship = extraEu ? "US" : "IT",
                    ContactEmail = "t@example.com",
                    IsExtraEU = extraEu,
                },
            ],
        };
    }
}
