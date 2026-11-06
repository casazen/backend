using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The billing data sync with Octorate (FIS-01, FIS-02 point 1, NF-81, NF-82, E06/F02/US01/T04). The two rules that cost if they fail: what the import
/// of Octorate pre-fills never replaces what the host wrote (data rule), and what CasaZen writes back holds the billing fields and nothing else — no
/// identity document, no Alloggiati datum, no phone or e-mail address of the guest (privacy rule). Around them, the behaviours the acceptance criteria
/// name: written once for each change and only where Octorate issues, and a failing or refused Octorate never fails an import or a host's save and never
/// leaks a personal value into a log. The Octorate client is the real one over a recorded HTTP exchange where the wire counts, a mock where the handler
/// does.
/// </summary>
public class OctorateBillingSyncProofTests
{
    private static readonly Guid OrgA = Guid.Parse("5b4a3928-1706-4e5d-9c8b-7a6958473611");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private const string AccommodationId = "585492";
    private const string ReservationId = "130219355";
    private const string CustomerId = "77001";
    private const string Owner = "auth0|owner-a";

    private const string FiscalCode = "RSSMRA80A01H501U";
    private const string OtherFiscalCode = "BNCGLI75C42F205Z";
    private const string VatNumber = "IT12345678903";
    private const string CompanyName = "Ristorante Da Gino S.r.l.";
    private const string CompanyPec = "fatture.gino@pec.example.com";

    private const string GuestEmail = "anna.verdi.privata@example.com";
    private const string GuestPhone = "+393331112233";
    private const string GuestDocumentNumber = "AX1234567";

    private static CancellationToken Ct => CancellationToken.None;

