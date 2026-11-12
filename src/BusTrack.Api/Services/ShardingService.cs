using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusTrack.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Services;

public static class ShardRegions
{
    public const string NorthIndia = "NorthIndia";
    public const string SouthIndia = "SouthIndia";
    public const string EastIndia = "EastIndia";
    public const string WestIndia = "WestIndia";
}

public class ShardDefinition
{
    public string ShardId { get; set; } = "";
    public string Region { get; set; } = ShardRegions.NorthIndia;
    public string ConnectionString { get; set; } = "";
    public bool ReadOnly { get; set; }
}

public class ShardingOptions
{
    public bool Enabled { get; set; }
    public string Strategy { get; set; } = "RegionAndHash";
    public string DefaultRegion { get; set; } = ShardRegions.NorthIndia;
    public int RetentionDays { get; set; } = 30;
    public int ArchiveIntervalMinutes { get; set; } = 60;
    public bool FailOpenToPrimary { get; set; } = true;
    public List<ShardDefinition> Shards { get; set; } = new();
}

public record ResolvedShard(string ShardId, string Region, string ShardKey, ShardDefinition Definition);

public record ShardInfoDto(
    string ShardId,
    string Region,
    bool ReadOnly,
    string ConnectionPreview);

public record ShardHealthItemDto(
    string ShardId,
    string Region,
    bool Connected,
    long GpsPingsCount,
    long ArchivedTripsCount,
    long AuditLogsCount,
    string? Error = null);

public record ShardingStatusDto(
    bool Enabled,
    bool Active,
    string Strategy,
    string DefaultRegion,
    int RetentionDays,
    int ArchiveIntervalMinutes,
    bool FailOpenToPrimary,
    List<ShardInfoDto> Shards);

public record ShardingHealthReportDto(
    bool Enabled,
    bool Active,
    string Strategy,
    string DefaultRegion,
    int RetentionDays,
    int ArchiveIntervalMinutes,
    bool FailOpenToPrimary,
    List<ShardHealthItemDto> Shards);

public record ShardRoutePreviewDto(
    bool ShardingActive,
    string Region,
    string? ShardId,
    string ShardKey);

public record TripArchiveResultDto(
    int ArchivedTrips,
    int MovedGpsPings,
    int ShardsWritten,
    DateOnly CutoffDateExclusive,
    List<string> ShardIds);

public record ArchivedStopSnapshot(
    int StopId,
    string Name,
    string Code,
    int Sequence,
    double Latitude,
    double Longitude,
    double DistanceKm,
    string ScheduledTime,
    string? EstimatedTime,
    string? ActualTime,
    string Status,
    string DisplayStatus,
    int? EtaMinutes,
    int? DelayMinutes);

public interface IShardRouter
{
    bool IsActive { get; }
    ShardingOptions Options { get; }
    IReadOnlyList<ShardDefinition> ActiveShards { get; }
    string ResolveRegion(double? latitude, double? longitude, string? regionHint = null);
    ResolvedShard? ResolveShard(int tripId, double? latitude = null, double? longitude = null, string? regionHint = null, DateOnly? serviceDate = null);
    ResolvedShard? ResolveShardForKey(string partitionKey, string? regionHint = null, DateTime? timestampUtc = null);
    ShardingStatusDto GetStatusSummary();
    ShardRoutePreviewDto PreviewRoute(int tripId, double? latitude = null, double? longitude = null, string? regionHint = null, DateOnly? serviceDate = null);
}

