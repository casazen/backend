using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Unit.Services;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F02/US02/T01 (NF-197, NF-190, ADR-013-R21): <c>read-document</c> reads the identity document in the process only. The readings are the ones the
/// check-in used when no AI provider answered (their tests moved here from the removed AI reader): the MRZ first, then the visual zone, with the same limits
/// and reasons; the picture handed to the readers is zeroed once the reading is over, a failure included, and no AI provider is ever asked.
/// </summary>
public class LocalCheckInDocumentReaderTests
{
    private static readonly MrzExtractionResult PassportMrz = new()
    {
        CheckDigitsValid = true,
        Surname = "ROSSI",
        GivenNames = "ALBA",
        DocumentType = "Passport",
        IssuingState = "ITA",
        Nationality = "ITA",
        DocumentNumber = "YA1234567",
        Sex = "F",
        DateOfBirth = new DateTime(1994, 2, 12),
        ExpirationDate = new DateTime(2032, 3, 11),
    };

    [Theory]
    [InlineData(null, "not_document")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("unreadable", "unreadable")]
    public async Task ReadAsync_ReaderFailureAndUnrecognizedPicture_HaveDifferentReasons(string? failure, string expected)
    {
        var result = await ReadAsync(new DocumentOcrObservation(null, failure));

        Assert.False(result.Read);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Fields);
    }

    [Fact]
    public async Task ReadAsync_NamesPrintedOnAnUnrelatedImage_CannotPrefillTheForm()
    {
        var result = await ReadAsync(new DocumentOcrObservation(new DocumentOcrResult { GivenNames = "ALBA", Surname = "ROSSI" }));

        Assert.False(result.Read);
        Assert.Equal("not_document", result.Reason);
        Assert.Null(result.Fields);
    }

    [Fact]
    public async Task ReadAsync_VerifiedCardFields_ReachThePublicResponse()
    {
        var card = new DocumentOcrResult
        {
            GivenNames = "ALBA",
            Surname = "ROSSI",
            Sex = "F",
            DocumentKind = "IdentityCard",
            HasDocumentEvidence = true,
            DocumentNumber = "CA12345ZZ",
            DateOfBirth = new DateTime(1994, 2, 12),
            BirthPlace = "ROMA",
            BirthProvince = "RM",
            PlaceOfIssue = "MILANO",
            DateOfIssue = new DateTime(2022, 3, 11),
            ExpirationDate = new DateTime(2032, 3, 11),
            Nationality = "ITA",
        };

        var result = await ReadAsync(new DocumentOcrObservation(card));

        Assert.True(result.Read);
        Assert.Equal("front", result.Side);
        Assert.Equal(new CheckInDocumentFields("ALBA", "ROSSI", "Female", "1994-02-12", true, "ROMA", "RM",
            "ITA", "IdentityCard", "CA12345ZZ", "MILANO", "2022-03-11", "2032-03-11"), result.Fields);
        // The answer of read-document keeps its shape: the controller and its DTO did not change.
        var response = CheckInDocumentReadResponse.From(result);
        Assert.True(response.Read);
        Assert.Null(response.Reason);
        Assert.Equal("front", response.Side);
        Assert.Equal("CA12345ZZ", response.Guest!.DocumentNumber);
    }

    [Fact]
    public async Task ReadAsync_MachineReadableBack_SuppliesIdentityAndPrintedResidenceOnSameImage()
    {
        var mrz = new MrzExtractionResult
        {
            CheckDigitsValid = true,
            Surname = "ROSSI",
            GivenNames = "ALBA",
            DocumentType = "IdCard",
            IssuingState = "ITA",
            DocumentNumber = "CA12345ZZ",
        };
        var printed = new DocumentOcrResult
        {
            ResidenceStreet = "VIA ROMA 8",
            ResidencePostalCode = "00100",
            ResidenceCityName = "ROMA",
            ResidenceProvince = "RM",
        };

        var result = await ReadAsync(new DocumentOcrObservation(printed), mrz);

        Assert.True(result.Read);
        Assert.Equal("back", result.Side);
        Assert.Equal("CA12345ZZ", result.Fields!.DocumentNumber);
        Assert.Equal("VIA ROMA 8", result.Fields.ResidenceStreet);
        Assert.Equal("00100", result.Fields.ResidencePostalCode);
        Assert.Equal("ROMA", result.Fields.ResidenceCityName);
        Assert.Equal("RM", result.Fields.ResidenceProvince);
        Assert.True(result.Fields.ResidenceInItaly);
        Assert.Null(result.Fields.BirthPlace);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheMrzFirstThenTheVisualZone_AndZeroesThePicture()
    {
        var calls = new List<string>();
        var machine = new RecordingMrzReader(calls, PassportMrz);
        var visual = new RecordingVisualZoneReader(
            calls, new DocumentOcrObservation(new DocumentOcrResult { PlaceOfIssue = "MILANO", DateOfIssue = new DateTime(2022, 3, 11) }));
        var picture = TestImages.Png(32, 24, seed: 2);

        var result = await new LocalCheckInDocumentReader(machine, visual).ReadAsync(new MemoryStream(picture.ToArray()));

        // The MRZ first, then the visual zone, each on the picture itself...
        Assert.Equal(["mrz", "visual"], calls);
        Assert.Equal(picture, machine.Seen);
        Assert.Equal(picture, visual.Seen);
        // ...whose printed fields complete the ones the MRZ proved.
        Assert.True(result.Read);
        Assert.Equal("single", result.Side);
        Assert.Equal(
            ("YA1234567", "1994-02-12", "2032-03-11", "MILANO", "2022-03-11"),
            (result.Fields!.DocumentNumber, result.Fields.DateOfBirth, result.Fields.DocumentExpiresOn, result.Fields.IssuePlace, result.Fields.DocumentIssuedOn));
        // Once the reader answered, nothing of the picture is left in the one copy it handed to both readers.
        Assert.Same(machine.Handed, visual.Handed);
        AssertZeroed(machine.Handed);
    }

    [Fact]
    public async Task ReadAsync_ReaderFails_ThePictureIsZeroedAllTheSame()
    {
        var machine = new RecordingMrzReader([], PassportMrz, fail: true);
        var reader = new LocalCheckInDocumentReader(machine, new RecordingVisualZoneReader([], new DocumentOcrObservation(null)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(new MemoryStream(TestImages.Png(32, 24, seed: 3))));

        Assert.NotNull(machine.Seen);
        AssertZeroed(machine.Handed);
    }

    [Fact]
    public async Task ReadAsync_PictureOverTheLimit_IsTooLarge_AndIsNotRead()
    {
        var calls = new List<string>();
        var reader = new LocalCheckInDocumentReader(new RecordingMrzReader(calls), new RecordingVisualZoneReader(calls, new DocumentOcrObservation(null)));
        // A real PNG header: only the size refuses it.
        var overLimit = new byte[CheckInDocumentLimits.MaxImageBytes + 1];
        TestImages.Png(8, 8, seed: 4).CopyTo(overLimit, 0);

        Assert.Equal(CheckInDocumentRead.Failed("too_large"), await reader.ReadAsync(new MemoryStream(overLimit)));
        Assert.Equal(CheckInDocumentRead.Failed("too_large"), await reader.ReadAsync(new UnseekableStream(overLimit)));
        Assert.Empty(calls);

        // At the limit the picture is read.
        await reader.ReadAsync(new MemoryStream(overLimit, 0, CheckInDocumentLimits.MaxImageBytes));
        Assert.Equal(["mrz", "visual"], calls);
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00 })]
    public async Task ReadAsync_NotAJpegPngOrWebp_IsUnsupported_AndIsNotRead(byte[] content)
    {
        var calls = new List<string>();
        var reader = new LocalCheckInDocumentReader(new RecordingMrzReader(calls), new RecordingVisualZoneReader(calls, new DocumentOcrObservation(null)));

        var result = await reader.ReadAsync(new MemoryStream(content));

        Assert.Equal(CheckInDocumentRead.Failed("unsupported_file"), result);
        Assert.Empty(calls);
    }

    /// <summary>
    /// Privacy (NF-197, ADR-013-R21, D5): the registrations of the application, in the order of Program.cs, with an AI provider in the container that throws as
    /// soon as it is asked and counts when it is built, and an HTTP client factory that throws: <c>read-document</c> reads the document, and no AI provider and
    /// no HTTP client is ever used. The picture does not leave the process.
    /// </summary>
    [Fact]
    public async Task LocalCheckInDocumentReader_NeverCallsAnAiProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var ai = new ThrowingAiProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        // The services of the application first (here the two readers of the document and the self-hosted identity verification), then the AI provider.
        services.AddScoped<IMrzExtractionService>(_ => new RecordingMrzReader([], PassportMrz));
        services.AddScoped<IDocumentOcrService>(_ => new RecordingVisualZoneReader([], new DocumentOcrObservation(null)));
        services.AddSelfHostedIdentityVerification();
        services.AddCasazenAiProvider(configuration);
        services.AddScoped<IAiProvider>(_ => ai.Built());
        services.AddSingleton<IHttpClientFactory>(_ => throw new InvalidOperationException("The identity document must never reach an HTTP client."));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var reader = scope.ServiceProvider.GetRequiredService<ICheckInDocumentReader>();
        var result = await reader.ReadAsync(new MemoryStream(TestImages.Png(32, 24, seed: 5)));

        Assert.IsType<LocalCheckInDocumentReader>(reader);
        Assert.True(result.Read);
        Assert.Equal(("ALBA", "ROSSI", "YA1234567"), (result.Fields!.FirstName, result.Fields.LastName, result.Fields.DocumentNumber));
        Assert.Equal((0, 0), (ai.Builds, ai.Calls));
    }

    private static async Task<CheckInDocumentRead> ReadAsync(DocumentOcrObservation observation, MrzExtractionResult? machine = null)
    {
        var reader = new LocalCheckInDocumentReader(new RecordingMrzReader([], machine), new RecordingVisualZoneReader([], observation));
        using var photo = new MemoryStream(TestImages.Png(32, 24, seed: 2));
        return await reader.ReadAsync(photo);
    }

    /// <summary>What a reader was handed, copied, and the stream itself: <see cref="MemoryStream.ToArray"/> still reads its buffer after it is closed.</summary>
    private static async Task<(byte[] Seen, MemoryStream? Handed)> CaptureAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        return (copy.ToArray(), stream as MemoryStream);
    }

    private static void AssertZeroed(MemoryStream? handed)
    {
        Assert.NotNull(handed);
        var left = handed.ToArray();
        Assert.NotEmpty(left);
        Assert.True(left.All(value => value == 0), "The picture handed to the readers must be zeroed once the reading is over.");
    }

    /// <summary>An MRZ reader with a fixed answer that records the call and what it was handed.</summary>
    private sealed class RecordingMrzReader(List<string> calls, MrzExtractionResult? result = null, bool fail = false) : IMrzExtractionService
    {
        public byte[]? Seen { get; private set; }

        public MemoryStream? Handed { get; private set; }

        public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            calls.Add("mrz");
            (Seen, Handed) = await CaptureAsync(imageStream, cancellationToken);
            return fail ? throw new InvalidOperationException("The MRZ reader failed.") : result;
        }

        public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            new(await ExtractAsync(imageStream, cancellationToken), []);
    }

    /// <summary>A reader of the visual zone with a fixed answer that records the call and what it was handed.</summary>
    private sealed class RecordingVisualZoneReader(List<string> calls, DocumentOcrObservation observation) : IDocumentOcrService
    {
        public byte[]? Seen { get; private set; }

        public MemoryStream? Handed { get; private set; }

        public async Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            (await ObserveAsync(imageStream, cancellationToken)).Fields;

        public async Task<DocumentOcrObservation> ObserveAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            calls.Add("visual");
            (Seen, Handed) = await CaptureAsync(imageStream, cancellationToken);
            return observation;
        }
    }

    /// <summary>An AI provider that counts how often it is built and asked, and throws when asked: the reader must never get to it.</summary>
    private sealed class ThrowingAiProvider : IAiProvider
    {
        public int Builds { get; private set; }

        public int Calls { get; private set; }

        public IAiProvider Built()
        {
            Builds++;
            return this;
        }

        public Task<AiGenerationResult> GenerateAsync(string prompt, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The identity document must never reach an AI provider.");
        }

        public Task<AiGenerationResult> GenerateAsync(AiRequest request, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The identity document must never reach an AI provider.");
        }
    }

    /// <summary>A stream that cannot seek, read in pieces like a request body.</summary>
    private sealed class UnseekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
