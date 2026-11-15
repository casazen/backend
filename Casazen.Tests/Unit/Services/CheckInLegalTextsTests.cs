using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E22/F01/US02/T02 proofs of the registry of the check-in legal texts: production serves only approved versions (privacy), a consent can refer only to a version the registry serves
/// and keeps the hash of its text (privacy), and a text is served only when its file is the one whose SHA-256 the manifest records (data).
/// </summary>
public sealed class CheckInLegalTextsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"checkin-legal-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Privacy: production serves the last approved version of each text and never a draft; every other environment serves the last version, flagged as a draft when it is one. A kind with
    /// nothing approved is not served at all in production. The files that ship today are all drafts: production serves nothing of them, the test environment serves both.
    /// </summary>
    [Fact]
    public void LegalTexts_ProductionServesOnlyApproved_Test()
    {
        WriteRegistry(
            ("biometric-consent", "consent-v1", "approved", Texts("Consenso approvato")),
            ("biometric-consent", "consent-v2", "draft", Texts("Consenso nuovo in bozza")),
            ("ticino-notice", "ticino-v1", "draft", Texts("Avviso in bozza")));

        var production = Registry("Production");
        var active = Assert.IsType<CheckInLegalVersion>(production.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent));
        Assert.Equal("consent-v1", active.Version);
        Assert.False(active.IsDraft);
        Assert.Null(production.GetActiveVersion(CheckInLegalTextKinds.TicinoNotice));
        Assert.Null(production.FindServedVersion(CheckInLegalTextKinds.BiometricConsent, "consent-v2"));
        Assert.Null(production.FindServedVersion(CheckInLegalTextKinds.TicinoNotice, "ticino-v1"));
        var forTicino = Assert.Single(production.GetForStay(documentNotArchived: true, "it"));
        Assert.Equal(("biometric-consent", "consent-v1", false), (forTicino.Kind, forTicino.Version, forTicino.IsDraft));
        Assert.Contains("Consenso approvato", forTicino.ContentHtml, StringComparison.Ordinal);

        var staging = Registry("Staging");
        Assert.Equal("consent-v2", staging.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent)!.Version);
        Assert.True(staging.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent)!.IsDraft);
        Assert.Equal(["biometric-consent", "ticino-notice"], staging.GetForStay(documentNotArchived: true, "it").Select(t => t.Kind));
        Assert.All(staging.GetForStay(documentNotArchived: true, "it"), text => Assert.True(text.IsDraft));

        // The texts that ship: production never serves a draft of them (today all are drafts, so it serves none); the test environment serves them, a draft with its flag.
        var shipped = new Dictionary<string, string?> { ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot() };
        var shippedProduction = new CheckInLegalTexts(Config(shipped), Env("Production"), NullLogger<CheckInLegalTexts>.Instance);
        Assert.All(shippedProduction.GetForStay(documentNotArchived: true, "it"), text => Assert.False(text.IsDraft));
        Assert.True(shippedProduction.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent) is null or { IsDraft: false });
        Assert.True(shippedProduction.FindServedVersion(CheckInLegalTextKinds.BiometricConsent, "checkin-biometric-2026-11") is null or { IsDraft: false });

        var shippedStaging = new CheckInLegalTexts(Config(shipped), Env("Staging"), NullLogger<CheckInLegalTexts>.Instance);
        var staged = shippedStaging.GetForStay(documentNotArchived: true, "it");
        Assert.Equal(["biometric-consent", "ticino-notice"], staged.Select(t => t.Kind));
        Assert.NotNull(shippedStaging.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent));
    }

    /// <summary>
    /// Privacy: a consent refers to the version the registry serves now; any other string (a free text, a version of the past, a draft in production) is refused with nothing recorded, and a
    /// consent that is recorded keeps the version and the hash of its text. With no text of the consent served the identity step is closed, an operator is told why, and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task Consent_UnknownVersionRefused_Test()
    {
        WriteRegistry(("biometric-consent", "consent-v2", "draft", Texts("Consenso 2")));
        var registry = Registry("Staging");
        var served = registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent)!;
        await using var rig = await FlowRig.CreateAsync();

        foreach (var free in new[] { "TEST-biometric-v1", "consent-v1", "CONSENT-V2", "x" })
        {
            var session = await rig.SessionAsync();
            var refused = await rig.Flow(registry).RecordConsentAsync(session, true, free, CancellationToken.None);

            Assert.Equal(BiometricConsentStatus.VersionUnknown, refused.Status);
            Assert.Null(session.BiometricConsentAt);
            Assert.Null(session.BiometricConsentVersion);
            Assert.Null(session.BiometricConsentTextHash);
        }

        var good = await rig.SessionAsync();
        var recorded = await rig.Flow(registry).RecordConsentAsync(good, true, " consent-v2 ", CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.Recorded, recorded.Status);
        Assert.NotNull(good.BiometricConsentAt);
        Assert.Equal("consent-v2", good.BiometricConsentVersion);
        Assert.Equal(served.TextHash, good.BiometricConsentTextHash);
        Assert.Equal(64, good.BiometricConsentTextHash!.Length);

        // Refusing needs no version and records no hash.
        var refusing = await rig.SessionAsync();
        Assert.Equal(BiometricConsentStatus.Refused, (await rig.Flow(registry).RecordConsentAsync(refusing, false, "anything", CancellationToken.None)).Status);
        Assert.Null(refusing.BiometricConsentTextHash);

        // Production, nothing approved: no text of the consent is served, the identity step is not offered and the operator is told why.
        var production = Registry("Production");
        var closed = await rig.SessionAsync();
        var state = (await rig.Flow(production).GetStateAsync(closed, CancellationToken.None));
        Assert.False(state.BiometricOffered);
        Assert.Equal(IdentityVerificationSteps.Operator, state.Step);

        var handedOver = await rig.Flow(production).RecordConsentAsync(closed, true, "consent-v2", CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.Operator, handedOver.Status);
        Assert.Null(closed.BiometricConsentAt);
        Assert.Null(closed.BiometricConsentTextHash);
        rig.Alerts.Verify(a => a.RaiseAsync(closed.Id, VerificationAlertReasons.LegalTextNotApproved, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("legal_text_not_approved", VerificationAlertReasons.LegalTextNotApproved);
        Assert.Contains(VerificationAlertReasons.LegalTextNotApproved, VerificationAlertReasons.All);
    }

    /// <summary>
    /// Data: the SHA-256 of every file that ships is the one of the manifest (line endings as <c>\n</c>, so a checkout on Windows and one on Linux agree), the texts are all there, and the
    /// registry does not serve a file that was changed after its hash was recorded (a version a guest accepted is never edited in place).
    /// </summary>
    [Fact]
    public void LegalTexts_HashMatchesFile_Test()
    {
        var folder = Path.Combine(LegalTextFixtures.ShippedRoot(), CheckInLegalTexts.FolderName);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, CheckInLegalTexts.ManifestFileName)));
        var texts = manifest.RootElement.GetProperty("texts").EnumerateArray().ToList();

        Assert.Equal(["biometric-consent", "ticino-notice"], texts.Select(t => t.GetProperty("kind").GetString()));
        foreach (var text in texts)
        {
            var kind = text.GetProperty("kind").GetString()!;
            var version = text.GetProperty("version").GetString()!;
            Assert.InRange(version.Length, 1, BiometricConsentRules.VersionMaxLength);
            // A draft has no approval, an approved text has both: who approved it and when.
            var approved = text.GetProperty("status").GetString() == "approved";
            Assert.True(approved || text.GetProperty("status").GetString() == "draft");
            Assert.Equal(approved ? JsonValueKind.String : JsonValueKind.Null, text.GetProperty("approvedBy").ValueKind);
            Assert.Equal(approved ? JsonValueKind.String : JsonValueKind.Null, text.GetProperty("approvedAt").ValueKind);

            var languages = text.GetProperty("languages").EnumerateObject().ToList();
            string[] expectedLanguages = kind == "biometric-consent" ? ["it", "en", "de", "fr", "es"] : ["it", "en"];
            Assert.Equal(expectedLanguages, languages.Select(l => l.Name));
            foreach (var language in languages)
            {
                var path = Path.Combine(folder, language.Value.GetProperty("file").GetString()!);
                var hash = CheckInLegalTexts.Sha256OfText(File.ReadAllBytes(path), out var content);
                Assert.Equal(language.Value.GetProperty("sha256").GetString(), hash);
                Assert.Equal(hash, hash.ToLowerInvariant());
                Assert.DoesNotContain('\r', content);

                // The Ticino notice says that it is a draft, in every language, until the pull request that approves it removes the line.
                if (kind == "ticino-notice")
                    Assert.Equal(!approved, content.Contains("BOZZA - da approvare dal legale", StringComparison.Ordinal));
            }
        }

        // The same text with Windows line endings has the same hash.
        var sample = Encoding.UTF8.GetBytes("Riga uno\nRiga due\n");
        Assert.Equal(
            CheckInLegalTexts.Sha256OfText(sample, out _),
            CheckInLegalTexts.Sha256OfText(Encoding.UTF8.GetBytes("Riga uno\r\nRiga due\r\n"), out _));

        // A file changed after its hash was recorded is not served: the version has no text left, so nothing of it is served.
        WriteRegistry(("biometric-consent", "consent-v1", "draft", Texts("Testo registrato")));
        File.WriteAllText(Path.Combine(_root, "checkin", "biometric-consent.consent-v1.it.md"), "Testo cambiato dopo la registrazione\n");
        var tampered = Registry("Staging");
        Assert.Null(tampered.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent));
        Assert.Empty(tampered.GetForStay(documentNotArchived: false, "it"));
    }

    /// <summary>
    /// With no approved consent in production the guest who asks for a person is on that path because of the registry, not by choice: the host reads <c>legal_text_not_approved</c>.
    /// With a consent served it is the request of the guest, as before.
    /// </summary>
    [Fact]
    public async Task LegalTexts_NoApprovedConsent_TheOperatorRequestCarriesTheReason()
    {
        WriteRegistry(("biometric-consent", "consent-v2", "draft", Texts("Consenso 2")));
        await using var rig = await FlowRig.CreateAsync();

        var withoutText = await rig.SessionAsync();
        var state = await rig.Flow(Registry("Production")).RequestOperatorAsync(withoutText, CancellationToken.None);
        Assert.True(state.OperatorRequested);
        rig.Alerts.Verify(a => a.RaiseAsync(withoutText.Id, VerificationAlertReasons.LegalTextNotApproved, It.IsAny<CancellationToken>()), Times.Once);

        var withText = await rig.SessionAsync();
        await rig.Flow(Registry("Staging")).RequestOperatorAsync(withText, CancellationToken.None);
        rig.Alerts.Verify(a => a.RaiseAsync(withText.Id, VerificationAlertReasons.GuestRequest, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>An approval is recorded in the manifest: an approved text without who approved it and when is not served, in production or anywhere.</summary>
    [Fact]
    public void LegalTexts_ApprovedWithoutItsApproval_IsNotServed()
    {
        WriteRegistry(
            ("biometric-consent", "consent-v1", "approved", Texts("Consenso"), WithApproval: false),
            ("ticino-notice", "ticino-v1", "approved", Texts("Avviso"), WithApproval: true));

        var production = Registry("Production");

        Assert.Null(production.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent));
        Assert.Equal("ticino-v1", production.GetActiveVersion(CheckInLegalTextKinds.TicinoNotice)!.Version);
    }

    [Fact]
    public void LegalTexts_TheConsentPicksTheParagraphOfThePropertyAndTheClosestLanguage()
    {
        var registry = new CheckInLegalTexts(
            Config(new Dictionary<string, string?> { ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot() }),
            Env("Staging"),
            NullLogger<CheckInLegalTexts>.Instance);

        var kept = Assert.Single(registry.GetForStay(documentNotArchived: false, "it"));
        var notKept = registry.GetForStay(documentNotArchived: true, "it").First(t => t.Kind == CheckInLegalTextKinds.BiometricConsent);

        // The photo of the document is kept encrypted until the check is over, except for a property that keeps no document.
        Assert.Contains("conservata cifrata", kept.ContentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("conservata cifrata", notKept.ContentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", kept.ContentHtml, StringComparison.Ordinal);
        Assert.StartsWith("<h3>", kept.ContentHtml, StringComparison.Ordinal);

        // A language the registry has is served as it is; one it lacks (the notice has Italian and English) falls back to English.
        Assert.Equal("de", registry.GetForStay(documentNotArchived: false, "de").Single().Language);
        var notice = registry.GetForStay(documentNotArchived: true, "de").First(t => t.Kind == CheckInLegalTextKinds.TicinoNotice);
        Assert.Equal("en", notice.Language);
        Assert.Contains("<strong>BOZZA - da approvare dal legale</strong>", notice.ContentHtml, StringComparison.Ordinal);
        Assert.Equal("it", registry.GetForStay(documentNotArchived: false, null).Single().Language);
    }

    [Fact]
    public void LegalTexts_ATextNeverBringsATagOfItsOwn()
    {
        WriteRegistry(("biometric-consent", "consent-v1", "draft", Texts("# Titolo\n\nUn <script>alert(1)</script> **forte** paragrafo.\n\n- uno\n- due\n")));

        var html = Registry("Staging").GetForStay(documentNotArchived: false, "it").Single().ContentHtml;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h3>Titolo</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>forte</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<ul><li>uno</li><li>due</li></ul>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void LegalTexts_NoManifest_ServesNothing()
    {
        Directory.CreateDirectory(_root);

        var registry = Registry("Staging");

        Assert.Null(registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent));
        Assert.Empty(registry.GetForStay(documentNotArchived: true, "it"));
        Assert.Null(registry.GetActiveVersion("not-a-kind"));
    }

    private static Dictionary<string, string> Texts(string italian) => new() { ["it"] = italian + "\n", ["en"] = italian + " (en)\n" };

    private CheckInLegalTexts Registry(string environment) =>
        new(Config(new Dictionary<string, string?> { ["Legal:ContentPath"] = _root }), Env(environment), NullLogger<CheckInLegalTexts>.Instance);

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Env(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(name);
        return environment.Object;
    }

    /// <summary>A registry on disk: one file per language of each text and the manifest with their hashes; the order is the order of the manifest.</summary>
    private void WriteRegistry(params (string Kind, string Version, string Status, Dictionary<string, string> Markdown, bool WithApproval)[] versions)
    {
        var folder = Path.Combine(_root, "checkin");
        Directory.CreateDirectory(folder);
        var texts = new List<object>();
        foreach (var (kind, version, status, markdown, withApproval) in versions)
        {
            var languages = new Dictionary<string, object>();
            foreach (var (language, content) in markdown)
            {
                var name = $"{kind}.{version}.{language}.md";
                File.WriteAllText(Path.Combine(folder, name), content);
                languages[language] = new { file = name, sha256 = CheckInLegalTexts.Sha256OfText(Encoding.UTF8.GetBytes(content), out _) };
            }

            texts.Add(new
            {
                kind,
                version,
                status,
                approvedBy = status == "approved" && withApproval ? "legale@example.test" : null,
                approvedAt = status == "approved" && withApproval ? "2026-12-01" : null,
                languages,
            });
        }

        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new { schemaVersion = 1, texts }));
    }

    private void WriteRegistry(params (string Kind, string Version, string Status, Dictionary<string, string> Markdown)[] versions) =>
        WriteRegistry([.. versions.Select(v => (v.Kind, v.Version, v.Status, v.Markdown, WithApproval: true))]);

    /// <summary>An org on the Agency plan (the biometric verification on by default), a stay of an adult guest and the flow, with fakes for everything that is not the consent.</summary>
    private sealed class FlowRig : IAsyncDisposable
    {
        private readonly Guid _orgId = Guid.NewGuid();
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"checkin-legal-{Guid.NewGuid()}").Options;

        private FlowRig()
        {
            Db = new AppDbContext(_options, null, new EphemeralDataProtectionProvider());
            Alerts.Setup(a => a.RaiseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        public AppDbContext Db { get; }

        public Mock<IVerificationOperatorAlerts> Alerts { get; } = new();

        public static async Task<FlowRig> CreateAsync()
        {
            var rig = new FlowRig();
            rig.Db.Orgs.Add(new OrgEntity
            {
                Id = rig._orgId,
                Name = "Agenzia consenso",
                Slug = $"consenso-{rig._orgId:N}"[..20],
                DisplayName = "Agenzia consenso",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            rig.Db.Properties.Add(new Property
            {
                Id = rig._propertyId,
                OrgId = rig._orgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            await rig.Db.SaveChangesAsync();
            return rig;
        }

        /// <summary>A new stay of an adult booker with an open check-in session, loaded as the public endpoint loads it.</summary>
        public async Task<GuestCheckInSession> SessionAsync()
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                FirstName = "Anna",
                LastName = "Bianchi",
                Email = "anna@example.com",
                DateOfBirth = new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 20),
                CheckOutDate = new DateTime(2026, 11, 22),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = _orgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(7),
                Status = GuestCheckInSessionStatus.InCompilazione,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync();

            return await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .FirstAsync(s => s.Id == session.Id);
        }

        public IdentityVerificationFlowService Flow(ICheckInLegalTexts legalTexts) => new(
            Db,
            Mock.Of<IIdentityVerificationAgent>(),
            Alerts.Object,
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            new FakeTimeProvider(Now),
            NullLogger<IdentityVerificationFlowService>.Instance,
            legalTexts: legalTexts);

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
