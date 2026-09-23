using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LeaseContractTemplateServiceTests
{
    [Fact]
    public async Task GeneratePdfAsync_UnapprovedRegime_Throws()
    {
        var sut = CreateSut(new Dictionary<string, LeaseTemplateVariantOptions>
        {
            ["CedolareSecca"] = new() { VersionId = "v1", Approved = false },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GeneratePdfAsync(new LeaseContract { FiscalRegime = FiscalRegime.CedolareSecca }));
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedCanoneConcordato_ReturnsPdfWithBozzaAndLeaseFields()
    {
        var sut = CreateSut(new Dictionary<string, LeaseTemplateVariantOptions>
        {
            ["CanoneConcordato"] = new() { VersionId = "dev-stub", Approved = true },
        });

        var bytes = await sut.GeneratePdfAsync(BuildConcordatoLease());
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("BOZZA", text, StringComparison.Ordinal);
        Assert.Contains("431/1998", text, StringComparison.Ordinal);
        Assert.Contains("Seveso", text, StringComparison.Ordinal);
        Assert.Contains("850.00", text, StringComparison.Ordinal);
        Assert.Contains("dev-stub", text, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Luigi Verdi", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePdfAsync_ApprovedCedolareSecca_ReturnsPdfNotUtf8Stub()
    {
        var sut = CreateSut(new Dictionary<string, LeaseTemplateVariantOptions>
        {
            ["CedolareSecca"] = new() { VersionId = "dev-stub", Approved = true },
        });

        var bytes = await sut.GeneratePdfAsync(BuildGenericLease(FiscalRegime.CedolareSecca));

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("BOZZA", text, StringComparison.Ordinal);
        Assert.Contains("dev-stub", text, StringComparison.Ordinal);
        Assert.Contains("Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Luigi Verdi", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LEASE CONTRACT PDF PLACEHOLDER", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePdfAsync_CanoneConcordatoWithMultipleParties_IncludesEveryParty()
    {
        var sut = CreateSut(new Dictionary<string, LeaseTemplateVariantOptions>
        {
            ["CanoneConcordato"] = new() { VersionId = "dev-stub", Approved = true },
        });
        var lease = BuildConcordatoLease();
        lease.Parties.Add(new Party { Role = PartyRole.Landlord, FirstName = "Anna", LastName = "Bianchi", FiscalCode = "BNCNNA82A41F205Z" });
        lease.Parties.Add(new Party { Role = PartyRole.Tenant, FirstName = "Sara", LastName = "Neri", FiscalCode = "NRISRA91C45F205Y" });

        var bytes = await sut.GeneratePdfAsync(lease);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("Locatore 1: Mario Rossi", text, StringComparison.Ordinal);
        Assert.Contains("Locatore 2: Anna Bianchi", text, StringComparison.Ordinal);
        Assert.Contains("Conduttore 1: Luigi Verdi", text, StringComparison.Ordinal);
        Assert.Contains("Conduttore 2: Sara Neri", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePdfAsync_CanoneConcordatoWithoutPropertyOrParties_StillReturnsPdf()
    {
        var sut = CreateSut(new Dictionary<string, LeaseTemplateVariantOptions>
        {
            ["CanoneConcordato"] = new() { VersionId = "dev-stub", Approved = true },
        });

        var bytes = await sut.GeneratePdfAsync(new LeaseContract
        {
            FiscalRegime = FiscalRegime.CanoneConcordato,
            MonthlyRent = 500m,
        });

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("BOZZA", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    private static LeaseContractTemplateService CreateSut(
        Dictionary<string, LeaseTemplateVariantOptions> variants) =>
        new(
            Options.Create(new LeaseTemplateOptions { Variants = variants }),
            Mock.Of<ILogger<LeaseContractTemplateService>>());

    private static LeaseContract BuildConcordatoLease() => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = FiscalRegime.CanoneConcordato,
        MonthlyRent = 850m,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        Property = new Property
        {
            Name = "Condominio Il Parco",
            City = "Seveso",
            Address = "Via Roma 1",
        },
        Parties =
        [
            new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U" },
            new Party { Role = PartyRole.Tenant, FirstName = "Luigi", LastName = "Verdi", FiscalCode = "VRDLGU85B02F205X" },
        ],
    };

    private static LeaseContract BuildGenericLease(FiscalRegime regime) => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = regime,
        MonthlyRent = 1000m,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        Property = new Property
        {
            City = "Milano",
        },
        Parties =
        [
            new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U" },
            new Party { Role = PartyRole.Tenant, FirstName = "Luigi", LastName = "Verdi", FiscalCode = "VRDLGU85B02F205X" },
        ],
    };
}
