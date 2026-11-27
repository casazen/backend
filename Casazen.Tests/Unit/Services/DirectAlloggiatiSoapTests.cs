using System.Net;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.External;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class DirectAlloggiatiSoapTests
{
    private static readonly XNamespace S = "AlloggiatiService";
    private static readonly XNamespace Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly PropertyQuesturaCredentials _credential = new() { Username = "user", Password = "pass", WsKey = "key" };

    [Fact]
    public async Task Authentication_uses_three_case_sensitive_fields_and_never_sends_a_row()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Fixture(request =>
        {
            var name = request.Name.LocalName;
            calls.Add(name);
            if (name == "GenerateToken")
            {
                Assert.Equal(new[] { "Utente", "Password", "WsKey" }, request.Elements().Select(e => e.Name.LocalName));
                return "<GenerateTokenResult><token>temporary</token></GenerateTokenResult><result><esito>true</esito></result>";
            }
            Assert.Equal(new[] { "Utente", "token" }, request.Elements().Select(e => e.Name.LocalName));
            return "<Authentication_TestResult><esito>true</esito></Authentication_TestResult>";
        }));
        var client = new DirectAlloggiatiSoap(http);
        var token = await client.TokenAsync(_credential, default);
        Assert.True(await client.VerifyAsync(_credential, token, default));
        Assert.Equal(new[] { "GenerateToken", "Authentication_Test" }, calls);
    }

    [Theory]
    [InlineData(false, false, "Test")]
    [InlineData(false, true, "Send")]
    [InlineData(true, false, "GestioneAppartamenti_Test")]
    [InlineData(true, true, "GestioneAppartamenti_Send")]
    public async Task Profile_routes_to_documented_operation_and_preserves_positions(bool apartments, bool send, string op)
    {
        if (apartments) { _credential.Profile = AlloggiatiProfile.ApartmentsManagement; _credential.ApartmentId = "17"; }
        using var http = new HttpClient(new Fixture(request =>
        {
            Assert.Equal(S + op, request.Name);
            Assert.Equal(apartments ? "17" : null, request.Element(S + "IdAppartamento")?.Value);
            Assert.Equal(new[] { "row1", "row2" }, request.Element(S + "ElencoSchedine")!.Elements(S + "string").Select(r => r.Value));
            return $"<{op}Result><esito>true</esito></{op}Result><result><SchedineValide>1</SchedineValide><Dettaglio><EsitoOperazioneServizio><esito>true</esito></EsitoOperazioneServizio><EsitoOperazioneServizio><esito>false</esito></EsitoOperazioneServizio></Dettaglio></result>";
        }));
        Assert.Equal(new[] { true, false }, await new DirectAlloggiatiSoap(http).RowsAsync(_credential, "temporary", ["row1", "row2"], send, default));
    }

    [Fact]
    public async Task Inconsistent_record_count_is_an_unknown_outcome()
    {
        using var http = new HttpClient(new Fixture(_ => "<SendResult><esito>true</esito></SendResult><result><SchedineValide>1</SchedineValide><Dettaglio/></result>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DirectAlloggiatiSoap(http).RowsAsync(_credential, "temporary", ["row"], true, default));
    }

    [Fact]
    public async Task Receipt_uses_one_day_and_readable_pdf_bytes()
    {
        using var http = new HttpClient(new Fixture(request =>
        {
            Assert.Equal("2026-05-01T00:00:00", request.Element(S + "Data")!.Value);
            return "<RicevutaResult><esito>true</esito></RicevutaResult><PDF>JVBERi0x</PDF>";
        }));
        var result = await new DirectAlloggiatiSoap(http).ReceiptAsync("user", "temporary", new(2026, 5, 1), default);
        Assert.Equal("%PDF-1", Encoding.ASCII.GetString(result));
    }

    private sealed class Fixture(Func<XElement, string> body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(DirectAlloggiatiSoap.Endpoint, request.RequestUri);
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var operation = Assert.Single(xml.Root!.Element(Envelope + "Body")!.Elements());
            var name = operation.Name.LocalName;
            Assert.Equal($"\"AlloggiatiService/{name}\"", request.Headers.GetValues("SOAPAction").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<s:Envelope xmlns:s='{Envelope}'><s:Body><{name}Response xmlns='AlloggiatiService'>{body(operation)}</{name}Response></s:Body></s:Envelope>")
            };
        }
    }
}
