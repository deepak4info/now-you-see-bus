using System.Collections.Concurrent;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

public class TrackingService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> TripGates = new();
    private readonly AppDbContext _db;
    private readonly IHubContext<TrackingHub> _hub;

    public TrackingService(AppDbContext db, IHubContext<TrackingHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task<bool> OperationsAreLive(CancellationToken ct = default)
    {
        var setting = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "OperationsLive", ct);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Stop?> FindStop(string name, CancellationToken ct = default)
    {
        var n = name.Trim();
        var stops = await _db.Stops.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        return stops.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? stops.FirstOrDefault(s => s.Code.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? stops.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<TrackSnapshotDto>> SearchAsync(string from, string to, DateOnly date, CancellationToken ct = default)
    {
        var fromStop = await FindStop(from, ct);
        var toStop = await FindStop(to, ct);
        if (fromStop is null || toStop is null) return new();

        var routeStops = await _db.RouteStops.AsNoTracking().Where(rs => rs.StopId == fromStop.Id || rs.StopId == toStop.Id).ToListAsync(ct);
        var routeIds = routeStops
            .GroupBy(rs => rs.RouteId)
            .Where(g =>
            {
                var origin = g.FirstOrDefault(x => x.StopId == fromStop.Id);
                var dest = g.FirstOrDefault(x => x.StopId == toStop.Id);
                return origin is not null && dest is not null && origin.SequenceNo < dest.SequenceNo;
            })
            .Select(g => g.Key)
            .ToList();

        if (routeIds.Count == 0) return new();

        var day = IndiaClock.DayIndex(date);
        var schedules = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.BusId != null && routeIds.Contains(s.RouteId))
            .ToListAsync(ct);
        schedules = schedules.Where(s => RunsOn(s.ServiceDays, day)).ToList();

        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var trips = await QueryTrips()
            .Where(t => scheduleIds.Contains(t.ScheduleId) && t.ServiceDate == date)
            .ToListAsync(ct);

        var results = new List<TrackSnapshotDto>();
        foreach (var schedule in schedules)
        {
            var trip = trips.FirstOrDefault(t => t.ScheduleId == schedule.Id);
            results.Add(trip is not null
                ? await BuildSnapshot(trip, fromStop.Name, toStop.Name, ct)
                : await BuildScheduleSnapshot(schedule, fromStop.Name, toStop.Name, date, ct));
        }

        return results
            .OrderBy(r => r.Status == "Running" ? 0 : r.Status == "Scheduled" ? 1 : 2)
            .ThenBy(r => r.DepartureTime)
            .ToList();
    }

    public async Task<TrackSnapshotDto?> GetByFleetAsync(
        string fleet, string? from, string? to, int? requestedRouteId = null, CancellationToken ct = default)
    {
        var normalized = NormalizeFleet(fleet);
        var bus = await _db.Buses.AsNoTracking().FirstOrDefaultAsync(
            b => b.IsActive && (b.FleetNumber == normalized || b.RegistrationNumber == normalized), ct);
        if (bus is null) return null;

        List<int>? legRouteIds = null;
        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
        {
            var fromStop = await FindStop(from, ct);
            var toStop = await FindStop(to, ct);
            if (fromStop is null || toStop is null || fromStop.Id == toStop.Id) return null;

            var legStops = await _db.RouteStops.AsNoTracking()
                .Where(rs => rs.StopId == fromStop.Id || rs.StopId == toStop.Id)
                .ToListAsync(ct);
            legRouteIds = legStops
                .GroupBy(rs => rs.RouteId)
                .Where(group =>
                {
                    var origin = group.FirstOrDefault(rs => rs.StopId == fromStop.Id);
                    var destination = group.FirstOrDefault(rs => rs.StopId == toStop.Id);
                    return origin is not null && destination is not null
                        && origin.SequenceNo < destination.SequenceNo;
                })
                .Select(group => group.Key)
                .ToList();
            if (legRouteIds.Count == 0) return null;
        }

        if (requestedRouteId is int routeId)
        {
            if (legRouteIds is not null && !legRouteIds.Contains(routeId)) return null;
            legRouteIds = [routeId];
        }

        var today = IndiaClock.Today();
        var trips = await QueryTrips().Where(t => t.BusId == bus.Id && t.ServiceDate == today).ToListAsync(ct);
        var matchingTrips = legRouteIds is null ? trips : trips.Where(t => legRouteIds.Contains(t.RouteId));
        var trip = matchingTrips
            .OrderByDescending(t => t.Status == "Running")
            .ThenByDescending(t => t.Status == "Scheduled")
            .ThenByDescending(t => t.Id)
            .FirstOrDefault();
        if (trip is not null) return await BuildSnapshot(trip, from, to, ct);

        var schedule = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.BusId == bus.Id
                && (legRouteIds == null || legRouteIds.Contains(s.RouteId)))
            .OrderBy(s => s.DepartureTime)
            .FirstOrDefaultAsync(ct);
        return schedule is null ? null : await BuildScheduleSnapshot(schedule, from, to, today, ct);
    }

    public async Task<TrackSnapshotDto?> GetTripAsync(int tripId, CancellationToken ct = default)
    {
        var trip = await QueryTrips().FirstOrDefaultAsync(t => t.Id == tripId, ct);
        return trip is null ? null : await BuildSnapshot(trip, null, null, ct);
    }

    public async Task<List<TrackSnapshotDto>> LiveAsync(CancellationToken ct = default)
    {
        var today = IndiaClock.Today();
        var trips = await QueryTrips().Where(t => t.ServiceDate == today && t.Status == "Running").ToListAsync(ct);
        var list = new List<TrackSnapshotDto>();
        foreach (var trip in trips)
            list.Add(await BuildSnapshot(trip, null, null, ct));
        return list;
    }

    public async Task<TrackSnapshotDto?> GetRouteSnapshotAsync(int routeId, CancellationToken ct = default)
    {
        var today = IndiaClock.Today();

        // 1. Live running trip on this route today
        var runningTrip = await QueryTrips()
            .Where(t => t.RouteId == routeId && t.ServiceDate == today && t.Status == "Running")
            .OrderByDescending(t => t.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (runningTrip is not null)
            return await BuildSnapshot(runningTrip, null, null, ct);

        // 2. Scheduled trip on this route today
        var scheduledTrip = await QueryTrips()
            .Where(t => t.RouteId == routeId && t.ServiceDate == today)
            .OrderBy(t => t.Schedule.DepartureTime)
            .FirstOrDefaultAsync(ct);
        if (scheduledTrip is not null)
            return await BuildSnapshot(scheduledTrip, null, null, ct);

        // 3. Fallback to active schedule for this route
        var schedule = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.RouteId == routeId && s.BusId != null)
            .OrderBy(s => s.DepartureTime)
            .FirstOrDefaultAsync(ct);
        if (schedule is not null)
            return await BuildScheduleSnapshot(schedule, null, null, today, ct);

        // 4. Fallback to route definition itself
        var route = await _db.Routes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == routeId, ct);
        if (route is null) return null;
        var routeStops = await LoadRouteStops(routeId, ct);
        var path = await LoadPath(routeId, ct);
        var firstStop = routeStops.FirstOrDefault()?.Stop.Name ?? "";
        var lastStop = routeStops.LastOrDefault()?.Stop.Name ?? "";

        return new TrackSnapshotDto
        {
            RouteId = route.Id,
            RouteCode = route.Code,
            RouteName = route.Name,
            FleetNumber = "SCHED",
            RegistrationNumber = "Scheduled Service",
            BusType = "Haryana Roadways",
            Capacity = 52,
            OperatorName = "Haryana Roadways",
            Status = "Scheduled",
            DepartureTime = "08:00",
            ServiceDate = today.ToString("yyyy-MM-dd"),
            FromStop = firstStop,
            ToStop = lastStop,
            Stops = routeStops.Select(s => new StopOnTripDto
            {
                StopId = s.StopId,
                Name = s.Stop.Name,
                Code = s.Stop.Code,
                Sequence = s.SequenceNo,
                Latitude = (double)s.Stop.Latitude,
                Longitude = (double)s.Stop.Longitude,
                DistanceKm = (double)s.DistanceFromOriginKm,
                ScheduledTime = "08:00",
                Status = "Scheduled",
                DisplayStatus = "Scheduled"
            }).ToList(),
            Path = path.Select(p => new PointDto(p.Lat, p.Lng)).ToList()
        };
    }

    public async Task<List<TrackSnapshotDto>> GetAllActiveRouteSnapshotsAsync(CancellationToken ct = default)
    {
        var routes = await _db.Routes.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct);
        var list = new List<TrackSnapshotDto>();
        foreach (var r in routes)
        {
            var snap = await GetRouteSnapshotAsync(r.Id, ct);
            if (snap is not null) list.Add(snap);
        }
        return list;
    }

    public async Task<int> MaterializeToday(CancellationToken ct = default)
    {
        var today = IndiaClock.Today();
        var day = IndiaClock.DayIndex(today);
        var schedules = await _db.Schedules.Where(s => s.IsActive && s.BusId != null && s.DriverId != null).ToListAsync(ct);
        var created = 0;
        foreach (var schedule in schedules)
        {
            if (!RunsOn(schedule.ServiceDays, day)) continue;
            var exists = await _db.Trips.AnyAsync(t => t.ScheduleId == schedule.Id && t.ServiceDate == today, ct);
            if (exists) continue;
            _db.Trips.Add(new Trip
            {
                ScheduleId = schedule.Id,
                BusId = schedule.BusId!.Value,
                DriverId = schedule.DriverId!.Value,
                RouteId = schedule.RouteId,
                ServiceDate = today,
                Status = "Scheduled"
            });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        return created;
    }

    public async Task<TrackSnapshotDto> StartAsync(Trip trip, string mode, Guid? userId, CancellationToken ct = default)
    {
        trip.Status = "Running";
        trip.StartedAt = DateTime.UtcNow;
        trip.Simulation = mode == "simulate";
        trip.OdometerKm = 0;
        await _db.SaveChangesAsync(ct);
        await Notify(trip, "started", $"Bus {trip.Bus.FleetNumber} has started",
            $"Route {trip.Route.Code} left on its {IndiaClock.Clock(trip.Schedule.DepartureTime)} service.", ct);
        await Audit(userId, "StartTrip", "Trip", trip.Id.ToString(), $"Bus {trip.Bus.FleetNumber} {mode}", ct);

        var path = await LoadPath(trip.RouteId, ct);
        var origin = path.Count > 0 ? path[0] : new LatLng(29.1492, 75.7217);
        return await IngestAsync(trip.Id, origin.Lat, origin.Lng, 0, null, trip.Simulation, ct);
    }

    public async Task<TrackSnapshotDto> EnableSimulationAsync(Trip trip, CancellationToken ct = default)
    {
        if (trip.Status != "Running")
            return await StartAsync(trip, "simulate", trip.Driver.UserId, ct);
        trip.Simulation = true;
        await _db.SaveChangesAsync(ct);
        var snap = await BuildSnapshot(trip, null, null, ct);
        await Publish(snap, ct);
        return snap;
    }

    public async Task<TrackSnapshotDto> IngestAsync(int tripId, double lat, double lng, double? speed, double? heading, bool keepSimulation, CancellationToken ct = default)
    {
        var gate = TripGates.GetOrAdd(tripId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await IngestCoreAsync(tripId, lat, lng, speed, heading, keepSimulation, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TrackSnapshotDto> IngestCoreAsync(int tripId, double lat, double lng, double? speed, double? heading, bool keepSimulation, CancellationToken ct)
    {
        var trip = await LoadTrip(tripId, ct);
        if (trip.Status != "Running")
            throw new InvalidOperationException("This trip is not running.");

        if (!keepSimulation) trip.Simulation = false;

        _db.GpsPings.Add(new GpsPing
        {
            TripId = trip.Id,
            Latitude = decimal.Round((decimal)lat, 6),
            Longitude = decimal.Round((decimal)lng, 6),
            SpeedKmph = speed is null ? null : decimal.Round((decimal)speed.Value, 2),
            Heading = heading is null ? null : decimal.Round((decimal)heading.Value, 2),
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow
        });

        var routeStops = await LoadRouteStops(trip.RouteId, ct);
        var path = await LoadPath(trip.RouteId, ct);
        var pathDistance = path.Count == 0 ? 0 : Geo.Project(new LatLng(lat, lng), path).DistanceAlongKm;
        var along = RouteDistanceAtPathDistance(routeStops, path, pathDistance);
        var displaySpeed = keepSimulation ? (double?)null : speed;

        var etas = await EnsureStopEtas(trip, routeStops, ct);

        var currentIndex = -1;
        for (var i = 0; i < routeStops.Count; i++)
        {
            if (along + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                currentIndex = i;
        }

        ApplyStopProgress(trip, etas, routeStops, along, currentIndex, displaySpeed, lat, lng);

        var last = routeStops.Count == 0 ? null : routeStops[^1];
        var arrived = last is not null
            && Geo.HaversineKm(lat, lng, (double)last.Stop.Latitude, (double)last.Stop.Longitude) < 0.35
            && along + 0.4 >= (double)last.DistanceFromOriginKm;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            foreach (var entry in _db.ChangeTracker.Entries<TripStopEta>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            etas = await _db.TripStopEtas.Where(e => e.TripId == trip.Id).ToListAsync(ct);
            ApplyStopProgress(trip, etas, routeStops, along, currentIndex, displaySpeed, lat, lng);
            await _db.SaveChangesAsync(ct);
        }
        if (arrived) await CompleteCore(trip, null, ct);

        var snap = await BuildSnapshot(trip, null, null, ct);
        await Publish(snap, ct);
        return snap;
    }

    private async Task<List<TripStopEta>> EnsureStopEtas(Trip trip, List<RouteStop> routeStops, CancellationToken ct)
    {
        var etas = await _db.TripStopEtas.Where(e => e.TripId == trip.Id).ToListAsync(ct);
        var seen = etas.Select(e => e.StopId).ToHashSet();
        var departureUtc = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime);

        foreach (var rs in routeStops)
        {
            var schedTime = rs.SequenceNo == 1
                ? departureUtc
                : departureUtc.AddMinutes(Geo.EtaMinutes((double)rs.DistanceFromOriginKm));

            if (!seen.Add(rs.StopId))
            {
                var existing = etas.First(e => e.StopId == rs.StopId);
                existing.ScheduledArrival ??= schedTime;
                existing.ScheduledDeparture ??= rs.SequenceNo == 1 ? departureUtc : schedTime;
                continue;
            }

            var row = new TripStopEta
            {
                TripId = trip.Id,
                StopId = rs.StopId,
                SequenceNo = rs.SequenceNo,
                Status = "Pending",
                ScheduledArrival = schedTime,
                ScheduledDeparture = rs.SequenceNo == 1 ? departureUtc : schedTime,
                EstimatedArrival = schedTime,
                EstimatedDeparture = rs.SequenceNo == 1 ? departureUtc : schedTime,
                DelayMinutes = 0,
                LastUpdatedAt = DateTime.UtcNow
            };
            _db.TripStopEtas.Add(row);
            etas.Add(row);
        }
        return etas;
    }

    private static void ApplyStopProgress(Trip trip, List<TripStopEta> etas, List<RouteStop> routeStops, double along, int currentIndex, double? displaySpeed, double? lat = null, double? lng = null)
    {
        var now = DateTime.UtcNow;
        var departureUtc = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime);

        for (var i = 0; i < routeStops.Count; i++)
        {
            var rs = routeStops[i];
            var row = etas.FirstOrDefault(e => e.StopId == rs.StopId);
            if (row is null) continue;

            row.ScheduledArrival ??= rs.SequenceNo == 1 ? departureUtc : departureUtc.AddMinutes(Geo.EtaMinutes((double)rs.DistanceFromOriginKm));
            row.ScheduledDeparture ??= rs.SequenceNo == 1 ? departureUtc : row.ScheduledArrival;

            if (i < currentIndex)
            {
                // Bus has departed this stop
                row.Status = "Departed";
                row.EtaMinutes = 0;
                row.ActualArrival ??= row.ScheduledArrival;
                row.ActualDeparture ??= now;
                if (row.ScheduledDeparture.HasValue && row.ActualDeparture.HasValue)
                {
                    row.DelayMinutes = (int)Math.Round((row.ActualDeparture.Value - row.ScheduledDeparture.Value).TotalMinutes);
                }
            }
            else if (i == currentIndex)
            {
                // Bus is currently at this stop or just reached it
                var isNearStop = lat.HasValue && lng.HasValue
                    && Geo.HaversineKm(lat.Value, lng.Value, (double)rs.Stop.Latitude, (double)rs.Stop.Longitude) < 0.25;

                row.ActualArrival ??= now;
                row.EtaMinutes = 0;
                row.Status = isNearStop ? "Arrived" : "Current";
                if (row.ScheduledArrival.HasValue && row.ActualArrival.HasValue)
                {
                    row.DelayMinutes = (int)Math.Round((row.ActualArrival.Value - row.ScheduledArrival.Value).TotalMinutes);
                }
            }
            else
            {
                // Upcoming stop: dynamically recalculate ETA based on current location and travel time
                var remain = Math.Max(0, (double)rs.DistanceFromOriginKm - along);
                var travelMins = Geo.EtaMinutes(remain, displaySpeed);
                row.EtaMinutes = travelMins;
                row.EstimatedArrival = now.AddMinutes(travelMins);
                row.EstimatedDeparture = row.EstimatedArrival;
                if (row.ScheduledArrival.HasValue)
                {
                    var delay = (int)Math.Round((row.EstimatedArrival.Value - row.ScheduledArrival.Value).TotalMinutes);
                    row.DelayMinutes = delay;
                    row.Status = delay > 3 ? "Delayed" : (delay < -3 ? "Early" : "OnTime");
                }
                else
                {
                    row.DelayMinutes = 0;
                    row.Status = i == currentIndex + 1 ? "Next" : "Pending";
                }
            }
            row.LastUpdatedAt = now;
        }
    }

    private static void ComputeScheduledStopEtas(Trip trip, List<TripStopEta> etas, List<RouteStop> routeStops)
    {
        var now = DateTime.UtcNow;
        var departureUtc = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime);
        var departs = (int)Math.Round((departureUtc - now).TotalMinutes);
        var delay = departs < -2 ? -departs : 0;

        for (var i = 0; i < routeStops.Count; i++)
        {
            var rs = routeStops[i];
            var row = etas.FirstOrDefault(e => e.StopId == rs.StopId);
            if (row is null) continue;

            var travelMins = Geo.EtaMinutes((double)rs.DistanceFromOriginKm);
            var schedTime = rs.SequenceNo == 1 ? departureUtc : departureUtc.AddMinutes(travelMins);

            row.ScheduledArrival = schedTime;
            row.ScheduledDeparture = rs.SequenceNo == 1 ? departureUtc : schedTime;
            row.EstimatedArrival = schedTime.AddMinutes(delay);
            row.EstimatedDeparture = row.EstimatedArrival;
            row.DelayMinutes = delay;
            row.Status = delay > 3 ? "Delayed" : "Scheduled";
            row.EtaMinutes = delay > 0 ? travelMins + delay : travelMins;
            row.LastUpdatedAt = now;
        }
    }

    public async Task<TrackSnapshotDto> EndAsync(Trip trip, Guid? userId, CancellationToken ct = default)
    {
        await CompleteCore(trip, userId, ct);
        var snap = await BuildSnapshot(trip, null, null, ct);
        await Publish(snap, ct);
        return snap;
    }

    public async Task AdvanceSimulations(CancellationToken ct = default)
    {
        var ids = await _db.Trips.Where(t => t.Status == "Running" && t.Simulation).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            var trip = await LoadTrip(id, ct);
            if (!trip.Simulation || trip.Status != "Running") continue;
            var path = await LoadPath(trip.RouteId, ct);
            if (path.Count < 2) continue;
            var routeStops = await LoadRouteStops(trip.RouteId, ct);
            var total = routeStops.Count > 0 ? (double)routeStops[^1].DistanceFromOriginKm : Geo.Length(path);
            var nextKm = (double)trip.OdometerKm + 0.22;
            if (nextKm >= total - 0.05)
            {
                trip.OdometerKm = decimal.Round((decimal)total, 3);
                var end = path[^1];
                await IngestAsync(trip.Id, end.Lat, end.Lng, Geo.RoadSpeedKmph, null, true, ct);
                continue;
            }
            var prev = Geo.PointAt(path, PathDistanceAtRouteDistance(routeStops, path, (double)trip.OdometerKm));
            var pos = Geo.PointAt(path, PathDistanceAtRouteDistance(routeStops, path, nextKm));
            trip.OdometerKm = decimal.Round((decimal)nextKm, 3);
            await IngestAsync(trip.Id, pos.Lat, pos.Lng, Geo.RoadSpeedKmph, Geo.Bearing(prev, pos), true, ct);
        }
    }

    public async Task Notify(Trip trip, string kind, string title, string body, CancellationToken ct = default)
    {
        var userIds = await _db.Favorites
            .Where(f => f.BusId == trip.BusId || f.RouteId == trip.RouteId)
            .Select(f => f.UserId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var userId in userIds)
        {
            _db.Notifications.Add(new Notification
            {
                UserId = userId,
                TripId = trip.Id,
                Title = title,
                Body = body,
                Kind = kind,
                CreatedAt = DateTime.UtcNow
            });
        }
        if (userIds.Count > 0) await _db.SaveChangesAsync(ct);
    }

    public async Task Audit(Guid? userId, string action, string entity, string? entityId, string? detail, CancellationToken ct = default)
    {
        if (entityId != null && entityId.Length > 40)
            entityId = entityId[..40];
        if (detail != null && detail.Length > 500)
            detail = detail[..500];
        if (action != null && action.Length > 80)
            action = action[..80];
        if (entity != null && entity.Length > 80)
            entity = entity[..80];

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action ?? "",
            Entity = entity ?? "",
            EntityId = entityId,
            Detail = detail,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task CompleteCore(Trip trip, Guid? userId, CancellationToken ct)
    {
        if (trip.Status == "Completed") return;
        trip.Status = "Completed";
        trip.EndedAt = DateTime.UtcNow;
        trip.Simulation = false;
        await _db.SaveChangesAsync(ct);
        await Notify(trip, "completed", "Trip completed",
            $"Bus {trip.Bus.FleetNumber} finished route {trip.Route.Code}.", ct);
        await Audit(userId, "CompleteTrip", "Trip", trip.Id.ToString(), $"Bus {trip.Bus.FleetNumber} completed", ct);
    }

    private async Task<TrackSnapshotDto> BuildSnapshot(Trip trip, string? from, string? to, CancellationToken ct)
    {
        var routeStops = await LoadRouteStops(trip.RouteId, ct);
        var path = await LoadPath(trip.RouteId, ct);
        var ping = await _db.GpsPings.AsNoTracking()
            .Where(p => p.TripId == trip.Id)
            .OrderByDescending(p => p.RecordedAt)
            .ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
        var live = await OperationsAreLive(ct);
        double? along = ping is null || path.Count == 0
            ? (trip.Status == "Running" ? (double)trip.OdometerKm : null)
            : RouteDistanceAtPathDistance(
                routeStops,
                path,
                Geo.Project(new LatLng((double)ping.Latitude, (double)ping.Longitude), path).DistanceAlongKm);
        double? speed = ping?.SpeedKmph is decimal s ? (double)s : null;

        var etas = await EnsureStopEtas(trip, routeStops, ct);
        if (trip.Status == "Running" && along.HasValue)
        {
            var currentIndex = -1;
            for (var i = 0; i < routeStops.Count; i++)
            {
                if (along.Value + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                    currentIndex = i;
            }
            ApplyStopProgress(trip, etas, routeStops, along.Value, currentIndex, speed, ping is null ? null : (double?)ping.Latitude, ping is null ? null : (double?)ping.Longitude);
        }
        else if (trip.Status == "Scheduled")
        {
            ComputeScheduledStopEtas(trip, etas, routeStops);
        }

        var (stops, current, next, etaNext, etaDest, tripDelay, delayStatus) = DescribeStops(trip, routeStops, etas, trip.Schedule.DepartureTime, trip.Status, along, speed);
        var (fare, etaYou, boardingPassed, etaAlight) = await PassengerLeg(trip.RouteId, routeStops, from, to, along, speed, ct);
        if (etaAlight is not null) etaDest = etaAlight;

        double? lat = ping is null ? null : (double)ping.Latitude;
        double? lng = ping is null ? null : (double)ping.Longitude;
        if (lat is null && routeStops.Count > 0)
        {
            var anchor = trip.Status == "Completed" ? routeStops[^1] : routeStops[0];
            lat = (double)anchor.Stop.Latitude;
            lng = (double)anchor.Stop.Longitude;
        }

        int? departs = null;
        var departureUtc = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime);
        if (trip.Status == "Scheduled")
        {
            departs = (int)Math.Round((departureUtc - DateTime.UtcNow).TotalMinutes);
        }

        var originEta = etas.FirstOrDefault(e => e.SequenceNo == 1);
        var actualDeparture = trip.StartedAt.HasValue
            ? IndiaClock.FormatClock12(trip.StartedAt.Value)
            : (originEta?.ActualDeparture is { } ad ? IndiaClock.FormatClock12(ad) : null);
        var estimatedDeparture = originEta?.EstimatedDeparture is { } ed
            ? IndiaClock.FormatClock12(ed)
            : IndiaClock.FormatClock12(trip.Schedule.DepartureTime);

        return new TrackSnapshotDto
        {
            TripId = trip.Id,
            ScheduleId = trip.ScheduleId,
            BusId = trip.BusId,
            RouteId = trip.RouteId,
            FleetNumber = trip.Bus.FleetNumber,
            RegistrationNumber = trip.Bus.RegistrationNumber,
            BusType = trip.Bus.BusType,
            Capacity = trip.Bus.Capacity,
            OperatorName = trip.Bus.Operator.Name,
            RouteCode = trip.Route.Code,
            RouteName = trip.Route.Name,
            Status = trip.Status,
            DriverName = trip.Driver.User.FullName,
            Latitude = lat,
            Longitude = lng,
            Heading = ping?.Heading is decimal h ? (double)h : null,
            SpeedKmph = speed,
            RecordedAt = ping is null ? null : IndiaClock.AsUtc(ping.RecordedAt),
            CurrentStop = current,
            NextStop = next,
            EtaMinutes = etaNext,
            DelayMinutes = tripDelay,
            OutsideSlot = tripDelay > 180,
            Simulation = trip.Simulation,
            OperationsLive = live,
            DepartureTime = IndiaClock.Clock(trip.Schedule.DepartureTime),
            ActualDepartureTime = actualDeparture,
            EstimatedDepartureTime = estimatedDeparture,
            DelayStatus = delayStatus,
            LastEtaUpdate = etas.Max(e => e.LastUpdatedAt) ?? (ping is null ? null : IndiaClock.AsUtc(ping.RecordedAt)),
            ServiceDate = trip.ServiceDate.ToString("yyyy-MM-dd"),
            Fare = fare,
            FromStop = from,
            ToStop = to,
            EtaToYouMinutes = etaYou,
            BoardingPassed = boardingPassed,
            EtaToDestinationMinutes = etaDest,
            DepartsInMinutes = departs,
            Stops = stops,
            Path = path.Select(p => new PointDto(p.Lat, p.Lng)).ToList()
        };
    }

    private async Task<TrackSnapshotDto> BuildScheduleSnapshot(Schedule schedule, string? from, string? to, DateOnly date, CancellationToken ct)
    {
        var routeStops = await LoadRouteStops(schedule.RouteId, ct);
        var path = await LoadPath(schedule.RouteId, ct);
        var departureUtc = IndiaClock.ToUtc(date, schedule.DepartureTime);
        var departs = (int)Math.Round((departureUtc - DateTime.UtcNow).TotalMinutes);
        var delay = (date == IndiaClock.Today() && departs < -2) ? -departs : 0;

        var etas = routeStops.Select(rs =>
        {
            var travelMins = Geo.EtaMinutes((double)rs.DistanceFromOriginKm);
            var schedTime = rs.SequenceNo == 1 ? departureUtc : departureUtc.AddMinutes(travelMins);
            return new TripStopEta
            {
                StopId = rs.StopId,
                SequenceNo = rs.SequenceNo,
                ScheduledArrival = schedTime,
                ScheduledDeparture = rs.SequenceNo == 1 ? departureUtc : schedTime,
                EstimatedArrival = schedTime.AddMinutes(delay),
                EstimatedDeparture = rs.SequenceNo == 1 ? departureUtc.AddMinutes(delay) : schedTime.AddMinutes(delay),
                DelayMinutes = delay,
                Status = delay > 3 ? "Delayed" : "Scheduled",
                EtaMinutes = delay > 0 ? travelMins + delay : travelMins,
                LastUpdatedAt = DateTime.UtcNow
            };
        }).ToList();

        var (stops, current, next, _, _, tripDelay, delayStatus) = DescribeStops(null, routeStops, etas, schedule.DepartureTime, "Scheduled", null, null, date);
        var (fare, _, _, _) = await PassengerLeg(schedule.RouteId, routeStops, from, to, null, null, ct);
        var live = await OperationsAreLive(ct);
        var anchor = routeStops.Count > 0 ? routeStops[0] : null;

        return new TrackSnapshotDto
        {
            ScheduleId = schedule.Id,
            BusId = schedule.Bus!.Id,
            RouteId = schedule.RouteId,
            FleetNumber = schedule.Bus.FleetNumber,
            RegistrationNumber = schedule.Bus.RegistrationNumber,
            BusType = schedule.Bus.BusType,
            Capacity = schedule.Bus.Capacity,
            OperatorName = schedule.Bus.Operator.Name,
            RouteCode = schedule.Route.Code,
            RouteName = schedule.Route.Name,
            Status = "Scheduled",
            DriverName = schedule.Driver?.User.FullName,
            Latitude = anchor is null ? null : (double)anchor.Stop.Latitude,
            Longitude = anchor is null ? null : (double)anchor.Stop.Longitude,
            OperationsLive = live,
            DepartureTime = IndiaClock.Clock(schedule.DepartureTime),
            ActualDepartureTime = null,
            EstimatedDepartureTime = delay > 0 ? IndiaClock.FormatClock12(departureUtc.AddMinutes(delay)) : IndiaClock.FormatClock12(schedule.DepartureTime),
            DelayStatus = delayStatus,
            LastEtaUpdate = DateTime.UtcNow,
            ServiceDate = date.ToString("yyyy-MM-dd"),
            Fare = fare,
            FromStop = from,
            ToStop = to,
            CurrentStop = current,
            NextStop = next,
            DepartsInMinutes = departs,
            DelayMinutes = departs < -180 ? null : (tripDelay ?? (delay > 0 ? delay : 0)),
            OutsideSlot = departs < -180,
            Stops = stops,
            Path = path.Select(p => new PointDto(p.Lat, p.Lng)).ToList()
        };
    }

    private static (List<StopOnTripDto> Stops, string? Current, string? Next, int? EtaNext, int? EtaDest, int? overallDelay, string delayStatus) DescribeStops(
        Trip? trip,
        List<RouteStop> routeStops,
        List<TripStopEta> etas,
        TimeOnly departure,
        string tripStatus,
        double? along,
        double? speed,
        DateOnly? serviceDate = null)
    {
        string? current = null;
        string? next = null;
        int? etaNext = null;
        int? etaDest = null;

        var currentIndex = -1;
        if (along is not null)
        {
            for (var i = 0; i < routeStops.Count; i++)
            {
                if (along.Value + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                    currentIndex = i;
            }
        }

        var stops = new List<StopOnTripDto>();
        for (var i = 0; i < routeStops.Count; i++)
        {
            var rs = routeStops[i];
            var row = etas.FirstOrDefault(e => e.StopId == rs.StopId);
            var dist = (double)rs.DistanceFromOriginKm;
            var schedTime = IndiaClock.ClockFromDeparture(departure, dist);

            string status;
            string displayStatus;
            int? etaMins = null;

            if (tripStatus == "Completed")
            {
                status = i == routeStops.Count - 1 ? "Current" : "Passed";
                displayStatus = i == routeStops.Count - 1 ? "Arrived" : "Departed";
                etaMins = 0;
            }
            else if (along is not null)
            {
                if (i < currentIndex)
                {
                    status = "Passed";
                    displayStatus = "Departed";
                    etaMins = 0;
                }
                else if (i == currentIndex)
                {
                    status = "Current";
                    displayStatus = row?.Status == "Arrived" ? "Arrived" : "Current";
                    etaMins = 0;
                    current = rs.Stop.Name;
                }
                else
                {
                    var remain = Math.Max(0, (double)rs.DistanceFromOriginKm - along.Value);
                    etaMins = Geo.EtaMinutes(remain, speed);
                    if (next is null)
                    {
                        next = rs.Stop.Name;
                        etaNext = etaMins;
                        status = "Next";
                    }
                    else
                    {
                        status = "Pending";
                    }
                    if (i == routeStops.Count - 1) etaDest = etaMins;

                    var delay = row?.DelayMinutes ?? 0;
                    displayStatus = delay > 3 ? "Delayed" : (delay < -3 ? "Early" : "On time");
                }
            }
            else if (tripStatus == "Scheduled")
            {
                status = i == 0 ? "Next" : "Pending";
                if (i == 0) next = rs.Stop.Name;
                var delay = row?.DelayMinutes ?? 0;
                displayStatus = delay > 3 ? "Delayed" : "Scheduled";
                etaMins = row?.EtaMinutes;
            }
            else
            {
                status = "Pending";
                displayStatus = "Scheduled";
            }

            stops.Add(new StopOnTripDto
            {
                StopId = rs.StopId,
                Name = rs.Stop.Name,
                Code = rs.Stop.Code,
                Sequence = rs.SequenceNo,
                Latitude = (double)rs.Stop.Latitude,
                Longitude = (double)rs.Stop.Longitude,
                DistanceKm = dist,
                ScheduledTime = schedTime,
                Status = status,
                EtaMinutes = etaMins,
                ScheduledArrival = IndiaClock.FormatClock12(row?.ScheduledArrival),
                EstimatedArrival = IndiaClock.FormatClock12(row?.EstimatedArrival),
                ActualArrival = IndiaClock.FormatClock12(row?.ActualArrival),
                ScheduledDeparture = IndiaClock.FormatClock12(row?.ScheduledDeparture),
                EstimatedDeparture = IndiaClock.FormatClock12(row?.EstimatedDeparture),
                ActualDeparture = IndiaClock.FormatClock12(row?.ActualDeparture),
                DelayMinutes = row?.DelayMinutes,
                DisplayStatus = displayStatus
            });
        }

        if (along is not null && currentIndex == stops.Count - 1) etaDest = 0;
        if (tripStatus == "Completed" && stops.Count > 0)
        {
            current = stops[^1].Name;
            next = null;
            etaNext = 0;
            etaDest = 0;
        }

        int? overallDelay = null;
        string delayStatus = "On time";

        if (tripStatus == "Completed")
        {
            delayStatus = "Completed";
            overallDelay = 0;
        }
        else if (tripStatus == "Scheduled")
        {
            var depUtc = IndiaClock.ToUtc(trip?.ServiceDate ?? serviceDate ?? IndiaClock.Today(), departure);
            var departs = (int)Math.Round((depUtc - DateTime.UtcNow).TotalMinutes);
            if (departs < -2)
            {
                overallDelay = -departs;
                delayStatus = $"{overallDelay} min delayed";
            }
            else
            {
                overallDelay = 0;
                delayStatus = "Scheduled";
            }
        }
        else // Running
        {
            var relevant = etas.FirstOrDefault(e => e.Status == "Current" || e.Status == "Arrived")
                        ?? etas.FirstOrDefault(e => e.Status == "Next" || e.Status == "Delayed" || e.Status == "OnTime")
                        ?? etas.LastOrDefault(e => e.Status == "Departed" || e.Status == "Passed");
            overallDelay = relevant?.DelayMinutes ?? 0;
            if (overallDelay > 3) delayStatus = $"{overallDelay} min delayed";
            else if (overallDelay < -3) delayStatus = $"{Math.Abs(overallDelay.Value)} min early";
            else delayStatus = "On time";
        }

        return (stops, current, next, etaNext, etaDest, overallDelay, delayStatus);
    }

    public async Task<TripEtaResponseDto?> GetTripEtasAsync(int tripId, CancellationToken ct = default)
    {
        var trip = await LoadTrip(tripId, ct);
        if (trip is null) return null;
        var snap = await BuildSnapshot(trip, null, null, ct);
        return new TripEtaResponseDto
        {
            TripId = trip.Id,
            FleetNumber = trip.Bus.FleetNumber,
            RouteName = trip.Route.Name,
            Status = trip.Status,
            DelayStatus = snap.DelayStatus ?? (snap.DelayMinutes > 3 ? $"{snap.DelayMinutes} min delayed" : "On time"),
            DelayMinutes = snap.DelayMinutes,
            ScheduledDeparture = snap.DepartureTime,
            ActualDeparture = snap.ActualDepartureTime,
            EstimatedDeparture = snap.EstimatedDepartureTime,
            LastUpdatedAt = snap.LastEtaUpdate ?? DateTime.UtcNow,
            Stops = snap.Stops
        };
    }

    public async Task<TrackSnapshotDto> MarkStopArrivalAsync(int tripId, int stopId, DateTime? arrivalTime, CancellationToken ct = default)
    {
        var gate = TripGates.GetOrAdd(tripId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var trip = await LoadTrip(tripId, ct);
            var routeStops = await LoadRouteStops(trip.RouteId, ct);
            var etas = await EnsureStopEtas(trip, routeStops, ct);

            var stopIndex = routeStops.FindIndex(rs => rs.StopId == stopId);
            if (stopIndex >= 0)
            {
                var now = arrivalTime ?? DateTime.UtcNow;
                var targetEta = etas.FirstOrDefault(e => e.StopId == stopId);
                if (targetEta is not null)
                {
                    targetEta.ActualArrival = now;
                    targetEta.Status = "Arrived";
                    if (targetEta.ScheduledArrival.HasValue)
                    {
                        targetEta.DelayMinutes = (int)Math.Round((now - targetEta.ScheduledArrival.Value).TotalMinutes);
                    }
                }

                // Recalculate upcoming stations starting from this stop's location and arrival time
                var currentAlong = (double)routeStops[stopIndex].DistanceFromOriginKm;
                for (var j = stopIndex + 1; j < routeStops.Count; j++)
                {
                    var upcoming = etas.FirstOrDefault(e => e.StopId == routeStops[j].StopId);
                    if (upcoming is not null)
                    {
                        var remainKm = (double)routeStops[j].DistanceFromOriginKm - currentAlong;
                        var travelMins = Geo.EtaMinutes(remainKm, Geo.RoadSpeedKmph);
                        upcoming.EtaMinutes = travelMins;
                        upcoming.EstimatedArrival = now.AddMinutes(travelMins);
                        upcoming.EstimatedDeparture = upcoming.EstimatedArrival;
                        if (upcoming.ScheduledArrival.HasValue)
                        {
                            var delay = (int)Math.Round((upcoming.EstimatedArrival.Value - upcoming.ScheduledArrival.Value).TotalMinutes);
                            upcoming.DelayMinutes = delay;
                            upcoming.Status = delay > 3 ? "Delayed" : (delay < -3 ? "Early" : "OnTime");
                        }
                        upcoming.LastUpdatedAt = DateTime.UtcNow;
                    }
                }

                await _db.SaveChangesAsync(ct);
            }

            var snap = await BuildSnapshot(trip, null, null, ct);
            await Publish(snap, ct);
            return snap;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TrackSnapshotDto> MarkStopDepartureAsync(int tripId, int stopId, DateTime? departureTime, CancellationToken ct = default)
    {
        var gate = TripGates.GetOrAdd(tripId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var trip = await LoadTrip(tripId, ct);
            var routeStops = await LoadRouteStops(trip.RouteId, ct);
            var etas = await EnsureStopEtas(trip, routeStops, ct);

            var stopIndex = routeStops.FindIndex(rs => rs.StopId == stopId);
            if (stopIndex >= 0)
            {
                var now = departureTime ?? DateTime.UtcNow;
                var targetEta = etas.FirstOrDefault(e => e.StopId == stopId);
                if (targetEta is not null)
                {
                    targetEta.ActualDeparture = now;
                    targetEta.Status = "Departed";
                    targetEta.EtaMinutes = 0;
                    if (targetEta.ScheduledDeparture.HasValue)
                    {
                        targetEta.DelayMinutes = (int)Math.Round((now - targetEta.ScheduledDeparture.Value).TotalMinutes);
                    }
                }

                // Recalculate upcoming stations starting from this departure
                var currentAlong = (double)routeStops[stopIndex].DistanceFromOriginKm;
                for (var j = stopIndex + 1; j < routeStops.Count; j++)
                {
                    var upcoming = etas.FirstOrDefault(e => e.StopId == routeStops[j].StopId);
                    if (upcoming is not null)
                    {
                        var remainKm = (double)routeStops[j].DistanceFromOriginKm - currentAlong;
                        var travelMins = Geo.EtaMinutes(remainKm, Geo.RoadSpeedKmph);
                        upcoming.EtaMinutes = travelMins;
                        upcoming.EstimatedArrival = now.AddMinutes(travelMins);
                        upcoming.EstimatedDeparture = upcoming.EstimatedArrival;
                        if (upcoming.ScheduledArrival.HasValue)
                        {
                            var delay = (int)Math.Round((upcoming.EstimatedArrival.Value - upcoming.ScheduledArrival.Value).TotalMinutes);
                            upcoming.DelayMinutes = delay;
                            upcoming.Status = delay > 3 ? "Delayed" : (delay < -3 ? "Early" : "OnTime");
                        }
                        upcoming.LastUpdatedAt = DateTime.UtcNow;
                    }
                }

                await _db.SaveChangesAsync(ct);
            }

            var snap = await BuildSnapshot(trip, null, null, ct);
            await Publish(snap, ct);
            return snap;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TrackSnapshotDto> RecalculateTripEtasAsync(int tripId, CancellationToken ct = default)
    {
        var gate = TripGates.GetOrAdd(tripId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var trip = await LoadTrip(tripId, ct);
            var routeStops = await LoadRouteStops(trip.RouteId, ct);
            var path = await LoadPath(trip.RouteId, ct);
            var etas = await EnsureStopEtas(trip, routeStops, ct);

            var ping = await _db.GpsPings.AsNoTracking()
                .Where(p => p.TripId == trip.Id)
                .OrderByDescending(p => p.RecordedAt)
                .ThenByDescending(p => p.Id)
                .FirstOrDefaultAsync(ct);

            double? along = ping is null || path.Count == 0
                ? (double)trip.OdometerKm
                : RouteDistanceAtPathDistance(
                    routeStops,
                    path,
                    Geo.Project(new LatLng((double)ping.Latitude, (double)ping.Longitude), path).DistanceAlongKm);

            if (trip.Status == "Running" && along.HasValue)
            {
                var currentIndex = -1;
                for (var i = 0; i < routeStops.Count; i++)
                {
                    if (along.Value + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                        currentIndex = i;
                }
                var speed = ping?.SpeedKmph is decimal s ? (double)s : Geo.RoadSpeedKmph;
                ApplyStopProgress(trip, etas, routeStops, along.Value, currentIndex, speed, ping is null ? null : (double?)ping.Latitude, ping is null ? null : (double?)ping.Longitude);
            }
            else if (trip.Status == "Scheduled")
            {
                ComputeScheduledStopEtas(trip, etas, routeStops);
            }

            await _db.SaveChangesAsync(ct);

            var snap = await BuildSnapshot(trip, null, null, ct);
            await Publish(snap, ct);
            return snap;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(decimal? Fare, int? EtaYou, bool BoardingPassed, int? EtaAlight)> PassengerLeg(
        int routeId, List<RouteStop> routeStops, string? from, string? to, double? along, double? speed, CancellationToken ct)
    {
        RouteStop? fromStop = null;
        RouteStop? toStop = null;
        if (!string.IsNullOrWhiteSpace(from))
            fromStop = routeStops.FirstOrDefault(r => r.Stop.Name.Equals(from.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(to))
            toStop = routeStops.FirstOrDefault(r => r.Stop.Name.Equals(to.Trim(), StringComparison.OrdinalIgnoreCase));

        decimal? fare = null;
        if (fromStop is not null && toStop is not null && fromStop.SequenceNo < toStop.SequenceNo)
        {
            fare = await FareBetween(routeId, fromStop.StopId, toStop.StopId, ct);
        }
        else if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to) && routeStops.Count >= 2)
        {
            fare = await FareBetween(routeId, routeStops[0].StopId, routeStops[^1].StopId, ct);
        }

        if (along is null || fromStop is null)
            return (fare, null, false, null);

        var boardingPassed = along.Value > (double)fromStop.DistanceFromOriginKm + 0.2;
        int? etaYou = boardingPassed
            ? 0
            : Geo.EtaMinutes(Math.Max(0, (double)fromStop.DistanceFromOriginKm - along.Value), speed);
        int? etaAlight = null;
        if (toStop is not null)
            etaAlight = Geo.EtaMinutes(Math.Max(0, (double)toStop.DistanceFromOriginKm - along.Value), speed);
        return (fare, etaYou, boardingPassed, etaAlight);
    }

    private Task<decimal?> FareBetween(int routeId, int fromStopId, int toStopId, CancellationToken ct) =>
        _db.FareSlabs.AsNoTracking()
            .Where(f => f.RouteId == routeId && f.FromStopId == fromStopId && f.ToStopId == toStopId)
            .Select(f => (decimal?)f.Amount)
            .FirstOrDefaultAsync(ct);

    private async Task Publish(TrackSnapshotDto snap, CancellationToken ct)
    {
        var sends = new List<Task>
        {
            _hub.Clients.Group("bus-" + snap.FleetNumber).SendAsync("location", snap, ct),
            _hub.Clients.Group("live").SendAsync("location", snap, ct)
        };
        if (snap.TripId is int id)
            sends.Add(_hub.Clients.Group("trip-" + id).SendAsync("location", snap, ct));
        await Task.WhenAll(sends);
    }

    private IQueryable<Trip> QueryTrips() =>
        _db.Trips
            .Include(t => t.Bus).ThenInclude(b => b.Operator)
            .Include(t => t.Route)
            .Include(t => t.Driver).ThenInclude(d => d.User)
            .Include(t => t.Schedule);

    private Task<Trip> LoadTrip(int id, CancellationToken ct) =>
        QueryTrips().FirstAsync(t => t.Id == id, ct);

    private Task<List<RouteStop>> LoadRouteStops(int routeId, CancellationToken ct) =>
        _db.RouteStops.AsNoTracking().Include(rs => rs.Stop)
            .Where(rs => rs.RouteId == routeId)
            .OrderBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

    public async Task<List<LatLng>> LoadPath(int routeId, CancellationToken ct = default)
    {
        var points = await _db.RoutePoints.AsNoTracking()
            .Where(p => p.RouteId == routeId)
            .OrderBy(p => p.SequenceNo)
            .Select(p => new LatLng((double)p.Latitude, (double)p.Longitude))
            .ToListAsync(ct);
        if (points.Count >= 2) return points;
        var stops = await LoadRouteStops(routeId, ct);
        return stops.Select(s => new LatLng((double)s.Stop.Latitude, (double)s.Stop.Longitude)).ToList();
    }

    private static double RouteDistanceAtPathDistance(
        IReadOnlyList<RouteStop> stops,
        IReadOnlyList<LatLng> path,
        double pathDistance)
    {
        if (stops.Count < 2 || path.Count < 2) return pathDistance;

        var stopPathDistances = StopPathDistances(stops, path);
        for (var index = 1; index < stops.Count; index++)
        {
            var previous = stops[index - 1];
            var current = stops[index];
            var segmentStart = stopPathDistances[index - 1];
            var segmentLength = Math.Max(0, stopPathDistances[index] - segmentStart);
            var segmentEnd = segmentStart + segmentLength;
            if (pathDistance <= segmentEnd || index == stops.Count - 1)
            {
                var progress = segmentLength < 1e-6
                    ? 0
                    : Math.Clamp((pathDistance - segmentStart) / segmentLength, 0, 1);
                var routeStart = (double)previous.DistanceFromOriginKm;
                var routeLength = (double)(current.DistanceFromOriginKm - previous.DistanceFromOriginKm);
                return routeStart + routeLength * progress;
            }
        }
        return (double)stops[^1].DistanceFromOriginKm;
    }

    private static double PathDistanceAtRouteDistance(
        IReadOnlyList<RouteStop> stops,
        IReadOnlyList<LatLng> path,
        double routeDistance)
    {
        if (stops.Count < 2 || path.Count < 2) return routeDistance;

        var stopPathDistances = StopPathDistances(stops, path);
        for (var index = 1; index < stops.Count; index++)
        {
            var previous = stops[index - 1];
            var current = stops[index];
            var routeStart = (double)previous.DistanceFromOriginKm;
            var routeLength = (double)(current.DistanceFromOriginKm - previous.DistanceFromOriginKm);
            var pathStart = stopPathDistances[index - 1];
            var pathLength = Math.Max(0, stopPathDistances[index] - pathStart);
            if (routeDistance <= routeStart + routeLength || index == stops.Count - 1)
            {
                var progress = routeLength < 1e-6
                    ? 0
                    : Math.Clamp((routeDistance - routeStart) / routeLength, 0, 1);
                return pathStart + pathLength * progress;
            }
        }
        return stopPathDistances[^1];
    }

    private static double[] StopPathDistances(IReadOnlyList<RouteStop> stops, IReadOnlyList<LatLng> path) =>
        stops.Select(stop => Geo.Project(
            new LatLng((double)stop.Stop.Latitude, (double)stop.Stop.Longitude),
            path).DistanceAlongKm).ToArray();

    public static string NormalizeFleet(string fleet)
    {
        var value = fleet.Trim();
        if (value.StartsWith("bus", StringComparison.OrdinalIgnoreCase))
            value = value[3..].Trim();
        return value;
    }

    private static bool RunsOn(string? days, int index)
    {
        if (string.IsNullOrWhiteSpace(days) || days.Length != 7) return true;
        return days[index] == '1';
    }
}
