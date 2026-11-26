using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalChatController(
    AppDbContext db,
    IGuestMessageService messages,
    IFeatureFlags flags,
    GuestHouseGuideStorage guides,
    IAssistantRulesService assistantRules,
    IAssistantSituationService situations,
    TimeProvider clock) : GuestPortalControllerBase
{
    [HttpGet("chat")]
    public async Task<ActionResult<GuestThreadDto>> Get(CancellationToken ct)
    {
        var booking = await BookingAsync(ct);
        return booking is null ? LinkInvalid() : GuestThreadDto.From(await messages.GetForGuestAsync(booking, ct));
    }

    [HttpPost("chat")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<ActionResult<SentGuestMessageDto>> Send(
        SendPortalMessageRequest request, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var booking = await BookingAsync(ct);
        if (booking is null)
            return LinkInvalid();

        var (answer, draft) = await GuideAnswerAsync(booking, request, ct);
        return SentGuestMessageDto.From(await messages.SendFromPortalAsync(booking, request.Text, key, answer, ct, draft));
    }

    private async Task<(string? Answer, string? Draft)> GuideAnswerAsync(Booking booking, SendPortalMessageRequest request, CancellationToken ct)
    {
        if (request.RequestHost || !flags.IsEnabled(FeatureFlags.GuestPortalAutoReplies))
            return default;

        var language = GuestLanguages.Normalize(request.Language) ?? Portal.Language;
        var guide = guides.Read(booking.PropertyId, booking.Property.GuestHouseGuideJson);
        var answer = GuestPortalQuickReplies.Answer(guide, request.Text, language, Portal.CanSeeReserved);

        var rules = await assistantRules.ResolveEffectiveAsync(booking.OrgId, booking.PropertyId, ct);
        if (!rules.Enabled || !rules.Languages.Contains(language) || guide.AutoReplies?.Enabled != true)
            return default;
        if (answer is null)
            return (null, GuestPortalQuickReplies.Acknowledgment(language));
        if (AssistantTopicNames.IsListed(rules.ForbiddenTopics, answer.Situation, answer.Topic)
            || (rules.AllowedTopics.Count > 0 && !AssistantTopicNames.IsListed(rules.AllowedTopics, answer.Situation, answer.Topic)))
            return default;

        if (rules.HoursFrom is { } from && rules.HoursTo is { } to
            && (!TimeZoneInfo.TryFindSystemTimeZoneById(booking.Property.Timezone, out var zone)
                || !TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime).IsBetween(from, to)))
            return default;

        var situation = await situations.ResolveAsync(booking.OrgId, booking.PropertyId, answer.Situation, ct);
        return situation.Mode switch
        {
            AssistantMode.Draft => (null, answer.ReviewedText),
            AssistantMode.Automatic or AssistantMode.AutomaticWithExceptions => (answer.Text, null),
            _ => default,
        };
    }

    private Task<Booking?> BookingAsync(CancellationToken ct)
    {
        var portal = Portal;
        return db.Bookings.AsNoTracking().Include(b => b.Property).Include(b => b.Org).Include(b => b.Guest)
            .SingleOrDefaultAsync(b => b.Id == portal.BookingId && b.OrgId == portal.OrgId
                && b.NoShowAt == null && b.Guest.DataAnonymizedDate == null, ct);
    }
}
