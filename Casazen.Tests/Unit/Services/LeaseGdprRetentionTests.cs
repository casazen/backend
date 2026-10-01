using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LeaseGdprRetentionTests
{
    private const string OwnerId = "auth0|gdpr-lease-owner";
    private static readonly Guid PropertyId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task CreateDraftAsync_NewLease_StoresNoRetentionDateNorErasure()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var property = new Property { Id = PropertyId, OwnerId = OwnerId, OrgId = Guid.NewGuid(), Name = "GDPR Property" };
        var properties = new Mock<IPropertyRepository>();
        properties.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        var ape = new Mock<IApeComplianceService>();
        ape.Setup(s => s.EnsurePropertyHasValidApeAsync(PropertyId)).Returns(Task.CompletedTask);
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.AddAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var sut = new LeaseWorkflowService(
            leases.Object,
            events.Object,
            properties.Object,
            ape.Object,
            Mock.Of<ICanoneConcordatoEligibilityService>(),
            Mock.Of<ILogger<LeaseWorkflowService>>());

        var result = await sut.CreateDraftAsync(PropertyId, new CreateLeaseRequest(
            FiscalRegime.CedolareSecca,
            start,
            start.AddYears(4),
            1100m,
            [
                new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com"),
                new CreatePartyRequest(PartyRole.Tenant, "Giulia", "Verdi", "VRDGLI85B02F205X", "IT", "giulia@example.com"),
            ]));

        // LT-12 (A7-18): no stored StartDate + 10 years; the retention is counted from the end date on read.
        Assert.Null(result.PartiesAnonymizedAt);
        Assert.Null(result.RegistrationDeadline); // fixed at the stipula (LT-04), not StartDate + 30
        Assert.False(result.ErasureRequested);
        Assert.Null(result.ErasureRequestedAt);
    }

    [Theory]
    [InlineData("2030-08-31", "2030-08-31", false)]
    [InlineData("2030-08-31", "2030-09-01", true)]
    [InlineData("2030-08-31", "2026-10-01", false)]
    public void HasEnded_EndDateAndToday_EndsTheDayAfterTheEndDate(string endDate, string today, bool expected)
    {
        var end = DateTime.SpecifyKind(DateTime.Parse(endDate, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var day = DateTime.SpecifyKind(DateTime.Parse(today, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

        Assert.Equal(expected, LeasePartyPrivacyService.HasEnded(end, day));
        Assert.Equal(end.AddDays(1), LeasePartyPrivacyService.EndedFrom(end));
    }

    [Fact]
    public void AnonymizeParty_PartyWithPersonalData_KeepsOnlyRoleAndExtraEuFlag()
    {
        var party = new Party
        {
            Role = PartyRole.Tenant,
            FirstName = "Giulia",
            LastName = "Verdi",
            FiscalCode = "VRDGLI85B02F205X",
            Citizenship = "US",
            ContactEmail = "giulia@example.com",
            IsExtraEU = true,
        };
        var now = new DateTime(2031, 1, 1, 3, 0, 0, DateTimeKind.Utc);

        LeasePartyPrivacyService.AnonymizeParty(party, now);

        Assert.Equal(
            ("ANONYMIZED", "ANONYMIZED", "ANONYMIZED", string.Empty, $"ANON-{party.Id:N}@deleted.local"),
            (party.FirstName, party.LastName, party.FiscalCode, party.Citizenship, party.ContactEmail));
        Assert.Equal((PartyRole.Tenant, true, now), (party.Role, party.IsExtraEU, party.AnonymizedAt));
    }
}
