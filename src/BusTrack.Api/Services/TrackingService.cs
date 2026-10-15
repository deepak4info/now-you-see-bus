using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

public class TrackingService
{
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

    public async Task<TrackSnapshotDto?> GetByFleetAsync(string fleet, string? from, string? to, CancellationToken ct = default)
    {
        var normalized = NormalizeFleet(fleet);
        var bus = await _db.Buses.AsNoTracking().FirstOrDefaultAsync(
            b => b.IsActive && (b.FleetNumber == normalized || b.RegistrationNumber == normalized), ct);
        if (bus is null) return null;

        var today = IndiaClock.Today();
        var trips = await QueryTrips().Where(t => t.BusId == bus.Id && t.ServiceDate == today).ToListAsync(ct);
        var trip = trips
            .OrderByDescending(t => t.Status == "Running")
            .ThenByDescending(t => t.Status == "Scheduled")
            .ThenByDescending(t => t.Id)
            .FirstOrDefault();
        if (trip is not null) return await BuildSnapshot(trip, from, to, ct);

        var schedule = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.BusId == bus.Id)
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
        var along = path.Count == 0 ? 0 : Geo.Project(new LatLng(lat, lng), path).DistanceAlongKm;
        var displaySpeed = keepSimulation ? (double?)null : speed;

