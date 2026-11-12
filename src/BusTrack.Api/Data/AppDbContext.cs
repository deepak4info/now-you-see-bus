using Microsoft.EntityFrameworkCore;

namespace BusTrack.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Bus> Buses => Set<Bus>();
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<RouteStop> RouteStops => Set<RouteStop>();
    public DbSet<RoutePoint> RoutePoints => Set<RoutePoint>();
    public DbSet<FareSlab> FareSlabs => Set<FareSlab>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<GpsPing> GpsPings => Set<GpsPing>();
    public DbSet<TripStopEta> TripStopEtas => Set<TripStopEta>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<StopReminder> StopReminders => Set<StopReminder>();
    public DbSet<BusInfoReport> BusInfoReports => Set<BusInfoReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Operator>().ToTable("Operators");
        modelBuilder.Entity<User>().ToTable("Users");
        modelBuilder.Entity<User>().Property(u => u.Id).ValueGeneratedNever();
        modelBuilder.Entity<Driver>().ToTable("Drivers");
        modelBuilder.Entity<Bus>().ToTable("Buses");
        modelBuilder.Entity<Bus>()
            .HasOne(b => b.AssignedDriver)
            .WithMany()
            .HasForeignKey(b => b.AssignedDriverId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Stop>().ToTable("Stops");
        modelBuilder.Entity<Stop>().Property(s => s.Latitude).HasPrecision(9, 6);
        modelBuilder.Entity<Stop>().Property(s => s.Longitude).HasPrecision(9, 6);
        modelBuilder.Entity<Route>().ToTable("Routes");
        modelBuilder.Entity<Route>().Property(r => r.DistanceKm).HasPrecision(8, 2);
        modelBuilder.Entity<RouteStop>().ToTable("RouteStops");
        modelBuilder.Entity<RouteStop>().Property(r => r.SegmentDistanceKm).HasPrecision(8, 2);
        modelBuilder.Entity<RouteStop>().Property(r => r.DistanceFromOriginKm).HasPrecision(8, 2);
        modelBuilder.Entity<RoutePoint>().ToTable("RoutePoints");
        modelBuilder.Entity<RoutePoint>().Property(p => p.Latitude).HasPrecision(9, 6);
        modelBuilder.Entity<RoutePoint>().Property(p => p.Longitude).HasPrecision(9, 6);
        modelBuilder.Entity<FareSlab>().ToTable("FareSlabs");
        modelBuilder.Entity<FareSlab>().Property(f => f.Amount).HasPrecision(10, 2);
        modelBuilder.Entity<FareSlab>()
            .HasOne(f => f.FromStop)
            .WithMany()
            .HasForeignKey(f => f.FromStopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<FareSlab>()
            .HasOne(f => f.ToStop)
            .WithMany()
            .HasForeignKey(f => f.ToStopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Schedule>().ToTable("Schedules");
        modelBuilder.Entity<Trip>().ToTable("Trips");
        modelBuilder.Entity<Trip>().Property(t => t.OdometerKm).HasPrecision(10, 3);
        modelBuilder.Entity<GpsPing>().ToTable("GpsPings");
        modelBuilder.Entity<GpsPing>().Property(p => p.Latitude).HasPrecision(9, 6);
        modelBuilder.Entity<GpsPing>().Property(p => p.Longitude).HasPrecision(9, 6);
        modelBuilder.Entity<GpsPing>().Property(p => p.SpeedKmph).HasPrecision(6, 2);
        modelBuilder.Entity<GpsPing>().Property(p => p.Heading).HasPrecision(6, 2);
        modelBuilder.Entity<TripStopEta>().ToTable("TripStopEtas");
        modelBuilder.Entity<TripStopEta>()
            .HasOne(e => e.Stop)
            .WithMany()
            .HasForeignKey(e => e.StopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Favorite>().ToTable("Favorites");
        modelBuilder.Entity<Favorite>()
            .HasOne(f => f.FromStop)
            .WithMany()
            .HasForeignKey(f => f.FromStopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Favorite>()
            .HasOne(f => f.ToStop)
            .WithMany()
            .HasForeignKey(f => f.ToStopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Notification>().ToTable("Notifications");
        modelBuilder.Entity<SystemSetting>().ToTable("SystemSettings");
        modelBuilder.Entity<SystemSetting>().HasKey(s => s.Key);
        modelBuilder.Entity<AuditLog>().ToTable("AuditLogs");
        modelBuilder.Entity<Visitor>().ToTable("Visitors");
        modelBuilder.Entity<Visitor>().HasKey(v => v.Id);
        modelBuilder.Entity<StopReminder>().ToTable("StopReminders");
        modelBuilder.Entity<StopReminder>()
            .HasOne(r => r.Route)
            .WithMany()
            .HasForeignKey(r => r.RouteId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StopReminder>()
            .HasOne(r => r.Stop)
            .WithMany()
            .HasForeignKey(r => r.StopId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StopReminder>().HasIndex(r => new { r.UserId, r.IsTriggered, r.TargetDate });
        modelBuilder.Entity<StopReminder>().HasIndex(r => new { r.RouteId, r.TargetDate, r.IsTriggered });
        modelBuilder.Entity<BusInfoReport>().ToTable("BusInfoReports");
        modelBuilder.Entity<BusInfoReport>().Property(r => r.FleetNumber).HasMaxLength(20);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.Category).HasMaxLength(40);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.ReportedStopName).HasMaxLength(120);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.ProposedTime).HasMaxLength(40);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.Description).HasMaxLength(500);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.Status).HasMaxLength(20);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.AdminNotes).HasMaxLength(500);
        modelBuilder.Entity<BusInfoReport>().Property(r => r.VerifiedBy).HasMaxLength(100);
        modelBuilder.Entity<BusInfoReport>().HasIndex(r => new { r.Status, r.CreatedAt });
    }
}
