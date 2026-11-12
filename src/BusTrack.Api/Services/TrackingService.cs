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
    private readonly ITransitCache _cache;

    public TrackingService(AppDbContext db, IHubContext<TrackingHub> hub, ITransitCache? cache = null)
    {
        _db = db;
        _hub = hub;
        _cache = cache ?? NullTransitCache.Instance;
    }

    public async Task<bool> OperationsAreLive(CancellationToken ct = default)
    {
        var setting = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "OperationsLive", ct);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task<List<CachedStopDto>> GetActiveStopsAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Stops,
            "dedup-active",
            CacheTtls.Stops,
            async token =>
            {
                var stops = await _db.Stops.AsNoTracking()
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.Name)
                    .ToListAsync(token);
                return StopNormalizer.DeduplicateStopsList(stops)
                    .Select(s => new CachedStopDto(s.Id, s.Name, s.Code, s.City, s.Address, s.Latitude, s.Longitude))
                    .ToList();
            },
            ct);

    private Task<List<CachedStopDto>> GetRawActiveStopsCachedAsync(CancellationToken ct) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Stops,
            "raw-active",
            CacheTtls.Stops,
            token => _db.Stops.AsNoTracking()
                .Where(s => s.IsActive)
                .Select(s => new CachedStopDto(s.Id, s.Name, s.Code, s.City, s.Address, s.Latitude, s.Longitude))
                .ToListAsync(token),
            ct);

    public async Task<Stop?> FindStop(string name, CancellationToken ct = default)
    {
        var n = name.Trim();
        var stops = await GetRawActiveStopsCachedAsync(ct);
        var match = stops.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? stops.FirstOrDefault(s => s.Code.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? stops.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? null
            : new Stop
            {
                Id = match.Id,
                Name = match.Name,
                Code = match.Code,
                City = match.City,
                Address = match.Address,
                Latitude = match.Latitude,
                Longitude = match.Longitude,
                IsActive = true
            };
    }

    public async Task<List<TrackSnapshotDto>> SearchAsync(string from, string to, DateOnly date, CancellationToken ct = default) =>
        (await SearchRouteAsync(from, to, date, ct: ct)).Results;

    public async Task<RouteSearchResultDto> SearchRouteAsync(
        string from,
        string to,
        DateOnly requestedDate,
        DateTime? nowIstOverride = null,
        CancellationToken ct = default)
    {
        var fromStop = await FindStop(from, ct);
        var toStop = await FindStop(to, ct);
        if (fromStop is null || toStop is null)
            return new RouteSearchResultDto(from, to, requestedDate, requestedDate, false, new());

        if (nowIstOverride.HasValue)
            return await ExecuteSearchRouteAsync(fromStop, toStop, requestedDate, nowIstOverride.Value, ct);

        var today = IndiaClock.Today();
        var ttl = requestedDate == today ? CacheTtls.SearchToday : CacheTtls.SearchFuture;
        var cacheKey = $"search:{fromStop.Id}:{toStop.Id}:{requestedDate:yyyy-MM-dd}";
        return await _cache.GetOrCreateAsync(
            CacheDomains.Schedules,
            cacheKey,
            ttl,
            token => ExecuteSearchRouteAsync(fromStop, toStop, requestedDate, IndiaClock.Now(), token),
            ct);
    }

    private async Task<RouteSearchResultDto> ExecuteSearchRouteAsync(
        Stop fromStop,
        Stop toStop,
        DateOnly requestedDate,
        DateTime nowIst,
        CancellationToken ct)
    {
        var routeStops = await _db.RouteStops.AsNoTracking()
            .Where(rs => rs.StopId == fromStop.Id || rs.StopId == toStop.Id)
            .ToListAsync(ct);

        var routeOrigins = routeStops
            .GroupBy(rs => rs.RouteId)
            .Select(g => new
            {
                RouteId = g.Key,
                Origin = g.FirstOrDefault(x => x.StopId == fromStop.Id),
                Dest = g.FirstOrDefault(x => x.StopId == toStop.Id)
            })
            .Where(x => x.Origin is not null && x.Dest is not null && x.Origin.SequenceNo < x.Dest.SequenceNo)
            .ToDictionary(x => x.RouteId, x => x.Origin!);

        if (routeOrigins.Count == 0)
            return new RouteSearchResultDto(fromStop.Name, toStop.Name, requestedDate, requestedDate, false, new());

        var today = DateOnly.FromDateTime(nowIst);

        var results = await FindUpcomingForDateAsync(fromStop, toStop, routeOrigins, requestedDate, nowIst, ct);
        var effectiveDate = requestedDate;
        var showingNextDate = false;

        // If searching for today and no remaining departures exist, fall back to the next day automatically.
        if (results.Count == 0 && requestedDate == today)
        {
            var nextDate = requestedDate.AddDays(1);
            var nextDayResults = await FindUpcomingForDateAsync(fromStop, toStop, routeOrigins, nextDate, nowIst, ct);
            if (nextDayResults.Count > 0)
            {
                results = nextDayResults;
                effectiveDate = nextDate;
                showingNextDate = true;
            }
        }

        return new RouteSearchResultDto(fromStop.Name, toStop.Name, effectiveDate, requestedDate, showingNextDate, results);
    }

    private async Task<List<TrackSnapshotDto>> FindUpcomingForDateAsync(
        Stop fromStop,
        Stop toStop,
        Dictionary<int, RouteStop> routeOrigins,
        DateOnly date,
        DateTime nowIst,
        CancellationToken ct)
    {
        var day = IndiaClock.DayIndex(date);
        var routeIds = routeOrigins.Keys.ToList();
        var schedules = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.BusId != null && routeIds.Contains(s.RouteId))
            .ToListAsync(ct);
        schedules = schedules.Where(s => RunsOn(s.ServiceDays, day)).ToList();

        // Filter schedules to departures strictly later than the current IST time before building snapshots.
        var upcomingSchedules = new List<Schedule>();
        foreach (var schedule in schedules)
        {
            var boardingOffsetMins = Geo.EtaMinutes((double)routeOrigins[schedule.RouteId].DistanceFromOriginKm);
            var boardingDepartureIst = date.ToDateTime(schedule.DepartureTime).AddMinutes(boardingOffsetMins);
            if (boardingDepartureIst > nowIst)
                upcomingSchedules.Add(schedule);
        }

        if (upcomingSchedules.Count == 0) return new();

        var scheduleIds = upcomingSchedules.Select(s => s.Id).ToList();
        var trips = await QueryTrips()
            .Where(t => scheduleIds.Contains(t.ScheduleId) && t.ServiceDate == date)
            .ToListAsync(ct);

        var results = new List<TrackSnapshotDto>();
        foreach (var schedule in upcomingSchedules)
        {
            var trip = trips.FirstOrDefault(t => t.ScheduleId == schedule.Id);
            if (trip is not null && trip.Status == "Completed") continue;

            var snap = trip is not null
                ? await BuildSnapshot(trip, fromStop.Name, toStop.Name, ct)
                : await BuildScheduleSnapshot(schedule, fromStop.Name, toStop.Name, date, ct);

            if (snap.BoardingPassed) continue;
            results.Add(snap);
        }

        return results
            .OrderBy(r => r.Status == "Running" ? 0 : r.Status == "Scheduled" ? 1 : 2)
            .ThenBy(r => GetBoardingDepartureKey(r, fromStop.Name))
            .ToList();
    }

    private static string GetBoardingDepartureKey(TrackSnapshotDto snap, string fromStopName) =>
        snap.Stops.FirstOrDefault(s => s.Name.Equals(fromStopName, StringComparison.OrdinalIgnoreCase))?.ScheduledTime
        ?? snap.DepartureTime;

    public async Task<TrackSnapshotDto?> GetByFleetAsync(
        string fleet,
        string? from,
        string? to,
        int? requestedRouteId = null,
        CancellationToken ct = default,
        DateOnly? serviceDate = null,
        int? tripId = null)
    {
        var normalized = NormalizeFleet(fleet);
        var bus = await _db.Buses.AsNoTracking().FirstOrDefaultAsync(
            b => b.IsActive && (b.FleetNumber == normalized || b.RegistrationNumber == normalized), ct);
        if (bus is null) return null;

        if (tripId is int explicitTripId)
        {
            var specificTrip = await QueryTrips().FirstOrDefaultAsync(t => t.Id == explicitTripId && t.BusId == bus.Id, ct);
            if (specificTrip is not null)
                return await BuildSnapshot(specificTrip, from, to, ct);
        }

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

        var targetDate = serviceDate ?? IndiaClock.Today();
        var trips = await QueryTrips().Where(t => t.BusId == bus.Id && t.ServiceDate == targetDate).ToListAsync(ct);
        var matchingTrips = legRouteIds is null ? trips : trips.Where(t => legRouteIds.Contains(t.RouteId));
        var trip = matchingTrips
            .OrderByDescending(t => t.Status == "Running")
            .ThenByDescending(t => t.Status == "Scheduled")
            .ThenByDescending(t => t.Id)
            .FirstOrDefault();
        if (trip is not null) return await BuildSnapshot(trip, from, to, ct);

        var targetDayIndex = IndiaClock.DayIndex(targetDate);
        var candidateSchedules = await _db.Schedules
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Include(s => s.Driver!).ThenInclude(d => d.User)
            .Where(s => s.IsActive && s.BusId == bus.Id
                && (legRouteIds == null || legRouteIds.Contains(s.RouteId)))
            .OrderBy(s => s.DepartureTime)
            .ToListAsync(ct);
        var schedule = candidateSchedules.FirstOrDefault(s => RunsOn(s.ServiceDays, targetDayIndex))
            ?? candidateSchedules.FirstOrDefault();
        return schedule is null ? null : await BuildScheduleSnapshot(schedule, from, to, targetDate, ct);
    }

    public async Task<TrackSnapshotDto?> GetTripAsync(int tripId, CancellationToken ct = default)
    {
        var trip = await QueryTrips().FirstOrDefaultAsync(t => t.Id == tripId, ct);
        return trip is null ? null : await BuildSnapshot(trip, null, null, ct);
    }

    public Task<List<TrackSnapshotDto>> LiveAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Live,
            "snapshots",
            CacheTtls.Live,
            async token =>
            {
                var today = IndiaClock.Today();
                var minDate = today.AddDays(-1);
                var trips = await QueryTrips()
                    .Where(t => t.Status == "Running" && t.ServiceDate >= minDate && t.ServiceDate <= today)
                    .ToListAsync(token);
                var list = new List<TrackSnapshotDto>();
                foreach (var trip in trips)
                    list.Add(await BuildSnapshot(trip, null, null, token));
                return list;
            },
            ct);

    public Task<List<CachedActiveRouteDto>> GetActiveRouteCatalogAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Routes,
            "catalog",
            CacheTtls.Routes,
            async token =>
            {
                var routes = await _db.Routes.AsNoTracking()
                    .Where(r => r.IsActive)
                    .OrderBy(r => r.Code)
                    .ToListAsync(token);
                var routeIds = routes.Select(r => r.Id).ToList();
                var routeStops = routeIds.Count == 0
                    ? new()
                    : await _db.RouteStops.AsNoTracking()
                        .Where(rs => routeIds.Contains(rs.RouteId))
                        .Include(rs => rs.Stop)
                        .OrderBy(rs => rs.SequenceNo)
                        .ToListAsync(token);

                var catalog = new List<CachedActiveRouteDto>(routes.Count);
                foreach (var route in routes)
                {
                    var stops = routeStops.Where(rs => rs.RouteId == route.Id).OrderBy(rs => rs.SequenceNo).ToList();
                    if (stops.Count == 0) continue;

                    var path = await LoadPath(route.Id, token);
                    catalog.Add(new CachedActiveRouteDto(
                        route.Id,
                        route.Code,
                        route.Name,
                        route.DistanceKm ?? stops.LastOrDefault()?.DistanceFromOriginKm,
                        stops[0].Stop.Name,
                        stops[^1].Stop.Name,
                        stops.Select(s => new CachedActiveRouteStopDto(
                            s.StopId,
                            s.Stop.Name,
                            s.Stop.Code,
                            s.SequenceNo,
                            (double)s.Stop.Latitude,
                            (double)s.Stop.Longitude,
                            (double)s.DistanceFromOriginKm)).ToList(),
                        path.Select(p => new CachedActiveRoutePointDto(p.Lat, p.Lng)).ToList()));
                }

                return catalog;
            },
            ct);

    public Task<TrackingFilterOptionsDto> GetTrackingFilterOptionsAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Routes,
            "filter-options",
            CacheTtls.FilterOptions,
            async token =>
            {
                var operators = await _db.Operators.AsNoTracking()
                    .Where(o => o.IsActive)
                    .OrderBy(o => o.Name)
                    .Select(o => new TrackingFilterOperatorOptionDto(o.Id, o.Name, o.Code))
                    .ToListAsync(token);

                var activeRoutes = await _db.Routes.AsNoTracking()
                    .Where(r => r.IsActive)
                    .OrderBy(r => r.Code)
                    .ThenBy(r => r.Name)
                    .Select(r => new { r.Id, r.Code, r.Name })
                    .ToListAsync(token);

                var routeIds = activeRoutes.Select(r => r.Id).ToList();
                var routeStopEndpoints = routeIds.Count == 0
                    ? new()
                    : await _db.RouteStops.AsNoTracking()
                        .Where(rs => routeIds.Contains(rs.RouteId))
                        .OrderBy(rs => rs.RouteId)
                        .ThenBy(rs => rs.SequenceNo)
                        .Select(rs => new { rs.RouteId, StopName = rs.Stop.Name })
                        .ToListAsync(token);

                var endpointsByRoute = routeStopEndpoints
                    .GroupBy(x => x.RouteId)
                    .ToDictionary(g => g.Key, g => (Origin: g.First().StopName, Dest: g.Last().StopName));

                var routes = new List<TrackingFilterRouteOptionDto>(activeRoutes.Count);
                foreach (var route in activeRoutes)
                {
                    if (!endpointsByRoute.TryGetValue(route.Id, out var endpoints)) continue;
                    routes.Add(new TrackingFilterRouteOptionDto(
                        route.Id,
                        route.Code,
                        route.Name,
                        endpoints.Origin,
                        endpoints.Dest
                    ));
                }

                var busTypes = (await _db.Buses.AsNoTracking()
                    .Where(b => b.IsActive && b.BusType != null && b.BusType != "")
                    .Select(b => b.BusType)
                    .Distinct()
                    .ToListAsync(token))
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new TrackingFilterOptionsDto(operators, routes, busTypes);
            },
            ct);

    public async Task<List<BusTrackingItemDto>> GetLiveTrackingAsync(
        DateTime? nowIstOverride = null,
        TrackingFilterDto? filter = null,
        CancellationToken ct = default)
    {
        if (!IsLiveStatusSupported(filter?.Status))
            return new();

        if (nowIstOverride.HasValue)
            return await ExecuteGetLiveTrackingAsync(nowIstOverride.Value, filter, ct);

        return await _cache.GetOrCreateAsync(
            CacheDomains.Live,
            $"feed:{BuildFilterKey(filter)}",
            CacheTtls.Live,
            token => ExecuteGetLiveTrackingAsync(IndiaClock.Now(), filter, token),
            ct);
    }

    private async Task<List<BusTrackingItemDto>> ExecuteGetLiveTrackingAsync(
        DateTime nowIst,
        TrackingFilterDto? filter,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(nowIst);
        var minDate = today.AddDays(-1);
        if (filter?.Date is DateOnly targetDate && (targetDate < minDate || targetDate > today))
            return new();

        // Only include trips whose actual operational status is Running (never Scheduled, Completed, or Cancelled).
        var tripsQuery = QueryTrips()
            .Where(t => t.Status == "Running" && t.ServiceDate >= minDate && t.ServiceDate <= today);

        if (filter?.Date is DateOnly exactDate)
            tripsQuery = tripsQuery.Where(t => t.ServiceDate == exactDate);

        tripsQuery = ApplyTripFilters(tripsQuery, filter);

        var trips = await tripsQuery
            .OrderByDescending(t => t.StartedAt)
            .ThenBy(t => t.Schedule.DepartureTime)
            .ToListAsync(ct);

        var items = new List<BusTrackingItemDto>(trips.Count);
        foreach (var trip in trips)
        {
            var snap = await BuildSnapshot(trip, null, null, ct);
            var item = ToTrackingItem(snap);
            if (!MatchesLiveStatus(item.DelayMinutes, filter?.Status)) continue;
            items.Add(item);
        }
        return items;
    }

    public Task<PaginatedBusTrackingDto> GetRecentTrackingAsync(
        int page = 1,
        int pageSize = 9,
        TrackingFilterDto? filter = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var statusFilter = filter?.Status?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(statusFilter) && statusFilter is not ("all" or "completed" or "cancelled"))
            return Task.FromResult(new PaginatedBusTrackingDto(new(), page, pageSize, 0, false));

        var key = $"feed:{page}:{pageSize}:{BuildFilterKey(filter)}";
        return _cache.GetOrCreateAsync(
            CacheDomains.Recent,
            key,
            CacheTtls.Recent,
            token => ExecuteGetRecentTrackingAsync(page, pageSize, filter, statusFilter, token),
            ct);
    }

    private async Task<PaginatedBusTrackingDto> ExecuteGetRecentTrackingAsync(
        int page,
        int pageSize,
        TrackingFilterDto? filter,
        string? statusFilter,
        CancellationToken ct)
    {
        var recentQuery = QueryTrips();
        if (string.IsNullOrEmpty(statusFilter) || statusFilter == "all")
        {
            recentQuery = recentQuery.Where(t => t.Status == "Completed" || t.Status == "Cancelled");
        }
        else if (statusFilter == "completed")
        {
            recentQuery = recentQuery.Where(t => t.Status == "Completed");
        }
        else
        {
            recentQuery = recentQuery.Where(t => t.Status == "Cancelled");
        }

        if (filter?.Date is DateOnly exactDate)
            recentQuery = recentQuery.Where(t => t.ServiceDate == exactDate);

        recentQuery = ApplyTripFilters(recentQuery, filter);

        var totalCount = await recentQuery.CountAsync(ct);
        if (totalCount == 0)
            return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);

        var trips = await recentQuery
            .OrderByDescending(t => t.ServiceDate)
            .ThenByDescending(t => t.EndedAt ?? t.StartedAt)
            .ThenByDescending(t => t.Schedule.DepartureTime)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var routeIds = trips.Select(t => t.RouteId).Distinct().ToList();
        var tripIds = trips.Select(t => t.Id).ToList();

        var routeStopsMap = await LoadRouteStopsBatchAsync(routeIds, ct);
        var etaRows = await _db.TripStopEtas.AsNoTracking()
            .Where(e => tripIds.Contains(e.TripId))
            .OrderBy(e => e.SequenceNo)
            .ToListAsync(ct);
        var etasByTrip = etaRows.GroupBy(e => e.TripId).ToDictionary(g => g.Key, g => g.ToList());
        var liveOps = await OperationsAreLive(ct);

        var items = new List<BusTrackingItemDto>(trips.Count);
        foreach (var trip in trips)
        {
            routeStopsMap.TryGetValue(trip.RouteId, out var stops);
            stops ??= new();
            etasByTrip.TryGetValue(trip.Id, out var etas);
            etas ??= new();

            var origin = stops.FirstOrDefault()?.Stop.Name ?? "";
            var dest = stops.LastOrDefault()?.Stop.Name ?? "";
            var totalDist = stops.Count > 0 ? (double)stops[^1].DistanceFromOriginKm : 0;

            var originEta = etas.FirstOrDefault();
            var destEta = etas.LastOrDefault();

            var actualDep = trip.Status == "Cancelled" && !trip.StartedAt.HasValue && originEta?.ActualDeparture is null
                ? null
                : trip.StartedAt.HasValue
                    ? IndiaClock.FormatClock12(trip.StartedAt.Value)
                    : (originEta?.ActualDeparture is { } ad ? IndiaClock.FormatClock12(ad) : null);

            var actualArr = trip.Status == "Cancelled" && !trip.EndedAt.HasValue && destEta?.ActualArrival is null
                ? null
                : trip.EndedAt.HasValue
                    ? IndiaClock.FormatClock12(trip.EndedAt.Value)
                    : (destEta?.ActualArrival is { } aa ? IndiaClock.FormatClock12(aa) : null);

            var estArrival = destEta?.EstimatedArrival is { } ea
                ? IndiaClock.FormatClock12(ea)
                : IndiaClock.Clock12FromDeparture(trip.Schedule.DepartureTime, totalDist);

            items.Add(new BusTrackingItemDto(
                TripId: trip.Id,
                ScheduleId: trip.ScheduleId,
                BusId: trip.BusId,
                RouteId: trip.RouteId,
                FleetNumber: trip.Bus.FleetNumber,
                RegistrationNumber: trip.Bus.RegistrationNumber,
                BusName: ResolveBusName(trip.Bus),
                BusType: trip.Bus.BusType,
                OperatorName: trip.Bus.Operator.Name,
                RouteCode: trip.Route.Code,
                RouteName: trip.Route.Name,
                OriginStop: origin,
                DestinationStop: dest,
                CurrentLocation: dest,
                NextStop: null,
                EtaMinutes: null,
                DepartureTime: IndiaClock.Clock(trip.Schedule.DepartureTime),
                ActualDepartureTime: actualDep,
                EstimatedArrivalTime: estArrival,
                ActualArrivalTime: actualArr,
                ServiceDate: trip.ServiceDate.ToString("yyyy-MM-dd"),
                Status: trip.Status,
                DelayMinutes: null,
                DelayStatus: trip.Status,
                RecordedAt: trip.EndedAt is DateTime ended ? IndiaClock.AsUtc(ended) : null,
                Latitude: stops.LastOrDefault() is { } lastStop ? (double)lastStop.Stop.Latitude : null,
                Longitude: stops.LastOrDefault() is { } lastStopLng ? (double)lastStopLng.Stop.Longitude : null,
                OperationsLive: liveOps
            ));
        }

        return new PaginatedBusTrackingDto(items, page, pageSize, totalCount, page * pageSize < totalCount);
    }

    public async Task<PaginatedBusTrackingDto> GetUpcomingTrackingAsync(
        int page = 1,
        int pageSize = 9,
        int daysAhead = 6,
        DateTime? nowIstOverride = null,
        TrackingFilterDto? filter = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        daysAhead = Math.Clamp(daysAhead, 0, 30);

        var statusFilter = filter?.Status?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(statusFilter) && statusFilter is not ("all" or "scheduled"))
            return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);

        if (nowIstOverride.HasValue)
            return await ExecuteGetUpcomingTrackingAsync(page, pageSize, daysAhead, nowIstOverride.Value, filter, ct);

        var nowIst = IndiaClock.Now();
        var key = $"upcoming:{page}:{pageSize}:{daysAhead}:{nowIst:yyyyMMddHHmm}:{BuildFilterKey(filter)}";
        return await _cache.GetOrCreateAsync(
            CacheDomains.Schedules,
            key,
            CacheTtls.Upcoming,
            token => ExecuteGetUpcomingTrackingAsync(page, pageSize, daysAhead, IndiaClock.Now(), filter, token),
            ct);
    }

    private async Task<PaginatedBusTrackingDto> ExecuteGetUpcomingTrackingAsync(
        int page,
        int pageSize,
        int daysAhead,
        DateTime nowIst,
        TrackingFilterDto? filter,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(nowIst);

        DateOnly startDate;
        DateOnly maxDate;
        if (filter?.Date is DateOnly requestedDate)
        {
            if (requestedDate < today)
                return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);
            startDate = requestedDate;
            maxDate = requestedDate;
        }
        else
        {
            startDate = today;
            maxDate = today.AddDays(daysAhead);
        }

        var schedulesQuery = _db.Schedules.AsNoTracking()
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Where(s => s.IsActive && s.BusId != null && s.Bus!.IsActive && s.Route.IsActive);

        schedulesQuery = ApplyScheduleFilters(schedulesQuery, filter);

        var schedules = await schedulesQuery.ToListAsync(ct);

        if (schedules.Count == 0)
            return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);

        var existingTrips = await _db.Trips.AsNoTracking()
            .Where(t => t.ServiceDate >= startDate && t.ServiceDate <= maxDate)
            .Select(t => new { t.Id, t.ScheduleId, t.ServiceDate, t.Status })
            .ToListAsync(ct);
        var tripLookup = existingTrips
            .GroupBy(t => (t.ScheduleId, t.ServiceDate))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First());

        var candidates = new List<(DateOnly Date, Schedule Schedule, int? TripId)>();
        for (var date = startDate; date <= maxDate; date = date.AddDays(1))
        {
            var dayIndex = IndiaClock.DayIndex(date);
            foreach (var schedule in schedules)
            {
                if (!RunsOn(schedule.ServiceDays, dayIndex)) continue;

                // Hide any departure whose scheduled time has already passed.
                var departureIst = date.ToDateTime(schedule.DepartureTime);
                if (departureIst <= nowIst) continue;

                // Automatically omit trips whose operational status is already Running, Completed, or Cancelled.
                if (tripLookup.TryGetValue((schedule.Id, date), out var existingTrip))
                {
                    if (existingTrip.Status != "Scheduled") continue;
                    candidates.Add((date, schedule, existingTrip.Id));
                }
                else
                {
                    candidates.Add((date, schedule, null));
                }
            }
        }

        var ordered = candidates
            .OrderBy(c => c.Date)
            .ThenBy(c => c.Schedule.DepartureTime)
            .ThenBy(c => c.Schedule.Id)
            .ToList();

        var totalCount = ordered.Count;
        if (totalCount == 0)
            return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);

        var slice = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var routeIds = slice.Select(x => x.Schedule.RouteId).Distinct().ToList();
        var routeStopsMap = await LoadRouteStopsBatchAsync(routeIds, ct);
        var liveOps = await OperationsAreLive(ct);

        var items = new List<BusTrackingItemDto>(slice.Count);
        foreach (var (date, schedule, tripId) in slice)
        {
            routeStopsMap.TryGetValue(schedule.RouteId, out var stops);
            stops ??= new();

            var origin = stops.FirstOrDefault()?.Stop.Name ?? "";
            var dest = stops.LastOrDefault()?.Stop.Name ?? "";
            var totalDist = stops.Count > 0 ? (double)stops[^1].DistanceFromOriginKm : 0;
            var estArrival = IndiaClock.Clock12FromDeparture(schedule.DepartureTime, totalDist);
            var anchor = stops.FirstOrDefault();

            items.Add(new BusTrackingItemDto(
                TripId: tripId,
                ScheduleId: schedule.Id,
                BusId: schedule.Bus!.Id,
                RouteId: schedule.RouteId,
                FleetNumber: schedule.Bus.FleetNumber,
                RegistrationNumber: schedule.Bus.RegistrationNumber,
                BusName: ResolveBusName(schedule.Bus),
                BusType: schedule.Bus.BusType,
                OperatorName: schedule.Bus.Operator.Name,
                RouteCode: schedule.Route.Code,
                RouteName: schedule.Route.Name,
                OriginStop: origin,
                DestinationStop: dest,
                CurrentLocation: origin,
                NextStop: stops.Count > 1 ? stops[1].Stop.Name : dest,
                EtaMinutes: null,
                DepartureTime: IndiaClock.Clock(schedule.DepartureTime),
                ActualDepartureTime: null,
                EstimatedArrivalTime: estArrival,
                ActualArrivalTime: null,
                ServiceDate: date.ToString("yyyy-MM-dd"),
                Status: "Scheduled",
                DelayMinutes: 0,
                DelayStatus: "Scheduled",
                RecordedAt: null,
                Latitude: anchor is null ? null : (double)anchor.Stop.Latitude,
                Longitude: anchor is null ? null : (double)anchor.Stop.Longitude,
                OperationsLive: liveOps
            ));
        }

        return new PaginatedBusTrackingDto(items, page, pageSize, totalCount, page * pageSize < totalCount);
    }

    private IQueryable<Trip> ApplyTripFilters(IQueryable<Trip> query, TrackingFilterDto? filter)
    {
        if (filter is null) return query;

        var busSubquery = BuildFilteredBusQuery(filter);
        if (busSubquery is not null)
            query = query.Where(t => busSubquery.Any(b => b.Id == t.BusId));

        var routeSubquery = BuildFilteredRouteQuery(filter);
        if (routeSubquery is not null)
            query = query.Where(t => routeSubquery.Any(r => r.Id == t.RouteId));

        var (start, end) = ResolveTimeWindow(filter.TimeOfDay);
        if (start is TimeOnly windowStart)
        {
            query = end is TimeOnly windowEnd
                ? query.Where(t => t.Schedule.DepartureTime >= windowStart && t.Schedule.DepartureTime < windowEnd)
                : query.Where(t => t.Schedule.DepartureTime >= windowStart);
        }

        return query;
    }

    private IQueryable<Schedule> ApplyScheduleFilters(IQueryable<Schedule> query, TrackingFilterDto? filter)
    {
        if (filter is null) return query;

        var busSubquery = BuildFilteredBusQuery(filter);
        if (busSubquery is not null)
            query = query.Where(s => s.BusId != null && busSubquery.Any(b => b.Id == s.BusId.Value));

        var routeSubquery = BuildFilteredRouteQuery(filter);
        if (routeSubquery is not null)
            query = query.Where(s => routeSubquery.Any(r => r.Id == s.RouteId));

        var (start, end) = ResolveTimeWindow(filter.TimeOfDay);
        if (start is TimeOnly windowStart)
        {
            query = end is TimeOnly windowEnd
                ? query.Where(s => s.DepartureTime >= windowStart && s.DepartureTime < windowEnd)
                : query.Where(s => s.DepartureTime >= windowStart);
        }

        return query;
    }

    private IQueryable<Bus>? BuildFilteredBusQuery(TrackingFilterDto filter)
    {
        var hasBusFilter = !string.IsNullOrWhiteSpace(filter.BusNumber)
            || filter.OperatorId.HasValue
            || !string.IsNullOrWhiteSpace(filter.Operator)
            || !string.IsNullOrWhiteSpace(filter.BusType);
        if (!hasBusFilter) return null;

        IQueryable<Bus> query = _db.Buses.AsNoTracking();

        if (filter.OperatorId is int operatorId)
            query = query.Where(b => b.OperatorId == operatorId);

        if (!string.IsNullOrWhiteSpace(filter.Operator))
        {
            var opLower = filter.Operator.Trim().ToLowerInvariant();
            query = query.Where(b =>
                b.Operator.Name.ToLower().Contains(opLower)
                || b.Operator.Code.ToLower().Contains(opLower));
        }

        if (!string.IsNullOrWhiteSpace(filter.BusType))
        {
            var btLower = filter.BusType.Trim().ToLowerInvariant();
            query = query.Where(b => b.BusType.ToLower() == btLower);
        }

        if (!string.IsNullOrWhiteSpace(filter.BusNumber))
        {
            var busTerm = filter.BusNumber.Trim();
            var normalized = NormalizeFleet(busTerm);
            if (string.IsNullOrWhiteSpace(normalized)) normalized = busTerm;
            var compact = new string(normalized.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToLowerInvariant();
            if (string.IsNullOrEmpty(compact)) compact = busTerm.ToLowerInvariant();

            var termLower = busTerm.ToLowerInvariant();
            var normLower = normalized.ToLowerInvariant();

            query = query.Where(b =>
                b.FleetNumber.ToLower().Contains(termLower)
                || b.FleetNumber.ToLower().Contains(normLower)
                || b.FleetNumber.Replace("-", "").Replace(" ", "").ToLower().Contains(compact)
                || b.RegistrationNumber.ToLower().Contains(termLower)
                || b.RegistrationNumber.ToLower().Contains(normLower)
                || b.RegistrationNumber.Replace("-", "").Replace(" ", "").ToLower().Contains(compact)
                || (b.Model != null && b.Model.ToLower().Contains(termLower)));
        }

        return query;
    }

    private IQueryable<Data.Route>? BuildFilteredRouteQuery(TrackingFilterDto filter)
    {
        var hasRouteFilter = filter.RouteId.HasValue
            || !string.IsNullOrWhiteSpace(filter.Route)
            || !string.IsNullOrWhiteSpace(filter.Stop);
        if (!hasRouteFilter) return null;

        IQueryable<Data.Route> query = _db.Routes.AsNoTracking();

        if (filter.RouteId is int routeId)
            query = query.Where(r => r.Id == routeId);

        if (!string.IsNullOrWhiteSpace(filter.Route))
        {
            var routeLower = filter.Route.Trim().ToLowerInvariant();
            query = query.Where(r =>
                r.Code.ToLower().Contains(routeLower)
                || r.Name.ToLower().Contains(routeLower));
        }

        if (!string.IsNullOrWhiteSpace(filter.Stop))
        {
            var stopLower = filter.Stop.Trim().ToLowerInvariant();
            query = query.Where(r =>
                r.Name.ToLower().Contains(stopLower)
                || _db.RouteStops.Any(rs => rs.RouteId == r.Id
                    && (rs.Stop.Name.ToLower().Contains(stopLower)
                        || rs.Stop.Code.ToLower().Contains(stopLower))));
        }

        return query;
    }

    private static (TimeOnly? Start, TimeOnly? End) ResolveTimeWindow(string? timeOfDay) =>
        timeOfDay?.Trim().ToLowerInvariant() switch
        {
            "morning" => (new TimeOnly(0, 0), new TimeOnly(12, 0)),
            "afternoon" => (new TimeOnly(12, 0), new TimeOnly(17, 0)),
            "evening" => (new TimeOnly(17, 0), new TimeOnly(21, 0)),
            "night" => (new TimeOnly(21, 0), null),
            _ => (null, null)
        };

    private static bool IsLiveStatusSupported(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return status.Trim().ToLowerInvariant() is "all" or "running" or "ontime" or "on-time" or "on time" or "delayed" or "early";
    }

    private static bool MatchesLiveStatus(int? delayMinutes, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return status.Trim().ToLowerInvariant() switch
        {
            "all" or "running" => true,
            "ontime" or "on-time" or "on time" => delayMinutes is null or (>= -2 and <= 2),
            "delayed" => delayMinutes is > 2,
            "early" => delayMinutes is < -2,
            _ => false
        };
    }

    private async Task<Dictionary<int, List<RouteStop>>> LoadRouteStopsBatchAsync(List<int> routeIds, CancellationToken ct)
    {
        if (routeIds.Count == 0) return new();
        var rows = await _db.RouteStops.AsNoTracking()
            .Include(rs => rs.Stop)
            .Where(rs => routeIds.Contains(rs.RouteId))
            .OrderBy(rs => rs.RouteId)
            .ThenBy(rs => rs.SequenceNo)
            .ToListAsync(ct);
        return rows.GroupBy(rs => rs.RouteId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static string ResolveBusName(Bus bus)
    {
        if (!string.IsNullOrWhiteSpace(bus.Model)) return bus.Model;
        if (!string.IsNullOrWhiteSpace(bus.BusType)) return $"{bus.BusType} Service";
        return $"Bus {bus.FleetNumber}";
    }

    private static BusTrackingItemDto ToTrackingItem(TrackSnapshotDto snap) =>
        new(
            TripId: snap.TripId,
            ScheduleId: snap.ScheduleId,
            BusId: snap.BusId,
            RouteId: snap.RouteId,
            FleetNumber: snap.FleetNumber,
            RegistrationNumber: snap.RegistrationNumber,
            BusName: snap.BusName,
            BusType: snap.BusType,
            OperatorName: snap.OperatorName,
            RouteCode: snap.RouteCode,
            RouteName: snap.RouteName,
            OriginStop: snap.OriginStop ?? snap.Stops.FirstOrDefault()?.Name ?? "",
            DestinationStop: snap.DestinationStop ?? snap.Stops.LastOrDefault()?.Name ?? "",
            CurrentLocation: snap.CurrentStop,
            NextStop: snap.NextStop,
            EtaMinutes: snap.EtaMinutes,
            DepartureTime: snap.DepartureTime,
            ActualDepartureTime: snap.ActualDepartureTime,
            EstimatedArrivalTime: snap.EstimatedArrivalTime ?? snap.Stops.LastOrDefault()?.EstimatedArrival,
            ActualArrivalTime: snap.ActualArrivalTime ?? snap.Stops.LastOrDefault()?.ActualArrival,
            ServiceDate: snap.ServiceDate,
            Status: snap.Status,
            DelayMinutes: snap.DelayMinutes,
            DelayStatus: snap.DelayStatus,
            RecordedAt: snap.RecordedAt,
            Latitude: snap.Latitude,
            Longitude: snap.Longitude,
            OperationsLive: snap.OperationsLive
        );

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
        if (created > 0)
            await InvalidateScheduleCachesAsync(ct);
        return created;
    }

    public async Task<TrackSnapshotDto> StartAsync(Trip trip, string mode, Guid? userId, CancellationToken ct = default)
    {
        trip.Status = "Running";
        trip.StartedAt = DateTime.UtcNow;
        trip.Simulation = mode == "simulate";
        trip.OdometerKm = 0;
        await _db.SaveChangesAsync(ct);
        await InvalidateLiveAndScheduleCachesAsync(ct);
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
        await InvalidateTripLifecycleCachesAsync(ct);
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

        var originStopName = routeStops.FirstOrDefault()?.Stop.Name;
        var destinationStopName = routeStops.LastOrDefault()?.Stop.Name;
        var destEtaRow = stops.LastOrDefault();
        var estimatedArrival = destEtaRow?.EstimatedArrival
            ?? (routeStops.Count > 0 ? IndiaClock.Clock12FromDeparture(trip.Schedule.DepartureTime, (double)routeStops[^1].DistanceFromOriginKm) : null);
        var actualArrival = trip.EndedAt.HasValue
            ? IndiaClock.FormatClock12(trip.EndedAt.Value)
            : destEtaRow?.ActualArrival;

        return new TrackSnapshotDto
        {
            TripId = trip.Id,
            ScheduleId = trip.ScheduleId,
            BusId = trip.BusId,
            RouteId = trip.RouteId,
            FleetNumber = trip.Bus.FleetNumber,
            RegistrationNumber = trip.Bus.RegistrationNumber,
            BusName = ResolveBusName(trip.Bus),
            BusType = trip.Bus.BusType,
            Capacity = trip.Bus.Capacity,
            OperatorName = trip.Bus.Operator.Name,
            RouteCode = trip.Route.Code,
            RouteName = trip.Route.Name,
            OriginStop = originStopName,
            DestinationStop = destinationStopName,
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
            EstimatedArrivalTime = estimatedArrival,
            ActualArrivalTime = actualArrival,
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
        var originStopName = routeStops.FirstOrDefault()?.Stop.Name;
        var destinationStopName = routeStops.LastOrDefault()?.Stop.Name;
        var estimatedArrival = stops.LastOrDefault()?.EstimatedArrival
            ?? (routeStops.Count > 0
                ? IndiaClock.FormatClock12(departureUtc.AddMinutes(Geo.EtaMinutes((double)routeStops[^1].DistanceFromOriginKm) + delay))
                : null);

        return new TrackSnapshotDto
        {
            ScheduleId = schedule.Id,
            BusId = schedule.Bus!.Id,
            RouteId = schedule.RouteId,
            FleetNumber = schedule.Bus.FleetNumber,
            RegistrationNumber = schedule.Bus.RegistrationNumber,
            BusName = ResolveBusName(schedule.Bus),
            BusType = schedule.Bus.BusType,
            Capacity = schedule.Bus.Capacity,
            OperatorName = schedule.Bus.Operator.Name,
            RouteCode = schedule.Route.Code,
            RouteName = schedule.Route.Name,
            OriginStop = originStopName,
            DestinationStop = destinationStopName,
            Status = "Scheduled",
            DriverName = schedule.Driver?.User.FullName,
            Latitude = anchor is null ? null : (double)anchor.Stop.Latitude,
            Longitude = anchor is null ? null : (double)anchor.Stop.Longitude,
            OperationsLive = live,
            DepartureTime = IndiaClock.Clock(schedule.DepartureTime),
            ActualDepartureTime = null,
            EstimatedDepartureTime = delay > 0 ? IndiaClock.FormatClock12(departureUtc.AddMinutes(delay)) : IndiaClock.FormatClock12(schedule.DepartureTime),
            EstimatedArrivalTime = estimatedArrival,
            ActualArrivalTime = null,
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

            await _cache.InvalidateDomainAsync(CacheDomains.Live, ct);
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

            await _cache.InvalidateDomainAsync(CacheDomains.Live, ct);
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

            await _cache.InvalidateDomainAsync(CacheDomains.Live, ct);
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

    private async Task<decimal?> FareBetween(int routeId, int fromStopId, int toStopId, CancellationToken ct)
    {
        var slabs = await _cache.GetOrCreateAsync(
            CacheDomains.Fares,
            $"route:{routeId}",
            CacheTtls.Fares,
            token => _db.FareSlabs.AsNoTracking()
                .Where(f => f.RouteId == routeId)
                .Select(f => new CachedFareSlabDto(f.FromStopId, f.ToStopId, f.Amount))
                .ToListAsync(token),
            ct);
        var match = slabs.FirstOrDefault(f => f.FromStopId == fromStopId && f.ToStopId == toStopId);
        return match?.Amount;
    }

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

    private async Task<List<RouteStop>> LoadRouteStops(int routeId, CancellationToken ct)
    {
        var cached = await _cache.GetOrCreateAsync(
            CacheDomains.Routes,
            $"route-stops:{routeId}",
            CacheTtls.Routes,
            token => _db.RouteStops.AsNoTracking()
                .Where(rs => rs.RouteId == routeId)
                .OrderBy(rs => rs.SequenceNo)
                .Select(rs => new CachedRouteStopDto(
                    rs.Id,
                    rs.RouteId,
                    rs.StopId,
                    rs.SequenceNo,
                    rs.DistanceFromOriginKm,
                    rs.SegmentDistanceKm,
                    rs.Stop.Name,
                    rs.Stop.Code,
                    rs.Stop.Latitude,
                    rs.Stop.Longitude))
                .ToListAsync(token),
            ct);

        return cached.Select(c => new RouteStop
        {
            Id = c.Id,
            RouteId = c.RouteId,
            StopId = c.StopId,
            SequenceNo = c.SequenceNo,
            DistanceFromOriginKm = c.DistanceFromOriginKm,
            SegmentDistanceKm = c.SegmentDistanceKm,
            Stop = new Stop
            {
                Id = c.StopId,
                Name = c.StopName,
                Code = c.StopCode,
                Latitude = c.StopLatitude,
                Longitude = c.StopLongitude,
                IsActive = true
            }
        }).ToList();
    }

    public Task<List<LatLng>> LoadPath(int routeId, CancellationToken ct = default) =>
        _cache.GetOrCreateAsync(
            CacheDomains.Routes,
            $"path:{routeId}",
            CacheTtls.Routes,
            async token =>
            {
                var points = await _db.RoutePoints.AsNoTracking()
                    .Where(p => p.RouteId == routeId)
                    .OrderBy(p => p.SequenceNo)
                    .Select(p => new LatLng((double)p.Latitude, (double)p.Longitude))
                    .ToListAsync(token);
                if (points.Count >= 2) return points;
                var stops = await LoadRouteStops(routeId, token);
                return stops.Select(s => new LatLng((double)s.Stop.Latitude, (double)s.Stop.Longitude)).ToList();
            },
            ct);

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

    private static string BuildFilterKey(TrackingFilterDto? filter)
    {
        if (filter is null) return "none";
        return string.Join("|",
            filter.BusNumber?.Trim().ToLowerInvariant() ?? "",
            filter.RouteId?.ToString() ?? "",
            filter.Route?.Trim().ToLowerInvariant() ?? "",
            filter.Date?.ToString("yyyy-MM-dd") ?? "",
            filter.OperatorId?.ToString() ?? "",
            filter.Operator?.Trim().ToLowerInvariant() ?? "",
            filter.BusType?.Trim().ToLowerInvariant() ?? "",
            filter.Status?.Trim().ToLowerInvariant() ?? "",
            filter.Stop?.Trim().ToLowerInvariant() ?? "",
            filter.TimeOfDay?.Trim().ToLowerInvariant() ?? "");
    }

    public Task InvalidateStopCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Stops, CacheDomains.Routes, CacheDomains.Schedules }, ct);

    public Task InvalidateRouteCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Routes, CacheDomains.Fares, CacheDomains.Schedules, CacheDomains.Live, CacheDomains.Recent }, ct);

    public Task InvalidateFareCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Fares, CacheDomains.Schedules }, ct);

    public Task InvalidateScheduleCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainAsync(CacheDomains.Schedules, ct);

    public Task InvalidateLiveAndScheduleCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Live, CacheDomains.Schedules }, ct);

    public Task InvalidateTripLifecycleCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Live, CacheDomains.Recent, CacheDomains.Schedules }, ct);

    public Task InvalidateCatalogCachesAsync(CancellationToken ct = default) =>
        _cache.InvalidateDomainsAsync(new[] { CacheDomains.Stops, CacheDomains.Routes, CacheDomains.Fares, CacheDomains.Schedules, CacheDomains.Live, CacheDomains.Recent }, ct);

    public Task InvalidateUserProfileCacheAsync(Guid userId, CancellationToken ct = default) =>
        _cache.RemoveDirectAsync(TransitCacheService.UserProfileKey(userId), ct);
}