public class ShardRouter : IShardRouter
{
    private static readonly Regex PasswordPattern = new(@"(Password|Pwd)\s*=\s*[^;]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly List<ShardDefinition> _activeShards;

    public ShardingOptions Options { get; }
    public bool IsActive => Options.Enabled && _activeShards.Count > 0;
    public IReadOnlyList<ShardDefinition> ActiveShards => _activeShards;

    public ShardRouter(ShardingOptions options, IConfiguration? configuration = null)
    {
        Options = NormalizeOptions(options ?? new ShardingOptions(), configuration);
        _activeShards = Options.Shards
            .Where(s => !string.IsNullOrWhiteSpace(s.ShardId) && !string.IsNullOrWhiteSpace(s.ConnectionString))
            .ToList();
    }

    public ShardRouter(IConfiguration configuration)
        : this(BindFromConfiguration(configuration), configuration)
    {
    }

    public string ResolveRegion(double? latitude, double? longitude, string? regionHint = null)
    {
        if (!string.IsNullOrWhiteSpace(regionHint))
            return NormalizeRegion(regionHint, Options.DefaultRegion);

        if (latitude is double lat && longitude is double lng)
        {
            if (lat >= 23.5 && lng <= 82.5)
                return ShardRegions.NorthIndia;
            if (lng >= 82.5 || (lng >= 81.5 && lat >= 19.5))
                return ShardRegions.EastIndia;
            if (lat >= 18.0 && lat < 23.5 && lng < 76.5)
                return ShardRegions.WestIndia;
            if (lat < 20.5)
                return ShardRegions.SouthIndia;
            if (lat >= 23.5)
                return ShardRegions.NorthIndia;
            return ShardRegions.WestIndia;
        }

        return NormalizeRegion(Options.DefaultRegion, ShardRegions.NorthIndia);
    }

    public ResolvedShard? ResolveShard(
        int tripId,
        double? latitude = null,
        double? longitude = null,
        string? regionHint = null,
        DateOnly? serviceDate = null)
    {
        if (!IsActive) return null;

        var region = ResolveRegion(latitude, longitude, regionHint);
        var pool = SelectShardPool(region);
        var hashKey = unchecked((ulong)(uint)tripId);
        var index = JumpConsistentHash(hashKey, pool.Count);
        var shard = pool[index];
        var monthTag = (serviceDate ?? IndiaClock.Today()).ToString("yyyy-MM");
        var shardKey = $"{shard.Region}:{monthTag}:trip-{tripId}";
        return new ResolvedShard(shard.ShardId, shard.Region, shardKey, shard);
    }

    public ResolvedShard? ResolveShardForKey(string partitionKey, string? regionHint = null, DateTime? timestampUtc = null)
    {
        if (!IsActive) return null;

        var region = NormalizeRegion(regionHint, Options.DefaultRegion);
        var pool = SelectShardPool(region);
        var hash = ComputeFnv1a64(string.IsNullOrWhiteSpace(partitionKey) ? region : partitionKey);
        var shard = pool[JumpConsistentHash(hash, pool.Count)];
        var monthTag = (timestampUtc ?? DateTime.UtcNow).ToString("yyyy-MM");
        return new ResolvedShard(shard.ShardId, shard.Region, $"{shard.Region}:{monthTag}", shard);
    }

    public ShardingStatusDto GetStatusSummary() =>
        new(
            Enabled: Options.Enabled,
            Active: IsActive,
            Strategy: Options.Strategy,
            DefaultRegion: Options.DefaultRegion,
            RetentionDays: Options.RetentionDays,
            ArchiveIntervalMinutes: Options.ArchiveIntervalMinutes,
            FailOpenToPrimary: Options.FailOpenToPrimary,
            Shards: _activeShards.Select(s => new ShardInfoDto(
                s.ShardId,
                s.Region,
                s.ReadOnly,
                SanitizeConnectionString(s.ConnectionString))).ToList());

    public ShardRoutePreviewDto PreviewRoute(
        int tripId,
        double? latitude = null,
        double? longitude = null,
        string? regionHint = null,
        DateOnly? serviceDate = null)
    {
        var region = ResolveRegion(latitude, longitude, regionHint);
        var resolved = ResolveShard(tripId, latitude, longitude, regionHint, serviceDate);
        if (resolved is null)
        {
            var monthTag = (serviceDate ?? IndiaClock.Today()).ToString("yyyy-MM");
            return new ShardRoutePreviewDto(false, region, null, $"{region}:{monthTag}:trip-{tripId}");
        }
        return new ShardRoutePreviewDto(true, resolved.Region, resolved.ShardId, resolved.ShardKey);
    }

    private List<ShardDefinition> SelectShardPool(string region)
    {
        var regionalWritable = _activeShards
            .Where(s => !s.ReadOnly && string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (regionalWritable.Count > 0) return regionalWritable;

        var regionalAny = _activeShards
            .Where(s => string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (regionalAny.Count > 0) return regionalAny;

        var writable = _activeShards.Where(s => !s.ReadOnly).ToList();
        return writable.Count > 0 ? writable : _activeShards;
    }

    public static int JumpConsistentHash(ulong key, int numBuckets)
    {
        if (numBuckets <= 1) return 0;
        long b = -1;
        long j = 0;
        while (j < numBuckets)
        {
            b = j;
            key = unchecked(key * 2862933555777941757UL + 1UL);
            j = (long)((b + 1) * ((double)(1L << 31) / ((key >> 33) + 1)));
        }
        return (int)b;
    }

    public static ulong ComputeFnv1a64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
    }

    public static string NormalizeRegion(string? region, string fallback = ShardRegions.NorthIndia)
    {
        if (string.IsNullOrWhiteSpace(region))
            return fallback;

        var compact = new string(region.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return compact switch
        {
            "north" or "northindia" or "ni" => ShardRegions.NorthIndia,
            "south" or "southindia" or "si" => ShardRegions.SouthIndia,
            "east" or "eastindia" or "ei" => ShardRegions.EastIndia,
            "west" or "westindia" or "wi" => ShardRegions.WestIndia,
            _ => region.Trim()
        };
    }

    public static string SanitizeConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "";
        return PasswordPattern.Replace(connectionString, "$1=***");
    }

    private static ShardingOptions BindFromConfiguration(IConfiguration configuration)
    {
        var opts = new ShardingOptions();
        configuration.GetSection("Sharding").Bind(opts);
        return opts;
    }

    private static ShardingOptions NormalizeOptions(ShardingOptions options, IConfiguration? configuration)
    {
        options.DefaultRegion = NormalizeRegion(options.DefaultRegion, ShardRegions.NorthIndia);
        options.RetentionDays = Math.Clamp(options.RetentionDays <= 0 ? 30 : options.RetentionDays, 1, 3650);
        options.ArchiveIntervalMinutes = Math.Clamp(options.ArchiveIntervalMinutes < 0 ? 60 : options.ArchiveIntervalMinutes, 0, 10080);

        foreach (var shard in options.Shards)
        {
            shard.ShardId = (shard.ShardId ?? "").Trim();
            shard.Region = NormalizeRegion(shard.Region, options.DefaultRegion);
            if (string.IsNullOrWhiteSpace(shard.ConnectionString) && configuration is not null && !string.IsNullOrWhiteSpace(shard.ShardId))
            {
                var envKey = $"SHARD_{Regex.Replace(shard.ShardId.ToUpperInvariant(), "[^A-Z0-9]+", "_")}_CONNECTION";
                shard.ConnectionString = new[]
                {
                    configuration.GetConnectionString($"Shard_{shard.ShardId}"),
                    configuration.GetConnectionString($"Shard:{shard.ShardId}"),
                    Environment.GetEnvironmentVariable(envKey)
                }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            }
        }

        return options;
    }
}

public interface IShardedDataService
{
    bool IsEnabled { get; }
    IShardRouter Router { get; }
    Task<bool> TryRecordGpsPingAsync(int tripId, int routeId, int busId, decimal latitude, decimal longitude, decimal? speedKmph, decimal? heading, DateTime recordedAt, DateTime receivedAt, double? routeLatHint, double? routeLngHint, CancellationToken ct = default);
    Task<GpsPing?> GetLatestGpsPingAsync(int tripId, double? routeLatHint, double? routeLngHint, CancellationToken ct = default);
    Task<int> DeleteTripGpsPingsAsync(int tripId, double? routeLatHint, double? routeLngHint, CancellationToken ct = default);
    Task<bool> TryRecordAuditAsync(AuditLog entry, string? regionHint = null, CancellationToken ct = default);
    Task<List<AuditLog>> QueryAuditLogsAsync(int take, string? actionFilter = null, string? entityFilter = null, CancellationToken ct = default);
    Task<TripArchiveResultDto> ArchiveCompletedTripsAsync(AppDbContext primaryDb, DateOnly cutoffDateExclusive, CancellationToken ct = default);
    Task<(long TotalCount, List<BusTrackingItemDto> Items)> QueryArchivedTripsAsync(TrackingFilterDto? filter, string? statusFilter, int maxRowsPerShard, CancellationToken ct = default);
    Task<TrackSnapshotDto?> FindArchivedTripSnapshotAsync(int tripId, int? busId = null, CancellationToken ct = default);
    Task<ShardingHealthReportDto> GetHealthReportAsync(CancellationToken ct = default);
}

public sealed class NullShardedDataService : IShardedDataService
{
    public static readonly NullShardedDataService Instance = new();
    public bool IsEnabled => false;
    public IShardRouter Router { get; } = new ShardRouter(new ShardingOptions());

    private NullShardedDataService() { }

    public Task<bool> TryRecordGpsPingAsync(int tripId, int routeId, int busId, decimal latitude, decimal longitude, decimal? speedKmph, decimal? heading, DateTime recordedAt, DateTime receivedAt, double? routeLatHint, double? routeLngHint, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<GpsPing?> GetLatestGpsPingAsync(int tripId, double? routeLatHint, double? routeLngHint, CancellationToken ct = default) =>
        Task.FromResult<GpsPing?>(null);

    public Task<int> DeleteTripGpsPingsAsync(int tripId, double? routeLatHint, double? routeLngHint, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<bool> TryRecordAuditAsync(AuditLog entry, string? regionHint = null, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<List<AuditLog>> QueryAuditLogsAsync(int take, string? actionFilter = null, string? entityFilter = null, CancellationToken ct = default) =>
        Task.FromResult(new List<AuditLog>());

    public Task<TripArchiveResultDto> ArchiveCompletedTripsAsync(AppDbContext primaryDb, DateOnly cutoffDateExclusive, CancellationToken ct = default) =>
        Task.FromResult(new TripArchiveResultDto(0, 0, 0, cutoffDateExclusive, new()));

    public Task<(long TotalCount, List<BusTrackingItemDto> Items)> QueryArchivedTripsAsync(TrackingFilterDto? filter, string? statusFilter, int maxRowsPerShard, CancellationToken ct = default) =>
        Task.FromResult((0L, new List<BusTrackingItemDto>()));

    public Task<TrackSnapshotDto?> FindArchivedTripSnapshotAsync(int tripId, int? busId = null, CancellationToken ct = default) =>
        Task.FromResult<TrackSnapshotDto?>(null);

    public Task<ShardingHealthReportDto> GetHealthReportAsync(CancellationToken ct = default) =>
        Task.FromResult(new ShardingHealthReportDto(false, false, "RegionAndHash", ShardRegions.NorthIndia, 30, 60, true, new()));
}

public class ShardedDataService : IShardedDataService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] ShardSchemaBatches =
    {
        """
        IF OBJECT_ID(N'dbo.ShardGpsPings', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ShardGpsPings (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShardGpsPings PRIMARY KEY,
                TripId INT NOT NULL,
                RouteId INT NOT NULL,
                BusId INT NOT NULL,
                Region NVARCHAR(40) NOT NULL,
                ShardKey NVARCHAR(80) NOT NULL,
                Latitude DECIMAL(9,6) NOT NULL,
                Longitude DECIMAL(9,6) NOT NULL,
                SpeedKmph DECIMAL(6,2) NULL,
                Heading DECIMAL(6,2) NULL,
                RecordedAt DATETIME2 NOT NULL,
                ReceivedAt DATETIME2 NOT NULL CONSTRAINT DF_ShardGpsPings_Received DEFAULT SYSUTCDATETIME()
            );
            CREATE INDEX IX_ShardGpsPings_Trip_Recorded ON dbo.ShardGpsPings(TripId, RecordedAt DESC, Id DESC);
            CREATE INDEX IX_ShardGpsPings_Region_Recorded ON dbo.ShardGpsPings(Region, RecordedAt DESC);
        END;
        """,
        """
        IF OBJECT_ID(N'dbo.ArchivedTrips', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ArchivedTrips (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ArchivedTrips PRIMARY KEY,
                OriginalTripId INT NOT NULL,
                ScheduleId INT NOT NULL,
                BusId INT NOT NULL,
                DriverId INT NOT NULL,
                RouteId INT NOT NULL,
                OperatorId INT NOT NULL,
                FleetNumber NVARCHAR(20) NOT NULL,
                RegistrationNumber NVARCHAR(20) NOT NULL,
                BusName NVARCHAR(120) NOT NULL,
                BusType NVARCHAR(40) NOT NULL,
                OperatorCode NVARCHAR(20) NOT NULL,
                OperatorName NVARCHAR(150) NOT NULL,
                RouteCode NVARCHAR(20) NOT NULL,
                RouteName NVARCHAR(150) NOT NULL,
                OriginStop NVARCHAR(120) NOT NULL,
                DestinationStop NVARCHAR(120) NOT NULL,
                StopSearchText NVARCHAR(2000) NOT NULL,
                OriginLatitude DECIMAL(9,6) NULL,
                OriginLongitude DECIMAL(9,6) NULL,
                DestinationLatitude DECIMAL(9,6) NULL,
                DestinationLongitude DECIMAL(9,6) NULL,
                ServiceDate DATE NOT NULL,
                DepartureTime TIME NOT NULL,
                Status NVARCHAR(20) NOT NULL,
                StartedAt DATETIME2 NULL,
                EndedAt DATETIME2 NULL,
                Simulation BIT NOT NULL CONSTRAINT DF_ArchivedTrips_Simulation DEFAULT 0,
                OdometerKm DECIMAL(10,3) NOT NULL CONSTRAINT DF_ArchivedTrips_Odometer DEFAULT 0,
                EstimatedArrivalTime NVARCHAR(32) NULL,
                ActualDepartureTime NVARCHAR(32) NULL,
                ActualArrivalTime NVARCHAR(32) NULL,
                StopEtasJson NVARCHAR(MAX) NULL,
                Region NVARCHAR(40) NOT NULL,
                ShardId NVARCHAR(60) NOT NULL,
                ArchivedAt DATETIME2 NOT NULL CONSTRAINT DF_ArchivedTrips_ArchivedAt DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_ArchivedTrips_OriginalTrip UNIQUE (OriginalTripId)
            );
            CREATE INDEX IX_ArchivedTrips_ServiceDate_Status ON dbo.ArchivedTrips(ServiceDate DESC, Status);
            CREATE INDEX IX_ArchivedTrips_Bus_Date ON dbo.ArchivedTrips(BusId, ServiceDate DESC);
            CREATE INDEX IX_ArchivedTrips_Route_Date ON dbo.ArchivedTrips(RouteId, ServiceDate DESC);
        END;
        """,
        """
        IF OBJECT_ID(N'dbo.ShardAuditLogs', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ShardAuditLogs (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShardAuditLogs PRIMARY KEY,
                UserId UNIQUEIDENTIFIER NULL,
                Action NVARCHAR(80) NOT NULL,
                Entity NVARCHAR(80) NOT NULL,
                EntityId NVARCHAR(40) NULL,
                Detail NVARCHAR(500) NULL,
                Region NVARCHAR(40) NOT NULL,
                ShardKey NVARCHAR(80) NOT NULL,
                CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ShardAuditLogs_Created DEFAULT SYSUTCDATETIME()
            );
            CREATE INDEX IX_ShardAuditLogs_Created ON dbo.ShardAuditLogs(CreatedAt DESC);
            CREATE INDEX IX_ShardAuditLogs_Action_Entity_Created ON dbo.ShardAuditLogs(Action, Entity, CreatedAt DESC);
        END;
        """
    };

    private readonly IShardRouter _router;
    private readonly Func<ShardDefinition, TelemetryShardDbContext> _contextFactory;
    private readonly ILogger<ShardedDataService> _logger;
    private readonly ConcurrentDictionary<string, bool> _schemaReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _schemaLocks = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled => _router.IsActive;
    public IShardRouter Router => _router;

    public ShardedDataService(
        IShardRouter router,
        ILogger<ShardedDataService> logger,
        Func<ShardDefinition, TelemetryShardDbContext>? contextFactory = null)
    {
        _router = router;
        _logger = logger;
        _contextFactory = contextFactory ?? CreateSqlServerShardContext;
    }

    private static TelemetryShardDbContext CreateSqlServerShardContext(ShardDefinition shard)
    {
        var options = new DbContextOptionsBuilder<TelemetryShardDbContext>()
            .UseSqlServer(shard.ConnectionString)
            .Options;
        return new TelemetryShardDbContext(options);
    }

    public async Task<bool> TryRecordGpsPingAsync(
        int tripId,
        int routeId,
        int busId,
        decimal latitude,
        decimal longitude,
        decimal? speedKmph,
        decimal? heading,
        DateTime recordedAt,
        DateTime receivedAt,
        double? routeLatHint,
        double? routeLngHint,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return false;

        var targetLat = routeLatHint ?? (double)latitude;
        var targetLng = routeLngHint ?? (double)longitude;
        var resolved = _router.ResolveShard(tripId, targetLat, targetLng);
        if (resolved is null) return false;

        try
        {
            await using var ctx = await OpenShardAsync(resolved.Definition, ct);
            ctx.ShardGpsPings.Add(new ShardGpsPing
            {
                TripId = tripId,
                RouteId = routeId,
                BusId = busId,
                Region = resolved.Region,
                ShardKey = resolved.ShardKey,
                Latitude = latitude,
                Longitude = longitude,
                SpeedKmph = speedKmph,
                Heading = heading,
                RecordedAt = recordedAt,
                ReceivedAt = receivedAt
            });
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (HandleFailOpen(ex, resolved.ShardId, "RecordGpsPing", ct))
        {
            return false;
        }
    }

    public async Task<GpsPing?> GetLatestGpsPingAsync(
        int tripId,
        double? routeLatHint,
        double? routeLngHint,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return null;

        var resolved = _router.ResolveShard(tripId, routeLatHint, routeLngHint);
        if (resolved is not null)
        {
            try
            {
                await using var ctx = await OpenShardAsync(resolved.Definition, ct);
                var row = await ctx.ShardGpsPings.AsNoTracking()
                    .Where(p => p.TripId == tripId)
                    .OrderByDescending(p => p.RecordedAt)
                    .ThenByDescending(p => p.Id)
                    .FirstOrDefaultAsync(ct);
                if (row is not null)
                    return ToGpsPing(row);
            }
            catch (Exception ex) when (HandleFailOpen(ex, resolved.ShardId, "GetLatestGpsPing", ct))
            {
            }
        }

        // Fallback probe across remaining shards only if route coordinates changed after trip start.
        foreach (var shard in _router.ActiveShards)
        {
            if (resolved is not null && string.Equals(shard.ShardId, resolved.ShardId, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                var row = await ctx.ShardGpsPings.AsNoTracking()
                    .Where(p => p.TripId == tripId)
                    .OrderByDescending(p => p.RecordedAt)
                    .ThenByDescending(p => p.Id)
                    .FirstOrDefaultAsync(ct);
                if (row is not null)
                    return ToGpsPing(row);
            }
            catch (Exception ex) when (HandleFailOpen(ex, shard.ShardId, "GetLatestGpsPingFallback", ct))
            {
            }
        }

        return null;
    }

    public async Task<int> DeleteTripGpsPingsAsync(
        int tripId,
        double? routeLatHint,
        double? routeLngHint,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return 0;

        var totalDeleted = 0;
        var resolved = _router.ResolveShard(tripId, routeLatHint, routeLngHint);
        var targets = resolved is not null
            ? new[] { resolved.Definition }.Concat(_router.ActiveShards.Where(s => !string.Equals(s.ShardId, resolved.ShardId, StringComparison.OrdinalIgnoreCase)))
            : _router.ActiveShards;

        foreach (var shard in targets)
        {
            if (shard.ReadOnly) continue;
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                var rows = await ctx.ShardGpsPings.Where(p => p.TripId == tripId).ToListAsync(ct);
                if (rows.Count > 0)
                {
                    ctx.ShardGpsPings.RemoveRange(rows);
                    await ctx.SaveChangesAsync(ct);
                    totalDeleted += rows.Count;
                }
            }
            catch (Exception ex) when (HandleFailOpen(ex, shard.ShardId, "DeleteTripGpsPings", ct))
            {
            }
        }

        return totalDeleted;
    }

    public async Task<bool> TryRecordAuditAsync(AuditLog entry, string? regionHint = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return false;

        var partitionKey = !string.IsNullOrWhiteSpace(entry.EntityId)
            ? $"{entry.Entity}:{entry.EntityId}"
            : entry.UserId?.ToString() ?? entry.Entity;
        var resolved = _router.ResolveShardForKey(partitionKey, regionHint, entry.CreatedAt);
        if (resolved is null) return false;

        try
        {
            await using var ctx = await OpenShardAsync(resolved.Definition, ct);
            ctx.ShardAuditLogs.Add(new ShardAuditLog
            {
                UserId = entry.UserId,
                Action = entry.Action,
                Entity = entry.Entity,
                EntityId = entry.EntityId,
                Detail = entry.Detail,
                Region = resolved.Region,
                ShardKey = resolved.ShardKey,
                CreatedAt = entry.CreatedAt
            });
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (HandleFailOpen(ex, resolved.ShardId, "RecordAudit", ct))
        {
            return false;
        }
    }

    public async Task<List<AuditLog>> QueryAuditLogsAsync(
        int take,
        string? actionFilter = null,
        string? entityFilter = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || take <= 0) return new();

        var tasks = _router.ActiveShards.Select(async shard =>
        {
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                IQueryable<ShardAuditLog> query = ctx.ShardAuditLogs.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(actionFilter))
                    query = query.Where(a => a.Action == actionFilter);
                if (!string.IsNullOrWhiteSpace(entityFilter))
                    query = query.Where(a => a.Entity == entityFilter && a.EntityId != null);

                var rows = await query
                    .OrderByDescending(a => a.CreatedAt)
                    .Take(take)
                    .ToListAsync(ct);
                return rows.Select(a => new AuditLog
                {
                    Id = a.Id,
                    UserId = a.UserId,
                    Action = a.Action,
                    Entity = a.Entity,
                    EntityId = a.EntityId,
                    Detail = a.Detail,
                    CreatedAt = a.CreatedAt
                }).ToList();
            }
            catch (Exception ex) when (HandleFailOpen(ex, shard.ShardId, "QueryAuditLogs", ct))
            {
                return new List<AuditLog>();
            }
        });

        var perShard = await Task.WhenAll(tasks);
        return perShard
            .SelectMany(list => list)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToList();
    }

    public async Task<TripArchiveResultDto> ArchiveCompletedTripsAsync(
        AppDbContext primaryDb,
        DateOnly cutoffDateExclusive,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new TripArchiveResultDto(0, 0, 0, cutoffDateExclusive, new());

        var candidates = await primaryDb.Trips
            .Include(t => t.Bus).ThenInclude(b => b.Operator)
            .Include(t => t.Route)
            .Include(t => t.Schedule)
            .Where(t => (t.Status == "Completed" || t.Status == "Cancelled") && t.ServiceDate < cutoffDateExclusive)
            .OrderBy(t => t.ServiceDate)
            .ThenBy(t => t.Id)
            .Take(200)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new TripArchiveResultDto(0, 0, 0, cutoffDateExclusive, new());

        var tripIds = candidates.Select(t => t.Id).ToList();
        var routeIds = candidates.Select(t => t.RouteId).Distinct().ToList();

        var routeStops = await primaryDb.RouteStops.AsNoTracking()
            .Include(rs => rs.Stop)
            .Where(rs => routeIds.Contains(rs.RouteId))
            .OrderBy(rs => rs.RouteId)
            .ThenBy(rs => rs.SequenceNo)
            .ToListAsync(ct);
        var routeStopsMap = routeStops.GroupBy(rs => rs.RouteId).ToDictionary(g => g.Key, g => g.ToList());

        var etas = await primaryDb.TripStopEtas
            .Include(e => e.Stop)
            .Where(e => tripIds.Contains(e.TripId))
            .OrderBy(e => e.SequenceNo)
            .ToListAsync(ct);
        var etaMap = etas.GroupBy(e => e.TripId).ToDictionary(g => g.Key, g => g.ToList());

        var primaryPings = await primaryDb.GpsPings
            .Where(p => tripIds.Contains(p.TripId))
            .ToListAsync(ct);
        var pingMap = primaryPings.GroupBy(p => p.TripId).ToDictionary(g => g.Key, g => g.ToList());

        var byShard = new Dictionary<string, (ResolvedShard Shard, List<Trip> Trips)>(StringComparer.OrdinalIgnoreCase);
        foreach (var trip in candidates)
        {
            routeStopsMap.TryGetValue(trip.RouteId, out var rStops);
            var firstStop = rStops?.FirstOrDefault()?.Stop;
            var latHint = firstStop is null ? (double?)null : (double)firstStop.Latitude;
            var lngHint = firstStop is null ? (double?)null : (double)firstStop.Longitude;
            var resolved = _router.ResolveShard(trip.Id, latHint, lngHint, serviceDate: trip.ServiceDate);
            if (resolved is null) continue;

            if (!byShard.TryGetValue(resolved.ShardId, out var bucket))
            {
                bucket = (resolved, new List<Trip>());
                byShard[resolved.ShardId] = bucket;
            }
            bucket.Trips.Add(trip);
        }

        var archivedTrips = new List<Trip>();
        var movedPingCount = 0;
        var shardsWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, bucket) in byShard)
        {
            var resolved = bucket.Shard;
            try
            {
                await using var shardCtx = await OpenShardAsync(resolved.Definition, ct);
                var groupTripIds = bucket.Trips.Select(t => t.Id).ToList();
                var existingArchivedIds = await shardCtx.ArchivedTrips.AsNoTracking()
                    .Where(a => groupTripIds.Contains(a.OriginalTripId))
                    .Select(a => a.OriginalTripId)
                    .ToListAsync(ct);
                var existingSet = existingArchivedIds.ToHashSet();

                var existingPingTripIds = await shardCtx.ShardGpsPings.AsNoTracking()
                    .Where(p => groupTripIds.Contains(p.TripId))
                    .Select(p => p.TripId)
                    .Distinct()
                    .ToListAsync(ct);
                var pingTripSet = existingPingTripIds.ToHashSet();

                var groupMovedPings = 0;
                foreach (var trip in bucket.Trips)
                {
                    if (!existingSet.Contains(trip.Id))
                    {
                        routeStopsMap.TryGetValue(trip.RouteId, out var rStops);
                        rStops ??= new();
                        etaMap.TryGetValue(trip.Id, out var tripEtas);
                        tripEtas ??= new();

                        shardCtx.ArchivedTrips.Add(BuildArchivedTripEntity(trip, rStops, tripEtas, resolved));
                    }

                    if (pingMap.TryGetValue(trip.Id, out var tripPings) && tripPings.Count > 0 && !pingTripSet.Contains(trip.Id))
                    {
                        foreach (var ping in tripPings)
                        {
                            shardCtx.ShardGpsPings.Add(new ShardGpsPing
                            {
                                TripId = trip.Id,
                                RouteId = trip.RouteId,
                                BusId = trip.BusId,
                                Region = resolved.Region,
                                ShardKey = resolved.ShardKey,
                                Latitude = ping.Latitude,
                                Longitude = ping.Longitude,
                                SpeedKmph = ping.SpeedKmph,
                                Heading = ping.Heading,
                                RecordedAt = ping.RecordedAt,
                                ReceivedAt = ping.ReceivedAt
                            });
                            groupMovedPings++;
                        }
                    }
                }

                await shardCtx.SaveChangesAsync(ct);
                archivedTrips.AddRange(bucket.Trips);
                movedPingCount += groupMovedPings;
                shardsWritten.Add(resolved.ShardId);
            }
            catch (Exception ex) when (HandleFailOpen(ex, resolved.ShardId, "ArchiveTrips", ct))
            {
            }
        }

        if (archivedTrips.Count > 0)
        {
            var migratedIds = archivedTrips.Select(t => t.Id).ToHashSet();

            // Clear optional notification references before removing primary Trip rows so FK constraints never fail.
            var notifications = await primaryDb.Notifications
                .Where(n => n.TripId != null && migratedIds.Contains(n.TripId.Value))
                .ToListAsync(ct);
            foreach (var notification in notifications)
                notification.TripId = null;

            primaryDb.TripStopEtas.RemoveRange(etas.Where(e => migratedIds.Contains(e.TripId)));
            primaryDb.GpsPings.RemoveRange(primaryPings.Where(p => migratedIds.Contains(p.TripId)));
            primaryDb.Trips.RemoveRange(archivedTrips);
            await primaryDb.SaveChangesAsync(ct);
        }

        return new TripArchiveResultDto(
            ArchivedTrips: archivedTrips.Count,
            MovedGpsPings: movedPingCount,
            ShardsWritten: shardsWritten.Count,
            CutoffDateExclusive: cutoffDateExclusive,
            ShardIds: shardsWritten.OrderBy(s => s).ToList());
    }

