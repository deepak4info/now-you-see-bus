using BusTrack.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

public class RouteGeometryService
{
    private readonly AppDbContext _db;

    public RouteGeometryService(AppDbContext db) => _db = db;

    public async Task RebuildAsync(int routeId, CancellationToken ct = default)
    {
        var route = await _db.Routes.FirstAsync(r => r.Id == routeId, ct);
        var stops = await _db.RouteStops
            .Include(rs => rs.Stop)
            .Where(rs => rs.RouteId == routeId)
            .OrderBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        double acc = 0;
        for (int i = 0; i < stops.Count; i++)
        {
            if (i > 0)
            {
                var prev = stops[i - 1].Stop;
                var cur = stops[i].Stop;
                acc += Geo.HaversineKm((double)prev.Latitude, (double)prev.Longitude, (double)cur.Latitude, (double)cur.Longitude);
            }
            stops[i].DistanceFromOriginKm = Math.Round((decimal)acc, 2);
        }
        route.DistanceKm = stops.Count == 0 ? 0 : Math.Round((decimal)acc, 2);

        var existingPoints = await _db.RoutePoints.Where(p => p.RouteId == routeId).ToListAsync(ct);
        _db.RoutePoints.RemoveRange(existingPoints);

        var dense = Geo.Densify(stops.Select(s => new LatLng((double)s.Stop.Latitude, (double)s.Stop.Longitude)).ToList());
        var seq = 1;
        foreach (var point in dense)
        {
            _db.RoutePoints.Add(new RoutePoint
            {
                RouteId = routeId,
                SequenceNo = seq++,
                Latitude = Math.Round((decimal)point.Lat, 6),
                Longitude = Math.Round((decimal)point.Lng, 6)
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
}
