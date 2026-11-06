using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Crm;

// The API of the CRM profiles (E07/F01/US03/T01, NF-88). Every enumeration is written by name (the same names as the rest of the API:
// «Direct», «BookingCom», «ServiceOnly»...). None of these types has a document, a birth date, a place of birth, a stay guest or an
// Alloggiati field, and none ever will (AC-NF88-6): the proof reads these types by reflection.

/// <summary>A profile in the list (<c>GET api/crm/guests</c>).</summary>
public class CrmProfileListItemDto
{
    public Guid Id { get; set; }

    /// <summary>The name of the primary guest (or of the first guest the caller reaches); empty for a lead with no name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    /// <summary>The stays that happened; for a caller limited to some properties, at those properties only.</summary>
    public int TotalStays { get; set; }

    public decimal TotalSpend { get; set; }

    /// <summary>The first arrival; <c>null</c> when there is no stay.</summary>
    public DateTime? FirstStayAt { get; set; }

    /// <summary>The latest arrival; <c>null</c> when there is no stay.</summary>
    public DateTime? LastStayAt { get; set; }

    /// <summary>The channels the guest booked on (<c>Direct</c>, <c>Airbnb</c>, <c>BookingCom</c>...), the most used first.</summary>
    public List<string> Channels { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    /// <summary>The best e-mail address of the profile, masked (<c>m***@example.it</c>); empty when there is none.</summary>
    public string EmailMasked { get; set; } = string.Empty;

    /// <summary>The legal basis of that address: <c>None</c>, <c>ServiceOnly</c>, <c>SoftOptIn</c> or <c>Consent</c>.</summary>
    public string EmailBasis { get; set; } = string.Empty;

    /// <summary>At least one e-mail address of the profile may receive marketing.</summary>
    public bool EmailReachable { get; set; }

    /// <summary>The strongest legal basis of the WhatsApp numbers of the profile (<c>None</c> when there is none).</summary>
    public string WhatsappBasis { get; set; } = string.Empty;

    /// <summary>A merge proposal on this profile waits for a person, and the caller may decide it.</summary>
    public bool HasMergeProposal { get; set; }

    public static CrmProfileListItemDto From(CrmProfileListItem item) => new()
    {
        Id = item.Id,
        DisplayName = item.DisplayName,
        Country = item.Country,
        Language = item.Language,
        TotalStays = item.TotalStays,
        TotalSpend = item.TotalSpend,
        FirstStayAt = item.FirstStayAt,
        LastStayAt = item.LastStayAt,
        Channels = item.Channels.ToList(),
        Tags = item.Tags.ToList(),
        EmailMasked = item.EmailMasked,
        EmailBasis = item.EmailBasis.ToString(),
        EmailReachable = item.EmailReachable,
        WhatsappBasis = item.WhatsappBasis.ToString(),
        HasMergeProposal = item.HasMergeProposal,
    };
}

/// <summary>One booking in the history of a profile.</summary>
public class CrmProfileStayDto
{
    public Guid BookingId { get; set; }

    public Guid PropertyId { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public DateTime CheckInDate { get; set; }

    public DateTime CheckOutDate { get; set; }

    public int Nights { get; set; }

    /// <summary>The source of the booking: <c>Direct</c>, <c>Airbnb</c>, <c>BookingCom</c>, <c>Manual</c>, <c>OtherChannel</c>...</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The name of the channel for a source with no value of its own (<c>OtherChannel</c>) or a calendar feed; <c>null</c> otherwise.</summary>
    public string? ChannelLabel { get; set; }

    /// <summary>The status of the booking: <c>Pending</c>, <c>Confirmed</c>, <c>CheckedIn</c>, <c>CheckedOut</c> or <c>Cancelled</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public int Adults { get; set; }

    public int Children { get; set; }

    /// <summary>Days between the booking and the arrival; <c>null</c> when the booking was recorded after the arrival.</summary>
    public int? AdvanceDays { get; set; }

    public static CrmProfileStayDto From(CrmProfileStay stay) => new()
    {
        BookingId = stay.BookingId,
        PropertyId = stay.PropertyId,
        PropertyName = stay.PropertyName,
        CheckInDate = stay.CheckInDate,
        CheckOutDate = stay.CheckOutDate,
        Nights = stay.Nights,
        Channel = stay.Channel.ToString(),
        ChannelLabel = stay.ChannelLabel,
        Status = stay.Status.ToString(),
        Total = stay.Total,
        Adults = stay.Adults,
        Children = stay.Children,
        AdvanceDays = stay.AdvanceDays,
    };
}

/// <summary>Stays and spend of a profile on one channel.</summary>
public class CrmChannelTotalDto
{
    public string Channel { get; set; } = string.Empty;

