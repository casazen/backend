using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.CfSimulator;

/// <summary>
/// The simulated module and status API of ConcordatoFacile (CF-SIM), for the test environment only: see <see cref="ICfSimulatorService"/>.
/// It keeps its practices in <c>cf_simulated_practices</c> and answers nothing about anybody's data: a practice has a number, a
/// municipality, a state and dates, and its documents speak about the house of its lease only to whoever holds its control token.
/// </summary>
public sealed partial class CfSimulatorService(
    AppDbContext db,
    IOptions<ConcordatoFacileOptions> options,
    IPdfDocumentRenderer pdfRenderer,
    TimeProvider timeProvider,
    ILogger<CfSimulatorService> logger) : ICfSimulatorService
{
    /// <summary>The reasons ConcordatoFacile gives for closing a practice without attestation (specification v0.2 §5).</summary>
    public static readonly IReadOnlyList<string> CloseReasons = ["duplicate", "withdrawn", "not_compliant", "other"];

    private const string DuplicateReason = "duplicate";
    private const string OtherReason = "other";
    private const int MaxPracticesOfALease = 20;

    [GeneratedRegex("^P-[0-9a-fA-F]{16}$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PracticeIdShape();

    public async Task<CfSimulatorLinkResult> CheckLinkAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        var check = CfSimulatorLinkVerifier.Verify(link, LinkSecret(), timeProvider.GetUtcNow());
        var comune = Clean(link.Comune, 200);
        var provincia = Clean(link.Provincia, 10);
        if (check.LeaseId is not { } leaseId)
            return new CfSimulatorLinkResult(false, check.Reason, null, comune, provincia, []);

        // Only a good link tells what the contract already has: the practices come with the lease, never without its signature.
        var practices = await db.CfSimulatedPractices
            .AsNoTracking()
            .Where(p => p.LeaseId == leaseId)
            .OrderByDescending(p => p.SubmittedAt)
            .Take(MaxPracticesOfALease)
            .ToListAsync(cancellationToken);

        return new CfSimulatorLinkResult(true, null, leaseId, comune, provincia, practices.Select(ToView).ToList());
    }

    public async Task<CfSimulatorCreatedPractice> CreatePracticeAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        // The module is filled in whatever the link: a link that is not good gives a practice with no lease (v0.2 §1).
        var check = CfSimulatorLinkVerifier.Verify(link, LinkSecret(), timeProvider.GetUtcNow());
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = NewControlToken();
        var practice = new CfSimulatedPractice
        {
            PracticeId = NewPracticeId(),
            LeaseId = check.Valid ? check.LeaseId : null,
            Comune = Clean(link.Comune, 200),
            Provincia = Clean(link.Provincia, 10),
            Status = CfPracticeStatus.Received,
            SubmittedAt = now,
            StatusChangedAt = now,
            ControlTokenHash = Hash(token),
        };

        if (practice.LeaseId is { } leaseId)
        {
            // A second practice for the same contract: the first, if it is not over, is closed as a duplicate (v0.2 §6).
            var notOver = await db.CfSimulatedPractices
                .Where(p => p.LeaseId == leaseId
                    && p.Status != CfPracticeStatus.Registered
                    && p.Status != CfPracticeStatus.ClosedWithoutAttestation)
                .ToListAsync(cancellationToken);
            foreach (var earlier in notOver)
            {
                earlier.Status = CfPracticeStatus.ClosedWithoutAttestation;
                earlier.CloseReason = DuplicateReason;
                earlier.StatusChangedAt = Later(now, earlier.StatusChangedAt);
            }
        }

        db.CfSimulatedPractices.Add(practice);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Simulated practice {PracticeId} received (lease linked: {Linked}, link not good because: {Reason})",
            practice.PracticeId,
            practice.LeaseId is not null,
            check.Reason ?? "-");

        return new CfSimulatorCreatedPractice(ToView(practice), practice.LeaseId is not null, token);
    }

    public async Task<CfSimulatorPracticeView?> SetStatusAsync(
        string practiceId,
        string? controlToken,
        CfPracticeStatus status,
        string? closeReason,
        CancellationToken cancellationToken = default)
    {
        var practice = await FindOwnedAsync(practiceId, controlToken, cancellationToken);
        if (practice is null)
            return null;

        practice.Status = status;
        practice.CloseReason = status == CfPracticeStatus.ClosedWithoutAttestation
            ? (closeReason is not null && CloseReasons.Contains(closeReason) ? closeReason : OtherReason)
            : null;
        practice.StatusChangedAt = Later(timeProvider.GetUtcNow().UtcDateTime, practice.StatusChangedAt);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Simulated practice {PracticeId} is now {Status}", practice.PracticeId, practice.Status);
        return ToView(practice);
    }

    public async Task<CfSimulatorDocument?> RenderDocumentAsync(
        string practiceId,
        string? controlToken,
        CfSimulatorDocumentKind kind,
        CancellationToken cancellationToken = default)
    {
        var practice = await FindOwnedAsync(practiceId, controlToken, cancellationToken);
        if (practice is null)
            return null;

        var house = await HouseOfAsync(practice, cancellationToken);
        var today = timeProvider.TodayInRomeAsDateOnly();
        var (content, name) = kind switch
        {
            CfSimulatorDocumentKind.Contract => (CfSimulatorDocuments.Contract(house, today), "contratto-esempio"),
            CfSimulatorDocumentKind.Attestation => (CfSimulatorDocuments.Attestation(house, practice.PracticeId, today), "attestazione-esempio"),
            CfSimulatorDocumentKind.Receipt => (CfSimulatorDocuments.Receipt(house, practice.PracticeId, today), "ricevuta-registrazione-esempio"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sample document."),
        };

        return new CfSimulatorDocument(pdfRenderer.Render(content), $"{name}-{practice.PracticeId}.pdf");
    }

    /// <summary>The practice, only when the number is well formed and the token is its own (the same <c>null</c> for every other case).</summary>
    private async Task<CfSimulatedPractice?> FindOwnedAsync(string practiceId, string? controlToken, CancellationToken cancellationToken)
    {
        if (!TryNormalizePracticeId(practiceId, out var number) || string.IsNullOrWhiteSpace(controlToken))
            return null;

        var practice = await db.CfSimulatedPractices.FirstOrDefaultAsync(p => p.PracticeId == number, cancellationToken);
        if (practice is null)
            return null;

        var offered = Encoding.ASCII.GetBytes(Hash(controlToken));
        var stored = Encoding.ASCII.GetBytes(practice.ControlTokenHash);
        return CryptographicOperations.FixedTimeEquals(offered, stored) ? practice : null;
    }

    /// <summary>The house a practice speaks about: that of its lease, or any house when it has none.</summary>
    private async Task<CfSimulatorHouse> HouseOfAsync(CfSimulatedPractice practice, CancellationToken cancellationToken)
    {
        if (practice.LeaseId is not { } leaseId)
            return CfSimulatorHouse.Generic(practice.Comune);

        // The endpoint is anonymous: no tenant filter applies, and the lease is the one of the signed link the practice was born from.
        var property = await (
                from lease in db.LeaseContracts.IgnoreQueryFilters()
                join house in db.Properties.IgnoreQueryFilters() on lease.PropertyId equals house.Id
                where lease.Id == leaseId
                select new { house.Address, house.City, house.PostalCode, house.ComuneIstatCode })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
            return CfSimulatorHouse.Generic(practice.Comune);

        var comune = string.IsNullOrWhiteSpace(property.ComuneIstatCode)
            ? null
            : await db.Comuni
                .Where(c => c.IstatCode == property.ComuneIstatCode)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken);

        var city = property.City.Trim();
        return new CfSimulatorHouse(property.Address.Trim(), city, property.PostalCode.Trim(), string.IsNullOrWhiteSpace(comune) ? city : comune);
    }

    /// <summary>The key of the signature: the same secret that signs the links (the variable wins over the configuration, as in the link service).</summary>
    private string? LinkSecret() => Environment.GetEnvironmentVariable("CONCORDATOFACILE_LINK_SECRET") ?? options.Value.LinkSecret;

    private static CfSimulatorPracticeView ToView(CfSimulatedPractice practice) => new(
        practice.PracticeId,
        practice.Status,
        practice.StatusChangedAt,
        practice.SubmittedAt,
        practice.Comune,
        practice.ComuneDiffersFromLink(),
        practice.CloseReason);

    /// <summary><c>P-</c> and 16 hexadecimal characters, in the form the simulator writes them (lowercase).</summary>
    internal static bool TryNormalizePracticeId(string? practiceId, out string number)
    {
        number = string.Empty;
        if (practiceId is null || !PracticeIdShape().IsMatch(practiceId))
            return false;

        number = "P-" + practiceId[2..].ToLowerInvariant();
        return true;
    }

    private static string NewPracticeId() => "P-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private static string NewControlToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>A state never goes back in time (v0.2 §5: <c>statusChangedAt</c> never goes backwards).</summary>
    private static DateTime Later(DateTime now, DateTime previous) => now > previous ? now : previous;

    /// <summary>The text of a parameter of the link, trimmed and cut to what the column holds; null when there is none.</summary>
    private static string? Clean(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
