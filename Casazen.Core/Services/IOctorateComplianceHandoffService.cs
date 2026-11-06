using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>The compliance outcome the host goes to check in the connected area of Octorate (CHK-15).</summary>
public enum OctorateHandoffTopic
{
    /// <summary>The Alloggiati Web communication of the guests of a stay.</summary>
    Alloggiati = 1,

    /// <summary>The ISTAT communication of a period.</summary>
    Istat = 2,
}

/// <summary>What the address of a handoff is.</summary>
public enum OctorateHandoffDestinationKind
{
    /// <summary>The authenticated home of the Octorate panel: the guide describes the menu steps.</summary>
    Home = 1,

    /// <summary>A deep link of the panel for the topic, configured and verified by a person.</summary>
    DeepLink = 2,
}

/// <summary>The one address a handoff opens: always read from the configuration and checked against the allowlist, never from the caller.</summary>
/// <param name="Url">Absolute https address, no credentials, on an allowlisted host.</param>
/// <param name="Kind">Whether it is the home of the panel or a verified deep link.</param>
public sealed record OctorateHandoffDestination(string Url, OctorateHandoffDestinationKind Kind);

/// <summary>What the host asks for (<c>POST api/compliance/octorate-handoffs</c>). There is no address in it: CasaZen alone decides where the host goes.</summary>
/// <param name="Topic">Alloggiati or ISTAT.</param>
/// <param name="PropertyId">The property the check is about.</param>
/// <param name="BookingId">The stay, when the check starts from a booking; it must belong to <paramref name="PropertyId"/>.</param>
/// <param name="Period"><c>yyyy-MM</c> or <c>yyyy-MM-dd</c>, when the check is about a period (<see cref="OctorateHandoffInput.TryNormalizePeriod"/>).</param>
/// <param name="ReturnPath">The relative CasaZen route the host comes from (<see cref="OctorateHandoffInput.TryNormalizeReturnPath"/>).</param>
public sealed record OctorateHandoffRequest(
    OctorateHandoffTopic Topic,
    Guid PropertyId,
    Guid? BookingId,
    string? Period,
    string? ReturnPath);

/// <summary>
/// A handoff prepared for the host: where to go, what to look at and for how long the client may keep its context. Ids, codes and the
/// names of the host's own property and listing only: no guest data, no credential, no outcome.
/// </summary>
/// <param name="HandoffId">Opaque, random, valid until <paramref name="ExpiresAt"/>; the client keeps it with the saved route, the activity log keeps it with the line.</param>
/// <param name="StartedAt">When the handoff was prepared (UTC).</param>
/// <param name="ExpiresAt">Until when the client may restore the saved context (UTC).</param>
/// <param name="Topic">Alloggiati or ISTAT.</param>
/// <param name="Destination">The only address the client may open, in the current tab.</param>
/// <param name="ReturnPath">The route the host comes from, as checked.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="OctorateListingName">The name of the property's listing on Octorate (the confirmed mapping), when known.</param>
/// <param name="BookingId">The stay, when there is one.</param>
/// <param name="BookingCode">The reference of the stay the host knows (<c>XXXXX-XXXXX</c>), not the id.</param>
/// <param name="Period">The period of the check, when there is one.</param>
/// <param name="GuideCode">Which guide describes the menu and the check (<see cref="OctorateHandoffGuides"/>).</param>
public sealed record OctorateHandoff(
    string HandoffId,
    DateTime StartedAt,
    DateTime ExpiresAt,
    OctorateHandoffTopic Topic,
    OctorateHandoffDestination Destination,
    string ReturnPath,
    Guid PropertyId,
    string PropertyName,
    string? OctorateListingName,
    Guid? BookingId,
    string? BookingCode,
    string? Period,
    string GuideCode);

