using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The final test of the listings flow (E10/F01/US01/T04): the property is on at least one channel and no channel refused its content. A property
/// whose every channel is written on the channel itself (Expedia) has nothing CasaZen sends: skipped. Reads the channel states, nothing else.
/// </summary>
public sealed class ListingsDryRunCheck(AppDbContext db) : IFlowDryRunCheck
{
    public OnboardingFlow Flow => OnboardingFlow.Listings;

    public async Task<FlowCheckResult> RunAsync(FlowDryRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var states = await db.ListingChannelStates
            .AsNoTracking()
            .Where(s => s.OrgId == context.OrgId && s.PropertyId == context.PropertyId)
            .Select(s => s.Status)
            .ToListAsync(ct);

        if (states.Count == 0)
            return FlowCheckResult.Fail(Flow, FlowCheckReasons.ChannelStatesMissing);
        if (states.All(status => status == ChannelPublishStatus.ManagedOnChannel))
            return FlowCheckResult.Skipped(Flow, FlowCheckReasons.ContentManagedOnChannel);

        return states.Contains(ChannelPublishStatus.ToFix)
            ? FlowCheckResult.Fail(Flow, FlowCheckReasons.ChannelContentToFix)
            : FlowCheckResult.Pass(Flow);
    }
}

/// <summary>
/// The final test of the bookings flow (E10/F01/US01/T04): the state of the booking maps both ways through <see cref="BookingStateMapper"/>
/// without an exception (the Octorate state it stands for comes back as the same CasaZen state, and the outbound operation of that state is
/// known), and a booking of an OTA carries at least one of the amounts Octorate documents (the commission, what the guest paid; the net and the
/// balance have no documented source and are never asked). Pure: no read, no call.
/// </summary>
public sealed class BookingsDryRunCheck(BookingStateMapper mapper) : IFlowDryRunCheck
{
    public OnboardingFlow Flow => OnboardingFlow.Bookings;

    public Task<FlowCheckResult> RunAsync(FlowDryRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var booking = context.Booking;
        var reasons = new List<string>();
        if (!MapsBothWays(booking.Status))
            reasons.Add(FlowCheckReasons.BookingStatusUnmapped);
        if (FiscalCopy.IsOtaBookingSource(booking.Source) && booking.OtaCommission is null && booking.OtaPaidAmount is null)
            reasons.Add(FlowCheckReasons.OtaAmountsMissing);

        return Task.FromResult(reasons.Count == 0 ? FlowCheckResult.Pass(Flow) : FlowCheckResult.Fail(Flow, [.. reasons]));
    }

    /// <summary>
    /// The state maps both ways when the mapper answers for it going out (the operation CasaZen sends, which may be none) and some Octorate state
    /// comes back as this very state. Derived from the mapper, with no table of its own: it follows the mapper when it changes. A booking in the
    /// house or over (<see cref="BookingStatus.CheckedIn"/>, <see cref="BookingStatus.CheckedOut"/>) is CasaZen's own state, which the mapper keeps
    /// whatever Octorate says.
    /// </summary>
    private bool MapsBothWays(BookingStatus status)
    {
        _ = mapper.ToOutbound(status);
        return Enum.GetValues<OctorateBookingStatus>().Any(remote => ReadsBack(remote, status));
    }

    private bool ReadsBack(OctorateBookingStatus remote, BookingStatus status)
    {
        try
        {
            return mapper.ToBookingStatus(remote, status) == status;
        }
        catch (ArgumentOutOfRangeException)
        {
            // An Octorate state the mapper does not read is no way back for this state.
            return false;
        }
    }
}

/// <summary>
/// The final test of the check-in and compliance flow (E10/F01/US01/T04): the state of the check-in wizard of the booking, read through
/// <see cref="ICheckInWizardService.GetStateAsync"/> only (the send is never called): the property has a stored sender of the police and ISTAT
/// communications, and the guest data still to give are reported (information: they are often given at the arrival). A booking that owes no
/// police communication (<see cref="AlloggiatiStatusRules.IsDue"/>: a request, a cancelled booking, a no-show) and a property outside Italy are
/// skipped.
/// </summary>
public sealed class CheckInComplianceDryRunCheck(ICheckInWizardService wizard) : IFlowDryRunCheck
{
    public OnboardingFlow Flow => OnboardingFlow.CheckInCompliance;

    public async Task<FlowCheckResult> RunAsync(FlowDryRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AlloggiatiStatusRules.IsDue(context.Booking))
            return FlowCheckResult.Skipped(Flow, FlowCheckReasons.BookingNotActive);

        CheckInState state;
        try
        {
            state = await wizard.GetStateAsync(context.Booking.Id, ct);
        }
        catch (DomainException ex)
        {
            // A rule of the wizard refused the read: the flow would stop there too, with that code.
            return FlowCheckResult.Fail(Flow, ex.Code);
        }

        if (!state.Sender.Applies)
            return FlowCheckResult.Skipped(Flow, FlowCheckReasons.NotApplicable);

        var guestDataMissing = state.Issues.Any(issue => issue.Blocking);
        if (state.Sender.Source != SenderSource.Stored)
        {
            return guestDataMissing
                ? FlowCheckResult.Fail(Flow, FlowCheckReasons.SenderMissing, FlowCheckReasons.GuestDataMissing)
                : FlowCheckResult.Fail(Flow, FlowCheckReasons.SenderMissing);
        }

        return guestDataMissing ? FlowCheckResult.Pass(Flow, FlowCheckReasons.GuestDataMissing) : FlowCheckResult.Pass(Flow);
    }
}
