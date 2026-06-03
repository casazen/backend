using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Serialization;

public class LeaseContractSerializationTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void SerializeLeaseWithParties_DoesNotThrow()
    {
        var leaseId = Guid.NewGuid();
        var lease = new LeaseContract
        {
            Id = leaseId,
            PropertyId = Guid.NewGuid(),
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddYears(4),
            MonthlyRent = 1200m,
            RegistrationDeadline = DateTime.UtcNow.AddDays(30),
            DataRetentionUntil = DateTime.UtcNow.AddYears(10),
            Property = new Property
            {
                Id = Guid.NewGuid(),
                Name = "Test Property",
                OwnerId = "auth0|owner",
                Address = "Via Roma 1",
                City = "Rome",
            },
            Parties =
            [
                new Party
                {
                    Id = Guid.NewGuid(),
                    LeaseContractId = leaseId,
                    Role = PartyRole.Tenant,
                    FirstName = "Mario",
                    LastName = "Rossi",
                    FiscalCode = "RSSMRA80A01H501Z",
                    Citizenship = "IT",
                    ContactEmail = "mario@example.com",
                },
            ],
        };

        lease.Parties.First().LeaseContract = lease;

        var json = JsonSerializer.Serialize(lease, ApiJsonOptions);

        Assert.Contains("\"parties\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"leaseContract\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
