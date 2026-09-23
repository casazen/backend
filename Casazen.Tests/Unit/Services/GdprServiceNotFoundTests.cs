using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GdprServiceNotFoundTests
{
    public static TheoryData<string> Operations => new() { "export", "delete", "consent" };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task GuestOperation_UnknownGuest_ThrowsNotFoundExceptionWithGuestCode(string operation)
    {
        var guestRepository = new Mock<IGuestRepository>();
        guestRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guest?)null);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var service = new GdprService(guestRepository.Object, db, NullLogger<GdprService>.Instance);
        var guestId = Guid.NewGuid();

        Func<Task> act = operation switch
        {
            "export" => () => service.ExportGuestDataAsync(guestId),
            "delete" => () => service.DeleteGuestDataAsync(guestId, "User request"),
            _ => () => service.UpdateConsentAsync(guestId, marketingConsent: true),
        };

        var ex = await Assert.ThrowsAsync<NotFoundException>(act);
        Assert.Equal("guest_not_found", ex.Code);
        Assert.Equal("GuestNotFound", ex.MessageKey);
    }
}
