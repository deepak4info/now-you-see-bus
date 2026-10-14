using System.Text.RegularExpressions;
using BusTrack.Api.Auth;
using BusRoute = BusTrack.Api.Data.Route;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TrackingService _tracking;
    private readonly RouteGeometryService _geometry;
    private readonly PasswordHasher<User> _passwords;

    public AdminController(AppDbContext db, TrackingService tracking, RouteGeometryService geometry, PasswordHasher<User> passwords)
    {
        _db = db;
        _tracking = tracking;
        _geometry = geometry;
        _passwords = passwords;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var today = IndiaClock.Today();
        var trips = await _db.Trips.AsNoTracking().Where(t => t.ServiceDate == today).ToListAsync(ct);
        var running = new List<TrackSnapshotDto>();
        foreach (var trip in trips.Where(t => t.Status == "Running"))
        {
            var snap = await _tracking.GetTripAsync(trip.Id, ct);
            if (snap is not null) running.Add(snap);
        }
        var audit = await _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(8).ToListAsync(ct);
        return Ok(new
        {
            operationsLive = await _tracking.OperationsAreLive(ct),
            serviceDate = today.ToString("yyyy-MM-dd"),
            operators = await _db.Operators.CountAsync(o => o.IsActive, ct),
            buses = await _db.Buses.CountAsync(b => b.IsActive, ct),
            drivers = await _db.Drivers.CountAsync(d => d.IsActive, ct),
            stops = await _db.Stops.CountAsync(s => s.IsActive, ct),
            routes = await _db.Routes.CountAsync(r => r.IsActive, ct),
            schedules = await _db.Schedules.CountAsync(s => s.IsActive, ct),
            tripsToday = trips.Count,
            runningNow = trips.Count(t => t.Status == "Running"),
            completedToday = trips.Count(t => t.Status == "Completed"),
            delayedNow = running.Count(s => s.DelayMinutes is > 5),
            recentAudit = audit.Select(a => new
            {
                a.Id,
                a.Action,
                a.Entity,
                a.EntityId,
                a.Detail,
                createdAt = IndiaClock.AsUtc(a.CreatedAt)
            })
        });
    }

    [HttpPost("operations/start")]
    public async Task<IActionResult> StartOperations(CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "OperationsLive", ct);
        if (setting is null)
        {
            setting = new SystemSetting { Key = "OperationsLive", Value = "true" };
            _db.SystemSettings.Add(setting);
        }
        setting.Value = "true";
        setting.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var created = await _tracking.MaterializeToday(ct);
        await _tracking.Audit(User.Id(), "StartOperations", "System", "OperationsLive", $"Created {created} trips", ct);
        return Ok(new { operationsLive = true, tripsCreated = created, serviceDate = IndiaClock.Today().ToString("yyyy-MM-dd") });
    }

    [HttpGet("operators")]
    public async Task<IActionResult> Operators(CancellationToken ct) =>
        Ok(await _db.Operators.AsNoTracking().Where(o => o.IsActive).OrderBy(o => o.Name).ToListAsync(ct));

    [HttpPost("operators")]
    public async Task<IActionResult> CreateOperator(OperatorRequest request, CancellationToken ct)
    {
        var error = ValidateOperator(request);
        if (error is not null) return BadRequest(new { message = error });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.Operators.AnyAsync(o => o.Code == code, ct))
            return BadRequest(new { message = "That operator code is already used." });
        var row = new Operator
        {
            Name = request.Name.Trim(),
            Code = code,
            ContactName = Blank(request.ContactName),
            Phone = Blank(request.Phone),
            Email = Blank(request.Email),
            CreatedAt = DateTime.UtcNow
        };
        _db.Operators.Add(row);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Operator", row.Id.ToString(), row.Name, ct);
        return Ok(row);
    }

    [HttpPut("operators/{id:int}")]
    public async Task<IActionResult> UpdateOperator(int id, OperatorRequest request, CancellationToken ct)
    {
        var row = await _db.Operators.FirstOrDefaultAsync(o => o.Id == id && o.IsActive, ct);
        if (row is null) return NotFound(new { message = "Operator not found." });
        var error = ValidateOperator(request);
        if (error is not null) return BadRequest(new { message = error });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.Operators.AnyAsync(o => o.Code == code && o.Id != id, ct))
            return BadRequest(new { message = "That operator code is already used." });
        row.Name = request.Name.Trim();
        row.Code = code;
        row.ContactName = Blank(request.ContactName);
        row.Phone = Blank(request.Phone);
        row.Email = Blank(request.Email);
        await _db.SaveChangesAsync(ct);
        return Ok(row);
    }

    [HttpDelete("operators/{id:int}")]
    public async Task<IActionResult> DeleteOperator(int id, CancellationToken ct)
    {
        var row = await _db.Operators.FirstOrDefaultAsync(o => o.Id == id && o.IsActive, ct);
        if (row is null) return NotFound(new { message = "Operator not found." });
        if (await _db.Buses.AnyAsync(b => b.OperatorId == id && b.IsActive, ct))
            return BadRequest(new { message = "Move or retire this operator's buses first." });
        row.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("buses")]
    public async Task<IActionResult> Buses(CancellationToken ct)
    {
        var rows = await _db.Buses.AsNoTracking().Include(b => b.Operator).Include(b => b.AssignedDriver).ThenInclude(d => d!.User)
            .Where(b => b.IsActive).OrderBy(b => b.FleetNumber).ToListAsync(ct);
        return Ok(rows.Select(b => new
        {
            b.Id,
            b.OperatorId,
            operatorName = b.Operator.Name,
            b.FleetNumber,
            b.RegistrationNumber,
            b.Model,
            b.Capacity,
            b.BusType,
            b.AssignedDriverId,
            driverName = b.AssignedDriver?.User.FullName,
            driverCode = b.AssignedDriver?.User.DriverCode
        }));
    }

    [HttpPost("buses")]
    public async Task<IActionResult> CreateBus(BusRequest request, CancellationToken ct)
    {
        var error = await ValidateBus(request, null, ct);
        if (error is not null) return BadRequest(new { message = error });
        var row = new Bus
        {
            OperatorId = request.OperatorId,
            FleetNumber = request.FleetNumber.Trim(),
            RegistrationNumber = request.RegistrationNumber.Trim().ToUpperInvariant(),
            Model = Blank(request.Model),
            Capacity = request.Capacity,
            BusType = request.BusType.Trim(),
            AssignedDriverId = request.AssignedDriverId,
            CreatedAt = DateTime.UtcNow
        };
        _db.Buses.Add(row);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Bus", row.Id.ToString(), row.RegistrationNumber, ct);
        return Ok(new { row.Id });
    }

    [HttpPut("buses/{id:int}")]
    public async Task<IActionResult> UpdateBus(int id, BusRequest request, CancellationToken ct)
    {
        var row = await _db.Buses.FirstOrDefaultAsync(b => b.Id == id && b.IsActive, ct);
        if (row is null) return NotFound(new { message = "Bus not found." });
        var error = await ValidateBus(request, id, ct);
        if (error is not null) return BadRequest(new { message = error });
        row.OperatorId = request.OperatorId;
        row.FleetNumber = request.FleetNumber.Trim();
        row.RegistrationNumber = request.RegistrationNumber.Trim().ToUpperInvariant();
        row.Model = Blank(request.Model);
        row.Capacity = request.Capacity;
        row.BusType = request.BusType.Trim();
        row.AssignedDriverId = request.AssignedDriverId;
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id });
    }

    [HttpDelete("buses/{id:int}")]
    public async Task<IActionResult> DeleteBus(int id, CancellationToken ct)
    {
        var row = await _db.Buses.FirstOrDefaultAsync(b => b.Id == id && b.IsActive, ct);
        if (row is null) return NotFound(new { message = "Bus not found." });
        if (await _db.Schedules.AnyAsync(s => s.BusId == id && s.IsActive, ct))
            return BadRequest(new { message = "This bus is still on a schedule." });
        row.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("drivers")]
    public async Task<IActionResult> Drivers(CancellationToken ct)
    {
        var rows = await _db.Drivers.AsNoTracking().Include(d => d.User).Include(d => d.Operator)
            .Where(d => d.IsActive).OrderBy(d => d.User.FullName).ToListAsync(ct);
        return Ok(rows.Select(d => new
        {
            d.Id,
            d.UserId,
            d.User.FullName,
            d.User.Email,
            d.User.Phone,
            d.User.DriverCode,
            d.LicenseNumber,
            d.OperatorId,
            operatorName = d.Operator?.Name
        }));
    }

    [HttpPost("drivers")]
    public async Task<IActionResult> CreateDriver(DriverRequest request, CancellationToken ct)
    {
        var code = request.DriverCode.Trim().ToUpperInvariant();
        var email = request.Email.Trim();
        if (request.FullName.Trim().Length < 2) return BadRequest(new { message = "Enter the driver's name." });
        if (code.Length < 3) return BadRequest(new { message = "Driver ID should be at least 3 characters." });
        if (!email.Contains('@')) return BadRequest(new { message = "Enter an email for the driver." });
        if (request.Password.Length < 8) return BadRequest(new { message = "Use a password of at least 8 characters." });
        if (request.LicenseNumber.Trim().Length < 3) return BadRequest(new { message = "Enter a licence number." });
        if (await _db.Users.AnyAsync(u => u.Email == email || u.DriverCode == code, ct))
            return BadRequest(new { message = "That email or driver ID is already used." });

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName.Trim(),
            Email = email,
            Phone = Blank(request.Phone),
            Role = "Driver",
            DriverCode = code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = _passwords.HashPassword(user, request.Password);
        _db.Users.Add(user);
        var driver = new Driver
        {
            User = user,
            LicenseNumber = request.LicenseNumber.Trim(),
            OperatorId = request.OperatorId,
            IsActive = true
        };
        _db.Drivers.Add(driver);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Driver", driver.Id.ToString(), code, ct);
        return Ok(new { driver.Id, user.DriverCode });
    }

    [HttpDelete("drivers/{id:int}")]
    public async Task<IActionResult> DeleteDriver(int id, CancellationToken ct)
    {
        var row = await _db.Drivers.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == id && d.IsActive, ct);
        if (row is null) return NotFound(new { message = "Driver not found." });
        if (await _db.Schedules.AnyAsync(s => s.DriverId == id && s.IsActive, ct))
            return BadRequest(new { message = "This driver is still assigned to a schedule." });
        row.IsActive = false;
        row.User.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("stops")]
    public async Task<IActionResult> Stops(CancellationToken ct) =>
        Ok(await _db.Stops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct));

    [HttpPost("stops")]
    public async Task<IActionResult> CreateStop(StopRequest request, CancellationToken ct)
    {
        var error = ValidateStop(request);
        if (error is not null) return BadRequest(new { message = error });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.Stops.AnyAsync(s => s.Code == code, ct))
            return BadRequest(new { message = "That stop code is already used." });
        var row = new Stop
        {
            Name = request.Name.Trim(),
            Code = code,
            Latitude = decimal.Round((decimal)request.Latitude, 6),
            Longitude = decimal.Round((decimal)request.Longitude, 6),
            Address = Blank(request.Address),
            City = Blank(request.City)
        };
        _db.Stops.Add(row);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Stop", row.Id.ToString(), row.Name, ct);
        return Ok(row);
    }

    [HttpPut("stops/{id:int}")]
    public async Task<IActionResult> UpdateStop(int id, StopRequest request, CancellationToken ct)
    {
        var row = await _db.Stops.FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (row is null) return NotFound(new { message = "Stop not found." });
        var error = ValidateStop(request);
        if (error is not null) return BadRequest(new { message = error });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.Stops.AnyAsync(s => s.Code == code && s.Id != id, ct))
            return BadRequest(new { message = "That stop code is already used." });
        row.Name = request.Name.Trim();
        row.Code = code;
        row.Latitude = decimal.Round((decimal)request.Latitude, 6);
        row.Longitude = decimal.Round((decimal)request.Longitude, 6);
        row.Address = Blank(request.Address);
        row.City = Blank(request.City);
        await _db.SaveChangesAsync(ct);
        var routeIds = await _db.RouteStops.Where(rs => rs.StopId == id).Select(rs => rs.RouteId).Distinct().ToListAsync(ct);
        foreach (var routeId in routeIds) await _geometry.RebuildAsync(routeId, ct);
        return Ok(row);
    }

    [HttpDelete("stops/{id:int}")]
    public async Task<IActionResult> DeleteStop(int id, CancellationToken ct)
    {
        var row = await _db.Stops.FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (row is null) return NotFound(new { message = "Stop not found." });
        if (await _db.RouteStops.AnyAsync(rs => rs.StopId == id, ct))
            return BadRequest(new { message = "Remove this stop from its routes first." });
        row.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("routes")]
    public async Task<IActionResult> Routes(CancellationToken ct)
    {
        var routes = await _db.Routes.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
        var stops = await _db.RouteStops.AsNoTracking().Include(rs => rs.Stop).OrderBy(rs => rs.SequenceNo).ToListAsync(ct);
        var fares = await _db.FareSlabs.AsNoTracking().Include(f => f.FromStop).Include(f => f.ToStop).ToListAsync(ct);
        return Ok(routes.Select(r => new
        {
            r.Id,
            r.Code,
            r.Name,
            r.OperatorId,
            distanceKm = r.DistanceKm,
            stops = stops.Where(s => s.RouteId == r.Id).Select(s => new
            {
                s.StopId,
                s.Stop.Name,
                s.Stop.Code,
                s.SequenceNo,
                distanceKm = s.DistanceFromOriginKm
            }),
            fares = fares.Where(f => f.RouteId == r.Id).Select(f => new
            {
                f.Id,
                f.FromStopId,
                from = f.FromStop.Name,
                f.ToStopId,
                to = f.ToStop.Name,
                f.Amount,
                f.Currency
            })
        }));
    }

    [HttpPost("routes")]
    public async Task<IActionResult> CreateRoute(RouteRequest request, CancellationToken ct)
    {
        var error = await ValidateRoute(request, null, ct);
        if (error is not null) return BadRequest(new { message = error });
        var route = new BusRoute
        {
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            OperatorId = request.OperatorId,
            IsActive = true
        };
        _db.Routes.Add(route);
        await _db.SaveChangesAsync(ct);
        await ReplaceStops(route.Id, request.StopIds, ct);
        await _geometry.RebuildAsync(route.Id, ct);
        await _tracking.Audit(User.Id(), "Create", "Route", route.Id.ToString(), route.Code, ct);
        return Ok(new { route.Id });
    }

    [HttpPut("routes/{id:int}")]
    public async Task<IActionResult> UpdateRoute(int id, RouteRequest request, CancellationToken ct)
    {
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.IsActive, ct);
        if (route is null) return NotFound(new { message = "Route not found." });
        var error = await ValidateRoute(request, id, ct);
        if (error is not null) return BadRequest(new { message = error });
        route.Code = request.Code.Trim();
        route.Name = request.Name.Trim();
        route.OperatorId = request.OperatorId;
        await _db.SaveChangesAsync(ct);
        await ReplaceStops(route.Id, request.StopIds, ct);
        await _geometry.RebuildAsync(route.Id, ct);
        return Ok(new { route.Id });
    }

    [HttpPut("routes/{id:int}/fares")]
    public async Task<IActionResult> UpdateFares(int id, FareUpdateRequest request, CancellationToken ct)
    {
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.IsActive, ct);
        if (route is null) return NotFound(new { message = "Route not found." });
        var slabs = await _db.FareSlabs.Where(f => f.RouteId == id).ToListAsync(ct);
        foreach (var fare in request.Fares)
        {
            if (fare.Amount < 0) return BadRequest(new { message = "Fare cannot be negative." });
            var slab = slabs.FirstOrDefault(s => s.FromStopId == fare.FromStopId && s.ToStopId == fare.ToStopId);
            if (slab is null) return BadRequest(new { message = "One of those stop pairs is not on this route." });
            slab.Amount = fare.Amount;
        }
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("routes/{id:int}")]
    public async Task<IActionResult> DeleteRoute(int id, CancellationToken ct)
    {
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.IsActive, ct);
        if (route is null) return NotFound(new { message = "Route not found." });
        if (await _db.Schedules.AnyAsync(s => s.RouteId == id && s.IsActive, ct))
            return BadRequest(new { message = "This route still has a schedule." });
        route.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("schedules")]
    public async Task<IActionResult> Schedules(CancellationToken ct)
    {
        var rows = await _db.Schedules.AsNoTracking()
            .Include(s => s.Route)
            .Include(s => s.Bus)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive)
            .OrderBy(s => s.DepartureTime)
            .ToListAsync(ct);
        return Ok(rows.Select(s => new
        {
            s.Id,
            s.RouteId,
            routeCode = s.Route.Code,
            routeName = s.Route.Name,
            s.BusId,
            fleetNumber = s.Bus?.FleetNumber,
            registrationNumber = s.Bus?.RegistrationNumber,
            s.DriverId,
            driverName = s.Driver?.User.FullName,
            driverCode = s.Driver?.User.DriverCode,
            departureTime = IndiaClock.Clock(s.DepartureTime),
            s.ServiceDays
        }));
    }

    [HttpPost("schedules")]
    public async Task<IActionResult> CreateSchedule(ScheduleRequest request, CancellationToken ct)
    {
        var error = await ValidateSchedule(request, ct);
        if (error is not null) return BadRequest(new { message = error });
        var row = new Schedule
        {
            RouteId = request.RouteId,
            BusId = request.BusId,
            DriverId = request.DriverId,
            DepartureTime = TimeOnly.Parse(request.DepartureTime),
            ServiceDays = string.IsNullOrWhiteSpace(request.ServiceDays) ? "1111111" : request.ServiceDays.Trim(),
            IsActive = true
        };
        _db.Schedules.Add(row);
        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Schedule", row.Id.ToString(), row.DepartureTime.ToString("HH:mm"), ct);
        return Ok(new { row.Id });
    }

    [HttpPut("schedules/{id:int}")]
    public async Task<IActionResult> UpdateSchedule(int id, ScheduleRequest request, CancellationToken ct)
    {
        var row = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (row is null) return NotFound(new { message = "Schedule not found." });
        var error = await ValidateSchedule(request, ct);
        if (error is not null) return BadRequest(new { message = error });
        row.RouteId = request.RouteId;
        row.BusId = request.BusId;
        row.DriverId = request.DriverId;
        row.DepartureTime = TimeOnly.Parse(request.DepartureTime);
        row.ServiceDays = string.IsNullOrWhiteSpace(request.ServiceDays) ? "1111111" : request.ServiceDays.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id });
    }

    [HttpDelete("schedules/{id:int}")]
    public async Task<IActionResult> DeleteSchedule(int id, CancellationToken ct)
    {
        var row = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (row is null) return NotFound(new { message = "Schedule not found." });
        if (await _db.Trips.AnyAsync(t => t.ScheduleId == id && t.Status == "Running", ct))
            return BadRequest(new { message = "End the running trip before removing this schedule." });
        row.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("live")]
    public async Task<IActionResult> Live(CancellationToken ct) => Ok(await _tracking.LiveAsync(ct));

    [HttpGet("reports/trips")]
    public async Task<IActionResult> Report([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? IndiaClock.Today();
        var trips = await _db.Trips.AsNoTracking()
            .Where(t => t.ServiceDate == day)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var rows = new List<TrackSnapshotDto>();
        foreach (var id in trips)
        {
            var snap = await _tracking.GetTripAsync(id, ct);
            if (snap is not null) rows.Add(snap);
        }
        return Ok(rows.OrderBy(r => r.DepartureTime));
    }

    private async Task ReplaceStops(int routeId, List<int> stopIds, CancellationToken ct)
    {
        var old = await _db.RouteStops.Where(rs => rs.RouteId == routeId).ToListAsync(ct);
        _db.RouteStops.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        var seq = 1;
        foreach (var stopId in stopIds)
        {
            _db.RouteStops.Add(new RouteStop { RouteId = routeId, StopId = stopId, SequenceNo = seq++ });
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string? ValidateOperator(OperatorRequest request)
    {
        if (request.Name.Trim().Length < 2) return "Enter the operator name.";
        if (request.Code.Trim().Length < 2) return "Enter an operator code.";
        return null;
    }

    private async Task<string?> ValidateBus(BusRequest request, int? id, CancellationToken ct)
    {
        if (!await _db.Operators.AnyAsync(o => o.Id == request.OperatorId && o.IsActive, ct))
            return "Choose an operator.";
        if (request.FleetNumber.Trim().Length == 0) return "Enter a fleet number.";
        if (request.RegistrationNumber.Trim().Length < 4) return "Enter the registration number.";
        if (request.Capacity is < 1 or > 120) return "Capacity should be between 1 and 120.";
        if (request.BusType.Trim().Length == 0) return "Enter a bus type.";
        var fleet = request.FleetNumber.Trim();
        var reg = request.RegistrationNumber.Trim().ToUpperInvariant();
        if (await _db.Buses.AnyAsync(b => b.FleetNumber == fleet && b.Id != id, ct))
            return "That fleet number is already used.";
        if (await _db.Buses.AnyAsync(b => b.RegistrationNumber == reg && b.Id != id, ct))
            return "That registration is already used.";
        if (request.AssignedDriverId is int driverId && !await _db.Drivers.AnyAsync(d => d.Id == driverId && d.IsActive, ct))
            return "Choose a driver who is on the roster.";
        return null;
    }

    private static string? ValidateStop(StopRequest request)
    {
        if (request.Name.Trim().Length < 2) return "Enter the stop name.";
        if (request.Code.Trim().Length < 2) return "Enter a short stop code.";
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return "Latitude or longitude is out of range.";
        return null;
    }

    private async Task<string?> ValidateRoute(RouteRequest request, int? id, CancellationToken ct)
    {
        if (request.Code.Trim().Length == 0) return "Enter a route code.";
        if (request.Name.Trim().Length < 2) return "Enter a route name.";
        if (request.StopIds.Count < 2) return "A route needs at least two stops.";
        if (request.StopIds.Distinct().Count() != request.StopIds.Count) return "Each stop can appear once.";
        var found = await _db.Stops.CountAsync(s => s.IsActive && request.StopIds.Contains(s.Id), ct);
        if (found != request.StopIds.Count) return "One of those stops is not available.";
        var code = request.Code.Trim();
        if (await _db.Routes.AnyAsync(r => r.Code == code && r.Id != id, ct))
            return "That route code is already used.";
        return null;
    }

    private async Task<string?> ValidateSchedule(ScheduleRequest request, CancellationToken ct)
    {
        if (!await _db.Routes.AnyAsync(r => r.Id == request.RouteId && r.IsActive, ct))
            return "Choose a route.";
        if (!TimeOnly.TryParse(request.DepartureTime, out _))
            return "Enter a departure time like 08:00.";
        if (request.BusId is int busId && !await _db.Buses.AnyAsync(b => b.Id == busId && b.IsActive, ct))
            return "Choose a bus.";
        if (request.DriverId is int driverId && !await _db.Drivers.AnyAsync(d => d.Id == driverId && d.IsActive, ct))
            return "Choose a driver.";
        var days = string.IsNullOrWhiteSpace(request.ServiceDays) ? "1111111" : request.ServiceDays.Trim();
        if (!Regex.IsMatch(days, "^[01]{7}$"))
            return "Service days must be 7 characters of 1 or 0, Monday through Sunday.";
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
