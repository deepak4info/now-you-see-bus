using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Route = BusTrack.Api.Data.Route;

namespace BusTrack.Api.Tests;

public class ShardingTests
{
    private static AppDbContext CreatePrimaryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"primary-{Guid.NewGuid()}")
            .Options);

    private static (ShardedDataService Service, Dictionary<string, DbContextOptions<TelemetryShardDbContext>> ShardOptions) CreateMultiRegionShards(
        bool failOpenToPrimary = true,
        bool simulateFaultOnNorth = false)
    {
        var shards = new List<ShardDefinition>
        {
            new() { ShardId = "north-india", Region = ShardRegions.NorthIndia, ConnectionString = "Server=north;Database=ShardNorth;Password=secret;" },
            new() { ShardId = "south-india", Region = ShardRegions.SouthIndia, ConnectionString = "Server=south;Database=ShardSouth;Password=secret;" },
            new() { ShardId = "east-india", Region = ShardRegions.EastIndia, ConnectionString = "Server=east;Database=ShardEast;Password=secret;" },
            new() { ShardId = "west-india", Region = ShardRegions.WestIndia, ConnectionString = "Server=west;Database=ShardWest;Password=secret;" }
        };

        var options = new ShardingOptions
        {
            Enabled = true,
            Strategy = "RegionAndHash",
            DefaultRegion = ShardRegions.NorthIndia,
            RetentionDays = 7,
            ArchiveIntervalMinutes = 60,
            FailOpenToPrimary = failOpenToPrimary,
            Shards = shards
        };

        var dbOptions = shards.ToDictionary(
            s => s.ShardId,
            s => new DbContextOptionsBuilder<TelemetryShardDbContext>()
                .UseInMemoryDatabase($"shard-{s.ShardId}-{Guid.NewGuid()}")
                .Options,
            StringComparer.OrdinalIgnoreCase);

        var router = new ShardRouter(options);
        var service = new ShardedDataService(
            router,
            NullLogger<ShardedDataService>.Instance,
            shard =>
            {
                if (simulateFaultOnNorth && string.Equals(shard.ShardId, "north-india", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Simulated shard outage");
                return new TelemetryShardDbContext(dbOptions[shard.ShardId]);
            });

        return (service, dbOptions);
    }

    private sealed class StubHubContext : IHubContext<TrackingHub>
    {
        public IHubClients Clients => new StubHubClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class StubHubClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new StubClientProxy();
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class StubClientProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }

    [Fact]
    public void ShardRouter_RoutesRegionsAndHashesDeterministically_AndSanitizesPasswords()
    {
        var (service, _) = CreateMultiRegionShards();
        var router = service.Router;

        Assert.Equal(ShardRegions.NorthIndia, router.ResolveRegion(29.70, 75.90)); // Tohana / Haryana
        Assert.Equal(ShardRegions.SouthIndia, router.ResolveRegion(12.97, 77.59)); // Bengaluru
        Assert.Equal(ShardRegions.EastIndia, router.ResolveRegion(22.57, 88.36));  // Kolkata
        Assert.Equal(ShardRegions.WestIndia, router.ResolveRegion(19.07, 72.87));  // Mumbai

        var northShard1 = router.ResolveShard(42, 29.70, 75.90);
        var northShard2 = router.ResolveShard(42, 29.70, 75.90);
        Assert.NotNull(northShard1);
        Assert.Equal("north-india", northShard1!.ShardId);
        Assert.Equal(northShard1.ShardId, northShard2!.ShardId);

        var southShard = router.ResolveShard(42, 13.08, 80.27);
        Assert.Equal("south-india", southShard!.ShardId);

        var summary = router.GetStatusSummary();
        Assert.True(summary.Active);
        Assert.Equal(4, summary.Shards.Count);
        Assert.All(summary.Shards, s => Assert.DoesNotContain("secret", s.ConnectionPreview, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TrackingService_IngestAsync_RoutesGpsPingsToRegionalShard_WithoutBloatingPrimaryDb()
    {
        await using var primary = CreatePrimaryDb();
        var (shardedService, shardOptions) = CreateMultiRegionShards();
        var tracking = new TrackingService(primary, new StubHubContext(), shards: shardedService);

        var op = new Operator { Id = 1, Name = "Haryana Roadways", Code = "HR", IsActive = true };
        var user = new User { Id = Guid.NewGuid(), FullName = "Ravi", Email = "ravi@test.local", PasswordHash = "x", Role = "Driver", IsActive = true };
        var driver = new Driver { Id = 1, UserId = user.Id, LicenseNumber = "HR01", OperatorId = 1, IsActive = true };
        var bus = new Bus { Id = 1, OperatorId = 1, FleetNumber = "HR-20-1001", RegistrationNumber = "HR20PA1001", Capacity = 52, BusType = "AC", IsActive = true };
        var stop1 = new Stop { Id = 1, Name = "Tohana", Code = "THN", Latitude = 29.70m, Longitude = 75.90m, IsActive = true };
        var stop2 = new Stop { Id = 2, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.70m, IsActive = true };
        var route = new Route { Id = 1, Code = "R-THN-BHN", Name = "Tohana - Bhuna", OperatorId = 1, IsActive = true };
        var rs1 = new RouteStop { Id = 1, RouteId = 1, StopId = 1, SequenceNo = 1, DistanceFromOriginKm = 0m };
        var rs2 = new RouteStop { Id = 2, RouteId = 1, StopId = 2, SequenceNo = 2, DistanceFromOriginKm = 28m };
        var schedule = new Schedule { Id = 1, RouteId = 1, BusId = 1, DriverId = 1, DepartureTime = new TimeOnly(8, 0), ServiceDays = "1111111", IsActive = true };
        var trip = new Trip { Id = 101, ScheduleId = 1, BusId = 1, DriverId = 1, RouteId = 1, ServiceDate = IndiaClock.Today(), Status = "Running", StartedAt = DateTime.UtcNow };

        primary.AddRange(op, user, driver, bus, stop1, stop2, route, rs1, rs2, schedule, trip);
        await primary.SaveChangesAsync();

        var snap = await tracking.IngestAsync(101, 29.62, 75.81, 48.5, 210, keepSimulation: false);

        Assert.Equal(0, await primary.GpsPings.CountAsync());
        await using (var northCtx = new TelemetryShardDbContext(shardOptions["north-india"]))
        {
            Assert.Equal(1, await northCtx.ShardGpsPings.CountAsync());
            var row = await northCtx.ShardGpsPings.FirstAsync();
            Assert.Equal(101, row.TripId);
            Assert.Equal(ShardRegions.NorthIndia, row.Region);
        }
        await using (var southCtx = new TelemetryShardDbContext(shardOptions["south-india"]))
        {
            Assert.Equal(0, await southCtx.ShardGpsPings.CountAsync());
        }

        Assert.Equal(29.62, snap.Latitude!.Value, 2);
        Assert.Equal(75.81, snap.Longitude!.Value, 2);

        await tracking.DeleteTripGpsPingsAsync(trip);
        await using (var northCtx = new TelemetryShardDbContext(shardOptions["north-india"]))
        {
            Assert.Equal(0, await northCtx.ShardGpsPings.CountAsync());
        }
    }

    [Fact]
    public async Task ShardedDataService_ArchivesAgedTrips_ScattersRecentQueries_AndSupportsTripLookup()
    {
        await using var primary = CreatePrimaryDb();
        var (shardedService, shardOptions) = CreateMultiRegionShards();
        var tracking = new TrackingService(primary, new StubHubContext(), shards: shardedService);

        var op = new Operator { Id = 1, Name = "National Transit", Code = "NT", IsActive = true };
        var u1 = new User { Id = Guid.NewGuid(), FullName = "Driver N", Email = "dn@test.local", PasswordHash = "x", Role = "Driver", IsActive = true };
        var d1 = new Driver { Id = 1, UserId = u1.Id, LicenseNumber = "DL01", OperatorId = 1, IsActive = true };
        var passenger = new User { Id = Guid.NewGuid(), FullName = "Passenger", Email = "p@test.local", PasswordHash = "x", Role = "Passenger", IsActive = true };

        var busNorth = new Bus { Id = 1, OperatorId = 1, FleetNumber = "HR-01", RegistrationNumber = "HR200001", Capacity = 50, BusType = "AC", IsActive = true };
        var busSouth = new Bus { Id = 2, OperatorId = 1, FleetNumber = "KA-01", RegistrationNumber = "KA010001", Capacity = 45, BusType = "Volvo", IsActive = true };

        var sNorth1 = new Stop { Id = 1, Name = "Tohana", Code = "THN", Latitude = 29.70m, Longitude = 75.90m, IsActive = true };
        var sNorth2 = new Stop { Id = 2, Name = "Bhuna", Code = "BHN", Latitude = 29.53m, Longitude = 75.70m, IsActive = true };
        var sSouth1 = new Stop { Id = 3, Name = "Bengaluru", Code = "BLR", Latitude = 12.97m, Longitude = 77.59m, IsActive = true };
        var sSouth2 = new Stop { Id = 4, Name = "Mysuru", Code = "MYS", Latitude = 12.29m, Longitude = 76.63m, IsActive = true };

        var rNorth = new Route { Id = 1, Code = "N-101", Name = "Tohana - Bhuna", OperatorId = 1, IsActive = true };
        var rSouth = new Route { Id = 2, Code = "S-202", Name = "Bengaluru - Mysuru", OperatorId = 1, IsActive = true };

        var schNorth = new Schedule { Id = 1, RouteId = 1, BusId = 1, DriverId = 1, DepartureTime = new TimeOnly(9, 0), ServiceDays = "1111111", IsActive = true };
        var schSouth = new Schedule { Id = 2, RouteId = 2, BusId = 2, DriverId = 1, DepartureTime = new TimeOnly(11, 30), ServiceDays = "1111111", IsActive = true };

        var oldDate = IndiaClock.Today().AddDays(-20);
        var hotDate = IndiaClock.Today().AddDays(-1);

        var agedNorth = new Trip { Id = 10, ScheduleId = 1, BusId = 1, DriverId = 1, RouteId = 1, ServiceDate = oldDate, Status = "Completed", StartedAt = DateTime.UtcNow.AddDays(-20), EndedAt = DateTime.UtcNow.AddDays(-20).AddHours(1) };
        var agedSouth = new Trip { Id = 11, ScheduleId = 2, BusId = 2, DriverId = 1, RouteId = 2, ServiceDate = oldDate.AddDays(1), Status = "Completed", StartedAt = DateTime.UtcNow.AddDays(-19), EndedAt = DateTime.UtcNow.AddDays(-19).AddHours(2) };
        var hotNorth = new Trip { Id = 12, ScheduleId = 1, BusId = 1, DriverId = 1, RouteId = 1, ServiceDate = hotDate, Status = "Completed", StartedAt = DateTime.UtcNow.AddDays(-1), EndedAt = DateTime.UtcNow.AddDays(-1).AddHours(1) };

        primary.AddRange(
            op, u1, d1, passenger,
            busNorth, busSouth,
            sNorth1, sNorth2, sSouth1, sSouth2,
            rNorth, rSouth,
            new RouteStop { Id = 1, RouteId = 1, StopId = 1, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 2, RouteId = 1, StopId = 2, SequenceNo = 2, DistanceFromOriginKm = 28m },
            new RouteStop { Id = 3, RouteId = 2, StopId = 3, SequenceNo = 1, DistanceFromOriginKm = 0m },
            new RouteStop { Id = 4, RouteId = 2, StopId = 4, SequenceNo = 2, DistanceFromOriginKm = 140m },
            schNorth, schSouth,
            agedNorth, agedSouth, hotNorth,
            new TripStopEta { Id = 1, TripId = 10, StopId = 1, SequenceNo = 1, Status = "Departed" },
            new TripStopEta { Id = 2, TripId = 11, StopId = 3, SequenceNo = 1, Status = "Departed" },
            new GpsPing { Id = 1, TripId = 10, Latitude = 29.65m, Longitude = 75.80m, RecordedAt = DateTime.UtcNow.AddDays(-20), ReceivedAt = DateTime.UtcNow.AddDays(-20) },
            new Notification { Id = 1, UserId = passenger.Id, TripId = 10, Title = "Done", Body = "Done", Kind = "completed", CreatedAt = DateTime.UtcNow.AddDays(-20) });
        await primary.SaveChangesAsync();

        var archiveResult = await tracking.ArchiveHistoricalTripsAsync(retentionDays: 7);

        Assert.Equal(2, archiveResult.ArchivedTrips);
        Assert.Equal(1, archiveResult.MovedGpsPings);
        Assert.Equal(2, archiveResult.ShardsWritten);

        // Only hot trip remains in primary DB; notification reference stays intact with TripId nulled.
        Assert.Equal(1, await primary.Trips.CountAsync());
        Assert.Equal(0, await primary.GpsPings.CountAsync());
        Assert.Null((await primary.Notifications.FirstAsync()).TripId);

        await using (var northCtx = new TelemetryShardDbContext(shardOptions["north-india"]))
        {
            Assert.Equal(1, await northCtx.ArchivedTrips.CountAsync());
            Assert.Equal(1, await northCtx.ShardGpsPings.CountAsync());
        }
        await using (var southCtx = new TelemetryShardDbContext(shardOptions["south-india"]))
        {
            Assert.Equal(1, await southCtx.ArchivedTrips.CountAsync());
            Assert.Equal("KA-01", (await southCtx.ArchivedTrips.FirstAsync()).FleetNumber);
        }

        // Combined Recent feed merges hot primary trip + archived North & South shard trips cleanly.
        var allRecent = await tracking.GetRecentTrackingAsync(page: 1, pageSize: 9);
        Assert.Equal(3, allRecent.TotalCount);
        Assert.Equal(new int?[] { 12, 11, 10 }, allRecent.Items.Select(i => i.TripId).ToArray());

        // Stop filter works across shards via StopSearchText.
        var filteredSouth = await tracking.GetRecentTrackingAsync(page: 1, pageSize: 9, filter: new TrackingFilterDto(Stop: "Mysuru"));
        Assert.Equal(1, filteredSouth.TotalCount);
        Assert.Equal(11, filteredSouth.Items[0].TripId);

        // Archived trip deep-link lookup works transparently even though primary DB row was deleted.
        var archivedSnapshot = await tracking.GetTripAsync(11);
        Assert.NotNull(archivedSnapshot);
        Assert.Equal("KA-01", archivedSnapshot!.FleetNumber);
        Assert.Equal(2, archivedSnapshot.Stops.Count);
    }

    [Fact]
    public async Task TrackingService_Audit_WritesToShard_AndFailOpenFallsBackToPrimary()
    {
        await using var primary = CreatePrimaryDb();
        var (shardedService, _) = CreateMultiRegionShards();
        var tracking = new TrackingService(primary, new StubHubContext(), shards: shardedService);

        await tracking.Audit(null, "Create", "Route", "15", "Created shard audit route");
        Assert.Equal(0, await primary.AuditLogs.CountAsync());

        var merged = await tracking.QueryAuditLogsAsync(20);
        Assert.Single(merged);
        Assert.Equal("15", merged[0].EntityId);

        // Simulate North shard outage with FailOpenToPrimary = true.
        var (faultyShards, _) = CreateMultiRegionShards(failOpenToPrimary: true, simulateFaultOnNorth: true);
        var resilientTracking = new TrackingService(primary, new StubHubContext(), shards: faultyShards);
        await resilientTracking.Audit(null, "Fallback", "Route", "16", "Primary fallback audit");

        Assert.Equal(1, await primary.AuditLogs.CountAsync());
        var primaryAudit = await primary.AuditLogs.FirstAsync();
        Assert.Equal("16", primaryAudit.EntityId);
    }
}
