using BusTrack.Api;
using BusTrack.Api.Services;

namespace BusTrack.Api.Application.AI.Functions;

public class PlanJourneyFunction
{
    private readonly TrackingService _trackingService;

    public PlanJourneyFunction(TrackingService trackingService)
    {
        _trackingService = trackingService;
    }

    public async Task<MultiRoutePlanDto> ExecuteAsync(
        string from,
        string to,
        DateOnly? date = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            var today = DateOnly.FromDateTime(IndiaClock.Now());
            return new MultiRoutePlanDto(from ?? "", to ?? "", today, false, new(), new(), new());
        }

        var journeyDate = date ?? DateOnly.FromDateTime(IndiaClock.Now());
        return await _trackingService.PlanJourneyAsync(from.Trim(), to.Trim(), journeyDate, null, ct);
    }
}