    // --- The proofs -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule. The host wrote the recipient first; the import of Octorate then reads the customer record and saves it with the lowest
    /// source: it fills the fields that are empty and changes none of the host's (name, fiscal code, address, kind of recipient), and the row stays the
    /// host's. The same event delivered again changes nothing and records no event.
    /// </summary>
    [Fact]
    public async Task OctorateBillingSync_HostDataNeverOverwritten_Test()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        await rig.SaveInvoiceAsync(
            booking.Id,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Guest,
                Name = "Mario Rossi",
                FiscalCode = FiscalCode,
                AddressStreet = "Via Roma 1",
                City = "Roma",
            },
            InvoiceDataSource.Host);
        rig.Client
            .Setup(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateBillingData
            {
                IsCompany = true,
                Name = "Octo Srl",
                FiscalCode = OtherFiscalCode,
                VatNumber = VatNumber,
                Address = "Via Milano 9",
                City = "Milano",
                Pec = "octo@pec.example.com",
            });
        var created = Created(booking, BookingEventOrigin.Octorate);

        await rig.FromOctorate().HandleAsync(created, Ct);

        var row = await rig.RowAsync(booking.Id);
        Assert.Equal("Mario Rossi", row.Name);
        Assert.Equal(FiscalCode, row.FiscalCode);
        Assert.Equal("Via Roma 1", row.AddressStreet);
        Assert.Equal("Roma", row.City);
        Assert.Equal(InvoiceRecipientKind.Guest, row.RecipientKind);
        Assert.Equal(VatNumber, row.VatNumber);
        Assert.Equal("octo@pec.example.com", row.Pec);
        Assert.Equal(InvoiceDataSource.Host, row.Source);

        // Delivered again: there is nothing left to fill, so nothing is written and no event is recorded.
        var events = await rig.EventCountAsync(booking.Id);
        await rig.FromOctorate().HandleAsync(created, Ct);
        Assert.Equal(events, await rig.EventCountAsync(booking.Id));
        Assert.Equal(row.UpdatedAt, (await rig.RowAsync(booking.Id)).UpdatedAt);
        rig.Client.Verify(
            c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The booking's guest has an e-mail address, a phone number and an identity document, and the invoice data is complete:
    /// what reaches Octorate is the read of the reservation (to find the customer record) and one <c>PUT</c> whose members are exactly the billing members
    /// CasaZen writes and no other, and nothing the guest holds privately appears in either request. The type that carries the data has no member for
    /// anything else, so no caller can add one.
    /// </summary>
    [Fact]
    public async Task OctorateBillingSync_OnlyBillingFieldsSent_Test()
    {
        var rig = new Rig();
        var guestId = Guid.NewGuid();
        var booking = await rig.SeedBookingAsync(guestId: guestId);
        await using (var db = rig.NewDb())
        {
            db.Guests.Add(new Guest
            {
                Id = guestId,
                OrgId = OrgA,
                FirstName = "Anna",
                LastName = "Verdi",
                Email = GuestEmail,
                PhoneNumber = GuestPhone,
                DocumentNumber = GuestDocumentNumber,
                DocumentIssuingCountry = "IT",
                DateOfBirth = new DateTime(1985, 3, 2, 0, 0, 0, DateTimeKind.Utc),
                PlaceOfBirth = "Torino",
                Nationality = "IT",
            });
            db.BookingInvoiceData.Add(new BookingInvoiceData
            {
                OrgId = OrgA,
                BookingId = booking.Id,
                InvoiceRequested = true,
                InvoiceRequestedAt = Now.UtcDateTime,
                RecipientKind = InvoiceRecipientKind.Company,
                Name = CompanyName,
                FiscalCode = "12345678903",
                VatNumber = VatNumber,
                AddressStreet = "Via del Sale Rosa",
                AddressCivic = "42/B",
                PostalCode = "22100",
                City = "Cernobbio",
                Province = "CO",
                CountryCode = "IT",
                SdiRecipientCode = "ABC1234",
                Pec = CompanyPec,
                Source = InvoiceDataSource.Host,
            });
            await db.SaveChangesAsync();
        }

        var http = new RecordingHandler();
        http.Answer(HttpStatusCode.OK, $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": {{CustomerId}}, "firstName": "Anna", "lastName": "Verdi" }""");
        http.Answer(HttpStatusCode.OK, "{}");
        var scope = new OctorateOrgScope(rig.Tenant);

        await rig.WriteBack(NewClient(http, scope, rig.Clock), scope).HandleAsync(Saved(booking.Id, InvoiceDataSource.Host), Ct);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(HttpMethod.Get, http.Requests[0].Method);
        Assert.EndsWith($"/rest/v1/reservation/{AccommodationId}/{ReservationId}", http.Requests[0].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Put, http.Requests[1].Method);
        Assert.EndsWith($"/rest/v1/guests/{AccommodationId}/{CustomerId}", http.Requests[1].Uri.AbsolutePath);

        // The members of the body: exactly the billing members CasaZen writes (the writable ones of the GuestDTO of the OpenAPI), with the data of
        // the invoice and nothing else: not the denomination, not the code of the SDI, not the province, which are not written until proven.
        using var body = JsonDocument.Parse(http.Requests[1].Body);
        var members = body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(OctorateBillingMapping.WrittenMembers.Order().ToArray(), members);
        Assert.Equal(["address", "city", "country", "fiscalCode", "legalMail", "vatCode", "zip"], members);
        Assert.Equal("12345678903", body.RootElement.GetProperty("fiscalCode").GetString());
        Assert.Equal(VatNumber, body.RootElement.GetProperty("vatCode").GetString());
        Assert.Equal("Via del Sale Rosa 42/B", body.RootElement.GetProperty("address").GetString());
        Assert.Equal("22100", body.RootElement.GetProperty("zip").GetString());
        Assert.Equal("Cernobbio", body.RootElement.GetProperty("city").GetString());
        Assert.Equal("IT", body.RootElement.GetProperty("country").GetString());
        Assert.Equal(CompanyPec, body.RootElement.GetProperty("legalMail").GetString());
        Assert.DoesNotContain(CompanyName, http.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC1234", http.Requests[1].Body, StringComparison.Ordinal);
        string[] neverAMember = ["document", "passport", "birth", "nationality", "phone", "mobile", "gender", "sex", "alloggiati", "police"];
        Assert.DoesNotContain(members, member => neverAMember.Any(word => member.Contains(word, StringComparison.OrdinalIgnoreCase)));

        // legalMail is the PEC of the recipient; the e-mail address of the guest is another member (email) that is never written.
        Assert.DoesNotContain(members, member => member.Equals("email", StringComparison.OrdinalIgnoreCase));

        // Nothing the guest holds privately is in either request, address or body.
        string[] neverAValue = [GuestEmail, GuestPhone, GuestDocumentNumber, "Anna", "Verdi", "Torino"];
        foreach (var request in http.Requests)
        {
            var wire = $"{request.Uri} {request.Body}";
            Assert.All(neverAValue, secret => Assert.DoesNotContain(secret, wire, StringComparison.OrdinalIgnoreCase));
        }

        // The type has no other member to carry one: the billing members and nothing else.
        Assert.Equal(
            new[] { "Address", "City", "CountryCode", "FiscalCode", "HasContent", "IsCompany", "Name", "Pec", "PostalCode", "Province", "SdiRecipientCode", "VatNumber" },
            typeof(OctorateBillingData).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    // --- Written once for each change, only where Octorate issues --------------------------------------------------------------------

    [Fact]
    public async Task WriteBack_IsOncePerChange_AndOnlyWhereOctorateIssues()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        var sent = new List<OctorateBillingData>();
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, OctorateBillingData, CancellationToken>((_, _, data, _) => sent.Add(data))
            .ReturnsAsync(true);

        // No invoice data yet: nothing to send.
        await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.Host), Ct);
        Assert.Empty(sent);

        // The host saves the recipient: one write, and the hash of what was written is kept.
        await rig.SaveInvoiceAsync(
            booking.Id,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Company,
                Name = CompanyName,
                VatNumber = VatNumber,
                AddressStreet = "Via del Sale Rosa 42/B",
                City = "Cernobbio",
                Pec = CompanyPec,
            },
            InvoiceDataSource.Host);
        var saved = Saved(booking.Id, InvoiceDataSource.Host);
        await rig.WriteBack().HandleAsync(saved, Ct);

        var first = Assert.Single(sent);
        Assert.True(first.IsCompany);
        Assert.Equal(CompanyName, first.Name);
        Assert.Equal(VatNumber, first.VatNumber);
        Assert.Equal("Via del Sale Rosa 42/B", first.Address);
        var written = await rig.RowAsync(booking.Id);
        Assert.False(string.IsNullOrEmpty(written.OctorateBillingHash));
        Assert.Equal(Now.UtcDateTime, written.OctorateBillingSentAt);

        // The same event delivered again, and another event for data that did not change: nothing is sent again.
        await rig.WriteBack().HandleAsync(saved, Ct);
        await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.CheckIn), Ct);
        Assert.Single(sent);

        // What came from Octorate is never sent back to it.
        await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.Octorate), Ct);
        Assert.Single(sent);

        // A change is one more write, with the new data and a new hash.
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.SaveInvoiceAsync(booking.Id, new SaveInvoiceDataCommand { Pec = "nuova@pec.example.com" }, InvoiceDataSource.Host);
        await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.Host), Ct);
        Assert.Equal(2, sent.Count);
        Assert.Equal("nuova@pec.example.com", sent[1].Pec);
        var rewritten = await rig.RowAsync(booking.Id);
        Assert.NotEqual(written.OctorateBillingHash, rewritten.OctorateBillingHash);

        // Where Octorate does not issue (CasaZen, the accountant, nobody) nothing is sent, however the data changes.
        foreach (var (kind, reason) in new[]
                 {
                     (IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen),
                     (IssuerDecisionKind.None, IssuerReasons.IssuerNobody),
                     (IssuerDecisionKind.None, IssuerReasons.BookingNotDirect),
                 })
        {
            rig.Issuer.Setup(i => i.ResolveIssuerAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new IssuerDecision(kind, reason));
            await rig.SaveInvoiceAsync(booking.Id, new SaveInvoiceDataCommand { City = $"Citta {reason}" }, InvoiceDataSource.Host);
            await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.Host), Ct);
        }

        Assert.Equal(2, sent.Count);
        Assert.Equal(rewritten.OctorateBillingHash, (await rig.RowAsync(booking.Id)).OctorateBillingHash);
    }

    // --- A failing or refused Octorate is never the host's problem --------------------------------------------------------------------

    [Fact]
    public async Task WriteBack_EveryFailureLeavesItsCode_IsNeverTheHostsProblem_AndLeaksNothing()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        await rig.SaveInvoiceAsync(
            booking.Id,
            new SaveInvoiceDataCommand { RecipientKind = InvoiceRecipientKind.Company, Name = CompanyName, VatNumber = VatNumber, Pec = CompanyPec },
            InvoiceDataSource.Host);
        var saved = Saved(booking.Id, InvoiceDataSource.Host);
        var logger = new CapturingLogger<OctorateBillingWriteBackHandler>();

        // A failing or refused call is thrown, so that the outbox delivers the event again (and keeps it dead-lettered with the code after five
        // deliveries): no answer, a 5xx, the rate limit, a refusal of the credentials or of the guests' data (the outbound IPs, D24). Nothing is marked
        // as written, the code of the failure stays on the invoice data with its time, and the log names the booking and the code, never a value.
        foreach (var redelivered in new OctorateException[]
                 {
                     new OctorateServerException(503),
                     new OctorateRateLimitException(429),
                     OctorateAuthException.Refused(403),
                 })
        {
            rig.Client
                .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(redelivered);

            await Assert.ThrowsAsync(redelivered.GetType(), () => rig.WriteBack(logger: logger).HandleAsync(saved, Ct));

            var failed = await rig.RowAsync(booking.Id);
            Assert.Null(failed.OctorateBillingHash);
            Assert.Equal(redelivered.Code, failed.OctorateBillingError);
            Assert.Equal(Now.UtcDateTime, failed.OctorateBillingErrorAt);
        }

        // A record Octorate does not know, a request it rejects, a conflict, the wrong environment, no connection: the same answer five times would
        // help nobody, so nothing is thrown and nothing is marked as written, but none of them is swallowed: the code stays on the invoice data.
        foreach (var leftAlone in new OctorateException[]
                 {
                     OctorateAuthException.NotConnected(),
                     new OctorateNotFoundException(),
                     new OctorateRequestRejectedException(400),
                     new OctorateConflictException(),
                     new OctorateWrongEnvironmentException(403),
                 })
        {
            rig.Client
                .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(leftAlone);
            rig.Clock.Advance(TimeSpan.FromMinutes(1));

            await rig.WriteBack(logger: logger).HandleAsync(saved, Ct);

            var left = await rig.RowAsync(booking.Id);
            Assert.Null(left.OctorateBillingHash);
            Assert.Null(left.OctorateBillingSentAt);
            Assert.Equal(leftAlone.Code, left.OctorateBillingError);
            Assert.Equal(rig.Clock.GetUtcNow().UtcDateTime, left.OctorateBillingErrorAt);
        }

        var log = logger.AllOutput;
        Assert.Contains(booking.Id.ToString(), log, StringComparison.Ordinal);
        Assert.Contains(OctorateAuthException.RefusedCode, log, StringComparison.Ordinal);
        Assert.Contains(OctorateAuthException.NotConnectedCode, log, StringComparison.Ordinal);
        string[] personal = [CompanyName, "Ristorante", VatNumber, "12345678903", CompanyPec, "pec.example.com"];
        Assert.All(personal, value => Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase));
        var stored = await rig.RowAsync(booking.Id);
        Assert.All(personal, value => Assert.DoesNotContain(value, stored.OctorateBillingError ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        // Octorate answers at the next delivery: written, marked, and the code of the failures is gone.
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await rig.WriteBack().HandleAsync(saved, Ct);

        var written = await rig.RowAsync(booking.Id);
        Assert.NotNull(written.OctorateBillingHash);
        Assert.Null(written.OctorateBillingError);
        Assert.Null(written.OctorateBillingErrorAt);
    }

    [Fact]
    public async Task WriteBack_AReservationWithNoCustomerRecord_MarksNothingAsWritten_AndSaysWhy()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        await rig.SaveInvoiceAsync(booking.Id, CompanyData(), InvoiceDataSource.Host);
        var saved = Saved(booking.Id, InvoiceDataSource.Host);
        var logger = new CapturingLogger<OctorateBillingWriteBackHandler>();
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // The reservation names no customer record: nothing was sent, so nothing is marked as written, and the data says why. Delivered again, the
        // same: no hash is ever stored for a write that did not happen.
        await rig.WriteBack(logger: logger).HandleAsync(saved, Ct);
        await rig.WriteBack(logger: logger).HandleAsync(saved, Ct);

        var row = await rig.RowAsync(booking.Id);
        Assert.Null(row.OctorateBillingHash);
        Assert.Null(row.OctorateBillingSentAt);
        Assert.Equal(OctorateBillingSyncRules.NoCustomerRecordCode, row.OctorateBillingError);
        Assert.Equal(Now.UtcDateTime, row.OctorateBillingErrorAt);
        Assert.Contains(OctorateBillingSyncRules.NoCustomerRecordCode, logger.AllOutput, StringComparison.Ordinal);
        rig.Client.Verify(
            c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // The customer is made in Octorate: the next save writes, marks the data and clears the code.
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.WriteBack().HandleAsync(saved, Ct);

        var written = await rig.RowAsync(booking.Id);
        Assert.NotNull(written.OctorateBillingHash);
        Assert.Equal(rig.Clock.GetUtcNow().UtcDateTime, written.OctorateBillingSentAt);
        Assert.Null(written.OctorateBillingError);
        Assert.Null(written.OctorateBillingErrorAt);
    }

    [Fact]
    public async Task WriteBack_TheCodeOfAFailedWrite_IsClearedWhenTheDataIsAgainWhatOctorateHolds()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        await rig.SaveInvoiceAsync(booking.Id, CompanyData(), InvoiceDataSource.Host);
        var saved = Saved(booking.Id, InvoiceDataSource.Host);
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await rig.WriteBack().HandleAsync(saved, Ct);
        var first = await rig.RowAsync(booking.Id);
        Assert.NotNull(first.OctorateBillingHash);

        // A change that Octorate refuses: the hash stays the one of what Octorate holds, and the code of the refusal is on the data.
        await rig.SaveInvoiceAsync(booking.Id, new SaveInvoiceDataCommand { City = "Como" }, InvoiceDataSource.Host);
        rig.Client
            .Setup(c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRequestRejectedException(400));
        await rig.WriteBack().HandleAsync(saved, Ct);
        var refused = await rig.RowAsync(booking.Id);
        Assert.Equal(first.OctorateBillingHash, refused.OctorateBillingHash);
        Assert.Equal("octorate_request_rejected", refused.OctorateBillingError);

        // The host puts the data back as it was: it is what Octorate holds, so nothing is sent and the code of the refusal is no longer the state.
        await rig.SaveInvoiceAsync(booking.Id, new SaveInvoiceDataCommand { City = "Cernobbio" }, InvoiceDataSource.Host);
        await rig.WriteBack().HandleAsync(saved, Ct);

        var back = await rig.RowAsync(booking.Id);
        Assert.Equal(first.OctorateBillingHash, back.OctorateBillingHash);
        Assert.Null(back.OctorateBillingError);
        Assert.Null(back.OctorateBillingErrorAt);
        rig.Client.Verify(
            c => c.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<OctorateBillingData>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task WriteBack_ReservationOnItsWay_IsDeliveredAgain_NotDropped()
    {
        var rig = new Rig();

        // A booking made in CasaZen whose confirmation is still waiting in the outbox: its create on Octorate is that handler's work, still to come.
        var waiting = await rig.SeedBookingAsync(reservationId: null, source: BookingSource.Direct);
        await rig.SaveInvoiceAsync(waiting.Id, CompanyData(), InvoiceDataSource.Host);
        await using (var db = rig.NewDb())
        {
            new DomainEventRecorder(db, rig.Tenant, rig.Clock).Record(
                DomainEventType.BookingConfirmed,
                waiting.Id,
                new BookingEventPayload(
                    waiting.Id, waiting.PropertyId, BookingSource.Direct, BookingEventOrigin.Host, BookingStatus.Confirmed, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3)),
                $"booking-confirmed:{waiting.Id}",
                OrgA);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<OctorateBillingPendingException>(() => rig.WriteBack().HandleAsync(Saved(waiting.Id, InvoiceDataSource.Host), Ct));

        // A booking whose create is under way (its mirror row is Pending): the same.
        var creating = await rig.SeedBookingAsync(reservationId: null, source: BookingSource.Manual);
        await rig.SaveInvoiceAsync(creating.Id, CompanyData(), InvoiceDataSource.Host);
        await using (var db = rig.NewDb())
        {
            db.OctorateBookingSyncs.Add(new OctorateBookingSync { OrgId = OrgA, BookingId = creating.Id, PropertyId = creating.PropertyId, State = OctorateSyncState.Pending });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<OctorateBillingPendingException>(() => rig.WriteBack().HandleAsync(Saved(creating.Id, InvoiceDataSource.Host), Ct));

        // Nothing is coming for these: a booking of a channel arrives with its reservation, and a booking with no create waiting has none to wait for.
        var channel = await rig.SeedBookingAsync(reservationId: null, source: BookingSource.Airbnb);
        await rig.SaveInvoiceAsync(channel.Id, CompanyData(), InvoiceDataSource.Host);
        await rig.WriteBack().HandleAsync(Saved(channel.Id, InvoiceDataSource.Host), Ct);
        var alone = await rig.SeedBookingAsync(reservationId: null, source: BookingSource.Direct);
        await rig.SaveInvoiceAsync(alone.Id, CompanyData(), InvoiceDataSource.Host);
        await rig.WriteBack().HandleAsync(Saved(alone.Id, InvoiceDataSource.Host), Ct);

        // Octorate was never called for any of them.
        rig.Client.VerifyNoOtherCalls();
    }

    // --- The pre-fill: nothing swallowed, and never the import's problem -----------------------------------------------------------------

    [Fact]
    public async Task PreFill_EveryFailureOfOctorate_IsDeliveredAgain_NeverSwallowed()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        var created = Created(booking, BookingEventOrigin.Octorate);
        var logger = new CapturingLogger<InvoiceDataFromOctorateHandler>();

        // Octorate has nothing for the booker (the reservation names no customer record, or the record holds no billing data): not an error, and
        // nothing is saved either.
        rig.Client.Setup(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>())).ReturnsAsync((OctorateBillingData?)null);
        await rig.FromOctorate().HandleAsync(created, Ct);
        Assert.Null(await rig.RowOrNullAsync(booking.Id));

        // Every failure of the call is thrown, whatever it is, so that the outbox delivers the event again (the import it came from is long committed)
        // and, after five deliveries, keeps it dead-lettered with the code: the booking may have no data at all, so it has no row of its own to keep the
        // code on, and nothing is swallowed. No answer, a 5xx, the rate limit, a refusal of the guests' data before the outbound IPs are whitelisted
        // (D24), but also a record or a reservation Octorate does not know, a request it rejects, a conflict, the wrong environment, no connection.
        foreach (var failure in new OctorateException[]
                 {
                     new OctorateServerException(503),
                     new OctorateRateLimitException(429),
                     OctorateAuthException.Refused(403),
                     OctorateAuthException.NotConnected(),
                     new OctorateNotFoundException(),
                     new OctorateRequestRejectedException(400),
                     new OctorateConflictException(),
                     new OctorateWrongEnvironmentException(403),
                 })
        {
            rig.Client.Setup(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            await Assert.ThrowsAsync(failure.GetType(), () => rig.FromOctorate(logger: logger).HandleAsync(created, Ct));
            Assert.Contains(failure.Code, logger.AllOutput, StringComparison.Ordinal);
        }

        Assert.Null(await rig.RowOrNullAsync(booking.Id));

        // The log names the booking and the code of each failure.
        Assert.Contains(booking.Id.ToString(), logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(OctorateAuthException.RefusedCode, logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(OctorateAuthException.NotConnectedCode, logger.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreFill_LeavesHalfOfTheQuotaOfTheOrgAlone_AndItIsNotAnErrorWhenItDoes()
    {
        var rig = new Rig();
        var logger = new CapturingLogger<InvoiceDataFromOctorateHandler>();
        var booking = await rig.SeedBookingAsync();
        var created = Created(booking, BookingEventOrigin.Octorate);
        rig.Client
            .Setup(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateBillingData { IsCompany = true, Name = CompanyName, VatNumber = VatNumber });

        // The bucket is the org's, shared with everything else sent to Octorate. At the boundary: a pre-fill makes two calls and must leave the half of the
        // bucket (50) alone, so it runs with 52 calls left and not with 51.
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute / 2, OctorateBillingSyncRules.PrefillQuotaReserve);
        var probe = new OctorateRateLimiter(rig.Clock);
        Assert.True(OctorateBillingSyncRules.QuotaAllowsPrefill(probe, OrgA));
        for (var used = 0; used < OctorateRateLimiter.RequestsPerMinute - 52; used++)
            Assert.True(await probe.TryAcquireAsync(OrgA));
        Assert.Equal(52, probe.AvailableCalls(OrgA));
        Assert.True(OctorateBillingSyncRules.QuotaAllowsPrefill(probe, OrgA));
        Assert.True(await probe.TryAcquireAsync(OrgA));
        Assert.False(OctorateBillingSyncRules.QuotaAllowsPrefill(probe, OrgA));

        // The org has used enough of its quota: nothing is read, nothing is thrown, the event is consumed with a line that says why, and the booking has
        // no data from Octorate (the host fills it in).
        while (rig.Limiter.AvailableCalls(OrgA) - OctorateBillingSyncRules.PrefillCalls >= OctorateBillingSyncRules.PrefillQuotaReserve)
            Assert.True(await rig.Limiter.TryAcquireAsync(OrgA));

        await rig.FromOctorate(logger: logger).HandleAsync(created, Ct);

        rig.Client.Verify(c => c.GetBookerBillingDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(await rig.RowOrNullAsync(booking.Id));
        Assert.Contains("quota of the org is low", logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(booking.Id.ToString(), logger.AllOutput, StringComparison.Ordinal);

        // The bucket refills (20 calls every 12 seconds): the next booking is pre-filled.
        rig.Clock.Advance(OctorateRateLimiter.BurstPeriod);
        await rig.FromOctorate(logger: logger).HandleAsync(created, Ct);

        rig.Client.Verify(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>()), Times.Once);
        var row = await rig.RowAsync(booking.Id);
        Assert.Equal(VatNumber, row.VatNumber);
        Assert.Equal(InvoiceDataSource.Octorate, row.Source);
    }

    [Fact]
    public async Task PreFill_ReadsNothingThatItShouldNotRead()
    {
        var rig = new Rig();

        // A stay that is over (the clock is on 2026-11-06), a booking cancelled since, a booking the host made in CasaZen (origin Host), a booking with
        // no reservation on Octorate and a property that is not mirrored: the strict client has no setup, so any call to Octorate would fail the test.
        var over = await rig.SeedBookingAsync(checkOut: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        await rig.FromOctorate().HandleAsync(Created(over, BookingEventOrigin.Octorate), Ct);

        var cancelled = await rig.SeedBookingAsync(status: BookingStatus.Cancelled);
        await rig.FromOctorate().HandleAsync(Created(cancelled, BookingEventOrigin.Octorate), Ct);

        var madeHere = await rig.SeedBookingAsync();
        await rig.FromOctorate().HandleAsync(Created(madeHere, BookingEventOrigin.Host), Ct);

        var unlinked = await rig.SeedBookingAsync(reservationId: null);
        await rig.FromOctorate().HandleAsync(Created(unlinked, BookingEventOrigin.Octorate), Ct);

        var notMirrored = await rig.SeedBookingAsync();
        rig.Targets.Setup(t => t.AccommodationOfAsync(notMirrored.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        await rig.FromOctorate().HandleAsync(Created(notMirrored, BookingEventOrigin.Octorate), Ct);

        // An event that is not about a booking created, and a payload that is not a booking event.
        var imported = await rig.SeedBookingAsync();
        await rig.FromOctorate().HandleAsync(Created(imported, BookingEventOrigin.Octorate) with { Type = DomainEventType.BookingConfirmed }, Ct);
        await rig.FromOctorate().HandleAsync(Created(imported, BookingEventOrigin.Octorate) with { PayloadJson = "not json" }, Ct);

        rig.Client.VerifyNoOtherCalls();
        foreach (var booking in new[] { over, cancelled, madeHere, unlinked, notMirrored, imported })
            Assert.Null(await rig.RowOrNullAsync(booking.Id));
    }

    [Fact]
    public async Task PreFill_FieldThatFailsTheChecks_IsLeftOut_AndTheRestIsSaved()
    {
        var rig = new Rig();
        var booking = await rig.SeedBookingAsync();
        var logger = new CapturingLogger<InvoiceDataFromOctorateHandler>();
        rig.Client
            .Setup(c => c.GetBookerBillingDataAsync(AccommodationId, ReservationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateBillingData
            {
                Name = "Mario Rossi",
                FiscalCode = FiscalCode,
                City = "Roma",
                Province = "Roma",
                PostalCode = "ABC",
                Pec = "not an address",
            });

        await rig.FromOctorate(logger: logger).HandleAsync(Created(booking, BookingEventOrigin.Octorate), Ct);

        var row = await rig.RowAsync(booking.Id);
        Assert.Equal("Mario Rossi", row.Name);
        Assert.Equal(FiscalCode, row.FiscalCode);
        Assert.Equal("Roma", row.City);
        Assert.Null(row.Province);
        Assert.Null(row.PostalCode);
        Assert.Null(row.Pec);
        Assert.Equal(InvoiceDataSource.Octorate, row.Source);

        // The log names the fields that were left out, never their values nor the rest of the record.
        Assert.Contains("province", logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains("pec", logger.AllOutput, StringComparison.Ordinal);
        string[] personal = ["Mario", "Rossi", FiscalCode, "not an address"];
        Assert.All(personal, value => Assert.DoesNotContain(value, logger.AllOutput, StringComparison.OrdinalIgnoreCase));

        // Nothing valid left in a record: nothing is saved.
        var emptyBooking = await rig.SeedBookingAsync(reservationId: "130219999");
        rig.Client
            .Setup(c => c.GetBookerBillingDataAsync(AccommodationId, "130219999", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateBillingData { Province = "Roma", Pec = "not an address" });
        await rig.FromOctorate().HandleAsync(Created(emptyBooking, BookingEventOrigin.Octorate), Ct);
        Assert.Null(await rig.RowOrNullAsync(emptyBooking.Id));
    }

    [Fact]
    public async Task Sync_IsOff_UnlessBothFlagsAreOn()
    {
        foreach (var flags in new[]
                 {
                     new TestFeatureFlags(),
                     new TestFeatureFlags(FeatureFlags.OctorateConnector),
                     new TestFeatureFlags(FeatureFlags.OctorateBillingSync),
                 })
        {
            var rig = new Rig(flags);
            var booking = await rig.SeedBookingAsync();
            await rig.SaveInvoiceAsync(booking.Id, CompanyData(), InvoiceDataSource.Host);

            await rig.FromOctorate().HandleAsync(Created(booking, BookingEventOrigin.Octorate), Ct);
            await rig.WriteBack().HandleAsync(Saved(booking.Id, InvoiceDataSource.Host), Ct);

            // The strict client has no setup: a call would have failed the test. The row is the host's, untouched.
            rig.Client.VerifyNoOtherCalls();
            Assert.Null((await rig.RowAsync(booking.Id)).OctorateBillingHash);
        }
    }

    // --- The client and its mapping ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Client_ReadsTheBillingRecordOfTheBooker_AndARefusalIsNeverAnEmptyResult()
    {
        var http = new RecordingHandler();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var client = NewClient(http, scope, new FixedTimeProvider(Now));
        async Task<OctorateBillingData?> ReadAsync()
        {
            using (scope.Use(OrgA))
                return await client.GetBookerBillingDataAsync(AccommodationId, ReservationId, Ct);
        }

        // The record the reservation names (invoiceHolderId) holds a company, in the members of the GuestDTO of the OpenAPI: its billing members are
        // read, the guest's contacts and the PEC are not (the type has no member for the first, and Octorate never returns the second).
        http.Answer(HttpStatusCode.OK, $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": {{CustomerId}} }""");
        http.Answer(
            HttpStatusCode.OK,
            $$"""
            { "id": {{CustomerId}}, "firstname": "Anna", "lastname": "Verdi", "email": "{{GuestEmail}}", "mobile": "{{GuestPhone}}",
              "company": "{{CompanyName}}", "vatCode": "{{VatNumber}}", "fiscalCode": "12345678903", "address": "Via del Sale Rosa 42/B",
              "zip": "22100", "city": "Cernobbio", "district": "CO", "country": "IT", "destinationOffice": "ABC1234",
              "legalMail": "{{CompanyPec}}", "legalMailAvailable": true, "newsletterConcent": true }
            """);
        var company = await ReadAsync();

        Assert.NotNull(company);
        Assert.True(company.IsCompany);
        Assert.Equal(CompanyName, company.Name);
        Assert.Equal(VatNumber, company.VatNumber);
        Assert.Equal("12345678903", company.FiscalCode);
        Assert.Equal("Via del Sale Rosa 42/B", company.Address);
        Assert.Equal("22100", company.PostalCode);
        Assert.Equal("Cernobbio", company.City);
        Assert.Equal("IT", company.CountryCode);
        Assert.Equal("ABC1234", company.SdiRecipientCode);

        // The province is not read (no member of the schema is known to hold it) and the PEC never is (write-only): a record has neither.
        Assert.Null(company.Province);
        Assert.Null(company.Pec);
        Assert.EndsWith($"/rest/v1/guests/{AccommodationId}/{CustomerId}", http.Requests[1].Uri.AbsolutePath);
        Assert.DoesNotContain(GuestEmail, company.ToString(), StringComparison.Ordinal);

        // A record that holds only the guest's name is no billing data; the name of a person is firstname and lastname, whatever the case.
        http.Answer(HttpStatusCode.OK, $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": {{CustomerId}} }""");
        http.Answer(HttpStatusCode.OK, $$"""{ "id": {{CustomerId}}, "firstname": "Anna", "lastname": "Verdi", "email": "{{GuestEmail}}" }""");
        Assert.Null(await ReadAsync());

        http.Answer(HttpStatusCode.OK, $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": {{CustomerId}} }""");
        http.Answer(HttpStatusCode.OK, """{ "firstName": "Anna", "lastName": "Verdi", "fiscalCode": "RSSMRA80A01H501U" }""");
        var person = await ReadAsync();
        Assert.NotNull(person);
        Assert.False(person.IsCompany);
        Assert.Equal("Anna Verdi", person.Name);
        Assert.Equal("RSSMRA80A01H501U", person.FiscalCode);

        // Octorate refuses the guest's data (the outbound IPs): a typed error, never an empty result.
        http.Answer(HttpStatusCode.OK, $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": {{CustomerId}} }""");
        http.Answer(HttpStatusCode.Forbidden, """{ "type": "ApiSecurityException" }""");
        var refusal = await Assert.ThrowsAsync<OctorateAuthException>(ReadAsync);
        Assert.Equal(OctorateAuthException.RefusedCode, refusal.Code);
    }

    [Fact]
    public async Task Client_WithoutACustomerRecord_ReadsNothing_WritesNothing_AndSaysSo()
    {
        var http = new RecordingHandler();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var client = NewClient(http, scope, new FixedTimeProvider(Now));
        var data = new OctorateBillingData { IsCompany = true, Name = CompanyName, VatNumber = VatNumber };

        using (scope.Use(OrgA))
        {
            // The reservation names no customer record, or only a guest of the stay (a guestId, the id of a guest of the stay: another table, whose id
            // could be another customer's): the read is empty (nothing is guessed), the write sends nothing, makes no record and answers false.
            foreach (var answer in new[]
                     {
                         $$"""{ "id": "{{ReservationId}}" }""",
                         $$"""{ "id": "{{ReservationId}}", "guestId": {{CustomerId}}, "guests": [ { "id": 67152873, "firstName": "Anna" } ] }""",
                         $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": 0 }""",
                         $$"""{ "id": "{{ReservationId}}", "invoiceHolderId": "not a number" }""",
                     })
            {
                http.Answer(HttpStatusCode.OK, answer);
                Assert.Null(await client.GetBookerBillingDataAsync(AccommodationId, ReservationId, Ct));

                http.Answer(HttpStatusCode.OK, answer);
                Assert.False(await client.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, data, Ct));
            }

            Assert.Equal(8, http.Requests.Count);
            Assert.All(http.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
            Assert.All(http.Requests, request => Assert.EndsWith($"/rest/v1/reservation/{AccommodationId}/{ReservationId}", request.Uri.AbsolutePath));

            // Data with nothing to write (a name alone, a company's denomination alone) is refused before Octorate is called.
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, new OctorateBillingData { Name = "Mario Rossi" }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.UpsertBookerBillingDataAsync(AccommodationId, ReservationId, new OctorateBillingData { IsCompany = true, Name = CompanyName }, Ct));
            Assert.Equal(8, http.Requests.Count);
        }
    }

    [Fact]
    public void BillingData_BodyAndHash_ChangeOnlyWithWhatIsWritten()
    {
        var company = new OctorateBillingData
        {
            IsCompany = true,
            Name = CompanyName,
            VatNumber = VatNumber,
            Address = "Via del Sale Rosa 42/B",
            City = "Cernobbio",
        };
        var hash = OctorateBillingMapping.HashOf(company);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, OctorateBillingMapping.HashOf(company with { }));
        Assert.NotEqual(hash, OctorateBillingMapping.HashOf(company with { City = "Como" }));
        Assert.NotEqual(hash, OctorateBillingMapping.HashOf(company with { Pec = CompanyPec }));
        Assert.DoesNotContain(CompanyName, hash, StringComparison.Ordinal);

        // A person's name is never written, so it is neither in the body nor in the hash, and a name alone is nothing to write.
        var person = new OctorateBillingData { Name = "Mario Rossi", FiscalCode = FiscalCode };
        Assert.DoesNotContain("Mario", OctorateBillingMapping.WriteBody(person), StringComparison.Ordinal);
        Assert.Equal(OctorateBillingMapping.HashOf(person), OctorateBillingMapping.HashOf(person with { Name = "Mario Rossi Junior" }));
        Assert.False(OctorateBillingMapping.HasMembersToWrite(new OctorateBillingData { Name = "Mario Rossi" }));
        Assert.True(OctorateBillingMapping.HasMembersToWrite(person));

        // The members of the OpenAPI, and only those that are known to be writable: the denomination of a company, the code of the SDI and the province
        // are in the model and never in the body, and the country only goes with another member (alone it would rewrite the country of a record).
        var everything = new OctorateBillingData
        {
            IsCompany = true,
            Name = CompanyName,
            FiscalCode = "12345678903",
            VatNumber = VatNumber,
            Address = "Via del Sale Rosa 42/B",
            PostalCode = "22100",
            City = "Cernobbio",
            Province = "CO",
            CountryCode = "IT",
            SdiRecipientCode = "ABC1234",
            Pec = CompanyPec,
        };
        using (var body = JsonDocument.Parse(OctorateBillingMapping.WriteBody(everything)))
        {
            Assert.Equal(
                ["fiscalCode", "vatCode", "address", "zip", "city", "country", "legalMail"],
                body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(OctorateBillingMapping.WrittenMembers, body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }

        Assert.Equal("""{"vatCode":"IT12345678903","country":"IT"}""", OctorateBillingMapping.WriteBody(new OctorateBillingData { VatNumber = VatNumber, CountryCode = "IT" }));
        Assert.False(OctorateBillingMapping.HasMembersToWrite(new OctorateBillingData { IsCompany = true, Name = CompanyName, CountryCode = "IT" }));
        Assert.False(OctorateBillingMapping.HasMembersToWrite(new OctorateBillingData { Province = "CO", SdiRecipientCode = "ABC1234" }));
        Assert.Equal(OctorateBillingMapping.EmptyBody, OctorateBillingMapping.WriteBody(new OctorateBillingData()));

        // The invoice data of a booking becomes the billing data (street and number on one line) and has nothing to send before the host types a recipient.
        var view = new InvoiceDataView(
            Guid.NewGuid(), true, Now.UtcDateTime, InvoiceRecipientKind.Company, CompanyName, null, VatNumber, "Via Roma", "12", "00100", "Roma", "RM", "IT", null, null,
            InvoiceDataSource.Host, Now.UtcDateTime);
        var billing = OctorateBillingData.From(view);
        Assert.NotNull(billing);
        Assert.True(billing.IsCompany);
        Assert.Equal("Via Roma 12", billing.Address);
        Assert.Null(OctorateBillingData.From(view with { Name = null, VatNumber = null, AddressStreet = null, AddressCivic = null, PostalCode = null, City = null, Province = null }));
        Assert.Equal(nameof(OctorateBillingData), company.ToString());
    }

    // --- Helpers -------------------------------------------------------------------------------------------------------------------------

    private static SaveInvoiceDataCommand CompanyData() => new()
    {
        RecipientKind = InvoiceRecipientKind.Company,
        Name = CompanyName,
        VatNumber = VatNumber,
        AddressStreet = "Via del Sale Rosa 42/B",
        City = "Cernobbio",
    };

    private static DomainEvent Created(Booking booking, BookingEventOrigin origin) =>
        new(
            Guid.NewGuid(),
            OrgA,
            DomainEventType.BookingCreated,
            booking.Id,
            Now.UtcDateTime,
            JsonSerializer.Serialize(
                new BookingEventPayload(
                    booking.Id, booking.PropertyId, booking.Source, origin, BookingStatus.Confirmed, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3))));

    private static DomainEvent Saved(Guid bookingId, InvoiceDataSource source) =>
        new(
            Guid.NewGuid(),
            OrgA,
            DomainEventType.InvoiceDataSaved,
            bookingId,
            Now.UtcDateTime,
            JsonSerializer.Serialize(new InvoiceDataSavedPayload(bookingId, source)));

    private static OctorateApiClient NewClient(HttpMessageHandler handler, IOctorateOrgScope scope, TimeProvider clock)
    {
        var tokens = new Mock<IOctorateTokenService>();
        tokens.Setup(t => t.GetAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("token");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
            .Build();
        return new OctorateApiClient(
            new HttpClient(handler),
            tokens.Object,
            new OctorateRateLimiter(clock),
            scope,
            configuration,
            clock,
            NullLogger<OctorateApiClient>.Instance);
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

        public List<RecordedRequest> Requests { get; } = [];

        public void Answer(HttpStatusCode status, string body) => _answers.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            var (status, answer) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>
    /// What the handlers need, built the way the dispatcher builds them: a new unit of work (context) for each event, under the org of the event. The
    /// Octorate client is strict: a call nobody set up fails the test.
    /// </summary>
    private sealed class Rig
    {
        public Rig(TestFeatureFlags? flags = null)
        {
            Flags = flags ?? new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.OctorateBillingSync);
            Limiter = new OctorateRateLimiter(Clock);
            Targets.Setup(t => t.AccommodationOfAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(AccommodationId);
            Issuer
                .Setup(i => i.ResolveIssuerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerOctorate));
        }

        public string DatabaseName { get; } = $"octorate-billing-{Guid.NewGuid():N}";

        public FakeTimeProvider Clock { get; } = new(Now);

        /// <summary>The bucket of calls to Octorate, one for the application (a singleton), on the clock of the rig.</summary>
        public OctorateRateLimiter Limiter { get; }

        public ITenantContext Tenant { get; } = new FixedTenantContext(OrgA);

        public TestFeatureFlags Flags { get; }

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IDocumentIssuerService> Issuer { get; } = new();

        public Mock<IOctorateApiClient> Client { get; } = new(MockBehavior.Strict);

        public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DatabaseName).Options, Tenant);

        public InvoiceDataFromOctorateHandler FromOctorate(ILogger<InvoiceDataFromOctorateHandler>? logger = null)
        {
            var db = NewDb();
            return new InvoiceDataFromOctorateHandler(
                db,
                Flags,
                Targets.Object,
                Client.Object,
                new OctorateOrgScope(Tenant),
                new InvoiceDataService(db, new DomainEventRecorder(db, Tenant, Clock), Clock),
                Limiter,
                Clock,
                logger ?? NullLogger<InvoiceDataFromOctorateHandler>.Instance);
        }

        public OctorateBillingWriteBackHandler WriteBack(
            IOctorateApiClient? client = null,
            IOctorateOrgScope? scope = null,
            ILogger<OctorateBillingWriteBackHandler>? logger = null) =>
            new(
                NewDb(),
                Flags,
                Issuer.Object,
                Targets.Object,
                client ?? Client.Object,
                scope ?? new OctorateOrgScope(Tenant),
                Clock,
                logger ?? NullLogger<OctorateBillingWriteBackHandler>.Instance);

        /// <summary>A booking of the org that Octorate knows by <paramref name="reservationId"/>, its stay ending on 2026-12-03 unless said otherwise.</summary>
        public async Task<Booking> SeedBookingAsync(
            string? reservationId = ReservationId,
            BookingSource source = BookingSource.BookingCom,
            DateTime? checkOut = null,
            Guid? guestId = null,
            BookingStatus status = BookingStatus.Confirmed)
        {
            await using var db = NewDb();
            var booking = new Booking
            {
                OrgId = OrgA,
                PropertyId = Guid.NewGuid(),
                GuestId = guestId ?? Guid.NewGuid(),
                CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = checkOut ?? new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = status,
                Source = source,
                OctorateReservationId = reservationId,
            };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking;
        }

        public async Task SaveInvoiceAsync(Guid bookingId, SaveInvoiceDataCommand command, InvoiceDataSource source)
        {
            await using var db = NewDb();
            await new InvoiceDataService(db, new DomainEventRecorder(db, Tenant, Clock), Clock)
                .SaveAsync(bookingId, command, source, source == InvoiceDataSource.Host ? Owner : null);
        }

        public async Task<BookingInvoiceData> RowAsync(Guid bookingId) =>
            await RowOrNullAsync(bookingId) ?? throw new InvalidOperationException($"Booking {bookingId} has no invoice data.");

        public async Task<BookingInvoiceData?> RowOrNullAsync(Guid bookingId)
        {
            await using var db = NewDb();
            return await db.BookingInvoiceData.AsNoTracking().SingleOrDefaultAsync(d => d.BookingId == bookingId);
        }

        public async Task<int> EventCountAsync(Guid bookingId)
        {
            await using var db = NewDb();
            return await db.OutboxEvents.AsNoTracking().CountAsync(e => e.EntityId == bookingId);
        }
    }
}
