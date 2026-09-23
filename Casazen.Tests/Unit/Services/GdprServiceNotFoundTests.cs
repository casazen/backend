using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GdprServiceNotFoundTests
{
    public static TheoryData<string> Operations => new() { "export", "delete", "anonymize", "consent" };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task GuestOperation_UnknownGuest_ThrowsNotFoundExceptionWithGuestCode(string operation)
    {
        var guestRepository = new Mock<IGuestRepository>();
        guestRepository
            .Setup(r => r.GetByIdInOrgAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guest?)null);
        await using var db = NewDb();
        var service = new GdprService(guestRepository.Object, db, NullLogger<GdprService>.Instance);

        var ex = await Assert.ThrowsAsync<NotFoundException>(Act(service, operation, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal("guest_not_found", ex.Code);
        Assert.Equal("GuestNotFound", ex.MessageKey);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task GuestOperation_GuestOfOtherOrg_ThrowsNotFoundAndLeavesGuestUntouched(string operation)
    {
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var guest = new Guest
        {
            OrgId = orgA,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
            DocumentNumber = "AB1234567",
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        var service = new GdprService(new GuestRepository(db), db, NullLogger<GdprService>.Instance);

        var ex = await Assert.ThrowsAsync<NotFoundException>(Act(service, operation, orgB, guest.Id));

        Assert.Equal("guest_not_found", ex.Code);
        db.ChangeTracker.Clear();
        var stored = await db.Guests.SingleAsync(g => g.Id == guest.Id);
        Assert.Equal("Mario", stored.FirstName);
        Assert.Equal("AB1234567", stored.DocumentNumber);
        Assert.False(stored.IsDeleted);
        Assert.False(stored.MarketingConsent);
    }

    private static Func<Task> Act(GdprService service, string operation, Guid orgId, Guid guestId) => operation switch
    {
        "export" => () => service.ExportGuestDataAsync(orgId, guestId),
        "delete" => () => service.DeleteGuestDataAsync(orgId, guestId, "User request"),
        "anonymize" => () => service.AnonymizeGuestDataAsync(orgId, guestId),
        _ => () => service.UpdateConsentAsync(orgId, guestId, marketingConsent: true),
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
