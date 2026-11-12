using System.Collections.Concurrent;
using System.Text.Json;
using BusTrack.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

/// <summary>
/// Singleton in-memory state store for IoT devices connected via the
/// EU Horizon TELEMETRY (telemetry-project.eu) framework.
/// </summary>
public class TelemetryStateStore
{
    public class DeviceInternalState
    {
        public string DeviceUid { get; set; } = "";
        public string FleetNumber { get; set; } = "";
        public double TrustScore { get; set; } = 1.0;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public double LastLatitude { get; set; }
        public double LastLongitude { get; set; }
        public double? LastSpeedKmph { get; set; }
        public DateTime LastRecordedAt { get; set; } = DateTime.UtcNow;
        public int TotalPacketsReceived { get; set; }
        public int TotalAnomaliesDetected { get; set; }
        public ConcurrentQueue<string> RecentAnomalies { get; } = new();
    }

    private readonly ConcurrentDictionary<string, DeviceInternalState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<TelemetryAnomalyRecord> _recentAnomalies = new();
    private const int MaxAnomalyHistory = 100;

    public DeviceInternalState GetOrAdd(string deviceUid, string fleetNumber)
    {
        return _devices.GetOrAdd(deviceUid, uid => new DeviceInternalState
        {
            DeviceUid = uid,
            FleetNumber = fleetNumber,
            TrustScore = 1.0,
            LastSeen = DateTime.UtcNow
        });
    }

    public bool TryGet(string deviceUid, out DeviceInternalState? state)
    {
        return _devices.TryGetValue(deviceUid, out state);
    }

    public IReadOnlyCollection<DeviceInternalState> GetAllDevices()
    {
        return _devices.Values.ToList();
    }

    public void AddAnomalyRecord(TelemetryAnomalyRecord record)
    {
        _recentAnomalies.Enqueue(record);
        while (_recentAnomalies.Count > MaxAnomalyHistory && _recentAnomalies.TryDequeue(out _)) { }
    }

    public IReadOnlyList<TelemetryAnomalyRecord> GetRecentAnomalies(int count = 50)
    {
        return _recentAnomalies.Reverse().Take(count).ToList();
    }

    public void ResetTrust(string deviceUid)
    {
        if (_devices.TryGetValue(deviceUid, out var state))
        {
            state.TrustScore = 1.0;
            state.RecentAnomalies.Clear();
        }
    }
}

/// <summary>
/// Service implementing EU TELEMETRY (telemetry-project.eu) IoT security validation,
/// anomaly detection (GPS spoofing, clock skew, teleportation barriers), trust scoring,
/// and transit integration.
/// </summary>
public class TelemetryService
{
    public const double MaxImpliedSpeedKmph = 130.0;
    public const double MaxTeleportationJumpKm = 3.0;
    public const double MaxClockSkewFutureMinutes = 3.0;
    public const double MaxClockSkewPastHours = 24.0;
    public const double QuarantineTrustThreshold = 0.35;
    public const double DegradedTrustThreshold = 0.70;

