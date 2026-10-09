using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's agenda (SP-03): weekly working hours, time off, blocks and extra openings, the rules (buffer, jobs a day,
/// notice, horizon, slot step) and the calendar that puts them together with the requests. Every route is scoped to the
/// caller's own supplier org (its supplier link, never a value of the request): a time off or a block of another supplier is
/// 404. Not behind a feature flag. The per-day override (<c>GET/PUT api/supplier/availability</c>) and the calendar sync
/// (<c>api/supplier/calendar/status|ical|sync</c>) are in <see cref="SupplierProfileController"/> and do not change.
/// Runbook <c>docs/runbooks/suppliers.md</c> section 20.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org, <c>supplier_time_off_not_found</c> and
/// <c>supplier_block_not_found</c>; 400 <c>validation_error</c> for a malformed body and for a calendar range that is
/// reversed or longer than 62 days; 422 <c>supplier_hours_invalid</c>, <c>supplier_time_off_invalid</c>,
/// <c>supplier_block_invalid</c> and <c>supplier_rules_invalid</c> (with <c>fields</c>: the names of the fields at fault),
/// <c>supplier_time_off_limit_reached</c> and <c>supplier_block_limit_reached</c>.
/// </remarks>
[ApiController]
[Route("api/supplier")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierAgendaController(
    ISupplierAgendaService agenda,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // ─── Weekly working hours ────────────────────────────────────────────────────

    /// <summary>The weekly working hours: seven days, Monday first, each with its bands (none is a rest day).</summary>
    [HttpGet("availability/hours")]
    [ProducesResponseType(typeof(SupplierHoursDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierHoursDto>> GetHours(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierAgendaMapper.ToDto(await agenda.GetHoursAsync(orgId, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Replaces the weekly working hours: the days sent, up to 3 bands each (minutes after midnight on the wall clock of
    /// Rome); a weekday that is not sent is a rest day. 422 <c>supplier_hours_invalid</c> (with <c>fields</c>) for bands that
    /// overlap, an end that is not after its start, more than 3 bands in a day or a repeated weekday.
    /// </summary>
    [HttpPut("availability/hours")]
    [ProducesResponseType(typeof(SupplierHoursDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierHoursDto>> ReplaceHours(
        [FromBody] SaveSupplierHoursRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierAgendaMapper.ToDto(
                await agenda.ReplaceHoursAsync(orgId, SupplierAgendaMapper.ToInput(request), cancellationToken))),
            cancellationToken);

    // ─── Time off ────────────────────────────────────────────────────────────────

    /// <summary>The time off that has not ended yet (last day today or later), by first day, with the limit per supplier.</summary>
    [HttpGet("availability/time-off")]
    [ProducesResponseType(typeof(SupplierTimeOffListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierTimeOffListResponse>> ListTimeOff(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var items = await agenda.ListTimeOffAsync(orgId, cancellationToken);
                return Ok(new SupplierTimeOffListResponse
                {
                    Items = items.Select(entry => SupplierAgendaMapper.ToDto(entry)).ToList(),
                    Total = items.Count,
                });
            },
            cancellationToken);

    /// <summary>
    /// Adds a time off: every day from <c>fromDate</c> to <c>toDate</c> (both included) is closed to new bookings; the jobs
    /// already accepted stay. 201 with the entry. 422 <c>supplier_time_off_invalid</c> (with <c>fields</c>) or
    /// <c>supplier_time_off_limit_reached</c>.
    /// </summary>
    [HttpPost("availability/time-off")]
    [ProducesResponseType(typeof(SupplierTimeOffDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierTimeOffDto>> AddTimeOff(
        [FromBody] CreateSupplierTimeOffRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var entry = await agenda.AddTimeOffAsync(orgId, SupplierAgendaMapper.ToInput(request), cancellationToken);
                return Created($"/api/supplier/availability/time-off/{entry.Id}", SupplierAgendaMapper.ToDto(entry));
            },
            cancellationToken);

    /// <summary>Deletes a time off: its days are open again. 204; 404 <c>supplier_time_off_not_found</c> for another supplier's or a deleted one.</summary>
    [HttpDelete("availability/time-off/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTimeOff(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                await agenda.DeleteTimeOffAsync(orgId, id, cancellationToken);
                return NoContent();
            },
            cancellationToken);

    // ─── Blocks and extra openings ───────────────────────────────────────────────

    /// <summary>The blocks and extra openings set by hand that have not ended yet, by start, with the limit per supplier.</summary>
    [HttpGet("availability/blocks")]
    [ProducesResponseType(typeof(SupplierBlockListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierBlockListResponse>> ListBlocks(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var items = await agenda.ListBlocksAsync(orgId, cancellationToken);
                return Ok(new SupplierBlockListResponse
                {
                    Items = items.Select(window => SupplierAgendaMapper.ToDto(window)).ToList(),
                    Total = items.Count,
                });
            },
            cancellationToken);

    /// <summary>
    /// Blocks hours (<c>Block</c>: no slot overlaps them) or opens hours on top of the weekly ones (<c>ExtraOpening</c>, inside
    /// one day of Rome). Instants in UTC. 201 with the block. 422 <c>supplier_block_invalid</c> (with <c>fields</c>) or
    /// <c>supplier_block_limit_reached</c>.
    /// </summary>
    [HttpPost("availability/blocks")]
    [ProducesResponseType(typeof(SupplierBlockDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierBlockDto>> AddBlock(
        [FromBody] CreateSupplierBlockRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var window = await agenda.AddBlockAsync(orgId, SupplierAgendaMapper.ToInput(request), cancellationToken);
                return Created($"/api/supplier/availability/blocks/{window.Id}", SupplierAgendaMapper.ToDto(window));
            },
            cancellationToken);

    /// <summary>
    /// Deletes a block or extra opening the supplier set by hand. 204; 404 <c>supplier_block_not_found</c> for another
    /// supplier's, a deleted one, or an engagement of the supplier's calendar feed (that one comes back at the next sync).
    /// </summary>
    [HttpDelete("availability/blocks/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBlock(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                await agenda.DeleteBlockAsync(orgId, id, cancellationToken);
                return NoContent();
            },
            cancellationToken);

    // ─── Rules ───────────────────────────────────────────────────────────────────

    /// <summary>The rules of the agenda; the defaults while the supplier never saved any (a read writes nothing).</summary>
    [HttpGet("availability/rules")]
    [ProducesResponseType(typeof(SupplierRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierRulesDto>> GetRules(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierAgendaMapper.ToDto(await agenda.GetRulesAsync(orgId, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Replaces the five rules (all of them are needed). 422 <c>supplier_rules_invalid</c> (with <c>fields</c>) for a rule
    /// that is missing or outside its limits.
    /// </summary>
    [HttpPut("availability/rules")]
    [ProducesResponseType(typeof(SupplierRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierRulesDto>> ReplaceRules(
        [FromBody] SaveSupplierRulesRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierAgendaMapper.ToDto(
                await agenda.ReplaceRulesAsync(orgId, SupplierAgendaMapper.ToInput(request), cancellationToken))),
            cancellationToken);

    // ─── Calendar ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the supplier's calendar shows from <paramref name="from"/> to <paramref name="to"/> (Europe/Rome days, both
    /// included; today and the next 30 days when left out; at most 62 days): the weekly working hours, the days closed (by
    /// hand or by the calendar feed), the time off, the blocks, extra openings and engagements that touch the range, and the
    /// requests of the supplier (SP-04): one with a time carries its <c>startUtc</c> and <c>endUtc</c> and falls on the
    /// Europe/Rome day of its start, one without is a whole-day item on the check-out day of its stay; a rejected or cancelled
    /// request is not listed. 400 <c>validation_error</c> for a range that is reversed or longer than 62 days.
    /// </summary>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(SupplierCalendarDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierCalendarDto>> GetCalendar(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var rangeFrom = from ?? _clock.TodayInRomeAsDateOnly();
        var rangeTo = to ?? rangeFrom.AddDays(SupplierAgendaLimits.DefaultCalendarDays - 1);

        if (rangeTo < rangeFrom)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierAvailabilityRangeInvalid");
        if (rangeTo.DayNumber - rangeFrom.DayNumber + 1 > SupplierAgendaLimits.MaxCalendarDays)
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                ProblemCodes.ValidationError,
                "SupplierAvailabilityRangeTooLong",
                SupplierAgendaLimits.MaxCalendarDays);

        return await RunAsync(
            async orgId => Ok(SupplierAgendaMapper.ToDto(await agenda.GetCalendarAsync(orgId, rangeFrom, rangeTo, cancellationToken))),
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="action"/> for the caller's linked supplier org. The org comes only from the caller's own supplier
    /// link (<see cref="ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync"/>): the agenda never provisions a supplier
    /// org, it is business data. A rule of the agenda that names its fields is answered with them (<c>fields</c>).
    /// </summary>
    private async Task<ActionResult> RunAsync(Func<Guid, Task<ActionResult>> action, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        try
        {
            return await action(orgId.Value);
        }
        catch (SupplierAgendaRuleException ex)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey, ex.MessageArgs);
            problem.Extensions["fields"] = ex.Fields;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }
    }
}
