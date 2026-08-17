using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class RliExportServiceTests
{
    private const string OwnerId = "auth0|owner";

    [Fact]
    public async Task ExportAsync_OwnerLease_ReturnsPdfWithoutCfInFilename_AndEmitsEvent()
    {
        var lease = BuildLease();
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);
        var sut = new RliExportService(leases.Object, events.Object);

        var result = await sut.ExportAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.True(result.PdfBytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(result.PdfBytes, 0, 4));
        Assert.StartsWith("rli-prefill-", result.FileName);
        Assert.DoesNotContain("RSSMRA", result.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("P.IVA", result.FileName, StringComparison.OrdinalIgnoreCase);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.RliExported)), Times.Once);
    }

    [Fact]
    public async Task ExportAsync_WrongOwner_ReturnsNull()
    {
        var lease = BuildLease();
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        var events = new Mock<ILeaseEventRepository>();
        var sut = new RliExportService(leases.Object, events.Object);

        Assert.Null(await sut.ExportAsync(lease.Id, "auth0|other"));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    private static LeaseContract BuildLease() => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = FiscalRegime.CedolareSecca,
        MonthlyRent = 1200m,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        RegistrationDeadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Property = new Property { OwnerId = OwnerId, City = "Milano", Name = "Via Roma" },
        Parties =
        [
            new Party
            {
                Role = PartyRole.Landlord,
                FirstName = "Mario",
                LastName = "Rossi",
                FiscalCode = "RSSMRA80A01H501U",
                Citizenship = "IT",
                ContactEmail = "mario@example.com",
            },
        ],
    };
}