    public int Stays { get; set; }

    public decimal Spend { get; set; }
}

/// <summary>Stays of a profile at one property.</summary>
public class CrmPropertyTotalDto
{
    public Guid PropertyId { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public int Stays { get; set; }
}

/// <summary>The totals of a profile over its stays that happened.</summary>
public class CrmProfileTotalsDto
{
    public int Stays { get; set; }

    public decimal Spend { get; set; }

    public decimal AverageSpend { get; set; }

    /// <summary>The booking advance on average, in days; <c>null</c> when no stay has a known advance.</summary>
    public double? AverageAdvanceDays { get; set; }

    public List<CrmChannelTotalDto> ByChannel { get; set; } = [];

    public List<CrmPropertyTotalDto> ByProperty { get; set; } = [];

    public double AverageAdults { get; set; }

    public double AverageChildren { get; set; }

    /// <summary>How the guest usually comes: <c>Single</c>, <c>Couple</c>, <c>Family</c> or <c>Group</c>; <c>null</c> without a stay.</summary>
    public string? TypicalGroup { get; set; }

    public static CrmProfileTotalsDto From(CrmProfileTotals totals) => new()
    {
        Stays = totals.Stays,
        Spend = totals.Spend,
        AverageSpend = totals.AverageSpend,
        AverageAdvanceDays = totals.AverageAdvanceDays,
        ByChannel = totals.ByChannel
            .Select(channel => new CrmChannelTotalDto { Channel = channel.Channel.ToString(), Stays = channel.Stays, Spend = channel.Spend })
            .ToList(),
        ByProperty = totals.ByProperty
            .Select(property => new CrmPropertyTotalDto { PropertyId = property.PropertyId, PropertyName = property.PropertyName, Stays = property.Stays })
            .ToList(),
        AverageAdults = totals.AverageAdults,
        AverageChildren = totals.AverageChildren,
        TypicalGroup = totals.TypicalGroup?.ToString(),
    };
}

/// <summary>A review in the summary: the mark and when it was published.</summary>
public class CrmReviewEntryDto
{
    public int Rating { get; set; }

    public DateTime PublishedAt { get; set; }
}

/// <summary>The reviews of the stays of a profile.</summary>
public class CrmReviewSummaryDto
{
    public int Count { get; set; }

    /// <summary>The average mark, 1 to 5; <c>null</c> without a review.</summary>
    public double? AverageRating { get; set; }

    /// <summary>The latest reviews, at most five.</summary>
    public List<CrmReviewEntryDto> Latest { get; set; } = [];

    public static CrmReviewSummaryDto From(CrmReviewSummary reviews) => new()
    {
        Count = reviews.Count,
        AverageRating = reviews.AverageRating,
        Latest = reviews.Latest
            .Select(review => new CrmReviewEntryDto { Rating = review.Rating, PublishedAt = review.PublishedAt })
            .ToList(),
    };
}

/// <summary>A contact of a profile, with what the rules of the CRM say of it (NF-89).</summary>
public class CrmContactDto
{
    public Guid Id { get; set; }

    /// <summary><c>Email</c>, <c>Phone</c> or <c>WhatsApp</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Where the contact came from: <c>OtaForward</c>, <c>CheckIn</c>, <c>DirectBooking</c>, <c>Manual</c>, <c>OctorateContactInfo</c>, <c>WelcomePage</c> or <c>GuestPage</c>.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The double opt-in: <c>Unconfirmed</c>, <c>Pending</c> or <c>Confirmed</c>.</summary>
    public string Confirmation { get; set; } = string.Empty;

