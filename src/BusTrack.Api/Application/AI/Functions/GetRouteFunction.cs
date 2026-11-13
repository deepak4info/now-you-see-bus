using BusTrack.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Application.AI.Functions;

public record AiRouteDetailsDto(
    int RouteId,
    string Code,
    string Name,
    decimal DistanceKm,
    string OperatorName,
    List<string> Stops
);

public class GetRouteFunction
{
    private readonly AppDbContext _db;

    public GetRouteFunction(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AiRouteDetailsDto?> ExecuteAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var clean = query.Trim();
        var route = await _db.Routes.AsNoTracking()
            .Include(r => r.Operator)
            .FirstOrDefaultAsync(r => r.IsActive && (r.Code == clean || r.Name.Contains(clean)), ct);

        if (route is null) return null;

        var routeStops = await _db.RouteStops.AsNoTracking()
            .Where(rs => rs.RouteId == route.Id)
            .Include(rs => rs.Stop)
            .OrderBy(rs => rs.SequenceNo)
            .ToListAsync(ct);

        var stops = routeStops
            .Select(rs => rs.Stop != null ? rs.Stop.Name : $"Stop #{rs.SequenceNo}")
            .ToList();

        return new AiRouteDetailsDto(
            RouteId: route.Id,
            Code: route.Code,
            Name: route.Name,
            DistanceKm: route.DistanceKm.GetValueOrDefault(),
            OperatorName: route.Operator?.Name ?? "State Transport",
            Stops: stops
        );
    }
}