    public async Task<(long TotalCount, List<BusTrackingItemDto> Items)> QueryArchivedTripsAsync(
        TrackingFilterDto? filter,
        string? statusFilter,
        int maxRowsPerShard,
        CancellationToken ct = default)
    {
        if (!IsEnabled || maxRowsPerShard <= 0)
            return (0L, new List<BusTrackingItemDto>());

        var tasks = _router.ActiveShards.Select(async shard =>
        {
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                var query = ApplyArchivedFilters(ctx.ArchivedTrips.AsNoTracking(), filter, statusFilter);
                var count = await query.LongCountAsync(ct);
                if (count == 0)
                    return (Count: 0L, Rows: new List<ArchivedTrip>());

                var rows = await query
                    .OrderByDescending(t => t.ServiceDate)
                    .ThenByDescending(t => t.EndedAt ?? t.StartedAt)
                    .ThenByDescending(t => t.DepartureTime)
                    .ThenByDescending(t => t.OriginalTripId)
                    .Take(maxRowsPerShard)
                    .ToListAsync(ct);
                return (Count: count, Rows: rows);
            }
            catch (Exception ex) when (HandleFailOpen(ex, shard.ShardId, "QueryArchivedTrips", ct))
            {
                return (Count: 0L, Rows: new List<ArchivedTrip>());
            }
        });

