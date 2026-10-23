using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Documents;
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
        var sut = new RliExportService(leases.Object, events.Object, new MigraDocPdfDocumentRenderer());

        var result = await sut.ExportAsync(lease.Id);

        Assert.NotNull(result);
        Assert.True(result.PdfBytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(result.PdfBytes, 0, 4));
        Assert.StartsWith("rli-prefill-", result.FileName);
        Assert.DoesNotContain("RSSMRA", result.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("P.IVA", result.FileName, StringComparison.OrdinalIgnoreCase);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.RliExported)), Times.Once);
    }

    [Theory]
    // LT-04 (A7-04): signed 1/8, start 1/10 → stipula and deadline 31/8 in the prefill.
    [InlineData(true, "Data di stipula: 2026-08-01", "Scadenza registrazione: 2026-08-31")]
    // Signed without a recorded stipula: no invented date.
    [InlineData(false, "Data di stipula: non disponibile", "Scadenza registrazione: da determinare")]
    public async Task ExportAsync_Deadline_FromStipulaOrToBeDetermined(bool withStipula, string stipulaLine, string deadlineLine)
    {
        var lease = BuildLease();
        lease.Status = LeaseStatus.Signed;
        lease.StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        lease.RegistrationDeadline = null;
        if (withStipula)
            lease.RecordStipula(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));
        var sut = new RliExportService(leases.Object, events.Object, new MigraDocPdfDocumentRenderer(), clock);

        var result = await sut.ExportAsync(lease.Id);

        var pdf = PdfTestReader.Text(result!.PdfBytes);
        Assert.Contains(stipulaLine, pdf, StringComparison.Ordinal);
        Assert.Contains(deadlineLine, pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_LeaseNotVisible_ReturnsNullWithoutEvent()
    {
        var lease = BuildLease();
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        var events = new Mock<ILeaseEventRepository>();
        var sut = new RliExportService(leases.Object, events.Object, new MigraDocPdfDocumentRenderer());

        // Who may export is decided by the controller (TN-3); an id outside the caller's org is not found.
        Assert.Null(await sut.ExportAsync(Guid.NewGuid()));
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    private static LeaseContract BuildLease() => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = FiscalRegime.CedolareSecca,
        MonthlyRent = 1200m,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
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
