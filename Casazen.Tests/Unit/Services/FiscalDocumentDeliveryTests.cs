using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E12/F01/US02/T01 (NF-83, ADR-022): the document of a stay sent to the guest by e-mail with a signed link of 30 days (by the host, or by itself when the issuer asks), a portal's relay address
/// accepted and a placeholder refused, and the two public reads the link opens, which answer every link that does not open with the same 404 and never log the token.
/// </summary>
public class FiscalDocumentDeliveryTests
{
    /// <summary>09:00 in Rome on 2026-11-20.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid OrgId = Guid.NewGuid();

    private const string BaseUrl = "https://casazen-app.test";

    private static readonly byte[] Pdf = "%PDF-1.4 TEST"u8.ToArray();

    // ---------------------------------------------------------------- the proofs

    /// <summary>
    /// PROOF TEST: privacy rule (the public endpoints). A link that does not open gets the same answer whatever is wrong with it: no token, an empty one, garbage, a token changed by a character, a token
    /// of another document, an expired one, one made for another purpose, a token for a document that does not exist, for a draft, a discarded draft, an owner invoice and an imported document.
    /// Both endpoints answer 404 <c>document_link_invalid</c> with the same title and detail, no word of the document, and read nothing but the token: no document is drawn and no tenant is entered.
    /// The token that opens its own document gets the summary and the PDF.
    /// </summary>
    [Fact]
    public async Task PublicDocumentLink_AnyInvalidTokenSame404_Test()
    {
        await using var kit = NewKit();
        var seeded = await SeedAsync(kit);
        var other = await SeedAsync(kit, document: d => d.Number = "2026/13");
        var draft = await SeedAsync(kit, document: d => (d.Status, d.Number, d.IssueDate) = (FiscalDocumentStatus.Draft, null, null));
        var discarded = await SeedAsync(kit, document: d => (d.Status, d.Number, d.IssueDate) = (FiscalDocumentStatus.Discarded, null, null));
        var ownerInvoice = await SeedAsync(kit, document: d => (d.Purpose, d.BookingId, d.Number) = (FiscalDocumentPurpose.OwnerInvoice, null, "PM2026/1"));
        var imported = await SeedAsync(kit, document: d => d.Source = FiscalDocumentSource.Octorate);
        var unknown = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddDays(30);
        var token = kit.Tokens.Create(seeded.DocumentId, future);
        var otherPurpose = kit.Keys.CreateProtector(ReviewLinkTokens.Purpose).ToTimeLimitedDataProtector().Protect(seeded.DocumentId.ToString("N"), future);

        var invalid = new (Guid Id, string? Token)[]
        {
            (seeded.DocumentId, null),
            (seeded.DocumentId, string.Empty),
            (seeded.DocumentId, "   "),
            (seeded.DocumentId, "not-a-token"),
            (seeded.DocumentId, Changed(token)),
            (seeded.DocumentId, kit.Tokens.Create(other.DocumentId, future)),
            (seeded.DocumentId, kit.Tokens.Create(seeded.DocumentId, DateTimeOffset.UtcNow.AddMinutes(-5))),
            (seeded.DocumentId, otherPurpose),
            (seeded.DocumentId, new string('A', 5000)),
            (other.DocumentId, token),
            (unknown, kit.Tokens.Create(unknown, future)),
            (draft.DocumentId, kit.Tokens.Create(draft.DocumentId, future)),
            (discarded.DocumentId, kit.Tokens.Create(discarded.DocumentId, future)),
            (ownerInvoice.DocumentId, kit.Tokens.Create(ownerInvoice.DocumentId, future)),
            (imported.DocumentId, kit.Tokens.Create(imported.DocumentId, future)),
        };

        var controller = PublicController(kit.Service);
        var answers = new List<(int? Status, object? Code, string? Title, string? Detail)>();
        foreach (var (id, text) in invalid)
        {
            var request = text is null ? null : new PublicFiscalDocumentLinkRequest { Token = text };
            foreach (var answer in new[] { await controller.Summary(id, request), await controller.Pdf(id, request) })
            {
                var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(answer).Value);
                answers.Add((problem.Status, problem.Extensions["code"], problem.Title, problem.Detail));

                // No word of the document: not its number, not its issuer, not the guest.
                var written = JsonSerializer.Serialize(problem);
                Assert.DoesNotContain("2026/1", written);
                Assert.DoesNotContain("TEST Casa Vacanze", written);
            }
        }