        var results = await Task.WhenAll(tasks);
        var total = results.Sum(r => r.Count);
        var items = results.SelectMany(r => r.Rows).Select(ToTrackingItem).ToList();
        return (total, items);
    }

    public async Task<TrackSnapshotDto?> FindArchivedTripSnapshotAsync(int tripId, int? busId = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;

        foreach (var shard in _router.ActiveShards)
        {
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                var row = await ctx.ArchivedTrips.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.OriginalTripId == tripId && (busId == null || t.BusId == busId.Value), ct);
                if (row is not null)
                    return ToTrackSnapshotDto(row);
            }
            catch (Exception ex) when (HandleFailOpen(ex, shard.ShardId, "FindArchivedTrip", ct))
            {
            }
        }

        return null;
    }

    public async Task<ShardingHealthReportDto> GetHealthReportAsync(CancellationToken ct = default)
    {
        var summary = _router.GetStatusSummary();
        if (!IsEnabled)
        {
            return new ShardingHealthReportDto(
                summary.Enabled,
                summary.Active,
                summary.Strategy,
                summary.DefaultRegion,
                summary.RetentionDays,
                summary.ArchiveIntervalMinutes,
                summary.FailOpenToPrimary,
                new());
        }

        var tasks = _router.ActiveShards.Select(async shard =>
        {
            try
            {
                await using var ctx = await OpenShardAsync(shard, ct);
                var gpsCount = await ctx.ShardGpsPings.LongCountAsync(ct);
                var tripCount = await ctx.ArchivedTrips.LongCountAsync(ct);
                var auditCount = await ctx.ShardAuditLogs.LongCountAsync(ct);
                return new ShardHealthItemDto(shard.ShardId, shard.Region, true, gpsCount, tripCount, auditCount);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new ShardHealthItemDto(shard.ShardId, shard.Region, false, 0, 0, 0, ex.Message);
            }
        });

        var reports = (await Task.WhenAll(tasks)).ToList();
        return new ShardingHealthReportDto(
            summary.Enabled,
            summary.Active,
            summary.Strategy,
            summary.DefaultRegion,
            summary.RetentionDays,
            summary.ArchiveIntervalMinutes,
            summary.FailOpenToPrimary,
            reports);
    }

    private async Task<TelemetryShardDbContext> OpenShardAsync(ShardDefinition shard, CancellationToken ct)
    {
        var ctx = _contextFactory(shard);
        try
        {
            await EnsureShardSchemaAsync(shard, ctx, ct);
            return ctx;
        }
        catch
        {
            await ctx.DisposeAsync();
            throw;
        }
    }

    private async Task EnsureShardSchemaAsync(ShardDefinition shard, TelemetryShardDbContext ctx, CancellationToken ct)
    {
        if (_schemaReady.ContainsKey(shard.ShardId)) return;

        var gate = _schemaLocks.GetOrAdd(shard.ShardId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_schemaReady.ContainsKey(shard.ShardId)) return;

            if (ctx.Database.IsSqlServer())
            {
                foreach (var batch in ShardSchemaBatches)
                    await ctx.Database.ExecuteSqlRawAsync(batch, ct);
            }
            else
            {
                await ctx.Database.EnsureCreatedAsync(ct);
            }

            _schemaReady[shard.ShardId] = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool HandleFailOpen(Exception ex, string shardId, string operation, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (!_router.Options.FailOpenToPrimary) return false;
        _logger.LogWarning(ex, "Shard '{ShardId}' failed during {Operation}; falling back cleanly.", shardId, operation);
        return true;
    }

    private static GpsPing ToGpsPing(ShardGpsPing row) =>
        new()
        {
            Id = row.Id,
            TripId = row.TripId,
            Latitude = row.Latitude,
            Longitude = row.Longitude,
            SpeedKmph = row.SpeedKmph,
            Heading = row.Heading,
            RecordedAt = row.RecordedAt,
            ReceivedAt = row.ReceivedAt
        };

    private static IQueryable<ArchivedTrip> ApplyArchivedFilters(
        IQueryable<ArchivedTrip> query,
        TrackingFilterDto? filter,
        string? statusFilter)
    {
        if (string.IsNullOrEmpty(statusFilter) || statusFilter == "all")
            query = query.Where(t => t.Status == "Completed" || t.Status == "Cancelled");
        else if (statusFilter == "completed")
            query = query.Where(t => t.Status == "Completed");
        else
            query = query.Where(t => t.Status == "Cancelled");

        if (filter is null) return query;

        if (filter.Date is DateOnly exactDate)
            query = query.Where(t => t.ServiceDate == exactDate);

        if (filter.OperatorId is int operatorId)
            query = query.Where(t => t.OperatorId == operatorId);

        if (!string.IsNullOrWhiteSpace(filter.Operator))
        {
            var opLower = filter.Operator.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.OperatorName.ToLower().Contains(opLower)
                || t.OperatorCode.ToLower().Contains(opLower));
        }

        if (!string.IsNullOrWhiteSpace(filter.BusType))
        {
            var btLower = filter.BusType.Trim().ToLowerInvariant();
            query = query.Where(t => t.BusType.ToLower() == btLower);
        }

        if (!string.IsNullOrWhiteSpace(filter.BusNumber))
        {
            var busTerm = filter.BusNumber.Trim();
            var normalized = TrackingService.NormalizeFleet(busTerm);
            if (string.IsNullOrWhiteSpace(normalized)) normalized = busTerm;
            var compact = new string(normalized.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToLowerInvariant();
            if (string.IsNullOrEmpty(compact)) compact = busTerm.ToLowerInvariant();

            var termLower = busTerm.ToLowerInvariant();
            var normLower = normalized.ToLowerInvariant();

            query = query.Where(t =>
                t.FleetNumber.ToLower().Contains(termLower)
                || t.FleetNumber.ToLower().Contains(normLower)
                || t.FleetNumber.Replace("-", "").Replace(" ", "").ToLower().Contains(compact)
                || t.RegistrationNumber.ToLower().Contains(termLower)
                || t.RegistrationNumber.ToLower().Contains(normLower)
                || t.RegistrationNumber.Replace("-", "").Replace(" ", "").ToLower().Contains(compact)
                || t.BusName.ToLower().Contains(termLower));
        }

        if (filter.RouteId is int routeId)
            query = query.Where(t => t.RouteId == routeId);

        if (!string.IsNullOrWhiteSpace(filter.Route))
        {
            var routeLower = filter.Route.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.RouteCode.ToLower().Contains(routeLower)
                || t.RouteName.ToLower().Contains(routeLower));
        }

        if (!string.IsNullOrWhiteSpace(filter.Stop))
        {
            var stopLower = filter.Stop.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.RouteName.ToLower().Contains(stopLower)
                || t.StopSearchText.ToLower().Contains(stopLower));
        }

        var (start, end) = TrackingService.ResolveTimeWindow(filter.TimeOfDay);
        if (start is TimeOnly windowStart)
        {
            query = end is TimeOnly windowEnd
                ? query.Where(t => t.DepartureTime >= windowStart && t.DepartureTime < windowEnd)
                : query.Where(t => t.DepartureTime >= windowStart);
        }

        return query;
    }

    private static ArchivedTrip BuildArchivedTripEntity(
        Trip trip,
        List<RouteStop> routeStops,
        List<TripStopEta> etas,
        ResolvedShard resolved)
    {
        var first = routeStops.FirstOrDefault()?.Stop;
        var last = routeStops.LastOrDefault()?.Stop;
        var totalDist = routeStops.Count > 0 ? (double)routeStops[^1].DistanceFromOriginKm : 0;

        var originEta = etas.FirstOrDefault();
        var destEta = etas.LastOrDefault();

        var actualDep = trip.Status == "Cancelled" && !trip.StartedAt.HasValue && originEta?.ActualDeparture is null
            ? null
            : trip.StartedAt.HasValue
                ? IndiaClock.FormatClock12(trip.StartedAt.Value)
                : (originEta?.ActualDeparture is { } ad ? IndiaClock.FormatClock12(ad) : null);

        var actualArr = trip.Status == "Cancelled" && !trip.EndedAt.HasValue && destEta?.ActualArrival is null
            ? null
            : trip.EndedAt.HasValue
                ? IndiaClock.FormatClock12(trip.EndedAt.Value)
                : (destEta?.ActualArrival is { } aa ? IndiaClock.FormatClock12(aa) : null);

        var estArrival = destEta?.EstimatedArrival is { } ea
            ? IndiaClock.FormatClock12(ea)
            : IndiaClock.Clock12FromDeparture(trip.Schedule.DepartureTime, totalDist);

        var busName = !string.IsNullOrWhiteSpace(trip.Bus.Model)
            ? trip.Bus.Model!
            : !string.IsNullOrWhiteSpace(trip.Bus.BusType)
                ? $"{trip.Bus.BusType} Service"
                : $"Bus {trip.Bus.FleetNumber}";

        var stopSearchTokens = routeStops
            .Select(rs => $"{rs.Stop.Name}|{rs.Stop.Code}")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var stopSearchText = string.Join("|", stopSearchTokens);
        if (stopSearchText.Length > 2000)
            stopSearchText = stopSearchText[..2000];

        var stopSnapshots = routeStops.Select(rs =>
        {
            var eta = etas.FirstOrDefault(e => e.StopId == rs.StopId);
            var scheduled = IndiaClock.Clock(trip.Schedule.DepartureTime.AddMinutes(Geo.EtaMinutes((double)rs.DistanceFromOriginKm)));
            var est = eta?.EstimatedArrival is { } eArr ? IndiaClock.FormatClock12(eArr) : scheduled;
            var act = eta?.ActualArrival is { } aArr ? IndiaClock.FormatClock12(aArr) : null;
            var st = eta?.Status ?? trip.Status;
            return new ArchivedStopSnapshot(
                rs.StopId,
                rs.Stop.Name,
                rs.Stop.Code,
                rs.SequenceNo,
                (double)rs.Stop.Latitude,
                (double)rs.Stop.Longitude,
                (double)rs.DistanceFromOriginKm,
                scheduled,
                est,
                act,
                st,
                st,
                eta?.EtaMinutes,
                eta?.DelayMinutes);
        }).ToList();

        return new ArchivedTrip
        {
            OriginalTripId = trip.Id,
            ScheduleId = trip.ScheduleId,
            BusId = trip.BusId,
            DriverId = trip.DriverId,
            RouteId = trip.RouteId,
            OperatorId = trip.Bus.OperatorId,
            FleetNumber = trip.Bus.FleetNumber,
            RegistrationNumber = trip.Bus.RegistrationNumber,
            BusName = busName,
            BusType = trip.Bus.BusType,
            OperatorCode = trip.Bus.Operator.Code,
            OperatorName = trip.Bus.Operator.Name,
            RouteCode = trip.Route.Code,
            RouteName = trip.Route.Name,
            OriginStop = first?.Name ?? "",
            DestinationStop = last?.Name ?? "",
            StopSearchText = stopSearchText,
            OriginLatitude = first?.Latitude,
            OriginLongitude = first?.Longitude,
            DestinationLatitude = last?.Latitude,
            DestinationLongitude = last?.Longitude,
            ServiceDate = trip.ServiceDate,
            DepartureTime = trip.Schedule.DepartureTime,
            Status = trip.Status,
            StartedAt = trip.StartedAt,
            EndedAt = trip.EndedAt,
            Simulation = trip.Simulation,
            OdometerKm = trip.OdometerKm,
            EstimatedArrivalTime = estArrival,
            ActualDepartureTime = actualDep,
            ActualArrivalTime = actualArr,
            StopEtasJson = JsonSerializer.Serialize(stopSnapshots, JsonOpts),
            Region = resolved.Region,
            ShardId = resolved.ShardId,
            ArchivedAt = DateTime.UtcNow
        };
    }

    private static BusTrackingItemDto ToTrackingItem(ArchivedTrip row) =>
        new(
            TripId: row.OriginalTripId,
            ScheduleId: row.ScheduleId,
            BusId: row.BusId,
            RouteId: row.RouteId,
            FleetNumber: row.FleetNumber,
            RegistrationNumber: row.RegistrationNumber,
            BusName: row.BusName,
            BusType: row.BusType,
            OperatorName: row.OperatorName,
            RouteCode: row.RouteCode,
            RouteName: row.RouteName,
            OriginStop: row.OriginStop,
            DestinationStop: row.DestinationStop,
            CurrentLocation: row.DestinationStop,
            NextStop: null,
            EtaMinutes: null,
            DepartureTime: IndiaClock.Clock(row.DepartureTime),
            ActualDepartureTime: row.ActualDepartureTime,
            EstimatedArrivalTime: row.EstimatedArrivalTime,
            ActualArrivalTime: row.ActualArrivalTime,
            ServiceDate: row.ServiceDate.ToString("yyyy-MM-dd"),
            Status: row.Status,
            DelayMinutes: null,
            DelayStatus: row.Status,
            RecordedAt: row.EndedAt is DateTime ended ? IndiaClock.AsUtc(ended) : null,
            Latitude: row.DestinationLatitude is decimal dLat ? (double)dLat : null,
            Longitude: row.DestinationLongitude is decimal dLng ? (double)dLng : null,
            OperationsLive: true);

    private static TrackSnapshotDto ToTrackSnapshotDto(ArchivedTrip row)
    {
        var stops = string.IsNullOrWhiteSpace(row.StopEtasJson)
            ? new List<ArchivedStopSnapshot>()
            : JsonSerializer.Deserialize<List<ArchivedStopSnapshot>>(row.StopEtasJson, JsonOpts) ?? new();

        return new TrackSnapshotDto
        {
            TripId = row.OriginalTripId,
            ScheduleId = row.ScheduleId,
            BusId = row.BusId,
            RouteId = row.RouteId,
            FleetNumber = row.FleetNumber,
            RegistrationNumber = row.RegistrationNumber,
            BusName = row.BusName,
            BusType = row.BusType,
            OperatorName = row.OperatorName,
            RouteCode = row.RouteCode,
            RouteName = row.RouteName,
            Status = row.Status,
            OperationsLive = true,
            Simulation = row.Simulation,
            DelayStatus = row.Status,
            DepartureTime = IndiaClock.Clock(row.DepartureTime),
            ActualDepartureTime = row.ActualDepartureTime,
            EstimatedArrivalTime = row.EstimatedArrivalTime,
            ActualArrivalTime = row.ActualArrivalTime,
            ServiceDate = row.ServiceDate.ToString("yyyy-MM-dd"),
            OriginStop = row.OriginStop,
            DestinationStop = row.DestinationStop,
            FromStop = row.OriginStop,
            ToStop = row.DestinationStop,
            CurrentStop = row.DestinationStop,
            Latitude = row.DestinationLatitude is decimal lat ? (double)lat : null,
            Longitude = row.DestinationLongitude is decimal lng ? (double)lng : null,
            RecordedAt = row.EndedAt is DateTime ended ? IndiaClock.AsUtc(ended) : null,
            Stops = stops.Select(s => new StopOnTripDto
            {
                StopId = s.StopId,
                Name = s.Name,
                Code = s.Code,
                Sequence = s.Sequence,
                Latitude = s.Latitude,
                Longitude = s.Longitude,
                DistanceKm = s.DistanceKm,
                ScheduledTime = s.ScheduledTime,
                EstimatedArrival = s.EstimatedTime,
                ActualArrival = s.ActualTime,
                Status = s.Status,
                DisplayStatus = s.DisplayStatus,
                EtaMinutes = s.EtaMinutes,
                DelayMinutes = s.DelayMinutes
            }).ToList()
        };
    }
}
