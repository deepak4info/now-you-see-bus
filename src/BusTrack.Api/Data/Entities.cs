namespace BusTrack.Api.Data;

public class Operator
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
    public string? DriverCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Driver? Driver { get; set; }
}

public class Driver
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string LicenseNumber { get; set; } = "";
    public int? OperatorId { get; set; }
    public Operator? Operator { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Bus
{
    public int Id { get; set; }
    public int OperatorId { get; set; }
    public Operator Operator { get; set; } = null!;
    public string FleetNumber { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
    public string? Model { get; set; }
    public int Capacity { get; set; }
    public string BusType { get; set; } = "";
    public int? AssignedDriverId { get; set; }
    public Driver? AssignedDriver { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class Stop
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Route
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? OperatorId { get; set; }
    public Operator? Operator { get; set; }
    public decimal? DistanceKm { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RouteStop> Stops { get; set; } = new();
}

public class RouteStop
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public Route Route { get; set; } = null!;
    public int StopId { get; set; }
    public Stop Stop { get; set; } = null!;
    public int SequenceNo { get; set; }
    public decimal DistanceFromOriginKm { get; set; }
}

public class RoutePoint
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public int SequenceNo { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
}

public class FareSlab
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public int FromStopId { get; set; }
    public Stop FromStop { get; set; } = null!;
    public int ToStopId { get; set; }
    public Stop ToStop { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
}

public class Schedule
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public Route Route { get; set; } = null!;
    public int? BusId { get; set; }
    public Bus? Bus { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public TimeOnly DepartureTime { get; set; }
    public string ServiceDays { get; set; } = "1111111";
    public bool IsActive { get; set; } = true;
}

public class Trip
{
    public int Id { get; set; }
    public int ScheduleId { get; set; }
    public Schedule Schedule { get; set; } = null!;
    public int BusId { get; set; }
    public Bus Bus { get; set; } = null!;
    public int DriverId { get; set; }
    public Driver Driver { get; set; } = null!;
    public int RouteId { get; set; }
    public Route Route { get; set; } = null!;
    public DateOnly ServiceDate { get; set; }
    public string Status { get; set; } = "Scheduled";
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public bool Simulation { get; set; }
    public decimal OdometerKm { get; set; }
}

public class GpsPing
{
    public long Id { get; set; }
    public int TripId { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKmph { get; set; }
    public decimal? Heading { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public class TripStopEta
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public int StopId { get; set; }
    public int SequenceNo { get; set; }
    public DateTime? ScheduledArrival { get; set; }
    public DateTime? EstimatedArrival { get; set; }
    public DateTime? ActualArrival { get; set; }
    public int? EtaMinutes { get; set; }
    public string Status { get; set; } = "Pending";
}

public class Favorite
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int? BusId { get; set; }
    public Bus? Bus { get; set; }
    public int? RouteId { get; set; }
    public Route? Route { get; set; }
    public int? FromStopId { get; set; }
    public Stop? FromStop { get; set; }
    public int? ToStopId { get; set; }
    public Stop? ToStop { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int? TripId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }
}
