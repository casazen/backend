namespace Casazen.Web.DTOs;

/// <summary>
/// Result of <c>POST /properties/{id}/pause</c> and <c>POST /properties/{id}/activate</c> (PC-03, A2-05): the pause
/// state right after the change, so the caller can update its UI without a second round trip.
/// </summary>
public sealed record PropertyPauseStatusResponse(bool IsPaused, DateTime? PausedAt);