/// <summary>
/// The controlled passage of a host from CasaZen to the area of Octorate that holds an Alloggiati or ISTAT outcome (CHK-15, E03/F05).
/// It prepares the destination and the guide and records that the passage started. It never signs in to Octorate, never reads an
/// outcome, never stores a credential, a cookie or a guest, and changes no compliance state or evidence: there is no completion or
/// result operation, and the return is the browser's back or a normal CasaZen navigation (no callback is assumed).
/// </summary>
public interface IOctorateComplianceHandoffService
{
    /// <summary>
    /// Prepares the handoff and writes one line in the activity log of the org (<c>OctorateComplianceHandoffStarted</c>) in the same save,
    /// nothing else. The property, and the booking when given, must be in the org and in the reach of <paramref name="scope"/> and the
    /// booking must belong to the property: otherwise <see cref="Exceptions.NotFoundException"/> (the same answer for everything that is
    /// not the caller's). The property needs a confirmed mapping to an Octorate listing, and a destination must be configured.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><c>octorate_handoff_return_path_invalid</c> or <c>octorate_handoff_period_invalid</c> (422).</exception>
    /// <exception cref="Exceptions.NotFoundException"><c>octorate_handoff_target_not_found</c> (404).</exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>octorate_handoff_property_not_mapped</c> or <c>octorate_handoff_destination_unavailable</c> (409).</exception>
    Task<OctorateHandoff> StartAsync(
        HostScope scope,
        string actorUserId,
        OctorateHandoffRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The guides of the handoff, by stable code. The client shows the texts of the code (the API returns them localized): the menu path in
/// the panel and what to check. The stay length picks the Alloggiati guide as CHK-15 asks: up to 24 hours (a stay of at most one night, the
/// rule of <see cref="AlloggiatiTerms.IsShortStay"/>) the immediate manual send; over 30 nights the file path and the Alloggiati portal;
/// otherwise the daily send of the panel.
/// </summary>
public static class OctorateHandoffGuides
{
    /// <summary>Alloggiati, the ordinary stay: the daily send of the panel, to check.</summary>
    public const string AlloggiatiDaily = "alloggiati_daily";

    /// <summary>Alloggiati, a stay of up to 24 hours: the term is 6 hours, so the manual send of the panel.</summary>
    public const string AlloggiatiShortStay = "alloggiati_short_stay";

    /// <summary>Alloggiati, a stay of more than 30 nights: the panel does not send it, the file goes to the Alloggiati portal.</summary>
    public const string AlloggiatiLongStay = "alloggiati_long_stay";

    /// <summary>ISTAT: the send of the period, per region, to check.</summary>
    public const string IstatPeriod = "istat_period";

    /// <summary>Every guide code.</summary>
    public static IReadOnlyList<string> All { get; } = [AlloggiatiDaily, AlloggiatiShortStay, AlloggiatiLongStay, IstatPeriod];

    /// <summary>The guide of a topic and, for Alloggiati, of the stay the check starts from (none: the ordinary one).</summary>
    public static string For(OctorateHandoffTopic topic, DateTime? checkInDate, DateTime? checkOutDate)
    {
        if (topic == OctorateHandoffTopic.Istat)
            return IstatPeriod;

        if (checkInDate is not { } checkIn || checkOutDate is not { } checkOut)
            return AlloggiatiDaily;

        if (AlloggiatiTerms.StayDays(checkIn, checkOut) > AlloggiatiTerms.MaxStayDaysPerSchedina)
            return AlloggiatiLongStay;

        return AlloggiatiTerms.IsShortStay(checkIn, checkOut) ? AlloggiatiShortStay : AlloggiatiDaily;
    }
}

/// <summary>
/// The two strings of a handoff request the client writes, checked in one place. The route is the one CasaZen string that looks like
/// an address, so it is held to be a relative path of CasaZen and never anything that a browser could read as another site.
/// </summary>
public static class OctorateHandoffInput
{
    /// <summary>The longest route (path and query) accepted.</summary>
    public const int MaxReturnPathLength = 1024;

    /// <summary>The longest period (<c>yyyy-MM-dd</c>).</summary>
    public const int MaxPeriodLength = 10;

    private const int FirstPeriodYear = 2000;
    private const int LastPeriodYear = 2100;

    /// <summary>
    /// True when <paramref name="value"/> is a relative CasaZen route: it starts with a single <c>/</c>, holds no backslash, no space or
    /// control character and no <c>://</c>, and is at most <see cref="MaxReturnPathLength"/> long. An address with a scheme or a host
    /// (<c>https://...</c>, <c>//host</c>, <c>/\host</c>, <c>javascript:...</c>) is never a route. <paramref name="path"/> is the trimmed route.
    /// </summary>
    public static bool TryNormalizeReturnPath(string? value, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.Length > MaxReturnPathLength || candidate[0] != '/')
            return false;

        if (candidate.Length > 1 && candidate[1] is '/' or '\\')
            return false;

        foreach (var c in candidate)
        {
            if (c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        }

        if (candidate.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(candidate, UriKind.Relative, out _))
            return false;

        path = candidate;
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> is empty (no period: <paramref name="period"/> is <c>null</c>) or a month <c>yyyy-MM</c> or a day
    /// <c>yyyy-MM-dd</c> of a real date between 2000 and 2100, written with ASCII digits.
    /// </summary>
    public static bool TryNormalizePeriod(string? value, out string? period)
    {
        period = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var candidate = value.Trim();
        if (candidate.Length is not (7 or 10))
            return false;

        for (var i = 0; i < candidate.Length; i++)
        {
            var isDash = i is 4 or 7;
            if (isDash ? candidate[i] != '-' : !char.IsAsciiDigit(candidate[i]))
                return false;
        }

        var year = int.Parse(candidate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(candidate.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (year is < FirstPeriodYear or > LastPeriodYear || month is < 1 or > 12)
            return false;

        if (candidate.Length == 10)
        {
            var day = int.Parse(candidate.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
                return false;
        }

        period = candidate;
        return true;
    }
}
