using System.Net;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Regulatory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class DirectAlloggiatiPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_twice_only_posts_once_and_only_acknowledged_response_links_daily_pdf(bool lostResponse)
    {
        var seed = await factory.SeedConfirmedBookingWithTokenAsync(completeGuestData: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var b = await db.Bookings.Include(x => x.Property).SingleAsync(x => x.Id == seed.BookingId);
        var clock = new MutableClock();
        b.CheckInDate = clock.TodayInRome().AddHours(8);
        b.CheckOutDate = b.CheckInDate.AddDays(3);
        b.ArrivedAt = clock.GetUtcNow().UtcDateTime;
        b.Property.Country = "IT";
        b.NumberOfGuests = 1;
        var account = new PropertyQuesturaCredentials { OrgId = b.OrgId, PropertyId = b.PropertyId,
            Username = "synthetic", Password = "local", WsKey = "fixture", LastVerificationOk = true };
        var report = new AlloggiatiWebReport { OrgId = b.OrgId, GuestId = b.GuestId, BookingId = b.Id, Status = AlloggiatiWebStatus.DaInviareManualmente };
        db.AddRange(account, report);
        await db.SaveChangesAsync();
        var guest = AlloggiatiRecordFileTests.SingleGuest() with { ArrivalDate = b.CheckInDate };
        var reports = new Mock<IAlloggiatiWebService>();
        reports.Setup(r => r.GetGuestSummaryAsync(b.Id)).ReturnsAsync(new AlloggiatiGuestSummaryInfo(b.Id, report.Status,
            b.CheckInDate, 3, false, 1, [guest], true, true, []));
        var delegation = new Mock<IAlloggiatiDelegationService>();
        delegation.Setup(d => d.GetAsync(b.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new AlloggiatiDelegationInfo(true, null, null, null, null));
        var route = new Mock<IDirectAlloggiatiRoute>();
        route.Setup(r => r.EffectiveFromAsync(b.OrgId, b.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(clock.TodayInRomeAsDateOnly());

        var handler = new LocalHandler(lostResponse);
        using var http = new HttpClient(handler);
        var soap = new DirectAlloggiatiSoap(http);
        var live = new AlloggiatiTransportEnvironment(true);
        var dispatch = new DirectAlloggiatiDispatch(db, soap, reports.Object, delegation.Object, route.Object, clock, Mock.Of<IFeatureFlags>(), live);
        Assert.Contains(new AlloggiatiJobTarget(b.OrgId, report.Id), await dispatch.CandidatesAsync(default));
        var arrivedAt = b.ArrivedAt;
        b.ArrivedAt = arrivedAt!.Value.AddDays(-1);
        await db.SaveChangesAsync();
        await dispatch.ProcessAsync(report.Id, default);
        Assert.Equal(0, handler.Sends);
        b.ArrivedAt = arrivedAt;
        await db.SaveChangesAsync();
        await dispatch.ProcessAsync(report.Id, default);
        await dispatch.ProcessAsync(report.Id, default);
        Assert.Equal(1, handler.Sends);
        var attempt = await db.AlloggiatiSendAttempts.SingleAsync(a => a.BookingId == b.Id);
        Assert.Equal(lostResponse ? AlloggiatiAttemptStatus.ToVerify : AlloggiatiAttemptStatus.Accepted, attempt.Status);
        Assert.Equal(lostResponse ? AlloggiatiWebStatus.ToVerify : AlloggiatiWebStatus.SentReceiptPending, report.Status);
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        storage.Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var receipts = new DirectAlloggiatiReceipts(db, soap, storage.Object, live, clock);
        await receipts.ArchiveAsync(attempt.Id, default);
        Assert.Null(attempt.ReceiptId);
        clock.Advance();
        await receipts.ArchiveAsync(attempt.Id, default);
        Assert.Equal(lostResponse ? AlloggiatiWebStatus.ToVerify : AlloggiatiWebStatus.Inviato, report.Status);
        Assert.Equal(!lostResponse, report.ReceiptId.HasValue);
        Assert.Equal(lostResponse ? 0 : 1, handler.Downloads);
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddDays(1);
    }

    private sealed class LocalHandler(bool lost) : HttpMessageHandler
    {
        public int Sends;
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var op = request.Headers.GetValues("SOAPAction").Single().Trim('"').Split('/').Last();
            if (op == "Send") { Sends++; if (lost) throw new HttpRequestException("lost response"); }
            if (op == "Ricevuta") Downloads++;
            var content = op switch
            {
                "GenerateToken" => "<GenerateTokenResult><token>local</token></GenerateTokenResult><result><esito>true</esito></result>",
                "Test" or "Send" => $"<{op}Result><esito>true</esito></{op}Result><result><SchedineValide>1</SchedineValide><Dettaglio><EsitoOperazioneServizio><esito>true</esito></EsitoOperazioneServizio></Dettaglio></result>",
                "Ricevuta" => "<RicevutaResult><esito>true</esito></RicevutaResult><PDF>JVBERi0x</PDF>",
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                $"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><{op}Response xmlns='AlloggiatiService'>{content}</{op}Response></s:Body></s:Envelope>", Encoding.UTF8, "text/xml") });
        }
    }
}
