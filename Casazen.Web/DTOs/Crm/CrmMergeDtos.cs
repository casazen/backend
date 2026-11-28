using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Crm;

// The merge proposals of the CRM (E07/F01/US03/T02, NF-88). The values of the contacts are masked; nothing here holds a document, a birth date,
// a place of birth, a stay guest or an Alloggiati field (AC-NF88-6).

public class CrmMergeContactDto
{
    public string Channel { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string ValueMasked { get; set; } = string.Empty;

    public static CrmMergeContactDto From(CrmMergeContactSummary contact) => new()
    {
        Channel = contact.Channel.ToString(),
        Origin = contact.Origin.ToString(),
        ValueMasked = contact.ValueMasked,
    };
}

public class CrmMergeProfileDto
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public int Stays { get; set; }

    public DateTime? LastStayAt { get; set; }

    public List<string> PropertyNames { get; set; } = [];

    public List<CrmMergeContactDto> Contacts { get; set; } = [];

    public static CrmMergeProfileDto From(CrmMergeProfileSummary profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        Country = profile.Country,
        Stays = profile.Stays,
        LastStayAt = profile.LastStayAt,
        PropertyNames = profile.PropertyNames.ToList(),
        Contacts = profile.Contacts.Select(CrmMergeContactDto.From).ToList(),
    };
}

public class CrmMergeCandidateDto
{
    public Guid Id { get; set; }

    public int Score { get; set; }

    public List<string> Reasons { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public CrmMergeProfileDto ProfileA { get; set; } = new();

    public CrmMergeProfileDto ProfileB { get; set; } = new();

    public static CrmMergeCandidateDto From(CrmMergeCandidateView candidate) => new()
    {
        Id = candidate.Id,
        Score = candidate.Score,
        Reasons = candidate.Reasons.ToList(),
        CreatedAt = candidate.CreatedAt,
        ProfileA = CrmMergeProfileDto.From(candidate.ProfileA),
        ProfileB = CrmMergeProfileDto.From(candidate.ProfileB),
    };
}

public class MergeCrmProfilesRequest
{
    public Guid SurvivorProfileId { get; set; }
}
