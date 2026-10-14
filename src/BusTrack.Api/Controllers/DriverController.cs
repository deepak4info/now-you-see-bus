using BusTrack.Api.Auth;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Controllers;

[ApiController]
[Authorize(Roles = "Driver")]
[Route("api/driver")]
public class DriverController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TrackingService _tracking;

    public DriverController(AppDbContext db, TrackingService tracking)
    {
        _db = db;
        _tracking = tracking;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var driver = await _db.Drivers.Include(d => d.User).FirstOrDefaultAsync(d => d.UserId == User.Id(), ct);
        if (driver is null) return Unauthorized(new { message = "This login is not linked to a driver." });

        var today = IndiaClock.Today();
        var trips = await _db.Trips
            .Include(t => t.Bus).ThenInclude(b => b.Operator)
            .Include(t => t.Route)
            .Include(t => t.Schedule)
            .Where(t => t.DriverId == driver.Id && t.ServiceDate == today)
            .OrderBy(t => t.Schedule.DepartureTime)
            .ToListAsync(ct);

        var bus = trips.FirstOrDefault()?.Bus
            ?? await _db.Buses.Include(b => b.Operator).FirstOrDefaultAsync(b => b.AssignedDriverId == driver.Id && b.IsActive, ct);

        var routeId = trips.FirstOrDefault()?.RouteId;
        var routeStops = routeId is null
            ? new List<RouteStop>()
            : await _db.RouteStops.Include(rs => rs.Stop).Where(rs => rs.RouteId == routeId).OrderBy(rs => rs.SequenceNo).ToListAsync(ct);

        return Ok(new
        {
            driverName = driver.User.FullName,
            driverCode = driver.User.DriverCode,
            licenseNumber = driver.LicenseNumber,
            operationsLive = await _tracking.OperationsAreLive(ct),
            serviceDate = today.ToString("yyyy-MM-dd"),
            bus = bus is null ? null : new
            {
                bus.Id,
                bus.FleetNumber,
                bus.RegistrationNumber,
                bus.BusType,
                bus.Model,
                bus.Capacity,
                operatorName = bus.Operator.Name
            },
            route = trips.FirstOrDefault()?.Route is { } route ? new
            {
                route.Id,
                route.Code,
                route.Name,
                distanceKm = route.DistanceKm,
                stops = routeStops.Select(rs => rs.Stop.Name)
            } : null,
            trips = trips.Select(t => new
            {
                t.Id,
                t.Status,
                t.Simulation,
                fleetNumber = t.Bus.FleetNumber,
                routeCode = t.Route.Code,
                routeName = t.Route.Name,
                departureTime = IndiaClock.Clock(t.Schedule.DepartureTime),
                startedAt = t.StartedAt is null ? null : (DateTime?)IndiaClock.AsUtc(t.StartedAt.Value),
                endedAt = t.EndedAt is null ? null : (DateTime?)IndiaClock.AsUtc(t.EndedAt.Value)
            })
        });
    }

    [HttpPost("trips/{id:int}/start")]
    public async Task<IActionResult> Start(int id, StartTripRequest request, CancellationToken ct)
    {
        if (!await _tracking.OperationsAreLive(ct))
            return BadRequest(new { message = "Dispatch has not started operations for today." });

        var trip = await OwnTrip(id, ct);
        if (trip is null) return NotFound(new { message = "That trip is not assigned to you." });
        if (trip.Status == "Completed") return BadRequest(new { message = "This trip is already finished." });
        if (trip.Status == "Running") return Ok(await _tracking.GetTripAsync(trip.Id, ct));

        var mode = string.Equals(request.Mode, "simulate", StringComparison.OrdinalIgnoreCase) ? "simulate" : "gps";
        var snap = await _tracking.StartAsync(trip, mode, User.Id(), ct);
        return Ok(snap);
    }

    [HttpPost("trips/{id:int}/simulate")]
    public async Task<IActionResult> Simulate(int id, CancellationToken ct)
    {
        if (!await _tracking.OperationsAreLive(ct))
            return BadRequest(new { message = "Dispatch has not started operations for today." });
        var trip = await OwnTrip(id, ct);
        if (trip is null) return NotFound(new { message = "That trip is not assigned to you." });
        if (trip.Status == "Completed") return BadRequest(new { message = "This trip is already finished." });
        return Ok(await _tracking.EnableSimulationAsync(trip, ct));
    }

    [HttpPost("trips/{id:int}/location")]
    public async Task<IActionResult> Location(int id, LocationRequest request, CancellationToken ct)
    {
        var trip = await OwnTrip(id, ct);
        if (trip is null) return NotFound(new { message = "That trip is not assigned to you." });
        if (trip.Status != "Running") return BadRequest(new { message = "Start the trip before sending a location." });
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { message = "That location is not valid." });
        var snap = await _tracking.IngestAsync(trip.Id, request.Latitude, request.Longitude, request.SpeedKmph, request.Heading, false, ct);
        return Ok(snap);
    }

    [HttpPost("trips/{id:int}/replay")]
    public async Task<IActionResult> Replay(int id, CancellationToken ct)
    {
        var trip = await OwnTrip(id, ct);
        if (trip is null) return NotFound(new { message = "That trip is not assigned to you." });
        if (trip.Status == "Running") return BadRequest(new { message = "End the trip before running it again." });
        var pings = await _db.GpsPings.Where(p => p.TripId == id).ToListAsync(ct);
        _db.GpsPings.RemoveRange(pings);
        var etas = await _db.TripStopEtas.Where(e => e.TripId == id).ToListAsync(ct);
        _db.TripStopEtas.RemoveRange(etas);
        trip.Status = "Scheduled";
        trip.StartedAt = null;
        trip.EndedAt = null;
        trip.Simulation = false;
        trip.OdometerKm = 0;
        await _db.SaveChangesAsync(ct);
        return Ok(await _tracking.GetTripAsync(id, ct));
    }

    [HttpPost("trips/{id:int}/end")]
    public async Task<IActionResult> End(int id, CancellationToken ct)
    {
        var trip = await OwnTrip(id, ct);
        if (trip is null) return NotFound(new { message = "That trip is not assigned to you." });
        if (trip.Status != "Running") return BadRequest(new { message = "This trip is not on the road." });
        return Ok(await _tracking.EndAsync(trip, User.Id(), ct));
    }

    private async Task<Trip?> OwnTrip(int id, CancellationToken ct)
    {
        var driver = await _db.Drivers.AsNoTracking().FirstOrDefaultAsync(d => d.UserId == User.Id(), ct);
        if (driver is null) return null;
        return await _db.Trips
            .Include(t => t.Bus).ThenInclude(b => b.Operator)
            .Include(t => t.Route)
            .Include(t => t.Driver).ThenInclude(d => d.User)
            .Include(t => t.Schedule)
            .FirstOrDefaultAsync(t => t.Id == id && t.DriverId == driver.Id, ct);
    }
}
