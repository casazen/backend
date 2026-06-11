namespace Casazen.Web.DTOs;

public record SeoComuneRegistryDto(string Code, string Name, string RegionSlug, string ComuneSlug);

public record SeoBulkApproveRequestDto(bool CounselApproved = true);

public record SeoBulkApproveResultDto(int ApprovedCount);
