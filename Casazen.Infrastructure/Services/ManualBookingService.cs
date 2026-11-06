using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IManualBookingService"/>
/// <remarks>
/// It orders what already exists and adds no write of its own. A booking by direct contact is priced by
/// <see cref="IBookingService.PriceHostStayAsync"/> (nightly rates and weekend surcharge, cleaning, tourist tax of the comune), the
/// price is resolved by <see cref="ManualBookingRules.PriceOf"/> and the booking is written by
/// <see cref="IBookingService.CreateManualBookingAsync"/>, whose last check on the dates runs under the lock of the property, in the
/// transaction that inserts it: two bookings entered together on the same dates cannot both succeed. A booking that completes a
/// portal's block goes through <see cref="IOtaStayService.ConvertBlockAsync"/>, under the lock of the property's feeds, with the same
/// final check. The minimum stay is a rule of the guests' checkout, not of the host who enters a booking: it is only reported.
/// No email, no push and no payment is started here, except the payment link the host asks for (E08/F01/US01/T05): the arrangement is
/// stored on the booking, and with <see cref="HostPaymentArrangement.PaymentLink"/> the link is made by <see cref="IBookingPaymentLinkService"/>
/// once the booking is written (what can stop it, a Stripe account not ready or nothing to pay, is checked before anything is written). The
/// booking is created and confirmed in one step, and its two events
/// (<c>BookingCreated</c>, <c>BookingConfirmed</c>, origin Host, E08/F06/US01/T01) are recorded by the two writers above, in the
/// save that inserts the booking: <see cref="BookingService"/> for a booking by direct contact, <see cref="OtaStayService"/> for the
/// completion of a block. They cannot be recorded from here, which does not save: an event after the commit could be lost.
/// </remarks>
public sealed class ManualBookingService(
    IBookingService bookings,
    IOtaStayService otaStays,
    ILogger<ManualBookingService> logger,
    TimeProvider? timeProvider = null,
    IBookingPaymentLinkService? paymentLinks = null) : IManualBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ManualBookingResult> CreateAsync(ManualBookingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Property);
        ArgumentNullException.ThrowIfNull(input.Guest);

        // PM-01: a property in long-term mode takes no stay. Before the guests check, which would answer "0 guests".
        var property = input.Property;
        PropertyRentalModeRules.EnsureShortRent(property);

        var firstName = input.Guest.FirstName?.Trim() ?? string.Empty;
        var email = input.Guest.Email?.Trim() ?? string.Empty;
        var guests = input.Adults + input.Children;
        if (firstName.Length == 0 || email.Length == 0 || input.Adults < 1 || input.Children < 0)
            throw new DomainRuleException(BookingErrorCodes.CreateInvalid, "BookingCreateInvalid");

        if (guests > property.MaxGuests)
            throw new DomainRuleException(BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", property.MaxGuests);

        // A payment link needs a Stripe account ready to take payments: checked before the booking is written. The portal of a block
        // collects its own booking, so there is no link for it.
        if (input.PaymentArrangement == HostPaymentArrangement.PaymentLink)
        {
            if (input.BlockId is not null || paymentLinks is null)
                throw PaymentLinkUnavailable();

            await paymentLinks.EnsureAvailableAsync(property.OrgId, cancellationToken);
        }

        var checkIn = input.CheckInDate.Date;
        var checkOut = input.CheckOutDate.Date;

        return input.BlockId is { } blockId
            ? await CompleteBlockAsync(input, blockId, checkIn, checkOut, firstName, email, guests, cancellationToken)
            : await CreateByDirectContactAsync(input, checkIn, checkOut, firstName, email, guests, cancellationToken);
    }

    private async Task<ManualBookingResult> CreateByDirectContactAsync(
        ManualBookingInput input,
        DateTime checkIn,
        DateTime checkOut,
        string firstName,
        string email,
        int guests,
        CancellationToken cancellationToken)
    {
        var property = input.Property;

        // The price and the tourist tax of the host's quote, like the form that changes a booking: the client never sends them.
        var quote = await bookings.PriceHostStayAsync(
            property, checkIn, checkOut, input.Adults, input.Children, input.ChildrenAges, cancellationToken);
        if (quote.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
            throw new DomainRuleException(DirectBookingErrorCodes.ChildAgesRequired, "TouristTaxChildAgesRequired");

        var price = ManualBookingRules.PriceOf(quote, input.TotalPrice, input.PriceNote);

        // Nothing to pay, no link: refused before the booking is written.
        if (input.PaymentArrangement == HostPaymentArrangement.PaymentLink && price.TotalPrice <= 0m)
            throw PaymentLinkUnavailable();

        var now = _clock.GetUtcNow().UtcDateTime;
        // One guest snapshot per booking, owned by the property's org (TN-1): never a lookup by email, so a booking can neither
        // reuse nor reveal a guest of another org. Only the first name and the email are asked; the rest is filled by the guest.
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = firstName,
            LastName = input.Guest.LastName?.Trim() ?? string.Empty,
            Email = email,
            PhoneNumber = input.Guest.Phone?.Trim() ?? string.Empty,
            Country = input.Guest.Country?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            CheckInDate = quote.CheckInDate,
            CheckOutDate = quote.CheckOutDate,
            NumberOfGuests = guests,
            NumberOfAdults = input.Adults,
            NumberOfChildren = input.Children,
            SpecialRequests = input.Note?.Trim() ?? string.Empty,
            BasePrice = price.BasePrice,
            CleaningFee = price.CleaningFee,
            TouristTax = price.TouristTax,
            TouristTaxAmount = price.TouristTax,
            TotalPrice = price.TotalPrice,
            PriceOverridden = price.Overridden,
            PriceOverrideNote = price.Note,
            HostPaymentArrangement = input.PaymentArrangement,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Status (Confirmed), source (Manual), the check-in not in the past (422) and the overlap check (409) are the service's job.
        var created = await bookings.CreateManualBookingAsync(booking, guest);

        var belowMinimum = property.MinNights is { } minimum && quote.Nights < minimum;
        logger.LogInformation(
            "Manual booking {BookingId} created on property {PropertyId} (agreed price: {Overridden}, below the minimum stay: {BelowMinimum}, arrangement: {Arrangement})",
            created.Id, property.Id, price.Overridden, belowMinimum, input.PaymentArrangement);

        if (input.PaymentArrangement != HostPaymentArrangement.PaymentLink || paymentLinks is null)
            return new ManualBookingResult(created, belowMinimum, property.MinNights);

        // The booking is written: a link that cannot be made now (Stripe did not answer) does not undo it. The host sends it again from the
        // booking (POST /api/bookings/{id}/payment-link) and the answer says why it is missing.
        try
        {
            var link = await paymentLinks.CreateAsync(created.Id, cancellationToken);
            return new ManualBookingResult(created, belowMinimum, property.MinNights, link.Url);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Manual booking {BookingId}: the payment link was not made ({Code})", created.Id, ex.Code);
            return new ManualBookingResult(created, belowMinimum, property.MinNights, PaymentLinkError: ex.Code);
        }
        catch (PaymentProcessingException ex)
        {
            logger.LogWarning(ex, "Manual booking {BookingId}: the payment link was not made (payment provider)", created.Id);
            return new ManualBookingResult(created, belowMinimum, property.MinNights, PaymentLinkError: PaymentProviderErrorCode);
        }
    }

    /// <summary>The code of a payment link Stripe did not make: the code the API gives a failure of the payment provider.</summary>
    internal const string PaymentProviderErrorCode = "payment_provider_error";

    private static DomainRuleException PaymentLinkUnavailable() =>
        new(BookingPaymentLinkErrorCodes.Unavailable, BookingPaymentLinkErrorCodes.UnavailableMessageKey);

    private async Task<ManualBookingResult> CompleteBlockAsync(
        ManualBookingInput input,
        Guid blockId,
        DateTime checkIn,
        DateTime checkOut,
        string firstName,
        string email,
        int guests,
        CancellationToken cancellationToken)
    {
        var property = input.Property;

        // A block of another property (or org) is as good as missing, like a block that is not there.
        var block = await otaStays.FindBlockAsync(blockId, cancellationToken);
        if (block is null || block.PropertyId != property.Id)
        {
            throw new NotFoundException($"Calendar block {blockId} not found")
            {
                Code = OtaStayErrorCodes.BlockNotFound,
                MessageKey = OtaStayErrorCodes.BlockNotFoundMessageKey,
            };
        }

        // The stay of a block has the dates of the block: dates other than the ones the host saw as linkable are a stale form.
        if (block.StartUtc.Date != checkIn || block.EndUtc.Date != checkOut)
            throw new DomainRuleException(BookingErrorCodes.BlockDatesMismatch, "BookingBlockDatesMismatch");

        // The portal collects: no quote, no tax and no payment here; the amount is the one the host reads on the portal, if given.
        var stay = await otaStays.ConvertBlockAsync(
            new OtaStayConversion(
                blockId,
                firstName,
                input.Guest.LastName?.Trim() ?? string.Empty,
                email,
                guests,
                input.TotalPrice,
                input.Source,
                input.Guest.Phone,
                input.Guest.Country,
                input.Note),
            cancellationToken);

        logger.LogInformation(
            "Manual booking {BookingId} completes block {BlockId} of property {PropertyId}", stay.Id, blockId, property.Id);
        return new ManualBookingResult(stay, BelowMinimum: false, property.MinNights);
    }
}
