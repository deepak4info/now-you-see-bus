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

    [HttpPost("visitors")]
    public async Task<IActionResult> RegisterVisitor(VisitorRegistrationRequest request, CancellationToken ct)
    {
        if (request.VisitorId == Guid.Empty)
            return BadRequest(new { message = "A valid anonymous visitor ID is required." });

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO dbo.Visitors (Id, FirstSeenAt)
            SELECT {request.VisitorId}, SYSUTCDATETIME()
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.Visitors WITH (UPDLOCK, HOLDLOCK) WHERE Id = {request.VisitorId}
            );
            """, ct);

        var visitorCount = await _db.Visitors.CountAsync(ct);
        return Ok(new { visitorCount });
    }

    [HttpGet("stops")]
    public async Task<IActionResult> Stops(CancellationToken ct)
    {
        var stops = await _db.Stops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var uniqueStops = StopNormalizer.DeduplicateStopsList(stops);
        return Ok(uniqueStops.Select(s => new
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
        var search = await _tracking.SearchRouteAsync(origin.Name, dest.Name, day, ct: ct);
        return Ok(new
        {
            from = search.From,
            to = search.To,
            date = search.EffectiveDate.ToString("yyyy-MM-dd"),
            requestedDate = search.RequestedDate.ToString("yyyy-MM-dd"),
            showingNextDate = search.ShowingNextDate,
            results = search.Results
        });
    }

    [HttpGet("track/{fleet}")]
    public async Task<IActionResult> Track(
        string fleet,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? routeId,
        CancellationToken ct)
    {
        var snap = await _tracking.GetByFleetAsync(fleet, from, to, routeId, ct);
        if (snap is null) return NotFound(new { message = $"No bus matches {fleet}." });
        return Ok(snap);
    }

    [HttpGet("live")]
    public async Task<IActionResult> Live([FromQuery] bool includeScheduled = false, CancellationToken ct = default)
    {
        var liveList = await _tracking.LiveAsync(ct);
        if (liveList.Count == 0 && includeScheduled)
        {
            var scheduledRoutes = await _tracking.GetAllActiveRouteSnapshotsAsync(ct);
            return Ok(scheduledRoutes);
        }
        return Ok(liveList);
    }

    [HttpGet("routes")]
    public async Task<IActionResult> Routes(CancellationToken ct)
    {
        var routes = await _db.Routes.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
        var routeIds = routes.Select(r => r.Id).ToList();
        var routeStops = await _db.RouteStops.AsNoTracking()
            .Where(rs => routeIds.Contains(rs.RouteId))
            .Include(rs => rs.Stop)
            .OrderBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        var runningBuses = await _tracking.LiveAsync(ct);
        var result = new List<object>();

        foreach (var route in routes)
        {
            var stops = routeStops.Where(rs => rs.RouteId == route.Id).OrderBy(rs => rs.SequenceNo).ToList();
            if (stops.Count == 0) continue;

            var path = await _tracking.LoadPath(route.Id, ct);
            var busesOnRoute = runningBuses.Where(b => b.RouteId == route.Id).ToList();

            result.Add(new
            {
                route.Id,
                route.Code,
                route.Name,
                distanceKm = route.DistanceKm ?? stops.LastOrDefault()?.DistanceFromOriginKm,
                origin = stops.First().Stop.Name,
                destination = stops.Last().Stop.Name,
                stops = stops.Select(s => new
                {
                    s.StopId,
                    name = s.Stop.Name,
                    code = s.Stop.Code,
                    s.SequenceNo,
                    latitude = (double)s.Stop.Latitude,
                    longitude = (double)s.Stop.Longitude,
                    distanceKm = (double)s.DistanceFromOriginKm
                }),
                path = path.Select(p => new { latitude = p.Lat, longitude = p.Lng }),
                liveCount = busesOnRoute.Count,
                hasLiveBus = busesOnRoute.Count > 0,
                runningBuses = busesOnRoute
            });
        }

        return Ok(result);
    }

    [HttpGet("routes/{id:int}/snapshot")]
    public async Task<IActionResult> RouteSnapshot(int id, CancellationToken ct)
    {
        var snap = await _tracking.GetRouteSnapshotAsync(id, ct);
        if (snap is null) return NotFound(new { message = $"Route {id} not found." });
        return Ok(snap);
    }

    [HttpGet("trips/{tripId:int}/eta")]
    public async Task<IActionResult> GetTripEta(int tripId, CancellationToken ct)
    {
        var response = await _tracking.GetTripEtasAsync(tripId, ct);
        if (response is null) return NotFound(new { message = $"Trip {tripId} not found." });
        return Ok(response);
    }

    [HttpPost("trips/{tripId:int}/recalculate-eta")]
    public async Task<IActionResult> RecalculateEta(int tripId, CancellationToken ct)
    {
        var snap = await _tracking.RecalculateTripEtasAsync(tripId, ct);
        return Ok(snap);
    }

    [HttpPost("trips/{tripId:int}/gps")]
    public async Task<IActionResult> IngestGps(int tripId, LocationRequest request, CancellationToken ct)
    {
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { message = "That location is not valid." });
        var snap = await _tracking.IngestAsync(tripId, request.Latitude, request.Longitude, request.SpeedKmph, request.Heading, false, ct);
        return Ok(snap);
    }

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
