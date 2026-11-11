using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusTrack.Api.Tests;

public class TrackingFeedTests
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

    private static async Task<(Operator op, Route route, Bus bus, Driver driver)> SeedBaseAsync(AppDbContext db)
    {
        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var origin = new Stop { Id = 1, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.71m, IsActive = true };
        var mid = new Stop { Id = 2, Name = "Bhattu", Code = "BHT", Latitude = 29.55m, Longitude = 75.55m, IsActive = true };
        var dest = new Stop { Id = 3, Name = "Fatehabad Bus Stand", Code = "FTB", Latitude = 29.51m, Longitude = 75.45m, IsActive = true };
        var user = new User { Id = Guid.NewGuid(), FullName = "Rajesh Kumar", Email = "drv@test.local", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, User = user, LicenseNumber = "HR-01-2026", OperatorId = op.Id, IsActive = true };
        var bus = new Bus
        {
            Id = 1,
            OperatorId = op.Id,
            Operator = op,
            FleetNumber = "HR-62-0101",
            RegistrationNumber = "HR62GA0101",
            Model = "TATA Starbus Ultra",
            BusType = "Express",
            Capacity = 52,
            IsActive = true
        };
        var route = new Route
        {
            Id = 1,
            Code = "BHN-FTB",
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
            new RouteStop { Id = 2, RouteId = route.Id, StopId = mid.Id, SequenceNo = 2, DistanceFromOriginKm = 21m },
            new RouteStop { Id = 3, RouteId = route.Id, StopId = dest.Id, SequenceNo = 3, DistanceFromOriginKm = 42m }
        );
        await db.SaveChangesAsync();
        return (op, route, bus, driver);
    }

    [Fact]
    public async Task GetLiveTrackingAsync_OnlyReturnsRunningTrips_AndNeverScheduledCompletedOrCancelled()
    {
        await using var db = CreateInMemoryDb();
        var (_, route, bus, driver) = await SeedBaseAsync(db);

        var today = new DateOnly(2026, 11, 11);
        var nowIst = new DateTime(2026, 11, 11, 10, 30, 0);

        var sPassedScheduled = new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var sRunning = new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(9, 30), ServiceDays = "1111111", IsActive = true };
        var sCompleted = new Schedule { Id = 3, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(7, 0), ServiceDays = "1111111", IsActive = true };
        var sCancelled = new Schedule { Id = 4, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(8, 30), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(sPassedScheduled, sRunning, sCompleted, sCancelled);

        db.Trips.AddRange(
            new Trip { Id = 1, ScheduleId = sPassedScheduled.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Scheduled" },
            new Trip { Id = 2, ScheduleId = sRunning.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Running", StartedAt = DateTime.UtcNow.AddMinutes(-20) },
            new Trip { Id = 3, ScheduleId = sCompleted.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Completed", StartedAt = DateTime.UtcNow.AddHours(-3), EndedAt = DateTime.UtcNow.AddHours(-2) },
            new Trip { Id = 4, ScheduleId = sCancelled.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Cancelled" }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var live = await tracking.GetLiveTrackingAsync(nowIst);

        var item = Assert.Single(live);
        Assert.Equal(2, item.TripId);
        Assert.Equal("Running", item.Status);
        Assert.Equal("TATA Starbus Ultra", item.BusName);
        Assert.Equal("Bhuna", item.OriginStop);
        Assert.Equal("Fatehabad Bus Stand", item.DestinationStop);
        Assert.False(string.IsNullOrWhiteSpace(item.EstimatedArrivalTime));
    }

    [Fact]
    public async Task GetRecentTrackingAsync_ReturnsCompletedAndCancelledNewestFirst_WithPagination()
    {
        await using var db = CreateInMemoryDb();
        var (_, route, bus, driver) = await SeedBaseAsync(db);

        var today = new DateOnly(2026, 11, 11);
        var yesterday = today.AddDays(-1);
        var s1 = new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var s2 = new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(10, 0), ServiceDays = "1111111", IsActive = true };
        var s3 = new Schedule { Id = 3, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(12, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(s1, s2, s3);

        var baseUtc = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        db.Trips.AddRange(
            new Trip { Id = 10, ScheduleId = s1.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = yesterday, Status = "Completed", StartedAt = baseUtc.AddDays(-1), EndedAt = baseUtc.AddDays(-1).AddHours(1) },
            new Trip { Id = 11, ScheduleId = s2.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Completed", StartedAt = baseUtc.AddHours(-2), EndedAt = baseUtc.AddHours(-1) },
            new Trip { Id = 12, ScheduleId = s3.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Cancelled", EndedAt = baseUtc.AddMinutes(-10) },
            new Trip { Id = 13, ScheduleId = s1.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today, Status = "Running", StartedAt = baseUtc },
            new Trip { Id = 14, ScheduleId = s2.Id, BusId = bus.Id, DriverId = driver.Id, RouteId = route.Id, ServiceDate = today.AddDays(1), Status = "Scheduled" }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);

        var page1 = await tracking.GetRecentTrackingAsync(page: 1, pageSize: 2);
        Assert.Equal(3, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.Equal(new int?[] { 12, 11 }, page1.Items.Select(i => i.TripId).ToArray());
        Assert.All(page1.Items, i => Assert.Contains(i.Status, new[] { "Completed", "Cancelled" }));

        var page2 = await tracking.GetRecentTrackingAsync(page: 2, pageSize: 2);
        Assert.False(page2.HasMore);
        var single = Assert.Single(page2.Items);
        Assert.Equal(10, single.TripId);
    }

    [Fact]
    public async Task GetUpcomingTrackingAsync_SortsByDateAndTime_ExcludesPassedAndRunningOrFinishedTrips()
    {
        await using var db = CreateInMemoryDb();
        var (_, route, bus, driver) = await SeedBaseAsync(db);

        var today = new DateOnly(2026, 11, 11);
        var nowIst = new DateTime(2026, 11, 11, 10, 0, 0);

        var s0800 = new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var s1100 = new Schedule { Id = 2, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(11, 0), ServiceDays = "1111111", IsActive = true };
        var s1500 = new Schedule { Id = 3, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(15, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(s0800, s1100, s1500);

        // Today 11:00 is already Running -> must live in Live tab, not Upcoming.
        db.Trips.Add(new Trip
        {
            Id = 20,
            ScheduleId = s1100.Id,
            BusId = bus.Id,
            DriverId = driver.Id,
            RouteId = route.Id,
            ServiceDate = today,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var result = await tracking.GetUpcomingTrackingAsync(page: 1, pageSize: 10, daysAhead: 1, nowIstOverride: nowIst);

        // Expect: today 15:00, then tomorrow 08:00, 11:00, 15:00 (4 total; today 08:00 passed and today 11:00 is Running).
        Assert.Equal(4, result.TotalCount);
        Assert.False(result.HasMore);
        Assert.Equal(
            new[] { ("2026-11-11", "15:00"), ("2026-11-12", "08:00"), ("2026-11-12", "11:00"), ("2026-11-12", "15:00") },
            result.Items.Select(i => (i.ServiceDate, i.DepartureTime)).ToArray()
        );
        Assert.All(result.Items, i => Assert.Equal("Scheduled", i.Status));
    }

    [Fact]
    public async Task GetByFleetAsync_WithExplicitTripId_ReturnsHistoricalCompletedTrip()
    {
        await using var db = CreateInMemoryDb();
        var (_, route, bus, driver) = await SeedBaseAsync(db);

        var yesterday = new DateOnly(2026, 11, 10);
        var schedule = new Schedule { Id = 1, RouteId = route.Id, BusId = bus.Id, DriverId = driver.Id, DepartureTime = new TimeOnly(7, 30), ServiceDays = "1111111", IsActive = true };
        var trip = new Trip
        {
            Id = 99,
            ScheduleId = schedule.Id,
            BusId = bus.Id,
            DriverId = driver.Id,
            RouteId = route.Id,
            ServiceDate = yesterday,
            Status = "Completed",
            StartedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc),
            EndedAt = new DateTime(2026, 11, 10, 3, 5, 0, DateTimeKind.Utc)
        };
        db.Schedules.Add(schedule);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var snap = await tracking.GetByFleetAsync(bus.FleetNumber, null, null, route.Id, CancellationToken.None, tripId: trip.Id);

        Assert.NotNull(snap);
        Assert.Equal(99, snap!.TripId);
        Assert.Equal("Completed", snap.Status);
        Assert.Equal("2026-11-10", snap.ServiceDate);
        Assert.Equal("TATA Starbus Ultra", snap.BusName);
        Assert.Equal("Bhuna", snap.OriginStop);
        Assert.Equal("Fatehabad Bus Stand", snap.DestinationStop);
        Assert.NotNull(snap.ActualDepartureTime);
        Assert.NotNull(snap.ActualArrivalTime);
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