    /// <summary><c>None</c>, <c>ServiceOnly</c>, <c>SoftOptIn</c> or <c>Consent</c>.</summary>
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>The version of the text the basis rests on; empty when there is none.</summary>
    public string BasisVersion { get; set; } = string.Empty;

    public DateTime? BasisRecordedAt { get; set; }

    public bool IsSuppressed { get; set; }

    /// <summary>The address is one an online travel agency forwards the mail through: it is never written to for marketing.</summary>
    public bool IsOtaForward { get; set; }

    /// <summary>The address or the number, as stored.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The contact may receive marketing.</summary>
    public bool Marketable { get; set; }

    /// <summary>Why it may not, when it may not: <c>suppressed</c>, <c>ota_forward</c>, <c>not_confirmed</c>, <c>no_basis</c> or <c>channel_not_available</c>.</summary>
    public string? ReachabilityReason { get; set; }

    public static CrmContactDto From(CrmContactView contact) => new()
    {
        Id = contact.Id,
        Channel = contact.Channel.ToString(),
        Origin = contact.Origin.ToString(),
        Confirmation = contact.Confirmation.ToString(),
        LegalBasis = contact.LegalBasis.ToString(),
        BasisVersion = contact.BasisVersion,
        BasisRecordedAt = contact.BasisRecordedAt,
        IsSuppressed = contact.IsSuppressed,
        IsOtaForward = contact.IsOtaForward,
        Value = contact.Value,
        Marketable = contact.Marketable,
        ReachabilityReason = contact.ReachabilityReason,
    };
}

/// <summary>A profile in detail (<c>GET api/crm/guests/{id}</c>).</summary>
public class CrmProfileDetailDto
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The guest record the name comes from, so that the screen can open it; <c>null</c> when none the caller reaches has a name.</summary>
    public Guid? PrimaryGuestId { get; set; }

    public string Country { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public DateTime? FirstStayAt { get; set; }

    public DateTime? LastStayAt { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>The bookings the caller reaches, the latest arrival first, at most 200.</summary>
    public List<CrmProfileStayDto> Stays { get; set; } = [];

    /// <summary>There are more bookings than <see cref="Stays"/> holds; the totals count them all.</summary>
    public bool StaysTruncated { get; set; }

    public CrmProfileTotalsDto Totals { get; set; } = new();

    public CrmReviewSummaryDto Reviews { get; set; } = new();

    public List<CrmContactDto> Contacts { get; set; } = [];

    /// <summary>The merge proposals on this profile that the caller may decide.</summary>
    public int MergeProposalCount { get; set; }

    public static CrmProfileDetailDto From(CrmProfileDetail detail) => new()
    {
        Id = detail.Id,
        DisplayName = detail.DisplayName,
        PrimaryGuestId = detail.PrimaryGuestId,
        Country = detail.Country,
        Language = detail.Language,
        FirstStayAt = detail.FirstStayAt,
        LastStayAt = detail.LastStayAt,
        Tags = detail.Tags.ToList(),
        Stays = detail.Stays.Select(CrmProfileStayDto.From).ToList(),
        StaysTruncated = detail.StaysTruncated,
        Totals = CrmProfileTotalsDto.From(detail.Totals),
        Reviews = CrmReviewSummaryDto.From(detail.Reviews),
        Contacts = detail.Contacts.Select(CrmContactDto.From).ToList(),
        MergeProposalCount = detail.MergeProposalCount,
    };
}

/// <summary>The body of <c>PUT api/crm/guests/{id}/tags</c>: the tags the profile has from now on (an empty list clears them).</summary>
public class SetCrmTagsRequest
{
    /// <summary>At most 50 tags of 1 to 40 characters, with no two alike, and no more than 2,000 characters in all. Required.</summary>
    public List<string?>? Tags { get; set; }
}
