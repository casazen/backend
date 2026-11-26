using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/staytour/como")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public sealed class ComoStayTourController(
    AppDbContext db,
    IHostResourceLookup hostResources,
    IAuthorizationService authorization,
    IStayTouristTaxService stayTax) : ControllerBase
{
    [HttpGet("{month}")]
    public async Task<ActionResult<ComoMonthlyDeclaration>> Get(Guid propertyId, string month, CancellationToken ct)
    {
        var resource = await hostResources.ForPropertyAsync(propertyId, ct);
        if (resource is null || !await authorization.IsAuthorizedAsync(User, resource, PaymentOperations.Read))
            return NotFound();
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day.Year is < 2000 or > 2100)
            return BadRequest();
        var property = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId)
            .Select(p => new { p.Country, p.ComuneIstatCode, p.City }).SingleOrDefaultAsync(ct);
        if (property is null || property.Country != "IT" ||
            !(string.IsNullOrWhiteSpace(property.ComuneIstatCode)
                ? string.Equals(property.City?.Trim(), "Como", StringComparison.OrdinalIgnoreCase)
                : property.ComuneIstatCode == "013075"))
            return NotFound();

        var departures = await ComoMonthlyDeclaration.DeparturesInMonth(db.Bookings.AsNoTracking(), resource.OrgId, propertyId, day)
            .Select(b => new { b.Id, b.NumberOfGuests, b.Status, b.CheckedOutAt, b.CheckOutDate }).ToListAsync(ct);

        var canReadReasons = await authorization.IsAuthorizedAsync(User, resource, GuestOperations.Read);
        var report = new ComoMonthlyDeclaration();
        foreach (var stay in departures)
        {
            if (report.CanIncludeDeparture(stay.Status, stay.CheckedOutAt, stay.CheckOutDate))
                report.Add(await stayTax.GetAsync(stay.Id, ct), stay.NumberOfGuests, canReadReasons);
        }
        return Ok(report);
    }
}
