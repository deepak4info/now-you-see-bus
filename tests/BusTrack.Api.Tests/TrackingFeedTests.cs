using System.Globalization;
using BusTrack.Api.Controllers;
using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task GetTrackingFilterOptionsAsync_ReturnsOnlyActiveOperatorsRoutesAndDistinctBusTypes()
    {
        await using var db = CreateInMemoryDb();
        await SeedMultiNetworkAsync(db);

        // Inactive operator, route, and bus should be excluded.
        var inactiveOp = new Operator { Id = 99, Name = "Retired Lines", Code = "RET", IsActive = false };
        var inactiveRoute = new Route { Id = 99, Code = "OLD-1", Name = "Old Corridor", OperatorId = 1, IsActive = false };
        var inactiveBus = new Bus
        {
            Id = 99,
            OperatorId = 1,
            FleetNumber = "OLD-99",
            RegistrationNumber = "HR00OLD99",
            BusType = "Sleeper",
            Capacity = 40,
            IsActive = false
        };
        db.Operators.Add(inactiveOp);
        db.Routes.Add(inactiveRoute);
        db.Buses.Add(inactiveBus);
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);
        var options = await tracking.GetTrackingFilterOptionsAsync();

        Assert.Equal(new[] { "Haryana Roadways", "Sutlej Express" }, options.Operators.Select(o => o.Name).ToArray());
        Assert.Equal(2, options.Routes.Count);
        Assert.Equal(new[] { "BHN-FTB", "HSR-DL" }, options.Routes.Select(r => r.Code).ToArray());
        Assert.Equal("Bhuna", options.Routes[0].OriginStop);
        Assert.Equal("Fatehabad Bus Stand", options.Routes[0].DestinationStop);
        Assert.Equal(new[] { "AC", "Express" }, options.BusTypes.ToArray());
    }

    [Fact]
    public async Task GetLiveTrackingAsync_FiltersByBusNumberRouteOperatorDateBusTypeStopTimeOfDayAndStatus()
    {
        await using var db = CreateInMemoryDb();
        var seeded = await SeedMultiNetworkAsync(db);

        var today = new DateOnly(2026, 11, 11);
        var nowIst = new DateTime(2026, 11, 11, 19, 0, 0);

        var sMorning = new Schedule { Id = 1, RouteId = seeded.Route1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var sEvening = new Schedule { Id = 2, RouteId = seeded.Route2.Id, BusId = seeded.Bus2.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(18, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(sMorning, sEvening);

        var t1 = new Trip
        {
            Id = 101,
            ScheduleId = sMorning.Id,
            BusId = seeded.Bus1.Id,
            DriverId = seeded.Driver.Id,
            RouteId = seeded.Route1.Id,
            ServiceDate = today,
            Status = "Running",
            StartedAt = IndiaClock.ToUtc(today, new TimeOnly(8, 0))
        };
        var t2 = new Trip
        {
            Id = 102,
            ScheduleId = sEvening.Id,
            BusId = seeded.Bus2.Id,
            DriverId = seeded.Driver.Id,
            RouteId = seeded.Route2.Id,
            ServiceDate = today,
            Status = "Running",
            StartedAt = IndiaClock.ToUtc(today, new TimeOnly(18, 12))
        };
        db.Trips.AddRange(t1, t2);
        db.TripStopEtas.AddRange(
            new TripStopEta { Id = 1, TripId = t1.Id, StopId = 1, SequenceNo = 1, Status = "Departed", DelayMinutes = 0 },
            new TripStopEta { Id = 2, TripId = t2.Id, StopId = 4, SequenceNo = 1, Status = "Departed", DelayMinutes = 12 }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);

        // BusNumber variants: compact without hyphens, "Bus " prefix, registration number, and bare "bus" safety check
        var byCompact = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(BusNumber: "hr620101"));
        Assert.Equal(101, Assert.Single(byCompact).TripId);

        var byPrefix = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(BusNumber: "Bus HR-62-0101"));
        Assert.Equal(101, Assert.Single(byPrefix).TripId);

        var byReg = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(BusNumber: "PB10ZZ"));
        Assert.Equal(102, Assert.Single(byReg).TripId);

        // RouteId & intermediate Stop
        var byRoute = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(RouteId: seeded.Route2.Id));
        Assert.Equal(102, Assert.Single(byRoute).TripId);

        var byIntermediateStop = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Stop: "Bhattu"));
        Assert.Equal(101, Assert.Single(byIntermediateStop).TripId);

        // OperatorId, BusType, TimeOfDay
        var byOperator = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(OperatorId: seeded.Operator2.Id));
        Assert.Equal(102, Assert.Single(byOperator).TripId);

        var byBusType = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(BusType: "AC"));
        Assert.Equal(102, Assert.Single(byBusType).TripId);

        var byMorning = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(TimeOfDay: "morning"));
        Assert.Equal(101, Assert.Single(byMorning).TripId);

        var byEvening = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(TimeOfDay: "evening"));
        Assert.Equal(102, Assert.Single(byEvening).TripId);

        // Date (today matches, 3 days ago returns empty)
        var byToday = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Date: today));
        Assert.Equal(2, byToday.Count);

        var byOldDate = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Date: today.AddDays(-3)));
        Assert.Empty(byOldDate);

        // Non-live status returns empty immediately
        var incompatibleStatus = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Status: "Completed"));
        Assert.Empty(incompatibleStatus);
    }

    [Fact]
    public async Task GetLiveTrackingAsync_FiltersByPunctualityStatus_OnTimeAndDelayed()
    {
        await using var db = CreateInMemoryDb();
        var seeded = await SeedMultiNetworkAsync(db);

        var nowIst = IndiaClock.Now();
        var delayedIst = nowIst.AddMinutes(-20);
        var onTimeDate = DateOnly.FromDateTime(nowIst);
        var delayedDate = DateOnly.FromDateTime(delayedIst);

        var sOnTime = new Schedule
        {
            Id = 10,
            RouteId = seeded.Route1.Id,
            BusId = seeded.Bus1.Id,
            DriverId = seeded.Driver.Id,
            DepartureTime = TimeOnly.FromDateTime(nowIst),
            ServiceDays = "1111111",
            IsActive = true
        };
        var sDelayed = new Schedule
        {
            Id = 11,
            RouteId = seeded.Route2.Id,
            BusId = seeded.Bus2.Id,
            DriverId = seeded.Driver.Id,
            DepartureTime = TimeOnly.FromDateTime(delayedIst),
            ServiceDays = "1111111",
            IsActive = true
        };
        db.Schedules.AddRange(sOnTime, sDelayed);

        db.Trips.AddRange(
            new Trip
            {
                Id = 111,
                ScheduleId = sOnTime.Id,
                BusId = seeded.Bus1.Id,
                DriverId = seeded.Driver.Id,
                RouteId = seeded.Route1.Id,
                ServiceDate = onTimeDate,
                Status = "Running",
                StartedAt = DateTime.UtcNow
            },
            new Trip
            {
                Id = 112,
                ScheduleId = sDelayed.Id,
                BusId = seeded.Bus2.Id,
                DriverId = seeded.Driver.Id,
                RouteId = seeded.Route2.Id,
                ServiceDate = delayedDate,
                Status = "Running",
                StartedAt = DateTime.UtcNow.AddMinutes(-5)
            }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);

        var delayed = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Status: "delayed"));
        Assert.Equal(112, Assert.Single(delayed).TripId);

        var ontime = await tracking.GetLiveTrackingAsync(nowIst, new TrackingFilterDto(Status: "ontime"));
        Assert.Equal(111, Assert.Single(ontime).TripId);
    }

    [Fact]
    public async Task GetRecentTrackingAsync_FiltersAcrossDateStatusOperatorRouteBusAndPaginates()
    {
        await using var db = CreateInMemoryDb();
        var seeded = await SeedMultiNetworkAsync(db);

        var d1 = new DateOnly(2026, 11, 9);
        var d2 = new DateOnly(2026, 11, 10);

        var s1 = new Schedule { Id = 1, RouteId = seeded.Route1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var s2 = new Schedule { Id = 2, RouteId = seeded.Route1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(14, 0), ServiceDays = "1111111", IsActive = true };
        var s3 = new Schedule { Id = 3, RouteId = seeded.Route2.Id, BusId = seeded.Bus2.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(22, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(s1, s2, s3);

        var baseUtc = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        db.Trips.AddRange(
            new Trip { Id = 201, ScheduleId = s1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, RouteId = seeded.Route1.Id, ServiceDate = d1, Status = "Completed", StartedAt = baseUtc.AddDays(-1), EndedAt = baseUtc.AddDays(-1).AddHours(1) },
            new Trip { Id = 202, ScheduleId = s2.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, RouteId = seeded.Route1.Id, ServiceDate = d2, Status = "Cancelled", EndedAt = baseUtc.AddMinutes(-20) },
            new Trip { Id = 203, ScheduleId = s1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, RouteId = seeded.Route1.Id, ServiceDate = d2, Status = "Completed", StartedAt = baseUtc.AddHours(-3), EndedAt = baseUtc.AddHours(-2) },
            new Trip { Id = 204, ScheduleId = s3.Id, BusId = seeded.Bus2.Id, DriverId = seeded.Driver.Id, RouteId = seeded.Route2.Id, ServiceDate = d2, Status = "Completed", StartedAt = baseUtc.AddMinutes(-40), EndedAt = baseUtc.AddMinutes(-5) }
        );
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);

        // Date + Operator filter with pagination (2 matches for Bus1/Operator1 on d2 -> pageSize=1)
        var page1 = await tracking.GetRecentTrackingAsync(
            page: 1,
            pageSize: 1,
            filter: new TrackingFilterDto(Date: d2, Operator: "Haryana"));
        Assert.Equal(2, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.Equal(202, Assert.Single(page1.Items).TripId);

        var page2 = await tracking.GetRecentTrackingAsync(
            page: 2,
            pageSize: 1,
            filter: new TrackingFilterDto(Date: d2, Operator: "Haryana"));
        Assert.False(page2.HasMore);
        Assert.Equal(203, Assert.Single(page2.Items).TripId);

        // Status=Cancelled
        var cancelled = await tracking.GetRecentTrackingAsync(filter: new TrackingFilterDto(Status: "Cancelled"));
        Assert.Equal(202, Assert.Single(cancelled.Items).TripId);

        // TimeOfDay=night + Stop=Hisar
        var nightHisar = await tracking.GetRecentTrackingAsync(filter: new TrackingFilterDto(TimeOfDay: "night", Stop: "Hisar"));
        Assert.Equal(204, Assert.Single(nightHisar.Items).TripId);
    }

    [Fact]
    public async Task GetUpcomingTrackingAsync_FiltersBySpecificFutureDateAndAttributes()
    {
        await using var db = CreateInMemoryDb();
        var seeded = await SeedMultiNetworkAsync(db);

        var nowIst = new DateTime(2026, 11, 11, 10, 0, 0);
        var today = DateOnly.FromDateTime(nowIst);

        var s1 = new Schedule { Id = 1, RouteId = seeded.Route1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(15, 0), ServiceDays = "1111111", IsActive = true };
        var s2 = new Schedule { Id = 2, RouteId = seeded.Route2.Id, BusId = seeded.Bus2.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(21, 30), ServiceDays = "1111111", IsActive = true };
        db.Schedules.AddRange(s1, s2);
        await db.SaveChangesAsync();

        var tracking = CreateTracking(db);

        // Specific date 10 days ahead (beyond default daysAhead=6) should work when Date filter is explicitly given.
        var farFuture = today.AddDays(10);
        var futureRes = await tracking.GetUpcomingTrackingAsync(
            page: 1,
            pageSize: 10,
            daysAhead: 6,
            nowIstOverride: nowIst,
            filter: new TrackingFilterDto(Date: farFuture, BusType: "AC"));
        var futureItem = Assert.Single(futureRes.Items);
        Assert.Equal(farFuture.ToString("yyyy-MM-dd"), futureItem.ServiceDate);
        Assert.Equal(seeded.Bus2.FleetNumber, futureItem.FleetNumber);

        // Past date returns 0 upcoming results.
        var pastRes = await tracking.GetUpcomingTrackingAsync(
            nowIstOverride: nowIst,
            filter: new TrackingFilterDto(Date: today.AddDays(-1)));
        Assert.Equal(0, pastRes.TotalCount);
        Assert.Empty(pastRes.Items);

        // Route + TimeOfDay filter on default window.
        var routeAfternoon = await tracking.GetUpcomingTrackingAsync(
            daysAhead: 0,
            nowIstOverride: nowIst,
            filter: new TrackingFilterDto(Route: "BHN-FTB", TimeOfDay: "afternoon"));
        var afternoonItem = Assert.Single(routeAfternoon.Items);
        Assert.Equal("15:00", afternoonItem.DepartureTime);
    }

    [Fact]
    public async Task PassengerController_TrackingEndpoints_SupportFilterQueryAndOptions()
    {
        await using var db = CreateInMemoryDb();
        var seeded = await SeedMultiNetworkAsync(db);
        var today = IndiaClock.Today();

        var s1 = new Schedule { Id = 1, RouteId = seeded.Route1.Id, BusId = seeded.Bus1.Id, DriverId = seeded.Driver.Id, DepartureTime = new TimeOnly(9, 0), ServiceDays = "1111111", IsActive = true };
        db.Schedules.Add(s1);
        db.Trips.Add(new Trip
        {
            Id = 301,
            ScheduleId = s1.Id,
            BusId = seeded.Bus1.Id,
            DriverId = seeded.Driver.Id,
            RouteId = seeded.Route1.Id,
            ServiceDate = today,
            Status = "Completed",
            StartedAt = DateTime.UtcNow.AddHours(-2),
            EndedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var controller = new PassengerController(db, CreateTracking(db));

        var filterOptionsResult = Assert.IsType<OkObjectResult>(await controller.TrackingFilters());
        var filterOptions = Assert.IsType<TrackingFilterOptionsDto>(filterOptionsResult.Value);
        Assert.Equal(2, filterOptions.Operators.Count);
        Assert.Equal(2, filterOptions.Routes.Count);

        var recentResult = Assert.IsType<OkObjectResult>(await controller.TrackingRecent(
            page: 1,
            pageSize: 8,
            query: new TrackingFilterQuery { BusNumber = "HR-62", OperatorId = seeded.Operator1.Id }));
        var paginated = Assert.IsType<PaginatedBusTrackingDto>(recentResult.Value);
        Assert.Equal(301, Assert.Single(paginated.Items).TripId);
    }

    [Fact]
    public async Task PassengerController_TrackingQueryString_BindsWithoutPrefix_ForRecentAndUpcoming()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var sp = services.BuildServiceProvider();

        var binderFactory = sp.GetRequiredService<IModelBinderFactory>();
        var metadataProvider = sp.GetRequiredService<IModelMetadataProvider>();
        var parameterBinder = sp.GetRequiredService<ParameterBinder>();

        async Task<TrackingFilterQuery?> BindQueryParamAsync(string methodName, string queryString)
        {
            var method = typeof(PassengerController).GetMethod(methodName)!;
            var paramInfo = method.GetParameters().Single(p => p.ParameterType == typeof(TrackingFilterQuery));
            var paramDesc = new ParameterDescriptor
            {
                Name = paramInfo.Name!,
                ParameterType = paramInfo.ParameterType,
                BindingInfo = BindingInfo.GetBindingInfo(paramInfo.GetCustomAttributes(false))
            };
            var metadata = metadataProvider.GetMetadataForType(typeof(TrackingFilterQuery));
            var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                BindingInfo = paramDesc.BindingInfo,
                CacheToken = paramInfo
            });

            var httpContext = new DefaultHttpContext { RequestServices = sp };
            httpContext.Request.QueryString = new QueryString(queryString);
            var actionContext = new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new ActionDescriptor());
            var valueProvider = new QueryStringValueProvider(BindingSource.Query, httpContext.Request.Query, CultureInfo.InvariantCulture);

            var result = await parameterBinder.BindModelAsync(actionContext, binder, valueProvider, paramDesc, metadata, value: null);
            return result.Model as TrackingFilterQuery;
        }

        var emptyRecent = await BindQueryParamAsync(nameof(PassengerController.TrackingRecent), "?page=1&pageSize=8");
        Assert.Null(emptyRecent?.ToFilterDto());

        var boundRecent = await BindQueryParamAsync(
            nameof(PassengerController.TrackingRecent),
            "?page=2&pageSize=8&busNumber=HR-62&routeId=1&date=2026-11-12&operatorId=1&busType=Express&status=Completed&stop=Bhattu&timeOfDay=afternoon");
        var recentDto = Assert.IsType<TrackingFilterDto>(boundRecent?.ToFilterDto());
        Assert.Equal("HR-62", recentDto.BusNumber);
        Assert.Equal(1, recentDto.RouteId);
        Assert.Equal(new DateOnly(2026, 11, 12), recentDto.Date);
        Assert.Equal(1, recentDto.OperatorId);
        Assert.Equal("Express", recentDto.BusType);
        Assert.Equal("Completed", recentDto.Status);
        Assert.Equal("Bhattu", recentDto.Stop);
        Assert.Equal("afternoon", recentDto.TimeOfDay);

        var boundUpcoming = await BindQueryParamAsync(
            nameof(PassengerController.TrackingUpcoming),
            "?page=1&pageSize=8&daysAhead=13&busNumber=PB-10&routeId=2&operator=Sutlej&busType=AC&stop=Hisar&timeOfDay=morning");
        var upcomingDto = Assert.IsType<TrackingFilterDto>(boundUpcoming?.ToFilterDto());
        Assert.Equal("PB-10", upcomingDto.BusNumber);
        Assert.Equal(2, upcomingDto.RouteId);
        Assert.Equal("Sutlej", upcomingDto.Operator);
        Assert.Equal("AC", upcomingDto.BusType);
        Assert.Equal("Hisar", upcomingDto.Stop);
        Assert.Equal("morning", upcomingDto.TimeOfDay);
    }

    private sealed record SeededMultiNetwork(
        Operator Operator1,
        Operator Operator2,
        Route Route1,
        Route Route2,
        Bus Bus1,
        Bus Bus2,
        Driver Driver
    );

    private static async Task<SeededMultiNetwork> SeedMultiNetworkAsync(AppDbContext db)
    {
        var op1 = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var op2 = new Operator { Id = 2, Name = "Sutlej Express", Code = "STJ", IsActive = true };

        var bhuna = new Stop { Id = 1, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.71m, IsActive = true };
        var bhattu = new Stop { Id = 2, Name = "Bhattu", Code = "BHT", Latitude = 29.55m, Longitude = 75.55m, IsActive = true };
        var fatehabad = new Stop { Id = 3, Name = "Fatehabad Bus Stand", Code = "FTB", Latitude = 29.51m, Longitude = 75.45m, IsActive = true };
        var hisar = new Stop { Id = 4, Name = "Hisar", Code = "HSR", Latitude = 29.15m, Longitude = 75.72m, IsActive = true };
        var delhi = new Stop { Id = 5, Name = "Delhi ISBT", Code = "DEL", Latitude = 28.66m, Longitude = 77.22m, IsActive = true };

        var user = new User { Id = Guid.NewGuid(), FullName = "Rajesh Kumar", Email = "drv-multi@test.local", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, User = user, LicenseNumber = "HR-01-2026", OperatorId = op1.Id, IsActive = true };

        var bus1 = new Bus
        {
            Id = 1,
            OperatorId = op1.Id,
            Operator = op1,
            FleetNumber = "HR-62-0101",
            RegistrationNumber = "HR62GA0101",
            Model = "TATA Starbus Ultra",
            BusType = "Express",
            Capacity = 52,
            IsActive = true
        };
        var bus2 = new Bus
        {
            Id = 2,
            OperatorId = op2.Id,
            Operator = op2,
            FleetNumber = "PB-10-0202",
            RegistrationNumber = "PB10ZZ0202",
            Model = "Volvo 9400",
            BusType = "AC",
            Capacity = 45,
            IsActive = true
        };

        var route1 = new Route { Id = 1, Code = "BHN-FTB", Name = "Bhuna - Fatehabad Bus Stand", OperatorId = op1.Id, IsActive = true };
        var route2 = new Route { Id = 2, Code = "HSR-DL", Name = "Hisar - Delhi ISBT", OperatorId = op2.Id, IsActive = true };

        db.Operators.AddRange(op1, op2);
        db.Stops.AddRange(bhuna, bhattu, fatehabad, hisar, delhi);
        db.Users.Add(user);
        db.Drivers.Add(driver);
        db.Buses.AddRange(bus1, bus2);
        db.Routes.AddRange(route1, route2);
        db.RouteStops.AddRange(
            new RouteStop { Id = 1, RouteId = route1.Id, StopId = bhuna.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = route1.Id, StopId = bhattu.Id, SequenceNo = 2, DistanceFromOriginKm = 21m },
            new RouteStop { Id = 3, RouteId = route1.Id, StopId = fatehabad.Id, SequenceNo = 3, DistanceFromOriginKm = 42m },
            new RouteStop { Id = 4, RouteId = route2.Id, StopId = hisar.Id, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 5, RouteId = route2.Id, StopId = delhi.Id, SequenceNo = 2, DistanceFromOriginKm = 165m }
        );
        await db.SaveChangesAsync();

        return new SeededMultiNetwork(op1, op2, route1, route2, bus1, bus2, driver);
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
