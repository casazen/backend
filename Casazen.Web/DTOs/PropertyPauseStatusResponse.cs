namespace Casazen.Web.DTOs;

public sealed record PropertyPauseStatusResponse(bool IsPaused, DateTime? PausedAt);
