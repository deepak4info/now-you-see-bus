namespace BusTrack.Api.Application.AI.DTOs;

public record AiChatMessage(
    string Role, // "user", "assistant", "system"
    string Content
);

public record AiChatRequest(
    string Message,
    List<AiChatMessage>? History = null,
    string? Language = "auto", // "hi", "en", "auto"
    double? UserLat = null,
    double? UserLng = null
);

public record AiBusCardDto(
    string FleetNumber,
    string RegistrationNumber,
    string BusName,
    string OperatorName,
    string BusType,
    int RouteId,
    string RouteName,
    string FromStop,
    string ToStop,
    string DepartureTime,
    string ArrivalTime,
    int DurationMinutes,
    decimal EstimatedFare,
    bool IsLive,
    int? DelayMinutes = null,
    string? DelayStatus = null,
    string? NextStop = null,
    int? EtaMinutes = null,
    int? TripId = null,
    double? Latitude = null,
    double? Longitude = null
);

public record AiLiveStatusDto(
    string FleetNumber,
    string RegistrationNumber,
    string BusName,
    string RouteName,
    string OriginStop,
    string DestinationStop,
    string? CurrentLocation,
    string? NextStop,
    int? EtaMinutes,
    string DepartureTime,
    double? SpeedKmph,
    int? DelayMinutes,
    string DelayStatus,
    DateTime? RecordedAt,
    double? Latitude,
    double? Longitude,
    int? TripId,
    int RouteId
);

public record AiAdminAnalyticsDto(
    int TotalBuses,
    int ActiveBuses,
    int TotalRoutes,
    int ActiveRoutes,
    int TotalSchedules,
    List<string> RoutesWithoutSchedules,
    List<string> InactiveBuses,
    int DelayedBusesCount,
    int TotalTripsToday,
    int ActiveTripsNow,
    string Summary
);

public record AiChatResponse(
    string Reply,
    string Language, // "hi" or "en"
    string Intent, // "bus_search", "journey_plan", "live_status", "stop_timetable", "customer_support", "admin_analytics", "general"
    string? FunctionCalled = null,
    List<AiBusCardDto>? Buses = null,
    MultiRoutePlanDto? JourneyPlan = null,
    AiLiveStatusDto? LiveStatus = null,
    StopTimetableDto? Timetable = null,
    AiAdminAnalyticsDto? AdminSummary = null,
    List<string>? QuickReplies = null,
    bool Success = true,
    string? ErrorMessage = null
);
