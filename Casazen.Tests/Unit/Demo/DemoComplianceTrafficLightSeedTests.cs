using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

/// <summary>
/// What the showcase writes for the compliance traffic light (E03/F02/US01/T03, NF-52) is valid for the real rules: the stay the template entrusts to
/// the channel manager reads yellow and the one with the uploaded receipt reads green, read by the real status engine over the rows the real seeding
/// code wrote, at any time of the day the showcase is reset. The rows are marked as showcase data, the receipt is a file that exists in the private
/// bucket under the key the row holds, and nothing is written for a stay the template does not mention.
/// </summary>
public class DemoComplianceTrafficLightSeedTests
{
    [Theory]
    [InlineData("2026-11-06T00:30:00Z")]
    [InlineData("2026-11-06T10:00:00Z")]
    [InlineData("2026-11-06T22:30:00Z")]
    [InlineData("2026-11-06T23:30:00Z")]
    public async Task ComplianceTrafficLight_TheSeededStays_ReadYellowAndGreen_ByTheRealStatusEngine(string now)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        var world = await SeedAsync(clock);
        var (entrustedHouse, entrustedStay) = world.StayOf(b => b.Compliance is { GuestsEntrusted: true });
        var (archivedHouse, archivedStay) = world.StayOf(b => b.Compliance is { ReceiptArchived: true });

        var status = await world.StatusService().EvaluateAsync(new HostScope(world.Org.Id), CancellationToken.None);

