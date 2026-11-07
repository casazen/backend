using System.Net;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F02/US01/T01, NF-56 to NF-58, ADR-023-R13: the username and password of a portal (a channel or a tool) go to Octorate in the body of one
/// create and nowhere else. Data rule: they never reach a log line or the message of an exception, whatever Octorate answers (a success that echoes
/// them, a refusal that repeats them, the failure of the channel, a failure of Octorate, an answer that cannot be used, a network error that names
/// them), and the objects that hold them do not print them. The HTTP answers are stubs: what is proven is what the client logs and raises, not Octorate.
/// </summary>
public class OctorateConnectionLoggingTests
{
    private const string Username = "host-user-7f3a91";
    private const string Password = "pw-Secret-4c8e2d6b";
    private const string Accommodation = "585492";
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

    [Fact]
    public async Task OctorateClient_CreateConnection_NeverLogsPortalCredentials_Test()
    {
        var h = new OctorateConnectionClientHarness();
        var request = new OctorateConnectionCreate("142", "H-4471", Username, Password);

        // The echo of the credentials in the answer is what Octorate's connection object does (it holds the username and the password).
        var echo = $$"""{"id": 7001, "channelId": 142, "accommodationId": {{Accommodation}}, "hotelId": "H-4471", "username": "{{Username}}", "password": "{{Password}}", "status": "WAITING"}""";

        using (h.Scope.Use(OrgA))
        {
            // 1. A success: the connection comes back with its id, portal and status, and only those are kept.
            h.Answer(HttpStatusCode.Created, echo);
            var created = await h.Client.CreateConnectionAsync(Accommodation, request, CancellationToken.None);
            Assert.Equal(("7001", "142", "WAITING"), (created.ConnectionId, created.PortalCode, created.Status));
            Assert.DoesNotContain(Password, created.ToString(), StringComparison.Ordinal);

            // The credentials did go in the body of the call: the proof below would otherwise prove nothing.
            var sent = Assert.Single(h.Requests);
            Assert.Contains(Password, sent.Body, StringComparison.Ordinal);
            Assert.Contains(Username, sent.Body, StringComparison.Ordinal);

            // 2. Every failure that repeats what was sent: the typed error holds neither in its message nor in its text.
            var failures = new (string Name, HttpStatusCode Status, string Body, Type Expected)[]
            {
                ("refused", HttpStatusCode.BadRequest, $$"""{"type":"ApiParamsExemption","message":"wrong login {{Username}} / {{Password}}"}""", typeof(OctorateRequestRejectedException)),
                ("forbidden", HttpStatusCode.Forbidden, $$"""{"type":"ApiSecurityException","message":"user {{Username}} password {{Password}}"}""", typeof(OctorateAuthException)),
                ("partner", (HttpStatusCode)550, $$"""{"message":"the portal refused {{Username}} {{Password}}"}""", typeof(OctoratePartnerFailureException)),
                ("conflict", HttpStatusCode.Conflict, echo, typeof(OctorateConflictException)),
                ("server", HttpStatusCode.InternalServerError, $"<html>{Username} {Password}</html>", typeof(OctorateServerException)),
                ("no id", HttpStatusCode.OK, $$"""{"channelId": 142, "username": "{{Username}}", "password": "{{Password}}"}""", typeof(OctorateServerException)),
                ("not an object", HttpStatusCode.OK, $"[\"{Password}\"]", typeof(OctorateServerException)),
            };
            foreach (var (name, status, body, expected) in failures)
            {
                h.Answer(status, body);
                var error = await Assert.ThrowsAnyAsync<OctorateException>(() => h.Client.CreateConnectionAsync(Accommodation, request, CancellationToken.None));
                Assert.True(expected == error.GetType(), $"{name}: the error is {error.GetType().Name}, expected {expected.Name}");
                Assert.DoesNotContain(Username, error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
            }

            // 3. A network error that names the password in its own text: the message of the typed error is the code and the status, no more.
            h.Fail(new HttpRequestException($"connection reset, password={Password} user={Username}"));
            var network = await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.CreateConnectionAsync(Accommodation, request, CancellationToken.None));
            Assert.DoesNotContain(Password, network.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Username, network.Message, StringComparison.Ordinal);

            // 4. A change that carries new credentials goes the same way.
            h.Answer(HttpStatusCode.OK, echo);
            await h.Client.UpdateConnectionAsync(Accommodation, "7001", new OctorateConnectionPatch { Username = Username, Password = Password }, CancellationToken.None);
        }

        // A portal code that is not a number is refused before anything leaves, and the refusal names no credential.
        var calls = h.Requests.Count;
        using (h.Scope.Use(OrgA))
        {
            var bad = await Assert.ThrowsAsync<ArgumentException>(
                () => h.Client.CreateConnectionAsync(Accommodation, new OctorateConnectionCreate("booking", "H", Username, Password), CancellationToken.None));
            Assert.DoesNotContain(Password, bad.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Username, bad.Message, StringComparison.Ordinal);
        }

        Assert.Equal(calls, h.Requests.Count);

        // The objects that hold the credentials do not print them (a log of the object, or an exception that names it, would).
        var patch = new OctorateConnectionPatch { Username = Username, Password = Password, UpdateCalendar = true };
        foreach (var text in new[] { request.ToString(), patch.ToString(), (request with { }).ToString() })
        {
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Username, text, StringComparison.Ordinal);
        }

        // The proof: the log has the line of the connection that was made (its id, portal and status) and not once a credential.
        var output = h.Logs.AllOutput;
        Assert.Contains("7001", output, StringComparison.Ordinal);
        Assert.Contains("142", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Username, output, StringComparison.Ordinal);
        Assert.DoesNotContain("H-4471", output, StringComparison.Ordinal);
    }
}
