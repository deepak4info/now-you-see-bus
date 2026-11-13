namespace BusTrack.Api;

public record PointDto(double Latitude, double Longitude);

public record VisitorRegistrationRequest(Guid VisitorId);

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
    public string? ScheduledArrival { get; init; }
    public string? EstimatedArrival { get; init; }
    public string? ActualArrival { get; init; }
    public string? ScheduledDeparture { get; init; }
    public string? EstimatedDeparture { get; init; }
    public string? ActualDeparture { get; init; }
    public int? DelayMinutes { get; init; }
    public string? DisplayStatus { get; init; }
}

public record TrackSnapshotDto
{
    public int? TripId { get; init; }
    public int? ScheduleId { get; init; }
    public int BusId { get; init; }
    public int RouteId { get; init; }
    public string FleetNumber { get; init; } = "";
    public string RegistrationNumber { get; init; } = "";
    public string BusName { get; init; } = "";
    public string BusType { get; init; } = "";
    public int Capacity { get; init; }
    public string OperatorName { get; init; } = "";
    public string RouteCode { get; init; } = "";
    public string RouteName { get; init; } = "";
    public string? OriginStop { get; init; }
    public string? DestinationStop { get; init; }
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
    public string? ActualDepartureTime { get; init; }
    public string? EstimatedDepartureTime { get; init; }
    public string? EstimatedArrivalTime { get; init; }
    public string? ActualArrivalTime { get; init; }
    public string? DelayStatus { get; init; }
    public DateTime? LastEtaUpdate { get; init; }
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

public record TripEtaResponseDto
{
    public int TripId { get; init; }
    public string FleetNumber { get; init; } = "";
    public string RouteName { get; init; } = "";
    public string Status { get; init; } = "";
    public string DelayStatus { get; init; } = "";
    public int? DelayMinutes { get; init; }
    public string ScheduledDeparture { get; init; } = "";
    public string? ActualDeparture { get; init; }
    public string? EstimatedDeparture { get; init; }
    public DateTime LastUpdatedAt { get; init; }
    public List<StopOnTripDto> Stops { get; init; } = new();
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
public record DriverUpdateRequest(string FullName, string DriverCode, string Email, string? Phone, string? Password, string LicenseNumber, int? OperatorId);
public record StopRequest(string Name, string Code, double Latitude, double Longitude, string? Address, string? City);
public record RouteRequest(
    string Code,
    string Name,
    int? OperatorId,
    List<int> StopIds,
    string DistanceMode = "Auto",
    List<double>? SegmentDistancesKm = null);
public record FareInput(int FromStopId, int ToStopId, decimal Amount);
public record FareUpdateRequest(List<FareInput> Fares);
public record ScheduleRequest(int RouteId, int? BusId, int? DriverId, string DepartureTime, string? ServiceDays);

public record BulkDeleteRequest(List<int> Ids);
public record BulkDeleteResponse(int Deleted, int Skipped, string Message, List<string>? SkippedReasons = null);

public record BulkUpdateStopsRequest(List<int> Ids, string? City, string? Address);
public record BulkUpdateBusesRequest(List<int> Ids, int? OperatorId, string? BusType, int? Capacity, int? AssignedDriverId);
public record BulkUpdateDriversRequest(List<int> Ids, int? OperatorId);
public record BulkUpdateOperatorsRequest(List<int> Ids, string? ContactName, string? Phone);
public record BulkUpdateRoutesRequest(List<int> Ids, int? OperatorId, string? DistanceMode);
public record BulkUpdateSchedulesRequest(List<int> Ids, string? ServiceDays, int? BusId, int? DriverId);
public record BulkOperationResult(int SuccessCount, int SkippedCount, string Message, List<string>? Errors = null);

// EU TELEMETRY (telemetry-project.eu) IoT Telematics Framework
public record IoTTelemetryPacket(
    string DeviceUid,
    string FleetNumber,
    double Latitude,
    double Longitude,
    double? SpeedKmph = null,
    double? Heading = null,
    double? AltitudeMeters = null,
    int? Satellites = null,
    double? Hdop = null,
    double? BatteryLevel = null,
    DateTime? RecordedAt = null,
    string? ApiKey = null
);

public record TelemetryIngestResult(
    bool Accepted,
    string Message,
    bool AnomalyDetected,
    List<string> AnomalyFlags,
    double TrustScore,
    DateTime ProcessedAt,
    TrackSnapshotDto? Snapshot = null
);

public record TelemetryDeviceStatus(
    string DeviceUid,
    string FleetNumber,
    string Status,
    double TrustScore,
    DateTime LastSeen,
    double Latitude,
    double Longitude,
    double? SpeedKmph,
    int TotalPacketsReceived,
    int TotalAnomaliesDetected,
    List<string> RecentAnomalies
);

public record TelemetryAnomalyRecord(
    string DeviceUid,
    string FleetNumber,
    List<string> AnomalyFlags,
    double Latitude,
    double Longitude,
    double? ImpliedSpeedKmph,
    double TrustScoreAfter,
    DateTime Timestamp
);

public record TelemetryStatsDto(
    int TotalPacketsReceived,
    int TotalAnomaliesDetected,
    int ActiveDevicesCount,
    int QuarantinedDevicesCount,
    double AverageTrustScore
);

public record ResetDeviceTrustRequest(string DeviceUid);

public record RouteSearchResultDto(
    string From,
    string To,
    DateOnly EffectiveDate,
    DateOnly RequestedDate,
    bool ShowingNextDate,
    List<TrackSnapshotDto> Results,
    MultiRoutePlanDto? JourneyPlan = null
);

public record JourneyLegDto(
    int RouteId,
    string RouteCode,
    string RouteName,
    int? ScheduleId,
    int? TripId,
    string? FleetNumber,
    string? BusType,
    string? OperatorName,
    string FromStop,
    string ToStop,
    string DepartureTime,
    string ArrivalTime,
    int DurationMinutes,
    decimal EstimatedFare,
    double DistanceKm,
    string Status
);

public record JourneyOptionDto(
    string OptionId,
    bool IsDirect,
    int TransfersCount,
    List<string> TransferStops,
    string DepartureTime,
    string ArrivalTime,
    int TotalJourneyMinutes,
    int TotalWaitMinutes,
    decimal EstimatedTotalFare,
    double TotalDistanceKm,
    List<JourneyLegDto> Legs,
    string Summary
);

public record MultiRoutePlanDto(
    string From,
    string To,
    DateOnly Date,
    bool DirectAvailable,
    List<JourneyOptionDto> DirectOptions,
    List<JourneyOptionDto> TransferOptions,
    List<JourneyOptionDto> AllOptions
);

public record BusTrackingItemDto(
    int? TripId,
    int? ScheduleId,
    int BusId,
    int RouteId,
    string FleetNumber,
    string RegistrationNumber,
    string BusName,
    string BusType,
    string OperatorName,
    string RouteCode,
    string RouteName,
    string OriginStop,
    string DestinationStop,
    string? CurrentLocation,
    string? NextStop,
    int? EtaMinutes,
    string DepartureTime,
    string? ActualDepartureTime,
    string? EstimatedArrivalTime,
    string? ActualArrivalTime,
    string ServiceDate,
    string Status,
    int? DelayMinutes,
    string? DelayStatus,
    DateTime? RecordedAt,
    double? Latitude,
    double? Longitude,
    bool OperationsLive,
    double? SpeedKmph = null
);

public record PaginatedBusTrackingDto(
    List<BusTrackingItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore
);

public record TrackingFilterDto(
    string? BusNumber = null,
    int? RouteId = null,
    string? Route = null,
    DateOnly? Date = null,
    int? OperatorId = null,
    string? Operator = null,
    string? BusType = null,
    string? Status = null,
    string? Stop = null,
    string? TimeOfDay = null
);

public record TrackingFilterOperatorOptionDto(int Id, string Name, string Code);

public record TrackingFilterRouteOptionDto(
    int Id,
    string Code,
    string Name,
    string OriginStop,
    string DestinationStop
);

public record TrackingFilterOptionsDto(
    List<TrackingFilterOperatorOptionDto> Operators,
    List<TrackingFilterRouteOptionDto> Routes,
    List<string> BusTypes
);

public record CachedStopDto(
    int Id,
    string Name,
    string Code,
    string? City,
    string? Address,
    decimal Latitude,
    decimal Longitude
);

public record CachedRouteStopDto(
    int Id,
    int RouteId,
    int StopId,
    int SequenceNo,
    decimal DistanceFromOriginKm,
    decimal? SegmentDistanceKm,
    string StopName,
    string StopCode,
    decimal StopLatitude,
    decimal StopLongitude
);

public record CachedFareSlabDto(int FromStopId, int ToStopId, decimal Amount);

public record CachedActiveRouteStopDto(
    int StopId,
    string Name,
    string Code,
    int SequenceNo,
    double Latitude,
    double Longitude,
    double DistanceKm
);

public record CachedActiveRoutePointDto(double Latitude, double Longitude);

public record CachedActiveRouteDto(
    int Id,
    string Code,
    string Name,
    decimal? DistanceKm,
    string Origin,
    string Destination,
    List<CachedActiveRouteStopDto> Stops,
    List<CachedActiveRoutePointDto> Path
);

public record CachedUserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? DriverCode,
    string? Phone,
    bool IsActive
);

public record StopReminderRequest(
    int RouteId,
    int StopId,
    int? TripId = null,
    string? FleetNumber = null,
    int RemindBeforeMinutes = 10,
    DateOnly? TargetDate = null
);

public record BusInfoReportRequest(
    string FleetNumber,
    int? TripId = null,
    int? RouteId = null,
    int? BusId = null,
    string Category = "Other",
    string? ReportedStopName = null,
    string? ProposedTime = null,
    int? DelayMinutes = null,
    string Description = ""
);

public record UpdateBusReportStatusRequest(
    string Status,
    string? AdminNotes = null,
    bool PublishAlert = false
);

public record StopTimetableDto(
    StopSummaryDto Stop,
    string ServiceDate,
    string CurrentTimeIst,
    StopStatsDto Stats,
    List<StopArrivalDto> UpcomingBuses,
    List<StopRouteSummaryDto> Routes
);

public record StopSummaryDto(
    int Id,
    string Name,
    string Code,
    string? City,
    string? Address,
    decimal Latitude,
    decimal Longitude
);

public record StopStatsDto(
    int TotalBusesToday,
    int UpcomingBuses,
    int ActiveRoutes
);

public record StopArrivalDto(
    int ScheduleId,
    int? TripId,
    int RouteId,
    string RouteCode,
    string RouteName,
    string OriginStop,
    string DestinationStop,
    int? BusId,
    string FleetNumber,
    string RegistrationNumber,
    string? BusType,
    string OperatorName,
    string ScheduledTime,
    string? EstimatedTime,
    string Status,
    int? DelayMinutes,
    int? EtaMinutes,
    bool IsLive,
    string TrackUrl
);

public record StopRouteSummaryDto(
    int RouteId,
    string RouteCode,
    string RouteName,
    string OperatorName,
    string OriginStop,
    string DestinationStop,
    int StopSequence,
    bool IsOrigin,
    bool IsTerminus,
    List<string> DownstreamStops,
    int DailyTripsCount,
    string FirstBusTime,
    string LastBusTime
);


