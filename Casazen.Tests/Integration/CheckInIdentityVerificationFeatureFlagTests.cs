using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DEB-01 (2026-10-31): the document reading and the selfie check of the guest check-in are placeholders (MRZ and OCR read nothing, the
/// liveness is always true, the face similarity a fixed 0.75), so they are behind <c>Features:CheckInIdentityVerification</c>, off by
/// default. Off, both public endpoints answer 404 like a route that does not exist, before the body is read: the session of the guest is
/// not touched and no verification is logged. On, they are reached; their images are buffered in memory only, never in a temporary file.
/// </summary>
public class CheckInIdentityVerificationFeatureFlagTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Theory]
    [InlineData("document", "document")]
    [InlineData("selfie", "selfie")]
    public async Task ImageEndpoint_FlagOff_Returns404_AndTheSessionIsUntouched(string endpoint, string field)
    {
        var (token, sessionId, bookingId) = await SeedSessionAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/public/checkin/{token}/{endpoint}", ImageForm(field));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(0, session.BiometricAttempts);
        Assert.Null(session.BiometricOutcome);
        Assert.False(await db.BiometricVerificationLogs.IgnoreQueryFilters().AnyAsync(l => l.BookingId == bookingId));
    }

    [Fact]
    public async Task ImageEndpoints_FlagOn_AreReached()
    {
        await using var enabled = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:CheckInIdentityVerification"] = "true" })));
        using var client = enabled.CreateClient();

        // A form without the image: the model validation of the action answers, so the request went past the gate.
        var document = await client.PostAsync($"/api/public/checkin/{Guid.NewGuid():N}/document", FormWithoutImage());
        var selfie = await client.PostAsync($"/api/public/checkin/{Guid.NewGuid():N}/selfie", FormWithoutImage());

        Assert.Equal(HttpStatusCode.BadRequest, document.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, selfie.StatusCode);
    }

    [Theory]
    [InlineData(nameof(PublicGuestCheckInController.UploadDocument))]
    [InlineData(nameof(PublicGuestCheckInController.UploadSelfie))]
    public void ImageEndpoint_BuffersTheWholeRequestInMemory_NeverOnDisk(string action)
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(action)!;
        var formLimits = method.GetCustomAttribute<RequestFormLimitsAttribute>();
        var sizeLimit = method.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(formLimits);
        Assert.NotNull(sizeLimit);
        Assert.Equal((long?)PublicGuestCheckInController.MaxImageRequestBytes, ((IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
        Assert.True(formLimits.MemoryBufferThreshold >= PublicGuestCheckInController.MaxImageRequestBytes);
    }

    private async Task<(string Token, Guid SessionId, Guid BookingId)> SeedSessionAsync()
    {
        var seed = await factory.SeedConfirmedBookingWithTokenAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId);
        var token = await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance)
            .CreateSessionAsync(seed.BookingId, booking.OrgId);
        var sessionId = await db.GuestCheckInSessions.IgnoreQueryFilters()
            .Where(s => s.BookingId == seed.BookingId)
            .Select(s => s.Id)
            .SingleAsync();

        return (token, sessionId, seed.BookingId);
    }

    /// <summary>A form with one small PNG under <paramref name="field"/>.</summary>
    private static MultipartFormDataContent ImageForm(string field)
    {
        var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { image, field, $"{field}.png" } };
    }

    private static MultipartFormDataContent FormWithoutImage() => new() { { new StringContent("x"), "note" } };
}
