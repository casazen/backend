using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Push;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>Booking actions delegated by the routine engine after its event and consent checks.</summary>
public sealed class RoutineBookingActions(
    AppDbContext db,
    IMessageReplyService messages,
    IMessageTemplateService templates,
    IAccessCodeService accessCodes,
    IEmailQueue email,
    IBackgroundJobClient jobs,
    TimeProvider clock,
    IGuestEmailBrandingProvider? emailBranding = null)
{
    public async Task<RoutineActionResult> ExecuteAsync(
        Guid orgId, Guid executionId, int index, RoutineAction action,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        if (!RoutinePayload.TryGetId(payload, "bookingId", out var bookingId) ||
            !await db.Bookings.AnyAsync(b => b.Id == bookingId && b.OrgId == orgId, ct))
            return RoutineActionResult.Blocked(index, action.Type, "booking_not_found");

        if (!RoutineActionRules.HasRequiredFields(action))
            return RoutineActionResult.Failed(index, action.Type, RoutineCodes.ActionConfigInvalid);

        var key = $"routine-{executionId:N}-{index}";
        string? reason;
        try
        {
            reason = action.Type switch
            {
                RoutineActionType.MessageToGuest => await SendMessageAsync(orgId, bookingId, Guid.Parse(action.GetString("templateId")!), key, ct),
                RoutineActionType.SendAccessCode => await SendCodeAsync(orgId, bookingId, ct),
                RoutineActionType.AddTag => await AddTagAsync(orgId, bookingId, action.GetString("tag")!.Trim(), ct),
                RoutineActionType.NotifyOwner => QueueNotification(bookingId, key, PushTypes.RoutineOwner),
                RoutineActionType.NotifyStaff => QueueNotification(bookingId, key, PushTypes.RoutineStaff),
                RoutineActionType.NotifyTeam => QueueNotification(bookingId, key, PushTypes.RoutineTeam),
                _ => RoutineCodes.ActionNotAvailable,
            };
        }
        catch (DomainException ex)
        {
            reason = ex.Code ?? RoutineCodes.ActionError;
        }
        catch (NotFoundException ex)
        {
            reason = ex.Code ?? "booking_not_found";
        }

        return reason switch
        {
            null => RoutineActionResult.Done(index, action.Type),
            "routine_access_code_unavailable" or "no_conversation" or "routine_crm_profile_missing" =>
                RoutineActionResult.Blocked(index, action.Type, reason),
            _ => RoutineActionResult.Failed(index, action.Type, reason),
        };
    }

    private async Task<string?> SendMessageAsync(Guid orgId, Guid bookingId, Guid templateId, string key, CancellationToken ct)
    {
        if (!await db.MessageTemplates.AnyAsync(t => t.Id == templateId && t.OrgId == orgId && t.IsActive, ct))
            return "message_template_not_found";

        var scope = new HostScope(orgId);
        var contact = await messages.ChooseChannelAsync(scope, bookingId, ct);
        if (contact.ThreadKey is not { } thread)
            return contact.Reason ?? "no_conversation";

        var text = await templates.RenderAsync(scope, templateId, thread, ct);
        var result = await messages.ReplyAsync(scope, thread, text, key, null, MessageOrigin.Automation, null, ct);
        return result.State == OtaMessageDeliveryState.Sent ? null : result.ErrorCode ?? "routine_message_unconfirmed";
    }

    private async Task<string?> SendCodeAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId && !b.Guest.IsDeleted && b.Guest.DataAnonymizedDate == null)
            .Select(b => new { b.Guest.Email, b.Guest.FirstName, b.Guest.LastName, b.Property.Name, b.CheckInDate })
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(booking?.Email))
            return "contact_info_unavailable";

        var code = await accessCodes.ReleaseToGuestAsync(bookingId, AccessCodeReleaseChannels.Email, ct);
        if (code is null)
            return "routine_access_code_unavailable";

        // E50/F01/US03/T01: the brand of the host while the guest portal is on (null = the CasaZen e-mail).
        var branding = await emailBranding.ForOrgOrNullAsync(orgId, ct);
        var content = EmailTemplates.GuestAccessCode(EmailTemplates.GuestAccessCodeCulture(null),
            $"{booking.FirstName} {booking.LastName}".Trim(), booking.Name, booking.CheckInDate, code, branding);
        return email.Enqueue(booking.Email, content, EmailTemplates.Names.GuestAccessCode) ? null : "routine_email_queue_failed";
    }

    private string? QueueNotification(Guid bookingId, string key, string type)
    {
        var notification = new QueuedInAppNotification
        {
            AudienceKind = PushAudienceKind.BookingHosts,
            AudienceId = bookingId,
            EntityId = bookingId,
            Type = type,
            OccurredAt = clock.GetUtcNow().UtcDateTime,
        };
        jobs.Enqueue<InAppNotificationJob>(job => job.CreateAsync(key, notification, CancellationToken.None));
        return null;
    }

    private async Task<string?> AddTagAsync(Guid orgId, Guid bookingId, string tag, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.CrmProfileMerge, orgId.ToString("N")));
        var profileId = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId && !b.Guest.IsDeleted && b.Guest.DataAnonymizedDate == null)
            .Select(b => b.Guest.GuestCrmProfileId)
            .SingleOrDefaultAsync(ct);
        var profile = await db.GuestCrmProfiles.SingleOrDefaultAsync(
            p => p.Id == profileId && p.OrgId == orgId && p.MergedIntoProfileId == null, ct);
        if (profile is null)
            return "routine_crm_profile_missing";

        var tags = CrmProfileRules.ParseTags(profile.TagsJson);
        if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return null;
        if (!CrmProfileRules.TryNormalizeTags([.. tags, tag], out var normalized))
            return "crm_tags_invalid";

        profile.TagsJson = CrmProfileRules.SerializeTags(normalized);
        profile.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return null;
    }
}
