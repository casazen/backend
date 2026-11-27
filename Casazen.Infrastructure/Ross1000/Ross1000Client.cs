using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Ross1000;

public sealed record Ross1000RecordOutcome(string Key, string State);
public sealed record Ross1000Result(string State, int Accepted = 0, int Rejected = 0)
{
    // Only keys matched against this payload leave the parser. Remote text may include personal data.
    public IReadOnlyList<Ross1000RecordOutcome> Records { get; init; } = [];
}
public sealed class Ross1000Client(HttpClient http)
{
    public static readonly Uri Lombardia = new("https://www.flussituristici.servizirl.it/Turismo5/app/ws/checkinV2");
    internal static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    internal static readonly XNamespace Service = "http://checkin.ws.service.turismo5.gies.it/";

    public async Task<Ross1000Result> SendAsync(PropertyIstatCredentials credentials, Ross1000Movement movement, CancellationToken ct)
    {
        var envelope = new XElement(Soap + "Envelope", new XElement(Soap + "Body",
            new XElement(Service + "inviaMovimentazioneList", movement.Xml)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Lombardia);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")));
        request.Headers.Add("SOAPAction", "\"\"");
        request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new("credentials_rejected");
            if (!response.IsSuccessStatusCode) return new("unknown");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            return Interpret(await XDocument.LoadAsync(reader, LoadOptions.None, ct), movement);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or XmlException)
        {
            // The remote server may have committed before the caller lost the response.
            return new("unknown");
        }
    }

    public static Ross1000Result Interpret(XDocument response, Ross1000Movement movement)
    {
        var result = response.Root?.Name == Soap + "Envelope" ? response.Root.Element(Soap + "Body")?.Element(Service + "inviaMovimentazioneListResponse")?.Element("return") : null;
        if (result is null) return new("unknown");
        var structures = result.Elements("struttura").ToArray();
        if (structures.Length != 1 || (string?)structures[0].Attribute("codice") != movement.Xml.Element("codice")?.Value)
            return new("unknown");
        var days = structures[0].Elements("giorno").ToArray();
        if (days.Length != 1 || (string?)days[0].Attribute("data") != movement.Xml.Element("movimento")?.Element("data")?.Value ||
            !string.IsNullOrWhiteSpace((string?)result.Element("errore")) || !string.IsNullOrWhiteSpace((string?)structures[0].Element("errore")))
            return new("unknown");

        var day = days[0];
        // Italy's current XSD defines day errors at the parent levels only. Treat explicit error extensions and unknown
        // children as unconfirmed as well, especially for a day with no guest records to corroborate acceptance.
        if (day.Elements().Any(e => e.Name != "arrivi" && e.Name != "partenze" && e.Name != "prenotazioni" && e.Name != "retifiche"))
            return new("unknown");
        var expected = movement.RecordKeys.ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<Ross1000RecordOutcome>();
        var accepted = 0;
        var rejected = 0;
        foreach (var record in days[0].Elements().SelectMany(group => group.Elements()))
        {
            var key = record.Name.LocalName + "/" + record.Element("idswh")?.Value;
            if ((record.Name == "arrivo" && record.Parent?.Name != "arrivi") ||
                (record.Name == "partenza" && record.Parent?.Name != "partenze")) return new("unknown");
            var success = record.Element("successo")?.Value;
            if (!expected.Remove(key) || success is not ("true" or "false" or "1" or "0")) return new("unknown");
            var ok = success is "true" or "1";
            if (ok && !string.IsNullOrWhiteSpace((string?)record.Element("errore")))
                return new("unknown", accepted, rejected) { Records = outcomes };
            outcomes.Add(new(key, ok ? "accepted" : "rejected"));
            if (ok) accepted++; else rejected++;
        }
        if (expected.Count > 0) return new("unknown", accepted, rejected) { Records = outcomes };
        return new(rejected == 0 ? "accepted" : accepted > 0 ? "partial" : "rejected", accepted, rejected) { Records = outcomes };
    }
}
