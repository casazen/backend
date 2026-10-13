using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GdprServiceNotFoundTests
{
    public static TheoryData<string> Operations => new() { "summary", "export", "delete", "anonymize", "consent", "files" };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task GuestOperation_UnknownGuest_ThrowsNotFoundExceptionWithGuestCode(string operation)
    {
        var guestRepository = new Mock<IGuestRepository>();
        guestRepository
            .Setup(r => r.GetByIdInOrgAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guest?)null);
        await using var db = NewDb();
        var service = NewService(db, guestRepository.Object);

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
        var service = NewService(db, new GuestRepository(db));

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
        "summary" => () => service.GetGuestPrivacySummaryAsync(orgId, guestId),
        "export" => () => service.ExportGuestDataAsync(orgId, guestId, "auth0|host"),
        "delete" => () => service.EraseGuestDataAsync(orgId, guestId, "User request", "auth0|host"),
        "anonymize" => () => service.AnonymizeGuestDataAsync(orgId, guestId, "auth0|host"),
        "files" => () => service.EraseStoredFilesBeforeRemovalAsync(orgId, guestId, "auth0|host"),
        _ => () => service.UpdateMarketingConsentAsync(orgId, guestId, marketingConsent: false, "Richiesta via email", "auth0|host"),
    };

    private static GdprService NewService(AppDbContext db, IGuestRepository guestRepository) =>
        new(
            db,
            guestRepository,
            new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions()),
            TimeProvider.System,
            NullLogger<GdprService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
