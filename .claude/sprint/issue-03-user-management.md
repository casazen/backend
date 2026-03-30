## User Story

As a **system user**, I want **role-based access control** distinguishing property owners, guests, admins, and staff, so that **each user sees only authorized data and functionality**.

## Context

Currently, `User` entity exists but no repositories/services. Auth0 integration present but no user CRUD operations. Need complete user management with authorization.

## Technical Details

### Files to Create

1. **Casazen.Core/Repositories/IUserRepository.cs**
```csharp
public interface IUserRepository
{
    Task<User?> GetByIdAsync(string id);
    Task<User?> GetByEmailAsync(string email);
    Task<IEnumerable<User>> GetAllAsync();
    Task<User> AddAsync(User user);
    Task<User> UpdateAsync(User user);
    Task DeleteAsync(string id);
    Task<bool> ExistsAsync(string id);
}
```

2. **Casazen.Infrastructure/Repositories/UserRepository.cs**
```csharp
public class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context) => _context = context;

    public async Task<User?> GetByIdAsync(string id)
        => await _context.Users.FindAsync(id);

    public async Task<User?> GetByEmailAsync(string email)
        => await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

    public async Task<IEnumerable<User>> GetAllAsync()
        => await _context.Users.ToListAsync();

    public async Task<User> AddAsync(User user)
    {
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    public async Task<User> UpdateAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        _context.Users.Update(user);
        await _context.SaveChangesAsync();
        return user;
    }

    public async Task DeleteAsync(string id)
    {
        var user = await GetByIdAsync(id);
        if (user != null)
        {
            user.IsActive = false; // Soft delete
            await UpdateAsync(user);
        }
    }

    public async Task<bool> ExistsAsync(string id)
        => await _context.Users.AnyAsync(u => u.Id == id);
}
```

3. **Casazen.Core/Services/IUserService.cs**
```csharp
public interface IUserService
{
    Task<User?> GetUserAsync(string id);
    Task<User?> GetByEmailAsync(string email);
    Task<IEnumerable<User>> GetAllUsersAsync();
    Task<User> CreateUserAsync(User user);
    Task<User> UpdateUserAsync(User user);
    Task<bool> DeleteUserAsync(string id);
}
```

4. **Casazen.Infrastructure/Services/UserService.cs**
```csharp
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UserService> _logger;

    public UserService(IUserRepository userRepository, ILogger<UserService> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task<User?> GetUserAsync(string id)
    {
        return await _userRepository.GetByIdAsync(id);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _userRepository.GetByEmailAsync(email);
    }

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        return await _userRepository.GetAllAsync();
    }

    public async Task<User> CreateUserAsync(User user)
    {
        // Validate email uniqueness
        var existing = await _userRepository.GetByEmailAsync(user.Email);
        if (existing != null)
            throw new InvalidOperationException($"User with email {user.Email} already exists");

        return await _userRepository.AddAsync(user);
    }

    public async Task<User> UpdateUserAsync(User user)
    {
        var existing = await _userRepository.GetByIdAsync(user.Id);
        if (existing == null)
            throw new KeyNotFoundException($"User with ID {user.Id} not found");

        return await _userRepository.UpdateAsync(user);
    }

    public async Task<bool> DeleteUserAsync(string id)
    {
        await _userRepository.DeleteAsync(id);
        return true;
    }
}
```

5. **Casazen.Web/Controllers/UsersController.cs**
```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet("me")]
    public async Task<ActionResult<User>> GetCurrentUser()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null)
            return Unauthorized();

        var user = await _userService.GetUserAsync(userId);
        if (user == null)
            return NotFound();

        return Ok(user);
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<User>>> GetAll()
    {
        var users = await _userService.GetAllUsersAsync();
        return Ok(users);
    }

    [HttpGet("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<User>> GetById(string id)
    {
        var user = await _userService.GetUserAsync(id);
        if (user == null)
            return NotFound();

        return Ok(user);
    }

    [HttpPut("me")]
    public async Task<ActionResult<User>> UpdateProfile([FromBody] UpdateUserRequest request)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null)
            return Unauthorized();

        var user = await _userService.GetUserAsync(userId);
        if (user == null)
            return NotFound();

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        user.PhoneNumber = request.PhoneNumber;

        var updated = await _userService.UpdateUserAsync(user);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string id)
    {
        await _userService.DeleteUserAsync(id);
        return NoContent();
    }
}
```

6. **Update Program.cs (DI registration)**
```csharp
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
```

7. **Authorization Middleware Enhancement**
```csharp
// Casazen.Web/Middleware/ResourceAuthorizationMiddleware.cs
public class ResourceAuthorizationMiddleware
{
    // Ensures property owners can only access their own properties
    // Ensures guests can only see their own bookings
}
```

## Acceptance Criteria

- [ ] IUserRepository and UserRepository implemented
- [ ] IUserService and UserService implemented
- [ ] UsersController with CRUD endpoints
- [ ] GET /api/users/me returns authenticated user profile
- [ ] PUT /api/users/me allows user to update their profile
- [ ] GET /api/users (admin only) returns all users
- [ ] DELETE /api/users/{id} (admin only) soft-deletes user
- [ ] Role-based authorization: Admin can see all, PropertyOwner sees only their data
- [ ] Unit tests for UserService (all methods)
- [ ] Integration tests for UsersController endpoints

## Definition of Done

- [ ] All files created and code complete
- [ ] DI registration in Program.cs
- [ ] Unit tests pass (80%+ coverage)
- [ ] Integration tests pass
- [ ] Swagger documentation updated
- [ ] Code reviewed

## Estimated Effort

**5-7 days**

## Priority

⚠️ **HIGH** - Foundation for authorization

## Dependencies

- Issue #9 (Fix User ID Type Mismatch) - should be completed first
