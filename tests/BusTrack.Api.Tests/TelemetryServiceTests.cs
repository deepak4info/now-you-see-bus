using BusTrack.Api.Data;
using BusTrack.Api.Hubs;
using BusTrack.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusTrack.Api.Tests;

public class TelemetryServiceTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TelemetryService CreateService(AppDbContext db, TelemetryStateStore? store = null)
    {
        var hub = new DummyHubContext();
        var tracking = new TrackingService(db, hub);
        var stateStore = store ?? new TelemetryStateStore();
        return new TelemetryService(db, tracking, stateStore, NullLogger<TelemetryService>.Instance);
    }

    [Fact]
    public async Task IngestPacket_ValidCoordinatesAndSignal_AcceptedWithTrustedStatus()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-001",
            FleetNumber: "HR-55-1001",
            Latitude: 28.4595,
            Longitude: 77.0266,
            SpeedKmph: 45.0,
            Heading: 90.0,
            Satellites: 9,
            Hdop: 1.2,
            RecordedAt: DateTime.UtcNow
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.Accepted);
        Assert.False(result.AnomalyDetected);
        Assert.Empty(result.AnomalyFlags);
        Assert.Equal(1.0, result.TrustScore);
    }

    [Fact]
    public async Task IngestPacket_InvalidCoordinates_FlagsAnomalyAndReducesTrust()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-002",
            FleetNumber: "HR-55-1002",
            Latitude: 195.0, // Invalid latitude > 90
            Longitude: 77.0266,
            RecordedAt: DateTime.UtcNow
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.AnomalyDetected);
        Assert.Contains("INVALID_COORDINATES", result.AnomalyFlags);
        Assert.True(result.TrustScore < 1.0);
    }

    [Fact]
    public async Task IngestPacket_NullIslandCoordinates_FlagsNullIslandSpoofing()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-003",
            FleetNumber: "HR-55-1003",
            Latitude: 0.0,
            Longitude: 0.0,
            RecordedAt: DateTime.UtcNow
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.AnomalyDetected);
        Assert.Contains("NULL_ISLAND_SPOOFING", result.AnomalyFlags);
    }

    [Fact]
    public async Task IngestPacket_FutureTimestampDrift_FlagsClockSkew()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-004",
            FleetNumber: "HR-55-1004",
            Latitude: 28.4595,
            Longitude: 77.0266,
            RecordedAt: DateTime.UtcNow.AddMinutes(15) // 15 min future drift
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.AnomalyDetected);
        Assert.Contains("FUTURE_TIMESTAMP_DRIFT", result.AnomalyFlags);
    }

    [Fact]
    public async Task IngestPacket_ExpiredTimestamp_FlagsReplayAttack()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-005",
            FleetNumber: "HR-55-1005",
            Latitude: 28.4595,
            Longitude: 77.0266,
            RecordedAt: DateTime.UtcNow.AddDays(-2) // 48h expired packet
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.AnomalyDetected);
        Assert.Contains("EXPIRED_OR_REPLAY_TIMESTAMP", result.AnomalyFlags);
    }

    [Fact]
    public async Task IngestPacket_LowSatellitesAndHighHdop_FlagsDegradedQuality()
    {
        await using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var packet = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-006",
            FleetNumber: "HR-55-1006",
            Latitude: 28.4595,
            Longitude: 77.0266,
            Satellites: 2,
            Hdop: 12.5,
            RecordedAt: DateTime.UtcNow
        );

        var result = await service.IngestPacketAsync(packet);

        Assert.True(result.AnomalyDetected);
        Assert.Contains("LOW_SATELLITE_COUNT", result.AnomalyFlags);
        Assert.Contains("DEGRADED_HDOP_ACCURACY", result.AnomalyFlags);
    }

    [Fact]
    public async Task IngestPacket_KinematicSpeedJump_FlagsImpossibleSpeedAndTeleportation()
    {
        await using var db = CreateInMemoryDb();
        var store = new TelemetryStateStore();
        var service = CreateService(db, store);
        var baseTime = DateTime.UtcNow;

        // Packet 1: Initial position in Gurgaon
        var p1 = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-007",
            FleetNumber: "HR-55-1007",
            Latitude: 28.4595,
            Longitude: 77.0266,
            RecordedAt: baseTime
        );
        var r1 = await service.IngestPacketAsync(p1);
        Assert.True(r1.Accepted);

        // Packet 2: Jump ~30km away in 5 seconds (over 20,000 km/h)
        var p2 = new IoTTelemetryPacket(
            DeviceUid: "OBU-TEST-007",
            FleetNumber: "HR-55-1007",
            Latitude: 28.7041,
            Longitude: 77.1025,
            RecordedAt: baseTime.AddSeconds(5)
        );
        var r2 = await service.IngestPacketAsync(p2);

        Assert.True(r2.AnomalyDetected);
        Assert.Contains("SPOOFING_IMPOSSIBLE_SPEED", r2.AnomalyFlags);
        Assert.Contains("SPOOFING_TELEPORTATION_JUMP", r2.AnomalyFlags);
        Assert.True(r2.TrustScore < 0.70);
    }

    [Fact]
    public async Task IngestPacket_RepeatedSevereAnomalies_QuarantinesDevice()
    {
        await using var db = CreateInMemoryDb();
        var store = new TelemetryStateStore();
        var service = CreateService(db, store);
        var time = DateTime.UtcNow;

        // Packet 1: Valid
        await service.IngestPacketAsync(new IoTTelemetryPacket("OBU-TEST-QUARANTINE", "HR-55-9999", 28.4595, 77.0266, RecordedAt: time));

        // Packet 2: Teleport jump #1 (trust drops by 0.75 -> 0.25)
        time = time.AddSeconds(5);
        var r2 = await service.IngestPacketAsync(new IoTTelemetryPacket("OBU-TEST-QUARANTINE", "HR-55-9999", 28.7500, 77.2000, RecordedAt: time));

        // Packet 3: Teleport jump #2 while quarantined
        time = time.AddSeconds(5);
        var r3 = await service.IngestPacketAsync(new IoTTelemetryPacket("OBU-TEST-QUARANTINE", "HR-55-9999", 28.4595, 77.0266, RecordedAt: time));

        Assert.False(r3.Accepted);
        Assert.Contains("quarantined", r3.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(r3.TrustScore < TelemetryService.QuarantineTrustThreshold);
    }

    [Fact]
    public async Task ResetDeviceTrust_RestoresTrustScoreToOne()
    {
        await using var db = CreateInMemoryDb();
        var store = new TelemetryStateStore();
        var service = CreateService(db, store);

        // Send an invalid packet to degrade trust
        var p = new IoTTelemetryPacket("OBU-TEST-RESET", "HR-55-RESET", 190.0, 77.0, RecordedAt: DateTime.UtcNow);
        var res = await service.IngestPacketAsync(p);
        Assert.True(res.TrustScore < 1.0);

        // Admin resets trust
        var resetSuccess = service.ResetDeviceTrust("OBU-TEST-RESET");
        Assert.True(resetSuccess);

        var devices = service.GetDevices();
        var device = devices.First(d => d.DeviceUid == "OBU-TEST-RESET");
        Assert.Equal(1.0, device.TrustScore);
        Assert.Equal("Trusted", device.Status);
    }

    [Fact]
    public async Task GetStats_ComputesAggregatesCorrectly()
    {
        await using var db = CreateInMemoryDb();
        var store = new TelemetryStateStore();
        var service = CreateService(db, store);

        await service.IngestPacketAsync(new IoTTelemetryPacket("DEV-A", "FL-1", 28.45, 77.02, RecordedAt: DateTime.UtcNow));
        await service.IngestPacketAsync(new IoTTelemetryPacket("DEV-B", "FL-2", 120.0, 77.02, RecordedAt: DateTime.UtcNow)); // Anomaly

        var stats = service.GetStats();

        Assert.Equal(2, stats.TotalPacketsReceived);
        Assert.Equal(1, stats.TotalAnomaliesDetected);
        Assert.Equal(2, stats.ActiveDevicesCount);
    }

    private class DummyHubContext : IHubContext<TrackingHub>
    {
        public IHubClients Clients => new DummyClients();
        public IGroupManager Groups => throw new NotImplementedException();

        private class DummyClients : IHubClients
        {
            public IClientProxy All => new DummyClientProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new DummyClientProxy();
            public IClientProxy Client(string connectionId) => new DummyClientProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new DummyClientProxy();
            public IClientProxy Group(string groupName) => new DummyClientProxy();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new DummyClientProxy();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new DummyClientProxy();
            public IClientProxy User(string userId) => new DummyClientProxy();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new DummyClientProxy();
        }

        private class DummyClientProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }
}
