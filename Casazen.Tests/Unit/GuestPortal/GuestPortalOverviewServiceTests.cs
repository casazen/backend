using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Unit.Services;
using Casazen.Web.DTOs.GuestPortal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalOverviewServiceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Overview_BeforeCheckIn_HasTheContractShapeAndNoReservedData()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();

        var overview = await world.OverviewService().GetAsync(context);

        Assert.NotNull(overview);
        Assert.False(overview.CanSeeReserved);
        Assert.False(overview.CheckInCompleted);
        Assert.Equal(
            new GuestPortalStay("Trullo Aurora", "Ostuni", "2026-10-10", "2026-10-13", 3, 3, "confirmed", overview.Stay.BookingCode, true, false),
            overview.Stay);
        Assert.Matches("^[A-Z0-9]{5}-[A-Z0-9]{5}$", overview.Stay.BookingCode);
        Assert.Equal(
            new GuestPortalBranding("Casa Vacanze Aurora", null, "#336699", overview.Branding.ThemeId, overview.Branding.FontId, "Il mare a due passi", "+390831000000"),
            overview.Branding);
        Assert.Equal("de", overview.Language);
        Assert.Equal(new GuestPortalStep("stay.welcome", "stay"), overview.NextStep);
        Assert.Equal(new GuestPortalFeatures(true, false, false), overview.Features);

        var json = JsonSerializer.Serialize(GuestPortalOverviewDto.From(overview), Web);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["stay", "branding", "language", "sections", "nextStep", "canSeeReserved", "checkInCompleted", "features"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["propertyName", "city", "checkIn", "checkOut", "nights", "guests", "status", "bookingCode", "isOta", "isDirect"],
            document.RootElement.GetProperty("stay").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["name", "logoUrl", "primaryColor", "themeId", "fontId", "tagline", "phone"],
            document.RootElement.GetProperty("branding").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["key", "order", "state", "badge"],
            document.RootElement.GetProperty("sections")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["key", "section", "panel", "params"], document.RootElement.GetProperty("nextStep").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["paidServices", "supplierServices", "autoReplies", "hasSyncedChannelConversation"],
            document.RootElement.GetProperty("features").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(GuestPortalTestWorld.HouseAddress, json, StringComparison.Ordinal);
        Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wifi", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overview_OffersTheChannelOnlyForTheSyncedThreadOfThisStay()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();
        var overview = world.OverviewService(flags: new TestFeatureFlags(FeatureFlags.GuestPortal, FeatureFlags.OctorateConnector));
        var connection = new OctorateConnection { OrgId = context.OrgId, Status = OctorateConnectionStatus.Connected, Environment = OctorateEnvironment.Test };
        var mapping = new OctorateListingMapping
        {
            OrgId = context.OrgId,
            PropertyId = context.PropertyId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = "123",
            Status = OctorateMappingStatus.Confirmed,
        };
        var sync = new OtaMessageSyncState
        {
            OrgId = context.OrgId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = "123",
            LastRecentPassAt = DateTime.UtcNow,
        };
        var thread = new OtaMessageThread
        {
            OrgId = context.OrgId,
            BookingId = context.BookingId,
            PropertyId = context.PropertyId,
            OctorateAccommodationId = "123",
            OctorateThreadId = "456",
            Kind = OtaThreadKind.Reservation,
            ChannelSource = OtaChannel.Airbnb,
        };
        world.Db.AddRange(connection, mapping, sync, thread);
        await world.Db.SaveChangesAsync();
        Assert.True((await overview.GetAsync(context))!.Features.HasSyncedChannelConversation);
        Assert.False((await world.OverviewService().GetAsync(context))!.Features.HasSyncedChannelConversation);

        thread.BookingId = null;
        await world.Db.SaveChangesAsync();
        Assert.False((await overview.GetAsync(context))!.Features.HasSyncedChannelConversation);
        thread.BookingId = context.BookingId;
        sync.LastError = "chat_not_licensed";
        await world.Db.SaveChangesAsync();
        Assert.False((await overview.GetAsync(context))!.Features.HasSyncedChannelConversation);
        sync.LastError = null;
        connection.Environment = OctorateEnvironment.Production;
        await world.Db.SaveChangesAsync();
        Assert.False((await overview.GetAsync(context))!.Features.HasSyncedChannelConversation);
        connection.Environment = OctorateEnvironment.Test;
        mapping.Status = OctorateMappingStatus.Ambiguous;
        await world.Db.SaveChangesAsync();
        Assert.False((await overview.GetAsync(context))!.Features.HasSyncedChannelConversation);
    }

    [Fact]
    public async Task Context_CompletedCheckIn_CanSeeReserved_AndTheLanguageOfTheGuest()
    {
        using var world = new GuestPortalTestWorld();
        world.Db.GuestCheckInSessions.AddRange(
            Session(world, GuestCheckInSessionStatus.Scaduto, "es", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Session(world, GuestCheckInSessionStatus.Completo, "fr", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)));
        await world.Db.SaveChangesAsync();

        var context = await world.ContextAsync();

        Assert.True(context.CheckInCompleted);
        Assert.True(context.CanSeeReserved);
        Assert.Equal("fr", context.Language);
        Assert.True(context.IsOta);
        Assert.False(context.IsDirect);
        Assert.Equal(BookingStatus.Confirmed, context.Status);
    }

    [Fact]
    public async Task Context_NoSession_IsTheLanguageOfTheOrg_AndNothingReserved()
    {
        using var world = new GuestPortalTestWorld();
        world.Db.GuestCheckInSessions.Add(Session(world, GuestCheckInSessionStatus.InCompilazione, null, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)));
        await world.Db.SaveChangesAsync();

        var context = await world.ContextAsync();

        Assert.False(context.CheckInCompleted);
        Assert.False(context.CanSeeReserved);
        Assert.Equal("de", context.Language);
    }

    /// <summary>
    /// Privacy rule (the reserved data close with the stay): the link of a cancelled stay, of a no-show and of a stay already checked out keeps opening,
    /// and the check-in they completed stays a fact (<c>checkInCompleted</c>), but they get <c>canSeeReserved: false</c>, in the context and in the
    /// overview, so none of the reserved data; the stay to come and the one in progress, with the same completed check-in, get it. The lock code of the
    /// guest is refused to the same stays.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, false, true)]
    [InlineData(BookingStatus.CheckedIn, false, true)]
    [InlineData(BookingStatus.CheckedOut, false, false)]
    [InlineData(BookingStatus.Cancelled, false, false)]
    [InlineData(BookingStatus.Confirmed, true, false)]
    public async Task Context_CompletedCheckIn_ReservedDataOnlyForAStayToComeOrInProgress(BookingStatus status, bool noShow, bool expected)
    {
        using var world = new GuestPortalTestWorld();
        world.Db.GuestCheckInSessions.Add(Session(world, GuestCheckInSessionStatus.Completo, "it", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)));
        await world.Db.SaveChangesAsync();

        var token = (await world.LinkService().GetOrCreateAsync(world.Org.Id, world.Booking.Id, createdByUserId: null))!.Link.Token;
        var booking = await world.Db.Bookings.SingleAsync(b => b.Id == world.Booking.Id);
        booking.Status = status;
        booking.NoShowAt = noShow ? GuestPortalTestWorld.Now.UtcDateTime : null;
        await world.Db.SaveChangesAsync();

        var (filter, accessor) = world.TokenFilter();
        var (result, _, ran) = await GuestPortalTestWorld.RunAsync(filter, token);

        Assert.Null(result);
        Assert.True(ran);
        var context = accessor.Current!;
        Assert.True(context.CheckInCompleted);
        Assert.Equal(expected, context.CanSeeReserved);

        var overview = await world.OverviewService().GetAsync(context);
        Assert.NotNull(overview);
        Assert.True(overview.CheckInCompleted);
        Assert.Equal(expected, overview.CanSeeReserved);
    }

    /// <summary>
    /// A section that throws is left out and logged with the id of the link and its key, never a failure of the overview; the next step is the proposal
    /// of the lowest priority that opens a section of the overview (one that throws and one that opens a section not offered are skipped); one «no» of a
    /// policy hides the paid services and so the services of the suppliers, whatever their flag says.
    /// </summary>
    [Fact]
    public async Task Overview_SectionThatThrows_IsLeftOutAndLogged_LowestPriorityStepWins_OneNoHidesThePaidServices()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal, FeatureFlags.GuestPortalSupplierServices, FeatureFlags.GuestPortalAutoReplies);

        var overview = await world.OverviewService(
                sections: [new StayPortalSection(), new ThrowingGuideSection(), new AttentionCheckInSection()],
                nextSteps: [new StayOverviewNextStep(), new CheckInStartStep(), new ThrowingStep(), new HiddenSectionStep()],
                policies: [new DefaultGuestPortalPolicy(), new RefusingPolicy()],
                flags: flags)
            .GetAsync(context);

        Assert.NotNull(overview);
        Assert.Equal(
            [(GuestPortalSectionKeys.Stay, GuestPortalSectionStates.Available), (GuestPortalSectionKeys.CheckIn, GuestPortalSectionStates.Attention)],
            overview.Sections.Select(s => (s.Key, s.State)));
        Assert.Equal(new GuestPortalStep("checkin.start", GuestPortalSectionKeys.CheckIn), overview.NextStep);
        Assert.Equal(new GuestPortalFeatures(false, false, true), overview.Features);

        Assert.Contains(
            world.Logs.Lines,
            line => line.Contains("SectionKey=guide", StringComparison.Ordinal) && line.Contains($"LinkId={context.LinkId}", StringComparison.Ordinal));

        var guarded = await world.OverviewService(policies: [new ThrowingPolicy()], flags: flags).GetAsync(context);
        Assert.Equal(new GuestPortalFeatures(false, false, true), guarded!.Features);
    }

    [Fact]
    public async Task Overview_StayNoLongerThere_IsNull()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();
        var property = await world.Db.Properties.SingleAsync(p => p.Id == world.Property.Id);
        world.Db.Properties.Remove(property);
        await world.Db.SaveChangesAsync();

        Assert.Null(await world.OverviewService().GetAsync(context));
    }

    [Fact]
    public void IsoDay_ARealDay_IsYearMonthDay()
    {
        Assert.Equal("2026-10-10", GuestPortalOverviewService.IsoDay(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-01-05", GuestPortalOverviewService.IsoDay(new DateTime(2026, 1, 5, 23, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("checkedIn", GuestPortalOverviewService.StatusWord(BookingStatus.CheckedIn));
        Assert.Equal("cancelled", GuestPortalOverviewService.StatusWord(BookingStatus.Cancelled));
    }

    private static GuestCheckInSession Session(GuestPortalTestWorld world, GuestCheckInSessionStatus status, string? language, DateTime createdAt) => new()
    {
        BookingId = world.Booking.Id,
        OrgId = world.Org.Id,
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAt = createdAt.AddDays(7),
        Status = status,
        Language = language,
        CreatedAt = createdAt,
    };
}

public sealed class ThrowingGuideSection : IGuestPortalSection
{
    public string Key => GuestPortalSectionKeys.Guide;

    public int Order => 30;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The guide could not be read.");
}

public sealed class AttentionCheckInSection : IGuestPortalSection
{
    public string Key => GuestPortalSectionKeys.CheckIn;

    public int Order => 20;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalSectionStatus?>(new GuestPortalSectionStatus(GuestPortalSectionState.Attention));
}

public sealed class CheckInStartStep : IGuestPortalNextStep
{
    public int Priority => 10;

    public Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalStep?>(new GuestPortalStep("checkin.start", GuestPortalSectionKeys.CheckIn));
}

public sealed class ThrowingStep : IGuestPortalNextStep
{
    public int Priority => 1;

    public Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The step could not be read.");
}

public sealed class HiddenSectionStep : IGuestPortalNextStep
{
    public int Priority => 5;

    public Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalStep?>(new GuestPortalStep("services.discover", GuestPortalSectionKeys.Services));
}

public sealed class RefusingPolicy : IGuestPortalPolicy
{
    public Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class ThrowingPolicy : IGuestPortalPolicy
{
    public Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The rule could not be read.");
}