        var only = Assert.Single(answers.Distinct());
        Assert.Equal((StatusCodes.Status404NotFound, (object)FiscalDocumentDeliveryErrors.LinkInvalid), (only.Status, only.Code));
        Assert.Equal(invalid.Length * 2, answers.Count);
        Assert.Empty(kit.Entered);
        kit.Documents.Verify(d => d.GetPdfAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The token that opens its own document: the summary and the PDF.
        var own = new PublicFiscalDocumentLinkRequest { Token = token };
        var summary = Assert.IsType<PublicFiscalDocumentSummaryDto>(Assert.IsType<OkObjectResult>(await controller.Summary(seeded.DocumentId, own)).Value);
        Assert.Equal((seeded.DocumentId, "Receipt", "2026/12", new DateOnly(2026, 11, 19), 400m, "EUR"), (summary.Id, summary.Kind, summary.Number, summary.IssueDate, summary.Total, summary.Currency));
        var file = Assert.IsType<FileContentResult>(await controller.Pdf(seeded.DocumentId, own));
        Assert.Equal(("application/pdf", "documento-2026-12.pdf"), (file.ContentType, file.FileDownloadName));
        Assert.Equal(Pdf, file.FileContents);
        Assert.Equal([OrgId], kit.Entered);
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The token of the link is never written to a log, whatever happens to it: the e-mail that carries it, the reads that open, the ones that do not, the consumer of the issue,
    /// and the source of every file that touches it has no log statement that names it. The logs still say what happened (the ids), and never the address or the name of the guest.
    /// </summary>
    [Fact]
    public async Task PublicDocumentLink_TokenNeverLogged_Test()
    {
        await using var kit = NewKit();
        var seeded = await SeedAsync(kit, guest => (guest.Email, guest.FirstName) = ("secret.guest@example.com", "Segreta"));
        var handlerLog = new CapturingLogger<FiscalDocumentEmailHandler>();

        // The e-mail carries the token: the send, and the consumer of the issue that sends it by itself, log without it.
        await kit.Service.SendToGuestAsync(seeded.DocumentId);
        var token = TokenOf(Assert.Single(kit.Sent).Content);
        await new FiscalDocumentEmailHandler(kit.Service, new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance), handlerLog)
            .HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.FiscalDocumentIssued, seeded.DocumentId, Now.UtcDateTime, "{}"), CancellationToken.None);
        Assert.Equal(2, kit.Sent.Count);

        // The reads that open, and the ones that do not (a token changed by a character, a token of another document).
        var controller = PublicController(kit.Service);
        var other = await SeedAsync(kit);
        foreach (var text in new[] { token, Changed(token), kit.Tokens.Create(other.DocumentId, DateTimeOffset.UtcNow.AddDays(1)) })
        {
            var request = new PublicFiscalDocumentLinkRequest { Token = text };
            Assert.NotNull(await controller.Summary(seeded.DocumentId, request));
            Assert.NotNull(await controller.Pdf(seeded.DocumentId, request));
        }

        var logged = kit.Log.AllOutput + Environment.NewLine + handlerLog.AllOutput;
        Assert.Contains(seeded.DocumentId.ToString(), logged);
        Assert.Contains(seeded.DocumentId.ToString(), handlerLog.AllOutput);
        foreach (var secret in new[] { token, Changed(token), token[..24], token[^24..], "secret.guest@example.com", "Segreta" })
            Assert.DoesNotContain(secret, logged);

        // The same rule in the source: no log statement of these files names a token.
        string[] touchingTheToken =
        [
            "Casazen.Infrastructure/Services/FiscalDocumentDeliveryService.cs",
            "Casazen.Infrastructure/Services/FiscalDocumentEmailHandler.cs",
            "Casazen.Infrastructure/Services/FiscalDocumentLinkTokens.cs",
            "Casazen.Web/Controllers/PublicFiscalDocumentsController.cs",
        ];
        var offending = new List<string>();
        foreach (var file in touchingTheToken)
        {
            var source = File.ReadAllText(Path.Combine(SolutionRoot(), file));
            foreach (System.Text.RegularExpressions.Match statement in Regex.Matches(source, @"(?:logger|log)\s*\.\s*Log\w*\([^;]*;", RegexOptions.Singleline))
            {
                if (Regex.IsMatch(statement.Value, "token", RegexOptions.IgnoreCase))
                    offending.Add($"{file}: {statement.Value.Split('\n')[0].Trim()}");
            }
        }

        Assert.Empty(offending);
    }

    // ---------------------------------------------------------------- the e-mail

    [Fact]
    public async Task SendToGuest_QueuesTheEmailWithTheSignedLink_OfThirtyDays_InTheLanguageOfTheGuest()
    {
        await using var kit = NewKit();
        var italian = await SeedAsync(kit, guest => (guest.Email, guest.FirstName) = ("abc123@guest.booking.com", "Giulia"));
        var english = await SeedAsync(kit, guest => (guest.Email, guest.FirstName, guest.Country) = ("hans@example.com", "Hans", "Germany"), document: d => d.Number = "2026/13");

        var result = await kit.Service.SendToGuestAsync(italian.DocumentId);
        await kit.Service.SendToGuestAsync(english.DocumentId);

        // The result: the link stops opening 30 days after the e-mail was queued, and nothing but ids and that instant.
        Assert.Equal((italian.DocumentId, Now.UtcDateTime.AddDays(30)), (result.DocumentId, result.LinkExpiresAt));
        var (first, second) = (kit.Sent[0], kit.Sent[1]);
        Assert.Equal(("abc123@guest.booking.com", EmailTemplates.Names.FiscalDocumentDelivery), (first.To, first.Template));
        Assert.Equal("Ricevuta n. 2026/12 di TEST Casa Vacanze Srl", first.Content.Subject);
        Assert.Equal("Receipt no. 2026/13 from TEST Casa Vacanze Srl", second.Content.Subject);

        // Italian: the greeting, the issuer, the kind, the number, the day of the issue and the last day of the link in Rome time, a real value through every format of a date.
        Assert.Contains("Ciao Giulia,", first.Content.HtmlBody);
        Assert.Contains("<strong>TEST Casa Vacanze Srl</strong> ti ha inviato il documento <strong>Ricevuta n. 2026/12</strong> del 19/11/2026.", first.Content.HtmlBody);
        Assert.Contains("Il link è valido fino al 20/12/2026 09:00.", first.Content.HtmlBody);
        Assert.Contains("<html lang=\"it\">", first.Content.HtmlBody);
        Assert.Contains("Hello Hans,", second.Content.HtmlBody);
        Assert.Contains("<html lang=\"en\">", second.Content.HtmlBody);

        // The link opens the document it was made for, and only that one; it holds the id and the token, nothing else.
        var url = LinkOf(first.Content);
        var token = TokenOf(first.Content);
        Assert.StartsWith($"{BaseUrl}/documents/view/{italian.DocumentId:D}?token=", url);
        Assert.True(kit.Tokens.TryRead(token, out var tokenDocumentId));
        Assert.Equal(italian.DocumentId, tokenDocumentId);
        Assert.Equal(FiscalDocumentLinkRules.TokenPurpose, FiscalDocumentLinkTokens.Purpose);
        Assert.Equal("Casazen.FiscalDocument.Link", FiscalDocumentLinkRules.TokenPurpose);
        Assert.Equal(TimeSpan.FromDays(30), FiscalDocumentLinkRules.Lifetime);

        // Nothing of the money, the address of the guest, a portal or a supplier is in the words.
        foreach (var content in new[] { first.Content, second.Content })
        {
            Assert.DoesNotContain("400,00", content.HtmlBody);
            Assert.DoesNotContain("400.00", content.HtmlBody);
            Assert.DoesNotContain("guest.booking.com", content.HtmlBody);
            Assert.DoesNotContain("Octorate", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Booking", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Airbnb", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("abc123@guest.booking.com", true)]
    [InlineData("x.y+z@guest.airbnb.com", true)]
    [InlineData("guest@example.com", true)]
    [InlineData("  Padded@Example.com  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-an-address", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("Name <a@example.com>", false)]
    [InlineData("a@localhost", false)]
    [InlineData("ANON-0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e@deleted.local", false)]
    [InlineData("someone@mail.invalid", false)]
    public async Task SendToGuest_AcceptsTheRelayAddressesOfThePortals_AndRefusesWhatIsNotAnAddress(string email, bool accepted)
    {
        await using var kit = NewKit();
        var seeded = await SeedAsync(kit, guest => guest.Email = email);

        if (accepted)
        {
            await kit.Service.SendToGuestAsync(seeded.DocumentId);
            Assert.Equal(email.Trim(), Assert.Single(kit.Sent).To);
            return;
        }

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => kit.Service.SendToGuestAsync(seeded.DocumentId));
        Assert.Equal("document_recipient_email_invalid", refused.Code);
        Assert.Empty(kit.Sent);
    }

    [Fact]
    public async Task SendToGuest_AnErasedGuest_IsNeverSentTo_EvenWithAWellFormedAddress()
    {
        await using var kit = NewKit();
        var seeded = await SeedAsync(kit, guest => (guest.Email, guest.DataAnonymizedDate) = ("erased@example.com", Now.UtcDateTime.AddDays(-1)));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => kit.Service.SendToGuestAsync(seeded.DocumentId));

        Assert.Equal("document_recipient_email_invalid", refused.Code);
        Assert.Empty(kit.Sent);
    }

    [Theory]
    [InlineData("IT", "EUR")]
    [InlineData("CH", "CHF")]
    public async Task SendToGuest_AnInvoiceAReceiptAndACreditNote_AreSentTheSameWay_TheSwissOnesLikeTheItalianOnes(string country, string currency)
    {
        await using var kit = NewKit();
        var words = new List<string>();
        foreach (var (kind, word) in new[] { (DocumentKind.Invoice, "Invoice"), (DocumentKind.Receipt, "Receipt"), (DocumentKind.CreditNote, "Credit note") })
        {
            var seeded = await SeedAsync(
                kit,
                guest => (guest.Email, guest.Country) = ("guest@example.com", "Germany"),
                document: d =>
                {
                    d.Kind = kind;
                    d.Currency = currency;
                    d.IssuerSnapshot = new FiscalIssuerSnapshot { LegalName = $"TEST {country} Sagl", CountryCode = country }.ToJson();
                });
            await kit.Service.SendToGuestAsync(seeded.DocumentId);
            words.Add(word);
        }

        Assert.Equal(3, kit.Sent.Count);
        Assert.All(kit.Sent, sent => Assert.Equal(EmailTemplates.Names.FiscalDocumentDelivery, sent.Template));
        Assert.All(kit.Sent, sent => Assert.EndsWith($"from TEST {country} Sagl", sent.Content.Subject));
        for (var i = 0; i < words.Count; i++)
            Assert.StartsWith($"{words[i]} no. ", kit.Sent[i].Content.Subject);
    }

    [Fact]
    public async Task SendToGuest_OnlyAnIssuedGuestDocumentOfCasaZen_IsSent_AndAQueueThatRefusesIsNeverASuccess()
    {
        await using var kit = NewKit();
        var draft = await SeedAsync(kit, document: d => (d.Status, d.Number, d.IssueDate) = (FiscalDocumentStatus.Draft, null, null));
        var discarded = await SeedAsync(kit, document: d => (d.Status, d.Number, d.IssueDate) = (FiscalDocumentStatus.Discarded, null, null));
        var ownerInvoice = await SeedAsync(kit, document: d => (d.Purpose, d.BookingId) = (FiscalDocumentPurpose.OwnerInvoice, null));
        var imported = await SeedAsync(kit, document: d => d.Source = FiscalDocumentSource.Octorate);

        async Task<string> CodeOfAsync(Guid id) => (await Assert.ThrowsAsync<DomainRuleException>(() => kit.Service.SendToGuestAsync(id))).Code;

        Assert.Equal("document_not_issued", await CodeOfAsync(draft.DocumentId));
        Assert.Equal("document_not_issued", await CodeOfAsync(discarded.DocumentId));
        Assert.Equal("document_owner_invoice", await CodeOfAsync(ownerInvoice.DocumentId));
        Assert.Equal("document_read_only", await CodeOfAsync(imported.DocumentId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => kit.Service.SendToGuestAsync(Guid.NewGuid()));
        Assert.Equal("fiscal_document_not_found", missing.Code);
        Assert.Empty(kit.Sent);

        // A queue that says no: not a success, and no «sent» anywhere.
        await using var refusing = NewKit(queueAccepts: false);
        var refused = await SeedAsync(refusing);
        var notSent = await Assert.ThrowsAsync<DomainRuleException>(() => refusing.Service.SendToGuestAsync(refused.DocumentId));
        Assert.Equal("document_email_not_sent", notSent.Code);

        // No public address of the web app: no link can be written, and nothing is queued.
        await using var unlinked = NewKit(linksConfigured: false);
        var withoutLink = await SeedAsync(unlinked);
        var noLink = await Assert.ThrowsAsync<DomainRuleException>(() => unlinked.Service.SendToGuestAsync(withoutLink.DocumentId));
        Assert.Equal("document_email_not_sent", noLink.Code);
        Assert.Empty(unlinked.Sent);
    }

    // ---------------------------------------------------------------- the automatic send

    [Fact]
    public async Task SendAutomatically_OnlyWhenTheIssuerAsks_AndNoAddressOrNoGuestDocumentIsAnAnswerNotAFailure()
    {
        await using var asking = NewKit(emailGuestDocuments: true);
        var issued = await SeedAsync(asking);
        var noAddress = await SeedAsync(asking, guest => guest.Email = string.Empty);
        var ownerInvoice = await SeedAsync(asking, document: d => (d.Purpose, d.BookingId) = (FiscalDocumentPurpose.OwnerInvoice, null));
        var draft = await SeedAsync(asking, document: d => (d.Status, d.Number, d.IssueDate) = (FiscalDocumentStatus.Draft, null, null));

        Assert.Equal(FiscalDocumentSendOutcome.Queued, await asking.Service.SendAutomaticallyAsync(issued.DocumentId));
        Assert.Equal(FiscalDocumentSendOutcome.NoAddress, await asking.Service.SendAutomaticallyAsync(noAddress.DocumentId));
        Assert.Equal(FiscalDocumentSendOutcome.NotSendable, await asking.Service.SendAutomaticallyAsync(ownerInvoice.DocumentId));
        Assert.Equal(FiscalDocumentSendOutcome.NotSendable, await asking.Service.SendAutomaticallyAsync(draft.DocumentId));
        Assert.Single(asking.Sent);

        // The profile says no: nothing goes, whatever the document.
        await using var notAsking = NewKit(emailGuestDocuments: false);
        var waiting = await SeedAsync(notAsking);
        Assert.Equal(FiscalDocumentSendOutcome.NotAsked, await notAsking.Service.SendAutomaticallyAsync(waiting.DocumentId));
        Assert.Empty(notAsking.Sent);

        // An environment that cannot send at all (no e-mail provider, or no public address of the web app) is an answer: there is nothing to try again.
        await using var noProvider = NewKit(emailConfigured: false);
        await using var noLinks = NewKit(linksConfigured: false);
        foreach (var kit in new[] { noProvider, noLinks })
        {
            var seeded = await SeedAsync(kit);
            Assert.Equal(FiscalDocumentSendOutcome.NotConfigured, await kit.Service.SendAutomaticallyAsync(seeded.DocumentId));
            Assert.Empty(kit.Sent);
        }

        // A queue that refuses while it could send is a failure of the send, not an answer: it is thrown, for the outbox to try again.
        await using var refusing = NewKit(emailGuestDocuments: true, queueAccepts: false);
        var refused = await SeedAsync(refusing);
        var notSent = await Assert.ThrowsAsync<DomainRuleException>(() => refusing.Service.SendAutomaticallyAsync(refused.DocumentId));
        Assert.Equal("document_email_not_sent", notSent.Code);
    }

    [Fact]
    public async Task Handler_SendsOnTheTwoIssueEvents_WhileTheIssuanceIsOn_AnAnswerIsNeverAFailure_AndAFailureIsDeliveredAgain()
    {
        var delivery = new Mock<IFiscalDocumentDeliveryService>();
        var log = new CapturingLogger<FiscalDocumentEmailHandler>();
        var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var handler = new FiscalDocumentEmailHandler(delivery.Object, flags, log);
        var id = Guid.NewGuid();
        delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(FiscalDocumentSendOutcome.Queued);

        // Only the two events of an issue, and only for an entity.
        foreach (var type in new[] { DomainEventType.FiscalDocumentIssued, DomainEventType.FiscalDocumentCreditNoteIssued })
            await handler.HandleAsync(Event(type, id), CancellationToken.None);
        foreach (var type in new[] { DomainEventType.CheckOutCompleted, DomainEventType.BookingModified, DomainEventType.PaymentRefunded })
            await handler.HandleAsync(Event(type, id), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, Guid.Empty), CancellationToken.None);
        delivery.Verify(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>()), Times.Exactly(2));
        delivery.VerifyNoOtherCalls();

        // Nothing while the issuance is off.
        flags.Set(FeatureFlags.FiscalDocumentIssuance, false);
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None);
        delivery.VerifyNoOtherCalls();
        flags.Set(FeatureFlags.FiscalDocumentIssuance, true);

        // Every answer of the service, an answer of the rules and a document that is gone are logged and not delivered again.
        foreach (var outcome in Enum.GetValues<FiscalDocumentSendOutcome>())
        {
            delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
            await handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None);
        }

        delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new DomainRuleException("document_not_issued", "FiscalDocumentNotIssued"));
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None);
        delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("gone"));
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None);

        // A queue that refused while it could send, and any other failure, are thrown again for the outbox (nothing was queued, so nothing is sent twice), with the code or the type of the error only.
        delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new DomainRuleException("document_email_not_sent", "FiscalDocumentEmailNotSent"));
        await Assert.ThrowsAsync<DomainRuleException>(() => handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None));
        delivery.Setup(d => d.SendAutomaticallyAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("the database said guest@example.com"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Event(DomainEventType.FiscalDocumentIssued, id), CancellationToken.None));

        Assert.Contains(nameof(FiscalDocumentSendOutcome.NotConfigured), log.AllOutput);
        Assert.Contains("document_not_issued", log.AllOutput);
        Assert.Contains("document_email_not_sent", log.AllOutput);
        Assert.Contains(nameof(InvalidOperationException), log.AllOutput);
        Assert.DoesNotContain("guest@example.com", log.AllOutput);
    }

    // ---------------------------------------------------------------- the pieces

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void Email_EveryDynamicValueIsHtmlEncoded_InBothLanguages(string culture)
    {
        const string payload = "<a href=\"https://phish.example\">Paga qui</a>";
        const string encoded = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";

        var content = EmailTemplates.FiscalDocumentDelivery(
            CultureInfo.GetCultureInfo(culture),
            payload,
            payload,
            DocumentKind.Invoice,
            payload,
            new DateTime(2026, 11, 19),
            $"{BaseUrl}/documents/view/{Guid.NewGuid():D}?token=abc_DEF-123",
            Now.UtcDateTime.AddDays(30));

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.DoesNotContain("phish.example\">", content.HtmlBody);
        Assert.Contains(encoded, content.HtmlBody);
        Assert.Contains($"<html lang=\"{culture[..2]}\">", content.HtmlBody);
        Assert.Contains("?token=abc_DEF-123", content.HtmlBody);

        // A guest with no first name is greeted without a gap.
        var nameless = EmailTemplates.FiscalDocumentDelivery(CultureInfo.GetCultureInfo(culture), " ", "TEST Srl", DocumentKind.Receipt, "2026/1", new DateTime(2026, 11, 19), BaseUrl, Now.UtcDateTime);
        Assert.DoesNotContain(" ,", nameless.HtmlBody);
    }

    [Fact]
    public void LinkTokens_OpenOnlyForTheirDocument_AndNotForAnotherPurposeAnExpiredOrAChangedToken()
    {
        var keys = new EphemeralDataProtectionProvider();
        var tokens = new FiscalDocumentLinkTokens(keys);
        var document = Guid.NewGuid();
        var token = tokens.Create(document, DateTimeOffset.UtcNow.AddDays(30));

        Assert.True(tokens.TryRead(token, out var read));
        Assert.Equal(document, read);
        Assert.True(tokens.TryRead($"  {token}  ", out _));

        Assert.False(tokens.TryRead(Changed(token), out var changed));
        Assert.Equal(Guid.Empty, changed);
        Assert.False(tokens.TryRead(tokens.Create(document, DateTimeOffset.UtcNow.AddSeconds(-1)), out _));
        Assert.False(tokens.TryRead(keys.CreateProtector(ReviewLinkTokens.Purpose).ToTimeLimitedDataProtector().Protect(document.ToString("N"), DateTimeOffset.UtcNow.AddDays(1)), out _));
        Assert.False(new FiscalDocumentLinkTokens(new EphemeralDataProtectionProvider()).TryRead(token, out _));
        Assert.False(tokens.TryRead(null, out _));
        Assert.False(tokens.TryRead(string.Empty, out _));
        Assert.False(tokens.TryRead(new string('A', 1025), out _));
        Assert.False(tokens.TryRead(tokens.Create(Guid.Empty, DateTimeOffset.UtcNow.AddDays(1)), out _));

        // The link is a few hundred characters of letters, digits, dash and underscore: nothing that needs escaping in an address.
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.InRange(token.Length, 40, 400);
    }

    // ---------------------------------------------------------------- the endpoints and the limit

    [Fact]
    public async Task SendEmail_IsForWhoMayWriteThePaymentsOfTheBooking_AndNotFoundForTheOthers()
    {
        var bookingId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var expires = Now.UtcDateTime.AddDays(30);
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.GetAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FiscalDocument { Id = documentId, OrgId = OrgId, BookingId = bookingId, Source = FiscalDocumentSource.CasaZen, Status = FiscalDocumentStatus.Issued });
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));
        var delivery = new Mock<IFiscalDocumentDeliveryService>();
        delivery.Setup(d => d.SendToGuestAsync(documentId, It.IsAny<CancellationToken>())).ReturnsAsync(new FiscalDocumentDeliveryResult(documentId, expires));

        // Somebody who may not write the payments of the booking: the same 404 as a document that is not there, and nothing is sent.
        var denied = await FiscalController(documents.Object, resources.Object, allowed: false).SendEmail(documentId, delivery.Object);
        Assert.Equal(StatusCodes.Status404NotFound, (denied.Result as IStatusCodeActionResult)?.StatusCode);
        var missing = await FiscalController(documents.Object, resources.Object, allowed: true).SendEmail(Guid.NewGuid(), delivery.Object);
        Assert.Equal(StatusCodes.Status404NotFound, (missing.Result as IStatusCodeActionResult)?.StatusCode);
        delivery.VerifyNoOtherCalls();

        var sent = await FiscalController(documents.Object, resources.Object, allowed: true).SendEmail(documentId, delivery.Object);
        var body = Assert.IsType<FiscalDocumentEmailQueuedDto>(Assert.IsType<OkObjectResult>(sent.Result).Value);
        Assert.Equal((documentId, true, expires), (body.DocumentId, body.Queued, body.LinkExpiresAt));

        // The action is behind the permission to write the payments and the flag of the issuance, and nothing else is in its answer.
        var action = typeof(FiscalDocumentsController).GetMethod(nameof(FiscalDocumentsController.SendEmail))!;
        Assert.Equal(CasazenPolicies.PaymentWrite, action.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal(FeatureFlags.FiscalDocumentIssuance, action.GetCustomAttribute<FeatureGateAttribute>()?.Flag);
        Assert.Equal("{id:guid}/send-email", action.GetCustomAttributes<HttpPostAttribute>().Single().Template);
        Assert.Equal(["DocumentId", "Queued", "LinkExpiresAt"], typeof(FiscalDocumentEmailQueuedDto).GetProperties().Select(p => p.Name).ToList());
    }

    [Fact]
    public void PublicEndpoints_AreAnonymous_PostOnly_WithTheTokenInTheBody_BoundedInSize_PrivateAndRateLimitedPerIp()
    {
        var controller = typeof(PublicFiscalDocumentsController);
        Assert.NotNull(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal(RateLimitPolicies.PublicFiscalDocumentDownload, controller.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("api/public/fiscal-documents", controller.GetCustomAttribute<RouteAttribute>()?.Template);

        var actions = new[] { nameof(PublicFiscalDocumentsController.Summary), nameof(PublicFiscalDocumentsController.Pdf) }.Select(name => controller.GetMethod(name)!).ToList();
        Assert.Equal(["{id:guid}", "{id:guid}/pdf"], actions.Select(a => a.GetCustomAttributes<HttpPostAttribute>().Single().Template).ToList());
        foreach (var action in actions)
        {
            // The token is in the body: no parameter of the query or of the route is called after it, and the body is small.
            Assert.DoesNotContain(action.GetParameters(), p => p.GetCustomAttribute<FromQueryAttribute>() is not null || p.Name!.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(action.GetCustomAttribute<RequestSizeLimitAttribute>());
        }

        // The policy of the limit: per client IP (not by person, not by token), 20 a minute, set by its own variable like the others.
        var policy = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.PublicFiscalDocumentDownload);
        Assert.Equal((20, TimeSpan.FromMinutes(1), false, false), (policy.DefaultPermitLimit, policy.DefaultWindow, policy.PartitionByToken, policy.PartitionByUser));
    }

    // ---------------------------------------------------------------- helpers

    private static DomainEvent Event(DomainEventType type, Guid entityId) =>
        new(Guid.NewGuid(), OrgId, type, entityId, Now.UtcDateTime, "{}");

    /// <summary>The token with one character of its middle changed: the same length, never a token of ours.</summary>
    private static string Changed(string token)
    {
        var middle = token.Length / 2;
        return token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];
    }

    /// <summary>The link of the e-mail: the address of its button, as the reader's client sees it.</summary>
    private static string LinkOf(EmailContent content)
    {
        var match = Regex.Match(content.HtmlBody, "<a href=\"(?<url>[^\"]+)\" style=");
        Assert.True(match.Success, "the e-mail has no button");
        return System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value);
    }

    /// <summary>The token of the link of the e-mail: what follows <c>?token=</c>.</summary>
    private static string TokenOf(EmailContent content)
    {
        var url = LinkOf(content);
        return url[(url.IndexOf("?token=", StringComparison.Ordinal) + "?token=".Length)..];
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    private static PublicFiscalDocumentsController PublicController(IFiscalDocumentDeliveryService delivery) => new(delivery)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider() },
        },
    };

    private static FiscalDocumentsController FiscalController(IFiscalDocumentService documents, IHostResourceLookup resources, bool allowed)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostScope(OrgId));
        var requestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|collaborator"), new Claim(ClaimTypes.NameIdentifier, "auth0|collaborator")], "test");
        return new FiscalDocumentsController(documents, resources, authorization.Object, org.Object, scopes.Object, NullLogger<FiscalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = requestServices },
            },
        };
    }

    private static Kit NewKit(bool emailGuestDocuments = true, bool queueAccepts = true, bool linksConfigured = true, bool emailConfigured = true)
    {
        var kit = new Kit(queueAccepts, linksConfigured, emailConfigured);
        kit.Db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = "TEST Casa Vacanze",
            Slug = $"test-{OrgId:N}"[..20],
            DisplayName = "TEST Casa Vacanze",
            ContactEmail = "test@example.com",
            HasPartitaIva = true,
            PartitaIvaNumber = "01234567897",
            FiscalCode = "01234567897",
        });
        kit.Db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = DocumentRegime.VatOrdinary,
            LegalName = "TEST Casa Vacanze Srl",
            CountryCode = "IT",
            EmailGuestDocuments = emailGuestDocuments,
        });
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        kit.Db.Properties.Add(property);
        kit.Db.SaveChanges();
        kit.PropertyId = property.Id;
        return kit;
    }

    private static int _sequence;

    /// <summary>An issued receipt of 400 EUR of a booking whose guest is «Test Guest»; <paramref name="guest"/> and <paramref name="document"/> change what a test needs different.</summary>
    private static async Task<Seeded> SeedAsync(Kit kit, Action<Guest>? guest = null, Action<FiscalDocument>? document = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var person = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = "guest@example.com" };
        guest?.Invoke(person);
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = kit.PropertyId,
            GuestId = person.Id,
            CheckInDate = new DateTime(2026, 11, 16),
            CheckOutDate = new DateTime(2026, 11, 19),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            CleaningFee = 50m,
            TotalPrice = 400m,
        };
        var fiscal = new FiscalDocument
        {
            OrgId = OrgId,
            BookingId = booking.Id,
            PropertyId = kit.PropertyId,
            Source = FiscalDocumentSource.CasaZen,
            Purpose = FiscalDocumentPurpose.GuestStay,
            Kind = DocumentKind.Receipt,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 100 + sequence,
            Number = "2026/12",
            IssueDate = new DateOnly(2026, 11, 19),
            Currency = "EUR",
            TaxableBase = 400m,
            Total = 400m,
            SdiStatus = FiscalDocumentSdiStatuses.NotApplicable,
            IssuerSnapshot = new FiscalIssuerSnapshot { LegalName = "TEST Casa Vacanze Srl", CountryCode = "IT" }.ToJson(),
        };
        document?.Invoke(fiscal);
        kit.Db.Guests.Add(person);
        kit.Db.Bookings.Add(booking);
        kit.Db.FiscalDocuments.Add(fiscal);
        await kit.Db.SaveChangesAsync();
        return new Seeded(fiscal.Id, booking.Id);
    }

    private sealed record Seeded(Guid DocumentId, Guid BookingId);

    private sealed record SentEmail(string? To, EmailContent Content, string Template);

    private sealed class NoTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => false;
    }

    private sealed class RecordingTenantScope(List<Guid> entered) : IBackgroundTenantScope
    {
        public void EnterOrg(Guid orgId) => entered.Add(orgId);
    }

    /// <summary>The service under test with a real in-memory database, a queue that keeps what it is given, the key ring of a test, and the document service of the PDF behind a mock.</summary>
    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Kit(bool queueAccepts, bool linksConfigured, bool emailConfigured)
        {
            Keys = new EphemeralDataProtectionProvider();
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalDelivery_{Guid.NewGuid()}").Options, new NoTenant(), Keys);
            Tokens = new FiscalDocumentLinkTokens(Keys);
            Log = new CapturingLogger<FiscalDocumentDeliveryService>();
            Queue = new Mock<IEmailQueue>();
            Queue
                .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
                .Callback<string?, EmailContent, string>((to, content, template) => Sent.Add(new SentEmail(to, content, template)))
                .Returns(queueAccepts);
            Documents = new Mock<IFiscalDocumentService>();
            Documents.Setup(d => d.GetPdfAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Pdf);

            var services = new ServiceCollection();
            services.AddScoped<IBackgroundTenantScope>(_ => new RecordingTenantScope(Entered));
            services.AddScoped(_ => Documents.Object);
            _provider = services.BuildServiceProvider();

            var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = linksConfigured ? BaseUrl : null }));
            var email = Options.Create(new EmailOptions { ApiKey = emailConfigured ? "test-key" : null, FromAddress = "casazen@example.com" });
            Service = new FiscalDocumentDeliveryService(
                Db,
                Queue.Object,
                email,
                links,
                Tokens,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new FakeTimeProvider(Now),
                Log);
        }

        public IDataProtectionProvider Keys { get; }

        public AppDbContext Db { get; }

        public FiscalDocumentLinkTokens Tokens { get; }

        public CapturingLogger<FiscalDocumentDeliveryService> Log { get; }

        public Mock<IEmailQueue> Queue { get; }

        public Mock<IFiscalDocumentService> Documents { get; }

        public FiscalDocumentDeliveryService Service { get; }

        /// <summary>What the queue was asked to queue, in order: an e-mail that a refusing queue says no to is here too (it got the ask).</summary>
        public List<SentEmail> Sent { get; } = [];

        public List<Guid> Entered { get; } = [];

        public Guid PropertyId { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
