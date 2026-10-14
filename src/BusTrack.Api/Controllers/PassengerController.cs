using BusTrack.Api.Auth;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Controllers;

[ApiController]
[Route("api")]
public class PassengerController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TrackingService _tracking;

    public PassengerController(AppDbContext db, TrackingService tracking)
    {
        _db = db;
        _tracking = tracking;
    }

    [HttpGet("stops")]
    public async Task<IActionResult> Stops(CancellationToken ct)
    {
        var stops = await _db.Stops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        return Ok(stops.Select(s => new
        {
            s.Id,
            s.Name,
            s.Code,
            s.City,
            s.Address,
            latitude = s.Latitude,
            longitude = s.Longitude
        }));
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string from, [FromQuery] string to, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return BadRequest(new { message = "Choose where you are leaving from and where you are going." });
        if (from.Trim().Equals(to.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Pick two different stops." });

        var origin = await _tracking.FindStop(from, ct);
        var dest = await _tracking.FindStop(to, ct);
        if (origin is null) return BadRequest(new { message = $"No stop matches \"{from.Trim()}\"." });
        if (dest is null) return BadRequest(new { message = $"No stop matches \"{to.Trim()}\"." });

        var day = date ?? IndiaClock.Today();
        var results = await _tracking.SearchAsync(origin.Name, dest.Name, day, ct);
        return Ok(new { from = origin.Name, to = dest.Name, date = day.ToString("yyyy-MM-dd"), results });
    }

    [HttpGet("track/{fleet}")]
    public async Task<IActionResult> Track(string fleet, [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var snap = await _tracking.GetByFleetAsync(fleet, from, to, ct);
        if (snap is null) return NotFound(new { message = $"No bus matches {fleet}." });
        return Ok(snap);
    }

    [HttpGet("live")]
    public async Task<IActionResult> Live(CancellationToken ct) => Ok(await _tracking.LiveAsync(ct));

    [Authorize]
    [HttpGet("favorites")]
    public async Task<IActionResult> Favorites(CancellationToken ct)
    {
        var rows = await _db.Favorites.AsNoTracking()
            .Include(f => f.Bus)
            .Include(f => f.Route)
            .Include(f => f.FromStop)
            .Include(f => f.ToStop)
            .Where(f => f.UserId == User.Id())
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(f => new
        {
            f.Id,
            f.BusId,
            fleetNumber = f.Bus?.FleetNumber,
            registrationNumber = f.Bus?.RegistrationNumber,
            f.RouteId,
            routeCode = f.Route?.Code,
            fromStop = f.FromStop?.Name,
            toStop = f.ToStop?.Name,
            f.FromStopId,
            f.ToStopId,
            f.Label,
            createdAt = IndiaClock.AsUtc(f.CreatedAt)
        }));
    }

    [Authorize]
    [HttpPost("favorites")]
    public async Task<IActionResult> AddFavorite(FavoriteRequest request, CancellationToken ct)
    {
        if (request.BusId is null && request.RouteId is null)
            return BadRequest(new { message = "Choose a bus or a route to save." });
        var row = new Favorite
        {
            UserId = User.Id(),
            BusId = request.BusId,
            RouteId = request.RouteId,
            FromStopId = request.FromStopId,
            ToStopId = request.ToStopId,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.Favorites.Add(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id });
    }

    [Authorize]
    [HttpDelete("favorites/{id:int}")]
    public async Task<IActionResult> RemoveFavorite(int id, CancellationToken ct)
    {
        var row = await _db.Favorites.FirstOrDefaultAsync(f => f.Id == id && f.UserId == User.Id(), ct);
        if (row is null) return NotFound(new { message = "That favourite is not on your list." });
        _db.Favorites.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize]
    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications(CancellationToken ct)
    {
        var rows = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == User.Id())
            .OrderByDescending(n => n.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        var unread = rows.Count(n => !n.IsRead);
        return Ok(new
        {
            unread,
            items = rows.Select(n => new
            {
                n.Id,
                n.TripId,
                n.Title,
                n.Body,
                n.Kind,
                n.IsRead,
                createdAt = IndiaClock.AsUtc(n.CreatedAt)
            })
        });
    }

    [Authorize]
    [HttpPost("notifications/{id:int}/read")]
    public async Task<IActionResult> Read(int id, CancellationToken ct)
    {
        var row = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == User.Id(), ct);
        if (row is null) return NotFound(new { message = "Notification not found." });
        row.IsRead = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize]
    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        var rows = await _db.Notifications.Where(n => n.UserId == User.Id() && !n.IsRead).ToListAsync(ct);
        foreach (var row in rows) row.IsRead = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
