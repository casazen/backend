using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class QuesturaCommunicationServiceTests
{
    [Fact]
    public async Task DeclareDeliveryDateAsync_AfterCommunicationMarkedDone_RejectsChangeAndKeepsChecklistBasis()
    {
        await using var db = CreateDb();
        var lease = BuildExtraEuLease();
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();

        var sut = new QuesturaCommunicationService(
            db,
            Mock.Of<IFileStorage>(),
            NullLogger<QuesturaCommunicationService>.Instance);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            sut.DeclareDeliveryDateAsync(
                lease.Id,
                "auth0|owner",
                new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(QuesturaCommunicationErrorCodes.DeliveryDateLocked, ex.Code);
        var stored = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == lease.Id);
        Assert.Null(stored.PropertyDeliveryDate);
        Assert.False(await db.LeaseEvents.AnyAsync(e =>
            e.LeaseContractId == lease.Id && e.EventType == LeaseEventType.PropertyDeliveryDateDeclared));
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"questura-communication-{Guid.NewGuid():N}")
            .Options);

    private static LeaseContract BuildExtraEuLease()
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        return new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            Property = new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Long rent flat",
                Address = "Via Roma 1",
                City = "Milano",
                PostalCode = "20100",
            },
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            ContractType = LeaseContractType.Libero,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 9, 22, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            QuesturaCommunicationDate = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc),
            QuesturaCommunicationDeclaredByUserId = "auth0|owner",
            Parties =
            [
                new Party
                {
                    LeaseContractId = leaseId,
                    Role = PartyRole.Tenant,
                    FirstName = "John",
                    LastName = "Smith",
                    FiscalCode = "SMTJHN85B02Z404X",
                    Citizenship = "US",
                    ContactEmail = "john@example.com",
                    IsExtraEU = true,
                },
            ],
        };
    }
}
