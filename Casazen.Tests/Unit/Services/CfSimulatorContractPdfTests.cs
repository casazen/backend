using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-SIM, the first proof of the whole way "a real PDF, then the reading" (NL-DOCS tested the text, never a PDF the application wrote): the
/// sample contract of the simulated module is rendered by the PDF renderer of the application, read back by the real text reader (the text
/// the PDF carries, no OCR) and by the real parser, and gives the people, the type, the rent, the deposit, the dates and the property that the
/// document was written with. A contract the local reading could not read would stop the whole round on the test environment at its
/// last steps.
/// </summary>
public sealed class CfSimulatorContractPdfTests
{
    [Fact]
    public async Task SampleContract_RenderedAndReadBack_GivesThePartiesTheDatesAndTheRent()
    {
        var today = new DateOnly(2026, 10, 25);
        var house = new CfSimulatorHouse("Via Sparano 12", "Bari", "70121", "Bari");

        var pdf = new MigraDocPdfDocumentRenderer().Render(CfSimulatorDocuments.Contract(house, today));
        var text = await new ContractTextReader().ReadAsync(pdf);
        var reading = LeaseContractTextParser.Parse(text.Text, new LeaseContractParseContext(house.Address, house.City));

        // One page of its own text: no OCR, and no footer cutting a sentence in two.
        Assert.Equal(1, text.Pages);
        Assert.Equal(0, text.PagesOcr);
        Assert.Contains(CfSimulatorDocuments.SampleNotice, text.Text, StringComparison.Ordinal);

        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        Assert.Equal(("Mario", "Rossi", CfSimulatorDocuments.LandlordFiscalCode), (landlord.FirstName, landlord.LastName, landlord.FiscalCode));
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal(("Giulia", "Verdi", CfSimulatorDocuments.TenantFiscalCode), (tenant.FirstName, tenant.LastName, tenant.FiscalCode));

        Assert.Equal(LeaseContractType.Concordato, reading.ContractType!.Value);
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, reading.MonthlyRent!.Value);
        Assert.Equal(CfSimulatorDocuments.SampleSecurityDeposit, reading.SecurityDeposit!.Value);
        Assert.Equal(new DateOnly(2026, 11, 1), reading.StartDate!.Value);
        Assert.Equal(new DateOnly(2029, 10, 31), reading.EndDate!.Value);
        Assert.Equal(36, reading.TermMonths!.Value);
        Assert.Equal(today, reading.StipulaDate!.Value);

        // The house is the one of the lease: the comparison with the property finds a match, never a conflict.
        Assert.Equal(PropertyAddressMatch.Match, reading.Property!.Value);
    }
}
