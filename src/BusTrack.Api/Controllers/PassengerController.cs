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
        var uniqueStops = await _tracking.GetActiveStopsAsync(ct);
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
        var plan = await _tracking.PlanJourneyAsync(origin.Name, dest.Name, day, ct: ct);
        return Ok(new
        {
            from = search.From,
            to = search.To,
            date = search.EffectiveDate.ToString("yyyy-MM-dd"),
            requestedDate = search.RequestedDate.ToString("yyyy-MM-dd"),
            showingNextDate = search.ShowingNextDate,
            results = search.Results,
            journeyPlan = plan
        });
    }

    [HttpGet("journey-planner")]
    public async Task<IActionResult> JourneyPlanner([FromQuery] string from, [FromQuery] string to, [FromQuery] DateOnly? date, CancellationToken ct)
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
        var plan = await _tracking.PlanJourneyAsync(origin.Name, dest.Name, day, ct: ct);
        return Ok(plan);
    }

    [HttpGet("track/{fleet}")]
    public async Task<IActionResult> Track(
        string fleet,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? routeId,
        [FromQuery] DateOnly? date,
        [FromQuery] int? tripId,
        CancellationToken ct)
    {
        var snap = await _tracking.GetByFleetAsync(fleet, from, to, routeId, ct, date, tripId);
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

    [HttpGet("tracking/filters")]
    public async Task<IActionResult> TrackingFilters(CancellationToken ct = default)
    {
        var options = await _tracking.GetTrackingFilterOptionsAsync(ct);
        return Ok(options);
    }

    [HttpGet("tracking/live")]
    public async Task<IActionResult> TrackingLive([FromQuery(Name = "")] TrackingFilterQuery? query = null, CancellationToken ct = default)
    {
        var items = await _tracking.GetLiveTrackingAsync(filter: query?.ToFilterDto(), ct: ct);
        return Ok(items);
    }

    [HttpGet("tracking/recent")]
    public async Task<IActionResult> TrackingRecent(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 9,
        [FromQuery(Name = "")] TrackingFilterQuery? query = null,
        CancellationToken ct = default)
    {
        var result = await _tracking.GetRecentTrackingAsync(page, pageSize, query?.ToFilterDto(), ct);
        return Ok(result);
    }

    [HttpGet("tracking/upcoming")]
    public async Task<IActionResult> TrackingUpcoming(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 9,
        [FromQuery] int daysAhead = 6,
        [FromQuery(Name = "")] TrackingFilterQuery? query = null,
        CancellationToken ct = default)
    {
        var result = await _tracking.GetUpcomingTrackingAsync(page, pageSize, daysAhead, filter: query?.ToFilterDto(), ct: ct);
        return Ok(result);
    }

    [HttpGet("routes")]
    public async Task<IActionResult> Routes(CancellationToken ct)
    {
        var catalog = await _tracking.GetActiveRouteCatalogAsync(ct);
        var runningBuses = await _tracking.LiveAsync(ct);
        var result = new List<object>(catalog.Count);

        foreach (var route in catalog)
        {
            var busesOnRoute = runningBuses.Where(b => b.RouteId == route.Id).ToList();

            result.Add(new
            {
                route.Id,
                route.Code,
                route.Name,
                distanceKm = route.DistanceKm,
                origin = route.Origin,
                destination = route.Destination,
                stops = route.Stops.Select(s => new
                {
                    s.StopId,
                    name = s.Name,
                    code = s.Code,
                    s.SequenceNo,
                    latitude = s.Latitude,
                    longitude = s.Longitude,
                    distanceKm = s.DistanceKm
                }),
                path = route.Path.Select(p => new { latitude = p.Latitude, longitude = p.Longitude }),
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
            kind = f.BusId != null ? "bus" : f.RouteId != null ? "route" : "stop",
            f.BusId,
            fleetNumber = f.Bus?.FleetNumber,
            registrationNumber = f.Bus?.RegistrationNumber,
            f.RouteId,
            routeCode = f.Route?.Code,
            routeName = f.Route?.Name,
            fromStop = f.FromStop?.Name,
            fromStopCode = f.FromStop?.Code,
            toStop = f.ToStop?.Name,
            toStopCode = f.ToStop?.Code,
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
        if (request.BusId is null && request.RouteId is null && request.FromStopId is null)
            return BadRequest(new { message = "Choose a bus, route, or bus stop to save." });

        var existing = await _db.Favorites.FirstOrDefaultAsync(
            f => f.UserId == User.Id()
                && f.BusId == request.BusId
                && f.RouteId == request.RouteId
                && f.FromStopId == request.FromStopId
                && f.ToStopId == request.ToStopId,
            ct);
        if (existing is not null)
            return Ok(new { existing.Id, duplicate = true });

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
        return Ok(new { row.Id, duplicate = false });
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

    [Authorize]
    [HttpGet("reminders")]
    public async Task<IActionResult> Reminders(CancellationToken ct)
    {
        var rows = await _db.StopReminders.AsNoTracking()
            .Where(r => r.UserId == User.Id())
            .OrderBy(r => r.IsTriggered)
            .ThenByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return Ok(rows.Select(r => new
        {
            r.Id,
            r.TripId,
            r.RouteId,
            r.StopId,
            r.StopName,
            r.FleetNumber,
            r.RemindBeforeMinutes,
            targetDate = r.TargetDate.ToString("yyyy-MM-dd"),
            r.IsTriggered,
            triggeredAt = r.TriggeredAt is DateTime t ? IndiaClock.AsUtc(t) : (DateTime?)null,
            createdAt = IndiaClock.AsUtc(r.CreatedAt)
        }));
    }

    [Authorize]
    [HttpPost("reminders")]
    public async Task<IActionResult> CreateReminder(StopReminderRequest request, CancellationToken ct)
    {
        var stop = await _db.Stops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StopId && s.IsActive, ct);
        if (stop is null)
            return BadRequest(new { message = "Selected stop was not found." });

        var routeExists = await _db.Routes.AsNoTracking().AnyAsync(r => r.Id == request.RouteId && r.IsActive, ct);
        if (!routeExists)
            return BadRequest(new { message = "Selected route was not found." });

        var lead = Math.Clamp(request.RemindBeforeMinutes, 1, 60);
        var targetDate = request.TargetDate ?? IndiaClock.Today();
        var fleet = (request.FleetNumber ?? "").Trim();
        if (fleet.Length > 20) fleet = fleet[..20];

        var existing = await _db.StopReminders.FirstOrDefaultAsync(
            r => r.UserId == User.Id()
                && r.RouteId == request.RouteId
                && r.StopId == request.StopId
                && r.TripId == request.TripId
                && r.TargetDate == targetDate
                && !r.IsTriggered,
            ct);

        if (existing is not null)
        {
            existing.RemindBeforeMinutes = lead;
            if (!string.IsNullOrWhiteSpace(fleet)) existing.FleetNumber = fleet;
            await _db.SaveChangesAsync(ct);
            return Ok(new { existing.Id, stopName = existing.StopName, remindBeforeMinutes = existing.RemindBeforeMinutes, updated = true });
        }

        var row = new StopReminder
        {
            UserId = User.Id(),
            TripId = request.TripId,
            RouteId = request.RouteId,
            StopId = request.StopId,
            StopName = stop.Name,
            FleetNumber = fleet,
            RemindBeforeMinutes = lead,
            TargetDate = targetDate,
            CreatedAt = DateTime.UtcNow
        };
        _db.StopReminders.Add(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, stopName = row.StopName, remindBeforeMinutes = row.RemindBeforeMinutes, updated = false });
    }

    [Authorize]
    [HttpDelete("reminders/{id:int}")]
    public async Task<IActionResult> DeleteReminder(int id, CancellationToken ct)
    {
        var row = await _db.StopReminders.FirstOrDefaultAsync(r => r.Id == id && r.UserId == User.Id(), ct);
        if (row is null) return NotFound(new { message = "Reminder not found." });
        _db.StopReminders.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("reports")]
    [HttpPost("community-updates")]
    public async Task<IActionResult> SubmitBusInfoReport(BusInfoReportRequest request, CancellationToken ct)
    {
        var description = (request.Description ?? "").Trim();
        if (description.Length < 5)
            return BadRequest(new { message = "Please describe the update or issue (at least 5 characters)." });
        if (description.Length > 500)
            description = description[..500];

        var fleet = string.IsNullOrWhiteSpace(request.FleetNumber) ? "General" : request.FleetNumber.Trim();
        if (fleet.Length > 20) fleet = fleet[..20];

        var allowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DelayReport", "CancelledService", "MissingRoute", "UpdatedTiming",
            "WrongTiming", "WrongStop", "WrongRoute", "WrongBusNumber", "GpsIssue", "Other"
        };
        var rawCategory = request.Category?.Trim() ?? "";
        var category = allowedCategories.TryGetValue(rawCategory, out var canonicalCategory)
            ? canonicalCategory
            : "Other";

        var stopName = string.IsNullOrWhiteSpace(request.ReportedStopName) ? null : request.ReportedStopName.Trim();
        if (stopName is { Length: > 120 }) stopName = stopName[..120];

        var proposedTime = string.IsNullOrWhiteSpace(request.ProposedTime) ? null : request.ProposedTime.Trim();
        if (proposedTime is { Length: > 40 }) proposedTime = proposedTime[..40];

        int? delayMinutes = request.DelayMinutes is int d && d > 0 ? Math.Clamp(d, 1, 600) : null;

        Guid? userId = User?.Identity?.IsAuthenticated == true ? User.Id() : null;

        var report = new BusInfoReport
        {
            UserId = userId,
            TripId = request.TripId,
            RouteId = request.RouteId,
            BusId = request.BusId,
            FleetNumber = fleet,
            Category = category,
            ReportedStopName = stopName,
            ProposedTime = proposedTime,
            DelayMinutes = delayMinutes,
            Description = description,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.BusInfoReports.Add(report);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(userId, "ReportBusInfo", "BusInfoReport", report.Id.ToString(), $"Bus {fleet} ({category}): {description}", ct);
        return Ok(new
        {
            report.Id,
            report.Status,
            message = "Submitted for admin verification. Once approved, it will appear on the public Community Updates board."
        });
    }

    [AllowAnonymous]
    [HttpGet("community-updates")]
    public async Task<IActionResult> CommunityUpdates(
        [FromQuery] string? category = null,
        [FromQuery] int? routeId = null,
        [FromQuery] string? fleetNumber = null,
        CancellationToken ct = default)
    {
        var query = _db.BusInfoReports.AsNoTracking()
            .Where(r => r.Status == "Published" || r.Status == "Verified");

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Category == category.Trim());

        if (routeId.HasValue && routeId.Value > 0)
            query = query.Where(r => r.RouteId == routeId.Value);

        if (!string.IsNullOrWhiteSpace(fleetNumber))
        {
            var normalized = fleetNumber.Trim();
            query = query.Where(r => r.FleetNumber == normalized);
        }

        var rows = await query
            .OrderByDescending(r => r.VerifiedAt ?? r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        var routeIds = rows.Where(r => r.RouteId.HasValue).Select(r => r.RouteId!.Value).Distinct().ToList();
        var routeMap = routeIds.Count > 0
            ? await _db.Routes.AsNoTracking()
                .Where(r => routeIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => new { r.Code, r.Name }, ct)
            : new();

        return Ok(rows.Select(r =>
        {
            var route = r.RouteId.HasValue && routeMap.TryGetValue(r.RouteId.Value, out var found) ? found : null;
            return new
            {
                r.Id,
                r.Category,
                r.FleetNumber,
                r.RouteId,
                routeCode = route?.Code,
                routeName = route?.Name,
                r.TripId,
                r.ReportedStopName,
                r.ProposedTime,
                r.DelayMinutes,
                r.Description,
                r.Status,
                r.AdminNotes,
                verifiedAt = r.VerifiedAt is DateTime v ? IndiaClock.AsUtc(v) : (DateTime?)null,
                r.VerifiedBy,
                createdAt = IndiaClock.AsUtc(r.CreatedAt)
            };
        }));
    }
}

public sealed class TrackingFilterQuery
{
    public string? BusNumber { get; set; }
    public int? RouteId { get; set; }
    public string? Route { get; set; }
    public DateOnly? Date { get; set; }
    public int? OperatorId { get; set; }
    public string? Operator { get; set; }
    public string? BusType { get; set; }
    public string? Status { get; set; }
    public string? Stop { get; set; }
    public string? TimeOfDay { get; set; }

    public TrackingFilterDto? ToFilterDto()
    {
        var validRouteId = RouteId is > 0 ? RouteId : null;
        var validOperatorId = OperatorId is > 0 ? OperatorId : null;

        if (string.IsNullOrWhiteSpace(BusNumber)
            && validRouteId is null
            && string.IsNullOrWhiteSpace(Route)
            && Date is null
            && validOperatorId is null
            && string.IsNullOrWhiteSpace(Operator)
            && string.IsNullOrWhiteSpace(BusType)
            && string.IsNullOrWhiteSpace(Status)
            && string.IsNullOrWhiteSpace(Stop)
            && string.IsNullOrWhiteSpace(TimeOfDay))
        {
            return null;
        }

        return new TrackingFilterDto(
            BusNumber: BusNumber,
            RouteId: validRouteId,
            Route: Route,
            Date: Date,
            OperatorId: validOperatorId,
            Operator: Operator,
            BusType: BusType,
            Status: Status,
            Stop: Stop,
            TimeOfDay: TimeOfDay
        );
    }
}
