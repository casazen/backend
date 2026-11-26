using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.External;

/// <summary>SOAP 1.1 as published by the CEN, MANUALEWS sections 3.2–3.3. No automatic retries.</summary>
public sealed class DirectAlloggiatiSoap(HttpClient client)
{
    public static readonly Uri Endpoint = new("https://alloggiatiweb.poliziadistato.it/service/service.asmx");
    private static readonly XNamespace Ns = "AlloggiatiService";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    public async Task<string> TokenAsync(PropertyQuesturaCredentials account, CancellationToken ct)
    {
        var response = await CallAsync("GenerateToken", [
            Field("Utente", account.Username), Field("Password", account.Password), Field("WsKey", account.WsKey)], ct);
        var token = response.Element(Ns + "GenerateTokenResult")?.Element(Ns + "token")?.Value;
        if (!Success(response.Element(Ns + "result")) || string.IsNullOrWhiteSpace(token))
            throw new InvalidDataException("alloggiati_authentication_failed");
        return token;
    }

    public async Task<bool> VerifyAsync(PropertyQuesturaCredentials account, string token, CancellationToken ct)
    {
        var response = await CallAsync("Authentication_Test", [Field("Utente", account.Username), Field("token", token)], ct);
        return Success(response.Element(Ns + "Authentication_TestResult"));
    }

    public async Task<bool[]> RowsAsync(PropertyQuesturaCredentials account, string token, string[] rows, bool send, CancellationToken ct)
    {
        var operation = send ? "Send" : "Test";
        var fields = new List<XElement> { Field("Utente", account.Username), Field("token", token),
            new(Ns + "ElencoSchedine", rows.Select(row => Field("string", row))) };
        if (account.Profile == AlloggiatiProfile.ApartmentsManagement)
        {
            if (!int.TryParse(account.ApartmentId, NumberStyles.None, CultureInfo.InvariantCulture, out var apartmentId) || apartmentId < 1)
                throw new InvalidDataException("alloggiati_apartment_id_invalid");
            operation = "GestioneAppartamenti_" + operation;
            fields.Add(new XElement(Ns + "IdAppartamento", apartmentId));
        }
        else if (account.Profile != AlloggiatiProfile.SingleStructure)
            throw new InvalidDataException("alloggiati_profile_unknown");

        var response = await CallAsync(operation, fields, ct);
        var result = response.Element(Ns + "result");
        var outcomes = result?.Element(Ns + "Dettaglio")?.Elements(Ns + "EsitoOperazioneServizio").Select(Success).ToArray();
        if (outcomes is null || outcomes.Length != rows.Length ||
            !int.TryParse(result?.Element(Ns + "SchedineValide")?.Value, out var accepted) || accepted != outcomes.Count(x => x))
            throw new InvalidDataException("alloggiati_row_outcomes_unknown");
        if (!Success(response.Element(Ns + (operation + "Result"))) && accepted > 0)
            throw new InvalidDataException("alloggiati_inconsistent_outcome");
        return outcomes;
    }

    public async Task<byte[]> ReceiptAsync(string user, string token, DateOnly day, CancellationToken ct)
    {
        var response = await CallAsync("Ricevuta", [Field("Utente", user), Field("token", token),
            Field("Data", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00")], ct);
        if (!Success(response.Element(Ns + "RicevutaResult"))) throw new InvalidDataException("alloggiati_receipt_unavailable");
        var bytes = Convert.FromBase64String(response.Element(Ns + "PDF")?.Value ?? "");
        if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new InvalidDataException("alloggiati_invalid_pdf");
        return bytes;
    }

    private static XElement Field(string name, string value) => new(Ns + name, value);

    private static bool Success(XElement? result)
    {
        var raw = result?.Element(Ns + "esito")?.Value;
        return raw switch { "true" or "1" => true, "false" or "0" => false, _ => throw new InvalidDataException("alloggiati_invalid_outcome") };
    }

    private async Task<XElement> CallAsync(string operation, IEnumerable<XElement> fields, CancellationToken ct)
    {
        var envelope = new XElement(Soap + "Envelope", new XElement(Soap + "Body", new XElement(Ns + operation, fields)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", "\"" + Ns.NamespaceName + "/" + operation + "\"");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8388608 });
        var doc = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        return doc.Element(Soap + "Envelope")?.Element(Soap + "Body")?.Element(Ns + (operation + "Response"))
            ?? throw new InvalidDataException("alloggiati_unexpected_response");
    }
}
