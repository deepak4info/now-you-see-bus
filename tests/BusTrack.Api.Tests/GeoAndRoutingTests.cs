using BusTrack.Api.Services;
using Xunit;

namespace BusTrack.Api.Tests;

public class GeoAndRoutingTests
{
    [Fact]
    public void HaversineKm_CalculatesDistanceAccurately()
    {
        // Hisar (29.1492, 75.7217) to Barwala (29.3804, 75.9125) is approx 31-32 km direct chord
        var distance = Geo.HaversineKm(29.1492, 75.7217, 29.3804, 75.9125);

        Assert.True(distance > 30.0 && distance < 35.0, $"Expected distance between 30 and 35 km, but got {distance:F2} km");
    }

    [Fact]
    public void HaversineKm_SamePoint_ReturnsZero()
    {
        var distance = Geo.HaversineKm(29.1492, 75.7217, 29.1492, 75.7217);
        Assert.Equal(0, distance, precision: 4);
    }

    [Fact]
    public void Length_CalculatesPolylineCumulativeDistance()
    {
        var path = new List<LatLng>
        {
            new(29.1492, 75.7217), // Hisar
            new(29.3804, 75.9125), // Barwala
            new(29.5300, 75.9000)  // Tohana direction
        };

        var total = Geo.Length(path);
        var seg1 = Geo.HaversineKm(path[0].Lat, path[0].Lng, path[1].Lat, path[1].Lng);
        var seg2 = Geo.HaversineKm(path[1].Lat, path[1].Lng, path[2].Lat, path[2].Lng);

        Assert.Equal(seg1 + seg2, total, precision: 4);
    }

    [Fact]
    public void Project_FindsDistanceAlongPathAndDistanceToLine()
    {
        var path = new List<LatLng>
        {
            new(29.0, 75.0),
            new(29.0, 76.0)
        };

        // Point is right in the middle, slightly north
        var testPoint = new LatLng(29.05, 75.5);
        var (distanceAlongKm, distanceToLineKm) = Geo.Project(testPoint, path);

        var totalPathLen = Geo.Length(path);
        // Distance along should be approx half of total length
        Assert.True(Math.Abs(distanceAlongKm - (totalPathLen / 2)) < 2.0);
        Assert.True(distanceToLineKm > 0 && distanceToLineKm < 10.0);
    }

    [Fact]
    public void PointAt_InterpolatesCorrectCoordinateAlongRoute()
    {
        var path = new List<LatLng>
        {
            new(29.0, 75.0),
            new(29.0, 76.0)
        };

        var totalLen = Geo.Length(path);
        var midpoint = Geo.PointAt(path, totalLen / 2);

        Assert.Equal(29.0, midpoint.Lat, precision: 1);
        Assert.Equal(75.5, midpoint.Lng, precision: 1);
    }

    [Theory]
    [InlineData(42.0, 60)]  // 42 km at 42 km/h is 60 minutes
    [InlineData(21.0, 30)]  // 21 km at 42 km/h is 30 minutes
    [InlineData(0.0, 0)]    // 0 km is 0 minutes
    public void EtaMinutes_CalculatesTravelTimeBasedOnRoadSpeed(double distanceKm, int expectedMinutes)
    {
        var minutes = Geo.EtaMinutes(distanceKm);
        Assert.Equal(expectedMinutes, minutes);
    }
}
