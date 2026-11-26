using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>Resend sends signed Svix events for all messages. Only tagged campaign deliveries contribute to marketing metrics.</summary>
[ApiController]
[Route("api/webhooks/marketing/resend")]
[AllowAnonymous]
public sealed class MarketingDeliveryWebhookController(AppDbContext db, IConfiguration config, TimeProvider clock) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var secret = config["Email:MarketingWebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret)) return StatusCode(503);
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(ct);
        if (!ValidSignature(secret, payload, Request.Headers["svix-id"].ToString(), Request.Headers["svix-timestamp"].ToString(),
                Request.Headers["svix-signature"].ToString(), clock.GetUtcNow().ToUnixTimeSeconds())) return Unauthorized();
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return BadRequest(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return BadRequest();
            if (!data.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object ||
                !tags.TryGetProperty("campaign_delivery", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(tag.GetString(), "N", out var id)) return NoContent();
            // The server-issued random tag is authenticated by the provider. This public callback has no tenant context.
            var deliveries = db.CampaignDeliveries.IgnoreQueryFilters().Where(d => d.Id == id);
            switch (type.GetString())
            {
                case "email.opened": await deliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.Opened, true), ct); break;
                case "email.clicked": await deliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.Clicked, true), ct); break;
                case "email.bounced": await deliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.Bounced, true), ct); break;
            }
            return NoContent();
        }
    }

    internal static bool ValidSignature(string secret, string payload, string id, string timestamp, string signatures, long now)
    {
        if (string.IsNullOrWhiteSpace(id) || !long.TryParse(timestamp, out var instant) || instant < now - 300 || instant > now + 300)
            return false;
        byte[] key;
        try { key = Convert.FromBase64String(secret.StartsWith("whsec-", StringComparison.Ordinal) ? secret[6..] : secret); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{payload}"));
        var candidate = new byte[32];
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s.StartsWith("v1,", StringComparison.Ordinal) &&
            Convert.TryFromBase64String(s.AsSpan(3), candidate, out var length) && length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(expected, candidate));
    }
}
