using BusTrack.Api.Auth;
using BusTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _passwords;
    private readonly JwtIssuer _jwt;

    public AuthController(AppDbContext db, PasswordHasher<User> passwords, JwtIssuer jwt)
    {
        _db = db;
        _passwords = passwords;
        _jwt = jwt;
    }

    [EnableRateLimiting("auth-limit")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var login = request.Login?.Trim() ?? string.Empty;
        if (login.Length == 0 || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Enter your email or driver ID, and a password." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == login || u.DriverCode == login, ct);
        if (user is null || _passwords.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Those credentials are not valid." });
        if (!user.IsActive)
            return Unauthorized(new { message = "This account is disabled." });

        return Ok(Token(user));
    }

    [EnableRateLimiting("auth-limit")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var name = request.FullName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        if (name.Length < 2) return BadRequest(new { message = "Enter your name." });
        if (!email.Contains('@') || email.Length > 256) return BadRequest(new { message = "Enter a valid email." });
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8) return BadRequest(new { message = "Use a password of at least 8 characters." });
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return BadRequest(new { message = "An account with that email already exists." });

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = name,
            Email = email,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Role = "Passenger",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = _passwords.HashPassword(user, request.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return Ok(Token(user));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == User.Id(), ct);
        if (user is null || !user.IsActive) return Unauthorized(new { message = "Sign in again." });
        return Ok(new { user.FullName, user.Email, user.Role, user.DriverCode, user.Phone });
    }

    private AuthResponse Token(User user) =>
        new(_jwt.Create(user), user.FullName, user.Email, user.Role, user.DriverCode);
}
