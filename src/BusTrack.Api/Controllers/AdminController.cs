using System.Security.Claims;
using System.Text.RegularExpressions;
using BusTrack.Api.Auth;
using BusRoute = BusTrack.Api.Data.Route;
using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        var audit = await _tracking.QueryAuditLogsAsync(8, ct: ct);
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
        await _tracking.InvalidateLiveAndScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "StartOperations", "System", "OperationsLive", $"Created {created} trips", ct);
        return Ok(new { operationsLive = true, tripsCreated = created, serviceDate = IndiaClock.Today().ToString("yyyy-MM-dd") });
    }

    [HttpGet("operators")]
    public async Task<IActionResult> Operators(CancellationToken ct) =>
        Ok(await _db.Operators.AsNoTracking().Where(o => o.IsActive).OrderBy(o => o.Name).ToListAsync(ct));

    [HttpGet("import-lookups")]
    public async Task<IActionResult> ImportLookups(CancellationToken ct)
    {
        var operators = await _db.Operators.AsNoTracking()
            .Select(o => new { o.Id, o.Code, o.IsActive })
            .ToListAsync(ct);
        var drivers = await _db.Drivers.AsNoTracking()
            .Include(d => d.User)
            .Select(d => new
            {
                d.Id,
                driverCode = d.User.DriverCode ?? "",
                d.User.Email,
                d.OperatorId,
                isActive = d.IsActive && d.User.IsActive
            })
            .ToListAsync(ct);
        var buses = await _db.Buses.AsNoTracking()
            .Select(b => new { b.Id, b.FleetNumber, b.RegistrationNumber })
            .ToListAsync(ct);
        var rawStops = await _db.Stops.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        var stops = StopNormalizer.DeduplicateStopsList(rawStops)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToList();
        var routes = await _db.Routes.AsNoTracking()
            .Where(r => r.IsActive)
            .Select(r => new { r.Id, r.Code, r.Name })
            .ToListAsync(ct);
        var scheduleRows = await _db.Schedules.AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new { s.RouteId, s.BusId, s.DepartureTime })
            .ToListAsync(ct);
        var schedules = scheduleRows.Select(s => new
        {
            s.RouteId,
            s.BusId,
            departure = s.DepartureTime.ToString("HH:mm")
        });
        return Ok(new { operators, drivers, buses, stops, routes, schedules });
    }

    [HttpGet("audit-logs")]
    public async Task<IActionResult> AuditLogs([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var logs = await _tracking.QueryAuditLogsAsync(Math.Clamp(limit, 1, 200), ct: ct);
        return Ok(logs.Select(a => new
        {
            a.Id,
            a.Action,
            a.Entity,
            a.EntityId,
            a.Detail,
            a.CreatedAt
        }));
    }

    [HttpGet("sharding/status")]
    public async Task<IActionResult> ShardingStatus(CancellationToken ct)
    {
        return Ok(await _tracking.GetShardingHealthAsync(ct));
    }

    [HttpGet("sharding/route-preview")]
    public IActionResult ShardingRoutePreview(
        [FromQuery] int tripId = 1,
        [FromQuery] double? lat = null,
        [FromQuery] double? lng = null,
        [FromQuery] string? region = null)
    {
        return Ok(_tracking.PreviewShardRoute(tripId, lat, lng, region));
    }

    [HttpPost("sharding/archive")]
    public async Task<IActionResult> TriggerTripArchive([FromQuery] int? retentionDays = null, CancellationToken ct = default)
    {
        var result = await _tracking.ArchiveHistoricalTripsAsync(retentionDays, ct);
        return Ok(result);
    }

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
        await _tracking.InvalidateRouteCachesAsync(ct);
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
        await _tracking.InvalidateRouteCachesAsync(ct);
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
        await _tracking.InvalidateRouteCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("operators/bulk-delete")]
    public async Task<IActionResult> BulkDeleteOperators([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No operator IDs provided." });

        var ops = await _db.Operators.Where(o => request.Ids.Contains(o.Id) && o.IsActive).ToListAsync(ct);
        var hasBusesOpIds = await _db.Buses
            .Where(b => request.Ids.Contains(b.OperatorId) && b.IsActive)
            .Select(b => b.OperatorId)
            .Distinct()
            .ToListAsync(ct);

        var hasBusesSet = hasBusesOpIds.ToHashSet();
        var deleted = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var o in ops)
        {
            if (hasBusesSet.Contains(o.Id))
            {
                skipped++;
                reasons.Add($"Operator '{o.Name}' ({o.Code}) still has active buses assigned.");
                continue;
            }
            o.IsActive = false;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);
        if (deleted > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Operator", string.Join(",", request.Ids), $"Deleted {deleted}, skipped {skipped}", ct);
        return Ok(new BulkDeleteResponse(deleted, skipped, $"Deleted {deleted} operator(s). {skipped} skipped.", reasons));
    }

    [HttpPost("operators/bulk-update")]
    public async Task<IActionResult> BulkUpdateOperators([FromBody] BulkUpdateOperatorsRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No operator IDs provided." });

        var ops = await _db.Operators.Where(o => request.Ids.Contains(o.Id) && o.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var o in ops)
        {
            var modified = false;
            if (!string.IsNullOrWhiteSpace(request.ContactName))
            {
                o.ContactName = request.ContactName.Trim();
                modified = true;
            }
            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                o.Phone = request.Phone.Trim();
                modified = true;
            }
            if (modified) updated++;
        }

        await _db.SaveChangesAsync(ct);
        if (updated > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkUpdate", "Operator", string.Join(",", request.Ids), $"Updated {updated} operators", ct);
        return Ok(new BulkOperationResult(updated, ops.Count - updated, $"Updated {updated} operator(s)."));
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
        await _tracking.InvalidateRouteCachesAsync(ct);
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
        await _tracking.InvalidateRouteCachesAsync(ct);
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
        await _tracking.InvalidateRouteCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("buses/bulk-delete")]
    public async Task<IActionResult> BulkDeleteBuses([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No bus IDs provided." });

        var buses = await _db.Buses.Where(b => request.Ids.Contains(b.Id) && b.IsActive).ToListAsync(ct);
        var inScheduleBusIds = await _db.Schedules
            .Where(s => s.BusId.HasValue && request.Ids.Contains(s.BusId.Value) && s.IsActive)
            .Select(s => s.BusId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var inScheduleSet = inScheduleBusIds.ToHashSet();
        var deleted = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var b in buses)
        {
            if (inScheduleSet.Contains(b.Id))
            {
                skipped++;
                reasons.Add($"Bus '{b.FleetNumber}' ({b.RegistrationNumber}) is currently assigned to an active schedule.");
                continue;
            }
            b.IsActive = false;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);
        if (deleted > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Bus", string.Join(",", request.Ids), $"Deleted {deleted}, skipped {skipped}", ct);
        return Ok(new BulkDeleteResponse(deleted, skipped, $"Deleted {deleted} bus(es). {skipped} skipped.", reasons));
    }

    [HttpPost("buses/bulk-update")]
    public async Task<IActionResult> BulkUpdateBuses([FromBody] BulkUpdateBusesRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No bus IDs provided." });

        var buses = await _db.Buses.Where(b => request.Ids.Contains(b.Id) && b.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var b in buses)
        {
            var modified = false;
            if (request.OperatorId.HasValue && request.OperatorId.Value > 0)
            {
                b.OperatorId = request.OperatorId.Value;
                modified = true;
            }
            if (!string.IsNullOrWhiteSpace(request.BusType))
            {
                b.BusType = request.BusType.Trim();
                modified = true;
            }
            if (request.Capacity.HasValue && request.Capacity.Value > 0)
            {
                b.Capacity = request.Capacity.Value;
                modified = true;
            }
            if (request.AssignedDriverId.HasValue)
            {
                b.AssignedDriverId = request.AssignedDriverId.Value > 0 ? request.AssignedDriverId.Value : null;
                modified = true;
            }
            if (modified) updated++;
        }

        await _db.SaveChangesAsync(ct);
        if (updated > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkUpdate", "Bus", string.Join(",", request.Ids), $"Updated {updated} buses", ct);
        return Ok(new BulkOperationResult(updated, buses.Count - updated, $"Updated {updated} bus(es)."));
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
        var license = request.LicenseNumber.Trim();
        if (license.Length < 3) return BadRequest(new { message = "Enter a licence number." });
        if (await _db.Drivers.AnyAsync(d => d.LicenseNumber == license, ct))
            return BadRequest(new { message = "That driving licence number is already registered." });
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

    [HttpPut("drivers/{id:int}")]
    public async Task<IActionResult> UpdateDriver(int id, DriverUpdateRequest request, CancellationToken ct)
    {
        var row = await _db.Drivers.Include(d => d.User)
            .FirstOrDefaultAsync(d => d.Id == id && d.IsActive, ct);
        if (row is null) return NotFound(new { message = "Driver not found." });

        var code = request.DriverCode.Trim().ToUpperInvariant();
        var email = request.Email.Trim();
        if (request.FullName.Trim().Length < 2) return BadRequest(new { message = "Enter the driver's name." });
        if (code.Length < 3) return BadRequest(new { message = "Driver ID should be at least 3 characters." });
        if (!email.Contains('@')) return BadRequest(new { message = "Enter an email for the driver." });
        if (!string.IsNullOrEmpty(request.Password) && request.Password.Length < 8)
            return BadRequest(new { message = "Use a password of at least 8 characters." });
        var license = request.LicenseNumber.Trim();
        if (license.Length < 3) return BadRequest(new { message = "Enter a licence number." });
        if (await _db.Drivers.AnyAsync(d => d.Id != id && d.LicenseNumber == license, ct))
            return BadRequest(new { message = "That driving licence number is already registered." });
        if (await _db.Users.AnyAsync(u => u.Id != row.UserId && (u.Email == email || u.DriverCode == code), ct))
            return BadRequest(new { message = "That email or driver ID is already used." });
        if (request.OperatorId is int operatorId &&
            !await _db.Operators.AnyAsync(o => o.Id == operatorId && o.IsActive, ct))
            return BadRequest(new { message = "Choose an operator who is active." });

        row.User.FullName = request.FullName.Trim();
        row.User.DriverCode = code;
        row.User.Email = email;
        row.User.Phone = Blank(request.Phone);
        row.LicenseNumber = request.LicenseNumber.Trim();
        row.OperatorId = request.OperatorId;
        if (!string.IsNullOrEmpty(request.Password))
            row.User.PasswordHash = _passwords.HashPassword(row.User, request.Password);

        await _db.SaveChangesAsync(ct);
        await _tracking.InvalidateUserProfileCacheAsync(row.UserId, ct);
        await _tracking.InvalidateLiveAndScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "Update", "Driver", row.Id.ToString(), code, ct);
        return Ok(new { row.Id, row.User.DriverCode });
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
        await _tracking.InvalidateUserProfileCacheAsync(row.UserId, ct);
        await _tracking.InvalidateLiveAndScheduleCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("drivers/bulk-delete")]
    public async Task<IActionResult> BulkDeleteDrivers([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No driver IDs provided." });

        var drivers = await _db.Drivers.Include(d => d.User).Where(d => request.Ids.Contains(d.Id) && d.IsActive).ToListAsync(ct);
        var inScheduleDriverIds = await _db.Schedules
            .Where(s => s.DriverId.HasValue && request.Ids.Contains(s.DriverId.Value) && s.IsActive)
            .Select(s => s.DriverId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var inScheduleSet = inScheduleDriverIds.ToHashSet();
        var deleted = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var d in drivers)
        {
            if (inScheduleSet.Contains(d.Id))
            {
                skipped++;
                reasons.Add($"Driver '{d.User.FullName}' ({d.User.DriverCode}) is assigned to an active schedule.");
                continue;
            }
            d.IsActive = false;
            d.User.IsActive = false;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);
        foreach (var d in drivers.Where(d => !d.IsActive))
            await _tracking.InvalidateUserProfileCacheAsync(d.UserId, ct);
        if (deleted > 0) await _tracking.InvalidateLiveAndScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Driver", string.Join(",", request.Ids), $"Deleted {deleted}, skipped {skipped}", ct);
        return Ok(new BulkDeleteResponse(deleted, skipped, $"Deleted {deleted} driver(s). {skipped} skipped.", reasons));
    }

    [HttpPost("drivers/bulk-update")]
    public async Task<IActionResult> BulkUpdateDrivers([FromBody] BulkUpdateDriversRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No driver IDs provided." });

        var drivers = await _db.Drivers.Where(d => request.Ids.Contains(d.Id) && d.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var d in drivers)
        {
            if (request.OperatorId.HasValue && request.OperatorId.Value > 0)
            {
                d.OperatorId = request.OperatorId.Value;
                updated++;
            }
        }

        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkUpdate", "Driver", string.Join(",", request.Ids), $"Updated {updated} drivers", ct);
        return Ok(new BulkOperationResult(updated, drivers.Count - updated, $"Updated {updated} driver(s)."));
    }

    [HttpGet("stops")]
    public async Task<IActionResult> Stops(CancellationToken ct)
    {
        var stops = await _db.Stops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        return Ok(StopNormalizer.DeduplicateStopsList(stops));
    }

    [HttpPost("stops/deduplicate")]
    public async Task<IActionResult> DeduplicateStops(CancellationToken ct)
    {
        var merged = await DbInitializer.DeduplicateStopsAsync(_db, null, ct);
        if (merged > 0) await _tracking.InvalidateStopCachesAsync(ct);
        var active = await _db.Stops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var uniqueStops = StopNormalizer.DeduplicateStopsList(active);
        return Ok(new
        {
            merged,
            remainingUniqueStops = uniqueStops.Count,
            message = merged > 0
                ? $"Successfully merged {merged} duplicate stops. All records are now unique."
                : "All stops are already unique."
        });
    }

    [HttpPost("stops")]
    public async Task<IActionResult> CreateStop(StopRequest request, CancellationToken ct)
    {
        var error = ValidateStop(request);
        if (error is not null) return BadRequest(new { message = error });
        var code = request.Code.Trim().ToUpperInvariant();
        var name = request.Name.Trim();

        if (await _db.Stops.AnyAsync(s => s.IsActive && s.Code == code, ct))
            return BadRequest(new { message = $"Stop code '{code}' is already used." });

        var existing = await _db.Stops.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive && s.Name.ToLower() == name.ToLower(), ct);
        if (existing is not null)
            return BadRequest(new { message = $"A stop named '{name}' already exists (Code: {existing.Code})." });

        var row = new Stop
        {
            Name = name,
            Code = code,
            Latitude = decimal.Round((decimal)request.Latitude, 6),
            Longitude = decimal.Round((decimal)request.Longitude, 6),
            Address = Blank(request.Address),
            City = Blank(request.City),
            IsActive = true
        };
        _db.Stops.Add(row);
        await _db.SaveChangesAsync(ct);
        await _tracking.InvalidateStopCachesAsync(ct);
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
        await _tracking.InvalidateStopCachesAsync(ct);
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
        await _tracking.InvalidateStopCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("stops/bulk-delete")]
    public async Task<IActionResult> BulkDeleteStops([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No stop IDs provided." });

        var stops = await _db.Stops.Where(s => request.Ids.Contains(s.Id) && s.IsActive).ToListAsync(ct);
        var inRoutesStopIds = await _db.RouteStops
            .Where(rs => request.Ids.Contains(rs.StopId))
            .Select(rs => rs.StopId)
            .Distinct()
            .ToListAsync(ct);

        var inRoutesSet = inRoutesStopIds.ToHashSet();
        var deletedCount = 0;
        var skippedCount = 0;
        var skippedReasons = new List<string>();

        foreach (var stop in stops)
        {
            if (inRoutesSet.Contains(stop.Id))
            {
                skippedCount++;
                skippedReasons.Add($"Stop '{stop.Name}' ({stop.Code}) is part of an active route.");
                continue;
            }

            stop.IsActive = false;
            deletedCount++;
        }

        await _db.SaveChangesAsync(ct);
        if (deletedCount > 0) await _tracking.InvalidateStopCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Stop", string.Join(",", request.Ids), $"Deleted {deletedCount}, skipped {skippedCount}", ct);
        return Ok(new BulkDeleteResponse(deletedCount, skippedCount, $"Deleted {deletedCount} stop(s). {skippedCount} skipped.", skippedReasons));
    }

    [HttpPost("stops/bulk-update")]
    public async Task<IActionResult> BulkUpdateStops([FromBody] BulkUpdateStopsRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No stop IDs provided." });

        var stops = await _db.Stops.Where(s => request.Ids.Contains(s.Id) && s.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var stop in stops)
        {
            var modified = false;
            if (!string.IsNullOrWhiteSpace(request.City))
            {
                stop.City = request.City.Trim();
                modified = true;
            }
            if (!string.IsNullOrWhiteSpace(request.Address))
            {
                stop.Address = request.Address.Trim();
                modified = true;
            }
            if (modified) updated++;
        }

        await _db.SaveChangesAsync(ct);
        if (updated > 0) await _tracking.InvalidateStopCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkUpdate", "Stop", string.Join(",", request.Ids), $"Updated {updated} stops", ct);
        return Ok(new BulkOperationResult(updated, stops.Count - updated, $"Updated {updated} stop(s)."));
    }

    [HttpGet("routes")]
    public async Task<IActionResult> Routes(CancellationToken ct)
    {
        var routes = await _db.Routes.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
        var stops = await _db.RouteStops.AsNoTracking().Include(rs => rs.Stop).OrderBy(rs => rs.SequenceNo).ToListAsync(ct);
        var fares = await _db.FareSlabs.AsNoTracking().Include(f => f.FromStop).Include(f => f.ToStop).ToListAsync(ct);
        var stopsByRoute = stops.ToLookup(s => s.RouteId);
        var faresByRoute = fares.ToLookup(f => f.RouteId);
        var createdRows = await _tracking.QueryAuditLogsAsync(2000, actionFilter: "Create", entityFilter: "Route", ct: ct);
        var addedAtById = createdRows
            .Where(a => a.EntityId != null)
            .GroupBy(a => a.EntityId!)
            .ToDictionary(g => g.Key, g => g.Max(a => a.CreatedAt));
        return Ok(routes.Select(r => new
        {
            r.Id,
            r.Code,
            r.Name,
            r.OperatorId,
            r.DistanceMode,
            distanceKm = r.DistanceKm,
            addedAt = addedAtById.TryGetValue(r.Id.ToString(), out var addedAt) ? addedAt : (DateTime?)null,
            stops = stopsByRoute[r.Id].Select(s => new
            {
                s.StopId,
                s.Stop.Name,
                s.Stop.Code,
                s.SequenceNo,
                distanceKm = s.DistanceFromOriginKm,
                segmentDistanceKm = s.SegmentDistanceKm
            }),
            fares = faresByRoute[r.Id].Select(f => new
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

    [HttpPost("routes/rebuild-roads")]
    public async Task<IActionResult> RebuildRoadDistances(CancellationToken ct)
    {
        var count = await _geometry.RebuildAllRoutesAsync(ct);
        await _tracking.InvalidateRouteCachesAsync(ct);
        return Ok(new { count, message = $"Recalculated road geometry for {count} route(s)." });
    }

    [HttpPost("routes")]
    public async Task<IActionResult> CreateRoute([FromBody] RouteRequest request, CancellationToken ct)
    {
        var error = await ValidateRoute(request, null, ct);
        if (error is not null) return BadRequest(new { message = error });

        var route = new BusRoute
        {
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            OperatorId = request.OperatorId,
            DistanceMode = request.DistanceMode.Trim(),
            IsActive = true
        };

        return await PersistRouteAsync(route, request, isCreate: true, ct);
    }

    [HttpPut("routes/{id:int}")]
    public async Task<IActionResult> UpdateRoute(int id, [FromBody] RouteRequest request, CancellationToken ct)
    {
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.IsActive, ct);
        if (route is null) return NotFound(new { message = "Route not found." });

        var error = await ValidateRoute(request, id, ct);
        if (error is not null) return BadRequest(new { message = error });

        route.Code = request.Code.Trim();
        route.Name = request.Name.Trim();
        route.OperatorId = request.OperatorId;
        route.DistanceMode = request.DistanceMode.Trim();

        return await PersistRouteAsync(route, request, isCreate: false, ct);
    }

    private async Task<IActionResult> PersistRouteAsync(BusRoute route, RouteRequest request, bool isCreate, CancellationToken ct)
    {
        IDbContextTransaction? tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            if (isCreate)
                _db.Routes.Add(route);

            await _db.SaveChangesAsync(ct);
            await ReplaceStops(route.Id, request, ct);
            await _geometry.RebuildAsync(route.Id, ct);

            if (isCreate)
                await _tracking.Audit(TryCurrentUserId(), "Create", "Route", route.Id.ToString(), route.Code, ct);

            if (tx is not null)
                await tx.CommitAsync(ct);

            await _tracking.InvalidateRouteCachesAsync(ct);
            return Ok(new { route.Id });
        }
        catch (DbUpdateException ex)
        {
            if (tx is not null)
                await tx.RollbackAsync(CancellationToken.None);

            var detail = ex.InnerException?.Message ?? ex.Message;
            if (detail.Contains("UQ_Routes_Code", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "That route code is already used." });
            }
            if (detail.Contains("FK_Routes_Operators", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Choose an active operator." });
            }
            if (detail.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Route code or name exceeds maximum allowed length." });
            }
            throw;
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync();
        }
    }

    private Guid? TryCurrentUserId()
    {
        var raw = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
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
        await _tracking.InvalidateFareCachesAsync(ct);
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
        await _tracking.InvalidateRouteCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("routes/bulk-delete")]
    public async Task<IActionResult> BulkDeleteRoutes([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No route IDs provided." });

        var routes = await _db.Routes.Where(r => request.Ids.Contains(r.Id) && r.IsActive).ToListAsync(ct);
        var hasSchedRouteIds = await _db.Schedules
            .Where(s => request.Ids.Contains(s.RouteId) && s.IsActive)
            .Select(s => s.RouteId)
            .Distinct()
            .ToListAsync(ct);

        var hasSchedSet = hasSchedRouteIds.ToHashSet();
        var deleted = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var r in routes)
        {
            if (hasSchedSet.Contains(r.Id))
            {
                skipped++;
                reasons.Add($"Route '{r.Code}' ({r.Name}) still has active schedules.");
                continue;
            }
            r.IsActive = false;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);
        if (deleted > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Route", string.Join(",", request.Ids), $"Deleted {deleted}, skipped {skipped}", ct);
        return Ok(new BulkDeleteResponse(deleted, skipped, $"Deleted {deleted} route(s). {skipped} skipped.", reasons));
    }

    [HttpPost("routes/bulk-update")]
    public async Task<IActionResult> BulkUpdateRoutes([FromBody] BulkUpdateRoutesRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No route IDs provided." });

        if (request.OperatorId.HasValue && request.OperatorId.Value > 0
            && !await _db.Operators.AnyAsync(o => o.Id == request.OperatorId.Value && o.IsActive, ct))
        {
            return BadRequest(new { message = "Choose an active operator." });
        }

        string? nextMode = null;
        if (!string.IsNullOrWhiteSpace(request.DistanceMode))
        {
            nextMode = request.DistanceMode.Trim();
            if (nextMode is not ("Auto" or "Manual"))
                return BadRequest(new { message = "Choose automatic or manual distance calculation." });
        }

        var routes = await _db.Routes.Where(r => request.Ids.Contains(r.Id) && r.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var r in routes)
        {
            var modified = false;
            if (request.OperatorId.HasValue && request.OperatorId.Value > 0)
            {
                r.OperatorId = request.OperatorId.Value;
                modified = true;
            }
            if (nextMode is not null)
            {
                r.DistanceMode = nextMode;
                modified = true;
            }
            if (modified) updated++;
        }

        await _db.SaveChangesAsync(ct);
        if (updated > 0) await _tracking.InvalidateRouteCachesAsync(ct);
        await _tracking.Audit(TryCurrentUserId(), "BulkUpdate", "Route", string.Join(",", request.Ids), $"Updated {updated} routes", ct);
        return Ok(new BulkOperationResult(updated, routes.Count - updated, $"Updated {updated} route(s)."));
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
        var error = await ValidateSchedule(request, null, ct);
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
        await _tracking.InvalidateScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "Create", "Schedule", row.Id.ToString(), row.DepartureTime.ToString("HH:mm"), ct);
        return Ok(new { row.Id });
    }

    [HttpPut("schedules/{id:int}")]
    public async Task<IActionResult> UpdateSchedule(int id, ScheduleRequest request, CancellationToken ct)
    {
        var row = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);
        if (row is null) return NotFound(new { message = "Schedule not found." });
        var error = await ValidateSchedule(request, id, ct);
        if (error is not null) return BadRequest(new { message = error });
        row.RouteId = request.RouteId;
        row.BusId = request.BusId;
        row.DriverId = request.DriverId;
        row.DepartureTime = TimeOnly.Parse(request.DepartureTime);
        row.ServiceDays = string.IsNullOrWhiteSpace(request.ServiceDays) ? "1111111" : request.ServiceDays.Trim();
        await _db.SaveChangesAsync(ct);
        await _tracking.InvalidateScheduleCachesAsync(ct);
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
        await _tracking.InvalidateScheduleCachesAsync(ct);
        return NoContent();
    }

    [HttpPost("schedules/bulk-delete")]
    public async Task<IActionResult> BulkDeleteSchedules([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No schedule IDs provided." });

        var schedules = await _db.Schedules.Where(s => request.Ids.Contains(s.Id) && s.IsActive).ToListAsync(ct);
        var runningScheduleIds = await _db.Trips
            .Where(t => request.Ids.Contains(t.ScheduleId) && t.Status == "Running")
            .Select(t => t.ScheduleId)
            .Distinct()
            .ToListAsync(ct);

        var runningSet = runningScheduleIds.ToHashSet();
        var deleted = 0;
        var skipped = 0;
        var reasons = new List<string>();

        foreach (var s in schedules)
        {
            if (runningSet.Contains(s.Id))
            {
                skipped++;
                reasons.Add($"Schedule #{s.Id} currently has a running trip in progress.");
                continue;
            }
            s.IsActive = false;
            deleted++;
        }

        await _db.SaveChangesAsync(ct);
        if (deleted > 0) await _tracking.InvalidateScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkDelete", "Schedule", string.Join(",", request.Ids), $"Deleted {deleted}, skipped {skipped}", ct);
        return Ok(new BulkDeleteResponse(deleted, skipped, $"Deleted {deleted} schedule(s). {skipped} skipped.", reasons));
    }

    [HttpPost("schedules/bulk-update")]
    public async Task<IActionResult> BulkUpdateSchedules([FromBody] BulkUpdateSchedulesRequest request, CancellationToken ct)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
            return BadRequest(new { message = "No schedule IDs provided." });

        var schedules = await _db.Schedules.Where(s => request.Ids.Contains(s.Id) && s.IsActive).ToListAsync(ct);
        var updated = 0;

        foreach (var s in schedules)
        {
            var modified = false;
            if (!string.IsNullOrWhiteSpace(request.ServiceDays))
            {
                s.ServiceDays = request.ServiceDays.Trim();
                modified = true;
            }
            if (request.BusId.HasValue)
            {
                s.BusId = request.BusId.Value > 0 ? request.BusId.Value : null;
                modified = true;
            }
            if (request.DriverId.HasValue)
            {
                s.DriverId = request.DriverId.Value > 0 ? request.DriverId.Value : null;
                modified = true;
            }
            if (modified) updated++;
        }

        await _db.SaveChangesAsync(ct);
        if (updated > 0) await _tracking.InvalidateScheduleCachesAsync(ct);
        await _tracking.Audit(User.Id(), "BulkUpdate", "Schedule", string.Join(",", request.Ids), $"Updated {updated} schedules", ct);
        return Ok(new BulkOperationResult(updated, schedules.Count - updated, $"Updated {updated} schedule(s)."));
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

    [HttpGet("reports/feedback")]
    public async Task<IActionResult> PassengerFeedbackReports(
        [FromQuery] string? status = null,
        [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        var query = _db.BusInfoReports.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == status.Trim());
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Category == category.Trim());

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
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
                r.UserId,
                r.TripId,
                r.RouteId,
                routeCode = route?.Code,
                routeName = route?.Name,
                r.BusId,
                r.FleetNumber,
                r.Category,
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

    [HttpPost("reports/feedback/{id:int}/status")]
    public async Task<IActionResult> UpdatePassengerFeedbackStatus(int id, UpdateBusReportStatusRequest request, CancellationToken ct)
    {
        var status = (request.Status ?? "").Trim();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Published", "Verified", "Rejected", "Reviewed", "Open", "Resolved"
        };
        if (!allowed.TryGetValue(status, out var canonicalStatus))
            return BadRequest(new { message = "Status must be Published, Verified, Rejected, Reviewed, Open, or Resolved." });

        var row = await _db.BusInfoReports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return NotFound(new { message = "Report not found." });

        row.Status = canonicalStatus;
        if (!string.IsNullOrWhiteSpace(request.AdminNotes))
        {
            var notes = request.AdminNotes.Trim();
            row.AdminNotes = notes.Length > 500 ? notes[..500] : notes;
        }

        var adminIdentifier = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(adminIdentifier))
        {
            var adminUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == User.Id(), ct);
            adminIdentifier = adminUser?.FullName ?? "Transport Admin";
        }

        row.VerifiedAt = DateTime.UtcNow;
        row.VerifiedBy = adminIdentifier;

        // If published/verified and admin requested public alert on an active trip:
        if ((request.PublishAlert || canonicalStatus == "Published") && row.TripId.HasValue)
        {
            var tripUsers = await _db.Notifications
                .Where(n => n.TripId == row.TripId.Value)
                .Select(n => n.UserId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var uid in tripUsers)
            {
                _db.Notifications.Add(new Notification
                {
                    UserId = uid,
                    TripId = row.TripId,
                    Title = $"Verified Community Update · Bus {row.FleetNumber}",
                    Body = $"{row.Category}: {row.Description}",
                    Kind = row.Category.Contains("Delay", StringComparison.OrdinalIgnoreCase) ? "delayed" : "alert",
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        await _tracking.Audit(User.Id(), "VerifyCommunityUpdate", "BusInfoReport", row.Id.ToString(), $"Marked report #{row.Id} as {canonicalStatus}. Notes: {row.AdminNotes}", ct);
        return Ok(new
        {
            row.Id,
            row.Status,
            row.AdminNotes,
            verifiedAt = row.VerifiedAt is DateTime v ? IndiaClock.AsUtc(v) : (DateTime?)null,
            row.VerifiedBy
        });
    }

    private async Task ReplaceStops(int routeId, RouteRequest request, CancellationToken ct)
    {
        var old = await _db.RouteStops.Where(rs => rs.RouteId == routeId).ToListAsync(ct);
        _db.RouteStops.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        var seq = 1;
        for (var index = 0; index < request.StopIds.Count; index++)
        {
            var segmentDistance = request.DistanceMode == "Manual" && index > 0
                ? decimal.Round((decimal)request.SegmentDistancesKm![index - 1], 2)
                : (decimal?)null;
            _db.RouteStops.Add(new RouteStop
            {
                RouteId = routeId,
                StopId = request.StopIds[index],
                SequenceNo = seq++,
                SegmentDistanceKm = segmentDistance
            });
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

    private async Task<string?> ValidateRoute(RouteRequest? request, int? id, CancellationToken ct)
    {
        if (request is null) return "Enter the route details.";

        var code = (request.Code ?? "").Trim();
        var name = (request.Name ?? "").Trim();
        var distanceMode = (request.DistanceMode ?? "").Trim();
        var stopIds = request.StopIds;

        if (code.Length == 0) return "Enter a route code.";
        if (code.Length > 20) return "Route code must be 20 characters or fewer.";
        if (name.Length < 2) return "Enter a route name.";
        if (name.Length > 150) return "Route name must be 150 characters or fewer.";

        if (request.OperatorId is int opId &&
            (opId <= 0 || !await _db.Operators.AnyAsync(o => o.Id == opId && o.IsActive, ct)))
        {
            return "Choose an active operator.";
        }

        if (stopIds is null || stopIds.Count < 2) return "A route needs at least two stops.";
        if (stopIds.Distinct().Count() != stopIds.Count) return "Each stop can appear once.";
        if (distanceMode is not ("Auto" or "Manual")) return "Choose automatic or manual distance calculation.";
        if (distanceMode == "Manual")
        {
            if (request.SegmentDistancesKm is null
                || request.SegmentDistancesKm.Count != stopIds.Count - 1
                || request.SegmentDistancesKm.Any(distance => !double.IsFinite(distance) || distance < 0.01 || distance > 999999.99))
            {
                return "Enter a valid distance of at least 0.01 km for every leg between stops.";
            }
            if (request.SegmentDistancesKm.Sum() > 999999.99)
                return "Total route distance cannot exceed 999,999.99 km.";
        }

        var found = await _db.Stops.CountAsync(s => s.IsActive && stopIds.Contains(s.Id), ct);
        if (found != stopIds.Count) return "One of those stops is not available.";

        if (await _db.Routes.AnyAsync(r => r.Code.ToLower() == code.ToLower() && r.Id != id, ct))
            return "That route code is already used.";

        return null;
    }

    private async Task<string?> ValidateSchedule(
        ScheduleRequest request,
        int? scheduleId,
        CancellationToken ct)
    {
        if (!await _db.Routes.AnyAsync(r => r.Id == request.RouteId && r.IsActive, ct))
            return "Choose a route.";

        if (!TimeOnly.TryParse(request.DepartureTime, out var departureTime))
            return "Enter a departure time like 08:00.";

        if (request.BusId is int busId &&
            !await _db.Buses.AnyAsync(b => b.Id == busId && b.IsActive, ct))
        {
            return "Choose a bus.";
        }

        if (request.DriverId is int driverId &&
            !await _db.Drivers.AnyAsync(d => d.Id == driverId && d.IsActive, ct))
        {
            return "Choose a driver.";
        }

        var days = string.IsNullOrWhiteSpace(request.ServiceDays)
            ? "1111111"
            : request.ServiceDays.Trim();

        if (!Regex.IsMatch(days, "^[01]{7}$"))
            return "Service days must be 7 characters of 1 or 0, Monday through Sunday.";

        var route = await _db.Routes
            .AsNoTracking()
            .FirstAsync(r => r.Id == request.RouteId, ct);

        var routeMinutes = Math.Max(
            30,
            Geo.EtaMinutes((double)(route.DistanceKm ?? 0)));

        var newStart = departureTime.ToTimeSpan();
        var newEnd = newStart.Add(TimeSpan.FromMinutes(routeMinutes + 15));

        var existingSchedules = await _db.Schedules
            .AsNoTracking()
            .Include(s => s.Route)
            .Where(s =>
                s.IsActive &&
                s.Id != scheduleId &&
                ((request.BusId != null && s.BusId == request.BusId) ||
                 (request.DriverId != null && s.DriverId == request.DriverId)))
            .ToListAsync(ct);

        foreach (var existing in existingSchedules)
        {
            if (!ServiceDaysOverlap(days, existing.ServiceDays))
                continue;

            var existingDurationMinutes = Math.Max(
                30,
                Geo.EtaMinutes((double)(existing.Route.DistanceKm ?? 0)));

            var existingStart = existing.DepartureTime.ToTimeSpan();
            var existingEnd = existingStart.Add(
                TimeSpan.FromMinutes(existingDurationMinutes + 15));

            if (newStart < existingEnd && existingStart < newEnd)
            {
                return "The selected bus or driver already has an overlapping schedule. " +
                       "Use a different departure time.";
            }
        }

        return null;
    }

    private static bool ServiceDaysOverlap(string first, string second)
    {
        if (first.Length != 7 || second.Length != 7)
            return true;

        for (var i = 0; i < 7; i++)
        {
            if (first[i] == '1' && second[i] == '1')
                return true;
        }

        return false;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
