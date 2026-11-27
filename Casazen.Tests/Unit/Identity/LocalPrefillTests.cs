using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Identity.DocumentStepSelfHostedTests;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US03/T01 (NF-190): the zoned prefill of <c>read-document</c> with the self-hosted flow on. The photo of a side of the Italian card is read as it always was and, on top of it, by
/// the zones of the card cut out of it: each value says its side, its zone, its confidence and its source, and what the guest still has to type is listed. The card is found and set
/// upright first, so a card that cannot be found leaves the reading of today alone; a reader that fails or a service that is not there never stops the prefill; no value reaches a log;
/// only the guest who sent the photo gets the values; with the flow off the answer is the one of before, without the three members.
/// </summary>
public sealed class LocalPrefillTests
{
    private const string Token = "token";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- The reader: the zones of the card, side, zone, confidence, what is missing -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadZoned_TheBackOfTheCard_ReturnsTheFieldsWithTheirSideZoneAndConfidence_AndWhatIsStillMissing()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Back] =
        [
            new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
            new ZonedField(ZonedFieldNames.GivenNames, GivenNames, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
            new ZonedField(ZonedFieldNames.FiscalCode, "ZZZZZZ00Z00Z000Z", DocumentSide.Back, ZonedFieldNames.FiscalCode, 0.55, ZonedFieldSources.Viz),
        ];

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(back), Ct);

        // The reading of today is the one it always was...
        Assert.True(result.Read.Read);
        Assert.Equal("back", result.Read.Side);
        Assert.Equal(DocumentNumber, result.Read.Fields!.DocumentNumber);
        // ...and the zones come with the place each value was read from.
        Assert.Equal(
            [
                (ZonedFieldNames.Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
                (ZonedFieldNames.GivenNames, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
                (ZonedFieldNames.FiscalCode, DocumentSide.Back, ZonedFieldNames.FiscalCode, 0.55, ZonedFieldSources.Viz),
            ],
            result.Fields.Select(field => (field.Name, field.Side, field.ZoneId, field.Confidence, field.Source)));
        // The place and the date of birth are the card's front; the place of issue is on no zone: the guest types what neither the MRZ nor a zone gave.
        Assert.Equal([CheckInPrefillFields.BirthPlace, CheckInPrefillFields.IssuePlace], result.Missing);
        var card = Assert.Single(world.Zoned.Cards);
        Assert.Equal((DocumentSide.Back, "it-cie-3.0"), (card.Side, card.FormatId));
        Assert.InRange(card.Width, 470, 495);
        Assert.InRange(card.Height, 295, 315);
    }

    [Fact]
    public async Task ReadZoned_TheFrontOfTheCard_IsReadAsTheSideWithThePortrait()
    {
        var world = new Zones();
        var front = Picture(DocumentSide.Front);
        world.Ocr.Fields = new DocumentOcrResult
        {
            HasDocumentEvidence = true,
            DocumentKind = "IdentityCard",
            GivenNames = GivenNames,
            Surname = Surname,
            DocumentNumber = DocumentNumber,
            BirthPlace = "ROMA",
            BirthProvince = "RM",
            DateOfBirth = DateTime.Parse(BirthDay, System.Globalization.CultureInfo.InvariantCulture),
            Sex = "F",
            Nationality = "ITA",
        };
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(front), Ct);

        Assert.True(result.Read.Read);
        Assert.Equal("front", result.Read.Side);
        Assert.Equal(7, result.Fields.Count);
        Assert.All(result.Fields, field => Assert.Equal((DocumentSide.Front, ZonedFieldSources.Viz), (field.Side, field.Source)));
        Assert.Equal([CheckInPrefillFields.IssuePlace], result.Missing);
        Assert.Equal(DocumentSide.Front, Assert.Single(world.Zoned.Cards).Side);
    }

    [Fact]
    public async Task ReadZoned_ACardThatCannotBeFoundWhole_KeepsTheReadingOfToday_AndReadsNoZone()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back, new DocumentImageFactory.Placement(-100, 70, 480, 303));
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Back] = [new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz)];

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(back), Ct);

        Assert.True(result.Read.Read);
        Assert.Empty(result.Fields);
        Assert.Empty(world.Zoned.Cards);
        Assert.Equal(CheckInPrefillFields.MissingFrom(result.Read.Fields, []), result.Missing);
        Assert.Contains(CheckInPrefillFields.BirthPlace, result.Missing);
    }

    [Fact]
    public async Task ReadZoned_NothingRead_ReadsNoZone_AndEverythingIsMissing()
    {
        var world = new Zones();

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(Picture(DocumentSide.Front)), Ct);

        Assert.False(result.Read.Read);
        Assert.Equal("not_document", result.Read.Reason);
        Assert.Empty(result.Fields);
        Assert.Equal(CheckInPrefillFields.Asked, result.Missing);
        Assert.Equal(0, world.Detector.Calls);
        Assert.Empty(world.Zoned.Cards);
    }

    [Theory]
    [InlineData("too-large")]
    [InlineData("not-a-picture")]
    public async Task ReadZoned_APictureThatIsNotRead_AnswersAsTheReadingOfTodayDoes(string kind)
    {
        var world = new Zones();
        var content = kind == "too-large" ? new byte[CheckInDocumentLimits.MaxImageBytes + 1] : [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
        if (kind == "too-large")
            Picture(DocumentSide.Front).CopyTo(content, 0);

        var zoned = await world.Reader().ReadZonedAsync(new MemoryStream(content), Ct);
        var plain = await world.Reader().ReadAsync(new MemoryStream(content), Ct);

        Assert.Equal(plain, zoned.Read);
        Assert.Empty(zoned.Fields);
        Assert.Equal(CheckInPrefillFields.Asked, zoned.Missing);
    }

    [Fact]
    public async Task ReadZoned_AModelThatFails_KeepsTheReadingOfToday_AndLogsTheTypeOnly()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Detector.Fail = new BiometricUnavailableException();
        world.Zoned.Fields[DocumentSide.Back] = [new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz)];

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(back), Ct);

        Assert.True(result.Read.Read);
        Assert.Empty(result.Fields);
        Assert.Contains(world.Logs, line => line.Contains(nameof(BiometricUnavailableException), StringComparison.Ordinal));
        AssertNoValueIn(world.Logs);
    }

    [Fact]
    public async Task ReadZoned_TheServicesOfTheZonesNotThere_KeepTheReadingOfToday()
    {
        var back = Picture(DocumentSide.Back);
        var mrz = new ScriptedMrz();
        mrz.Add(back, CieMrz());
        var logs = new List<string>();
        var reader = new LocalCheckInDocumentReader(mrz, new Ocr(), new ServiceCollection().BuildServiceProvider(), new ListLogger(logs));

        var result = await reader.ReadZonedAsync(new MemoryStream(back), Ct);

        Assert.True(result.Read.Read);
        Assert.Empty(result.Fields);
        Assert.Equal(CheckInPrefillFields.MissingFrom(result.Read.Fields, []), result.Missing);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task ReadZoned_NoValueIsLogged_AndARecordPrintedByMistakeLeaksNone()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Back] = [new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz)];

        var result = await world.Reader().ReadZonedAsync(new MemoryStream(back), Ct);

        AssertNoValueIn(world.Logs);
        // The answer prints without a value: a record logged by mistake leaks none.
        Assert.DoesNotContain(Surname, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Surname, result.Fields[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Surname, CheckInZonedFieldDto.From(result.Fields[0]).ToString(), StringComparison.Ordinal);
    }

    // ---- The endpoint -----------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadDocument_SelfHostedOn_ReturnsTheFieldsTheMissingAndTheLocalSource_ForAGuestOfTheStay()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Back] =
        [
            new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
            new ZonedField(ZonedFieldNames.GivenNames, GivenNames, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
        ];
        var endpoint = new Endpoint(world.Reader(), selfHosted: true, Surname, GivenNames);

        var response = await endpoint.ReadAsync(back);

        Assert.True(response.Read);
        Assert.Equal(0, response.GuestPosition);
        Assert.Equal(CheckInDocumentReadResponse.LocalSource, response.Source);
        var field = Assert.IsType<CheckInZonedFieldDto>(response.Fields![0]);
        Assert.Equal((ZonedFieldNames.Surname, Surname, "back", ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz), (field.Name, field.Value, field.Side, field.Zone, field.Confidence, field.Source));
        Assert.Equal([CheckInPrefillFields.BirthPlace, CheckInPrefillFields.IssuePlace], response.Missing);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, Web));
        foreach (var member in new[] { "fields", "missing", "source", "guest", "read", "side" })
            Assert.True(json.RootElement.TryGetProperty(member, out _), $"{member} is missing from the answer");

        Assert.Equal("local", json.RootElement.GetProperty("source").GetString());
        Assert.Equal(
            ["confidence", "name", "side", "source", "value", "zone"],
            json.RootElement.GetProperty("fields")[0].EnumerateObject().Select(member => member.Name).Order());
        AssertNoValueIn(endpoint.Logs);
        AssertNoValueIn(world.Logs);
    }

    [Fact]
    public async Task ReadDocument_SelfHostedOff_IsTheAnswerOfBefore_WithoutTheThreeMembers()
    {
        var plain = new Mock<ICheckInDocumentReader>();
        plain.Setup(reader => reader.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CheckInDocumentRead(true, null, new CheckInDocumentFields(GivenNames, Surname, null, null, null, null, null, null, null, null, null), "back"));
        var endpoint = new Endpoint(plain.Object, selfHosted: false, Surname, GivenNames);

        var response = await endpoint.ReadAsync(Picture(DocumentSide.Back));

        Assert.True(response.Read);
        Assert.Null(response.Fields);
        Assert.Null(response.Missing);
        Assert.Null(response.Source);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, Web));
        Assert.Equal(
            ["confirmGuest", "guest", "guestPosition", "read", "reason", "side"],
            json.RootElement.EnumerateObject().Select(member => member.Name).Order());
        plain.Verify(reader => reader.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        plain.Verify(reader => reader.ReadZonedAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadDocument_SelfHostedOn_APhotoOfSomebodyElse_GivesNoValue()
    {
        var world = new Zones();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Back] = [new ZonedField(ZonedFieldNames.Surname, Surname, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz)];
        // The guest of the stay is somebody else: the photo is not theirs, and nothing the zones read leaves.
        var endpoint = new Endpoint(world.Reader(), selfHosted: true, "ALTRO", "NOME");

        var response = await endpoint.ReadAsync(back);

        Assert.False(response.Read);
        Assert.Equal("guest_mismatch", response.Reason);
        Assert.Null(response.Guest);
        Assert.Empty(response.Fields!);
        Assert.Equal(CheckInPrefillFields.Asked, response.Missing);
        Assert.Equal(CheckInDocumentReadResponse.LocalSource, response.Source);
        var text = JsonSerializer.Serialize(response, Web);
        foreach (var value in new[] { Surname, GivenNames, DocumentNumber })
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadDocument_SelfHostedOn_NothingRead_AnswersNoFieldAndEverythingMissing()
    {
        var world = new Zones();
        var endpoint = new Endpoint(world.Reader(), selfHosted: true, Surname, GivenNames);

        var response = await endpoint.ReadAsync(Picture(DocumentSide.Front));

        Assert.False(response.Read);
        Assert.Equal("not_document", response.Reason);
        Assert.Empty(response.Fields!);
        Assert.Equal(CheckInPrefillFields.Asked, response.Missing);
        Assert.Equal("local", response.Source);
    }

    [Fact]
    public void CheckInPrefillFields_MissingFrom_IsWhatNeitherTheReadingNorAZoneGave()
    {
        var read = new CheckInDocumentFields(GivenNames, Surname, null, null, null, null, null, null, "IdentityCard", null, null);
        ZonedField[] zones =
        [
            new(ZonedFieldNames.Sex, "F", DocumentSide.Front, ZonedFieldNames.Sex, 0.55, ZonedFieldSources.Viz),
            new(ZonedFieldNames.BirthDate, "  ", DocumentSide.Front, ZonedFieldNames.BirthDate, 0.55, ZonedFieldSources.Viz),
            new(ZonedFieldNames.DocumentNumber, DocumentNumber, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz),
        ];

        // The last name, the first name and the kind come from the reading, the gender and the number from a zone; a blank value is nothing.
        Assert.Equal(
            [CheckInPrefillFields.DateOfBirth, CheckInPrefillFields.BirthPlace, CheckInPrefillFields.Citizenship, CheckInPrefillFields.IssuePlace],
            CheckInPrefillFields.MissingFrom(read, zones));
        Assert.Equal(CheckInPrefillFields.Asked, CheckInPrefillFields.MissingFrom(null, []));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------------------------------------------

    private static void AssertNoValueIn(IEnumerable<string> lines)
    {
        foreach (var value in new[] { Surname, GivenNames, DocumentNumber, BirthDay, ExpiryDay })
            Assert.All(lines, line => Assert.DoesNotContain(value, line, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reader of the check-in with the readers of the MRZ, the visual zone and the zones scripted, the real geometry, classifier and registry, and a container for them.</summary>
    private sealed class Zones
    {
        private readonly ServiceProvider _provider;

        public Zones()
        {
            Detector = new SwitchableDetector();
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<BiometricModelOptions>>(Options.Create(new BiometricModelOptions()));
            services.AddSingleton(new DocumentGeometryInspector(Detector, Options.Create(new BiometricModelOptions())));
            services.AddSingleton<IZonedFieldReader>(Zoned);
            services.AddScoped<DocumentPictureAnalyzer>();
            services.AddSingleton(new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory));
            services.AddSingleton<DocumentSideClassifier>();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public SwitchableDetector Detector { get; }

        public ScriptedMrz Mrz { get; } = new();

        public Ocr Ocr { get; } = new();

        public ScriptedZonedReader Zoned { get; } = new();

        public List<string> Logs { get; } = [];

        public LocalCheckInDocumentReader Reader()
        {
            return new LocalCheckInDocumentReader(Mrz, Ocr, _provider.CreateScope().ServiceProvider, new ListLogger(Logs));
        }
    }

    /// <summary>The visual zone: what it was told to say (nothing, by default).</summary>
    private sealed class Ocr : IDocumentOcrService
    {
        public DocumentOcrResult? Fields { get; set; }

        public Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) => Task.FromResult(Fields);
    }

    /// <summary>The endpoint <c>read-document</c> over a reader, for a stay whose booker is <c>surname</c> and <c>givenNames</c>.</summary>
    private sealed class Endpoint
    {
        private readonly PublicGuestCheckInController _controller;
        private readonly Mock<IStayGuestService> _roster = new();

        public Endpoint(ICheckInDocumentReader reader, bool selfHosted, string surname, string givenNames)
        {
            var booker = new Guest { Id = Guid.NewGuid(), FirstName = givenNames, LastName = surname };
            var booking = new Booking { Id = Guid.NewGuid(), GuestId = booker.Id, Guest = booker, NumberOfGuests = 1 };
            var session = new GuestCheckInSession { BookingId = booking.Id, Booking = booking };
            _roster.Setup(service => service.GetForBookingAsync(booking, It.IsAny<CancellationToken>()))
                .ReturnsAsync([StayGuest.FromBooker(booking, booker)]);
            var checkIn = new Mock<IGuestCheckInService>();
            checkIn.Setup(service => service.GetSessionByTokenAsync(Token)).ReturnsAsync(session);
            var flags = new TestFeatureFlags(selfHosted ? [FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted] : [FeatureFlags.CheckInIdentityVerification]);
            _controller = new PublicGuestCheckInController(
                checkIn.Object,
                Mock.Of<IAlloggiatiCodeTableService>(),
                reader,
                Mock.Of<IAlloggiatiReportScheduler>(),
                Mock.Of<IIdentityVerificationFlowService>(),
                flags,
                Mock.Of<IStringLocalizer<SharedResources>>(),
                new ListLogger<PublicGuestCheckInController>(Logs))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        public List<string> Logs { get; } = [];

        public async Task<CheckInDocumentReadResponse> ReadAsync(byte[] picture)
        {
            var file = new FormFile(new MemoryStream(picture), 0, picture.Length, "file", "document.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png",
            };
            var result = await _controller.ReadDocument(Token, file, _roster.Object);
            return Assert.IsType<CheckInDocumentReadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }
    }

    private sealed class ListLogger(List<string> lines) : ILogger<LocalCheckInDocumentReader>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
