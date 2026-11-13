using BusTrack.Api.Application.AI.DTOs;
using BusTrack.Api.Services;

namespace BusTrack.Api.Application.AI.Functions;

public class GetLiveStatusFunction
{
    private readonly TrackingService _trackingService;

    public GetLiveStatusFunction(TrackingService trackingService)
    {
        _trackingService = trackingService;
    }

    public async Task<AiLiveStatusDto?> ExecuteAsync(string busNumberOrFleet, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(busNumberOrFleet))
            return null;

        var clean = busNumberOrFleet.Trim();
        var snapshot = await _trackingService.GetByFleetAsync(clean, null, null, null, ct);

        // If not found directly, check live tracking list for fuzzy match
        if (snapshot is null)
        {
            var liveBuses = await _trackingService.GetLiveTrackingAsync(null, null, ct);
            var matched = liveBuses.FirstOrDefault(b =>
                string.Equals(b.FleetNumber, clean, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.RegistrationNumber, clean, StringComparison.OrdinalIgnoreCase) ||
                b.BusName.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                b.RouteName.Contains(clean, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
            {
                return new AiLiveStatusDto(
                    FleetNumber: matched.FleetNumber,
                    RegistrationNumber: matched.RegistrationNumber,
                    BusName: matched.BusName,
                    RouteName: matched.RouteName,
                    OriginStop: matched.OriginStop,
                    DestinationStop: matched.DestinationStop,
                    CurrentLocation: matched.CurrentLocation,
                    NextStop: matched.NextStop,
                    EtaMinutes: matched.EtaMinutes,
                    DepartureTime: matched.DepartureTime,
                    SpeedKmph: matched.SpeedKmph,
                    DelayMinutes: matched.DelayMinutes,
                    DelayStatus: matched.DelayStatus ?? "On Time",
                    RecordedAt: matched.RecordedAt,
                    Latitude: matched.Latitude,
                    Longitude: matched.Longitude,
                    TripId: matched.TripId,
                    RouteId: matched.RouteId
                );
            }
            return null;
        }

        return new AiLiveStatusDto(
            FleetNumber: snapshot.FleetNumber,
            RegistrationNumber: snapshot.RegistrationNumber,
            BusName: snapshot.BusName,
            RouteName: snapshot.RouteName,
            OriginStop: snapshot.OriginStop ?? "",
            DestinationStop: snapshot.DestinationStop ?? "",
            CurrentLocation: snapshot.CurrentStop ?? "In Transit",
            NextStop: snapshot.NextStop,
            EtaMinutes: snapshot.EtaMinutes,
            DepartureTime: snapshot.DepartureTime,
            SpeedKmph: snapshot.SpeedKmph,
            DelayMinutes: snapshot.DelayMinutes,
            DelayStatus: snapshot.DelayStatus ?? "On Time",
            RecordedAt: snapshot.RecordedAt,
            Latitude: snapshot.Latitude,
            Longitude: snapshot.Longitude,
            TripId: snapshot.TripId,
            RouteId: snapshot.RouteId
        );
    }
}
