using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.ServiceRequests;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// A service request in the supplier console (SU-08, A4-14; SP-04): <c>GET /api/supplier/inbox</c> items. What the supplier sees
/// before and after the take is in <see cref="SupplierJobDisclosure"/> (decision D9: before the take only the comune, the postal
/// code, the day and time, the price and who asks; the name of the property, the host's notes, the street address and the
/// contact come with the take); the guest of the stay is never included.
/// </summary>
public class SupplierServiceRequestDto
{
    public Guid Id { get; set; }

    public string RentalContext { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;

    public string? Notes { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? TakenAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public Guid? PropertyId { get; set; }

    public string? PropertyName { get; set; }

    public string City { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    public string? Address { get; set; }

    public string? Floor { get; set; }

    public string? AccessNotes { get; set; }

    public DateOnly? ScheduledFor { get; set; }

    public SupplierStayDto? Stay { get; set; }

    public bool ContactDisclosed { get; set; }

    public SupplierHostContactDto? HostContact { get; set; }

    public string Source { get; set; } = SupplierRequestSources.CasaZen;

    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public Guid? ServiceListingId { get; set; }

    public string? ServiceName { get; set; }

    public DateTime? ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    /// <summary>The instant the supplier has to answer by, while the request is new (<c>null</c> otherwise and for older requests).</summary>
    public DateTime? RespondBy { get; set; }

    public DateTime? StartedAt { get; set; }

    public ServiceRequestPriceDto Price { get; set; } = new();

    public ServiceRequestProposalDto? Proposal { get; set; }

    public ServiceRequestQuoteDto? Quote { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancellationReason { get; set; }

    public string? CompletionNotes { get; set; }

    public IEnumerable<ServiceRequestWorkPhotoDto> WorkPhotos { get; set; } = [];

    public string PaymentMode { get; set; } = nameof(Casazen.Core.Entities.Enums.ServiceRequestPaymentMode.Manual);

    public bool AutoAccepted { get; set; }

    public string Kind { get; set; } = nameof(Casazen.Core.Entities.Enums.ServiceRequestKind.Standard);

    public string Phase { get; set; } = SupplierRequestPhases.Unknown;

    /// <summary>
    /// The payment of a completed job (see <see cref="SupplierRequestPaymentDto"/>): <b>null</b> unless the request has a payment record or is
    /// <c>Completato</c>. The same object in the inbox, in <c>today</c> and in the detail.
    /// </summary>
    public SupplierRequestPaymentDto? Payment { get; set; }

    /// <summary>
    /// SP-BKP: where the request stands in its group of backup suppliers, <b>null</b> for a request sent to this supplier alone. A request the
    /// supplier receives <b>as a backup</b> is declared as such here (<c>role: backup</c>): the job is its only if the primary supplier does
    /// not take it by <c>primaryDueAt</c>; until then it can accept as a backup (<c>POST api/service-requests/{id}/accept-as-backup</c>) or
    /// refuse. Like any request before the take it shows the comune, the time and the price, never the property or the host's notes.
    /// </summary>
    public SupplierBackupDto? Backup { get; set; }
}

/// <summary>
/// The backup side of a request for its supplier (SP-BKP). <c>role</c>: <c>primary</c> (the host chose this supplier, which has until
/// <c>primaryDueAt</c> for the priority) or <c>backup</c>. <c>state</c> (an open set; show a generic wording for a value you do not know):
/// for a backup <c>waiting</c> (the primary has its window), <c>accepted</c> (it said it would take the job), <c>active</c> (the second window:
/// it can take the job now, until <c>backupDueAt</c>, and the first to do so has it), <c>won</c> (it took the job in place of the primary) or
/// <c>closed</c>; for the primary <c>priority</c> (within its window), <c>late</c> (the window passed: still in the race without the priority).
/// </summary>
public class SupplierBackupDto
{
    public string Role { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime PrimaryDueAt { get; set; }
    public DateTime? BackupDueAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public class SupplierServiceRequestDetailDto : SupplierServiceRequestDto
{
    public IEnumerable<ServiceRequestHistoryEntryDto> History { get; set; } = [];
}

public class SupplierStayDto
{
    public Guid BookingId { get; set; }

    public DateOnly CheckIn { get; set; }

    public DateOnly CheckOut { get; set; }
}

public class SupplierHostContactDto
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }
}

public class ServiceRequestHistoryEntryDto
{
    public string Status { get; set; } = string.Empty;

    public DateTime At { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string? ActorName { get; set; }

    public string? Reason { get; set; }

    public string? Event { get; set; }

    internal static ServiceRequestHistoryEntryDto From(ServiceRequestHistoryEntry entry) => new()
    {
        Status = entry.Status.ToString(),
        At = entry.At,
        Actor = entry.Actor.ToString(),
        ActorName = entry.ActorName,
        Reason = entry.Reason,
        Event = entry.Event,
    };
}

public class SupplierTodayDto
{
    public DateOnly Date { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public IEnumerable<SupplierServiceRequestDto> Jobs { get; set; } = [];

    public IEnumerable<SupplierServiceRequestDto> NewRequests { get; set; } = [];

    public int NewRequestsTotal { get; set; }

    public SupplierTodayEarningsDto Earnings { get; set; } = new();

    public int? AverageResponseMinutes { get; set; }

    public int Days { get; set; } = 1;

    public DateOnly DateTo { get; set; }

    public int JobsTotal { get; set; }
}

public class SupplierTodayEarningsDto
{
    public DateOnly MonthFrom { get; set; }

    public DateOnly MonthTo { get; set; }

    public long MonthAmountCents { get; set; }

    public int MonthJobs { get; set; }

    public long ToCollectAmountCents { get; set; }

    public int ToCollectJobs { get; set; }

    public bool Estimated { get; set; }
}

public class SupplierChecklistDto
{
    public int ProfileCompletionPercent { get; set; }

    public bool ProfileComplete { get; set; }

    public int ActiveServices { get; set; }

    public bool HoursConfigured { get; set; }

    public DateTime? HoursConfiguredAt { get; set; }

    public bool ShowcasePublished { get; set; }

    /// <summary>The supplier answered a request at least once (took one or rejected one).</summary>
    public bool FirstRequestAnswered { get; set; }

    /// <summary>
    /// Whether the supplier receives payments through CasaZen (SP-16a): <c>true</c> when its Stripe account takes charges and payouts,
    /// <c>false</c> when it does not, <b>only while the flag <c>SupplierOnlinePayments</c> is on</b>. <c>null</c> (flag off) means
    /// "not available", not "no": the console does not count the step.
    /// </summary>
    public bool? PaymentsActive { get; set; }

    public SupplierVerificationDto? Verification { get; set; }
}

/// <summary>
/// The "Verificato" badge of the supplier (decision D11, SP-16a): an active profile, a Stripe account that takes charges <b>and</b> payouts,
/// and a VAT number. No documents (SP-D2).
/// </summary>
public class SupplierVerificationDto
{
    public bool Verified { get; set; }

    public IReadOnlyList<string> Missing { get; set; } = [];
}

public class AcceptServiceRequestsRequest
{
    [Required(ErrorMessage = "ServiceRequestIdsRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestIdsRequired")]
    [MaxLength(ServiceRequestLimits.MaxBatchAccept, ErrorMessage = "ServiceRequestIdsTooMany")]
    public List<Guid>? Ids { get; set; }
}

public class AcceptServiceRequestsResponse
{
    public IEnumerable<AcceptServiceRequestResultDto> Results { get; set; } = [];

    public int Accepted { get; set; }

    public int Failed { get; set; }
}

public class AcceptServiceRequestResultDto
{
    public Guid Id { get; set; }

    public bool Accepted { get; set; }

    public string? Status { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }
}

public static class SupplierServiceRequestMapper
{
    public static SupplierServiceRequestDto ToDto(SupplierServiceRequestView view) =>
        Fill(new SupplierServiceRequestDto(), view);

    public static SupplierServiceRequestDetailDto ToDetailDto(SupplierServiceRequestView view)
    {
        var dto = Fill(new SupplierServiceRequestDetailDto(), view);
        dto.History = (view.History ?? []).Select(h => ServiceRequestHistoryEntryDto.From(h)).ToList();
        return dto;
    }

    public static SupplierTodayDto ToTodayDto(SupplierToday today) => new()
    {
        Date = today.Date,
        Days = today.Days,
        DateTo = today.LastDate,
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        Jobs = today.Jobs.Select(view => ToDto(view)).ToList(),
        JobsTotal = today.JobsTotal,
        NewRequests = today.NewRequests.Select(view => ToDto(view)).ToList(),
        NewRequestsTotal = today.NewRequestsTotal,
        Earnings = new SupplierTodayEarningsDto
        {
            MonthFrom = today.Earnings.MonthFrom,
            MonthTo = today.Earnings.MonthTo,
            MonthAmountCents = today.Earnings.MonthAmountCents,
            MonthJobs = today.Earnings.MonthJobs,
            ToCollectAmountCents = today.Earnings.ToCollectAmountCents,
            ToCollectJobs = today.Earnings.ToCollectJobs,
            Estimated = today.Earnings.IsEstimate,
        },
        AverageResponseMinutes = today.Earnings.AverageResponseMinutes,
    };

    public static SupplierChecklistDto ToChecklistDto(SupplierChecklist checklist) => new()
    {
        ProfileCompletionPercent = checklist.ProfileCompletionPercent,
        ProfileComplete = checklist.ProfileComplete,
        ActiveServices = checklist.ActiveServices,
        HoursConfigured = checklist.HoursConfigured,
        HoursConfiguredAt = checklist.HoursConfiguredAt,
        ShowcasePublished = checklist.ShowcasePublished,
        FirstRequestAnswered = checklist.FirstRequestAnswered,
        PaymentsActive = checklist.PaymentsActive,
        Verification = checklist.Verification is null
            ? null
            : new SupplierVerificationDto { Verified = checklist.Verification.Verified, Missing = checklist.Verification.Missing },
    };

    private static T Fill<T>(T dto, SupplierServiceRequestView view)
        where T : SupplierServiceRequestDto
    {
        dto.Id = view.Id;
        dto.RentalContext = view.RentalContext.ToString();
        dto.Status = view.Status.ToString();
        dto.Category = view.Category;
        dto.Urgency = view.Urgency.ToString();
        dto.Notes = view.Notes;
        dto.RejectionReason = view.RejectionReason;
        dto.CreatedAt = view.CreatedAt;
        dto.UpdatedAt = view.UpdatedAt;
        dto.TakenAt = view.TakenAt;
        dto.CompletedAt = view.CompletedAt;
        dto.PaidAt = view.PaidAt;
        dto.PropertyId = view.Location.PropertyId;
        dto.PropertyName = view.Location.PropertyName;
        dto.City = view.Location.City;
        dto.PostalCode = view.Location.PostalCode;
        dto.Address = view.Location.Address;
        dto.Floor = view.Location.Floor;
        dto.AccessNotes = view.Location.AccessNotes;
        dto.ScheduledFor = view.ScheduledFor;
        dto.Stay = view.Stay is null
            ? null
            : new SupplierStayDto { BookingId = view.Stay.BookingId, CheckIn = view.Stay.CheckIn, CheckOut = view.Stay.CheckOut };
        dto.ContactDisclosed = view.ContactDisclosed;
        dto.HostContact = view.HostContact is null
            ? null
            : new SupplierHostContactDto
            {
                Name = view.HostContact.Name,
                Email = view.HostContact.Email,
                Phone = view.HostContact.Phone,
            };

        dto.Source = view.Source;
        dto.ClientId = view.Client.Id;
        dto.ClientName = view.Client.Name;
        dto.ServiceListingId = view.ServiceListingId;
        dto.ServiceName = view.ServiceName;
        dto.ScheduledStart = view.Schedule.StartUtc;
        dto.ScheduledEnd = view.Schedule.EndUtc;
        dto.RespondBy = view.Schedule.RespondBy;
        dto.StartedAt = view.Schedule.StartedAt;
        dto.Price = ServiceRequestDtoParts.ToPriceDto(
            view.Price.EstimatedAmountCents,
            view.Price.QuotedAmountCents,
            view.Price.FinalAmountCents,
            view.Price.FinalAmountNeedsConfirmation,
            view.Price.Lines);
        dto.Proposal = view.Proposal is null
            ? null
            : new ServiceRequestProposalDto
            {
                StartUtc = view.Proposal.StartUtc,
                EndUtc = view.Proposal.EndUtc,
                ProposedAt = view.Proposal.ProposedAt,
                Message = view.Proposal.Message,
            };
        dto.Quote = ServiceRequestDtoParts.ToQuoteDto(view.Quote);
        dto.CancelledAt = view.Cancellation?.At;
        dto.CancelledBy = view.Cancellation?.By.ToString();
        dto.CancellationReason = view.Cancellation?.Reason;
        dto.CompletionNotes = view.CompletionNotes;
        dto.WorkPhotos = ServiceRequestDtoParts.ToPhotoDtos(view.Id, view.WorkPhotos, ServiceRequestDtoParts.ShortRentBasePath);
        dto.PaymentMode = view.PaymentMode.ToString();
        dto.AutoAccepted = view.AutoAccepted;
        dto.Kind = view.Kind.ToString();
        dto.Phase = view.Phase;
        dto.Payment = view.Payment is null ? null : SupplierRequestPaymentDto.From(view.Payment);
        dto.Backup = view.Backup is null
            ? null
            : new SupplierBackupDto
            {
                Role = view.Backup.Role,
                State = view.Backup.State,
                PrimaryDueAt = view.Backup.PrimaryDueAt,
                BackupDueAt = view.Backup.BackupDueAt,
                AcceptedAt = view.Backup.AcceptedAt,
            };
        return dto;
    }
}

/// <summary>
/// Where the payment of a completed job stands and what the supplier may do about it (SP-04e). It exists when the request has a payment
/// record or is <c>Completato</c>; a completed job paid outside CasaZen has no record yet (<see cref="Id"/>, <see cref="Status"/> and the
/// commission fields are then null and <see cref="AmountCents"/> is the total the supplier declared). The commission is the supplier's own
/// figure: the payer never sees it. No text may promise a payout date.
/// </summary>
public class SupplierRequestPaymentDto
{
    public Guid? Id { get; set; }

    public string? Status { get; set; }

    public int? AmountCents { get; set; }

    public decimal? CommissionPercent { get; set; }

    public int? CommissionCents { get; set; }

    public int? NetCents { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? PaidVia { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? LastSentAt { get; set; }

    public int SentCount { get; set; }

    public bool Late { get; set; }

    public bool NeedsAmountConfirmation { get; set; }

    public bool CanRequest { get; set; }

    public bool CanRecordOffline { get; set; }

    public HostServiceDocumentPlan? HostDocuments { get; set; }

    internal static SupplierRequestPaymentDto From(SupplierJobPayment payment) => new()
    {
        Id = payment.Id,
        Status = payment.Status?.ToString(),
        AmountCents = payment.AmountCents,
        CommissionPercent = payment.CommissionPercent,
        CommissionCents = payment.CommissionCents,
        NetCents = payment.NetCents,
        PaidAt = payment.PaidAt,
        PaidVia = payment.PaidVia?.ToString(),
        RequestedAt = payment.RequestedAt,
        LastSentAt = payment.LastSentAt,
        SentCount = payment.SentCount,
        Late = payment.Late,
        NeedsAmountConfirmation = payment.NeedsAmountConfirmation,
        CanRequest = payment.CanRequest,
        CanRecordOffline = payment.CanRecordOffline,
        HostDocuments = payment.HostDocuments,
    };
}

public class SupplierOverviewDto
{
    public string Period { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public bool OnlinePayments { get; set; }

    public SupplierOverviewPeriodDto Current { get; set; } = new();

    public SupplierOverviewPeriodDto Previous { get; set; } = new();
}

public class SupplierOverviewPeriodDto
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public int Received { get; set; }

    public int Accepted { get; set; }

    public int Rejected { get; set; }

    public double? AcceptanceRate { get; set; }

    public int? AverageResponseMinutes { get; set; }

    public int CompletedJobs { get; set; }

    /// <summary>The net, in cents, of the payments paid in the period (by payment date); 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long PaidNetCents { get; set; }

    /// <summary>The gross, in cents, of the payments paid in the period; 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long PaidGrossCents { get; set; }

    /// <summary>The commission CasaZen kept on them, in cents; 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long CommissionCents { get; set; }

    public long? ToCollectCents { get; set; }

    public int? ToCollectJobs { get; set; }
}

public class SupplierInboxCountsDto
{
    public int ToAnswer { get; set; }

    public int AwaitingCustomer { get; set; }

    public int Scheduled { get; set; }

    public int ToCollect { get; set; }

    public int Archive { get; set; }

    public int PaymentsLate { get; set; }
}

public static class SupplierReadModelsMapper
{
    public static SupplierOverviewDto ToDto(SupplierOverview overview) => new()
    {
        Period = SupplierKpiPeriods.OverviewToken(overview.Period),
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        OnlinePayments = overview.OnlinePayments,
        Current = ToDto(overview.Current),
        Previous = ToDto(overview.Previous),
    };

    public static SupplierInboxCountsDto ToDto(SupplierInboxCounts counts) => new()
    {
        ToAnswer = counts.ToAnswer,
        AwaitingCustomer = counts.AwaitingCustomer,
        Scheduled = counts.Scheduled,
        ToCollect = counts.ToCollect,
        Archive = counts.Archive,
        PaymentsLate = counts.PaymentsLate,
    };

    private static SupplierOverviewPeriodDto ToDto(SupplierOverviewPeriod period) => new()
    {
        From = period.From,
        To = period.To,
        Received = period.Received,
        Accepted = period.Accepted,
        Rejected = period.Rejected,
        AcceptanceRate = period.AcceptanceRate,
        AverageResponseMinutes = period.AverageResponseMinutes,
        CompletedJobs = period.CompletedJobs,
        PaidNetCents = period.PaidNetCents,
        PaidGrossCents = period.PaidGrossCents,
        CommissionCents = period.CommissionCents,
        ToCollectCents = period.ToCollectCents,
        ToCollectJobs = period.ToCollectJobs,
    };
}
