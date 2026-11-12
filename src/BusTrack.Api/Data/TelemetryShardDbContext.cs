using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Data;

/// <summary>
/// Sharded GPS ping row stored independently of the primary catalog database.
/// </summary>
public class ShardGpsPing
{
    public long Id { get; set; }
    public int TripId { get; set; }
    public int RouteId { get; set; }
    public int BusId { get; set; }
    public string Region { get; set; } = "";
    public string ShardKey { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKmph { get; set; }
    public decimal? Heading { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>
/// Denormalized historical trip record archived into a telemetry/history shard once past the hot retention window.
/// </summary>
public class ArchivedTrip
{
    public long Id { get; set; }
    public int OriginalTripId { get; set; }
    public int ScheduleId { get; set; }
    public int BusId { get; set; }
    public int DriverId { get; set; }
    public int RouteId { get; set; }
    public int OperatorId { get; set; }
    public string FleetNumber { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
    public string BusName { get; set; } = "";
    public string BusType { get; set; } = "";
    public string OperatorCode { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string RouteCode { get; set; } = "";
    public string RouteName { get; set; } = "";
    public string OriginStop { get; set; } = "";
    public string DestinationStop { get; set; } = "";
    public string StopSearchText { get; set; } = "";
    public decimal? OriginLatitude { get; set; }
    public decimal? OriginLongitude { get; set; }
    public decimal? DestinationLatitude { get; set; }
    public decimal? DestinationLongitude { get; set; }
    public DateOnly ServiceDate { get; set; }
    public TimeOnly DepartureTime { get; set; }
    public string Status { get; set; } = "Completed";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public bool Simulation { get; set; }
    public decimal OdometerKm { get; set; }
    public string? EstimatedArrivalTime { get; set; }
    public string? ActualDepartureTime { get; set; }
    public string? ActualArrivalTime { get; set; }
    public string? StopEtasJson { get; set; }
    public string Region { get; set; } = "";
    public string ShardId { get; set; } = "";
    public DateTime ArchivedAt { get; set; }
}

/// <summary>
/// Sharded audit/activity record distributed across shards.
/// </summary>
public class ShardAuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public string Region { get; set; } = "";
    public string ShardKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Lightweight EF Core DbContext targeting an individual telemetry/archive shard database.
/// </summary>
public class TelemetryShardDbContext : DbContext
{
    public TelemetryShardDbContext(DbContextOptions<TelemetryShardDbContext> options) : base(options) { }

    public DbSet<ShardGpsPing> ShardGpsPings => Set<ShardGpsPing>();
    public DbSet<ArchivedTrip> ArchivedTrips => Set<ArchivedTrip>();
    public DbSet<ShardAuditLog> ShardAuditLogs => Set<ShardAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShardGpsPing>().ToTable("ShardGpsPings");
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.Latitude).HasPrecision(9, 6);
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.Longitude).HasPrecision(9, 6);
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.SpeedKmph).HasPrecision(6, 2);
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.Heading).HasPrecision(6, 2);
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.Region).HasMaxLength(40);
        modelBuilder.Entity<ShardGpsPing>().Property(p => p.ShardKey).HasMaxLength(80);
        modelBuilder.Entity<ShardGpsPing>().HasIndex(p => new { p.TripId, p.RecordedAt, p.Id });

        modelBuilder.Entity<ArchivedTrip>().ToTable("ArchivedTrips");
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.OdometerKm).HasPrecision(10, 3);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.OriginLatitude).HasPrecision(9, 6);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.OriginLongitude).HasPrecision(9, 6);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.DestinationLatitude).HasPrecision(9, 6);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.DestinationLongitude).HasPrecision(9, 6);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.StopSearchText).HasMaxLength(2000);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.Region).HasMaxLength(40);
        modelBuilder.Entity<ArchivedTrip>().Property(t => t.ShardId).HasMaxLength(60);
        modelBuilder.Entity<ArchivedTrip>().HasIndex(t => t.OriginalTripId).IsUnique();
        modelBuilder.Entity<ArchivedTrip>().HasIndex(t => new { t.ServiceDate, t.Status });
        modelBuilder.Entity<ArchivedTrip>().HasIndex(t => new { t.BusId, t.ServiceDate });
        modelBuilder.Entity<ArchivedTrip>().HasIndex(t => new { t.RouteId, t.ServiceDate });

        modelBuilder.Entity<ShardAuditLog>().ToTable("ShardAuditLogs");
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.Action).HasMaxLength(80);
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.Entity).HasMaxLength(80);
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.EntityId).HasMaxLength(40);
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.Detail).HasMaxLength(500);
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.Region).HasMaxLength(40);
        modelBuilder.Entity<ShardAuditLog>().Property(a => a.ShardKey).HasMaxLength(80);
        modelBuilder.Entity<ShardAuditLog>().HasIndex(a => a.CreatedAt);
    }
}
