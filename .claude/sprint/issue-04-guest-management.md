## User Story

As a **property owner**, I want to **manage guest profiles** (create, view, update guest information), so that I **maintain accurate records and can pre-populate booking data for returning guests**.

## Context

`Guest` entity exists but no repository, service, or controller. Critical for booking flow and compliance (Alloggiati Web reporting requires detailed guest data).

## Technical Details

### Files to Create

1. **Casazen.Core/Repositories/IGuestRepository.cs**
```csharp
public interface IGuestRepository
{
    Task<Guest?> GetByIdAsync(Guid id);
    Task<Guest?> GetByEmailAsync(string email);
    Task<IEnumerable<Guest>> GetAllAsync();
    Task<IEnumerable<Guest>> SearchAsync(string searchTerm);
    Task<Guest> AddAsync(Guest guest);
    Task<Guest> UpdateAsync(Guest guest);
    Task DeleteAsync(Guid id);
}
```

2. **Casazen.Infrastructure/Repositories/GuestRepository.cs**
```csharp
public class GuestRepository : IGuestRepository
{
    private readonly AppDbContext _context;

    public GuestRepository(AppDbContext context) => _context = context;

    public async Task<Guest?> GetByIdAsync(Guid id)
        => await _context.Guests
            .Include(g => g.Bookings)
            .FirstOrDefaultAsync(g => g.Id == id);

    public async Task<Guest?> GetByEmailAsync(string email)
        => await _context.Guests.FirstOrDefaultAsync(g => g.Email == email);

    public async Task<IEnumerable<Guest>> GetAllAsync()
        => await _context.Guests.ToListAsync();

    public async Task<IEnumerable<Guest>> SearchAsync(string searchTerm)
    {
        var term = searchTerm.ToLower();
        return await _context.Guests
            .Where(g => g.FirstName.ToLower().Contains(term) ||
                       g.LastName.ToLower().Contains(term) ||
                       g.Email.ToLower().Contains(term))
            .ToListAsync();
    }

    public async Task<Guest> AddAsync(Guest guest)
    {
        _context.Guests.Add(guest);
        await _context.SaveChangesAsync();
        return guest;
    }

    public async Task<Guest> UpdateAsync(Guest guest)
    {
        guest.UpdatedAt = DateTime.UtcNow;
        _context.Guests.Update(guest);
        await _context.SaveChangesAsync();
        return guest;
    }

    public async Task DeleteAsync(Guid id)
    {
        var guest = await GetByIdAsync(id);
        if (guest != null)
        {
            _context.Guests.Remove(guest);
            await _context.SaveChangesAsync();
        }
    }
}
```

3. **Casazen.Core/Services/IGuestService.cs**
```csharp
public interface IGuestService
{
    Task<Guest?> GetGuestAsync(Guid id);
    Task<Guest?> GetByEmailAsync(string email);
    Task<IEnumerable<Guest>> GetAllGuestsAsync();
    Task<IEnumerable<Guest>> SearchGuestsAsync(string searchTerm);
    Task<Guest> CreateGuestAsync(Guest guest);
    Task<Guest> UpdateGuestAsync(Guest guest);
    Task<bool> DeleteGuestAsync(Guid id);
}
```