        var yellow = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == entrustedStay.Id);
        Assert.Equal(entrustedHouse.Id, yellow.PropertyId);
        Assert.Equal(ComplianceCockpitAction.UploadAlloggiatiReceipt, yellow.Action);
        Assert.Equal(
            (ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager, ComplianceEvidenceClass.ExternalVerificationRequired),
            (yellow.Verdict.Light, yellow.Verdict.Reason, yellow.Verdict.EvidenceClass));
        Assert.Equal(OctorateHandoffTopic.Alloggiati, yellow.Verdict.HandoffTopic);

        var green = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == archivedStay.Id);
        Assert.Equal(archivedHouse.Id, green.PropertyId);
        Assert.Equal(
            (ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived, ComplianceEvidenceClass.OfficialProof, ComplianceEvidenceSources.ManualUpload),
            (green.Verdict.Light, green.Verdict.Reason, green.Verdict.EvidenceClass, green.Verdict.EvidenceSource));
        Assert.StartsWith("manual-proof:", green.Verdict.ProofReference, StringComparison.Ordinal);

        // The connection of the house the host declared active is not a second item beside the yellow stay, and the two stays are the only ones read.
        Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.Connection && i.PropertyId == entrustedHouse.Id);
        Assert.Equal(
            new[] { entrustedStay.Id, archivedStay.Id }.Order(),
            status.Items.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value).Distinct().Order());
    }

    [Fact]
    public async Task ComplianceTrafficLight_TheSeededRows_AreMarkedAsShowcaseData_AndTheReceiptIsAFileInThePrivateBucketUnderTheKeyOfItsRow()
    {
        var world = await SeedAsync(new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)));
        var (entrustedHouse, entrustedStay) = world.StayOf(b => b.Compliance is { GuestsEntrusted: true });
        var (archivedHouse, archivedStay) = world.StayOf(b => b.Compliance is { ReceiptArchived: true });

        // The sender: the channel manager for both communications (ISTAT follows Alloggiati), the host's word that it saw «Activated», by the owner.
        var sender = await world.Db.PropertyComplianceSenders.AsNoTracking().SingleAsync();
        Assert.Equal(
            (entrustedHouse.Id, ComplianceSenderType.ChannelManager, ComplianceSenderType.ChannelManager, world.Owner.Id),
            (sender.PropertyId, sender.AlloggiatiSender, sender.IstatSender, sender.LastChangedByUserId));
        Assert.NotNull(sender.ActivationDeclaredAt);
        Assert.True(sender.ActivationDeclaredAt >= sender.LastChangedAt, "A declaration older than the choice of the sender would be void.");

        // The guests of the entrusted stay are on Octorate for good, one row for each, with a hash the column accepts and no personal data.
        var links = await world.Db.StayGuestOctorateLinks.AsNoTracking().OrderBy(l => l.Position).ToListAsync();
        Assert.Equal([0, 1], links.Select(l => l.Position));
        Assert.All(links, link =>
        {
            Assert.Equal(entrustedStay.Id, link.BookingId);
            Assert.Equal((OctorateSyncStatus.Sent, false, 1), (link.Status, link.SentAsDraft, link.Attempts));
            Assert.Matches("^[0-9a-f]{64}$", link.PayloadHash);
            Assert.InRange(link.OctorateGuestId!.Length, 1, StayGuestOctorateLink.OctorateGuestIdMaxLength);
            Assert.Contains("TEST", link.OctorateGuestId, StringComparison.Ordinal);
            Assert.NotNull(link.SentAt);
        });

        // The arrival of the entrusted stay is three hours before the reset, so the legal term runs from then.
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc), entrustedStay.ArrivedAt);
        Assert.Null(archivedStay.ArrivedAt);

        // The receipt: one row for the arrival day of the finished stay, of its house, pointing at a real one-page PDF that says it is test data.
        var proof = await world.Db.ManualComplianceProofs.AsNoTracking().SingleAsync();
        Assert.Equal(
            (archivedHouse.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(archivedStay.CheckInDate), "application/pdf", world.Owner.Id),
            (proof.PropertyId, proof.Kind, proof.Day, proof.ContentType, proof.UploadedByUserId));
        Assert.True(StorageKeys.IsValid(proof.FileKey), proof.FileKey);
        Assert.StartsWith($"compliance-proofs/{world.Org.Id}/{proof.Id}/", proof.FileKey, StringComparison.Ordinal);
        var file = Assert.Single(world.StoredFiles);
        Assert.Equal(proof.FileKey, file.Key);
        var text = System.Text.Encoding.ASCII.GetString(file.Value);
        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains(DemoDocumentPdf.Marker, text, StringComparison.Ordinal);

        // Every row the seeding wrote is marked as showcase data, so the reset finds it.
        var markers = await world.Db.TestDataMarkers.AsNoTracking().Where(m => m.Origin == TestDataOrigins.Showcase).ToListAsync();
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.PropertyComplianceSender && m.EntityId == sender.Id);
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.ManualComplianceProof && m.EntityId == proof.Id);
        Assert.Equal(
            links.Select(l => l.Id).Order(),
            markers.Where(m => m.EntityType == TestDataEntities.StayGuestOctorateLink).Select(m => m.EntityId).Order());
        Assert.Equal(2, await world.Db.StayGuests.CountAsync(g => g.BookingId == entrustedStay.Id));
        Assert.Equal(1, await world.Db.StayGuests.CountAsync(g => g.BookingId == archivedStay.Id));
    }

    [Fact]
    public async Task ComplianceTrafficLight_AStayTheTemplateDoesNotMention_GetsNoLinkNoReceiptAndNoArrival()
    {
        var world = await SeedAsync(new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)), plainStayEmail: "ospite.demo.semplice@example.com");

        // A stay of the same house written by the same code, from a template with no compliance block: it has its guest and nothing else.
        var guest = await world.Db.Guests.AsNoTracking().SingleAsync(g => g.Email == "ospite.demo.semplice@example.com");
        var plain = await world.Db.Bookings.AsNoTracking().SingleAsync(b => b.GuestId == guest.Id);
        Assert.Empty(await world.Db.StayGuestOctorateLinks.AsNoTracking().Where(l => l.BookingId == plain.Id).ToListAsync());
        Assert.Null(plain.ArrivedAt);
        Assert.Equal(1, await world.Db.StayGuests.CountAsync(g => g.BookingId == plain.Id));

        // And the rows of the compliance block are still only the two the template asks for.
        Assert.Single(await world.Db.PropertyComplianceSenders.AsNoTracking().ToListAsync());
        Assert.Single(await world.Db.ManualComplianceProofs.AsNoTracking().ToListAsync());
        Assert.Equal(2, await world.Db.StayGuestOctorateLinks.CountAsync());
    }

    /// <summary>
    /// An org with its owner and the houses of the catalog that have a stay with a compliance block, seeded by the real code of the showcase (the
    /// sender of the house, then each stay), over an InMemory context and a file store that keeps what is put in it.
    /// </summary>
    private static async Task<SeedWorld> SeedAsync(FakeTimeProvider clock, string? plainStayEmail = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Orgs.Add(org);
        db.Users.Add(owner);

        var templates = DemoShowcaseCatalog.Properties.Where(p => p.Bookings.Any(b => b.Compliance is not null)).ToList();
        var houses = templates.ToDictionary(t => t.Key, t => HostScopeScenario.NewProperty(org.Id, owner.Id, t.Name));
        db.Properties.AddRange(houses.Values);
        await db.SaveChangesAsync();

        var files = new Dictionary<string, byte[]>();
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((StorageBucket _, string key, Stream content, string _, CancellationToken _) =>
            {
                using var copy = new MemoryStream();
                content.CopyTo(copy);
                files[key] = copy.ToArray();
            })
            .Returns(Task.CompletedTask);
        var gate = new Mock<IDemoEnvironmentGate>();
        gate.SetupGet(g => g.IsAvailable).Returns(true);
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var seeding = new DemoEnvironmentService(
            db,
            storage.Object,
            Mock.Of<IPropertyComplianceStatusService>(),
            Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()),
            gate.Object,
            NullLogger<DemoEnvironmentService>.Instance,
            clock,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ICrmContactIndex>(),
            Mock.Of<IFeatureFlags>(),
            hostEnvironment.Object);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        foreach (var template in templates)
        {
            var house = houses[template.Key];
            if (template.ComplianceSender is { } sender)
                await seeding.CreateComplianceSenderAsync(org.Id, owner.Id, house, sender, template.Key, CancellationToken.None);

            foreach (var booking in template.Bookings.Where(b => b.Compliance is not null))
                await seeding.CreateBookingAsync(org.Id, house, booking, today, CancellationToken.None);
        }

        if (plainStayEmail is not null)
        {
            var plain = new DemoBookingTemplate(
                "semplice", "Mario", "Bianchi", plainStayEmail, 3, 2, BookingStatus.Confirmed, DemoShowcaseCatalog.Scenario.BookingUpcoming,
                BookingSource.Direct, Guests: [new("Mario", "Bianchi", StayGuestType.SingleGuest, 40)]);
            await seeding.CreateBookingAsync(org.Id, houses.Values.First(), plain, today, CancellationToken.None);
        }

        return new SeedWorld(db, org, owner, houses, files, clock);
    }

    private sealed class SeedWorld(
        AppDbContext db,
        OrgEntity org,
        User owner,
        IReadOnlyDictionary<string, Property> houses,
        IReadOnlyDictionary<string, byte[]> files,
        FakeTimeProvider clock)
    {
        public AppDbContext Db { get; } = db;

        public OrgEntity Org { get; } = org;

        public User Owner { get; } = owner;

        public IReadOnlyDictionary<string, byte[]> StoredFiles { get; } = files;

        /// <summary>The house and the only stay of it that the compliance block <paramref name="which"/> picks in the catalog.</summary>
        public (Property House, Booking Stay) StayOf(Func<DemoBookingTemplate, bool> which)
        {
            var template = DemoShowcaseCatalog.Properties.Single(p => p.Bookings.Any(which));
            var house = houses[template.Key];
            return (house, Db.Bookings.AsNoTracking().Single(b => b.PropertyId == house.Id));
        }

        /// <summary>The real status engine over the same context, with the real sender store and the connector on.</summary>
        public ComplianceStatusService StatusService()
        {
            var environment = new Mock<IHostEnvironment>();
            environment.SetupGet(h => h.EnvironmentName).Returns("Staging");
            var senders = new PropertySenderStore(Db, new ActivityLog(Db, clock), environment.Object, clock);

            var health = new Mock<IComplianceConnectionHealthService>();
            health
                .Setup(h => h.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    (IReadOnlyDictionary<Guid, ComplianceConnectionHealth>)ids.ToDictionary(id => id, id => UnknownHealth(id)));
            var quotes = new Mock<ITouristTaxQuoteService>();
            quotes
                .Setup(q => q.GetRatesInForceAsync(It.IsAny<TouristTaxComune>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<TouristTaxRate>)new List<TouristTaxRate>());
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);

            return new ComplianceStatusService(
                Db,
                senders,
                health.Object,
                quotes.Object,
                flags.Object,
                Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()),
                NullLogger<ComplianceStatusService>.Instance,
                clock);
        }

        /// <summary>
        /// What the health service answers for a house of the channel manager when Octorate exposes nothing about the police account: unknown, and the
        /// host's word (<c>declaredAt</c>) when the sender row holds one, as the real service reads it from the same row.
        /// </summary>
        private ComplianceConnectionHealth UnknownHealth(Guid propertyId)
        {
            var declaredAt = Db.PropertyComplianceSenders.AsNoTracking().Where(s => s.PropertyId == propertyId).Select(s => s.ActivationDeclaredAt).FirstOrDefault();
            var line = new ConnectionHealthLine(ComplianceSenderType.ChannelManager, ConnectionHealthState.Unknown, null, null, null, null, declaredAt);
            return new ComplianceConnectionHealth(propertyId, true, line, line, false, false, clock.GetUtcNow().UtcDateTime);
        }
    }
}
