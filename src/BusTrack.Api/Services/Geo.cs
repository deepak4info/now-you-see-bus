namespace BusTrack.Api.Services;

public readonly record struct LatLng(double Lat, double Lng);

public static class Geo
{
    public const double RoadSpeedKmph = 42.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = Rad(lat2 - lat1);
        double dLon = Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double Length(IReadOnlyList<LatLng> path)
    {
        double sum = 0;
        for (int i = 1; i < path.Count; i++)
            sum += HaversineKm(path[i - 1].Lat, path[i - 1].Lng, path[i].Lat, path[i].Lng);
        return sum;
    }

    public static (double DistanceAlongKm, double DistanceToLineKm) Project(LatLng point, IReadOnlyList<LatLng> path)
    {
        if (path.Count == 0) return (0, double.MaxValue);
        if (path.Count == 1) return (0, HaversineKm(point.Lat, point.Lng, path[0].Lat, path[0].Lng));

        double bestDist = double.MaxValue;
        double bestAlong = 0;
        double cursor = 0;
        var origin = path[0];

        for (int i = 0; i < path.Count - 1; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var (ax, ay) = ToXY(a, origin);
            var (bx, by) = ToXY(b, origin);
            var (px, py) = ToXY(point, origin);
            double dx = bx - ax;
            double dy = by - ay;
            double segLen2 = dx * dx + dy * dy;
            double t = segLen2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / segLen2, 0, 1);
            double projLat = a.Lat + (b.Lat - a.Lat) * t;
            double projLng = a.Lng + (b.Lng - a.Lng) * t;
            double dist = HaversineKm(point.Lat, point.Lng, projLat, projLng);
            double segLen = HaversineKm(a.Lat, a.Lng, b.Lat, b.Lng);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestAlong = cursor + t * segLen;
            }
            cursor += segLen;
        }

        return (bestAlong, bestDist);
    }

    public static LatLng PointAt(IReadOnlyList<LatLng> path, double distanceKm)
    {
        if (path.Count == 0) return new LatLng(0, 0);
        if (distanceKm <= 0) return path[0];
        double cursor = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double seg = HaversineKm(path[i - 1].Lat, path[i - 1].Lng, path[i].Lat, path[i].Lng);
            if (cursor + seg >= distanceKm)
            {
                double t = seg < 1e-6 ? 0 : (distanceKm - cursor) / seg;
                return new LatLng(
                    path[i - 1].Lat + (path[i].Lat - path[i - 1].Lat) * t,
                    path[i - 1].Lng + (path[i].Lng - path[i - 1].Lng) * t);
            }
            cursor += seg;
        }
        return path[^1];
    }

    public static double Bearing(LatLng a, LatLng b)
    {
        double y = Math.Sin(Rad(b.Lng - a.Lng)) * Math.Cos(Rad(b.Lat));
        double x = Math.Cos(Rad(a.Lat)) * Math.Sin(Rad(b.Lat)) -
                   Math.Sin(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Cos(Rad(b.Lng - a.Lng));
        double brng = Math.Atan2(y, x) * 180 / Math.PI;
        return (brng + 360) % 360;
    }

    public static List<LatLng> Densify(IReadOnlyList<LatLng> stops, double stepKm = 2)
    {
        var result = new List<LatLng>();
        if (stops.Count == 0) return result;
        if (stops.Count == 1)
        {
            result.Add(stops[0]);
            return result;
        }
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            double dist = HaversineKm(a.Lat, a.Lng, b.Lat, b.Lng);
            int steps = Math.Max(1, (int)Math.Ceiling(dist / stepKm));
            for (int s = 0; s < steps; s++)
            {
                double t = (double)s / steps;
                result.Add(new LatLng(a.Lat + (b.Lat - a.Lat) * t, a.Lng + (b.Lng - a.Lng) * t));
            }
        }
        result.Add(stops[^1]);
        return result;
    }

    public static int EtaMinutes(double remainingKm, double? speedKmph = null)
    {
        double speed = speedKmph is > 8 and < 110 ? speedKmph.Value : RoadSpeedKmph;
        if (remainingKm <= 0.15) return 0;
        return (int)Math.Ceiling(remainingKm / speed * 60.0);
    }

    public static decimal FareForKm(double km)
    {
        if (km <= 0.2) return 0;
        var raw = (decimal)km * 1.80m;
        var rounded = Math.Round(raw / 5m, MidpointRounding.AwayFromZero) * 5m;
        return rounded < 15 ? 15 : rounded;
    }

    private static (double x, double y) ToXY(LatLng p, LatLng origin)
    {
        double x = (p.Lng - origin.Lng) * Math.Cos(Rad(origin.Lat)) * 111.320;
        double y = (p.Lat - origin.Lat) * 110.574;
        return (x, y);
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;
}

public static class IndiaClock
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static DateOnly Today() => DateOnly.FromDateTime(Now());

    public static DateTime Now() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);

    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, Tz);
    }

    public static string Clock(TimeOnly time) => time.ToString("HH:mm");

    public static string ClockFromDeparture(TimeOnly departure, double distanceKm)
    {
        int mins = Geo.EtaMinutes(distanceKm, Geo.RoadSpeedKmph);
        return departure.AddMinutes(mins).ToString("HH:mm");
    }

    public static int DayIndex(DateOnly date)
    {
        var dow = date.DayOfWeek;
        return ((int)dow + 6) % 7;
    }

    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
