using BusTrack.Api.Application.AI.DTOs;
using BusTrack.Api.Services;

namespace BusTrack.Api.Application.AI.Functions;

public class SearchBusesFunction
{
    private readonly TrackingService _trackingService;

    public SearchBusesFunction(TrackingService trackingService)
    {
        _trackingService = trackingService;
    }

    public async Task<List<AiBusCardDto>> ExecuteAsync(
        string from,
        string to,
        DateOnly? date = null,
        TimeSpan? departureAfter = null,
        TimeSpan? departureBefore = null,
        bool? onlyLive = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return new List<AiBusCardDto>();

        var journeyDate = date ?? DateOnly.FromDateTime(IndiaClock.Now());
        var searchResult = await _trackingService.SearchRouteAsync(
            from.Trim(),
            to.Trim(),
            journeyDate,
            null,
            ct);

        var buses = new List<AiBusCardDto>();
        foreach (var s in searchResult.Results)
        {
            if (departureAfter.HasValue && TimeSpan.TryParse(s.DepartureTime, out var depTimeAfter))
            {
                if (depTimeAfter < departureAfter.Value)
                    continue;
            }

            if (departureBefore.HasValue && TimeSpan.TryParse(s.DepartureTime, out var depTime))
            {
                if (depTime > departureBefore.Value)
                    continue;
            }

            int durationMinutes = 60;
            if (TimeSpan.TryParse(s.DepartureTime, out var dt) && TimeSpan.TryParse(s.EstimatedArrivalTime, out var at))
            {
                var diff = at - dt;
                if (diff.TotalMinutes < 0) diff += TimeSpan.FromHours(24);
                durationMinutes = (int)Math.Max(15, diff.TotalMinutes);
            }

            buses.Add(new AiBusCardDto(
                FleetNumber: s.FleetNumber,
                RegistrationNumber: string.IsNullOrWhiteSpace(s.RegistrationNumber) ? s.FleetNumber : s.RegistrationNumber,
                BusName: string.IsNullOrWhiteSpace(s.BusName) ? $"{s.OperatorName} {s.BusType}".Trim() : s.BusName,
                OperatorName: s.OperatorName,
                BusType: string.IsNullOrWhiteSpace(s.BusType) ? "Standard" : s.BusType,
                RouteId: s.RouteId,
                RouteName: s.RouteName,
                FromStop: s.FromStop ?? from,
                ToStop: s.ToStop ?? to,
                DepartureTime: s.DepartureTime,
                ArrivalTime: s.EstimatedArrivalTime ?? s.DepartureTime,
                DurationMinutes: durationMinutes,
                EstimatedFare: s.Fare ?? 0m,
                IsLive: s.OperationsLive && s.Status == "Active",
                DelayMinutes: s.DelayMinutes,
                DelayStatus: s.DelayStatus ?? (s.DelayMinutes > 5 ? $"{s.DelayMinutes}m delay" : "On Time"),
                NextStop: s.NextStop,
                EtaMinutes: s.EtaMinutes,
                TripId: s.TripId,
                Latitude: s.Latitude,
                Longitude: s.Longitude
            ));
        }

        return buses.OrderBy(b => b.DepartureTime).ToList();
    }
}
