using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusTrack.Api.Tests;

public class SearchRouteTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TrackingService CreateTracking(AppDbContext db) =>
        new(db, new TestHubContext());

    private static async Task<(Route route, Bus bus, Driver driver, Stop origin, Stop mid, Stop dest)> SeedCorridorAsync(
        AppDbContext db,
        decimal midDistKm = 30m,
        decimal destDistKm = 60m)
    {
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var origin = new Stop { Id = 1, Name = "Hisar", Code = "HSR", Latitude = 29.15m, Longitude = 75.72m, IsActive = true };
        var mid = new Stop { Id = 2, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.71m, IsActive = true };
        var dest = new Stop { Id = 3, Name = "Fatehabad Bus Stand", Code = "FTB", Latitude = 29.51m, Longitude = 75.45m, IsActive = true };

        var user = new User { Id = Guid.NewGuid(), FullName = "Driver Kumar", Email = "drv@test.local", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, User = user, LicenseNumber = "HR-01-2026", OperatorId = op.Id, IsActive = true };
        var bus = new Bus
        {
            Id = 1,
            OperatorId = op.Id,
            Operator = op,
            FleetNumber = "HR-62-0101",
            RegistrationNumber = "HR62GA0101",
            BusType = "Express",
            Capacity = 52,
            IsActive = true
        };
        var route = new Route
        {
            Id = 1,
            Code = "BHN-FTB-01",
            Name = "Bhuna - Fatehabad Bus Stand",
            OperatorId = op.Id,
            IsActive = true
        };

        db.Operators.Add(op);
        db.Stops.AddRange(origin, mid, dest);
        db.Users.Add(user);
        db.Drivers.Add(driver);
        db.Buses.Add(bus);
        db.Routes.Add(route);
        db.RouteStops.AddRange(
            new RouteStop { Id = 1, RouteId = route.Id, StopId = origin.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = route.Id, StopId = mid.Id, SequenceNo = 2, DistanceFromOriginKm = midDistKm },
            new RouteStop { Id = 3, RouteId = route.Id, StopId = dest.Id, SequenceNo = 3, DistanceFromOriginKm = destDistKm }
        );
        await db.SaveChangesAsync();
        return (route, bus, driver, origin, mid, dest);
    }

    [Fact]
    public async Task SearchRouteAsync_ExcludesPassedAndExactCurrentDeparturesToday()
    {
        await using var db = CreateInMemoryDb();
        var (route, bus, driver, _, _, _) = await SeedCorridorAsync(db, midDistKm: 20m, destDistKm: 40m);

        db.Schedules.AddRange(
            new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true },
            new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(10, 30), ServiceDays = "1111111", IsActive = true },
            new Schedule { Id = 3, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(12, 15), ServiceDays = "1111111", IsActive = true },
            new Schedule { Id = 4, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(18, 0), ServiceDays = "1111111", IsActive = true }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var today = new DateOnly(2026, 11, 11);
        var nowIst = new DateTime(2026, 11, 11, 10, 30, 0);

        var result = await tracking.SearchRouteAsync("Hisar", "Fatehabad Bus Stand", today, nowIst);

        Assert.False(result.ShowingNextDate);
        Assert.Equal(today, result.EffectiveDate);
        Assert.Equal(new[] { "12:15", "18:00" }, result.Results.Select(r => r.DepartureTime).ToArray());
    }

    [Fact]
    public async Task SearchRouteAsync_WhenNoBusesRemainToday_FallsBackToNextDateAutomatically()
    {
        await using var db = CreateInMemoryDb();
        var (route, bus, driver, _, _, _) = await SeedCorridorAsync(db);

        // 2026-11-11 is Wednesday (DayIndex 2), 2026-11-12 is Thursday (DayIndex 3).
        db.Schedules.AddRange(
            new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(6, 30), ServiceDays = "1111111", IsActive = true },
            new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(9, 0), ServiceDays = "0001000", IsActive = true }, // Thursday only
            new Schedule { Id = 3, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(17, 30), ServiceDays = "1111111", IsActive = true }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var today = new DateOnly(2026, 11, 11);
        var nowIst = new DateTime(2026, 11, 11, 20, 0, 0);

        var result = await tracking.SearchRouteAsync("Hisar", "Fatehabad Bus Stand", today, nowIst);

        Assert.True(result.ShowingNextDate);
        Assert.Equal(today, result.RequestedDate);
        Assert.Equal(today.AddDays(1), result.EffectiveDate);
        Assert.Equal(new[] { "06:30", "09:00", "17:30" }, result.Results.Select(r => r.DepartureTime).ToArray());
        Assert.All(result.Results, snap => Assert.Equal("2026-11-12", snap.ServiceDate));
    }

    [Fact]
    public async Task SearchRouteAsync_ForFutureDate_ReturnsAllDaySchedulesWithoutTodayTimeCutoff()
    {
        await using var db = CreateInMemoryDb();
        var (route, bus, driver, _, _, _) = await SeedCorridorAsync(db);

        db.Schedules.AddRange(
            new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(7, 0), ServiceDays = "1111111", IsActive = true },
            new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(19, 0), ServiceDays = "1111111", IsActive = true }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var nowIst = new DateTime(2026, 11, 11, 16, 0, 0);
        var futureDate = new DateOnly(2026, 11, 13);

        var result = await tracking.SearchRouteAsync("Hisar", "Fatehabad Bus Stand", futureDate, nowIst);

        Assert.False(result.ShowingNextDate);
        Assert.Equal(futureDate, result.EffectiveDate);
        Assert.Equal(new[] { "07:00", "19:00" }, result.Results.Select(r => r.DepartureTime).ToArray());
    }

    [Fact]
    public async Task SearchRouteAsync_IntermediateBoardingStop_FiltersUsingBoardingStopDepartureTime()
    {
        await using var db = CreateInMemoryDb();
        // 42 km from origin -> Geo.EtaMinutes(42) = 60 min (RoadSpeedKmph = 42).
        var (route, bus, driver, _, _, _) = await SeedCorridorAsync(db, midDistKm: 42m, destDistKm: 70m);

        // Origin departure at 09:30 -> scheduled at Bhuna at 10:30.
        db.Schedules.Add(
            new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(9, 30), ServiceDays = "1111111", IsActive = true }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var today = new DateOnly(2026, 11, 11);

        var beforeBoarding = await tracking.SearchRouteAsync("Bhuna", "Fatehabad Bus Stand", today, new DateTime(2026, 11, 11, 10, 10, 0));
        Assert.Single(beforeBoarding.Results);
        Assert.Equal(today, beforeBoarding.EffectiveDate);

        var afterBoarding = await tracking.SearchRouteAsync("Bhuna", "Fatehabad Bus Stand", today, new DateTime(2026, 11, 11, 10, 35, 0));
        Assert.True(afterBoarding.ShowingNextDate);
        Assert.Equal(today.AddDays(1), afterBoarding.EffectiveDate);
    }

    [Fact]
    public async Task SearchRouteAsync_ExcludesCompletedTripsToday()
    {
        await using var db = CreateInMemoryDb();
        var (route, bus, driver, _, _, _) = await SeedCorridorAsync(db);
        var today = new DateOnly(2026, 11, 11);

        var s1 = new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(12, 0), ServiceDays = "1111111", IsActive = true };
        var s2 = new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(15, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(s1, s2);
        db.Trips.Add(new Trip
        {
            Id = 1,
            ScheduleId = s1.Id,
            BusId = bus.Id,
            DriverId = driver.Id,
            RouteId = route.Id,
            ServiceDate = today,
            Status = "Completed"
        });
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var result = await tracking.SearchRouteAsync("Hisar", "Fatehabad Bus Stand", today, new DateTime(2026, 11, 11, 11, 0, 0));

        Assert.Single(result.Results);
        Assert.Equal("15:00", result.Results[0].DepartureTime);
    }

    private sealed class TestHubContext : IHubContext<TrackingHub>
    {
        public IHubClients Clients => new NoopClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class NoopClients : IHubClients
        {
            public IClientProxy All => new NoopProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
            public IClientProxy Client(string connectionId) => new NoopProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoopProxy();
            public IClientProxy Group(string groupName) => new NoopProxy();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoopProxy();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoopProxy();
            public IClientProxy User(string userId) => new NoopProxy();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new NoopProxy();
        }

        private sealed class NoopProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
