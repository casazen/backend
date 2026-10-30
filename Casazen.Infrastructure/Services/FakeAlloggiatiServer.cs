using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Fake SOAP server for Alloggiati Web (ADR-015, phase 1). Returns mock responses matching manual examples
/// (pp. 7-18) without calling the real Questura service. Controlled by <c>Features:AutomaticAlloggiati</c> flag.
/// All operations return test confirmation numbers and simulate successful flows.
/// </summary>
public class FakeAlloggiatiServer(ILogger<FakeAlloggiatiServer> logger)
{
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string AlloggiatiNs = "http://alloggiatiweb.poliziadistato.it/";
    private const string TestToken = "TEST_TOKEN_12345";
    private const string TestConfirmationPrefix = "TEST_";

    /// <summary>
    /// Mock GenerateToken response (p. 7 of manual).
    /// </summary>
    public string GenerateTokenResponse(string wsKey)
    {
        logger.LogDebug("Fake Alloggiati: GenerateToken for WSKEY={WSKey}", MaskSecret(wsKey));

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("GenerateTokenResponse", AlloggiatiNs),
                    new XElement(XName.Get("Token", AlloggiatiNs), TestToken))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Mock Authentication_Test response (p. 8).
    /// </summary>
    public string AuthenticationTestResponse(string username, string password)
    {
        logger.LogDebug("Fake Alloggiati: Authentication_Test for user={User}", username);

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("Authentication_TestResponse", AlloggiatiNs),
                    new XElement(XName.Get("Result", AlloggiatiNs), "OK"))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Mock Test response (pp. 9-11). Returns empty errors array (all rows valid) in phase 1.
    /// Real validation would check 168/174-char format and field rules.
    /// </summary>
    public string TestResponse(string recordData)
    {
        var rowCount = recordData.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        logger.LogDebug("Fake Alloggiati: Test for {RowCount} rows (all valid in phase 1)", rowCount);

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("TestResponse", AlloggiatiNs),
                    new XElement(XName.Get("Result", AlloggiatiNs), "OK"),
                    new XElement(XName.Get("Errors", AlloggiatiNs)))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Mock Send response (pp. 12-14). Returns test confirmation number.
    /// </summary>
    public string SendResponse(string recordData)
    {
        var rowCount = recordData.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var confirmationNumber = $"{TestConfirmationPrefix}{DateTime.UtcNow:yyyyMMddHHmmss}_{rowCount}";

        logger.LogInformation(
            "Fake Alloggiati: Send for {RowCount} rows, confirmation={Confirmation}",
            rowCount,
            confirmationNumber);

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("SendResponse", AlloggiatiNs),
                    new XElement(XName.Get("Result", AlloggiatiNs), "OK"),
                    new XElement(XName.Get("NumeroRicevuta", AlloggiatiNs), confirmationNumber))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Mock Ricevuta response (pp. 15-17). Returns fake receipts for testing.
    /// Real implementation would query Questura for actual receipts.
    /// </summary>
    public string RicevutaResponse(DateTime fromDate, DateTime toDate)
    {
        logger.LogDebug(
            "Fake Alloggiati: Ricevuta from {FromDate} to {ToDate}",
            fromDate.ToString("yyyy-MM-dd"),
            toDate.ToString("yyyy-MM-dd"));

        // Generate one test receipt per day in range (max 3 for demo)
        var receipts = new List<XElement>();
        var days = Math.Min((toDate - fromDate).Days + 1, 3);

        for (var i = 0; i < days; i++)
        {
            var receiptDate = fromDate.AddDays(i);
            var confirmationNumber = $"{TestConfirmationPrefix}{receiptDate:yyyyMMdd}_01";

            receipts.Add(new XElement(XName.Get("RicevutaItem", AlloggiatiNs),
                new XElement(XName.Get("NumeroRicevuta", AlloggiatiNs), confirmationNumber),
                new XElement(XName.Get("Data", AlloggiatiNs), receiptDate.ToString("yyyy-MM-dd")),
                new XElement(XName.Get("PdfContent", AlloggiatiNs), GenerateTestPdfBase64())));
        }

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("RicevutaResponse", AlloggiatiNs),
                    new XElement(XName.Get("Result", AlloggiatiNs), "OK"),
                    new XElement(XName.Get("Receipts", AlloggiatiNs), receipts))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Mock Tabella response (p. 18). Returns empty in phase 1 (code tables already imported).
    /// </summary>
    public string TabellaResponse(string tableName)
    {
        logger.LogDebug("Fake Alloggiati: Tabella for {TableName} (empty in phase 1)", tableName);

        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs),
                new XElement(XName.Get("TabellaResponse", AlloggiatiNs),
                    new XElement(XName.Get("Result", AlloggiatiNs), "OK"),
                    new XElement(XName.Get("Data", AlloggiatiNs), ""))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Generate minimal test PDF as base64 (placeholder - real receipts are PDF documents).
    /// </summary>
    private static string GenerateTestPdfBase64()
    {
        // Minimal PDF header for testing (real receipts would be actual PDF content)
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                        "2 0 obj\n<< /Type /Pages /Count 0 >>\nendobj\nxref\n0 3\n" +
                        "trailer\n<< /Root 1 0 R >>\n%%EOF";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pdfContent));
    }

    /// <summary>
    /// Mask secret values in logs (show only first 4 chars).
    /// </summary>
    private static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length <= 4)
            return "***";
        return $"{secret[..4]}***";
    }
}
