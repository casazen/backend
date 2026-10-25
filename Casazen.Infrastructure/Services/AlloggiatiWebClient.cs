using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// SOAP 1.1 client for Alloggiati Web (art. 109 TULPS) communication with Questura (ADR-015).
/// Supports both Single Structure (property-level credentials) and Gestione Appartamenti (org-level with IdAppartamento).
/// Phase 1: routed to <see cref="FakeAlloggiatiServer"/> via feature flag.
/// </summary>
public class AlloggiatiWebClient(
    IHttpClientFactory httpClientFactory,
    AppDbContext db,
    IDataProtectionProvider dataProtectionProvider,
    IMemoryCache cache,
    ILogger<AlloggiatiWebClient> logger)
{
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string AlloggiatiNs = "http://alloggiatiweb.poliziadistato.it/";
    private const int TokenCacheMinutes = 60;

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        Infrastructure.Data.Encryption.EncryptedColumns.QuesturaCredentialsPurpose);

    /// <summary>
    /// Authenticate with Alloggiati Web and return a session token (cached for 1 hour per credential).
    /// </summary>
    public async Task<string> GenerateTokenAsync(Guid credentialId, CancellationToken ct = default)
    {
        var cacheKey = $"AlloggiatiToken_{credentialId}";
        if (cache.TryGetValue<string>(cacheKey, out var cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            logger.LogDebug("Using cached token for credential {CredentialId}", credentialId);
            return cachedToken;
        }

        var credential = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            ?? throw new InvalidOperationException($"Questura credential {credentialId} not found");

        var wsKey = _protector.Unprotect(credential.WsKey);

        var soapBody = new XElement(XName.Get("GenerateToken", AlloggiatiNs),
            new XElement(XName.Get("WSKEY", AlloggiatiNs), wsKey));

        var response = await SendSoapRequestAsync("GenerateToken", soapBody, ct);
        var token = response.Descendants(XName.Get("Token", AlloggiatiNs)).FirstOrDefault()?.Value
            ?? throw new InvalidOperationException("GenerateToken response missing Token element");

        cache.Set(cacheKey, token, TimeSpan.FromMinutes(TokenCacheMinutes));
        logger.LogInformation("Generated new Alloggiati token for credential {CredentialId}", credentialId);

        return token;
    }

    /// <summary>
    /// Test authentication with the Questura portal (does not validate records).
    /// </summary>
    public async Task<bool> AuthenticationTestAsync(Guid credentialId, CancellationToken ct = default)
    {
        var credential = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            ?? throw new InvalidOperationException($"Questura credential {credentialId} not found");

        var username = _protector.Unprotect(credential.Username);
        var password = _protector.Unprotect(credential.Password);
        var token = await GenerateTokenAsync(credentialId, ct);

        var soapBody = new XElement(XName.Get("Authentication_Test", AlloggiatiNs),
            new XElement(XName.Get("Token", AlloggiatiNs), token),
            new XElement(XName.Get("Username", AlloggiatiNs), username),
            new XElement(XName.Get("Password", AlloggiatiNs), password));

        var response = await SendSoapRequestAsync("Authentication_Test", soapBody, ct);
        var result = response.Descendants(XName.Get("Result", AlloggiatiNs)).FirstOrDefault()?.Value;

        return result == "OK";
    }

    /// <summary>
    /// Test rows before sending (returns per-row errors with TipoErrore codes).
    /// </summary>
    public async Task<AlloggiatiTestResult> TestAsync(
        Guid credentialId,
        IReadOnlyList<string> rows,
        CancellationToken ct = default)
    {
        var credential = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            ?? throw new InvalidOperationException($"Questura credential {credentialId} not found");

        var username = _protector.Unprotect(credential.Username);
        var password = _protector.Unprotect(credential.Password);
        var token = await GenerateTokenAsync(credentialId, ct);

        var recordData = string.Join("\n", rows);

        var soapBody = new XElement(XName.Get("Test", AlloggiatiNs),
            new XElement(XName.Get("Token", AlloggiatiNs), token),
            new XElement(XName.Get("Username", AlloggiatiNs), username),
            new XElement(XName.Get("Password", AlloggiatiNs), password),
            new XElement(XName.Get("RecordData", AlloggiatiNs), recordData));

        var response = await SendSoapRequestAsync("Test", soapBody, ct);

        var errors = new List<AlloggiatiRowError>();

        var errorElements = response.Descendants(XName.Get("Errore", AlloggiatiNs));
        foreach (var error in errorElements)
        {
            var rowNumber = int.Parse(error.Element(XName.Get("NumeroRiga", AlloggiatiNs))?.Value ?? "0");
            var tipoErrore = error.Element(XName.Get("TipoErrore", AlloggiatiNs))?.Value ?? "UNKNOWN";
            var descrizione = error.Element(XName.Get("Descrizione", AlloggiatiNs))?.Value ?? "";

            errors.Add(new AlloggiatiRowError
            {
                RowNumber = rowNumber,
                ErrorCode = tipoErrore,
                Description = descrizione
            });
        }

        return new AlloggiatiTestResult
        {
            Success = errors.Count == 0,
            Errors = errors
        };
    }

    /// <summary>
    /// Send validated rows to Alloggiati Web (only called after Test confirms all rows valid).
    /// </summary>
    public async Task<AlloggiatiSendResult> SendAsync(
        Guid credentialId,
        IReadOnlyList<string> rows,
        CancellationToken ct = default)
    {
        var credential = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            ?? throw new InvalidOperationException($"Questura credential {credentialId} not found");

        var username = _protector.Unprotect(credential.Username);
        var password = _protector.Unprotect(credential.Password);
        var token = await GenerateTokenAsync(credentialId, ct);

        var recordData = string.Join("\n", rows);

        var soapBody = new XElement(XName.Get("Send", AlloggiatiNs),
            new XElement(XName.Get("Token", AlloggiatiNs), token),
            new XElement(XName.Get("Username", AlloggiatiNs), username),
            new XElement(XName.Get("Password", AlloggiatiNs), password),
            new XElement(XName.Get("RecordData", AlloggiatiNs), recordData));

        var response = await SendSoapRequestAsync("Send", soapBody, ct);

        var confirmationNumber = response.Descendants(XName.Get("NumeroRicevuta", AlloggiatiNs))
            .FirstOrDefault()?.Value;

        if (string.IsNullOrEmpty(confirmationNumber))
        {
            throw new InvalidOperationException("Send response missing NumeroRicevuta element");
        }

        return new AlloggiatiSendResult
        {
            Success = true,
            ConfirmationNumber = confirmationNumber
        };
    }

    /// <summary>
    /// Retrieve receipts for a date range (daily job retrieves last 30 days per credential).
    /// </summary>
    public async Task<IReadOnlyList<AlloggiatiReceipt>> RicevutaAsync(
        Guid credentialId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken ct = default)
    {
        var credential = await db.PropertyQuesturaCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct)
            ?? throw new InvalidOperationException($"Questura credential {credentialId} not found");

        var username = _protector.Unprotect(credential.Username);
        var password = _protector.Unprotect(credential.Password);
        var token = await GenerateTokenAsync(credentialId, ct);

        var soapBody = new XElement(XName.Get("Ricevuta", AlloggiatiNs),
            new XElement(XName.Get("Token", AlloggiatiNs), token),
            new XElement(XName.Get("Username", AlloggiatiNs), username),
            new XElement(XName.Get("Password", AlloggiatiNs), password),
            new XElement(XName.Get("DataDa", AlloggiatiNs), fromDate.ToString("yyyy-MM-dd")),
            new XElement(XName.Get("DataA", AlloggiatiNs), toDate.ToString("yyyy-MM-dd")));

        var response = await SendSoapRequestAsync("Ricevuta", soapBody, ct);

        var receipts = new List<AlloggiatiReceipt>();
        var ricevutaElements = response.Descendants(XName.Get("RicevutaItem", AlloggiatiNs));

        foreach (var item in ricevutaElements)
        {
            var numeroRicevuta = item.Element(XName.Get("NumeroRicevuta", AlloggiatiNs))?.Value ?? "";
            var data = item.Element(XName.Get("Data", AlloggiatiNs))?.Value;
            var pdfBase64 = item.Element(XName.Get("PdfContent", AlloggiatiNs))?.Value;

            if (DateTime.TryParse(data, out var receiptDate) && !string.IsNullOrEmpty(numeroRicevuta))
            {
                receipts.Add(new AlloggiatiReceipt
                {
                    ConfirmationNumber = numeroRicevuta,
                    ReceiptDate = receiptDate,
                    PdfContent = pdfBase64
                });
            }
        }

        return receipts;
    }

    /// <summary>
    /// Retrieve official code tables (comuni, province, stati, etc.) from Alloggiati Web.
    /// </summary>
    public async Task<string> TabellaAsync(string tableName, CancellationToken ct = default)
    {
        // For phase 1, returns empty - code tables are already imported via AlloggiatiCodeTableService
        await Task.CompletedTask;
        logger.LogDebug("Tabella operation called for {TableName} (not implemented in phase 1)", tableName);
        return string.Empty;
    }

    private async Task<XElement> SendSoapRequestAsync(string action, XElement bodyContent, CancellationToken ct)
    {
        var envelope = new XElement(XName.Get("Envelope", SoapNs),
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tns", AlloggiatiNs),
            new XElement(XName.Get("Body", SoapNs), bodyContent));

        var soapContent = new StringContent(
            envelope.ToString(SaveOptions.DisableFormatting),
            System.Text.Encoding.UTF8,
            "text/xml");

        soapContent.Headers.Add("SOAPAction", $"{AlloggiatiNs}{action}");

        var client = httpClientFactory.CreateClient("AlloggiatiWeb");
        var response = await client.PostAsync("", soapContent, ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Alloggiati SOAP request {Action} failed with status {Status}",
                action,
                response.StatusCode);
            throw new InvalidOperationException(
                $"SOAP request {action} failed with status {response.StatusCode}");
        }

        var responseXml = await response.Content.ReadAsStringAsync(ct);
        var responseDoc = XDocument.Parse(responseXml);

        return responseDoc.Root?.Descendants(XName.Get("Body", SoapNs)).FirstOrDefault()
            ?? throw new InvalidOperationException("Invalid SOAP response structure");
    }
}

/// <summary>Result of Test operation with per-row errors.</summary>
public sealed record AlloggiatiTestResult
{
    public bool Success { get; init; }
    public List<AlloggiatiRowError> Errors { get; init; } = [];
}

/// <summary>Per-row error from Test operation.</summary>
public sealed record AlloggiatiRowError
{
    public int RowNumber { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

/// <summary>Result of Send operation.</summary>
public sealed record AlloggiatiSendResult
{
    public bool Success { get; init; }
    public string ConfirmationNumber { get; init; } = string.Empty;
}

/// <summary>Receipt retrieved from Ricevuta operation.</summary>
public sealed record AlloggiatiReceipt
{
    public string ConfirmationNumber { get; init; } = string.Empty;
    public DateTime ReceiptDate { get; init; }
    public string? PdfContent { get; init; }
}
