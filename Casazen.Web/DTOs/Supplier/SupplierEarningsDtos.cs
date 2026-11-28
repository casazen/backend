using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SupplierEarningsResponse
{
    public IEnumerable<SupplierEarningDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public SupplierEarningsSummaryDto Summary { get; set; } = new();
}

public class SupplierEarningDto
{
    public Guid RequestId { get; set; }

    public string? ServiceName { get; set; }

    public string Category { get; set; } = string.Empty;

    public Guid ClientId { get; set; }

    public string? ClientLabel { get; set; }

    public bool ClientAnonymized { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string State { get; set; } = string.Empty;

    public string PaymentMode { get; set; } = string.Empty;

    public int? GrossCents { get; set; }

    public decimal? CommissionPercent { get; set; }

    public int? CommissionCents { get; set; }

    public int? NetCents { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? PaidVia { get; set; }

    public int RefundedCents { get; set; }

    public DateTime? LateSince { get; set; }
}

public class SupplierEarningsSummaryDto
{
    public long ToCollectNetCents { get; set; }

    public int ToCollectJobs { get; set; }

    public long PaidThisMonthNetCents { get; set; }

    public long CommissionThisMonthCents { get; set; }

    public int LateCount { get; set; }

    public IEnumerable<SupplierEarningsMonthDto> Series { get; set; } = [];

    public SupplierEarningsCommissionDto Commission { get; set; } = new();

    public bool OnlinePayments { get; set; }
}

public class SupplierEarningsMonthDto
{
    public string Month { get; set; } = string.Empty;

    public long NetCents { get; set; }
}

public class SupplierEarningsCommissionDto
{
    public decimal Percent { get; set; }

    public DateTime? FreeUntil { get; set; }
}

public static class SupplierEarningsMapper
{
    public static SupplierEarningsResponse ToResponse(SupplierEarningsPage page, int pageNumber, int pageSize) => new()
    {
        Items = page.Items.Select(ToDto).ToList(),
        Total = page.Total,
        Page = pageNumber,
        PageSize = pageSize,
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        Summary = new SupplierEarningsSummaryDto
        {
            ToCollectNetCents = page.Totals.ToCollect.Cents,
            ToCollectJobs = page.Totals.ToCollect.Jobs,
            PaidThisMonthNetCents = page.Totals.PaidThisMonthNetCents,
            CommissionThisMonthCents = page.Totals.CommissionThisMonthCents,
            LateCount = page.Totals.LateCount,
            Series = page.Totals.Series.Select(month => new SupplierEarningsMonthDto { Month = month.Month, NetCents = month.NetCents }).ToList(),
            Commission = new SupplierEarningsCommissionDto { Percent = page.Totals.Commission.Percent, FreeUntil = page.Totals.Commission.FreeUntil },
            OnlinePayments = page.Totals.OnlinePayments,
        },
    };

    private static SupplierEarningDto ToDto(SupplierEarningRow row) => new()
    {
        RequestId = row.RequestId,
        ServiceName = row.ServiceName,
        Category = row.Category,
        ClientId = row.ClientId,
        ClientLabel = row.ClientLabel,
        ClientAnonymized = row.ClientAnonymized,
        CompletedAt = row.CompletedAt,
        State = row.State,
        PaymentMode = row.PaymentMode.ToString(),
        GrossCents = row.GrossCents,
        CommissionPercent = row.CommissionPercent,
        CommissionCents = row.CommissionCents,
        NetCents = row.NetCents,
        PaidAt = row.PaidAt,
        PaidVia = row.PaidVia?.ToString(),
        RefundedCents = row.RefundedCents,
        LateSince = row.LateSince,
    };
}
