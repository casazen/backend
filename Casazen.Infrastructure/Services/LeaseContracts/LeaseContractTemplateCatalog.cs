using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services.LeaseContracts;

/// <summary>State of the template of a fiscal regime, from the least to the most usable.</summary>
public enum LeaseContractTemplateStatus
{
    /// <summary>No version configured or no file for it.</summary>
    Missing,

    /// <summary>The file exists but a required section has no text, a placeholder is wrong or the title is missing.</summary>
    Incomplete,

    /// <summary>Complete, but not approved (or the approval lacks a real version or its evidence).</summary>
    NotApproved,

    /// <summary>Complete and approved: the only state that generates the final contract.</summary>
    Approved,
}

/// <summary>Template of a fiscal regime as loaded at startup, with the reasons it cannot be used.</summary>
public sealed record LeaseContractTemplateState(
    FiscalRegime Regime,
    string? VersionId,
    LeaseContractTemplateStatus Status,
    string? Title,
    IReadOnlyList<LeaseContractTemplateSection> Sections,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> Issues,
    bool DeclaredApproved,
    IReadOnlyList<string> InvalidApprovalReasons)
{
    public bool IsApproved => Status == LeaseContractTemplateStatus.Approved;

    /// <summary>Everything that keeps the template from being approved, for logs and startup errors.</summary>
    public IEnumerable<string> Problems =>
        MissingSections.Select(id => $"section '{id}' has no text")
            .Concat(Issues)
            .Concat(InvalidApprovalReasons);
}

public interface ILeaseContractTemplateCatalog
{
    LeaseContractTemplateState Get(FiscalRegime regime);
}

/// <summary>
/// Loads the template files of the product owner, one per fiscal regime (<c>{TemplatesDirectory}/{Regime}/{VersionId}.md</c>),
/// and computes their state against the required structure (<see cref="LeaseContractTemplateStructure"/>) and the
/// approval rules (<see cref="LeaseTemplateApproval"/>). Files are read once per process: restart after changing them.
/// </summary>
public sealed class LeaseContractTemplateCatalog(
    IOptions<LeaseTemplateOptions> options,
    IHostEnvironment environment,
    ILogger<LeaseContractTemplateCatalog> logger) : ILeaseContractTemplateCatalog
{
    public const string FileExtension = ".md";

    private readonly Lazy<IReadOnlyDictionary<FiscalRegime, LeaseContractTemplateState>> _states =
        new(() => LoadAll(options.Value, environment.ContentRootPath, logger));

    public LeaseContractTemplateState Get(FiscalRegime regime) => _states.Value[regime];

    public static string ResolveDirectory(LeaseTemplateOptions options, string contentRootPath)
    {
        var directory = string.IsNullOrWhiteSpace(options.TemplatesDirectory)
            ? LeaseTemplateOptions.DefaultTemplatesDirectory
            : options.TemplatesDirectory.Trim();
        return Path.IsPathRooted(directory) ? directory : Path.Combine(contentRootPath, directory);
    }

    /// <summary>Loads and checks the template of <paramref name="regime"/> (pure apart from reading the file).</summary>
    public static LeaseContractTemplateState Load(FiscalRegime regime, LeaseTemplateOptions options, string contentRootPath)
    {
        var variant = options.Variants.TryGetValue(regime.ToString(), out var configured)
            ? configured
            : new LeaseTemplateVariantOptions();
        var invalidApproval = LeaseTemplateApproval.GetInvalidApprovalReasons(variant);
        var versionId = variant.VersionId?.Trim();

        LeaseContractTemplateState Missing(string issue) => new(
            regime, string.IsNullOrEmpty(versionId) ? null : versionId, LeaseContractTemplateStatus.Missing, null, [],
            LeaseContractTemplateStructure.RequiredSections(regime).Select(s => s.Id).ToList(), [issue],
            variant.Approved, invalidApproval);

        if (string.IsNullOrEmpty(versionId))
            return Missing("no VersionId configured");
        if (!LeaseTemplateApproval.IsValidVersionId(versionId))
            return Missing("VersionId is not a valid file name");

        var relativePath = $"{regime}/{versionId}{FileExtension}";
        var path = Path.Combine(ResolveDirectory(options, contentRootPath), regime.ToString(), versionId + FileExtension);
        string content;
        try
        {
            if (!File.Exists(path))
                return Missing($"template file {relativePath} not found");
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing($"template file {relativePath} cannot be read ({ex.GetType().Name})");
        }

        var parsed = LeaseContractTemplateParser.Parse(content);
        var issues = new List<string>(parsed.Errors);
        if (parsed.Title is null)
            issues.Add("the title ('# ' line) is missing");

        var requiredSections = LeaseContractTemplateStructure.RequiredSections(regime);
        var missingSections = new List<string>();
        foreach (var definition in requiredSections)
        {
            var section = parsed.Sections.FirstOrDefault(s => s.Id == definition.Id);
            if (section is null || section.Text.Length == 0)
            {
                missingSections.Add(definition.Id);
                continue;
            }

            foreach (var group in definition.RequiredPlaceholders.Where(g => !g.Any(section.Placeholders.Contains)))
                issues.Add($"section '{definition.Id}' must use {string.Join(" or ", group.Select(p => $"{{{{{p}}}}}"))}");
        }

        foreach (var extra in parsed.Sections.Where(s => s.Text.Length == 0 && requiredSections.All(d => d.Id != s.Id)))
            issues.Add($"section '{extra.Id}' has no text");

        var status = missingSections.Count > 0 || issues.Count > 0
            ? LeaseContractTemplateStatus.Incomplete
            : variant.Approved && invalidApproval.Count == 0
                ? LeaseContractTemplateStatus.Approved
                : LeaseContractTemplateStatus.NotApproved;

        return new LeaseContractTemplateState(
            regime, versionId, status, parsed.Title, parsed.Sections, missingSections, issues, variant.Approved, invalidApproval);
    }

    private static IReadOnlyDictionary<FiscalRegime, LeaseContractTemplateState> LoadAll(
        LeaseTemplateOptions options, string contentRootPath, ILogger logger)
    {
        var states = Enum.GetValues<FiscalRegime>().ToDictionary(r => r, r => Load(r, options, contentRootPath));
        foreach (var state in states.Values)
        {
            if (state.DeclaredApproved && !state.IsApproved)
            {
                logger.LogWarning(
                    "Lease contract template {Regime} is declared approved but is {Status} (version {VersionId}): {Problems}. " +
                    "Final contracts are blocked; see docs/runbooks/lease-contract-templates.md",
                    state.Regime, state.Status, state.VersionId, string.Join("; ", state.Problems));
            }
            else
            {
                logger.LogInformation(
                    "Lease contract template {Regime}: {Status} (version {VersionId}). Missing sections: {MissingSections}",
                    state.Regime, state.Status, state.VersionId, string.Join(", ", state.MissingSections));
            }
        }

        return states;
    }
}
