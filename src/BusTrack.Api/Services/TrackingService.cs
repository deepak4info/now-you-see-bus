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
    private static readonly ConcurrentDictionary<int, (int DelayMinutes, DateTime NotifiedAtUtc)> DelayAlertStates = new();
    private readonly AppDbContext _db;
    private readonly IHubContext<TrackingHub> _hub;
    private readonly ITransitCache _cache;
    private readonly IShardedDataService _shards;

    public TrackingService(
        AppDbContext db,
        IHubContext<TrackingHub> hub,
        ITransitCache? cache = null,
        IShardedDataService? shards = null)
    {
        _db = db;
        _hub = hub;
        _cache = cache ?? NullTransitCache.Instance;
        _shards = shards ?? NullShardedDataService.Instance;
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

    public async Task<StopTimetableDto?> GetStopTimetableAsync(string idOrCode, DateOnly? date = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrCode)) return null;
        var query = idOrCode.Trim();

        // Find Stop by Id, Code or Name
        Stop? stop = null;
        if (int.TryParse(query, out var stopId))
        {
            stop = await _db.Stops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stopId && s.IsActive, ct);
        }

        if (stop is null)
        {
            stop = await FindStop(query, ct);
        }

        if (stop is null) return null;

        var targetDate = date ?? IndiaClock.Today();
        var nowIst = IndiaClock.Now();
        var dayIndex = IndiaClock.DayIndex(targetDate);

        // Find all route stops that include this stop
        var routeStopsAtThisStop = await _db.RouteStops.AsNoTracking()
            .Where(rs => rs.StopId == stop.Id)
            .ToListAsync(ct);

        var routeIds = routeStopsAtThisStop.Select(rs => rs.RouteId).Distinct().ToList();
        if (routeIds.Count == 0)
        {
            return new StopTimetableDto(
                new StopSummaryDto(stop.Id, stop.Name, stop.Code, stop.City, stop.Address, stop.Latitude, stop.Longitude),
                targetDate.ToString("yyyy-MM-dd"),
                IndiaClock.Clock(TimeOnly.FromDateTime(nowIst)),
                new StopStatsDto(0, 0, 0),
                new(),
                new()
            );
        }

        // Load all active routes serving this stop with their operators
        var routes = await _db.Routes.AsNoTracking()
            .Include(r => r.Operator)
            .Where(r => routeIds.Contains(r.Id) && r.IsActive)
            .ToListAsync(ct);

        // Load all RouteStops for these routes to determine sequence, origin, destination, and downstream stops
        var allRouteStops = await _db.RouteStops.AsNoTracking()
            .Include(rs => rs.Stop)
            .Where(rs => routeIds.Contains(rs.RouteId) && rs.Stop.IsActive)
            .OrderBy(rs => rs.RouteId)
            .ThenBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        var stopsByRoute = allRouteStops
            .GroupBy(rs => rs.RouteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.SequenceNo).ToList());

        // Load all schedules for these routes
        var schedules = await _db.Schedules.AsNoTracking()
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Where(s => routeIds.Contains(s.RouteId) && s.IsActive && s.BusId != null)
            .ToListAsync(ct);

        schedules = schedules.Where(s => RunsOn(s.ServiceDays, dayIndex)).ToList();

        // Load today's trips and stop ETAs
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var trips = await QueryTrips()
            .Include(t => t.Bus)
            .Where(t => scheduleIds.Contains(t.ScheduleId) && t.ServiceDate == targetDate)
            .ToListAsync(ct);

        var tripIds = trips.Select(t => t.Id).ToList();
        var stopEtas = tripIds.Count > 0
            ? await _db.TripStopEtas.AsNoTracking()
                .Where(e => tripIds.Contains(e.TripId) && e.StopId == stop.Id)
                .ToListAsync(ct)
            : new();
        var etasByTrip = stopEtas.ToDictionary(e => e.TripId);

        var upcomingBuses = new List<StopArrivalDto>();
        var totalBusesToday = 0;

        foreach (var schedule in schedules)
        {
            if (!stopsByRoute.TryGetValue(schedule.RouteId, out var routeStopList) || routeStopList.Count == 0)
                continue;

            var currentStopIndex = routeStopList.FindIndex(s => s.StopId == stop.Id);
            if (currentStopIndex < 0) continue;

            totalBusesToday++;

            var currentStopRecord = routeStopList[currentStopIndex];
            var originStopName = routeStopList[0].Stop?.Name ?? "Origin";
            var terminusStopName = routeStopList[^1].Stop?.Name ?? "Terminus";

            // Calculate scheduled arrival at this stop
            var travelOffsetMinutes = Geo.EtaMinutes((double)currentStopRecord.DistanceFromOriginKm);
            var scheduledArrivalAtStop = schedule.DepartureTime.Add(TimeSpan.FromMinutes(travelOffsetMinutes));
            var scheduledDateTimeIst = targetDate.ToDateTime(scheduledArrivalAtStop);

            var trip = trips.FirstOrDefault(t => t.ScheduleId == schedule.Id);
            etasByTrip.TryGetValue(trip?.Id ?? -1, out var etaRecord);

            var isLive = trip is not null && (trip.Status == "Running" || trip.Status == "InProgress");
            int? delayMinutes = etaRecord?.DelayMinutes;
            if (delayMinutes == null && trip is not null && trip.StartedAt.HasValue)
            {
                var origDep = targetDate.ToDateTime(schedule.DepartureTime);
                delayMinutes = (int)Math.Max(0, (trip.StartedAt.Value - origDep).TotalMinutes);
            }

            DateTime? estimatedDateTime = delayMinutes.HasValue && delayMinutes.Value != 0
                ? scheduledDateTimeIst.AddMinutes(delayMinutes.Value)
                : scheduledDateTimeIst;

            var effectiveDateTime = estimatedDateTime ?? scheduledDateTimeIst;

            string status;
            if (trip?.Status == "Completed" || etaRecord?.Status == "Departed" || effectiveDateTime < nowIst.AddMinutes(-10))
            {
                status = "Departed";
            }
            else if (isLive && effectiveDateTime <= nowIst.AddMinutes(15))
            {
                status = "Approaching";
            }
            else if (isLive)
            {
                status = "Live";
            }
            else if (delayMinutes.HasValue && delayMinutes.Value > 5)
            {
                status = "Delayed";
            }
            else
            {
                status = "Scheduled";
            }

            int? etaMinutes = null;
            if (status != "Departed")
            {
                etaMinutes = Math.Max(0, (int)Math.Round((effectiveDateTime - nowIst).TotalMinutes));
            }

            var fleet = trip?.Bus?.FleetNumber ?? schedule.Bus?.FleetNumber ?? "Bus";
            var regNo = trip?.Bus?.RegistrationNumber ?? schedule.Bus?.RegistrationNumber ?? "";
            var busType = trip?.Bus?.BusType ?? schedule.Bus?.BusType ?? "Standard";
            var opName = schedule.Bus?.Operator?.Name ?? schedule.Route?.Operator?.Name ?? "Haryana Roadways";

            var trackUrl = $"/track/{Uri.EscapeDataString(fleet)}?from={Uri.EscapeDataString(stop.Name)}&to={Uri.EscapeDataString(terminusStopName)}&date={targetDate:yyyy-MM-dd}";

            upcomingBuses.Add(new StopArrivalDto(
                schedule.Id,
                trip?.Id,
                schedule.RouteId,
                schedule.Route?.Code ?? "",
                schedule.Route?.Name ?? "",
                originStopName,
                terminusStopName,
                schedule.BusId,
                fleet,
                regNo,
                busType,
                opName,
                IndiaClock.Clock(scheduledArrivalAtStop),
                estimatedDateTime.HasValue && delayMinutes.HasValue && delayMinutes.Value != 0 ? IndiaClock.Clock(TimeOnly.FromDateTime(estimatedDateTime.Value)) : null,
                status,
                delayMinutes,
                etaMinutes,
                isLive,
                trackUrl
            ));
        }

        // Sort upcoming buses: live and approaching first, then by scheduled time
        upcomingBuses = upcomingBuses
            .OrderBy(b => b.Status == "Departed" ? 2 : 1)
            .ThenBy(b => b.EtaMinutes ?? 9999)
            .ThenBy(b => b.ScheduledTime)
            .ToList();

        // Build Routes Summary
        var routeSummaries = new List<StopRouteSummaryDto>();
        foreach (var route in routes.OrderBy(r => r.Code))
        {
            if (!stopsByRoute.TryGetValue(route.Id, out var routeStopList) || routeStopList.Count == 0)
                continue;

            var currentStopIndex = routeStopList.FindIndex(s => s.StopId == stop.Id);
            if (currentStopIndex < 0) continue;

            var routeSchedules = schedules.Where(s => s.RouteId == route.Id).OrderBy(s => s.DepartureTime).ToList();
            if (routeSchedules.Count == 0) continue;

            var currentStopRecord = routeStopList[currentStopIndex];
            var originStopName = routeStopList[0].Stop?.Name ?? "Origin";
            var terminusStopName = routeStopList[^1].Stop?.Name ?? "Terminus";
            var isOrigin = currentStopIndex == 0;
            var isTerminus = currentStopIndex == routeStopList.Count - 1;

            var downstreamStops = routeStopList
                .Skip(currentStopIndex + 1)
                .Select(s => s.Stop?.Name ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var offsetMins = Geo.EtaMinutes((double)currentStopRecord.DistanceFromOriginKm);
            var firstTime = routeSchedules.First().DepartureTime.Add(TimeSpan.FromMinutes(offsetMins));
            var lastTime = routeSchedules.Last().DepartureTime.Add(TimeSpan.FromMinutes(offsetMins));

            routeSummaries.Add(new StopRouteSummaryDto(
                route.Id,
                route.Code,
                route.Name,
                route.Operator?.Name ?? "Haryana Roadways",
                originStopName,
                terminusStopName,
                currentStopRecord.SequenceNo,
                isOrigin,
                isTerminus,
                downstreamStops,
                routeSchedules.Count,
                IndiaClock.Clock(firstTime),
                IndiaClock.Clock(lastTime)
            ));
        }

        var upcomingCount = upcomingBuses.Count(b => b.Status != "Departed");

        return new StopTimetableDto(
            new StopSummaryDto(stop.Id, stop.Name, stop.Code, stop.City, stop.Address, stop.Latitude, stop.Longitude),
            targetDate.ToString("yyyy-MM-dd"),
            IndiaClock.Clock(TimeOnly.FromDateTime(nowIst)),
            new StopStatsDto(totalBusesToday, upcomingCount, routeSummaries.Count),
            upcomingBuses,
            routeSummaries
        );
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

    public async Task<MultiRoutePlanDto> PlanJourneyAsync(
        string from,
        string to,
        DateOnly requestedDate,
        DateTime? nowIstOverride = null,
        CancellationToken ct = default)
    {
        var fromStop = await FindStop(from, ct);
        var toStop = await FindStop(to, ct);
        if (fromStop is null || toStop is null || fromStop.Id == toStop.Id)
            return new MultiRoutePlanDto(from.Trim(), to.Trim(), requestedDate, false, new(), new(), new());

        var nowIst = nowIstOverride ?? IndiaClock.Now();
        var today = DateOnly.FromDateTime(nowIst);

        var plan = await ExecutePlanJourneyAsync(fromStop, toStop, requestedDate, nowIst, ct);
        if (plan.AllOptions.Count == 0 && requestedDate == today && !nowIstOverride.HasValue)
        {
            var tomorrow = requestedDate.AddDays(1);
            var nextDayPlan = await ExecutePlanJourneyAsync(fromStop, toStop, tomorrow, nowIst, ct);
            if (nextDayPlan.AllOptions.Count > 0)
                return nextDayPlan;
        }

        return plan;
    }

    private async Task<MultiRoutePlanDto> ExecutePlanJourneyAsync(
        Stop fromStop,
        Stop toStop,
        DateOnly date,
        DateTime nowIst,
        CancellationToken ct)
    {
        var day = IndiaClock.DayIndex(date);
        var today = DateOnly.FromDateTime(nowIst);

        var allRoutes = await _db.Routes.AsNoTracking()
            .Where(r => r.IsActive)
            .Include(r => r.Operator)
            .ToListAsync(ct);

        var allRouteStops = await _db.RouteStops.AsNoTracking()
            .Include(rs => rs.Stop)
            .Where(rs => rs.Stop != null && rs.Stop.IsActive)
            .OrderBy(rs => rs.RouteId)
            .ThenBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        var stopsByRoute = allRouteStops
            .GroupBy(rs => rs.RouteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.SequenceNo).ToList());

        var schedulesByRoute = (await _db.Schedules.AsNoTracking()
            .Include(s => s.Bus!).ThenInclude(b => b.Operator)
            .Include(s => s.Route)
            .Where(s => s.IsActive && s.BusId != null)
            .ToListAsync(ct))
            .Where(s => RunsOn(s.ServiceDays, day))
            .GroupBy(s => s.RouteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DepartureTime).ToList());

        var routeLookup = allRoutes.ToDictionary(r => r.Id);
        var fareCache = new Dictionary<(int RouteId, int FromId, int ToId), decimal>();

        async Task<decimal> ResolveLegFareAsync(int routeId, int fId, int tId, double distKm)
        {
            if (fareCache.TryGetValue((routeId, fId, tId), out var cached)) return cached;
            var explicitFare = await FareBetween(routeId, fId, tId, ct);
            var resolved = explicitFare ?? Math.Max(15m, Math.Round((decimal)distKm * 1.25m, 0));
            fareCache[(routeId, fId, tId)] = resolved;
            return resolved;
        }

        List<(Schedule Schedule, DateTime BoardIst, DateTime AlightIst, int DurationMin, double DistKm, string Status)> BuildLegSlots(
            int routeId,
            RouteStop originRs,
            RouteStop destRs)
        {
            if (!schedulesByRoute.TryGetValue(routeId, out var routeSchedules))
                return new();

            var distKm = Math.Max(1.0, (double)(destRs.DistanceFromOriginKm - originRs.DistanceFromOriginKm));
            var boardOffsetMin = Geo.EtaMinutes((double)originRs.DistanceFromOriginKm);
            var durationMin = Math.Max(10, Geo.EtaMinutes(distKm));

            var slots = new List<(Schedule, DateTime, DateTime, int, double, string)>();
            foreach (var sched in routeSchedules)
            {
                var boardIst = date.ToDateTime(sched.DepartureTime).AddMinutes(boardOffsetMin);
                if (date == today && boardIst <= nowIst)
                    continue;
                var alightIst = boardIst.AddMinutes(durationMin);
                slots.Add((sched, boardIst, alightIst, durationMin, distKm, "Scheduled"));
            }
            return slots;
        }

        // 1. Direct options (0 transfers)
        var directOptions = new List<JourneyOptionDto>();
        foreach (var route in allRoutes)
        {
            if (!stopsByRoute.TryGetValue(route.Id, out var rStops)) continue;
            var originRs = rStops.FirstOrDefault(rs => rs.StopId == fromStop.Id);
            var destRs = rStops.FirstOrDefault(rs => rs.StopId == toStop.Id);
            if (originRs is null || destRs is null || originRs.SequenceNo >= destRs.SequenceNo) continue;

            var fare = await ResolveLegFareAsync(route.Id, fromStop.Id, toStop.Id, (double)(destRs.DistanceFromOriginKm - originRs.DistanceFromOriginKm));
            var slots = BuildLegSlots(route.Id, originRs, destRs);
            foreach (var slot in slots)
            {
                var depStr = slot.BoardIst.ToString("HH:mm");
                var arrStr = slot.AlightIst.ToString("HH:mm");
                var leg = new JourneyLegDto(
                    RouteId: route.Id,
                    RouteCode: route.Code,
                    RouteName: route.Name,
                    ScheduleId: slot.Schedule.Id,
                    TripId: null,
                    FleetNumber: slot.Schedule.Bus?.FleetNumber,
                    BusType: slot.Schedule.Bus?.BusType,
                    OperatorName: slot.Schedule.Bus?.Operator?.Name ?? route.Operator?.Name,
                    FromStop: originRs.Stop!.Name,
                    ToStop: destRs.Stop!.Name,
                    DepartureTime: depStr,
                    ArrivalTime: arrStr,
                    DurationMinutes: slot.DurationMin,
                    EstimatedFare: fare,
                    DistanceKm: Math.Round(slot.DistKm, 1),
                    Status: slot.Status
                );
                directOptions.Add(new JourneyOptionDto(
                    OptionId: $"direct-{route.Id}-{slot.Schedule.Id}",
                    IsDirect: true,
                    TransfersCount: 0,
                    TransferStops: new(),
                    DepartureTime: depStr,
                    ArrivalTime: arrStr,
                    TotalJourneyMinutes: slot.DurationMin,
                    TotalWaitMinutes: 0,
                    EstimatedTotalFare: fare,
                    TotalDistanceKm: Math.Round(slot.DistKm, 1),
                    Legs: new List<JourneyLegDto> { leg },
                    Summary: $"Direct bus on Route {route.Code} ({originRs.Stop.Name} → {destRs.Stop.Name})"
                ));
            }
        }

        // 2. 1-Transfer multi-route options (e.g. Bhuna -> Hisar -> Delhi)
        var transferOptions = new List<JourneyOptionDto>();
        var firstLegCandidates = new List<(Data.Route Route, RouteStop OriginRs, RouteStop HubRs)>();
        var secondLegCandidates = new List<(Data.Route Route, RouteStop HubRs, RouteStop DestRs)>();

        foreach (var route in allRoutes)
        {
            if (!stopsByRoute.TryGetValue(route.Id, out var rStops)) continue;

            var oRs = rStops.FirstOrDefault(rs => rs.StopId == fromStop.Id);
            if (oRs is not null)
            {
                foreach (var rs in rStops.Where(rs => rs.SequenceNo > oRs.SequenceNo))
                {
                    if (rs.StopId != toStop.Id)
                        firstLegCandidates.Add((route, oRs, rs));
                }
            }

            var dRs = rStops.FirstOrDefault(rs => rs.StopId == toStop.Id);
            if (dRs is not null)
            {
                foreach (var rs in rStops.Where(rs => rs.SequenceNo < dRs.SequenceNo))
                {
                    if (rs.StopId != fromStop.Id)
                        secondLegCandidates.Add((route, rs, dRs));
                }
            }
        }

        var seenPairs = new HashSet<string>();
        foreach (var leg1 in firstLegCandidates)
        {
            foreach (var leg2 in secondLegCandidates)
            {
                if (leg1.Route.Id == leg2.Route.Id) continue;
                if (!AreTransferHubsCompatible(leg1.HubRs.Stop!, leg2.HubRs.Stop!)) continue;

                var leg1Dist = (double)(leg1.HubRs.DistanceFromOriginKm - leg1.OriginRs.DistanceFromOriginKm);
                var leg2Dist = (double)(leg2.DestRs.DistanceFromOriginKm - leg2.HubRs.DistanceFromOriginKm);
                var fare1 = await ResolveLegFareAsync(leg1.Route.Id, leg1.OriginRs.StopId, leg1.HubRs.StopId, leg1Dist);
                var fare2 = await ResolveLegFareAsync(leg2.Route.Id, leg2.HubRs.StopId, leg2.DestRs.StopId, leg2Dist);

                var leg1Slots = BuildLegSlots(leg1.Route.Id, leg1.OriginRs, leg1.HubRs);
                var leg2Slots = BuildLegSlots(leg2.Route.Id, leg2.HubRs, leg2.DestRs);

                foreach (var s1 in leg1Slots)
                {
                    // Pick the earliest connecting bus arriving after s1 with at least 10 min transfer buffer & max 240 min layover
                    var matchingS2 = leg2Slots
                        .Where(s2 =>
                        {
                            var wait = (s2.BoardIst - s1.AlightIst).TotalMinutes;
                            return wait >= 10 && wait <= 240;
                        })
                        .OrderBy(s2 => s2.BoardIst)
                        .FirstOrDefault();

                    if (matchingS2.Schedule is null) continue;

                    var dedupKey = $"{s1.Schedule.Id}-{matchingS2.Schedule.Id}-{leg1.HubRs.StopId}";
                    if (!seenPairs.Add(dedupKey)) continue;

                    var waitMin = (int)Math.Round((matchingS2.BoardIst - s1.AlightIst).TotalMinutes);
                    var totalMin = s1.DurationMin + waitMin + matchingS2.DurationMin;
                    var totalDist = Math.Round(s1.DistKm + matchingS2.DistKm, 1);
                    var hubName = leg1.HubRs.Stop!.Name;

                    var leg1Dto = new JourneyLegDto(
                        RouteId: leg1.Route.Id,
                        RouteCode: leg1.Route.Code,
                        RouteName: leg1.Route.Name,
                        ScheduleId: s1.Schedule.Id,
                        TripId: null,
                        FleetNumber: s1.Schedule.Bus?.FleetNumber,
                        BusType: s1.Schedule.Bus?.BusType,
                        OperatorName: s1.Schedule.Bus?.Operator?.Name ?? leg1.Route.Operator?.Name,
                        FromStop: leg1.OriginRs.Stop!.Name,
                        ToStop: hubName,
                        DepartureTime: s1.BoardIst.ToString("HH:mm"),
                        ArrivalTime: s1.AlightIst.ToString("HH:mm"),
                        DurationMinutes: s1.DurationMin,
                        EstimatedFare: fare1,
                        DistanceKm: Math.Round(s1.DistKm, 1),
                        Status: s1.Status
                    );

                    var leg2Dto = new JourneyLegDto(
                        RouteId: leg2.Route.Id,
                        RouteCode: leg2.Route.Code,
                        RouteName: leg2.Route.Name,
                        ScheduleId: matchingS2.Schedule.Id,
                        TripId: null,
                        FleetNumber: matchingS2.Schedule.Bus?.FleetNumber,
                        BusType: matchingS2.Schedule.Bus?.BusType,
                        OperatorName: matchingS2.Schedule.Bus?.Operator?.Name ?? leg2.Route.Operator?.Name,
                        FromStop: leg2.HubRs.Stop!.Name,
                        ToStop: leg2.DestRs.Stop!.Name,
                        DepartureTime: matchingS2.BoardIst.ToString("HH:mm"),
                        ArrivalTime: matchingS2.AlightIst.ToString("HH:mm"),
                        DurationMinutes: matchingS2.DurationMin,
                        EstimatedFare: fare2,
                        DistanceKm: Math.Round(matchingS2.DistKm, 1),
                        Status: matchingS2.Status
                    );

                    transferOptions.Add(new JourneyOptionDto(
                        OptionId: $"xfer-{s1.Schedule.Id}-{matchingS2.Schedule.Id}",
                        IsDirect: false,
                        TransfersCount: 1,
                        TransferStops: new List<string> { hubName },
                        DepartureTime: leg1Dto.DepartureTime,
                        ArrivalTime: leg2Dto.ArrivalTime,
                        TotalJourneyMinutes: totalMin,
                        TotalWaitMinutes: waitMin,
                        EstimatedTotalFare: fare1 + fare2,
                        TotalDistanceKm: totalDist,
                        Legs: new List<JourneyLegDto> { leg1Dto, leg2Dto },
                        Summary: $"{fromStop.Name} → {hubName} → {toStop.Name} (1 transfer · {waitMin} min wait)"
                    ));
                }
            }
        }

        directOptions = directOptions
            .OrderBy(o => o.TotalJourneyMinutes)
            .ThenBy(o => o.DepartureTime)
            .ToList();

        transferOptions = transferOptions
            .OrderBy(o => o.TotalJourneyMinutes)
            .ThenBy(o => o.DepartureTime)
            .Take(8)
            .ToList();

        var allOptions = directOptions.Concat(transferOptions).ToList();
        return new MultiRoutePlanDto(
            From: fromStop.Name,
            To: toStop.Name,
            Date: date,
            DirectAvailable: directOptions.Count > 0,
            DirectOptions: directOptions,
            TransferOptions: transferOptions,
            AllOptions: allOptions
        );
    }

    private static bool AreTransferHubsCompatible(Stop a, Stop b)
    {
        if (a.Id == b.Id) return true;
        if (!string.IsNullOrWhiteSpace(a.City) && !string.IsNullOrWhiteSpace(b.City) &&
            a.City.Trim().Equals(b.City.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Geo.HaversineKm((double)a.Latitude, (double)a.Longitude, (double)b.Latitude, (double)b.Longitude) <= 5.0;
        }
        return Geo.HaversineKm((double)a.Latitude, (double)a.Longitude, (double)b.Latitude, (double)b.Longitude) <= 2.5;
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

            var archivedSnap = await _shards.FindArchivedTripSnapshotAsync(explicitTripId, bus.Id, ct);
            if (archivedSnap is not null)
                return archivedSnap;
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
        if (trip is not null)
            return await BuildSnapshot(trip, null, null, ct);

        return await _shards.FindArchivedTripSnapshotAsync(tripId, null, ct);
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

        var primaryCount = await recentQuery.LongCountAsync(ct);

        List<Trip> trips;
        List<BusTrackingItemDto> archivedItems = new();
        long archivedCount = 0;

        if (!_shards.IsEnabled)
        {
            if (primaryCount == 0)
                return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);

            trips = await recentQuery
                .OrderByDescending(t => t.ServiceDate)
                .ThenByDescending(t => t.EndedAt ?? t.StartedAt)
                .ThenByDescending(t => t.Schedule.DepartureTime)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);
        }
        else
        {
            var topK = page * pageSize;
            trips = primaryCount == 0
                ? new()
                : await recentQuery
                    .OrderByDescending(t => t.ServiceDate)
                    .ThenByDescending(t => t.EndedAt ?? t.StartedAt)
                    .ThenByDescending(t => t.Schedule.DepartureTime)
                    .ThenByDescending(t => t.Id)
                    .Take(topK)
                    .ToListAsync(ct);

            (archivedCount, archivedItems) = await _shards.QueryArchivedTripsAsync(filter, statusFilter, topK, ct);
            if (primaryCount == 0 && archivedCount == 0)
                return new PaginatedBusTrackingDto(new(), page, pageSize, 0, false);
        }

        var items = await MaterializeRecentItemsAsync(trips, ct);
        if (!_shards.IsEnabled)
        {
            var total = (int)Math.Min(int.MaxValue, primaryCount);
            return new PaginatedBusTrackingDto(items, page, pageSize, total, page * pageSize < total);
        }

        var merged = new Dictionary<int, BusTrackingItemDto>();
        foreach (var item in items)
        {
            if (item.TripId is int tid)
                merged[tid] = item;
        }

        var overlap = 0;
        foreach (var archived in archivedItems)
        {
            if (archived.TripId is int atid && !merged.TryAdd(atid, archived))
                overlap++;
        }

        var combinedTotal = (int)Math.Min(int.MaxValue, Math.Max(0, primaryCount + archivedCount - overlap));
        var pagedItems = merged.Values
            .OrderByDescending(i => i.ServiceDate)
            .ThenByDescending(i => i.RecordedAt ?? DateTime.MinValue)
            .ThenByDescending(i => TimeOnly.TryParse(i.DepartureTime, out var dep) ? dep : TimeOnly.MinValue)
            .ThenByDescending(i => i.TripId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PaginatedBusTrackingDto(pagedItems, page, pageSize, combinedTotal, page * pageSize < combinedTotal);
    }

    private async Task<List<BusTrackingItemDto>> MaterializeRecentItemsAsync(List<Trip> trips, CancellationToken ct)
    {
        if (trips.Count == 0) return new();

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

        return items;
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

    internal static (TimeOnly? Start, TimeOnly? End) ResolveTimeWindow(string? timeOfDay) =>
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
            OperationsLive: snap.OperationsLive,
            SpeedKmph: snap.SpeedKmph
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

        var routeStops = await LoadRouteStops(trip.RouteId, ct);
        var path = await LoadPath(trip.RouteId, ct);
        var routeLatHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Latitude : null;
        var routeLngHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Longitude : null;
        var pingLat = decimal.Round((decimal)lat, 6);
        var pingLng = decimal.Round((decimal)lng, 6);
        decimal? pingSpeed = speed is null ? null : decimal.Round((decimal)speed.Value, 2);
        decimal? pingHeading = heading is null ? null : decimal.Round((decimal)heading.Value, 2);
        var nowUtc = DateTime.UtcNow;

        var wroteToShard = await _shards.TryRecordGpsPingAsync(
            trip.Id, trip.RouteId, trip.BusId,
            pingLat, pingLng, pingSpeed, pingHeading,
            nowUtc, nowUtc,
            routeLatHint, routeLngHint, ct);
        if (!wroteToShard)
        {
            _db.GpsPings.Add(new GpsPing
            {
                TripId = trip.Id,
                Latitude = pingLat,
                Longitude = pingLng,
                SpeedKmph = pingSpeed,
                Heading = pingHeading,
                RecordedAt = nowUtc,
                ReceivedAt = nowUtc
            });
        }
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
        await MaybeNotifyDelayAsync(trip, snap, ct);
        await MaybeTriggerStopRemindersAsync(trip, snap, etas, ct);
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

    private async Task MaybeNotifyDelayAsync(Trip trip, TrackSnapshotDto snap, CancellationToken ct)
    {
        if (trip.Status != "Running" || snap.DelayMinutes is not int delayMins || delayMins < 5)
            return;

        var now = DateTime.UtcNow;
        var shouldNotify = true;
        if (DelayAlertStates.TryGetValue(trip.Id, out var prev))
        {
            var worsenedSignificantly = delayMins - prev.DelayMinutes >= 10;
            var cooldownElapsed = (now - prev.NotifiedAtUtc).TotalMinutes >= 15;
            shouldNotify = worsenedSignificantly && cooldownElapsed;
        }
        if (!shouldNotify)
            return;

        DelayAlertStates[trip.Id] = (delayMins, now);
        var etaInfo = snap.EstimatedArrivalTime is { Length: > 0 } eta
            ? $" Updated arrival estimate: {eta}."
            : "";
        var stopInfo = snap.NextStop is { Length: > 0 } next
            ? $" Approaching {next}."
            : "";
        var title = $"Bus {snap.FleetNumber} delayed by {delayMins} min";
        var body = $"Route {snap.RouteCode} ({snap.RouteName}) is running {delayMins} min behind schedule.{stopInfo}{etaInfo}";

        await Notify(trip, "delayed", title, body, ct);
        await BroadcastToTripGroupsAsync(
            snap,
            "delayAlert",
            new
            {
                tripId = trip.Id,
                fleetNumber = snap.FleetNumber,
                routeCode = snap.RouteCode,
                routeName = snap.RouteName,
                delayMinutes = delayMins,
                nextStop = snap.NextStop,
                estimatedArrivalTime = snap.EstimatedArrivalTime,
                recordedAt = snap.RecordedAt,
                title,
                message = body
            },
            ct);
    }

    private async Task MaybeTriggerStopRemindersAsync(Trip trip, TrackSnapshotDto snap, List<TripStopEta> etas, CancellationToken ct)
    {
        if (trip.Status != "Running") return;

        var pending = await _db.StopReminders
            .Where(r => !r.IsTriggered
                && r.RouteId == trip.RouteId
                && r.TargetDate == trip.ServiceDate
                && (r.TripId == null || r.TripId == trip.Id))
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var etaByStop = etas.ToDictionary(e => e.StopId);
        var anyTriggered = false;

        foreach (var reminder in pending)
        {
            if (!etaByStop.TryGetValue(reminder.StopId, out var etaRow)) continue;
            if (etaRow.Status is "Departed") continue;

            var etaMins = etaRow.EtaMinutes;
            var approaching = etaMins is >= 0 && etaMins.Value <= reminder.RemindBeforeMinutes;
            var arrivedNow = etaRow.Status is "Current" or "Arrived";
            if (!approaching && !arrivedNow) continue;

            reminder.IsTriggered = true;
            reminder.TriggeredAt = DateTime.UtcNow;
            anyTriggered = true;

            var etaText = arrivedNow || etaMins is <= 0
                ? $"arriving now at {reminder.StopName}"
                : $"about {etaMins} min from {reminder.StopName}";
            var title = $"Bus {snap.FleetNumber} {etaText}";
            var body = $"Stop arrival reminder for {reminder.StopName} on Route {snap.RouteCode} ({snap.RouteName}).";

            _db.Notifications.Add(new Notification
            {
                UserId = reminder.UserId,
                TripId = trip.Id,
                Title = title,
                Body = body,
                Kind = "reminder",
                CreatedAt = DateTime.UtcNow
            });

            await BroadcastToTripGroupsAsync(
                snap,
                "stopReminder",
                new
                {
                    reminderId = reminder.Id,
                    userId = reminder.UserId,
                    tripId = trip.Id,
                    fleetNumber = snap.FleetNumber,
                    routeId = trip.RouteId,
                    routeCode = snap.RouteCode,
                    stopId = reminder.StopId,
                    stopName = reminder.StopName,
                    etaMinutes = etaMins ?? 0,
                    title,
                    message = body
                },
                ct);
        }

        if (anyTriggered)
            await _db.SaveChangesAsync(ct);
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

        var entry = new AuditLog
        {
            UserId = userId,
            Action = action ?? "",
            Entity = entity ?? "",
            EntityId = entityId,
            Detail = detail,
            CreatedAt = DateTime.UtcNow
        };
        var wroteToShard = await _shards.TryRecordAuditAsync(entry, null, ct);
        if (!wroteToShard)
        {
            _db.AuditLogs.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<GpsPing?> GetLatestGpsPingForTripAsync(int tripId, List<RouteStop> routeStops, CancellationToken ct)
    {
        var latHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Latitude : null;
        var lngHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Longitude : null;
        var shardPing = await _shards.GetLatestGpsPingAsync(tripId, latHint, lngHint, ct);
        if (shardPing is not null) return shardPing;

        return await _db.GpsPings.AsNoTracking()
            .Where(p => p.TripId == tripId)
            .OrderByDescending(p => p.RecordedAt)
            .ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task CompleteCore(Trip trip, Guid? userId, CancellationToken ct)
    {
        if (trip.Status == "Completed") return;
        trip.Status = "Completed";
        trip.EndedAt = DateTime.UtcNow;
        trip.Simulation = false;
        DelayAlertStates.TryRemove(trip.Id, out _);
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
        var ping = await GetLatestGpsPingForTripAsync(trip.Id, routeStops, ct);
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
            await MaybeNotifyDelayAsync(trip, snap, ct);
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
            await MaybeNotifyDelayAsync(trip, snap, ct);
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

            var ping = await GetLatestGpsPingForTripAsync(trip.Id, routeStops, ct);

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
            await MaybeNotifyDelayAsync(trip, snap, ct);
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

    private Task Publish(TrackSnapshotDto snap, CancellationToken ct) =>
        BroadcastToTripGroupsAsync(snap, "location", snap, ct);

    private async Task BroadcastToTripGroupsAsync(TrackSnapshotDto snap, string method, object payload, CancellationToken ct)
    {
        var sends = new List<Task>
        {
            _hub.Clients.Group("bus-" + snap.FleetNumber).SendAsync(method, payload, ct),
            _hub.Clients.Group("live").SendAsync(method, payload, ct)
        };
        if (snap.TripId is int id)
            sends.Add(_hub.Clients.Group("trip-" + id).SendAsync(method, payload, ct));
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

    public async Task DeleteTripGpsPingsAsync(Trip trip, CancellationToken ct = default)
    {
        DelayAlertStates.TryRemove(trip.Id, out _);
        var routeStops = await LoadRouteStops(trip.RouteId, ct);
        var latHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Latitude : null;
        var lngHint = routeStops.Count > 0 ? (double?)routeStops[0].Stop.Longitude : null;
        await _shards.DeleteTripGpsPingsAsync(trip.Id, latHint, lngHint, ct);

        var pings = await _db.GpsPings.Where(p => p.TripId == trip.Id).ToListAsync(ct);
        if (pings.Count > 0)
            _db.GpsPings.RemoveRange(pings);
    }

    public async Task<List<AuditLog>> QueryAuditLogsAsync(
        int limit,
        string? actionFilter = null,
        string? entityFilter = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 2000);
        IQueryable<AuditLog> primaryQuery = _db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actionFilter))
            primaryQuery = primaryQuery.Where(a => a.Action == actionFilter);
        if (!string.IsNullOrWhiteSpace(entityFilter))
            primaryQuery = primaryQuery.Where(a => a.Entity == entityFilter && a.EntityId != null);

        var primaryRows = await primaryQuery
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        if (!_shards.IsEnabled)
            return primaryRows;

        var shardRows = await _shards.QueryAuditLogsAsync(take, actionFilter, entityFilter, ct);
        if (shardRows.Count == 0)
            return primaryRows;

        return primaryRows
            .Concat(shardRows)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToList();
    }

    public async Task<TripArchiveResultDto> ArchiveHistoricalTripsAsync(int? retentionDays = null, CancellationToken ct = default)
    {
        var days = Math.Clamp(retentionDays ?? _shards.Router.Options.RetentionDays, 0, 3650);
        var cutoff = IndiaClock.Today().AddDays(-days);
        var result = await _shards.ArchiveCompletedTripsAsync(_db, cutoff, ct);
        if (result.ArchivedTrips > 0)
            await InvalidateTripLifecycleCachesAsync(ct);
        return result;
    }

    public Task<ShardingHealthReportDto> GetShardingHealthAsync(CancellationToken ct = default) =>
        _shards.GetHealthReportAsync(ct);

    public ShardRoutePreviewDto PreviewShardRoute(int tripId, double? lat, double? lng, string? regionHint = null) =>
        _shards.Router.PreviewRoute(tripId, lat, lng, regionHint);
}