    private readonly AppDbContext _db;
    private readonly TrackingService _tracking;
    private readonly TelemetryStateStore _stateStore;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        AppDbContext db,
        TrackingService tracking,
        TelemetryStateStore stateStore,
        ILogger<TelemetryService> logger)
    {
        _db = db;
        _tracking = tracking;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <summary>
    /// Processes an incoming IoT telemetry packet with automated cybersecurity anomaly checks.
    /// </summary>
    public async Task<TelemetryIngestResult> IngestPacketAsync(IoTTelemetryPacket packet, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var recordedAt = packet.RecordedAt ?? now;
        var state = _stateStore.GetOrAdd(packet.DeviceUid, packet.FleetNumber);

        state.FleetNumber = packet.FleetNumber;
        state.TotalPacketsReceived++;
        state.LastSeen = now;

        var anomalies = new List<string>();
        double? impliedSpeed = null;

        // 1. Boundary & Coordinate Sanity Check
        if (packet.Latitude < -90 || packet.Latitude > 90 || packet.Longitude < -180 || packet.Longitude > 180)
        {
            anomalies.Add("INVALID_COORDINATES");
        }
        else if (Math.Abs(packet.Latitude) < 0.0001 && Math.Abs(packet.Longitude) < 0.0001)
        {
            anomalies.Add("NULL_ISLAND_SPOOFING");
        }

        // 2. Clock Skew & Timestamp Verification (Defense against Replay / Desynchronization)
        if (recordedAt > now.AddMinutes(MaxClockSkewFutureMinutes))
        {
            anomalies.Add("FUTURE_TIMESTAMP_DRIFT");
        }
        else if (recordedAt < now.AddHours(-MaxClockSkewPastHours))
        {
            anomalies.Add("EXPIRED_OR_REPLAY_TIMESTAMP");
        }

        // 3. GNSS Signal Quality Checks
        if (packet.Satellites.HasValue && packet.Satellites.Value < 4)
        {
            anomalies.Add("LOW_SATELLITE_COUNT");
        }
        if (packet.Hdop.HasValue && packet.Hdop.Value > 8.0)
        {
            anomalies.Add("DEGRADED_HDOP_ACCURACY");
        }

        // 4. Kinematic & GPS Spoofing Verification
        if (state.TotalPacketsReceived > 1 && !anomalies.Contains("INVALID_COORDINATES") && !anomalies.Contains("NULL_ISLAND_SPOOFING"))
        {
            var distKm = Geo.HaversineKm(state.LastLatitude, state.LastLongitude, packet.Latitude, packet.Longitude);
            var timeDeltaSec = (recordedAt - state.LastRecordedAt).TotalSeconds;

            if (timeDeltaSec >= 0.5)
            {
                impliedSpeed = (distKm / (timeDeltaSec / 3600.0));

                if (impliedSpeed > MaxImpliedSpeedKmph)
                {
                    anomalies.Add("SPOOFING_IMPOSSIBLE_SPEED");
                }
                if (distKm > MaxTeleportationJumpKm && timeDeltaSec <= 10.0)
                {
                    anomalies.Add("SPOOFING_TELEPORTATION_JUMP");
                }
                if (packet.SpeedKmph.HasValue && packet.SpeedKmph.Value <= 0.5 && distKm > 1.0 && timeDeltaSec <= 15.0)
                {
                    anomalies.Add("SENSOR_VELOCITY_DISCREPANCY");
                }
            }
            else if (distKm > 0.1) // Impossible leap in under half a second
            {
                anomalies.Add("SPOOFING_TELEPORTATION_JUMP");
            }
        }

        // 5. Dynamic Trust Score Calculation
        if (anomalies.Count > 0)
        {
            state.TotalAnomaliesDetected++;
            double penalty = 0;
            foreach (var a in anomalies)
            {
                penalty += a switch
                {
                    "SPOOFING_IMPOSSIBLE_SPEED" => 0.35,
                    "SPOOFING_TELEPORTATION_JUMP" => 0.40,
                    "NULL_ISLAND_SPOOFING" => 0.30,
                    "EXPIRED_OR_REPLAY_TIMESTAMP" => 0.35,
                    "FUTURE_TIMESTAMP_DRIFT" => 0.20,
                    "INVALID_COORDINATES" => 0.30,
                    "SENSOR_VELOCITY_DISCREPANCY" => 0.20,
                    "LOW_SATELLITE_COUNT" => 0.10,
                    "DEGRADED_HDOP_ACCURACY" => 0.10,
                    _ => 0.15
                };
            }
            state.TrustScore = Math.Clamp(state.TrustScore - penalty, 0.0, 1.0);

            var anomalyTimestamp = $"{now:HH:mm:ss}: {string.Join(", ", anomalies)}";
            state.RecentAnomalies.Enqueue(anomalyTimestamp);
            while (state.RecentAnomalies.Count > 10 && state.RecentAnomalies.TryDequeue(out _)) { }

            _stateStore.AddAnomalyRecord(new TelemetryAnomalyRecord(
                packet.DeviceUid,
                packet.FleetNumber,
                anomalies,
                packet.Latitude,
                packet.Longitude,
                impliedSpeed,
                state.TrustScore,
                now
            ));

            _logger.LogWarning("TELEMETRY ANOMALY DETECTED for device {DeviceUid} ({FleetNumber}): {Anomalies}. New Trust Score: {TrustScore:F2}",
                packet.DeviceUid, packet.FleetNumber, string.Join(", ", anomalies), state.TrustScore);

            try
            {
                var detailJson = JsonSerializer.Serialize(new
                {
                    packet.FleetNumber,
                    packet.Latitude,
                    packet.Longitude,
                    packet.SpeedKmph,
                    impliedSpeedKmph = impliedSpeed,
                    anomalies,
                    trustScore = state.TrustScore,
                    status = GetDeviceStatusString(state.TrustScore)
                });
                await _tracking.Audit(null, "IoTTelemetryAnomaly", "IoTDevice", packet.DeviceUid, detailJson, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist IoT telemetry anomaly audit log.");
            }
        }
        else
        {
            // Gradual trust restoration
            state.TrustScore = Math.Clamp(state.TrustScore + 0.05, 0.0, 1.0);
        }

        // Update last known valid position
        state.LastLatitude = packet.Latitude;
        state.LastLongitude = packet.Longitude;
        state.LastSpeedKmph = packet.SpeedKmph;
        state.LastRecordedAt = recordedAt;

        // 6. Quarantine Enforcement
        var isQuarantined = state.TrustScore < QuarantineTrustThreshold;
        if (isQuarantined)
        {
            return new TelemetryIngestResult(
                Accepted: false,
                Message: "Device quarantined by TELEMETRY security shield. Trust score is below safe operating threshold.",
                AnomalyDetected: anomalies.Count > 0,
                AnomalyFlags: anomalies,
                TrustScore: state.TrustScore,
                ProcessedAt: now
            );
        }

        // 7. Route and Trip Synchronization
        TrackSnapshotDto? snapshot = null;
        try
        {
            var normalizedFleet = TrackingService.NormalizeFleet(packet.FleetNumber);
            var today = IndiaClock.Today();

            var trip = await _db.Trips
                .Include(t => t.Bus)
                .FirstOrDefaultAsync(t =>
                    t.Status == "Running" &&
                    (t.Bus.FleetNumber == normalizedFleet || t.Bus.RegistrationNumber == normalizedFleet) &&
                    t.ServiceDate == today, ct);

            if (trip is not null)
            {
                snapshot = await _tracking.IngestAsync(
                    trip.Id,
                    packet.Latitude,
                    packet.Longitude,
                    packet.SpeedKmph,
                    packet.Heading,
                    keepSimulation: false,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to correlate and broadcast IoT telemetry for fleet {FleetNumber}", packet.FleetNumber);
        }

        return new TelemetryIngestResult(
            Accepted: true,
            Message: snapshot is not null ? "Telemetry accepted and synchronized with live trip." : "Telemetry accepted and registered (no active trip in progress).",
            AnomalyDetected: anomalies.Count > 0,
            AnomalyFlags: anomalies,
            TrustScore: state.TrustScore,
            ProcessedAt: now,
            Snapshot: snapshot
        );
    }

    public List<TelemetryDeviceStatus> GetDevices()
    {
        return _stateStore.GetAllDevices().Select(d => new TelemetryDeviceStatus(
            d.DeviceUid,
            d.FleetNumber,
            GetDeviceStatusString(d.TrustScore),
            Math.Round(d.TrustScore, 2),
            d.LastSeen,
            d.LastLatitude,
            d.LastLongitude,
            d.LastSpeedKmph,
            d.TotalPacketsReceived,
            d.TotalAnomaliesDetected,
            d.RecentAnomalies.ToList()
        )).OrderByDescending(d => d.LastSeen).ToList();
    }

    public IReadOnlyList<TelemetryAnomalyRecord> GetRecentAnomalies(int count = 50)
    {
        return _stateStore.GetRecentAnomalies(count);
    }

    public TelemetryStatsDto GetStats()
    {
        var devices = _stateStore.GetAllDevices().ToList();
        var totalPackets = devices.Sum(d => d.TotalPacketsReceived);
        var totalAnomalies = devices.Sum(d => d.TotalAnomaliesDetected);
        var activeDevices = devices.Count(d => (DateTime.UtcNow - d.LastSeen).TotalMinutes <= 30);
        var quarantined = devices.Count(d => d.TrustScore < QuarantineTrustThreshold);
        var avgTrust = devices.Count > 0 ? Math.Round(devices.Average(d => d.TrustScore), 2) : 1.0;

        return new TelemetryStatsDto(
            totalPackets,
            totalAnomalies,
            activeDevices,
            quarantined,
            avgTrust
        );
    }

    public bool ResetDeviceTrust(string deviceUid)
    {
        if (_stateStore.TryGet(deviceUid, out _))
        {
            _stateStore.ResetTrust(deviceUid);
            return true;
        }
        return false;
    }

    public static string GetDeviceStatusString(double trustScore) => trustScore switch
    {
        >= DegradedTrustThreshold => "Trusted",
        >= QuarantineTrustThreshold => "Degraded",
        _ => "Quarantined"
    };
}
