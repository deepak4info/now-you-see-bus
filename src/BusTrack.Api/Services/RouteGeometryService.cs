using BusTrack.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

public class RouteGeometryService
{
    private readonly AppDbContext _db;
    private readonly RoadRouter _roads;

    public RouteGeometryService(AppDbContext db, RoadRouter roads)
    {
        _db = db;
        _roads = roads;
    }

    public async Task<int> RebuildAllRoutesAsync(CancellationToken ct = default)
    {
        var ids = await _db.Routes.Where(r => r.IsActive).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in ids) await RebuildAsync(id, ct);
        return ids.Count;
    }

    public Task<int> RebuildAutoRoutesAsync(CancellationToken ct = default) => RebuildAllRoutesAsync(ct);

    private const int MaxRoutePoints = 1500;
    private static readonly TimeSpan OsrmLookupTimeout = TimeSpan.FromSeconds(8);

    public async Task RebuildAsync(int routeId, CancellationToken ct = default)
    {
        var route = await _db.Routes.FirstAsync(r => r.Id == routeId, ct);
        var stops = await _db.RouteStops
            .Include(rs => rs.Stop)
            .Where(rs => rs.RouteId == routeId)
            .OrderBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        (double[] LegKm, List<LatLng> Points)? road = null;
        if (stops.Count >= 2)
        {
            var waypoints = stops.Select(s => new LatLng((double)s.Stop.Latitude, (double)s.Stop.Longitude)).ToList();
            using var osrmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            osrmCts.CancelAfter(OsrmLookupTimeout);
            try { road = await _roads.DriveAsync(waypoints, osrmCts.Token); }
            catch (Exception) { road = null; }
        }

        double acc = 0;
        for (int i = 0; i < stops.Count; i++)
        {
            if (i > 0)
            {
                if (route.DistanceMode == "Manual")
                {
                    var seg = stops[i].SegmentDistanceKm;
                    if (seg.HasValue && seg.Value > 0)
                    {
                        acc += (double)seg.Value;
                    }
                    else if (road is not null && i - 1 < road.Value.LegKm.Length)
                    {
                        acc += road.Value.LegKm[i - 1];
                    }
                    else
                    {
                        var prev = stops[i - 1].Stop;
                        var cur = stops[i].Stop;
                        acc += Geo.HaversineKm((double)prev.Latitude, (double)prev.Longitude, (double)cur.Latitude, (double)cur.Longitude);
                    }
                }
                else if (road is not null && i - 1 < road.Value.LegKm.Length)
                {
                    acc += road.Value.LegKm[i - 1];
                }
                else
                {
                    var prev = stops[i - 1].Stop;
                    var cur = stops[i].Stop;
                    acc += Geo.HaversineKm((double)prev.Latitude, (double)prev.Longitude, (double)cur.Latitude, (double)cur.Longitude);
                }
            }
            stops[i].DistanceFromOriginKm = ClampDistance(acc);
        }
        route.DistanceKm = stops.Count == 0 ? 0 : ClampDistance(acc);

        // Persist/delete old points before inserting SequenceNo = 1..N so SQL UQ_RoutePoints_Sequence never conflicts
        if (_db.Database.IsRelational())
        {
            await _db.RoutePoints.Where(p => p.RouteId == routeId).ExecuteDeleteAsync(ct);
        }
        else
        {
            var existingPoints = await _db.RoutePoints.Where(p => p.RouteId == routeId).ToListAsync(ct);
            if (existingPoints.Count > 0)
            {
                _db.RoutePoints.RemoveRange(existingPoints);
                await _db.SaveChangesAsync(ct);
            }
        }

        var rawDense = road is not null && road.Value.Points.Count >= 2
            ? road.Value.Points
            : Geo.Densify(stops.Select(s => new LatLng((double)s.Stop.Latitude, (double)s.Stop.Longitude)).ToList());
        var dense = DownsamplePoints(rawDense, MaxRoutePoints);
        var seq = 1;
        foreach (var point in dense)
        {
            if (!double.IsFinite(point.Lat) || !double.IsFinite(point.Lng)) continue;
            _db.RoutePoints.Add(new RoutePoint
            {
                RouteId = routeId,
                SequenceNo = seq++,
                Latitude = Math.Clamp(Math.Round((decimal)point.Lat, 6), -90m, 90m),
                Longitude = Math.Clamp(Math.Round((decimal)point.Lng, 6), -180m, 180m)
            });
        }

        var fares = await _db.FareSlabs.Where(f => f.RouteId == routeId).ToListAsync(ct);
        var keep = new HashSet<(int, int)>();
        for (int i = 0; i < stops.Count; i++)
        {
            for (int j = i + 1; j < stops.Count; j++)
            {
                var fromId = stops[i].StopId;
                var toId = stops[j].StopId;
                keep.Add((fromId, toId));
                if (fares.Any(f => f.FromStopId == fromId && f.ToStopId == toId)) continue;
                var km = (double)(stops[j].DistanceFromOriginKm - stops[i].DistanceFromOriginKm);
                _db.FareSlabs.Add(new FareSlab
                {
                    RouteId = routeId,
                    FromStopId = fromId,
                    ToStopId = toId,
                    Amount = Geo.FareForKm(km),
                    Currency = "INR"
                });
            }
        }
        foreach (var fare in fares.Where(f => !keep.Contains((f.FromStopId, f.ToStopId))))
            _db.FareSlabs.Remove(fare);

        await _db.SaveChangesAsync(ct);
    }

    private static decimal ClampDistance(double km)
    {
        if (!double.IsFinite(km) || km <= 0) return 0m;
        return Math.Min(999999.99m, Math.Round((decimal)km, 2));
    }

    private static IReadOnlyList<LatLng> DownsamplePoints(IReadOnlyList<LatLng> points, int maxCount)
    {
        if (points.Count <= maxCount || maxCount < 2) return points;
        var sampled = new List<LatLng>(maxCount);
        var step = (double)(points.Count - 1) / (maxCount - 1);
        for (int i = 0; i < maxCount - 1; i++)
        {
            sampled.Add(points[(int)Math.Round(i * step)]);
        }
        sampled.Add(points[^1]);
        return sampled;
    }
}
