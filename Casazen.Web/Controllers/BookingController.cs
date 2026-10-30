using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingsController(
    IBookingService bookingService,
    IAlloggiatiWebService alloggiatiWebService,
    IPropertyService propertyService,
    IPropertyAuthorizationService authorizationService,
    PropertyICalSyncService propertyICalSyncService,
    IOtaStayService otaStays,
    ILogger<BookingsController> logger,
    IOptions<BookingsOptions> bookingsOptions) : ControllerBase
{
    /// <summary>Code of a <c>status</c> query value of the search that is not the name of a booking status (SR-03).</summary>
    public const string InvalidStatusCode = "booking_list_invalid_status";

    /// <summary>Code of a <c>from</c> after <c>to</c> in the search (SR-03).</summary>
    public const string InvalidRangeCode = "booking_list_invalid_range";

    /// <summary>Upper bound for <c>pageSize</c> on <c>GET /api/bookings</c>, same cap as the other paged lists.</summary>
    private const int MaxPageSize = 100;

    private readonly BookingsOptions _bookingsOptions = bookingsOptions.Value;

    /// <summary>Code of a <c>view</c> query value of the search that is not the name of a chip (SR-03b).</summary>
    public const string InvalidViewCode = "booking_list_invalid_view";

    /// <summary>Code of a <c>channel</c> query value of the search that is not the name of a channel (SR-03b).</summary>
    public const string InvalidChannelCode = "booking_list_invalid_channel";

    /// <summary>Code of a <c>checkInStatus</c> query value of the search that is not the name of a check-in state (SR-03b).</summary>
    public const string InvalidCheckInStatusCode = "booking_list_invalid_check_in_status";

    /// <summary>Code of a <c>sort</c> query value of the search that is not an order the list knows (SR-03b).</summary>
    public const string InvalidSortCode = "booking_list_invalid_sort";

    /// <summary>
    /// The bookings the caller sees, latest check-in first, a page at a time (PC-14: <c>{ items, totalCount, page, pageSize }</c>,
    /// <c>Bookings:DefaultPageSize</c> when <c>pageSize</c> is missing): <c>propertyId</c> and <c>guestId</c> narrow it.
    /// TN-3: filtered in SQL by the caller's scope (org, and the owned properties for a non org-wide role) in one query whatever
    /// the number of bookings (A2-17). With <c>propertyId</c>: 404 when the property is not in the caller's org, 403 without
    /// <c>booking.read</c> on it. <c>GET /api/bookings/search</c> adds days, status, text, the chips and the order. Since SR-03b it
    /// is that search without a filter but these two (<see cref="IBookingSearchService"/>: one rule for the order and the scope), so
    /// the answer is the same, rows included.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<BookingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<BookingListItemDto>>> GetAll(
        [FromServices] IBookingSearchService searchService,
        [FromServices] IOrgContextResolver orgContextResolver,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IHostScopeResolver hostScopeResolver,
        [FromQuery] Guid? propertyId = null,
        [FromQuery] Guid? guestId = null,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null)
    {
        if (GetUserId() == null)
            return Unauthorized();

        // Out-of-range values would become a negative OFFSET/LIMIT in SQL, i.e. a 500 (A1-26): clamp them instead.
        page = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize ?? _bookingsOptions.DefaultPageSize, 1, MaxPageSize);

        var cancellationToken = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        // No org yet, or no reach on it: nothing of any org is visible (the tenant filter showed nothing either).
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return Ok(new PagedResultDto<BookingListItemDto> { Items = [], TotalCount = 0, Page = page, PageSize = effectivePageSize });

        if (await DenyPropertyFilterAsync(propertyId, hostAuthorization) is { } denied)
            return denied;

        var result = await searchService.SearchAsync(
            scope,
            new BookingSearchCriteria(
                PropertyId: propertyId,
                GuestId: guestId,
                Page: page,
                PageSize: effectivePageSize,
                IncludePaidAmount: await CanReadPaymentsAsync(hostAuthorization)),
            cancellationToken);

        return Ok(ToPage(result));
    }

    /// <summary>
    /// The paged list of <c>GET /api/bookings</c> (PC-14: the same bookings, the same order, the same
    /// <c>{ items, totalCount, page, pageSize }</c> answer) narrowed by days, status and text, for the screens that look for a
    /// booking (SR-03). <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, stay dates, both included): the stays that have a day in
    /// the range, arrival day to departure day. <c>status</c>: one or more names (<c>Pending</c>, <c>Confirmed</c>,
    /// <c>CheckedIn</c>, <c>CheckedOut</c>, <c>Cancelled</c>), repeated or separated by commas. <c>q</c>: the name or email of the
    /// guest, the name of the property or the booking code, any case. <c>propertyId</c> and <c>guestId</c> as in the plain list.
    /// <c>page</c> (from 1) and <c>pageSize</c> (the <c>Bookings:DefaultPageSize</c> of the plain list when missing, at most
    /// 100). Latest check-in first unless <c>sort</c> says otherwise, the id as the last criterion: a total order, so a page never
    /// repeats or skips a booking.
    /// <para>
    /// SR-03b, all optional: <c>arrivalFrom</c> and <c>arrivalTo</c> (<c>yyyy-MM-dd</c>, both included) keep the stays that
    /// <i>arrive</i> in the range (the check-in date: not the stays that have a day in it, which <c>from</c>/<c>to</c> do);
    /// <c>view</c> is a chip, <c>all</c> (default), <c>toConfirm</c>, <c>upcoming</c>, <c>inProgress</c>, <c>completed</c> or
    /// <c>cancelled</c> (<see cref="BookingListRules.InView"/>); <c>channel</c> one or more of <c>Direct</c>, <c>Manual</c>,
    /// <c>Airbnb</c>, <c>BookingCom</c>, <c>Other</c>; <c>checkInStatus</c> one or more of <c>NotSent</c>, <c>Sent</c>,
    /// <c>InProgress</c>, <c>Completed</c>, <c>Expired</c> (the state of the online check-in link, as in
    /// <c>GET /api/dashboard/today</c>); <c>sort</c> is <c>checkIn</c>, <c>createdAt</c>, <c>total</c>, <c>status</c> or
    /// <c>guest</c>, then <c>:asc</c> (default) or <c>:desc</c>. Each row also says <c>channel</c>, <c>paymentState</c>,
    /// <c>paidAmount</c> (only for a caller with <c>payment.read</c>), <c>alloggiatiStatus</c> and <c>respondBy</c>.
    /// </para>
    /// TN-3: filtered in SQL by the caller's scope; with <c>propertyId</c>: 404 when the property is not in the caller's org,
    /// 403 without <c>booking.read</c> on it. 400 <c>booking_list_invalid_status</c>, <c>booking_list_invalid_range</c> (also for
    /// <c>arrivalFrom</c> after <c>arrivalTo</c>), <c>booking_list_invalid_view</c>, <c>booking_list_invalid_channel</c>,
    /// <c>booking_list_invalid_check_in_status</c> and <c>booking_list_invalid_sort</c>.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(PagedResultDto<BookingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<BookingListItemDto>>> Search(
        [FromServices] IBookingSearchService searchService,
        [FromServices] IOrgContextResolver orgContextResolver,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IHostScopeResolver hostScopeResolver,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery(Name = "status")] string[]? status = null,
        [FromQuery] string? q = null,
        [FromQuery] Guid? propertyId = null,
        [FromQuery] Guid? guestId = null,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        [FromQuery] DateOnly? arrivalFrom = null,
        [FromQuery] DateOnly? arrivalTo = null,
        [FromQuery] string? view = null,
        [FromQuery(Name = "channel")] string[]? channel = null,
        [FromQuery(Name = "checkInStatus")] string[]? checkInStatus = null,
        [FromQuery] string? sort = null)
    {
        if (GetUserId() == null)
            return Unauthorized();

        // The size of a page of the plain list when the caller does not say (PC-14: Bookings:DefaultPageSize).
        var requestedPageSize = pageSize ?? _bookingsOptions.DefaultPageSize;

        if (TryReadFilters(
                fromDay, toDay, status, q, propertyId, guestId, arrivalFrom, arrivalTo, view, channel, checkInStatus, sort, out var filters)
            is { } invalid)
            return invalid;

        var cancellationToken = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        // No org yet, or no reach on it: nothing of any org is visible, as in the plain list.
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
        {
            return Ok(new PagedResultDto<BookingListItemDto>
            {
                Items = [],
                TotalCount = 0,
                Page = Math.Clamp(page, 1, BookingSearchCriteria.MaxPage),
                PageSize = Math.Clamp(requestedPageSize, 1, BookingSearchCriteria.MaxPageSize),
            });
        }

        if (await DenyPropertyFilterAsync(propertyId, hostAuthorization) is { } denied)
            return denied;

        var result = await searchService.SearchAsync(
            scope,
            filters with { Page = page, PageSize = requestedPageSize, IncludePaidAmount = await CanReadPaymentsAsync(hostAuthorization) },
            cancellationToken);

        return Ok(ToPage(result));
    }

    /// <summary>
    /// How many bookings are in each chip of the list (SR-03b): <c>{ all, toConfirm, upcoming, inProgress, completed, cancelled }</c>
    /// for the bookings that satisfy the filters of <c>GET /api/bookings/search</c> but the chip (<c>view</c>), the page and the
    /// order. One aggregate query, filtered in SQL by the caller's scope like the list. A booking is in at most one chip, so the
    /// counters never count it twice. Same 400s as the search for the filters it takes; same 404 and 403 for <c>propertyId</c>.
    /// </summary>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(BookingCountsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingCountsDto>> Counts(
        [FromServices] IBookingSearchService searchService,
        [FromServices] IOrgContextResolver orgContextResolver,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IHostScopeResolver hostScopeResolver,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery(Name = "status")] string[]? status = null,
        [FromQuery] string? q = null,
        [FromQuery] Guid? propertyId = null,
        [FromQuery] Guid? guestId = null,
        [FromQuery] DateOnly? arrivalFrom = null,
        [FromQuery] DateOnly? arrivalTo = null,
        [FromQuery(Name = "channel")] string[]? channel = null,
        [FromQuery(Name = "checkInStatus")] string[]? checkInStatus = null)
    {
        if (GetUserId() == null)
            return Unauthorized();

        if (TryReadFilters(
                fromDay, toDay, status, q, propertyId, guestId, arrivalFrom, arrivalTo, null, channel, checkInStatus, null, out var filters)
            is { } invalid)
            return invalid;

        var cancellationToken = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return Ok(new BookingCountsDto());

        if (await DenyPropertyFilterAsync(propertyId, hostAuthorization) is { } denied)
            return denied;

        return Ok(BookingCountsDto.From(await searchService.CountAsync(scope, filters, cancellationToken)));
    }

    // The filters of a request as criteria, or the 400 that says which one is not understood (the same words for the list and the
    // counters of its chips). The page, the size and the money are the caller's to add.
    private ObjectResult? TryReadFilters(
        DateOnly? fromDay,
        DateOnly? toDay,
        string[]? status,
        string? q,
        Guid? propertyId,
        Guid? guestId,
        DateOnly? arrivalFrom,
        DateOnly? arrivalTo,
        string? view,
        string[]? channel,
        string[]? checkInStatus,
        string? sort,
        out BookingSearchCriteria filters)
    {
        filters = new BookingSearchCriteria();

        if (!TryParseStatuses(status, out var statuses))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidStatusCode, "BookingListInvalidStatus");

        if (fromDay is { } first && toDay is { } last && first > last)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidRangeCode, "BookingListInvalidRange");

        if (arrivalFrom is { } firstArrival && arrivalTo is { } lastArrival && firstArrival > lastArrival)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidRangeCode, "BookingListInvalidArrivalRange");

        if (!BookingListRules.TryParseView(view, out var chip))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidViewCode, "BookingListInvalidView");

        if (!BookingListRules.TryParseNames<BookingChannel>(channel, out var channels))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidChannelCode, "BookingListInvalidChannel");

        if (!BookingListRules.TryParseNames<HostTodayCheckInState>(checkInStatus, out var checkInStates))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidCheckInStatusCode, "BookingListInvalidCheckInStatus");

        if (!BookingListRules.TryParseSort(sort, out var order))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidSortCode, "BookingListInvalidSort");

        filters = new BookingSearchCriteria(
            fromDay,
            toDay,
            statuses,
            q,
            propertyId,
            guestId,
            ArrivalFrom: arrivalFrom,
            ArrivalTo: arrivalTo,
            View: chip,
            Channels: channels,
            CheckInStates: checkInStates,
            Sort: order);
        return null;
    }

    // The answer of the lists: each booking with what its row says besides it.
    private static PagedResultDto<BookingListItemDto> ToPage(BookingSearchPage result)
    {
        var nowUtc = DateTime.UtcNow;
        return new PagedResultDto<BookingListItemDto>
        {
            Items = result.Items.Select(b => BookingMapper.ToListItem(b, result.Details.GetValueOrDefault(b.Id), nowUtc)).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        };
    }

    // Money (what came in on a booking) is payment data: it needs payment.read, which the collaborator does not hold (SR-D7).
    private async Task<bool> CanReadPaymentsAsync(IAuthorizationService hostAuthorization) =>
        (await hostAuthorization.AuthorizeAsync(User, CasazenPolicies.PaymentRead)).Succeeded;

    // 404 when the property is not in the caller's org, 403 without booking.read on it; null when the filter may be used.
    private async Task<ActionResult?> DenyPropertyFilterAsync(Guid? propertyId, IAuthorizationService hostAuthorization)
    {
        if (propertyId is not { } id)
            return null;

        var property = await propertyService.GetPropertyRecordAsync(id);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Read))
        {
            logger.LogWarning(
                "User {UserId} denied booking.read on the bookings of property {PropertyId}",
                User.GetUserId(), id);
            return Forbid();
        }

        return null;
    }

    /// <summary>
    /// The statuses of the <c>status</c> query values (repeated, or separated by commas), by name and case-insensitive: a number
    /// would bind to an undefined enum value, so it is refused like any other word. No value at all = no filter.
    /// </summary>
    public static bool TryParseStatuses(IEnumerable<string>? values, out IReadOnlyList<BookingStatus> statuses)
    {
        var parsed = new List<BookingStatus>();
        statuses = parsed;
        foreach (var token in (values ?? []).SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!Enum.TryParse<BookingStatus>(token, ignoreCase: true, out var parsedStatus)
                || !Enum.IsDefined(parsedStatus)
                || token.All(char.IsAsciiDigit))
                return false;

            if (!parsed.Contains(parsedStatus))
                parsed.Add(parsedStatus);
        }

        return true;
    }

    /// <summary>
    /// One booking, with the state of what is tied to it (SR-05a): <c>paymentState</c> (<c>None</c>, <c>Partial</c>, <c>Paid</c>,
    /// <c>Refunded</c>), <c>paidAmount</c> (euros; <c>null</c> for a caller without <c>payment.read</c>, who still gets the state),
    /// <c>guestDataComplete</c> (<c>null</c> for a booking with no Alloggiati communication), and, as on every booking, the times it kept
    /// (<c>confirmedAt</c>, <c>arrivedAt</c>, <c>checkedOutAt</c>, <c>cancelledAt</c>) and <c>canEdit</c>. Its history is
    /// <c>GET /api/bookings/{id}/timeline</c>.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(BookingDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDetailDto>> GetById(Guid id, [FromServices] IAuthorizationService hostAuthorization)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        var response = BookingMapper.Fill(new BookingDetailDto(), booking, DateTime.UtcNow);

        // What came in: the state for everyone, the money only for a caller who may read payments (SR-D7). The payments came with the booking.
        var payments = booking.Payments.Select(p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount)).ToList();
        response.PaymentState = BookingListRules.PaymentStateOf(booking.TotalPrice, payments).ToString();
        response.PaidAmount = await CanReadPaymentsAsync(hostAuthorization) ? BookingListRules.PaidAmount(payments) : null;
        // A communication exists for a stay that is confirmed or later: a request, a hold or a cancelled booking has none.
        response.GuestDataComplete = booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut
            ? await alloggiatiWebService.IsStayDataCompleteAsync(id)
            : null;
        // OTA stay from iCal (CO-21): the dates its block has now on the channel, for the "da verificare" notice.
        if (booking.ICalFeedId is not null)
        {
            var block = await otaStays.GetLinkedBlockAsync(id, HttpContext.RequestAborted);
            response.ChannelBlock = block is null ? null : OtaChannelBlockDto.From(block);
        }

        return Ok(response);
    }

    /// <summary>
    /// Booking entered by the host (phone, walk-in, another channel): created <c>Confirmed</c> with source
    /// <c>Manual</c>, so it occupies its dates on the booking site and in the iCal export at once and is never
    /// cancelled by the expiry of abandoned checkout holds (PC-01, A2-01). Overlapping dates answer 409
    /// <c>booking_dates_unavailable</c>; a property in long-term mode (PM-01) takes no stay: 422
    /// <c>property_not_bookable_in_long_mode</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Create(
        [FromBody] CreateBookingRequest request,
        [FromServices] IAuthorizationService hostAuthorization)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        // TN-3: the tenant filter hides a property of another org (404); a visible one still needs booking.write on
        // it (owner or org-wide role), otherwise 403.
        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Write))
        {
            logger.LogWarning(
                "User {UserId} denied booking.write on property {PropertyId}",
                User.GetUserId(), request.PropertyId);
            return Forbid();
        }

        // PM-01: a property in long-term mode takes no stay: 422 property_not_bookable_in_long_mode. Before the guests
        // check, which would answer "too many guests (0)" for a property without guest capacity.
        PropertyRentalModeRules.EnsureShortRent(property);

        // A CHF house is booked like any other, in its own currency (CHF-MONEY, which supersedes ADR-001-R10); only a currency the
        // product does not take (neither EUR nor CHF) is refused.
        if (!StayCurrency.IsSupported(property.Currency))
        {
            return this.ApiProblem(
                StatusCodes.Status501NotImplemented,
                DirectBookingErrorCodes.CurrencyNotSupported,
                "DirectBookingCurrencyNotSupported");
        }

        if (request.NumberOfGuests > property.MaxGuests)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                BookingErrorCodes.TooManyGuests,
                "BookingTooManyGuests",
                property.MaxGuests);
        }

        // Same pricing as the change of a manual booking and the host quote (PC-07): nightly rate x nights + cleaning
        // fee, tourist tax of BK-03 with the minors and their ages when the rates of the comune depend on them. Unknown
        // rate: no tax, never an invented one.
        var adults = request.NumberOfGuests - request.NumberOfChildren;
        var price = await bookingService.PriceHostStayAsync(
            property,
            request.CheckInDate,
            request.CheckOutDate,
            adults,
            request.NumberOfChildren,
            request.ChildrenAges,
            HttpContext.RequestAborted);
        if (price.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                DirectBookingErrorCodes.ChildAgesRequired,
                "TouristTaxChildAgesRequired");
        }

        logger.LogInformation("Creating manual booking for property {PropertyId}", request.PropertyId);

        var booking = new Booking
        {
            PropertyId = request.PropertyId,
            OrgId = property.OrgId,
            CheckInDate = price.CheckInDate,
            CheckOutDate = price.CheckOutDate,
            NumberOfGuests = request.NumberOfGuests,
            NumberOfAdults = adults,
            NumberOfChildren = request.NumberOfChildren,
            SpecialRequests = request.SpecialRequests ?? string.Empty,
            BasePrice = price.BasePrice,
            CleaningFee = price.CleaningFee,
            TouristTax = price.TouristTax.AmountOrZero,
            TouristTaxAmount = price.TouristTax.AmountOrZero,
            TotalPrice = price.TotalPrice,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Status (Confirmed), source (Manual), guest snapshot and the overlap check (409) are the service's job.
        var created = await bookingService.CreateManualBookingAsync(booking, NewGuestSnapshot(property.OrgId, request.Guest));
        var loaded = await bookingService.GetBookingAsync(created.Id);
        var response = loaded is null ? BookingMapper.ToResponse(created) : BookingMapper.ToResponse(loaded);
        logger.LogInformation(
            "Manual booking created: {BookingId}, tourist tax: {Tax} {Currency}",
            created.Id,
            created.TouristTax,
            StayCurrency.ForProperty(property));
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    /// <summary>
    /// Host calendar of a property, read by the web console and the app (MO-06): its bookings and calendar blocks from
    /// <paramref name="startDate"/> to <paramref name="endDate"/>. The two ends are stay dates (<c>2026-09-01</c>,
    /// <c>2026-09-30</c>), both included, never converted to a time zone; an entry is returned when a day of it, from
    /// arrival to departure, falls in the range (<see cref="HostCalendarRange"/>). The dates of the entries are stay
    /// dates too. <paramref name="timezone"/> (default: the property's) only fills <c>timezone</c> and
    /// <c>utcOffsetMinutes</c>. Blocks are <c>ical-block</c> items with the channel of their feed: they have no guest and
    /// no booking detail.
    /// </summary>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(CalendarResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalendarResponseDto>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromQuery] string? timezone = null)
    {
        // TN-3: the tenant filter hides a property of another org (404); a visible one still needs booking.read on it.
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Read))
        {
            logger.LogWarning(
                "User {UserId} denied booking.read on the calendar of property {PropertyId}",
                User.GetUserId(), propertyId);
            return Forbid();
        }

        if (endDate.Date < startDate.Date)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, BookingErrorCodes.CalendarRangeInvalid, "BookingCalendarRangeInvalid");
        }

        var targetTimezone = timezone ?? property.Timezone;
        if (!TimezoneHelper.IsValidTimezone(targetTimezone))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "PropertyTimezoneInvalid");

        var bookings = await bookingService.GetCalendarAsync(propertyId, startDate, endDate);
        var blocks = await propertyICalSyncService.GetBlocksInRangeAsync(propertyId, startDate, endDate);

        var utcOffsetMinutes = TimezoneHelper.GetUtcOffsetMinutes(targetTimezone, DateTime.UtcNow);

        var calendarBookings = bookings
            .OrderBy(b => b.CheckInDate)
            .Select(b => new CalendarBookingDto
            {
                Id = b.Id,
                PropertyId = b.PropertyId,
                GuestId = b.GuestId,
                CheckInDate = HostCalendarRange.StayDate(b.CheckInDate),
                CheckOutDate = HostCalendarRange.StayDate(b.CheckOutDate),
                CheckInDateUtc = b.CheckInDate,
                CheckOutDateUtc = b.CheckOutDate,
                Status = b.Status.ToString(),
                Source = b.Source.ToString(),
                NumberOfGuests = b.NumberOfGuests,
                TotalPrice = b.TotalPrice,
                GuestName = b.Guest != null ? $"{b.Guest.FirstName} {b.Guest.LastName}".Trim() : ""
            }).ToList();

        var items = calendarBookings.Select(b => new CalendarItemDto
        {
            Type = "booking",
            Id = b.Id,
            PropertyId = b.PropertyId,
            StartDate = b.CheckInDate,
            EndDate = b.CheckOutDate,
            StartDateUtc = b.CheckInDateUtc,
            EndDateUtc = b.CheckOutDateUtc,
            Status = b.Status,
            Source = b.Source,
            NumberOfGuests = b.NumberOfGuests,
            TotalPrice = b.TotalPrice,
            GuestName = b.GuestName,
        }).ToList();

        // OTA stays created from iCal (CO-21): feed and "da verificare" on the booking item.
        var stays = bookings.ToDictionary(b => b.Id);
        foreach (var item in items)
        {
            var stay = stays[item.Id];
            item.IcalFeedId = stay.ICalFeedId;
            item.ChannelLabel = stay.ChannelLabel;
            item.OtaReviewReason = stay.Status == BookingStatus.Cancelled ? null : stay.OtaReviewReason?.ToString();
        }

        // A block that became an OTA stay with the same dates is shown once, as the stay (its nights count once too).
        items.AddRange(blocks.Where(block => !block.RepresentedByStay).Select(block => new CalendarItemDto
        {
            Type = "ical-block",
            Id = block.Id,
            PropertyId = block.PropertyId,
            StartDate = HostCalendarRange.StayDate(block.StartUtc),
            EndDate = HostCalendarRange.StayDate(block.EndUtc),
            StartDateUtc = block.StartUtc,
            EndDateUtc = block.EndUtc,
            Summary = block.Summary,
            Channel = block.Channel?.ToString(),
            FeedLabel = block.FeedLabel,
            BlockSource = block.Source.ToString(),
            FeedId = block.FeedId,
            BookingId = block.StayId,
            Convertible = block.Convertible,
            BlockReason = block.ManualReason?.ToString(),
        }));

        var response = new CalendarResponseDto
        {
            Timezone = targetTimezone,
            UtcOffsetMinutes = utcOffsetMinutes,
            Bookings = calendarBookings,
            Items = items.OrderBy(i => i.StartDate).ThenBy(i => i.Type).ToList(),
        };

        return Ok(response);
    }

    [HttpGet("{id}/alloggiati-status")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<IActionResult> GetAlloggiatiStatus(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        var status = await alloggiatiWebService.GetStatusAsync(id);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    // One guest snapshot per host booking (#431), owned by the property's org (TN-1): never a lookup by
    // e-mail, so a booking can neither reuse nor reveal a guest of another org.
    private static Guest NewGuestSnapshot(Guid orgId, CreateBookingGuestRequest guestInfo) => new()
    {
        OrgId = orgId,
        FirstName = guestInfo.FirstName,
        LastName = guestInfo.LastName,
        Email = guestInfo.Email,
        PhoneNumber = guestInfo.Phone,
        Country = guestInfo.Country,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IReadOnlyList<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
