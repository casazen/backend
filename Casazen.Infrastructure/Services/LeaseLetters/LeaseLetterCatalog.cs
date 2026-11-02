using System.Text;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services.LeaseContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.LeaseLetters;

/// <summary>State of the text of a letter, from the least to the most usable.</summary>
public enum LeaseLetterStatus
{
    /// <summary>The file of the version is not there (or cannot be read).</summary>
    Missing,

    /// <summary>The file exists but a required section has no text, a placeholder is wrong or the title is missing.</summary>
    Incomplete,

    /// <summary>Complete, but not approved by the product owner (or the approval is not valid): printed with a watermark.</summary>
    NotApproved,

    /// <summary>Complete and approved: printed without the watermark.</summary>
    Approved,
}

/// <summary>The text of one letter as loaded from its file, with the reasons it cannot be used or approved.</summary>
public sealed record LeaseLetterState(
    LeaseLetterKind Kind,
    string VersionId,
    LeaseLetterStatus Status,
    string? Title,
    IReadOnlyList<LeaseContractTemplateSection> Sections,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> Issues,
    bool DeclaredApproved,
    IReadOnlyList<string> InvalidApprovalReasons)
{
    public bool IsApproved => Status == LeaseLetterStatus.Approved;

    /// <summary>A letter can be built from a complete text, approved or not.</summary>
    public bool IsUsable => Status is LeaseLetterStatus.NotApproved or LeaseLetterStatus.Approved;

    /// <summary>Everything that keeps the text from being used or approved, for logs and startup errors.</summary>
    public IEnumerable<string> Problems =>
        MissingSections.Select(id => $"section '{id}' has no text")
            .Concat(Issues)
            .Concat(InvalidApprovalReasons);
}

public interface ILeaseLetterCatalog
{
    LeaseLetterState Get(LeaseLetterKind kind);
}

/// <summary>
/// Loads the files of the letters (<c>{LettersDirectory}/{Kind}/{VersionId}.md</c>, same grammar as the contract templates) and computes
/// their state against the required sections (<see cref="LeaseLetterStructure"/>) and the approval rules (<see cref="LeaseTemplateApproval"/>,
/// the same as for a contract). Without a <c>VersionId</c> the draft shipped with the API is used, and it is not approved; the product owner
/// names the version a lawyer reviewed and approves it on the host. Files are read once per process.
/// </summary>
public sealed class LeaseLetterCatalog(
    IOptions<LeaseLetterOptions> options,
    IHostEnvironment environment,
    ILogger<LeaseLetterCatalog> logger) : ILeaseLetterCatalog
{
    public const string FileExtension = ".md";

    private readonly Lazy<IReadOnlyDictionary<LeaseLetterKind, LeaseLetterState>> _states =
        new(() => LoadAll(options.Value, environment.ContentRootPath, logger));

    public LeaseLetterState Get(LeaseLetterKind kind) => _states.Value[kind];

    public static string ResolveDirectory(LeaseLetterOptions options, string contentRootPath)
    {
        var directory = string.IsNullOrWhiteSpace(options.LettersDirectory)
            ? LeaseLetterOptions.DefaultLettersDirectory
            : options.LettersDirectory.Trim();
        return Path.IsPathRooted(directory) ? directory : Path.Combine(contentRootPath, directory);
    }

    /// <summary>Loads and checks the text of <paramref name="kind"/> (pure apart from reading the file).</summary>
    public static LeaseLetterState Load(LeaseLetterKind kind, LeaseLetterOptions options, string contentRootPath)
    {
        var variant = options.Variants.TryGetValue(kind.ToString(), out var configured)
            ? configured
            : new LeaseTemplateVariantOptions();
        var invalidApproval = LeaseTemplateApproval.GetInvalidApprovalReasons(variant);
        var configuredVersion = variant.VersionId?.Trim();
        var versionId = string.IsNullOrEmpty(configuredVersion) ? LeaseLetterStructure.DraftVersion(kind) : configuredVersion;
        var required = LeaseLetterStructure.Sections(kind);

        LeaseLetterState Missing(string issue) => new(
            kind, versionId, LeaseLetterStatus.Missing, null, [], required.Select(s => s.Id).ToList(), [issue], variant.Approved, invalidApproval);

        if (!LeaseTemplateApproval.IsValidVersionId(versionId))
            return Missing("VersionId is not a valid file name");

        var relativePath = $"{kind}/{versionId}{FileExtension}";
        var path = Path.Combine(ResolveDirectory(options, contentRootPath), kind.ToString(), versionId + FileExtension);
        string content;
        try
        {
            if (!File.Exists(path))
                return Missing($"letter file {relativePath} not found");
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing($"letter file {relativePath} cannot be read ({ex.GetType().Name})");
        }

        var parsed = LeaseContractTemplateParser.Parse(content, LeaseLetterPlaceholders.IsKnown);
        var issues = new List<string>(parsed.Errors);
        if (parsed.Title is null)
            issues.Add("the title ('# ' line) is missing");

        var missingSections = new List<string>();
        foreach (var definition in required)
        {
            var section = parsed.Sections.FirstOrDefault(s => s.Id == definition.Id);
            if (section is null || section.Text.Length == 0)
            {
                missingSections.Add(definition.Id);
                continue;
            }

            foreach (var placeholder in definition.RequiredPlaceholders ?? [])
            {
                if (!section.Placeholders.Contains(placeholder))
                    issues.Add($"section '{definition.Id}' must use {{{{{placeholder}}}}}");
            }
        }

        foreach (var extra in parsed.Sections.Where(s => required.All(d => d.Id != s.Id)))
            issues.Add($"section '{extra.Id}' is not part of this letter");

        var status = missingSections.Count > 0 || issues.Count > 0
            ? LeaseLetterStatus.Incomplete
            : variant.Approved && invalidApproval.Count == 0
                ? LeaseLetterStatus.Approved
                : LeaseLetterStatus.NotApproved;

        return new LeaseLetterState(kind, versionId, status, parsed.Title, parsed.Sections, missingSections, issues, variant.Approved, invalidApproval);
    }

    private static IReadOnlyDictionary<LeaseLetterKind, LeaseLetterState> LoadAll(
        LeaseLetterOptions options, string contentRootPath, ILogger logger)
    {
        var states = Enum.GetValues<LeaseLetterKind>().ToDictionary(k => k, k => Load(k, options, contentRootPath));
        foreach (var state in states.Values)
        {
            if ((state.DeclaredApproved && !state.IsApproved) || !state.IsUsable)
            {
                logger.LogWarning(
                    "Lease letter {Kind} is {Status} (version {VersionId}, declared approved: {Declared}): {Problems}. " +
                    "It is printed as a draft or not at all; see docs/runbooks/lease-operations.md",
                    state.Kind, state.Status, state.VersionId, state.DeclaredApproved, string.Join("; ", state.Problems));
            }
            else
            {
                logger.LogInformation("Lease letter {Kind}: {Status} (version {VersionId})", state.Kind, state.Status, state.VersionId);
            }
        }

        return states;
    }
}
