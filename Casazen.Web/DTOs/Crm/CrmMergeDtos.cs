using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Crm;

// The merge proposals of the CRM (E07/F01/US03/T02, NF-88). The values of the contacts are masked; nothing here holds a document, a birth date,
// a place of birth, a stay guest or an Alloggiati field (AC-NF88-6).

/// <summary>A contact of a profile in the summary of a proposal: the channel, where it came from, and the value masked.</summary>
public class CrmMergeContactDto
{
    /// <summary><c>Email</c>, <c>Phone</c> or <c>WhatsApp</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Where it came from: <c>OtaForward</c>, <c>CheckIn</c>, <c>DirectBooking</c>, <c>Manual</c>, <c>OctorateContactInfo</c>, <c>WelcomePage</c> or <c>GuestPage</c>.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The address (<c>m***@example.it</c>) or the number (<c>*********567</c>) with most of it hidden.</summary>
    public string ValueMasked { get; set; } = string.Empty;

    public static CrmMergeContactDto From(CrmMergeContactSummary contact) => new()
    {
        Channel = contact.Channel.ToString(),
        Origin = contact.Origin.ToString(),
        ValueMasked = contact.ValueMasked,
    };
}

/// <summary>One of the two profiles of a proposal, as much as a person needs to tell whether it is the same guest.</summary>
public class CrmMergeProfileDto
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>The stays that happened at the properties the caller reaches.</summary>
    public int Stays { get; set; }

    public DateTime? LastStayAt { get; set; }

    /// <summary>The properties the caller reaches where the guest has a booking that is not cancelled, A to Z.</summary>
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

/// <summary>A proposal to decide (<c>GET api/crm/merge-candidates</c>): how sure the matching is, why, and the two profiles side by side.</summary>
public class CrmMergeCandidateDto
{
    public Guid Id { get; set; }

    /// <summary>From 0 to 100.</summary>
    public int Score { get; set; }

    /// <summary>Why, as codes (<c>same_email</c>, <c>same_name_birth</c>, <c>same_phone</c>, <c>same_name_same_property</c>): never what matched.</summary>
    public List<string> Reasons { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>The profile with the smaller id, as the pair is stored.</summary>
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

/// <summary>The body of <c>POST api/crm/merge-candidates/{id}/merge</c>: the profile that stays.</summary>
public class MergeCrmProfilesRequest
{
    /// <summary>One of the two profiles of the proposal; anything else is 422 <c>crm_merge_survivor_invalid</c>.</summary>
    public Guid SurvivorProfileId { get; set; }
}
