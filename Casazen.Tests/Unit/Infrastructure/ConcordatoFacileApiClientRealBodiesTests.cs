using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-STATUS-REAL: the first answers of the REAL status API of ConcordatoFacile, read on 2026-10-29 from its test environment with the key
/// that CF delivered that day and kept, re-indented, under <c>Fixtures/ConcordatoFacile/StatusApiTest</c>: the eleven contracts and two
/// practices of CF's e-mail, which carry every status and every close reason of §5 of the specification (data invented by CF, no personal
/// data). Until then the client had been proven only with the examples of the specification (<see cref="ConcordatoFacileApiClientTests"/>),
/// which is how the bare array and the non-nullable boolean of CF-FIX2 went unseen. Each body is read field for field against its own JSON.
/// To renew them (CF says an example may change for a few minutes and may add states) read the answers again with a test key and replace
/// the files.
/// </summary>
public class ConcordatoFacileApiClientRealBodiesTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConcordatoFacile", "StatusApiTest");

    public static IEnumerable<object[]> Leases() => FileNames("lease-*.json").Select(name => new object[] { name });

    public static IEnumerable<object[]> Practices() => FileNames("practice-*.json").Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Leases))]
    public async Task GetPracticesByLeaseId_RealBody_IsReadFieldForField(string file)
    {
        var body = Read(file);
        var expected = JsonNode.Parse(body)!;
        var leaseId = Guid.Parse((string)expected["leaseId"]!);

        var practices = await Client(new StubHandler(body)).GetPracticesByLeaseIdAsync(leaseId);

        var expectedPractices = expected["practices"]!.AsArray();
        Assert.NotEmpty(expectedPractices);
        Assert.Equal(expectedPractices.Count, practices.Count);
        for (var i = 0; i < practices.Count; i++)
        {
            AssertReadFieldForField(expectedPractices[i]!, practices[i]);
        }
    }

    /// <summary>The practice that CF received without a link to a contract (<c>leaseId</c> null), and one that is linked.</summary>
    [Theory]
    [MemberData(nameof(Practices))]
    public async Task GetPractice_RealBody_IsReadFieldForField(string file)
    {
        var body = Read(file);
        var expected = JsonNode.Parse(body)!;

        var practice = await Client(new StubHandler(body)).GetPracticeAsync((string)expected["practiceId"]!);

        Assert.NotNull(practice);
        AssertReadFieldForField(expected, practice);
        var leaseId = (string?)expected["leaseId"];
        Assert.Equal<Guid?>(leaseId is null ? null : Guid.Parse(leaseId), practice.LeaseId);
    }

    /// <summary>
    /// The saved answers (the practices of the contracts and the single ones) keep covering every status and every close reason of §5 of
    /// the specification: <c>Received</c> is only in the practice that CF received without a contract.
    /// </summary>
    [Fact]
    public void RealBodies_CoverEveryStatusAndEveryCloseReasonOfTheSpecification()
    {
        var ofContracts = FileNames("lease-*.json").SelectMany(file => JsonNode.Parse(Read(file))!["practices"]!.AsArray());
        var single = FileNames("practice-*.json").Select(file => JsonNode.Parse(Read(file)));
        var practices = ofContracts.Concat(single).ToList();

        var statuses = practices.Select(practice => (string)practice!["status"]!).Distinct().Order().ToList();
        var closeReasons = practices.Select(practice => (string?)practice!["closeReason"]).OfType<string>().Distinct().Order().ToList();

        Assert.Equal(Enum.GetNames<CfPracticeStatus>().Where(name => name != nameof(CfPracticeStatus.Unknown)).Order(), statuses);
        Assert.Equal(["duplicate", "not_compliant", "other", "withdrawn"], closeReasons);
    }

    private static void AssertReadFieldForField(JsonNode expected, PracticeDto actual)
    {
        Assert.Equal((string)expected["practiceId"]!, actual.PracticeId);
        Assert.Equal((string)expected["status"]!, actual.Status.ToString()); // a status that the client does not know would be Unknown
        Assert.Equal(Utc((string)expected["statusChangedAt"]!), actual.StatusChangedAt);
        Assert.Equal(DateTimeKind.Utc, actual.StatusChangedAt.Kind);
        var submittedAt = (string?)expected["submittedAt"];
        Assert.Equal<DateTime?>(submittedAt is null ? null : Utc(submittedAt), actual.SubmittedAt);
        Assert.Equal((string?)expected["comune"], actual.Comune);
        Assert.Equal((bool?)expected["comuneDiffersFromLink"], actual.ComuneDiffersFromLink);
        Assert.Equal((string?)expected["closeReason"], actual.CloseReason);
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static IEnumerable<string> FileNames(string pattern) =>
        Directory.EnumerateFiles(Folder, pattern).Select(path => Path.GetFileName(path)!).Order();

    private static string Read(string file) => File.ReadAllText(Path.Combine(Folder, file));

    private static ConcordatoFacileApiClient Client(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://stato.concordatofacile.it") },
            NullLogger<ConcordatoFacileApiClient>.Instance);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
