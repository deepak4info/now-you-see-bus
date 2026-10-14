namespace BusTrack.Api;

public record PointDto(double Latitude, double Longitude);

public record StopOnTripDto
{
    public int StopId { get; init; }
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public int Sequence { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double DistanceKm { get; init; }
    public string ScheduledTime { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public int? EtaMinutes { get; init; }
}

public record TrackSnapshotDto
{
    public int? TripId { get; init; }
    public int? ScheduleId { get; init; }
    public int BusId { get; init; }
    public int RouteId { get; init; }
    public string FleetNumber { get; init; } = "";
    public string RegistrationNumber { get; init; } = "";
    public string BusType { get; init; } = "";
    public int Capacity { get; init; }
    public string OperatorName { get; init; } = "";
    public string RouteCode { get; init; } = "";
    public string RouteName { get; init; } = "";
    public string Status { get; init; } = "";
    public string? DriverName { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Heading { get; init; }
    public double? SpeedKmph { get; init; }
    public DateTime? RecordedAt { get; init; }
    public string? CurrentStop { get; init; }
    public string? NextStop { get; init; }
    public int? EtaMinutes { get; init; }
    public int? DelayMinutes { get; init; }
    public bool OutsideSlot { get; init; }
    public bool Simulation { get; init; }
    public bool OperationsLive { get; init; }
    public string DepartureTime { get; init; } = "";
    public string ServiceDate { get; init; } = "";
    public decimal? Fare { get; init; }
    public string? FromStop { get; init; }
    public string? ToStop { get; init; }
    public int? EtaToYouMinutes { get; init; }
    public bool BoardingPassed { get; init; }
    public int? EtaToDestinationMinutes { get; init; }
    public int? DepartsInMinutes { get; init; }
    public List<StopOnTripDto> Stops { get; init; } = new();
    public List<PointDto> Path { get; init; } = new();
}

public record LoginRequest(string Login, string Password);
public record RegisterRequest(string FullName, string Email, string? Phone, string Password);

public record AuthResponse(string Token, string FullName, string Email, string Role, string? DriverCode);

public record FavoriteRequest(int? BusId, int? RouteId, int? FromStopId, int? ToStopId, string? Label);

public record LocationRequest(double Latitude, double Longitude, double? SpeedKmph, double? Heading);

public record StartTripRequest(string Mode);

public record OperatorRequest(string Name, string Code, string? ContactName, string? Phone, string? Email);
public record BusRequest(int OperatorId, string FleetNumber, string RegistrationNumber, string? Model, int Capacity, string BusType, int? AssignedDriverId);
public record DriverRequest(string FullName, string DriverCode, string Email, string? Phone, string Password, string LicenseNumber, int? OperatorId);
public record StopRequest(string Name, string Code, double Latitude, double Longitude, string? Address, string? City);
public record RouteRequest(string Code, string Name, int? OperatorId, List<int> StopIds);
public record FareInput(int FromStopId, int ToStopId, decimal Amount);
public record FareUpdateRequest(List<FareInput> Fares);
public record ScheduleRequest(int RouteId, int? BusId, int? DriverId, string DepartureTime, string? ServiceDays);
