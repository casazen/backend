namespace Casazen.Web.DTOs;

public class PropertyIcalImportUrlRequest
{
    public string ImportUrl { get; set; } = string.Empty;
}

public class PropertyIcalStatusDto
{
    public string? ImportUrl { get; set; }
    public string ExportUrl { get; set; } = string.Empty;
    public DateTime? LastImportAt { get; set; }
    public string? LastImportStatus { get; set; }
    public string? LastError { get; set; }
    public int BlockCount { get; set; }
}

public class PropertyIcalExportUrlDto
{
    public string ExportUrl { get; set; } = string.Empty;
}

public class CalendarItemDto
{
    public string Type { get; set; } = "booking";
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    public string? Status { get; set; }
    public string? Source { get; set; }
    public int? NumberOfGuests { get; set; }
    public decimal? TotalPrice { get; set; }
    public string? GuestName { get; set; }
    public string? Summary { get; set; }
}