        var etas = await _db.TripStopEtas.Where(e => e.TripId == trip.Id).ToListAsync(ct);
        if (etas.Count == 0)
        {
            foreach (var rs in routeStops)
            {
                var row = new TripStopEta
                {
                    TripId = trip.Id,
                    StopId = rs.StopId,
                    SequenceNo = rs.SequenceNo,
                    Status = "Pending",
                    ScheduledArrival = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime)
                        .AddMinutes(Geo.EtaMinutes((double)rs.DistanceFromOriginKm))
                };
                _db.TripStopEtas.Add(row);
                etas.Add(row);
            }
        }

        var currentIndex = -1;
        for (var i = 0; i < routeStops.Count; i++)
        {
            if (along + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                currentIndex = i;
        }

        for (var i = 0; i < routeStops.Count; i++)
        {
            var rs = routeStops[i];
            var row = etas.First(e => e.StopId == rs.StopId);
            if (i <= currentIndex)
            {
                row.Status = i == currentIndex ? "Current" : "Passed";
                row.EtaMinutes = 0;
                row.ActualArrival ??= DateTime.UtcNow;
            }
            else
            {
                var remain = Math.Max(0, (double)rs.DistanceFromOriginKm - along);
                row.EtaMinutes = Geo.EtaMinutes(remain, displaySpeed);
                row.EstimatedArrival = DateTime.UtcNow.AddMinutes(row.EtaMinutes.Value);
                row.Status = i == currentIndex + 1 ? "Next" : "Pending";
            }
        }

        var last = routeStops.Count == 0 ? null : routeStops[^1];
        var arrived = last is not null
            && Geo.HaversineKm(lat, lng, (double)last.Stop.Latitude, (double)last.Stop.Longitude) < 0.35
            && along + 0.4 >= (double)last.DistanceFromOriginKm;

        await _db.SaveChangesAsync(ct);
        if (arrived) await CompleteCore(trip, null, ct);

        var snap = await BuildSnapshot(trip, null, null, ct);
        await Publish(snap, ct);
        return snap;
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
            var total = Geo.Length(path);
            var nextKm = (double)trip.OdometerKm + 0.22;
            if (nextKm >= total - 0.05)
            {
                trip.OdometerKm = decimal.Round((decimal)total, 3);
                var end = path[^1];
                await IngestAsync(trip.Id, end.Lat, end.Lng, Geo.RoadSpeedKmph, null, true, ct);
                continue;
            }
            var prev = Geo.PointAt(path, (double)trip.OdometerKm);
            var pos = Geo.PointAt(path, nextKm);
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
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            Entity = entity,
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
            ? null
            : Geo.Project(new LatLng((double)ping.Latitude, (double)ping.Longitude), path).DistanceAlongKm;
        double? speed = ping?.SpeedKmph is decimal s ? (double)s : null;

        var (stops, current, next, etaNext, etaDest) = DescribeStops(routeStops, trip.Schedule.DepartureTime, trip.Status, along, speed);
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
        int? delay = null;
        var departureUtc = IndiaClock.ToUtc(trip.ServiceDate, trip.Schedule.DepartureTime);
        if (trip.Status == "Scheduled")
        {
            departs = (int)Math.Round((departureUtc - DateTime.UtcNow).TotalMinutes);
            delay = departs < -2 ? -departs : 0;
        }
        else if (next is not null && etaNext is not null)
        {
            var target = routeStops.First(r => r.Stop.Name == next);
            var scheduled = departureUtc.AddMinutes(Geo.EtaMinutes((double)target.DistanceFromOriginKm));
            delay = (int)Math.Round((DateTime.UtcNow.AddMinutes(etaNext.Value) - scheduled).TotalMinutes);
        }
        var outsideSlot = delay is > 180;
        if (outsideSlot) delay = null;

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
            DelayMinutes = delay,
            OutsideSlot = outsideSlot,
            Simulation = trip.Simulation,
            OperationsLive = live,
            DepartureTime = IndiaClock.Clock(trip.Schedule.DepartureTime),
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
        var (stops, current, next, _, _) = DescribeStops(routeStops, schedule.DepartureTime, "Scheduled", null, null);
        var (fare, _, _, _) = await PassengerLeg(schedule.RouteId, routeStops, from, to, null, null, ct);
        var live = await OperationsAreLive(ct);
        var departureUtc = IndiaClock.ToUtc(date, schedule.DepartureTime);
        var departs = (int)Math.Round((departureUtc - DateTime.UtcNow).TotalMinutes);
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
            ServiceDate = date.ToString("yyyy-MM-dd"),
            Fare = fare,
            FromStop = from,
            ToStop = to,
            CurrentStop = current,
            NextStop = next,
            DepartsInMinutes = departs,
            DelayMinutes = departs < -180 ? null : departs < -2 ? -departs : 0,
            OutsideSlot = departs < -180,
            Stops = stops,
            Path = path.Select(p => new PointDto(p.Lat, p.Lng)).ToList()
        };
    }

    private static (List<StopOnTripDto> Stops, string? Current, string? Next, int? EtaNext, int? EtaDest) DescribeStops(
        List<RouteStop> routeStops, TimeOnly departure, string tripStatus, double? along, double? speed)
    {
        var stops = routeStops.Select(rs => new StopOnTripDto
        {
            StopId = rs.StopId,
            Name = rs.Stop.Name,
            Code = rs.Stop.Code,
            Sequence = rs.SequenceNo,
            Latitude = (double)rs.Stop.Latitude,
            Longitude = (double)rs.Stop.Longitude,
            DistanceKm = (double)rs.DistanceFromOriginKm,
            ScheduledTime = IndiaClock.ClockFromDeparture(departure, (double)rs.DistanceFromOriginKm)
        }).ToList();

        string? current = null;
        string? next = null;
        int? etaNext = null;
        int? etaDest = null;

        if (along is not null)
        {
            var currentIndex = -1;
            for (var i = 0; i < routeStops.Count; i++)
            {
                if (along.Value + 0.15 >= (double)routeStops[i].DistanceFromOriginKm)
                    currentIndex = i;
            }
            for (var i = 0; i < stops.Count; i++)
            {
                if (i < currentIndex)
                    stops[i] = stops[i] with { Status = "Passed", EtaMinutes = 0 };
                else if (i == currentIndex)
                {
                    stops[i] = stops[i] with { Status = "Current", EtaMinutes = 0 };
                    current = stops[i].Name;
                }
                else
                {
                    var remain = Math.Max(0, (double)routeStops[i].DistanceFromOriginKm - along.Value);
                    var mins = Geo.EtaMinutes(remain, speed);
                    var status = next is null ? "Next" : "Pending";
                    if (next is null)
                    {
                        next = stops[i].Name;
                        etaNext = mins;
                    }
                    if (i == stops.Count - 1) etaDest = mins;
                    stops[i] = stops[i] with { Status = status, EtaMinutes = mins };
                }
            }
            if (currentIndex == stops.Count - 1) etaDest = 0;
            return (stops, current, next, etaNext, etaDest);
        }

        if (tripStatus == "Completed" && stops.Count > 0)
        {
            for (var i = 0; i < stops.Count; i++)
                stops[i] = stops[i] with { Status = i == stops.Count - 1 ? "Current" : "Passed", EtaMinutes = 0 };
            return (stops, stops[^1].Name, null, 0, 0);
        }

        if (stops.Count > 0)
        {
            stops[0] = stops[0] with { Status = "Next" };
            next = stops[0].Name;
        }
        return (stops, current, next, etaNext, etaDest);
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

    private async Task<List<LatLng>> LoadPath(int routeId, CancellationToken ct)
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
