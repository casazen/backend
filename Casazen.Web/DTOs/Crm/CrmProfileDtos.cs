using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Crm;

// The API of the CRM profiles (E07/F01/US03/T01, NF-88). Every enumeration is written by name (the same names as the rest of the API:
// «Direct», «BookingCom», «ServiceOnly»...). None of these types has a document, a birth date, a place of birth, a stay guest or an
// Alloggiati field, and none ever will (AC-NF88-6): the proof reads these types by reflection.

public class CrmProfileListItemDto
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public int TotalStays { get; set; }

    public decimal TotalSpend { get; set; }

    public DateTime? FirstStayAt { get; set; }

    public DateTime? LastStayAt { get; set; }

    public List<string> Channels { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public string EmailMasked { get; set; } = string.Empty;

    public string EmailBasis { get; set; } = string.Empty;

    public bool EmailReachable { get; set; }

    public string WhatsappBasis { get; set; } = string.Empty;

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

public class CrmProfileStayDto
{
    public Guid BookingId { get; set; }

    public Guid PropertyId { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public DateTime CheckInDate { get; set; }

    public DateTime CheckOutDate { get; set; }

    public int Nights { get; set; }

    public string Channel { get; set; } = string.Empty;

    /// <summary>The name of the channel for a source with no value of its own (<c>OtherChannel</c>) or a calendar feed; <c>null</c> otherwise.</summary>
    public string? ChannelLabel { get; set; }

    public string Status { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public int Adults { get; set; }

    public int Children { get; set; }

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

public class CrmChannelTotalDto
{
    public string Channel { get; set; } = string.Empty;

    public int Stays { get; set; }

    public decimal Spend { get; set; }
}

public class CrmPropertyTotalDto
{
    public Guid PropertyId { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public int Stays { get; set; }
}

public class CrmProfileTotalsDto
{
    public int Stays { get; set; }

    public decimal Spend { get; set; }

    public decimal AverageSpend { get; set; }

    public double? AverageAdvanceDays { get; set; }

    public List<CrmChannelTotalDto> ByChannel { get; set; } = [];

    public List<CrmPropertyTotalDto> ByProperty { get; set; } = [];

    public double AverageAdults { get; set; }

    public double AverageChildren { get; set; }

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

public class CrmReviewEntryDto
{
    public int Rating { get; set; }

    public DateTime PublishedAt { get; set; }
}

public class CrmReviewSummaryDto
{
    public int Count { get; set; }

    public double? AverageRating { get; set; }

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

public class CrmContactDto
{
    public Guid Id { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string Confirmation { get; set; } = string.Empty;

    public string LegalBasis { get; set; } = string.Empty;

    public string BasisVersion { get; set; } = string.Empty;

    public DateTime? BasisRecordedAt { get; set; }

    public bool IsSuppressed { get; set; }

    public bool IsOtaForward { get; set; }

    public string Value { get; set; } = string.Empty;

    public bool Marketable { get; set; }

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

    public List<CrmProfileStayDto> Stays { get; set; } = [];

    public bool StaysTruncated { get; set; }

    public CrmProfileTotalsDto Totals { get; set; } = new();

    public CrmReviewSummaryDto Reviews { get; set; } = new();

    public List<CrmContactDto> Contacts { get; set; } = [];

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

public class SetCrmTagsRequest
{
    public List<string?>? Tags { get; set; }
}
