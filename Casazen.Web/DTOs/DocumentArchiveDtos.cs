using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// One document of the archive (<c>GET api/documents</c>, NF-99): <c>Type</c> is the key of the kind of document
/// (<see cref="ArchiveDocumentTypes.KeyOf"/>: <c>propertyDocument</c>, <c>contract</c>, <c>touristTax</c>, ...), never a text to show: the
/// client translates it. <c>Date</c> is the Europe/Rome day of the document. The file is fetched with
/// <c>GET api/documents/{type}/{id}/download</c>; no address of the storage is ever in the answer.
/// </summary>
public sealed class ArchiveDocumentDto
{
    public string Type { get; init; } = string.Empty;
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public Guid? PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public Guid? BookingId { get; init; }
    public string? BookingCode { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }

    public static ArchiveDocumentDto From(ArchiveRow row) => new()
    {
        Type = ArchiveDocumentTypes.KeyOf(row.Type),
        Id = row.Id,
        Title = row.Title,
        Date = row.DocumentDate,
        PropertyId = row.PropertyId,
        PropertyName = row.PropertyName,
        BookingId = row.BookingId,
        BookingCode = row.BookingCode,
        ContentType = row.ContentType,
        SizeBytes = row.SizeBytes,
    };
}

public sealed class DocumentArchivePageDto
{
    public IReadOnlyList<ArchiveDocumentDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }

    public static DocumentArchivePageDto From(DocumentArchivePage page) => new()
    {
        Items = [.. page.Items.Select(ArchiveDocumentDto.From)],
        TotalCount = page.TotalCount,
        Page = page.Page,
        PageSize = page.PageSize,
    };
}

public sealed record ArchiveTypeCountDto(string Type, int Count);

public sealed class DocumentArchiveTypesDto
{
    public IReadOnlyList<ArchiveTypeCountDto> Types { get; init; } = [];

    public static DocumentArchiveTypesDto From(IReadOnlyList<ArchiveTypeCount> counts) => new()
    {
        Types = [.. counts.Select(c => new ArchiveTypeCountDto(ArchiveDocumentTypes.KeyOf(c.Type), c.Count))],
    };
}
