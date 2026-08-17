using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
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

        var result = await sut.GetAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.Contains(result.Items, i => i.Key == "questura_extra_eu");
        Assert.Equal("2026-08-rli-delega-bozza", result.TosVersion);
    }

    [Fact]
    public async Task GetAsync_EuOnly_OmitsQuesturaItem()
    {
        var lease = BuildLease(extraEu: false);
        var sut = CreateSut(lease);

        var result = await sut.GetAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Items, i => i.Key == "questura_extra_eu");
    }

    private static RliChecklistService CreateSut(LeaseContract lease)
    {
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        var auths = new Mock<ILeaseRegistrationAuthorizationRepository>();
        auths.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync((LeaseRegistrationAuthorization?)null);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync([]);
        return new RliChecklistService(
            leases.Object,
            auths.Object,
            events.Object,
            Options.Create(new RliOptions { TosVersion = "2026-08-rli-delega-bozza", AttestationText = "bozza" }));
    }

    private static LeaseContract BuildLease(bool extraEu)
    {
        var property = new Property { OwnerId = OwnerId, City = "Milano", Name = "X" };
        return new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            RegistrationDeadline = DateTime.UtcNow.Date.AddDays(20),
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