4. **Casazen.Infrastructure/Services/GuestService.cs**
```csharp
public class GuestService : IGuestService
{
    private readonly IGuestRepository _guestRepository;
    private readonly ILogger<GuestService> _logger;

    public GuestService(IGuestRepository guestRepository, ILogger<GuestService> logger)
    {
        _guestRepository = guestRepository;
        _logger = logger;
    }

    public async Task<Guest?> GetGuestAsync(Guid id)
        => await _guestRepository.GetByIdAsync(id);

    public async Task<Guest?> GetByEmailAsync(string email)
        => await _guestRepository.GetByEmailAsync(email);

    public async Task<IEnumerable<Guest>> GetAllGuestsAsync()
        => await _guestRepository.GetAllAsync();

    public async Task<IEnumerable<Guest>> SearchGuestsAsync(string searchTerm)
        => await _guestRepository.SearchAsync(searchTerm);

    public async Task<Guest> CreateGuestAsync(Guest guest)
    {
        // Check for duplicate email
        var existing = await _guestRepository.GetByEmailAsync(guest.Email);
        if (existing != null)
        {
            _logger.LogWarning("Guest with email {Email} already exists, returning existing", guest.Email);
            return existing; // Return existing instead of error (for booking convenience)
        }

        return await _guestRepository.AddAsync(guest);
    }

    public async Task<Guest> UpdateGuestAsync(Guest guest)
    {
        var existing = await _guestRepository.GetByIdAsync(guest.Id);
        if (existing == null)
            throw new KeyNotFoundException($"Guest with ID {guest.Id} not found");

        return await _guestRepository.UpdateAsync(guest);
    }

    public async Task<bool> DeleteGuestAsync(Guid id)
    {
        await _guestRepository.DeleteAsync(id);
        return true;
    }
}
```

5. **Casazen.Web/Controllers/GuestsController.cs**
```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GuestsController : ControllerBase
{
    private readonly IGuestService _guestService;

    public GuestsController(IGuestService guestService)
    {
        _guestService = guestService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Guest>>> GetAll()
    {
        var guests = await _guestService.GetAllGuestsAsync();
        return Ok(guests);
    }

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<Guest>>> Search([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Search query cannot be empty");

        var guests = await _guestService.SearchGuestsAsync(query);
        return Ok(guests);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Guest>> GetById(Guid id)
    {
        var guest = await _guestService.GetGuestAsync(id);
        if (guest == null)
            return NotFound();

        return Ok(guest);
    }

    [HttpPost]
    public async Task<ActionResult<Guest>> Create([FromBody] CreateGuestRequest request)
    {
        var guest = new Guest
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Address = request.Address,
            City = request.City,
            PostalCode = request.PostalCode,
            Country = request.Country,
            Notes = request.Notes
        };

        var created = await _guestService.CreateGuestAsync(guest);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Guest>> Update(Guid id, [FromBody] UpdateGuestRequest request)
    {
        var guest = await _guestService.GetGuestAsync(id);
        if (guest == null)
            return NotFound();

        guest.FirstName = request.FirstName;
        guest.LastName = request.LastName;
        guest.Email = request.Email;
        guest.PhoneNumber = request.PhoneNumber;
        guest.Address = request.Address;
        guest.City = request.City;
        guest.PostalCode = request.PostalCode;
        guest.Country = request.Country;
        guest.Notes = request.Notes;

        var updated = await _guestService.UpdateGuestAsync(guest);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _guestService.DeleteGuestAsync(id);
        return NoContent();
    }
}
```

6. **DTOs** (Casazen.Web/DTOs/Guests/)
```csharp
public record CreateGuestRequest(
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    string? Address,
    string? City,
    string? PostalCode,
    string? Country,
    string? Notes
);

public record UpdateGuestRequest(
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    string? Address,
    string? City,
    string? PostalCode,
    string? Country,
    string? Notes
);
```

## Acceptance Criteria

- [ ] IGuestRepository and GuestRepository implemented
- [ ] IGuestService and GuestService implemented
- [ ] GuestsController with full CRUD
- [ ] GET /api/guests returns all guests
- [ ] GET /api/guests/search?query=john returns matching guests
- [ ] POST /api/guests creates new guest (or returns existing if email matches)
- [ ] PUT /api/guests/{id} updates guest
- [ ] DELETE /api/guests/{id} deletes guest
- [ ] Unit tests for GuestService (all methods)
- [ ] Integration tests for GuestsController
- [ ] Guest profile includes booking history (via navigation property)

## Definition of Done

- [ ] All files created
- [ ] DI registration in Program.cs
- [ ] Unit tests pass (80%+ coverage)
- [ ] Integration tests pass
- [ ] Swagger documentation
- [ ] Code reviewed

## Estimated Effort

**3-4 days**

## Priority

🔥 **CRITICAL** - Required for bookings

## Dependencies

None
