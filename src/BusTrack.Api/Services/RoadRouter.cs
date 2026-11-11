using System.Globalization;
using System.Text.Json;

namespace BusTrack.Api.Services;

public sealed class RoadRouter
{
    private readonly HttpClient _http;
    private const string SecondaryBase = "https://routing.openstreetmap.de/routed-car/";

    public RoadRouter(HttpClient http) => _http = http;

    public async Task<(double[] LegKm, List<LatLng> Points)?> DriveAsync(IReadOnlyList<LatLng> stops, CancellationToken ct)
    {
        if (stops.Count < 2) return null;

        // 1. Try full multi-waypoint route across primary and secondary mirrors
        var fullResult = await TryFetchFullRouteAsync(stops, ct);
        if (fullResult is not null) return fullResult;

        // 2. Resilient fallback: route leg-by-leg so an issue with one stop doesn't break the entire corridor
        return await DriveLegByLegAsync(stops, ct);
    }

    private async Task<(double[] LegKm, List<LatLng> Points)?> TryFetchFullRouteAsync(IReadOnlyList<LatLng> stops, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        var coords = string.Join(';', stops.Select(stop =>
            $"{stop.Lng.ToString("F6", CultureInfo.InvariantCulture)},{stop.Lat.ToString("F6", CultureInfo.InvariantCulture)}"));

        var relativeUrl = $"route/v1/driving/{coords}?overview=full&geometries=geojson";
        var primaryResult = await FetchOsrmRouteAsync(relativeUrl, stops.Count - 1, ct);
        if (primaryResult is not null || ct.IsCancellationRequested) return primaryResult;

        var secondaryUrl = $"{SecondaryBase}route/v1/driving/{coords}?overview=full&geometries=geojson";
        return await FetchOsrmRouteAsync(secondaryUrl, stops.Count - 1, ct);
    }

    private async Task<(double[] LegKm, List<LatLng> Points)?> FetchOsrmRouteAsync(string url, int expectedLegs, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.GetString() != "Ok") return null;
            if (!root.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0) return null;

            var route = routes[0];
            if (!route.TryGetProperty("legs", out var legs)) return null;
            if (legs.GetArrayLength() != expectedLegs) return null;

            var legKm = new double[legs.GetArrayLength()];
            for (var i = 0; i < legKm.Length; i++)
            {
                if (legs[i].TryGetProperty("distance", out var distElem))
                    legKm[i] = distElem.GetDouble() / 1000.0;
            }

            if (!route.TryGetProperty("geometry", out var geometry)) return null;
            if (!geometry.TryGetProperty("coordinates", out var coordinates)) return null;

            // Use 15m threshold (0.015 km) to follow highway bends, flyovers, and curves without shortcuts
            var points = ExtractThinnedPoints(coordinates, minDistanceKm: 0.015);
            return points.Count >= 2 ? (legKm, points) : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<LatLng> ExtractThinnedPoints(JsonElement coordinates, double minDistanceKm = 0.015)
    {
        var points = new List<LatLng>();
        foreach (var pair in coordinates.EnumerateArray())
        {
            var point = new LatLng(pair[1].GetDouble(), pair[0].GetDouble());
            if (points.Count == 0 || Geo.HaversineKm(points[^1].Lat, points[^1].Lng, point.Lat, point.Lng) >= minDistanceKm)
                points.Add(point);
        }
        if (coordinates.GetArrayLength() > 0)
        {
            var end = coordinates[coordinates.GetArrayLength() - 1];
            var endPoint = new LatLng(end[1].GetDouble(), end[0].GetDouble());
            if (points.Count == 0 || Geo.HaversineKm(points[^1].Lat, points[^1].Lng, endPoint.Lat, endPoint.Lng) > 0.005)
                points.Add(endPoint);
        }
        return points;
    }

    private async Task<(double[] LegKm, List<LatLng> Points)> DriveLegByLegAsync(IReadOnlyList<LatLng> stops, CancellationToken ct)
    {
        var legKm = new double[stops.Count - 1];
        var points = new List<LatLng>();

        for (int i = 0; i < stops.Count - 1; i++)
        {
            var from = stops[i];
            var to = stops[i + 1];
            var legCoords = $"{from.Lng.ToString("F6", CultureInfo.InvariantCulture)},{from.Lat.ToString("F6", CultureInfo.InvariantCulture)};" +
                            $"{to.Lng.ToString("F6", CultureInfo.InvariantCulture)},{to.Lat.ToString("F6", CultureInfo.InvariantCulture)}";

            var legResult = ct.IsCancellationRequested
                ? null
                : await FetchOsrmRouteAsync($"route/v1/driving/{legCoords}?overview=full&geometries=geojson", 1, ct);
            if (legResult is null && !ct.IsCancellationRequested)
                legResult = await FetchOsrmRouteAsync($"{SecondaryBase}route/v1/driving/{legCoords}?overview=full&geometries=geojson", 1, ct);

            if (legResult is not null && legResult.Value.Points.Count >= 2)
            {
                legKm[i] = legResult.Value.LegKm.Length > 0 ? legResult.Value.LegKm[0] : Geo.HaversineKm(from.Lat, from.Lng, to.Lat, to.Lng);
                var legPoints = legResult.Value.Points;
                var startIndex = points.Count > 0 ? 1 : 0;
                for (int j = startIndex; j < legPoints.Count; j++)
                {
                    points.Add(legPoints[j]);
                }
            }
            else
            {
                // Fallback for this single leg only
                legKm[i] = Geo.HaversineKm(from.Lat, from.Lng, to.Lat, to.Lng);
                var straight = Geo.Densify(new[] { from, to });
                var startIndex = points.Count > 0 ? 1 : 0;
                for (int j = startIndex; j < straight.Count; j++)
                {
                    points.Add(straight[j]);
                }
            }
        }

        return (legKm, points);
    }
}
